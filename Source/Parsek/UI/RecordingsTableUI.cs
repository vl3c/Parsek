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
        private const float ColW_Loop = 60f;
        private const float ColW_Watch = 50f;
        private const float ColW_Rewind = 75f;
        private const float ColW_Hide = 80f;
        private const string RewindActionLabel = "Rewind";
        private const string FastForwardActionLabel = "Forward";

        // Header cell height — the cells containing a label + select-all toggle
        // (Loop, Archive) naturally measure taller than a single bold label in
        // colHdr. Forcing every header cell to this height keeps the row visually
        // uniform; +10px over the single-label default balances with the toggle
        // cells without over-inflating them.
        private const float ColHeaderHeight = 32f;
        private const float ColW_Site = 90f;
        private const float ColW_Group = 60f;

        // Reusable per-frame buffers (avoid allocation each frame)
        private static readonly Dictionary<string, int> chainTipIndexBuffer = new Dictionary<string, int>();
        private static readonly HashSet<int> chainStatusBuffer = new HashSet<int>();

        // Chain and group expansion state
        private HashSet<string> expandedChains = new HashSet<string>();
        private HashSet<string> expandedGroups = new HashSet<string>();

        // Rewind-to-Staging row context. Any RP-backed unfinished flight row
        // must use the Rewind-to-RP button instead of the legacy
        // rewind-to-launch path; this depth only tells DrawRecordingRow when
        // to reserve an empty cell if a virtual-group member loses its RP
        // between group membership computation and row rendering.
        // Integer counter rather than bool so nested draw calls (theoretical
        // future) compose.
        private int unfinishedFlightRowDepth;

        // CanInvoke result cache by RP id — reset every OnGUI tick so log
        // transitions fire exactly once per draw when the result flips.
        private readonly Dictionary<string, bool> lastCanInvoke
            = new Dictionary<string, bool>();

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
        private enum RootItemType { Group, Chain, Recording, VirtualGroup }

        private struct RootDrawItem
        {
            public double SortKey;
            public string SortName;
            public RootItemType ItemType;
            public string GroupName;   // for groups
            public string ChainId;     // for chains
            public int RecIdx;         // for standalone recordings
        }

        internal struct GroupDisplayBlock
        {
            public string Key;
            public string DisplayName;
            public List<int> Members;
        }
        private int[] sortedIndices; // maps display row -> CommittedRecordings index
        private int lastSortedCount = -1;

        // Tooltip state
        private GUIStyle tooltipLabelStyle;
        private Rect scrollViewRect;
        private GUIStyle zeroHeightLabelStyle;
        private GUIStyle wrappedTooltipStyle;
        private string recordingsWindowTooltipText = "";

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

        private const float ColW_Period = 90f;
        private const float SpacingSmall = 3f;
        private static readonly Color LoopPeriodClampColor = new Color(1.0f, 0.8f, 0.4f);

        // Rewind/Forward button state tracking for transition logging
        private Dictionary<int, bool> lastCanRewind = new Dictionary<int, bool>();
        private Dictionary<int, bool> lastCanFF = new Dictionary<int, bool>();

        // Tracks rows where the legacy rewind-to-launch button is suppressed
        // because the recording is a non-owner tree branch (the rewind save
        // belongs to the tree root). Keyed by row index so the debounce
        // mirrors lastCanRewind. Logged once per transition so a tree merge
        // doesn't flood the log with one line per branch every frame.
        private Dictionary<int, bool> lastSuppressedTreeBranch = new Dictionary<int, bool>();

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
        private Dictionary<string, string> lastResolvedWatchTargetByGroup = new Dictionary<string, string>();

        // Bug #382: transient per-group rotation cursor. Group name → RecordingId most
        // recently entered via the group W button. Cleared when the stored RecordingId
        // no longer exists in committed (via PruneStaleWatchEntries). Not serialized;
        // resets naturally when the player opens a different save.
        private Dictionary<string, string> groupWatchCursorByGroupName = new Dictionary<string, string>();

        // Cached styles for status labels
        private GUIStyle statusStyleFuture;
        private GUIStyle statusStyleActive;
        private GUIStyle statusStylePast;

        // Zero-horizontal-padding body-box style: preserves the dark list-area
        // background without shifting rows inward (so column left edges align with
        // the fixed header above the scroll view).
        private GUIStyle tableBodyBoxStyle;
        private GUIStyle boldHeaderInnerLabel;
        private GUIStyle indexRowStyle;
        private GUIStyle bodyCellLabel;
        // Zero-horizontal-margin button style used inside DrawBodyCenteredButton's
        // BeginHorizontal wrap. With default 4/4 margin, a Space(N)+Button layout
        // overflows the container (margins are "outside" the content width). Zeroing
        // horizontal margin lets Space(N) + Button(Width=cellW-N) fit the cell exactly.
        private GUIStyle bodyCellButtonFlush;
        // Compact-padding variant for Rewind-column actions; keeps full-word
        // labels readable inside the original 75px column.
        private GUIStyle bodyCellButtonCompact;
        // Zero-horizontal-margin text-field style for the Period val TextField inside
        // the same shifted-right wrap treatment as bodyCellButtonFlush.
        private GUIStyle bodyCellTextFieldFlush;
        // Wrap container style for button / Period cells: margin 4/4 so the BeginHorizontal
        // consumes the same row layout footprint a direct Button would (matching the
        // colHdr Label margin 4/4 used by the corresponding header cell). Without this,
        // wraps had margin 0 and wrap-to-Label gaps collapsed to 4 (correct) but wrap-to-
        // wrap gaps collapsed to 0 (incorrect), shrinking body fixed-width sum by 4 px
        // per wrap-wrap transition and making Name.ExpandWidth absorb the extra.
        private GUIStyle bodyCellWrapStyle;
        // Space(5) before Name shifts Name text right inside the ExpandWidth cell so it
        // sits under the header "Name" text (which is 5 px inset by its colHdr box padding).
        private const float NameColumnLeadGap = 5f;
        // padding.left applied to every fixed-width body cell after Name. Matches the
        // visual text inset produced by the header's colHdr box padding, so body text
        // lands under header text without moving the body cells' layout boundaries.
        private const int BodyCellTextIndent = 5;
        // Left-only inset for DrawBodyCenteredButton and the Period wrap: the button
        // / Period val visible rect starts this many px into the cell and extends to
        // the cell's right edge, shifting the rectangle right (asymmetric inset) so
        // the body button doesn't span edge-to-edge.
        private const float BodyCellButtonLeftInset = 10f;
        // Container style for boxed header cells that wrap a toggle (merged toggle+#,
        // Loop, Archive). Same visual as colHdr but with left/right margin zeroed so
        // each BeginHorizontal container occupies exactly its Width(X) in the parent
        // horizontal flow. Top/bottom margin preserved (4/4) so the vertical gap
        // between the header row and the body box remains intact.
        private GUIStyle colHdrCellContainerStyle;

        // Deferred ghost-only recording deletion (avoids mid-layout list mutation)
        private int pendingDeleteGhostOnlyIndex = -1;

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

        internal bool IsMouseOverOpenWindow(Vector2 mousePosition)
        {
            return ParsekUI.IsPointerOverOpenWindow(
                showRecordingsWindow,
                recordingsWindowRect,
                mousePosition);
        }

        /// <summary>
        /// Called by the Timeline GoTo button. Ensures the target recording is visible
        /// (unhides if hidden, disables hide filter if needed, expands parent groups),
        /// opens the window, and scrolls to the recording.
        /// </summary>
        internal void ScrollToRecording(string recordingId)
        {
            if (!showRecordingsWindow) showRecordingsWindow = true;

            // [Phase 3] ERS-routed: cross-link navigation resolves to the
            // effective set only; hidden/superseded recordings cannot be scrolled to.
            var committed = EffectiveState.ComputeERS();
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
                ParsekLog.Warn("UI", $"Cross-link: recording {recordingId} not found in effective recording set");
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
                // Do NOT clear the slot decision cache here. TimelineWindowUI's
                // Fly button calls CanInvokeRewindPointSlot, and DrawIfOpen runs
                // every OnGUI pass — clearing per pass while Recordings is closed
                // and Timeline is open re-spams slot-ok every frame. The cache is
                // bounded by RP-slot count and is cleared on RP lifecycle events
                // (Reaper / Author / Discard / LoadTimeSweep) and on save load
                // (ParsekScenario.OnLoad).
                return;
            }

            // Position to the right of main window on first open
            if (recordingsWindowRect.width < 1f)
            {
                float recHeight = parentUI.InFlightMode ? mainWindowRect.height : mainWindowRect.height * 2;
                recordingsWindowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10,
                    mainWindowRect.y,
                    1280, recHeight);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI", $"Recordings window initial position: x={recordingsWindowRect.x.ToString("F0", ic)} y={recordingsWindowRect.y.ToString("F0", ic)}");
            }

            ParsekUI.HandleResizeDrag(ref recordingsWindowRect, ref isResizingRecordingsWindow,
                MinWindowWidth, MinWindowHeight, "Recordings window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                recordingsWindowRect = ClickThruBlocker.GUILayoutWindow(
                    "ParsekRecordings".GetHashCode(),
                    recordingsWindowRect,
                    DrawRecordingsWindow,
                    "Parsek - Recordings",
                    opaqueWindowStyle,
                    GUILayout.Width(recordingsWindowRect.width),
                    GUILayout.Height(recordingsWindowRect.height)
                );
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
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

            var cellLabelPadding = new RectOffset(BodyCellTextIndent, 0, 0, 0);
            phaseStyleAtmo = new GUIStyle(GUI.skin.label) { padding = cellLabelPadding };
            phaseStyleAtmo.normal.textColor = new Color(0.4f, 0.7f, 1f); // blue

            phaseStyleExo = new GUIStyle(GUI.skin.label) { padding = cellLabelPadding };
            phaseStyleExo.normal.textColor = new Color(0.75f, 0.55f, 1f); // light purple

            phaseStyleSpace = new GUIStyle(GUI.skin.label) { padding = cellLabelPadding };
            phaseStyleSpace.normal.textColor = new Color(0.2f, 1f, 0.6f); // lime green

            phaseStyleApproach = new GUIStyle(GUI.skin.label) { padding = cellLabelPadding };
            phaseStyleApproach.normal.textColor = new Color(0.3f, 0.8f, 1f); // cyan

            phaseStyleSurface = new GUIStyle(GUI.skin.label) { padding = cellLabelPadding };
            phaseStyleSurface.normal.textColor = new Color(1f, 0.6f, 0.2f); // orange

            // Generic body-cell label style (used by Site/Launch/Duration/expanded stats/
            // Period placeholder/Watch placeholder/Rewind placeholder). Same padding as
            // phaseStyle* so every left-aligned body label has a consistent text inset
            // matching the header's colHdr box padding.
            bodyCellLabel = new GUIStyle(GUI.skin.label) { padding = cellLabelPadding };
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

            // Body box: dark background only, zero horizontal padding. Keeps the
            // Career-State-style list surface without pushing row columns inward.
            tableBodyBoxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(0, 0, 2, 2),
                margin = new RectOffset(0, 0, 0, 0)
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

            // One-shot diagnostic log of the runtime GUI skin margins — dictates
            // exactly how much space each cell leaks or collapses in the layout.
            var colHdrStyle = parentUI.GetColumnHeaderStyle();
            ParsekLog.Verbose("UI",
                $"Rec table skin margins: box=L{GUI.skin.box.margin.left}/R{GUI.skin.box.margin.right}/T{GUI.skin.box.margin.top}/B{GUI.skin.box.margin.bottom} " +
                $"pad=L{GUI.skin.box.padding.left}/R{GUI.skin.box.padding.right} " +
                $"button.margin=L{GUI.skin.button.margin.left}/R{GUI.skin.button.margin.right} " +
                $"label.margin=L{GUI.skin.label.margin.left}/R{GUI.skin.label.margin.right} " +
                $"toggle.margin=L{GUI.skin.toggle.margin.left}/R{GUI.skin.toggle.margin.right} " +
                $"textField.margin=L{GUI.skin.textField.margin.left}/R{GUI.skin.textField.margin.right} " +
                $"colHdr.margin=L{colHdrStyle.margin.left}/R{colHdrStyle.margin.right}/T{colHdrStyle.margin.top}/B{colHdrStyle.margin.bottom}");

            // Arm one-shot alignment capture so the next header+row draw dumps actual rects.
            ArmAlignmentDebug();
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
            PruneStaleWatchEntries(
                lastCanWatchByRecId,
                lastCanWatchByGroup,
                lastResolvedWatchTargetByGroup,
                groupWatchCursorByGroupName,
                committed);
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
            PruneStaleWatchEntries(
                lastCanWatchByRecId,
                lastCanWatchByGroup,
                null,
                null,
                committed);
        }

        internal static void PruneStaleWatchEntries(
            Dictionary<string, bool> lastCanWatchByRecId,
            Dictionary<string, bool> lastCanWatchByGroup,
            Dictionary<string, string> lastResolvedWatchTargetByGroup,
            IReadOnlyList<Recording> committed)
        {
            PruneStaleWatchEntries(
                lastCanWatchByRecId,
                lastCanWatchByGroup,
                lastResolvedWatchTargetByGroup,
                null,
                committed);
        }

        internal static void PruneStaleWatchEntries(
            Dictionary<string, bool> lastCanWatchByRecId,
            Dictionary<string, bool> lastCanWatchByGroup,
            Dictionary<string, string> lastResolvedWatchTargetByGroup,
            Dictionary<string, string> groupWatchCursorByGroupName,
            IReadOnlyList<Recording> committed)
        {
            if (committed == null || committed.Count == 0)
            {
                lastCanWatchByRecId?.Clear();
                lastCanWatchByGroup?.Clear();
                lastResolvedWatchTargetByGroup?.Clear();
                groupWatchCursorByGroupName?.Clear();
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
                    {
                        lastCanWatchByGroup.Remove(stale[i]);
                        lastResolvedWatchTargetByGroup?.Remove(stale[i]);
                    }
            }

            // Bug #382: per-group rotation cursor. Keys are group names,
            // values are RecordingIds. Drop entries whose RecordingId is
            // no longer live — the rotation naturally falls back to the
            // first eligible entry on the next press.
            if (groupWatchCursorByGroupName != null && groupWatchCursorByGroupName.Count > 0)
            {
                List<string> stale = null;
                foreach (var kv in groupWatchCursorByGroupName)
                {
                    if (string.IsNullOrEmpty(kv.Value) || !liveIds.Contains(kv.Value))
                    {
                        if (stale == null) stale = new List<string>();
                        stale.Add(kv.Key);
                    }
                }
                if (stale != null)
                    for (int i = 0; i < stale.Count; i++)
                        groupWatchCursorByGroupName.Remove(stale[i]);
            }
        }

        internal static bool IsWatchButtonEnabled(
            bool hasGhost, bool sameBody, bool inRange, bool isDebris)
        {
            return hasGhost && sameBody && inRange && !isDebris;
        }

        internal static bool ShouldEnableWatchButton(bool canWatch, bool isWatching)
        {
            return canWatch || isWatching;
        }

        internal static bool UpdateWatchButtonTransitionCache(
            Dictionary<string, bool> lastCanWatchByRecId, string watchKey, bool canWatch)
        {
            if (lastCanWatchByRecId == null || string.IsNullOrEmpty(watchKey))
                return false;

            bool previousCanWatch;
            if (lastCanWatchByRecId.TryGetValue(watchKey, out previousCanWatch)
                && previousCanWatch == canWatch)
                return false;

            lastCanWatchByRecId[watchKey] = canWatch;
            return true;
        }

        internal static string GetWatchButtonReason(
            bool canWatch, bool hasGhost, bool sameBody, bool inRange, bool isDebris)
        {
            if (canWatch) return "enabled";
            if (isDebris) return "disabled (debris)";
            if (!hasGhost) return "disabled (no ghost)";
            if (!sameBody) return "disabled (different body)";
            if (!inRange) return "disabled (out of range)";
            return "disabled (unknown)";
        }

        internal static string GetWatchButtonTooltip(
            bool isWatching, bool hasGhost, bool sameBody, bool inRange, bool isDebris)
        {
            if (isWatching)
                return "Exit watch mode";
            if (isDebris)
                return "Debris is not watchable";
            if (!hasGhost)
                return "No active ghost - recording is in the past/future or has no trajectory points";
            if (!sameBody)
                return "Ghost is on a different body";
            if (!inRange)
                return "Ghost is beyond the fixed 300 km watch range";
            return "Follow ghost in watch mode";
        }

        private string BuildWatchObservabilitySuffix(ParsekFlight flight, int index)
        {
            if (flight == null)
                return "watchEval(unavailable=flight-null) watch=unavailable";
            return flight.DescribeWatchEligibilityForLogs(index) + " " + flight.DescribeWatchFocusForLogs();
        }

        private string BuildWatchObservabilitySuffix(ParsekFlight flight, int sourceIndex, int resolvedIndex)
        {
            if (flight == null)
                return "watchEval(unavailable=flight-null) watch=unavailable";
            if (resolvedIndex < 0 || resolvedIndex == sourceIndex)
                return BuildWatchObservabilitySuffix(flight, sourceIndex);

            return "source=" + flight.DescribeWatchEligibilityForLogs(sourceIndex)
                + " resolved=" + flight.DescribeWatchEligibilityForLogs(resolvedIndex)
                + " " + flight.DescribeWatchFocusForLogs();
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

        // One-shot capture of actual header/body cell rects for alignment debugging.
        // Armed via ArmAlignmentDebug() and cleared after the next full header+row draw.
        private bool alignmentDebugArmed;
        private readonly System.Text.StringBuilder alignmentDebugHeaderLog = new System.Text.StringBuilder();
        private readonly System.Text.StringBuilder alignmentDebugRowLog = new System.Text.StringBuilder();
        private bool alignmentDebugHeaderCaptured;
        private bool alignmentDebugRowCaptured;

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
            bool clicked = GUILayout.Button(content, buttonStyle ?? bodyCellButtonFlush,
                GUILayout.Width(cellWidth - BodyCellButtonLeftInset));
            GUILayout.EndHorizontal();
            return clicked;
        }

        private bool DrawBodyCenteredButton(string text, float cellWidth)
        {
            return DrawBodyCenteredButton(new GUIContent(text), cellWidth);
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
            string firstText, string secondText, float cellWidth,
            out bool firstClicked, out bool secondClicked)
        {
            DrawBodyCenteredTwoButtons(
                new GUIContent(firstText), true,
                new GUIContent(secondText), true,
                cellWidth, out firstClicked, out secondClicked);
        }

        private void DrawBodyCenteredTwoButtons(
            GUIContent firstContent, bool firstEnabled,
            GUIContent secondContent, bool secondEnabled,
            float cellWidth,
            out bool firstClicked, out bool secondClicked)
        {
            bool priorEnabled = GUI.enabled;
            float innerW = cellWidth - BodyCellButtonLeftInset;
            float halfInner = (innerW - 4f) * 0.5f;
            GUILayout.BeginHorizontal(bodyCellWrapStyle, GUILayout.Width(cellWidth));
            GUILayout.Space(BodyCellButtonLeftInset);
            GUI.enabled = priorEnabled && firstEnabled;
            firstClicked = GUILayout.Button(firstContent, bodyCellButtonCompact, GUILayout.Width(halfInner));
            GUILayout.Space(4f);
            GUI.enabled = priorEnabled && secondEnabled;
            secondClicked = GUILayout.Button(secondContent, bodyCellButtonCompact, GUILayout.Width(halfInner));
            GUI.enabled = priorEnabled;
            GUILayout.EndHorizontal();
        }

        private void DrawRecordingsTableHeader(IReadOnlyList<Recording> committed)
        {
            // Header row
            GUILayout.BeginHorizontal();

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
                GUILayout.Width(ColW_Enable + ColW_Index + 8f),
                GUILayout.Height(ColHeaderHeight));
            bool newAllEnabled = GUILayout.Toggle(allEnabled, "", GUILayout.Width(ColW_Enable));
            if (newAllEnabled != allEnabled)
            {
                for (int i = 0; i < committed.Count; i++)
                    committed[i].PlaybackEnabled = newAllEnabled;
                ParsekLog.Info("UI", $"Set playback enabled for all recordings: enabled={newAllEnabled}");
            }
            // "#" sortable — inline (no outer box because the parent BeginHorizontal
            // already supplies it; otherwise we'd double-box).
            string hashArrow = (sortColumn == SortColumn.Index)
                ? (sortAscending ? " \u25b2" : " \u25bc") : "";
            if (GUILayout.Button("#" + hashArrow, boldHeaderInnerLabel, GUILayout.Width(ColW_Index)))
            {
                if (sortColumn == SortColumn.Index) sortAscending = !sortAscending;
                else { sortColumn = SortColumn.Index; sortAscending = true; }
                InvalidateSort();
                ParsekLog.Verbose("UI", $"Sort column changed: {sortColumn} {(sortAscending ? "asc" : "desc")}");
            }
            GUILayout.EndHorizontal();
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrMerged");

            DrawSortableHeader("Name", SortColumn.Name, 0, true);
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrName");
            DrawSortableHeader("Phase", SortColumn.Phase, ColW_Phase);
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrPhase");
            DrawSortableHeader("Site", SortColumn.LaunchSite, ColW_Site);
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrSite");
            DrawSortableHeader("Launch", SortColumn.LaunchTime, ColW_Launch);
            DrawSortableHeader("Duration", SortColumn.Duration, ColW_Dur);
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrDur");

            var colHdr = parentUI.GetColumnHeaderStyle();
            if (showExpandedStats)
            {
                GUILayout.Label("MaxAlt", colHdr, GUILayout.Width(ColW_MaxAlt), GUILayout.Height(ColHeaderHeight));
                GUILayout.Label("MaxSpd", colHdr, GUILayout.Width(ColW_MaxSpd), GUILayout.Height(ColHeaderHeight));
                GUILayout.Label("Dist", colHdr, GUILayout.Width(ColW_Dist), GUILayout.Height(ColHeaderHeight));
                GUILayout.Label("Pts", colHdr, GUILayout.Width(ColW_Pts), GUILayout.Height(ColHeaderHeight));
                GUILayout.Label("Start", colHdr, GUILayout.Width(ColW_StartPos), GUILayout.Height(ColHeaderHeight));
                GUILayout.Label("End", colHdr, GUILayout.Width(ColW_EndPos), GUILayout.Height(ColHeaderHeight));
            }

            DrawSortableHeader("Status", SortColumn.Status, ColW_Status);
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrStatus");

            // Group column header
            GUILayout.Label("Group", colHdr, GUILayout.Width(ColW_Group), GUILayout.Height(ColHeaderHeight));
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrGroup");

            // Select-all loop header + checkbox. colHdr supplies the dark boxed
            // background so the whole cell reads as a header (not just the label).
            int loopCount = 0;
            for (int i = 0; i < committed.Count; i++)
                if (committed[i].LoopPlayback) loopCount++;

            bool allLoop = loopCount == committed.Count;
            GUILayout.BeginHorizontal(colHdrCellContainerStyle, GUILayout.Width(ColW_Loop), GUILayout.Height(ColHeaderHeight));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Loop", boldHeaderInnerLabel);
            bool newAllLoop = GUILayout.Toggle(allLoop, "");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrLoop");
            if (newAllLoop != allLoop)
            {
                for (int i = 0; i < committed.Count; i++)
                    committed[i].LoopPlayback = newAllLoop;
                ParsekLog.Info("UI", $"Set loop playback for all recordings: enabled={newAllLoop}");
            }

            GUILayout.Label(new GUIContent("Period",
                "Launch-to-launch period: how often the ghost relaunches.\nWhen shorter than the recording duration, successive launches overlap.\nClick unit to cycle: sec \u2192 min \u2192 hr \u2192 auto.\n\"auto\" inherits from Settings > Looping."),
                colHdr, GUILayout.Width(ColW_Period), GUILayout.Height(ColHeaderHeight));
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrPeriod");

            if (parentUI.InFlightMode)
            {
                GUILayout.Label("Watch", colHdr, GUILayout.Width(ColW_Watch), GUILayout.Height(ColHeaderHeight));
                if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrWatch");
            }
            GUILayout.Label("Actions", colHdr, GUILayout.Width(ColW_Rewind), GUILayout.Height(ColHeaderHeight));
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrRewind");

            // Hide column header + toggle
            GUILayout.BeginHorizontal(colHdrCellContainerStyle, GUILayout.Width(ColW_Hide), GUILayout.Height(ColHeaderHeight));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Archive", boldHeaderInnerLabel);
            bool newHideActive = GUILayout.Toggle(GroupHierarchyStore.HideActive, "");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrArchive");
            if (newHideActive != GroupHierarchyStore.HideActive)
            {
                GroupHierarchyStore.HideActive = newHideActive;
                ParsekLog.Info("UI", $"Hide active toggled: {GroupHierarchyStore.HideActive}");
            }

            // Reserve the vertical-scrollbar column so the fixed header's right edge
            // aligns with the row cells' right edges (the scroll view always shows a
            // vertical scrollbar, which claims a fixed-width strip on the right).
            float scrollbarWidth = GUI.skin.verticalScrollbar != null
                ? GUI.skin.verticalScrollbar.fixedWidth
                : 16f;
            if (scrollbarWidth <= 0f) scrollbarWidth = 16f;
            GUILayout.Space(scrollbarWidth);

            GUILayout.EndHorizontal();

            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured && Event.current.type == EventType.Repaint)
            {
                alignmentDebugHeaderCaptured = true;
                ParsekLog.Info("UI", "AlignDbg HEADER: " + alignmentDebugHeaderLog.ToString());
            }
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
                    if (showExpandedStats && recordingsWindowRect.width < 1738f)
                        recordingsWindowRect.width = 1738f;
                    else if (!showExpandedStats)
                        recordingsWindowRect.width = 1280f;
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
            if (GUILayout.Button("Clear", GUILayout.Width(50)))
            {
                filter.Clear();
                parentUI.GetTimelineUI()?.ResetTimeRangeSliders();
                ParsekLog.Verbose("UI", "Time-range filter: cleared from Recordings table");
            }
            GUILayout.EndHorizontal();
        }

        private void DrawRecordingsWindow(int windowID)
        {
            // Breathing room below the title bar — matches Timeline's visual spacing.
            GUILayout.Space(5);

            // Process deferred ghost-only recording deletion (avoids mid-layout list mutation)
            if (pendingDeleteGhostOnlyIndex >= 0)
            {
                int delIdx = pendingDeleteGhostOnlyIndex;
                pendingDeleteGhostOnlyIndex = -1;
                DeleteGhostOnlyRecording(delIdx);
            }

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
                GUILayout.Label("No recordings.");
            }
            else
            {
                // Fixed header row (outside the scroll view) — stays pinned to the top
                // while the body scrolls. A trailing spacer inside the header matches the
                // vertical scrollbar's reserved width so column right-edges line up with
                // the row cells exactly.
                DrawRecordingsTableHeader(committed);

                renderedRowCounter = 0;
                recordingsScrollPos = GUILayout.BeginScrollView(
                    recordingsScrollPos, false, true, GUILayout.ExpandHeight(true));

                // Dark list-area background (matches Career State) without horizontal
                // padding so row columns align with the header above.
                GUILayout.BeginVertical(tableBodyBoxStyle);

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
                    out rootGrps, out rootChainIds, supersedes);

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
                    if (IsSupersededForDisplay(rec, supersedes)) continue;
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
            }

            DrawRecordingsBottomBar(committed);
            DrawRecordingsWindowTooltip();
        }

        /// <summary>
        /// Draws a single recording row. Returns true if the list was modified (break iteration).
        /// </summary>
        private bool DrawRecordingRow(int ri, IReadOnlyList<Recording> committed, double now, float indentPx,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null)
        {
            var rec = committed[ri];
            if (IsSupersededForDisplay(rec, supersedes ?? CurrentRecordingSupersedesForDisplay())) return false;
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
            bool captureThisRow = alignmentDebugArmed && !alignmentDebugRowCaptured;

            // Enable checkbox (always at column 0)
            bool enabled = GUILayout.Toggle(rec.PlaybackEnabled, "", GUILayout.Width(ColW_Enable));
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowToggle");
            if (enabled != rec.PlaybackEnabled)
            {
                rec.PlaybackEnabled = enabled;
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
            DrawRecordingNameCell(ri, rec, committed, indentPx);
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

            // Site (launch site name)
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

            // Expanded stats
            if (showExpandedStats)
            {
                var stats = GetOrComputeStats(rec);
                GUILayout.Label(FormatAltitude(stats.maxAltitude), bodyCellLabel, GUILayout.Width(ColW_MaxAlt));
                GUILayout.Label(FormatSpeed(stats.maxSpeed), bodyCellLabel, GUILayout.Width(ColW_MaxSpd));
                GUILayout.Label(FormatDistance(stats.distanceTravelled), bodyCellLabel, GUILayout.Width(ColW_Dist));
                GUILayout.Label(stats.pointCount.ToString(), bodyCellLabel, GUILayout.Width(ColW_Pts));
                string parentVessel = ResolveParentVesselName(rec, committed);
                GUILayout.Label(FormatStartPosition(rec, parentVessel), bodyCellLabel, GUILayout.Width(ColW_StartPos));
                GUILayout.Label(FormatEndPosition(rec, parentVessel), bodyCellLabel, GUILayout.Width(ColW_EndPos));
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
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowStatus");

            // Group assignment button (split with X delete for ghost-only recordings)
            if (rec.IsGhostOnly && CanOfferGhostOnlyDelete(parentUI.Mode))
            {
                bool ghostGClicked, ghostXClicked;
                DrawBodyCenteredTwoButtons("G", "X", ColW_Group, out ghostGClicked, out ghostXClicked);
                if (ghostGClicked)
                {
                    var mousePos = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                    groupPicker.OpenForRecording(ri, mousePos);
                    ParsekLog.Verbose("UI", $"Group popup opened for recording index={ri} name='{rec.VesselName}'");
                }
                if (ghostXClicked)
                {
                    pendingDeleteGhostOnlyIndex = ri;
                    ParsekLog.Verbose("UI", $"Delete ghost-only recording clicked: index={ri} name='{rec.VesselName}'");
                }
            }
            else
            {
                if (DrawBodyCenteredButton("G", ColW_Group))
                {
                    var mousePos = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                    groupPicker.OpenForRecording(ri, mousePos);
                    ParsekLog.Verbose("UI", $"Group popup opened for recording index={ri} name='{rec.VesselName}'");
                }
            }
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowGroup");

            // Loop checkbox — suppressed when this row is being drawn inside the
            // virtual Unfinished Flights group (unfinishedFlightRowDepth > 0).
            // The group is a re-fly TODO list; surfacing a loop toggle there
            // is misleading because the user's next action on these rows is
            // Fly, not playback configuration. The same recording's row in
            // its real (mission) group still exposes the toggle, so loop
            // remains editable for unfinished-flight recordings — just not
            // from inside the virtual group itself. We render an empty cell
            // of the same column width to keep the table grid aligned.
            if (unfinishedFlightRowDepth > 0)
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Loop));
            }
            else
            {
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
            }
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowLoop");

            // Period — wrapped with Space(BodyCellButtonLeftInset) on the left so
            // val+unit start 10 px into the cell, matching the shifted-right treatment
            // applied to single-button body cells (DrawBodyCenteredButton).
            // Suppressed alongside the loop checkbox when this row is being
            // drawn inside the virtual Unfinished Flights group: the period
            // editor is editable for any recording with LoopPlayback=true,
            // so leaving it active would re-open loop configuration on the
            // re-fly TODO surface that the loop-checkbox hide was meant to
            // remove. Render an empty cell of the same column width to keep
            // the table grid aligned.
            if (unfinishedFlightRowDepth > 0)
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Period));
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
                    string reason = GetWatchButtonReason(canWatch, hasGhost, sameBody, inRange, rec.IsDebris);
                    ParsekLog.Info("UI",
                        $"Watch button #{ri} \"{rec.VesselName}\" {reason} " +
                        $"(hasGhost={hasGhost} sameBody={sameBody} inRange={inRange} debris={rec.IsDebris}) " +
                        $"{BuildWatchObservabilitySuffix(flight, ri)}");
                }

                GUI.enabled = ShouldEnableWatchButton(canWatch, isWatching);
                string watchLabel = isWatching ? "W*" : "W";
                string watchTooltip = GetWatchButtonTooltip(isWatching, hasGhost, sameBody, inRange, rec.IsDebris);
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

            // Rewind / Forward button
            if (DrawUnfinishedFlightRewindButton(rec, ri, now,
                reserveCellWhenUnavailable: unfinishedFlightRowDepth > 0))
            {
                // Rendered as Rewind-to-RP (Phase 6); skip the legacy rewind-to-launch block.
            }
            else if (DrawStashUnfinishedFlightButton(rec, ri))
            {
                // Rendered as Stash-in-Unfinished-Flights; skip the legacy rewind-to-launch block.
            }
            else
            {
                bool isFuture = now < rec.StartUT;
                bool isActive = now >= rec.StartUT && now <= rec.EndUT;
                bool hasRewindSave = !string.IsNullOrEmpty(RecordingStore.GetRewindSaveFileName(rec));
                if (isFuture)
                {
                    // Future recording: Forward button advances UT to recording start
                    string ffReason;
                    bool isRecording = parentUI.InFlightMode && flight.IsRecording;
                    bool canFF = RecordingStore.CanFastForward(rec, out ffReason, isRecording: isRecording);
                    bool prevFF;
                    if (!lastCanFF.TryGetValue(ri, out prevFF) || prevFF != canFF)
                    {
                        lastCanFF[ri] = canFF;
                        ParsekLog.Verbose("UI", $"Forward #{ri} \"{rec.VesselName}\": {(canFF ? "enabled" : "disabled — " + ffReason)}");
                    }
                    GUI.enabled = canFF;
                    string tooltip = canFF
                        ? "Fast-forward to this launch"
                        : ffReason;
                    if (DrawRewindColumnButton(new GUIContent(FastForwardActionLabel, tooltip)))
                    {
                        ParsekLog.Info("UI", $"Forward button clicked: #{ri} \"{rec.VesselName}\"");
                        ShowFastForwardConfirmation(rec);
                    }
                    GUI.enabled = true;
                }
                else if (ShouldShowLegacyRewindButton(rec, now))
                {
                    // Past/active recording with save AND we are the rewind
                    // owner: render the Rewind button. The owner gate inside
                    // ShouldShowLegacyRewindButton suppresses tree branches
                    // (debris / decouple children / EVA splits) so the player
                    // only sees one Rewind per tree — on the launch row.
                    // The unfinished-flight gate inside the helper suppresses
                    // Rewind only for the row that is itself an unfinished flight
                    // (it gets Rewind-to-Staging instead via
                    // DrawUnfinishedFlightRewindButton); the chain HEAD keeps
                    // Rewind-to-launch even when a sibling chain TIP is the
                    // unfinished flight.
                    string rewindReason;
                    bool isRecording = parentUI.InFlightMode && flight.IsRecording;
                    bool canRewind = RecordingStore.CanRewind(rec, out rewindReason, isRecording: isRecording);
                    bool prevR;
                    if (!lastCanRewind.TryGetValue(ri, out prevR) || prevR != canRewind)
                    {
                        lastCanRewind[ri] = canRewind;
                        ParsekLog.Verbose("UI", $"Rewind #{ri} \"{rec.VesselName}\": {(canRewind ? "enabled" : "disabled — " + rewindReason)}");
                    }
                    // Owner row → suppression flag is now false; clear the
                    // debounce + log the flip if it was true previously.
                    bool prevSuppressed;
                    if (lastSuppressedTreeBranch.TryGetValue(ri, out prevSuppressed) && prevSuppressed)
                    {
                        lastSuppressedTreeBranch[ri] = false;
                        ParsekLog.Verbose("UI", $"Rewind #{ri} \"{rec.VesselName}\": no longer suppressed — owner row");
                    }
                    GUI.enabled = canRewind;
                    string tooltip = canRewind
                        ? "Rewind to this launch"
                        : rewindReason;
                    if (DrawRewindColumnButton(new GUIContent(RewindActionLabel, tooltip)))
                    {
                        ParsekLog.Info("UI", $"Rewind button clicked: #{ri} \"{rec.VesselName}\"");
                        ShowRewindConfirmation(rec);
                    }
                    GUI.enabled = true;
                }
                else
                {
                    // Tree-branch suppression: log once when this row first
                    // becomes a non-owner with a (resolved) rewind save, and
                    // again whenever it flips back. hasRewindSave is true via
                    // the tree root, but GetRewindRecording != rec, so the R
                    // button would have been redundant.
                    var owner = RecordingStore.GetRewindRecording(rec);
                    bool suppressedTreeBranch = hasRewindSave
                        && owner != null
                        && !ReferenceEquals(owner, rec)
                        && !EffectiveState.IsChainMemberOfUnfinishedFlight(rec);
                    bool prevSuppressed;
                    if (!lastSuppressedTreeBranch.TryGetValue(ri, out prevSuppressed)
                        || prevSuppressed != suppressedTreeBranch)
                    {
                        lastSuppressedTreeBranch[ri] = suppressedTreeBranch;
                        if (suppressedTreeBranch)
                        {
                            ParsekLog.Verbose("UI",
                                $"Rewind #{ri} \"{rec.VesselName}\": suppressed — tree branch, use root recording's Rewind button");
                        }
                        else
                        {
                            ParsekLog.Verbose("UI",
                                $"Rewind #{ri} \"{rec.VesselName}\": no longer suppressed");
                        }
                    }
                    GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Rewind));
                }
            }
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowRewind");

            // Hide checkbox
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Hide));
            GUILayout.FlexibleSpace();
            bool hidden = GUILayout.Toggle(rec.Hidden, "");
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
                if (EffectiveState.IsUnfinishedFlight(rec))
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
            IReadOnlyList<Recording> committed, float indentPx)
        {
            // Uniform 2px nudge so rows (which have a "#" number in col 1) line up
            // their Name text with the group/chain header Name text above. Group/
            // chain headers render the arrow at the cell's raw origin; rows'
            // Name button gets shifted right by this 2px to match visually.
            GUILayout.Space(2f);
            // Indent inside Name column for grouped/chained subitems.
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
            Dictionary<string, List<string>> grpChildren,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null)
        {
            // Compute this tree's unfinished-flight members up front so the
            // nested virtual subgroup can be rendered even when the mission
            // group itself is hidden. Unfinished Flights is a system group
            // (design §7.30) and must remain visible regardless of parent
            // hide state — without this, hiding the auto-generated mission
            // group silently made unresolved re-fly opportunities disappear.
            var nestedUnfinished = CollectUnfinishedFlightsForTreeGroup(groupName);
            bool hasNestedUnfinished = nestedUnfinished != null && nestedUnfinished.Count > 0;

            // Skip hidden groups when hide is active — but still render the
            // nested Unfinished Flights subgroup if any, as an escape hatch.
            if (GroupHierarchyStore.HideActive && GroupHierarchyStore.IsGroupHidden(groupName))
            {
                if (hasNestedUnfinished
                    && DrawVirtualUnfinishedFlightsGroup(committed, now, supersedes, depth + 1, nestedUnfinished))
                    return true;
                return false;
            }

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

            // Lead-gap before Name column (see DrawRecordingRow for rationale).
            GUILayout.Space(NameColumnLeadGap);

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
                        if (RecordingStore.IsPermanentRootGroup(groupName))
                        {
                            ParsekLog.Verbose("UI", $"Rename blocked for permanent root group '{groupName}'");
                            lastClickedGroup = null;
                            return false;
                        }

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
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Phase));

            // Site (from main/root recording if available)
            int grpMainIdx = FindGroupMainRecordingIndex(descendants, committed);
            string grpSite = grpMainIdx >= 0 ? committed[grpMainIdx].LaunchSiteName : null;
            GUILayout.Label(grpSite ?? "", bodyCellLabel, GUILayout.Width(ColW_Site));

            // Launch time (earliest among descendants)
            double grpEarliest = GetGroupEarliestStartUT(descendants, committed);
            string grpLaunchText = (memberCount > 0 && grpEarliest < double.MaxValue)
                ? KSPUtil.PrintDateCompact(grpEarliest, true)
                : "-";
            GUILayout.Label(grpLaunchText, bodyCellLabel, GUILayout.Width(ColW_Launch));

            // Duration (sum of descendant durations)
            double grpTotalDur = GetGroupTotalDuration(descendants, committed);
            GUILayout.Label(FormatDuration(grpTotalDur), bodyCellLabel, GUILayout.Width(ColW_Dur));

            // Expanded stats spacers
            if (showExpandedStats)
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_MaxAlt));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_MaxSpd));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Dist));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Pts));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartPos));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndPos));
            }

            // Status (closest active T- among descendants)
            string grpStatusText;
            int grpStatusOrder;
            GetGroupStatus(descendants, committed, now, out grpStatusText, out grpStatusOrder);
            GUIStyle grpStatusStyle = grpStatusOrder == 0 ? statusStyleFuture
                : grpStatusOrder == 1 ? statusStyleActive
                : statusStylePast;
            GUILayout.Label(grpStatusText, grpStatusStyle, GUILayout.Width(ColW_Status));

            bool canDisbandGroup = !RecordingStore.IsPermanentGroup(groupName);

            // Group management buttons: custom groups get G + X (wrapped to match the
            // single-G footprint of permanent groups); system-owned groups keep only
            // G via DrawBodyCenteredButton.
            bool grpGClicked;
            bool grpXClicked = false;
            if (canDisbandGroup)
            {
                DrawBodyCenteredTwoButtons("G", "X", ColW_Group, out grpGClicked, out grpXClicked);
            }
            else
            {
                grpGClicked = DrawBodyCenteredButton("G", ColW_Group);
            }
            if (grpGClicked)
            {
                var mousePos = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                groupPicker.OpenForGroup(groupName, mousePos);
                ParsekLog.Verbose("UI", $"Group popup opened for group '{groupName}'");
            }
            if (grpXClicked)
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
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Period));

            var flight = parentUI.Flight;

            // Find the "main" (earliest non-debris) recording for group-level W and Rewind/Forward buttons
            int mainIdx = FindGroupMainRecordingIndex(descendants, committed);

            // Watch button (flight only) — Bug #382: cycles through eligible
            // descendants on repeated presses instead of toggling a single main.
            if (parentUI.InFlightMode)
            {
                // Bug #382: build the rotation and let the W button cycle through
                // eligible descendants. The helper is pure — we pass a closure
                // capturing the live eligibility probes; cursorRecId comes from
                // groupWatchCursorByGroupName (null on the first press or after prune).
                Func<int, bool> isEligibleForWatch = idx =>
                {
                    if (idx < 0 || idx >= committed.Count) return false;
                    var r = committed[idx];
                    if (r == null || r.IsDebris) return false;
                    if (!flight.HasActiveGhost(idx)) return false;
                    if (!flight.IsGhostOnSameBody(idx)) return false;
                    if (!flight.IsGhostWithinVisualRange(idx)) return false;
                    return true;
                };

                // W* indicator: any descendant currently under watch → W*.
                int watchedIdx = flight.WatchedRecordingIndex;
                bool anyDescendantWatched =
                    watchedIdx >= 0
                    && descendants.Contains(watchedIdx)
                    && watchedIdx < committed.Count
                    && committed[watchedIdx] != null;
                string watchedDescendantRecId = anyDescendantWatched ? committed[watchedIdx].RecordingId : null;

                string cursorRecId;
                groupWatchCursorByGroupName.TryGetValue(groupName, out cursorRecId);
                GhostPlaybackLogic.GroupWatchAdvanceResult rotation = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                    descendants, committed, isEligibleForWatch, cursorRecId, watchedDescendantRecId);

                int nextTargetIdx = rotation.NextRecordingId != null
                    ? GhostPlaybackLogic.FindRecordingIndexById(committed, rotation.NextRecordingId)
                    : -1;

                // nextTargetIdx can legitimately be -1 even if rotation.NextRecordingId != null
                // when the recording has been removed between draws — treat as empty rotation.
                if (nextTargetIdx < 0)
                    rotation = GhostPlaybackLogic.GroupWatchAdvanceResult.Empty;

                bool canWatch = rotation.NextRecordingId != null && nextTargetIdx >= 0 && !rotation.IsToggleOff;
                bool isToggleOffPress = rotation.IsToggleOff && anyDescendantWatched;
                bool isWatching = anyDescendantWatched;

                // Bug #279 transition logging. Key by groupName + mainRecId (unchanged).
                // To keep this log silent on cursor rotations (would otherwise spam
                // once per press), log the eligibility-set fingerprint as
                // resolvedTargetId instead of an individual target id — the set only
                // changes when a vessel enters/leaves eligibility, not on cursor
                // advance. Fingerprint is the sorted, comma-joined RecordingIds.
                if (mainIdx >= 0)
                {
                    string mainRecId = committed[mainIdx].RecordingId;
                    if (!string.IsNullOrEmpty(mainRecId))
                    {
                        string groupWatchKey = groupName + "/" + mainRecId;
                        string resolvedTargetId = BuildEligibleSetFingerprint(
                            descendants, committed, isEligibleForWatch);
                        bool prevGroupCanWatch;
                        string prevResolvedTargetId;
                        if (!lastCanWatchByGroup.TryGetValue(groupWatchKey, out prevGroupCanWatch)
                            || prevGroupCanWatch != canWatch
                            || !lastResolvedWatchTargetByGroup.TryGetValue(groupWatchKey, out prevResolvedTargetId)
                            || prevResolvedTargetId != resolvedTargetId)
                        {
                            lastCanWatchByGroup[groupWatchKey] = canWatch;
                            lastResolvedWatchTargetByGroup[groupWatchKey] = resolvedTargetId;
                            string reason = GetWatchButtonReason(canWatch,
                                hasGhost: nextTargetIdx >= 0,
                                sameBody: nextTargetIdx >= 0,
                                inRange: nextTargetIdx >= 0,
                                isDebris: false);
                            ParsekLog.Info("UI",
                                $"Group Watch button '{groupName}' source=#{mainIdx} \"{committed[mainIdx].VesselName}\" " +
                                $"next={(nextTargetIdx >= 0 ? "#" + nextTargetIdx + " \"" + committed[nextTargetIdx].VesselName + "\"" : "<none>")} " +
                                $"pos={rotation.Position}/{rotation.TotalEligible} eligibleSet=[{resolvedTargetId}] {reason} " +
                                $"{BuildWatchObservabilitySuffix(flight, mainIdx, nextTargetIdx)}");
                        }
                    }
                }

                GUI.enabled = canWatch || isToggleOffPress;
                string watchLabel = isWatching ? "W*" : "W";
                string watchTooltip;
                if (rotation.TotalEligible == 0)
                    watchTooltip = "no watchable vessels in this group";
                else if (isWatching && nextTargetIdx >= 0 && rotation.NextRecordingId != watchedDescendantRecId)
                    watchTooltip = $"switch to {committed[nextTargetIdx].VesselName}";
                else if (isWatching && rotation.IsToggleOff)
                    watchTooltip = "exit watch (no other watchable vessels)";
                else if (nextTargetIdx >= 0)
                    watchTooltip = $"enter watch on {committed[nextTargetIdx].VesselName}";
                else
                    watchTooltip = GetWatchButtonTooltip(isWatching, false, false, false, false);

                if (DrawBodyCenteredButton(new GUIContent(watchLabel, watchTooltip), ColW_Watch))
                {
                    string beforeFocus = flight.DescribeWatchFocusForLogs();
                    string beforeEligibility = BuildWatchObservabilitySuffix(flight, mainIdx, nextTargetIdx);
                    string pressKind;
                    if (isToggleOffPress)
                    {
                        flight.ExitWatchMode();
                        groupWatchCursorByGroupName.Remove(groupName);
                        pressKind = "toggle-off";
                    }
                    else if (nextTargetIdx >= 0)
                    {
                        flight.EnterWatchMode(nextTargetIdx);
                        groupWatchCursorByGroupName[groupName] = rotation.NextRecordingId;
                        pressKind = string.IsNullOrEmpty(cursorRecId)
                            ? "enter"
                            : (rotation.IsWrap ? "wrap" : "advance");
                    }
                    else
                    {
                        // Unreachable: GUI.enabled is canWatch || isToggleOffPress,
                        // and canWatch==true implies nextTargetIdx>=0. If this branch
                        // ever fires, the enable-gate has drifted — log a warning so
                        // the regression surfaces instead of silently no-opping.
                        ParsekLog.Warn("UI",
                            $"Group '{groupName}' W button press reached unreachable branch " +
                            $"(canWatch={canWatch} isToggleOffPress={isToggleOffPress} nextTargetIdx={nextTargetIdx}) — " +
                            "enable-gate and press-dispatch are out of sync");
                        pressKind = "no-op";
                    }
                    ParsekLog.Info("UI",
                        $"Group '{groupName}' W button: {pressKind} " +
                        $"next={(nextTargetIdx >= 0 ? "#" + nextTargetIdx + " \"" + committed[nextTargetIdx].VesselName + "\"" : "<none>")} " +
                        $"pos={rotation.Position}/{rotation.TotalEligible} " +
                        $"cursorBefore={cursorRecId ?? "<null>"} " +
                        $"before={beforeEligibility} beforeFocus={beforeFocus} afterFocus={flight.DescribeWatchFocusForLogs()}");
                }
                GUI.enabled = true;
            }

            // Rewind / Forward button — targets main recording
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
                    if (DrawRewindColumnButton(new GUIContent(FastForwardActionLabel, tooltip)))
                    {
                        ParsekLog.Info("UI", $"Group '{groupName}' Forward button: #{mainIdx} \"{mainRec.VesselName}\"");
                        ShowFastForwardConfirmation(mainRec);
                    }
                    GUI.enabled = true;
                }
                else if (ShouldShowLegacyRewindButton(mainRec, now))
                {
                    // Mirror of the per-row gate. Group's main recording must
                    // be the rewind owner — tree roots are the typical case.
                    // If the group's main happens to be a non-owner branch
                    // (rare, but possible for a group whose root is hidden or
                    // pruned), suppress the Rewind button here too so the column
                    // doesn't render a redundant duplicate.
                    string rewindReason;
                    bool canRewind = RecordingStore.CanRewind(mainRec, out rewindReason, isRecording: isRecording);
                    GUI.enabled = canRewind;
                    string tooltip = canRewind ? "Rewind to this launch" : rewindReason;
                    if (DrawRewindColumnButton(new GUIContent(RewindActionLabel, tooltip)))
                    {
                        ParsekLog.Info("UI", $"Group '{groupName}' Rewind button: #{mainIdx} \"{mainRec.VesselName}\"");
                        ShowRewindConfirmation(mainRec);
                    }
                    GUI.enabled = true;
                }
                else
                {
                    GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Rewind));
                }
            }
            else
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Rewind));
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

            // When this tree owns Unfinished Flight members (rendered below as
            // the nested virtual subgroup), exclude them from the regular
            // member list so the same recording does not appear twice in the
            // table — once as a top-level tree row and once inside the
            // Unfinished Flights group. Without this filter the UF row
            // duplicates because grpToRecs stores raw tree membership and
            // does not know about the virtual subgroup.
            List<int> displayMembers = hasNestedUnfinished
                ? FilterUnfinishedFlightRowsForRegularTree(directMembers, committed, groupName)
                : directMembers;

            if (displayMembers != null)
            {
                var displayBlocks = BuildGroupDisplayBlocks(groupName, displayMembers, committed, chainToRecs);
                for (int i = 0; i < displayBlocks.Count; i++)
                {
                    var block = displayBlocks[i];
                    if (block.Members == null || block.Members.Count == 0)
                        continue;

                    if (block.Members.Count > 1)
                    {
                        if (DrawGroupedRecordingBlock(block.Key, block.DisplayName,
                            block.Members, depth + 1, committed, now, supersedes))
                            return true;
                    }
                    else if (DrawRecordingRow(block.Members[0], committed, now, (depth + 1) * 15f, supersedes))
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
                        grpToRecs, chainToRecs, grpChildren, supersedes))
                        return true;
                }
            }

            // Nested Unfinished Flights: if this group is the auto-generated
            // root group of a tree that has any Unfinished Flight members,
            // render the virtual group as an indented sub-entry. This replaces
            // the pre-2026-04-24 design where the virtual group sat at root
            // level, detached from the mission it belongs to.
            // `nestedUnfinished` was already computed at the top of
            // DrawGroupTree so the hide-escape path can use it too.
            if (hasNestedUnfinished
                && DrawVirtualUnfinishedFlightsGroup(committed, now, supersedes, depth + 1, nestedUnfinished))
                return true;

            return false;
        }

        /// <summary>
        /// Returns the Unfinished Flight recordings that belong to the tree
        /// whose auto-generated root group name matches <paramref name="groupName"/>.
        /// Used by <see cref="DrawGroupTree"/> to decide where to nest the
        /// virtual Unfinished Flights subgroup. Returns an empty list (not
        /// null) if the group isn't any tree's root, or if no tree has
        /// unfinished members right now.
        /// </summary>
        private static IReadOnlyList<Recording> CollectUnfinishedFlightsForTreeGroup(string groupName)
        {
            if (string.IsNullOrEmpty(groupName)) return null;

            var trees = RecordingStore.CommittedTrees;
            if (trees == null) return null;

            string treeId = null;
            for (int i = 0; i < trees.Count; i++)
            {
                var tree = trees[i];
                if (tree == null) continue;
                if (string.Equals(tree.AutoGeneratedRootGroupName, groupName, StringComparison.Ordinal))
                {
                    treeId = tree.Id;
                    break;
                }
            }
            if (string.IsNullOrEmpty(treeId)) return null;

            var allMembers = UnfinishedFlightsGroup.ComputeMembers();
            if (allMembers == null || allMembers.Count == 0) return null;

            var filtered = new List<Recording>();
            for (int i = 0; i < allMembers.Count; i++)
            {
                var rec = allMembers[i];
                if (rec == null) continue;
                if (string.Equals(rec.TreeId, treeId, StringComparison.Ordinal))
                    filtered.Add(rec);
            }
            return filtered;
        }

        /// <summary>
        /// Phase 5 of Rewind-to-Staging (design §5.11 / §7.25 / §7.30). Draws
        /// the virtual "Unfinished Flights" group row. Members are computed
        /// each frame from ERS filtered through
        /// <see cref="EffectiveState.IsUnfinishedFlight"/>. The row:
        /// <list type="bullet">
        ///   <item><description>has NO X disband button (mirrors the chain block precedent in <see cref="DrawChainBlock"/>);</description></item>
        ///   <item><description>has NO hide checkbox (design §7.30); consults <see cref="GroupHierarchyStore.CanHide"/>;</description></item>
        ///   <item><description>is NOT a drop target for manual group assignment (design §7.25); the G button is absent on the group header.</description></item>
        /// </list>
        /// Per-member rows render via <see cref="DrawRecordingRow"/> so rename
        /// / hide / G button on the individual row remain usable per §7.33.
        /// Returns true if the recording list was modified.
        ///
        /// <para>
        /// Called by <see cref="DrawGroupTree"/> once per owning mission tree
        /// when any of that tree's recordings are Unfinished Flights, with
        /// <paramref name="filteredMembers"/> narrowed to that tree's
        /// unfinished members and <paramref name="depth"/> set to the parent
        /// tree-group depth + 1 so the virtual group is indented as a
        /// sub-group instead of floating at root level.
        /// </para>
        /// </summary>
        private bool DrawVirtualUnfinishedFlightsGroup(
            IReadOnlyList<Recording> committed, double now,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null,
            int depth = 0,
            IReadOnlyList<Recording> filteredMembers = null)
        {
            var members = filteredMembers ?? UnfinishedFlightsGroup.ComputeMembers();
            if (members == null || members.Count == 0)
                return false;

            string groupName = UnfinishedFlightsGroup.GroupName;
            float indent = depth * 15f;

            // Build descendants set (committed-list indices) so the shared
            // group helpers (status / earliest / duration) can work unchanged.
            var descendants = new HashSet<int>();
            for (int m = 0; m < members.Count; m++)
            {
                var rec = members[m];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                for (int c = 0; c < committed.Count; c++)
                {
                    if (committed[c] == null) continue;
                    if (string.Equals(committed[c].RecordingId, rec.RecordingId, StringComparison.Ordinal))
                    {
                        descendants.Add(c);
                        break;
                    }
                }
            }

            int memberCount = descendants.Count;
            if (memberCount == 0)
                return false;

            GUILayout.BeginHorizontal();

            // -- Enable checkbox (aggregate) --
            int enabledCount = 0;
            foreach (int idx in descendants)
                if (committed[idx].PlaybackEnabled) enabledCount++;
            bool allEnabled = memberCount > 0 && enabledCount == memberCount;
            bool newEnabled = GUILayout.Toggle(allEnabled, "", GUILayout.Width(ColW_Enable));
            if (newEnabled != allEnabled)
            {
                foreach (int idx in descendants)
                    committed[idx].PlaybackEnabled = newEnabled;
                ParsekLog.Info("UI",
                    $"Virtual group '{groupName}' playback enabled={newEnabled} ({memberCount} recordings)");
            }

            // # spacer (indent column)
            GUILayout.Label("", GUILayout.Width(ColW_Index));
            GUILayout.Space(NameColumnLeadGap);
            if (indent > 0f)
                GUILayout.Space(indent);

            // Expand / collapse toggle + label — no rename (system group).
            bool expanded = expandedGroups.Contains(groupName);
            string arrow = expanded ? "\u25bc" : "\u25b6";
            var ufGroupContent = new GUIContent(
                $"{arrow} {groupName} ({memberCount})",
                UnfinishedFlightsGroup.Tooltip);
            if (GUILayout.Button(ufGroupContent, GUI.skin.label, GUILayout.ExpandWidth(true)))
            {
                if (expanded) expandedGroups.Remove(groupName);
                else expandedGroups.Add(groupName);
                expanded = !expanded;
                ParsekLog.Verbose("UI",
                    $"Virtual group '{groupName}' {(expanded ? "expanded" : "collapsed")} ({memberCount} recordings)");
            }

            // Phase placeholder.
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Phase));

            // Site from main recording if any.
            int mainIdx = FindGroupMainRecordingIndex(descendants, committed);
            string grpSite = mainIdx >= 0 ? committed[mainIdx].LaunchSiteName : null;
            GUILayout.Label(grpSite ?? "", bodyCellLabel, GUILayout.Width(ColW_Site));

            // Earliest start UT.
            double grpEarliest = GetGroupEarliestStartUT(descendants, committed);
            string grpLaunchText = (memberCount > 0 && grpEarliest < double.MaxValue)
                ? KSPUtil.PrintDateCompact(grpEarliest, true)
                : "-";
            GUILayout.Label(grpLaunchText, bodyCellLabel, GUILayout.Width(ColW_Launch));

            // Total duration.
            double grpTotalDur = GetGroupTotalDuration(descendants, committed);
            GUILayout.Label(FormatDuration(grpTotalDur), bodyCellLabel, GUILayout.Width(ColW_Dur));

            if (showExpandedStats)
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_MaxAlt));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_MaxSpd));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Dist));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Pts));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartPos));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndPos));
            }

            // Status.
            string grpStatusText;
            int grpStatusOrder;
            GetGroupStatus(descendants, committed, now, out grpStatusText, out grpStatusOrder);
            GUIStyle grpStatusStyle = grpStatusOrder == 0 ? statusStyleFuture
                : grpStatusOrder == 1 ? statusStyleActive
                : statusStylePast;
            GUILayout.Label(grpStatusText, grpStatusStyle, GUILayout.Width(ColW_Status));

            // System group: no G button (design §7.25 rejects adds), no X
            // disband button (design §7.30 the group cannot be removed). Keep
            // the column occupied with an empty cell so sibling rows stay
            // aligned.
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Group));

            // Loop aggregate placeholder — the virtual Unfinished Flights group
            // hides its loop toggle (and the per-member rows hide theirs too,
            // see DrawRecordingRow) because the group is a re-fly TODO list.
            // Surfacing an aggregate "loop all" toggle here would write
            // LoopPlayback back to every member, undoing the hide-from-the-
            // TODO-surface intent. The per-recording loop state stays
            // editable from the same recording's row in its real (mission)
            // group. Render an empty cell to keep the column aligned.
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Loop));

            // Period placeholder — paired with the suppressed loop aggregate
            // above so the virtual group exposes no playback configuration.
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Period));

            // Watch placeholder (flight only) — Unfinished Flights row does
            // not expose a group-level W button; per-row W remains available.
            if (parentUI.InFlightMode)
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Watch));

            // Rewind / Forward placeholder — the virtual group has no
            // group-level Re-Fly button because Unfinished Flights is a
            // special system group: each member maps to a specific RP child
            // slot, so a single aggregate "re-fly all" makes no sense. The
            // per-row Re-Fly button (DrawUnfinishedFlightRewindButton) is the
            // only action surface for this group.
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Rewind));

            // Hide checkbox — design §7.30: system group cannot be hidden.
            // We consult GroupHierarchyStore.CanHide so the gate lives in the
            // store (single source of truth). Render an empty cell to keep
            // alignment.
            if (GroupHierarchyStore.CanHide(groupName))
            {
                // Unreachable in Phase 5, but guard the branch so a future
                // CanHide flip does not silently drop the control.
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
                    ParsekLog.Info("UI",
                        $"Virtual group '{groupName}' hide-all={newAllHidden} ({memberCount} recordings)");
                }
            }
            else
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Hide));
            }

            GUILayout.EndHorizontal();

            if (!expanded) return false;

            // -- Draw member rows --
            // Order by StartUT to match the group-level sort.
            var sortedMembers = new List<int>(descendants);
            sortedMembers.Sort((a, b) =>
            {
                double ua = committed[a].StartUT;
                double ub = committed[b].StartUT;
                return ua.CompareTo(ub);
            });

            unfinishedFlightRowDepth++;
            try
            {
                float memberIndent = (depth + 1) * 15f;
                for (int i = 0; i < sortedMembers.Count; i++)
                {
                    if (DrawRecordingRow(sortedMembers[i], committed, now, memberIndent, supersedes))
                        return true;
                }
            }
            finally
            {
                unfinishedFlightRowDepth--;
            }

            return false;
        }

        /// <summary>
        /// Phase 6 of Rewind-to-Staging (design §6.3). Renders the Rewind-to-RP
        /// button for an Unfinished Flight row, whether the row is shown in the
        /// normal recording list or inside the virtual group. Returns
        /// <c>true</c> iff this row consumed the Rewind/Forward cell, so the caller
        /// can skip the legacy rewind-to-launch fallback.
        ///
        /// <para>
        /// Uses the same Rewind-column width as the legacy Rewind/Forward
        /// buttons so the table column renders consistently regardless of
        /// which path drew the cell.
        /// </para>
        /// </summary>
        private bool DrawUnfinishedFlightRewindButton(
            Recording rec, int ri, double now, bool reserveCellWhenUnavailable = false)
        {
            if (rec == null) return false;

            // Find the RP + child-slot list index for this recording. This is
            // deliberately not limited to rows rendered under the virtual
            // Unfinished Flights group: the normal list shows the same
            // recordings, and sending those rows through RecordingStore's
            // launch-save fallback would rewind to the tree root instead of
            // the staging split.
            RewindPoint rp;
            int slotListIndex;
            string routeReason;
            UnfinishedFlightRewindRoute route = ResolveUnfinishedFlightRewindRoute(
                rec, out rp, out slotListIndex, out routeReason);
            if (route == UnfinishedFlightRewindRoute.NotUnfinishedFlight)
            {
                if (reserveCellWhenUnavailable)
                {
                    GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Rewind));
                    return true;
                }

                return false;
            }

            // Always "Fly" — matches the Timeline-window separation-row label
            // (DrawTimelineFlyButton) so the same action carries the same
            // glyph in both surfaces. The action is qualitatively different
            // from the legacy Rewind / Forward buttons (rewind time and watch
            // playback): clicking this loads a Rewind Point quicksave,
            // places the player in control of the unfinished sibling vessel,
            // and starts a re-fly session (marker, supersede tracking,
            // merge dialog later). Past-vs-future relative to current UT is
            // irrelevant for the user-facing label. `now` is kept on the
            // signature for future per-row state if needed.
            const string kReFlyLabel = "Fly";
            _ = now;

            if (route == UnfinishedFlightRewindRoute.MissingSlot)
            {
                DrawDisabledUnfinishedFlightRewindButton(
                    rec, ri, routeReason ?? "Rewind point slot not found");
                return true;
            }

            if (rp == null || slotListIndex < 0 || rp.ChildSlots == null
                || slotListIndex >= rp.ChildSlots.Count)
            {
                DrawDisabledUnfinishedFlightRewindButton(
                    rec, ri, "Rewind point slot not found");
                return true;
            }

            string reason;
            bool canInvoke = CanInvokeRewindPointSlot(rp, slotListIndex, out reason);
            bool prev;
            string rpKey = rp.RewindPointId ?? "<no-id>";
            var selectedSlot = rp.ChildSlots[slotListIndex];
            int slotId = selectedSlot != null ? selectedSlot.SlotIndex : slotListIndex;
            string invokeKey = rpKey + "/" + slotId.ToString(CultureInfo.InvariantCulture);
            if (!lastCanInvoke.TryGetValue(invokeKey, out prev) || prev != canInvoke)
            {
                lastCanInvoke[invokeKey] = canInvoke;
                ParsekLog.Verbose("RewindUI",
                    $"Re-Fly #{ri} rp={rpKey} slot={slotId}: " +
                    $"{(canInvoke ? "enabled" : "disabled — " + reason)}");
            }

            string tooltip = canInvoke
                ? "Re-fly this unfinished flight from the separation moment"
                : (reason ?? "Re-Fly unavailable");
            bool flyClicked;
            bool sealClicked;
            DrawBodyCenteredTwoButtons(
                new GUIContent(kReFlyLabel, tooltip), canInvoke,
                new GUIContent("Seal", "Close this re-fly slot permanently without changing the recording"), true,
                ColW_Rewind, out flyClicked, out sealClicked);
            if (flyClicked)
            {
                ParsekLog.Info("RewindUI",
                    $"Button clicked: rp={rpKey} slot={slotId} rec=\"{rec.VesselName}\"");
                RewindInvoker.ShowDialog(rp, slotListIndex);
            }

            if (sealClicked)
            {
                ParsekLog.Info("UnfinishedFlights",
                    $"Seal button clicked rec={rec.RecordingId ?? "<no-id>"} rp={rpKey} slot={slotId}");
                UnfinishedFlightSealHandler.ShowConfirmation(rec);
            }
            return true;
        }

        private void DrawDisabledUnfinishedFlightRewindButton(
            Recording rec, int ri, string reason)
        {
            reason = string.IsNullOrEmpty(reason) ? "Re-Fly unavailable" : reason;
            string recId = rec?.RecordingId ?? "<no-id>";
            string key = "disabled/" + recId + "/" + reason;
            bool prev;
            if (!lastCanInvoke.TryGetValue(key, out prev) || prev)
            {
                lastCanInvoke[key] = false;
                ParsekLog.Verbose("RewindUI",
                    $"Re-Fly #{ri} rec={recId} disabled — {reason}");
            }

            bool ignoredFlyClicked;
            bool ignoredSealClicked;
            DrawBodyCenteredTwoButtons(
                new GUIContent("Fly", reason), false,
                new GUIContent("Seal", reason), false,
                ColW_Rewind, out ignoredFlyClicked, out ignoredSealClicked);
        }

        private bool DrawStashUnfinishedFlightButton(Recording rec, int ri)
        {
            if (rec == null) return false;

            RewindPoint rp;
            int slotListIndex;
            string reason;
            if (!TryResolveStashableUnfinishedFlightRewindPoint(
                    rec, out rp, out slotListIndex, out reason))
                return false;

            if (rp == null || rp.ChildSlots == null
                || slotListIndex < 0 || slotListIndex >= rp.ChildSlots.Count)
                return false;

            var slot = rp.ChildSlots[slotListIndex];
            int slotId = slot != null ? slot.SlotIndex : slotListIndex;
            string tooltip =
                "Stash this stable Rewind Point slot in Unfinished Flights so it can be re-flown later";
            if (DrawRewindColumnButton(new GUIContent("Stash", tooltip)))
            {
                string rpKey = rp.RewindPointId ?? "<no-id>";
                ParsekLog.Info("UnfinishedFlights",
                    $"Stash button clicked rec={rec.RecordingId ?? "<no-id>"} rp={rpKey} slot={slotId}");
                string stashReason;
                if (!UnfinishedFlightStashHandler.TryStash(rec, out stashReason))
                {
                    ParsekLog.Warn("UnfinishedFlights",
                        $"Stash button failed rec={rec.RecordingId ?? "<no-id>"} reason={stashReason ?? "<none>"}");
                    ParsekLog.ScreenMessage(
                        $"Cannot stash '{rec.VesselName ?? rec.RecordingId ?? "<unnamed>"}': " +
                        (stashReason ?? "slot is unavailable"),
                        4f);
                }
            }

            return true;
        }

        internal enum UnfinishedFlightRewindRoute
        {
            NotUnfinishedFlight,
            Resolved,
            MissingSlot
        }

        internal static UnfinishedFlightRewindRoute ResolveUnfinishedFlightRewindRoute(
            Recording rec, out RewindPoint rp, out int slotListIndex, out string reason)
        {
            rp = null;
            slotListIndex = -1;
            reason = null;

            if (!IsVisibleUnfinishedFlight(rec, out reason))
                return UnfinishedFlightRewindRoute.NotUnfinishedFlight;

            if (!TryResolveRewindPointForRecording(rec, out rp, out slotListIndex))
            {
                reason = "Rewind point slot not found";
                return UnfinishedFlightRewindRoute.MissingSlot;
            }

            return UnfinishedFlightRewindRoute.Resolved;
        }

        /// <summary>
        /// Decides whether a row should render the legacy Rewind
        /// (Rewind-to-launch) button. The legacy button only makes sense on the
        /// recording that actually owns the quicksave: standalone recordings
        /// (own <c>RewindSaveFileName</c>) and tree roots that captured the
        /// save on behalf of their tree. Tree branches (debris, decouple
        /// children, EVA splits) inherited the save through
        /// <see cref="RecordingStore.GetRewindRecording"/> and would draw
        /// duplicate buttons that all rewind to the same root launch — the
        /// player sees four identical Rewind buttons after a normal merge and
        /// reasonably concludes they're broken. Future rows take the Forward path
        /// instead, and rows that ARE THEMSELVES an unfinished flight render
        /// the Rewind-to-Staging button drawn separately by
        /// <c>DrawUnfinishedFlightRewindButton</c>; the chain HEAD (the launch
        /// row that owns the rewind quicksave) keeps its Rewind-to-launch even when
        /// a sibling chain TIP is the unfinished flight, so the player can
        /// always rewind a launch to the pad.
        /// </summary>
        internal static bool ShouldShowLegacyRewindButton(Recording rec, double now)
        {
            if (rec == null) return false;
            // Future recording — the Forward path renders instead. Keep the legacy
            // gate strictly past/active so a flipped clock can't double-render.
            if (now < rec.StartUT) return false;
            // Owner gate: only the recording that holds the rewind save
            // (standalone or tree root) should expose the legacy button.
            // Reference equality — GetRewindRecording returns the same instance
            // when rec is the owner and the tree root recording instance
            // otherwise.
            var owner = RecordingStore.GetRewindRecording(rec);
            if (owner == null) return false;
            if (!ReferenceEquals(owner, rec)) return false;
            // Suppress only when THIS row is itself an unfinished flight: the
            // Rewind-to-Staging button (DrawUnfinishedFlightRewindButton) takes
            // over and offering rewind-to-launch alongside it would be a
            // footgun. We must NOT suppress when only some OTHER chain member
            // is the unfinished flight — that incorrectly hides Rewind on the
            // launch row of a multi-segment chain whose destroyed continuation
            // (TIP) carries the BP link, leaving the player no way to rewind
            // the mission to the pad. See bug: launch root recording with
            // chainIndex=0 + destroyed chain TIP at chainIndex=1.
            if (EffectiveState.IsUnfinishedFlight(rec)) return false;
            return true;
        }

        /// <summary>
        /// Resolves the RewindPoint + child-slot list index for an unfinished
        /// flight recording. Used by both normal rows and the virtual
        /// Unfinished Flights group so the row cannot accidentally fall back to
        /// the tree root's legacy launch rewind.
        /// </summary>
        internal static bool TryResolveUnfinishedFlightRewindPoint(
            Recording rec, out RewindPoint rp, out int slotListIndex)
        {
            string reason;
            return ResolveUnfinishedFlightRewindRoute(
                rec, out rp, out slotListIndex, out reason)
                == UnfinishedFlightRewindRoute.Resolved;
        }

        internal static bool TryResolveStashableUnfinishedFlightRewindPoint(
            Recording rec, out RewindPoint rp, out int slotListIndex)
        {
            string reason;
            return TryResolveStashableUnfinishedFlightRewindPoint(
                rec, out rp, out slotListIndex, out reason);
        }

        internal static bool TryResolveStashableUnfinishedFlightRewindPoint(
            Recording rec, out RewindPoint rp, out int slotListIndex, out string reason)
        {
            return UnfinishedFlightClassifier.TryResolveStashableRewindPointForRecording(
                rec, out rp, out slotListIndex, out reason);
        }

        /// <summary>
        /// Cheap, non-logging front gate for row rendering. The full
        /// <see cref="EffectiveState.IsUnfinishedFlight"/> predicate emits
        /// diagnostic Verbose lines for every rejection, so normal table rows
        /// use this shape check before asking the full RP-backed predicate.
        /// The terminal-crash check walks the chain to the tip via
        /// <see cref="EffectiveState.ResolveChainTerminalRecording"/>, matching
        /// the full predicate: merge-time SplitAtSection leaves the chain HEAD
        /// (the only segment with a parentBranchPointId) with terminal=null,
        /// while the TIP carries the actual Destroyed outcome. Without this
        /// walk, the cheap-path rejected chain-head rows and the UI fell
        /// through to the legacy rewind-to-launch button.
        /// </summary>
        internal static bool IsUnfinishedFlightCandidateShape(Recording rec)
        {
            return UnfinishedFlightClassifier.IsUnfinishedFlightCandidateShape(rec);
        }

        internal static bool IsVisibleUnfinishedFlight(Recording rec, out string reason)
        {
            return UnfinishedFlightClassifier.IsVisibleUnfinishedFlight(rec, out reason);
        }

        /// <summary>
        /// Resolves the RewindPoint + child-slot list index for a recording with
        /// a parent branch point. This helper does not apply the unfinished
        /// flight classifier; callers that expose user actions should decide the
        /// eligibility predicate first.
        /// </summary>
        internal static bool TryResolveRewindPointForRecording(
            Recording rec, out RewindPoint rp, out int slotListIndex)
        {
            return UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                rec, out rp, out slotListIndex);
        }

        /// <summary>
        /// Slot-aware precondition for an RP invocation. The global RP
        /// precondition can pass while a specific child slot is disabled
        /// because the split-time vessel could not be correlated into the
        /// quicksave; keep that disabled row from starting an invocation that
        /// would fail only after a scene load.
        /// </summary>
        internal static bool CanInvokeRewindPointSlot(
            RewindPoint rp, int slotListIndex, out string reason)
        {
            if (rp == null)
            {
                reason = "rewind point is null";
                LogRewindSlotCanInvokeDecision(rp, slotListIndex, canInvoke: false, reason: reason, slot: null);
                return false;
            }
            if (rp.ChildSlots == null || slotListIndex < 0 || slotListIndex >= rp.ChildSlots.Count)
            {
                reason = "rewind slot missing";
                LogRewindSlotCanInvokeDecision(rp, slotListIndex, canInvoke: false, reason: reason, slot: null);
                return false;
            }

            var slot = rp.ChildSlots[slotListIndex];
            if (slot == null)
            {
                reason = "rewind slot missing";
                LogRewindSlotCanInvokeDecision(rp, slotListIndex, canInvoke: false, reason: reason, slot: null);
                return false;
            }
            if (slot.Disabled)
            {
                reason = !string.IsNullOrEmpty(slot.DisabledReason)
                    ? "rewind slot disabled: " + slot.DisabledReason
                    : "rewind slot disabled";
                LogRewindSlotCanInvokeDecision(rp, slotListIndex, canInvoke: false, reason: reason, slot: slot);
                return false;
            }

            bool canInvoke = RewindInvoker.CanInvoke(rp, out reason);
            LogRewindSlotCanInvokeDecision(rp, slotListIndex, canInvoke, reason, slot);
            return canInvoke;
        }

        // Per-slot last-emitted decision stateKey. Mirrors the `lastCanInvoke`
        // pattern at line ~2587 above so the on-change gate works reliably from
        // OnGUI per-frame draw loops. ParsekLog.VerboseOnChange has shown
        // production spam from this call site (1389 identical emits in 6s
        // observed in 2026-04-26_1025 playtest); a file-local dictionary is
        // empirically reliable in the same OnGUI hot path elsewhere in this
        // file.
        private static readonly Dictionary<string, string> slotCanInvokeLastStateKey =
            new Dictionary<string, string>();

        private static void LogRewindSlotCanInvokeDecision(
            RewindPoint rp,
            int slotListIndex,
            bool canInvoke,
            string reason,
            ChildSlot slot)
        {
            string rpId = rp == null || string.IsNullOrEmpty(rp.RewindPointId)
                ? "<null>"
                : rp.RewindPointId;
            string normalizedReason = string.IsNullOrEmpty(reason) ? "<none>" : reason;
            string identity = $"CanInvokeSlot|{rpId}|{slotListIndex}";
            bool slotDisabled = slot != null && slot.Disabled;
            string blockedKind = slotDisabled ? "slot-disabled" : "global-blocked";
            string stateKey = canInvoke ? "slot-ok" : blockedKind + "|" + normalizedReason;

            if (slotCanInvokeLastStateKey.TryGetValue(identity, out string lastKey)
                && string.Equals(lastKey, stateKey, StringComparison.Ordinal))
                return;
            slotCanInvokeLastStateKey[identity] = stateKey;

            string slotOrigin = slot == null || string.IsNullOrEmpty(slot.OriginChildRecordingId)
                ? "<none>"
                : slot.OriginChildRecordingId;
            int slotId = slot != null ? slot.SlotIndex : -1;

            ParsekLog.Verbose(
                "RewindUI",
                canInvoke
                    ? $"CanInvokeSlot: slot-ok rp={rpId} slot={slotId} listIndex={slotListIndex} origin={slotOrigin}"
                    : $"CanInvokeSlot: {blockedKind} rp={rpId} slot={slotId} listIndex={slotListIndex} " +
                      $"origin={slotOrigin} reason='{normalizedReason}'");
        }

        internal static int ClearRewindSlotCanInvokeLogState(string rewindPointId)
        {
            if (string.IsNullOrEmpty(rewindPointId))
                return 0;

            string prefix = $"CanInvokeSlot|{rewindPointId}|";
            int removed = 0;
            List<string> toRemove = null;
            foreach (string key in slotCanInvokeLastStateKey.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    if (toRemove == null) toRemove = new List<string>();
                    toRemove.Add(key);
                }
            }
            if (toRemove != null)
            {
                for (int i = 0; i < toRemove.Count; i++)
                    slotCanInvokeLastStateKey.Remove(toRemove[i]);
                removed = toRemove.Count;
            }

            removed += ParsekLog.ClearVerboseOnChangeIdentitiesWithPrefix(
                "RewindUI",
                prefix);
            return removed;
        }

        internal static int ClearAllRewindSlotCanInvokeLogState()
        {
            int removed = slotCanInvokeLastStateKey.Count;
            slotCanInvokeLastStateKey.Clear();
            removed += ParsekLog.ClearVerboseOnChangeIdentitiesWithPrefix(
                "RewindUI",
                "CanInvokeSlot|");
            return removed;
        }

        /// <summary>
        /// Resolves the child-slot list index inside <paramref name="rp"/> whose
        /// <see cref="ChildSlot.OriginChildRecordingId"/> matches
        /// <paramref name="rec"/> (or the forward-walked effective recording
        /// id so that a re-fly that produced another crash still maps back to
        /// its slot).
        /// </summary>
        internal static int ResolveSlotListIndexForRecording(RewindPoint rp, Recording rec)
        {
            return UnfinishedFlightClassifier.ResolveSlotListIndexForRecording(rp, rec);
        }

        /// <summary>
        /// Draws a chain block (header + members). Returns true if the recording list was modified.
        /// </summary>
        private bool DrawChainBlock(string chainId, List<int> members, int depth,
            IReadOnlyList<Recording> committed, double now,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null)
        {
            string chainName = members.Count > 0 ? committed[members[0]].VesselName : null;
            return DrawRecordingBlock(chainId, chainName, members, depth,
                committed, now, chainId, "Chain", supersedes);
        }

        private bool DrawGroupedRecordingBlock(string blockKey, string blockName, List<int> members, int depth,
            IReadOnlyList<Recording> committed, double now,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null)
        {
            return DrawRecordingBlock(blockKey, blockName, members, depth,
                committed, now, null, "Block", supersedes);
        }

        private bool DrawRecordingBlock(string blockId, string blockName, List<int> members, int depth,
            IReadOnlyList<Recording> committed, double now, string chainIdForPopup, string logKind,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null)
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

            float indent = depth * 15f;

            GUILayout.BeginHorizontal();

            int blockEnabledCount = 0;
            for (int m = 0; m < members.Count; m++)
                if (committed[members[m]].PlaybackEnabled) blockEnabledCount++;
            bool blockAllEnabled = members.Count > 0 && blockEnabledCount == members.Count;
            bool blockNewEnabled = GUILayout.Toggle(blockAllEnabled, "", GUILayout.Width(ColW_Enable));
            if (string.IsNullOrEmpty(blockName)) blockName = logKind;
            string logId = !string.IsNullOrEmpty(chainIdForPopup) ? chainIdForPopup : blockName;
            if (blockNewEnabled != blockAllEnabled)
            {
                for (int m = 0; m < members.Count; m++)
                    committed[members[m]].PlaybackEnabled = blockNewEnabled;
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

            if (GUILayout.Button($"{arrow} {blockName} ({members.Count})",
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

            if (showExpandedStats)
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_MaxAlt));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_MaxSpd));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Dist));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Pts));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartPos));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndPos));
            }

            string blockStatusText;
            int blockStatusOrder;
            GetChainStatus(members, committed, now, out blockStatusText, out blockStatusOrder);
            GUIStyle blockStatusStyle = blockStatusOrder == 0 ? statusStyleFuture
                : blockStatusOrder == 1 ? statusStyleActive
                : statusStylePast;
            GUILayout.Label(blockStatusText, blockStatusStyle, GUILayout.Width(ColW_Status));

            // Aggregated blocks expose group assignment but no disband button.
            if (DrawBodyCenteredButton("G", ColW_Group))
            {
                var mousePos = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                if (!string.IsNullOrEmpty(chainIdForPopup))
                    groupPicker.OpenForChain(chainIdForPopup, mousePos);
                else
                    groupPicker.OpenForRecordings(members, mousePos);
                ParsekLog.Verbose("UI", $"Group popup opened for {logKind.ToLowerInvariant()} '{blockName}'");
            }

            int blockLoopCount = 0;
            for (int m = 0; m < members.Count; m++)
                if (committed[members[m]].LoopPlayback) blockLoopCount++;
            bool blockAllLoop = members.Count > 0 && blockLoopCount == members.Count;
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Loop));
            GUILayout.FlexibleSpace();
            bool blockNewLoop = GUILayout.Toggle(blockAllLoop, "");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (blockNewLoop != blockAllLoop)
            {
                for (int m = 0; m < members.Count; m++)
                {
                    committed[members[m]].LoopPlayback = blockNewLoop;
                    ApplyAutoLoopRange(committed[members[m]], blockNewLoop);
                }
                ParsekLog.Info("UI", $"{logKind} '{logId}' loop set to {blockNewLoop} ({members.Count} recordings)");
            }
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Period));
            if (parentUI.InFlightMode) GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Watch));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Rewind));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Hide));

            GUILayout.EndHorizontal();

            if (expanded)
            {
                for (int m = 0; m < members.Count; m++)
                {
                    if (DrawRecordingRow(members[m], committed, now, (depth + 1) * 15f, supersedes))
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

            if (RecordingStore.IsPermanentRootGroup(oldName))
            {
                ParsekLog.Warn("UI", $"Group rename blocked for permanent root group '{oldName}'");
                return;
            }

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
            if (RecordingStore.IsPermanentGroup(groupName))
            {
                ParsekLog.Warn("UI", $"Disband blocked for permanent group '{groupName}'");
                return;
            }

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

        /// <summary>
        /// Deletes a ghost-only recording by index. No confirmation dialog — ghost-only
        /// recordings are low-commitment.
        /// </summary>
        private void DeleteGhostOnlyRecording(int index)
        {
            // [ERS-exempt] reason: delete operates by index into the raw
            // committed list. See TODO(phase 6+) on DrawRecordingsWindow.
            if (!CanOfferGhostOnlyDelete(parentUI.Mode))
            {
                ParsekLog.Warn("UI",
                    $"DeleteGhostOnlyRecording ignored in {parentUI.Mode} mode: index={index}");
                return;
            }
            var committed = RecordingStore.CommittedRecordings;
            if (index < 0 || index >= committed.Count)
            {
                ParsekLog.Warn("UI", $"DeleteGhostOnlyRecording: index {index} out of range");
                return;
            }

            var rec = committed[index];
            if (!rec.IsGhostOnly)
            {
                ParsekLog.Warn("UI", $"DeleteGhostOnlyRecording: recording at index {index} is not ghost-only");
                return;
            }

            var flight = parentUI.Flight;
            if (flight != null && parentUI.InFlightMode)
            {
                // Flight scene: use ParsekFlight.DeleteRecording for ghost cleanup
                flight.DeleteGhostOnlyRecording(index);
            }
            else
            {
                // KSC scene: direct store deletion
                RecordingStore.DeleteRecordingFull(index);
            }

            InvalidateSort();
            ParsekLog.Info("UI",
                $"Deleted ghost-only recording \"{rec.VesselName}\" (id={rec.RecordingId})");
        }

        internal static bool CanOfferGhostOnlyDelete(UIMode mode)
        {
            return mode != UIMode.TrackingStation;
        }

        private void DrawSortableHeader(string label, SortColumn col, float width, bool expand = false)
        {
            parentUI.DrawSortableHeaderCore(label, col, ref sortColumn, ref sortAscending, width, expand, () =>
            {
                InvalidateSort();
                ParsekLog.Verbose("UI", $"Sort column changed: {sortColumn} {(sortAscending ? "asc" : "desc")}");
            }, ColHeaderHeight);
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

        internal static string GetPhaseStyleKey(Recording rec)
        {
            if (rec == null) return null;

            if (RecordingStore.ShouldSuppressEvaBoundaryPhaseLabel(rec)
                && rec.TrackSections != null)
            {
                for (int i = rec.TrackSections.Count - 1; i >= 0; i--)
                {
                    switch (RecordingOptimizer.SplitEnvironmentClass(rec.TrackSections[i].environment))
                    {
                        case 0: return "atmo";
                        case 2: return "surface";
                        case 3: return "approach";
                        case 1: return "exo";
                    }
                }
            }

            return rec.SegmentPhase;
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
            => RecordingsTableFormatters.FormatAltitude(meters);

        internal static string FormatSpeed(double mps)
            => RecordingsTableFormatters.FormatSpeed(mps);

        internal static string FormatDistance(double meters)
            => RecordingsTableFormatters.FormatDistance(meters);

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
            => RecordingsTableFormatters.FormatStartPosition(rec, parentVesselName);

        /// <summary>
        /// Formats the recording end position for the expanded stats column.
        /// Matches timeline style: "Orbiting {body}", "{biome}, {body}", "Boarded {vessel}".
        /// Body fallback priority for terminal recordings: TerminalOrbitBody → StartBodyName.
        /// Body fallback priority for mid-segments: SegmentBodyName → last point body → StartBodyName.
        /// </summary>
        internal static string FormatEndPosition(Recording rec, string parentVesselName = null)
            => RecordingsTableFormatters.FormatEndPosition(rec, parentVesselName);

        /// <summary>
        /// Formats a resource manifest for tooltip display.
        /// If both start and end: "Resources:\n  LiquidFuel: 3600.0 → 200.0 (-3400.0)"
        /// If start only: "Resources at start:\n  LiquidFuel: 3600.0 / 3600.0"
        /// If both null: returns null (no section shown).
        /// </summary>
        internal static string FormatResourceManifest(
            Dictionary<string, ResourceAmount> start,
            Dictionary<string, ResourceAmount> end)
            => RecordingsTableFormatters.FormatResourceManifest(start, end);

        /// <summary>
        /// Formats an inventory manifest for tooltip display.
        /// If both start and end: "Inventory:\n  solarPanels5: 4 -> 0 (-4)"
        /// If start only: "Inventory at start:\n  solarPanels5: 4"
        /// If both null: returns null (no section shown).
        /// </summary>
        internal static string FormatInventoryManifest(
            Dictionary<string, InventoryItem> start,
            Dictionary<string, InventoryItem> end)
            => RecordingsTableFormatters.FormatInventoryManifest(start, end);

        /// <summary>
        /// Formats a crew manifest for tooltip display.
        /// If both start and end: "Crew:\n  Pilot: 1 → 1 (+0)\n  Engineer: 2 → 0 (-2)"
        /// If start only: "Crew at start:\n  Pilot: 1\n  Engineer: 2"
        /// If both null: returns null (no section shown).
        /// </summary>
        internal static string FormatCrewManifest(
            Dictionary<string, int> start,
            Dictionary<string, int> end)
            => RecordingsTableFormatters.FormatCrewManifest(start, end);

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
        /// Bug #382: builds a stable fingerprint of the eligible descendant set
        /// for the group W button transition log. The fingerprint is the sorted,
        /// comma-joined RecordingIds of eligible descendants, so the bug #279
        /// transition log only fires when the eligibility set itself changes
        /// (vessel comes into range, goes out of range, etc.) — cursor advances
        /// within a steady set do not retrigger the log.
        /// </summary>
        private static string BuildEligibleSetFingerprint(
            HashSet<int> descendants, IReadOnlyList<Recording> committed, Func<int, bool> isEligible)
        {
            if (descendants == null || committed == null || isEligible == null) return string.Empty;
            var ids = new List<string>(descendants.Count);
            foreach (int idx in descendants)
            {
                if (idx < 0 || idx >= committed.Count) continue;
                var r = committed[idx];
                if (r == null || string.IsNullOrEmpty(r.RecordingId)) continue;
                if (!isEligible(idx)) continue;
                ids.Add(r.RecordingId);
            }
            ids.Sort(StringComparer.Ordinal);
            return string.Join(",", ids);
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

        private static string GetGroupDisplayIdentity(Recording rec)
        {
            if (rec == null) return null;

            if (!string.IsNullOrEmpty(rec.TreeId) && rec.VesselPersistentId != 0)
                return $"treevessel:{rec.TreeId}:{rec.VesselPersistentId}";
            if (!string.IsNullOrEmpty(rec.ChainId))
                return $"chain:{rec.ChainId}";
            return null;
        }

        private static void AddDisplayBlockMember(List<int> members, int ri)
        {
            if (!members.Contains(ri))
                members.Add(ri);
        }

        private static void AddDisplayBlockMembers(List<int> members, List<int> additions)
        {
            if (additions == null) return;
            for (int i = 0; i < additions.Count; i++)
                AddDisplayBlockMember(members, additions[i]);
        }

        private static string ResolveGroupDisplayBlockName(List<int> members, IReadOnlyList<Recording> committed)
        {
            for (int i = 0; i < members.Count; i++)
            {
                int ri = members[i];
                if (ri < 0 || ri >= committed.Count) continue;
                string vesselName = committed[ri].VesselName;
                if (!string.IsNullOrEmpty(vesselName))
                    return vesselName;
            }

            return "Recording";
        }

        /// <summary>
        /// Pure helper: returns a copy of <paramref name="directMembers"/> with
        /// every recording for which
        /// <see cref="EffectiveState.IsUnfinishedFlight(Recording)"/> is true
        /// stripped out. Used by <see cref="DrawGroupTree"/> when a tree
        /// already nests an Unfinished Flights virtual subgroup so the same
        /// recording does not render in both places.
        ///
        /// <para>
        /// Out-of-range indices are passed through unchanged (defensive — any
        /// future caller error stays visible at the row layer instead of
        /// being silently swallowed by a UF-filter pre-pass). Returns
        /// <paramref name="directMembers"/> unchanged when no filtering is
        /// needed (avoids an allocation in the common no-UF case). Emits one
        /// rate-limited Verbose log line per `<groupName>` flush so the trim
        /// is auditable without flooding the log.
        /// </para>
        /// </summary>
        internal static List<int> FilterUnfinishedFlightRowsForRegularTree(
            IList<int> directMembers,
            IReadOnlyList<Recording> committed,
            string groupName)
        {
            if (directMembers == null) return null;
            if (committed == null) return directMembers as List<int> ?? new List<int>(directMembers);

            int filtered = 0;
            var trimmed = new List<int>(directMembers.Count);
            for (int i = 0; i < directMembers.Count; i++)
            {
                int ri = directMembers[i];
                if (ri < 0 || ri >= committed.Count)
                {
                    trimmed.Add(ri);
                    continue;
                }
                if (EffectiveState.IsUnfinishedFlight(committed[ri]))
                {
                    filtered++;
                    continue;
                }
                trimmed.Add(ri);
            }

            if (filtered == 0)
                return directMembers as List<int> ?? new List<int>(directMembers);

            ParsekLog.VerboseRateLimited("UnfinishedFlights",
                "uf-filter-out-of-tree-row-" + (groupName ?? "<none>"),
                $"DrawGroupTree: filtered {filtered} UF row(s) from regular tree '{groupName ?? "<none>"}' " +
                "(rendered in nested Unfinished Flights subgroup)");
            return trimmed;
        }

        internal static List<GroupDisplayBlock> BuildGroupDisplayBlocks(
            string groupName,
            List<int> directMembers,
            IReadOnlyList<Recording> committed,
            Dictionary<string, List<int>> chainToRecs)
        {
            var blocks = new List<GroupDisplayBlock>();
            if (directMembers == null || committed == null)
                return blocks;

            var membersByKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < directMembers.Count; i++)
            {
                int ri = directMembers[i];
                if (ri < 0 || ri >= committed.Count)
                    continue;

                var rec = committed[ri];
                string identity = GetGroupDisplayIdentity(rec);
                if (string.IsNullOrEmpty(identity))
                    continue;

                string blockKey = (groupName ?? "") + "::" + identity;
                if (!membersByKey.TryGetValue(blockKey, out List<int> members))
                {
                    members = new List<int>();
                    membersByKey[blockKey] = members;
                }

                // Preserve existing grouped-chain visibility: if one row in a group
                // belongs to a chain, render the whole chain inside that block.
                if (!string.IsNullOrEmpty(rec.ChainId)
                    && chainToRecs != null
                    && chainToRecs.TryGetValue(rec.ChainId, out List<int> fullChain))
                {
                    AddDisplayBlockMembers(members, fullChain);
                }

                AddDisplayBlockMember(members, ri);
            }

            var emitted = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < directMembers.Count; i++)
            {
                int ri = directMembers[i];
                if (ri < 0 || ri >= committed.Count)
                    continue;

                var rec = committed[ri];
                string identity = GetGroupDisplayIdentity(rec);
                string blockKey = !string.IsNullOrEmpty(identity)
                    ? (groupName ?? "") + "::" + identity
                    : null;

                if (!string.IsNullOrEmpty(blockKey)
                    && membersByKey.TryGetValue(blockKey, out List<int> members)
                    && members.Count > 1)
                {
                    if (emitted.Add(blockKey))
                    {
                        blocks.Add(new GroupDisplayBlock
                        {
                            Key = blockKey,
                            DisplayName = ResolveGroupDisplayBlockName(members, committed),
                            Members = new List<int>(members)
                        });
                    }
                    continue;
                }

                blocks.Add(new GroupDisplayBlock
                {
                    Key = (groupName ?? "") + "::rec:" + ri.ToString(CultureInfo.InvariantCulture),
                    DisplayName = rec.VesselName,
                    Members = new List<int> { ri }
                });
            }

            return blocks;
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

        internal static int CompareRootItemsForSort(
            bool aIsGroup, string aGroupName, string aSortName, double aSortKey,
            bool bIsGroup, string bGroupName, string bSortName, double bSortKey,
            SortColumn column, bool ascending)
        {
            int pinnedCmp = ComparePinnedRootGroups(aIsGroup, aGroupName, bIsGroup, bGroupName);
            if (pinnedCmp != 0)
                return pinnedCmp;

            int cmp;
            if (column == SortColumn.Name || column == SortColumn.Phase || column == SortColumn.LaunchSite)
                cmp = string.Compare(aSortName, bSortName, StringComparison.OrdinalIgnoreCase);
            else
                cmp = aSortKey.CompareTo(bSortKey);
            return ascending ? cmp : -cmp;
        }

        private static int ComparePinnedRootGroups(
            bool aIsGroup, string aGroupName,
            bool bIsGroup, string bGroupName)
        {
            bool aPinned = aIsGroup && RecordingStore.IsPermanentRootGroup(aGroupName);
            bool bPinned = bIsGroup && RecordingStore.IsPermanentRootGroup(bGroupName);
            if (aPinned == bPinned)
                return 0;
            return aPinned ? -1 : 1;
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
                            $", ghost: {DiagnosticsComputation.FormatBytes(storage.ghostSnapshotBytes)}" +
                            $", readable mirrors: {DiagnosticsComputation.FormatBytes(storage.readableMirrorBytes)})";
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

        /// <summary>
        /// Clears any active loop-period text-edit focus. Called by TimelineWindowUI
        /// when the L button toggles loop off, so a stale edit field doesn't linger.
        /// </summary>
        internal void ClearLoopPeriodFocus()
        {
            loopPeriodFocusedRi = -1;
        }

        // --- Loop period cell ---

        private void DrawLoopPeriodCell(Recording rec, int ri)
        {
            // All states use same [value area][unit button] layout. val + Space(4) + unit fills
            // ColW_Period - BodyCellButtonLeftInset. The wrapping BeginHorizontal at the call
            // site provides the Space(BodyCellButtonLeftInset) on the left so val+unit start
            // 10 px into the Period cell.
            const float unitBtnW = 40f;
            float valueBtnW = ColW_Period - unitBtnW - 4f - BodyCellButtonLeftInset;

            if (!rec.LoopPlayback)
            {
                // Disabled: gray out the same two-control layout
                GUI.enabled = false;
                string disabledText;
                if (rec.LoopTimeUnit == LoopTimeUnit.Auto)
                {
                    var settings = ParsekSettings.Current;
                    double gv = settings != null
                        ? ParsekUI.ConvertFromSeconds(settings.autoLoopIntervalSeconds, settings.AutoLoopDisplayUnit)
                        : LoopTiming.DefaultLoopIntervalSeconds;
                    var gu = settings != null ? settings.AutoLoopDisplayUnit : LoopTimeUnit.Sec;
                    disabledText = ParsekUI.FormatLoopValue(gv, gu) + UnitSuffix(gu);
                }
                else
                {
                    disabledText = ParsekUI.FormatLoopValue(ParsekUI.ConvertFromSeconds(rec.LoopIntervalSeconds, rec.LoopTimeUnit), rec.LoopTimeUnit);
                }
                GUILayout.TextField(disabledText, bodyCellTextFieldFlush, GUILayout.Width(valueBtnW));
                GUILayout.Space(4f);
                GUILayout.Button(ParsekUI.UnitLabel(rec.LoopTimeUnit), bodyCellButtonFlush, GUILayout.Width(unitBtnW));
                GUI.enabled = true;
                return;
            }

            if (rec.LoopTimeUnit == LoopTimeUnit.Auto)
            {
                // Auto mode: disabled text field showing global value + "auto" unit button
                var settings = ParsekSettings.Current;
                double globalVal = settings != null
                    ? ParsekUI.ConvertFromSeconds(settings.autoLoopIntervalSeconds, settings.AutoLoopDisplayUnit)
                    : LoopTiming.DefaultLoopIntervalSeconds;
                GUI.enabled = false;
                var globalDisplayUnit = settings != null ? settings.AutoLoopDisplayUnit : LoopTimeUnit.Sec;
                GUILayout.TextField(ParsekUI.FormatLoopValue(globalVal, globalDisplayUnit) + UnitSuffix(globalDisplayUnit), bodyCellTextFieldFlush, GUILayout.Width(valueBtnW));
                GUI.enabled = true;
            }
            else
            {
                // Manual mode: editable text field with edit buffer.
                double loopDuration = GhostPlaybackEngine.EffectiveLoopDuration(rec);
                bool displayClamped;
                double displayedSeconds = ComputeDisplayedLoopPeriod(
                    rec.LoopIntervalSeconds, loopDuration,
                    GhostPlayback.MaxOverlapGhostsPerRecording,
                    out displayClamped);
                string displayText = FormatLoopPeriodDisplayText(
                    displayedSeconds, rec.LoopTimeUnit, displayClamped);
                string clampTooltip = displayClamped
                    ? BuildLoopPeriodClampTooltip(
                        rec.LoopIntervalSeconds, displayedSeconds, loopDuration,
                        GhostPlayback.MaxOverlapGhostsPerRecording)
                    : string.Empty;
                if (loopPeriodFocusedRi != ri)
                {
                    // Not editing: show runtime-effective value, but seed the edit buffer
                    // from the stored raw value when the field gains focus.
                    string controlName = "LoopPeriod_" + ri;
                    GUI.SetNextControlName(controlName);
                    Color prevContentColor = GUI.contentColor;
                    if (displayClamped)
                        GUI.contentColor = LoopPeriodClampColor;
                    try
                    {
                        GUILayout.TextField(displayText, bodyCellTextFieldFlush, GUILayout.Width(valueBtnW));
                    }
                    finally
                    {
                        GUI.contentColor = prevContentColor;
                    }
                    Rect valueRect = GUILayoutUtility.GetLastRect();
                    if (displayClamped && valueRect.Contains(Event.current.mousePosition))
                        recordingsWindowTooltipText = clampTooltip;
                    if (GUI.GetNameOfFocusedControl() == controlName)
                    {
                        loopPeriodEditText = FormatLoopPeriodEditStartText(
                            rec.LoopIntervalSeconds, rec.LoopTimeUnit);
                        loopPeriodFocusedRi = ri;
                        loopPeriodEditRect = valueRect;
                        ParsekLog.Verbose("UI",
                            $"Recording '{rec.VesselName}' loop period edit started: " +
                            $"value='{loopPeriodEditText}' unit={ParsekUI.UnitLabel(rec.LoopTimeUnit)}");
                    }
                }
                else
                {
                    // Enter key -> commit (check before TextField, which consumes KeyDown)
                    bool submitPeriod = Event.current.type == EventType.KeyDown &&
                        (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

                    // Editing: use buffer, track rect for click-outside
                    GUI.SetNextControlName("LoopPeriod_" + ri);
                    string newText = GUILayout.TextField(loopPeriodEditText, bodyCellTextFieldFlush, GUILayout.Width(valueBtnW));
                    loopPeriodEditRect = GUILayoutUtility.GetLastRect();
                    if (newText != loopPeriodEditText)
                        loopPeriodEditText = newText;

                    if (submitPeriod)
                    {
                        // [ERS-exempt] reason: loopPeriodFocusedRi is an index
                        // into the raw committed list used by the recordings
                        // table. See TODO(phase 6+) on DrawRecordingsWindow.
                        CommitLoopPeriodEdit(RecordingStore.CommittedRecordings);
                        Event.current.Use();
                    }
                }
            }

            // Unit cycling button (shared by both auto and manual modes)
            GUILayout.Space(4f);
            if (GUILayout.Button(ParsekUI.UnitLabel(rec.LoopTimeUnit), bodyCellButtonFlush, GUILayout.Width(unitBtnW)))
            {
                var newUnit = CycleRecordingUnit(rec.LoopTimeUnit);
                rec.LoopTimeUnit = newUnit;
                GUIUtility.keyboardControl = 0;
                loopPeriodFocusedRi = -1;
                ParsekLog.Info("UI",
                    $"Recording '{rec.VesselName}' loop unit changed to {newUnit}");
            }
        }

        internal static double ComputeDisplayedLoopPeriod(
            double storedSeconds, double loopDurationSeconds, int cap, out bool clamped)
        {
            double effectiveSeconds = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                storedSeconds, loopDurationSeconds, cap);
            clamped = double.IsNaN(storedSeconds)
                || double.IsInfinity(storedSeconds)
                || Math.Abs(effectiveSeconds - storedSeconds) > 1e-6;
            return effectiveSeconds;
        }

        internal static string FormatLoopPeriodDisplayText(
            double displayedSeconds, LoopTimeUnit unit, bool preserveSecondResolution)
        {
            double displayValue = ParsekUI.ConvertFromSeconds(displayedSeconds, unit);
            if (!preserveSecondResolution)
                return ParsekUI.FormatLoopValue(displayValue, unit);

            return displayValue.ToString("0.######", CultureInfo.InvariantCulture);
        }

        internal static string FormatLoopPeriodEditStartText(
            double storedSeconds, LoopTimeUnit unit)
        {
            double displayValue = ParsekUI.ConvertFromSeconds(storedSeconds, unit);
            if (double.IsNaN(displayValue) || double.IsInfinity(displayValue))
                displayValue = ParsekUI.ConvertFromSeconds(LoopTiming.MinCycleDuration, unit);
            if (unit == LoopTimeUnit.Min || unit == LoopTimeUnit.Hour)
                return displayValue.ToString("G17", CultureInfo.InvariantCulture);

            return ParsekUI.FormatLoopValue(displayValue, unit);
        }

        internal static string BuildLoopPeriodClampTooltip(
            double storedSeconds, double effectiveSeconds, double loopDurationSeconds, int cap)
        {
            if (double.IsNaN(storedSeconds) || double.IsInfinity(storedSeconds)
                || Math.Abs(effectiveSeconds - storedSeconds) <= 1e-6)
            {
                if (!(double.IsNaN(storedSeconds) || double.IsInfinity(storedSeconds)))
                    return string.Empty;
            }

            bool invalidStored = double.IsNaN(storedSeconds) || double.IsInfinity(storedSeconds);
            double minAdjustedSeconds = invalidStored
                ? LoopTiming.MinCycleDuration
                : Math.Max(storedSeconds, LoopTiming.MinCycleDuration);
            bool minAdjusted = invalidStored
                || storedSeconds < LoopTiming.MinCycleDuration - 1e-6;
            bool capAdjusted = loopDurationSeconds > 0.0 && cap > 0
                && effectiveSeconds - minAdjustedSeconds > 1e-6;

            string effectiveText = effectiveSeconds.ToString("0.######", CultureInfo.InvariantCulture);
            string requestedText = invalidStored
                ? "invalid"
                : storedSeconds.ToString("0.######", CultureInfo.InvariantCulture);
            string durationText = loopDurationSeconds.ToString("0.######", CultureInfo.InvariantCulture);
            string minText = LoopTiming.MinCycleDuration.ToString("0.######", CultureInfo.InvariantCulture);

            if (capAdjusted && minAdjusted)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Runtime cadence clamped to {0}s to keep concurrent cycles <= {1} (requested: {2}s, minimum period: {3}s, duration: {4}s).",
                    effectiveText, cap, requestedText, minText, durationText);
            }

            if (capAdjusted)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Runtime cadence clamped to {0}s to keep concurrent cycles <= {1} (requested: {2}s, duration: {3}s).",
                    effectiveText, cap, requestedText, durationText);
            }

            if (minAdjusted)
            {
                if (invalidStored)
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "Runtime cadence repaired to {0}s from an invalid stored value (minimum period: {1}s).",
                        effectiveText, minText);
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Runtime cadence raised to {0}s because the minimum period is {1}s (requested: {2}s).",
                    effectiveText, minText, requestedText);
            }

            return string.Empty;
        }

        private void CommitLoopPeriodEdit(IReadOnlyList<Recording> committed)
        {
            if (loopPeriodFocusedRi < 0 || loopPeriodFocusedRi >= committed.Count) { loopPeriodFocusedRi = -1; return; }
            var rec = committed[loopPeriodFocusedRi];
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            double parsed;
            if (ParsekUI.TryParseLoopInput(loopPeriodEditText, rec.LoopTimeUnit, out parsed))
            {
                double newSeconds = ParsekUI.ConvertToSeconds(parsed, rec.LoopTimeUnit);
                // #381: period is launch-to-launch; negatives are rejected outright.
                if (newSeconds < 0)
                {
                    ParsekLog.Warn("UI",
                        $"Recording '{rec.VesselName}' loop period edit rejected: " +
                        $"negative value {newSeconds.ToString("F1", ic)}s " +
                        "(period must be >= 0 under launch-to-launch semantics #381)");
                }
                else
                {
                    // Defensively clamp below-minimum to MinCycleDuration.
                    if (newSeconds < LoopTiming.MinCycleDuration)
                    {
                        ParsekLog.Info("UI",
                            $"Recording '{rec.VesselName}' loop period clamped from " +
                            $"{newSeconds.ToString("F1", ic)}s to " +
                            $"{LoopTiming.MinCycleDuration.ToString("F1", ic)}s " +
                            "(MinCycleDuration)");
                        newSeconds = LoopTiming.MinCycleDuration;
                    }
                    rec.LoopIntervalSeconds = newSeconds;
                    ParsekLog.Info("UI",
                        $"Recording '{rec.VesselName}' loop period updated to " +
                        rec.LoopIntervalSeconds.ToString("F1", ic) +
                        $"s (display: {ParsekUI.FormatLoopValue(ParsekUI.ConvertFromSeconds(newSeconds, rec.LoopTimeUnit), rec.LoopTimeUnit)} " +
                        $"{ParsekUI.UnitLabel(rec.LoopTimeUnit)})");
                }
            }
            else
            {
                ParsekLog.Warn("UI",
                    $"Recording '{rec.VesselName}' loop period edit rejected: " +
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

        private void EnsureWindowTooltipStyles()
        {
            if (zeroHeightLabelStyle == null)
            {
                zeroHeightLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    fixedHeight = 0f,
                    stretchHeight = false,
                    wordWrap = false
                };
                zeroHeightLabelStyle.margin = new RectOffset(0, 0, 0, 0);
                zeroHeightLabelStyle.padding = new RectOffset(0, 0, 0, 0);
            }

            if (wrappedTooltipStyle == null)
            {
                wrappedTooltipStyle = new GUIStyle(GUI.skin.box)
                {
                    wordWrap = true,
                    alignment = TextAnchor.UpperLeft
                };
            }
        }

        private void DrawRecordingsWindowTooltip()
        {
            EnsureWindowTooltipStyles();

            string tooltip = !string.IsNullOrEmpty(recordingsWindowTooltipText)
                ? recordingsWindowTooltipText
                : (GUI.tooltip ?? string.Empty);
            GUILayout.Space(tooltip.Length > 0 ? SpacingSmall : 0f);
            GUILayout.Label(
                tooltip.Length > 0 ? tooltip : string.Empty,
                tooltip.Length > 0 ? wrappedTooltipStyle : zeroHeightLabelStyle,
                tooltip.Length > 0 ? GUILayout.ExpandWidth(true) : GUILayout.Height(0f));
        }

        private void EnsureStatusStyles()
        {
            if (statusStyleFuture != null) return;

            var statusPadding = new RectOffset(BodyCellTextIndent, 0, 0, 0);
            statusStyleFuture = new GUIStyle(GUI.skin.label) { padding = statusPadding };
            statusStyleFuture.normal.textColor = Color.white;

            statusStyleActive = new GUIStyle(GUI.skin.label) { padding = statusPadding };
            statusStyleActive.normal.textColor = Color.green;

            statusStylePast = new GUIStyle(GUI.skin.label) { padding = statusPadding };
            statusStylePast.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
        }

        /// <summary>
        /// Builds the group tree data structures used to render the recordings tree.
        /// No IMGUI calls, but not pure: normalizes permanent root groups and
        /// falls back to the active scenario supersede list when none is passed.
        /// </summary>
        internal static void BuildGroupTreeData(
            IReadOnlyList<Recording> committed, int[] sortedIndices,
            List<string> KnownEmptyGroups,
            out Dictionary<string, List<int>> grpToRecs,
            out Dictionary<string, List<int>> chainToRecs,
            out Dictionary<string, List<string>> grpChildren,
            out List<string> rootGrps,
            out HashSet<string> rootChainIds,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null)
        {
            GroupHierarchyStore.EnsurePermanentRootGroupsAreRoot();

            // group name -> list of recording indices directly in that group
            grpToRecs = new Dictionary<string, List<int>>();
            // chainId -> list of recording indices
            chainToRecs = new Dictionary<string, List<int>>();
            supersedes = supersedes ?? CurrentRecordingSupersedesForDisplay();

            for (int row = 0; row < sortedIndices.Length; row++)
            {
                int ri = sortedIndices[row];
                var rec = committed[ri];
                if (IsSupersededForDisplay(rec, supersedes))
                    continue;

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
            {
                if (!RecordingStore.IsPermanentRootGroup(KnownEmptyGroups[i]))
                    allGrpNames.Add(KnownEmptyGroups[i]);
            }

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

        private static IReadOnlyList<RecordingSupersedeRelation> CurrentRecordingSupersedesForDisplay()
        {
            var scenario = ParsekScenario.Instance;
            return object.ReferenceEquals(null, scenario)
                ? null
                : scenario.RecordingSupersedes;
        }

        private static bool IsSupersededForDisplay(
            Recording rec,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            return EffectiveState.IsSupersededByRelation(rec, supersedes);
        }
    }
}
