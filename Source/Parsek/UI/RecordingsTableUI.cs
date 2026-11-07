using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Recordings table window extracted from ParsekUI.
    /// Manages the recordings list, sorting, group tree, rename, loop period editing, and all
    /// recordings-window-specific UI state.
    /// </summary>
    internal partial class RecordingsTableUI
    {
        private readonly ParsekUI parentUI;

        // Recordings window
        private bool showRecordingsWindow;
        private Rect recordingsWindowRect;
        /// <summary>
        /// The live window rect, readable and writable from outside the draw pass.
        /// <para>Two consumers: an in-game test that needs the measured rect, and the
        /// automation-only <c>UiAction op=rect</c> seam op, which places and enlarges a
        /// window so one capture shows more rows than the default size fits. Writing it is
        /// safe before the first draw as well as after: <c>DrawIfOpen</c> only seeds its
        /// default when <c>width &lt; 1</c>, so a commanded rect suppresses the seed rather
        /// than being overwritten by it.</para>
        /// </summary>
        internal Rect WindowRectForTesting
        {
            get { return recordingsWindowRect; }
            set { recordingsWindowRect = value; }
        }

        private Vector2 recordingsScrollPos;
        // Horizontal scroll for both tabs when the window is capped below its natural
        // width by a narrow screen (WideWindowLayout). One state for the window: the two
        // tabs share its width, and only one of them draws per frame.
        private readonly WideWindowScroll wideScroll =
            new WideWindowScroll("Missions window", MinWindowWidth);
        /// <summary>The window's horizontal-scroll state. Read by the Missions tab's draw
        /// (which lives in MissionsWindowUI) and by the automation-only <c>op=state
        /// key=scrollX</c> seam key.</summary>
        internal WideWindowScroll WideScroll => wideScroll;
        private bool isResizingRecordingsWindow;
        private bool recordingsWindowHasInputLock;
        private const string RecordingsInputLockId = "Parsek_RecordingsWindow";
        /// <summary>
        /// The key this window's IMGUI window id is hashed from. Named once and used at
        /// BOTH the <c>ClickThruBlocker.GUILayoutWindow</c> call below and
        /// <c>UiWindowHandle.GetWindowId</c> (wired by
        /// <c>ParsekTestCommandAddon.ResolveWindowHandle</c>), which the <c>UiAction op=find</c>
        /// seam uses to scope a captured GUI tree to THIS window's subtree. Two copies of
        /// the literal would let the seam search the wrong window's children and answer a
        /// plausible rect for a control in another window.
        /// </summary>
        internal const string WindowIdKey = "ParsekRecordings";

        private const float ResizeHandleSize = 16f;
        // Minimum resize width = the window's default width, so the window can never be
        // dragged narrow enough to clip either tab or push the shared help strip past its
        // one-line budget. Shared by both tabs (one window, one resize clamp in DrawIfOpen ->
        // HandleResizeDrag). C# resolves the forward const reference regardless of textual
        // order.
        internal const float MinWindowWidth = DefaultWindowWidth;
        internal const float MinWindowHeight = 150f;

        // Column widths — shared between header and body for alignment
        private const float ColW_Enable = 20f;
        // Wide enough for a two-body phase label on one line ("Kerbin -> Mun exo" measured
        // ~107 px of text in the 2026-09-26 census, plus the cell's 4 + 4 padding).
        private const float ColW_Phase = 120f;
        private const float ColW_Index = 30f;
        private const float ColW_Launch = 110f;
        // The longest FormatDuration output ("394d 4h", "10m 57s") is about 55 px of text.
        private const float ColW_Dur = 70f;
        private const float ColW_Status = 120f;
        private const float ColW_Loop = 60f;
        private const float ColW_Watch = 50f;
        private const float ColW_Rewind = 60f;
        private const float ColW_ReFly = 90f;

        // The width and left inset the Re-Fly cell (Fly / Seal, Stash / Seal) draws at. The
        // Recordings tab always uses ColW_ReFly / BodyCellButtonLeftInset; the Missions tab's
        // Interact column overrides both for the span of ONE DrawReFlyColumnCell call (the
        // width-taking overload below), so its button pair spans the column's single-button
        // width with the edges every other Interact button has.
        private float reFlyCellWidth = ColW_ReFly;
        private float reFlyCellLeftInset = BodyCellButtonLeftInset;
        private const float ColW_Hide = 80f;
        // The width the window opens at, and its minimum. Held by the WINDOW, not by the
        // Recordings columns: the window is shared with the Missions tab (its default tab,
        // whose header bar needs this width), and the one-line help strip both tabs share is
        // budgeted at 189 characters for exactly this width (TooltipEchoBudgetTests,
        // DisabledReasonWordingTests). Wider fixed columns come out of the expanding Name
        // column, never out of a wider window.
        internal const float DefaultWindowWidth = 1355f;
        private const string RewindActionLabel = "R";
        private const string FastForwardActionLabel = "FF";

        // Header cell height — the cells containing a label + select-all toggle
        // (Loop, Archive) naturally measure taller than a single bold label in
        // colHdr. Forcing every header cell to this height keeps the row visually
        // uniform; +10px over the single-label default balances with the toggle
        // cells without over-inflating them.
        private const float ColHeaderHeight = 32f;
        // "Launch Pad" measured ~69 px of text; 80 holds it with the cell padding.
        private const float ColW_Site = 80f;
        private const float ColW_Group = 60f;
        // The header's merged [select-all toggle + "#"] cell: the two body cells it sits over
        // plus the 8 px of margin budget between them (see DrawRecordingsTableHeader). Named
        // outside the ColW_ family on purpose: it is a HEADER-only container around the two
        // real columns, whose own ColW_Enable / ColW_Index widths the header's inner toggle
        // and "#" button draw, so the header and every row declare one column sequence.
        private const float HeaderMergedEnableIndexWidth = ColW_Enable + ColW_Index + 8f;

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

        // Recording rename (deferred to next frame). Captured by RecordingId, not row
        // index: the committed list can be restructured between two OnGUI frames (a
        // chain-segment commit on EVA exit runs the optimizer), and an index captured
        // before that would rename the neighbour.
        private string renamingRecordingId;
        private string renamingRecordingText = "";
        private bool renamingRecordingFocused;
        private Rect activeRenameRect;

        // Double-click detection (same by-id rule as the rename capture)
        private string lastClickedRecordingId;
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

        // Two-tab bar (Missions | Recordings). Missions is the primary identity of this
        // window: it renders the higher mission abstraction over the same recordings
        // (delegated to MissionsWindowUI) and is both the first tab and the default
        // selection. The Recordings tab is the raw table. Switching tabs swaps the content
        // inside this one window. Transient (not persisted), matching the Kerbals / Career
        // State tab idiom, so no player carries a stored index across this ordering.
        // internal (not private) so unit tests can pin the order and the default.
        internal const int TabMissions = 0;
        internal const int TabRecordings = 1;
        private int selectedTab = TabMissions;
        // GUIContent (not bare strings) so each tab explains itself in the bottom help
        // strip on hover; static readonly so the toolbar allocates nothing per draw pass.
        internal static readonly GUIContent[] TabLabels = new[]
        {
            new GUIContent("Missions",
                "Your flights grouped as whole missions - the higher-level view."),
            new GUIContent("Recordings",
                "The raw table of every recorded vessel, one row each.")
        };

        // Read-only test seam for the transient tab selection (section 4.1/4.1a guards).
        // Settable so an in-game test can restore the tab it moved (mirrors
        // KerbalsWindowUI's accessor); `selectedTab` is transient either way.
        internal int SelectedTabForTesting
        {
            get { return selectedTab; }
            set { selectedTab = value; }
        }

        // The scroll ScrollToRecording schedules, and the expansion set it grows. Both are
        // consumed only inside the row draw (an IMGUI callback with no headless seam), so they
        // are the only observable outcome of the method beyond the window/tab writes - and
        // without them the API's coverage would stop at its first three lines.
        internal string PendingScrollToRecordingIdForTesting => pendingScrollToRecordingId;
        internal IReadOnlyCollection<string> ExpandedGroupsForTesting => expandedGroups;

        // ----- GUI-census seam accessors (UiAction op=picker) -----
        //
        // The picker's two openers are wrapped HERE rather than called from the seam,
        // because the recording one takes an INDEX into the committed list and this class
        // is where that convention lives (the `G` button passes the draw loop's own `ri`).
        // Resolving it in the seam would mean a second file deciding which list the index
        // counts against, and this file is the one on the ERS allowlist.

        /// <summary>Opens the "Set Parent Group" popup on a group, the way the folder row's
        /// <c>G</c> button does, but with no click point, so it centres over this window.
        /// False when no such group exists.</summary>
        internal bool TryOpenGroupPickerForGroupForTesting(string groupName)
        {
            if (string.IsNullOrEmpty(groupName)) return false;
            List<string> names = EnumerateGroupNamesForTesting();
            if (!names.Contains(groupName)) return false;
            groupPicker.OpenForGroup(groupName, PickerWindowLayout.NoClickPoint);
            return groupPicker.IsOpen;
        }

        /// <summary>
        /// Opens the "Manage Groups" popup on one recording, the way a recording row's
        /// <c>G</c> button does, but with no click point, so it centres over this window.
        /// <paramref name="recordingId"/> may be a recording id or the token the seam passes
        /// for "the first row", since a committed spec cannot carry a save-specific id.
        /// </summary>
        internal bool TryOpenGroupPickerForRecordingForTesting(
            string recordingId, bool takeFirst, out string resolvedId)
        {
            resolvedId = null;
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            int index = -1;
            for (int i = 0; i < committed.Count; i++)
            {
                if (committed[i] == null) continue;
                if (takeFirst) { index = i; break; }
                if (string.Equals(committed[i].RecordingId, recordingId, System.StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }
            if (index < 0) return false;
            resolvedId = committed[index].RecordingId;
            groupPicker.OpenForRecording(index, PickerWindowLayout.NoClickPoint);
            return groupPicker.IsOpen;
        }

        /// <summary>Every recording id the picker could be opened on, for the "what does
        /// exist" reject message.</summary>
        internal List<string> EnumerateRecordingIdsForTesting()
        {
            var ids = new List<string>();
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
                if (committed[i] != null && !string.IsNullOrEmpty(committed[i].RecordingId))
                    ids.Add(committed[i].RecordingId);
            return ids;
        }

        // ----- GUI-census seam accessors (UiAction op=expand) -----
        //
        // The read-only view above cannot ADD, and every leaf row in the Recordings tab is
        // behind an expanded group: a census of a dense career photographed 16 collapsed
        // folders and not one of the 21 body cells. These are the same writes the caret
        // click makes (DrawGroupTree / DrawRecordingBlock), exposed so the seam can make
        // them without synthesising input.

        internal bool IsGroupExpandedForTesting(string groupName)
            => groupName != null && expandedGroups.Contains(groupName);

        internal bool SetGroupExpandedForTesting(string groupName, bool expanded)
        {
            if (string.IsNullOrEmpty(groupName)) return false;
            return expanded
                ? expandedGroups.Add(groupName)
                : expandedGroups.Remove(groupName);
        }

        internal bool IsChainExpandedForTesting(string blockId)
            => blockId != null && expandedChains.Contains(blockId);

        internal bool SetChainExpandedForTesting(string blockId, bool expanded)
        {
            if (string.IsNullOrEmpty(blockId)) return false;
            return expanded
                ? expandedChains.Add(blockId)
                : expandedChains.Remove(blockId);
        }

        internal int ExpandedGroupCountForTesting => expandedGroups.Count;

        internal int ExpandedChainCountForTesting => expandedChains.Count;

        /// <summary>Every group name the tab could draw a folder for, taken from the
        /// PRODUCTION tree model the group picker builds - so the enumeration cannot
        /// disagree with the tree the player sees (empty groups and hierarchy
        /// included).</summary>
        internal List<string> EnumerateGroupNamesForTesting()
        {
            GroupPickerTreeModel model = GroupPickerPresentation.BuildTreeModel(
                RecordingStore.GetGroupNames(),
                GroupHierarchyStore.GroupParents,
                parentUI.KnownEmptyGroups,
                null);
            var names = new List<string>(model.AllNames);
            names.Sort(System.StringComparer.Ordinal);
            return names;
        }

        /// <summary>Every chain id that carries a chain block header. Routed through the
        /// effective set (ERS), not the raw committed list, so a superseded segment's chain
        /// does not appear as an expandable block that never draws.</summary>
        internal List<string> EnumerateChainIdsForTesting()
        {
            var ids = new List<string>();
            var seen = new HashSet<string>();
            IReadOnlyList<Recording> committed = EffectiveState.ComputeERS();
            for (int i = 0; i < committed.Count; i++)
            {
                string chainId = committed[i] != null ? committed[i].ChainId : null;
                if (string.IsNullOrEmpty(chainId) || !seen.Add(chainId)) continue;
                ids.Add(chainId);
            }
            ids.Sort(System.StringComparer.Ordinal);
            return ids;
        }

        /// <summary>
        /// How many tabs the tab bar draws in <paramref name="mode"/> (design 7.4). The
        /// pure, testable expression of the zero-toolbar rule.
        /// <para>Advanced draws the full two-entry <see cref="TabLabels"/> toolbar. Basic
        /// draws NO toolbar at all - not a one-button one: a <c>GUILayout.Toolbar</c> with a
        /// single entry is visual noise, and the window title ("Parsek - Missions") already
        /// carries the identity. Hence 0, not 1, even though the Missions tab's CONTENT is
        /// what Basic renders.</para>
        /// <para><see cref="TabLabels"/> stays the single two-entry array (design 6.2);
        /// there is deliberately no separate Basic array to drift out of step with it.
        /// Derived from <see cref="UiSurfaceVisibility.IsVisible"/> so the tab-bar rule has
        /// the same single decision point as every other gated surface.</para>
        /// </summary>
        internal static int VisibleTabCount(UiComplexityMode mode)
        {
            return UiSurfaceVisibility.IsVisible(UiSurface.TabRecordings, mode)
                ? TabLabels.Length
                : 0;
        }

        /// <summary>
        /// Pure tab-index clamp (design 7.4). With <see cref="TabMissions"/> at index 0 the
        /// only out-of-range case left is a player sitting on the Recordings tab (index 1)
        /// when Basic is selected; clamping that to 0 lands on Missions, which is both in
        /// range and the semantically right destination.
        /// <para>Advanced is returned UNCHANGED: every Basic index is valid there, so
        /// entering Advanced never needs a clamp.</para>
        /// </summary>
        internal static int ClampTabIndexForMode(int selectedTabIndex, UiComplexityMode mode)
        {
            if (VisibleTabCount(mode) > 0)
                return selectedTabIndex;

            return TabMissions;
        }

        /// <summary>
        /// Applies the Basic clamp to this window's transient tab selection. Called from the
        /// deferred mode-apply step (design 7.2 / 7.4) via
        /// <c>ParsekUI.OnUiComplexityModeApplied</c>, so the clamp happens the moment the
        /// mode takes effect rather than waiting for the next draw.
        /// </summary>
        internal void ClampTabForBasic()
        {
            ApplyTabClamp(UiComplexityMode.Basic, "mode apply");
        }

        /// <summary>
        /// Closes the group picker popup as part of the Advanced -> Basic mode apply
        /// (design 7.2 step 2, edge case 4). The picker is OWNED by this window and is
        /// reachable only from the Recordings tab that Basic hides, but an already-open
        /// picker survives the switch: it draws from <c>DrawIfOpen</c> regardless of the
        /// selected tab, and would otherwise keep offering group mutations from a surface
        /// the player can no longer reach. It holds no input lock, so this is a
        /// reachability rule rather than a lock rule.
        /// <para>This window itself is NOT closed - it survives as the Missions window.</para>
        /// </summary>
        internal void CloseGroupPickerForModeChange()
        {
            if (groupPicker == null)
                return;

            groupPicker.Close();
        }

        /// <summary>Whether this window's group picker popup is currently open.</summary>
        internal bool IsGroupPickerOpen => groupPicker != null && groupPicker.IsOpen;

        /// <summary>
        /// Test seam: the group picker instance, so the close-set tests can open it the
        /// same way a row's `G` button does.
        /// </summary>
        internal GroupPickerUI GroupPickerForTesting => groupPicker;

        // ----- the two in-place rename editors, for UiAction op=edit -----
        //
        // ARM plus COMMIT plus READ, rather than raw field access, because the arming is
        // three writes that have to agree: the row key (which row is being edited), the
        // draft, and the focus sentinel that makes the NEXT draw pass call
        // GUI.FocusControl once. A seam that wrote two of the three would put the window
        // in a state no click can produce - a text field with no keyboard focus, which
        // photographs as an ordinary label and would read as the op having done nothing.
        //
        // The COMMIT stays a separate call because op=edit does not commit unless asked:
        // these two commits are not "write a name". A group rename of a tree root goes
        // through MissionGroupLink.RenameMissionGroup, which renames the root plus the auto
        // `/ Debris` and `/ Crew` subgroups plus Mission.Name atomically and REJECTS BOTH
        // on a collision; a recording rename silently DROPS when the id has left the
        // committed list. A census wants the mid-edit LAYOUT (a label became a text field),
        // which is reachable without either.

        /// <summary>Arms the recording-row rename editor on a recording id with a draft.
        /// False when that id is not in the committed list, which is the only way this can
        /// fail - the seam turns it into a typed REJECTED rather than arming an editor over
        /// a row that is not drawn.</summary>
        internal bool TryBeginRecordingRenameForTesting(string recordingId, string draft)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            // Resolved against the EFFECTIVE set, which is the list the table draws and
            // the one the commit body re-resolves against.
            if (RecordingStore.IndexOfRecordingId(
                    EffectiveState.ComputeERS(), recordingId) < 0)
                return false;
            // Production's double-click COMMITS a rival editor before arming this one
            // (the group rename and this one share `activeRenameRect` and cannot both be
            // live). Discarding it instead would lose a draft a player had typed, so the
            // seam does what the click does.
            if (renamingGroup != null) CommitGroupRename(renamingGroup);
            renamingRecordingId = recordingId;
            renamingRecordingText = draft ?? string.Empty;
            renamingRecordingFocused = false;
            return true;
        }

        internal string RenamingRecordingIdForTesting => renamingRecordingId;

        /// <summary>Whether the recording-rename text field has actually DRAWN since the
        /// arm. Written by the draw (the branch that calls <c>GUI.FocusControl</c> on its
        /// first pass), never by the arm, which is what makes it the one signal the seam
        /// cannot fake - see <c>TestCommandUiEdit.EditNotDrawnReason</c>.</summary>
        internal bool RenamingRecordingFocusedForTesting => renamingRecordingFocused;

        internal string RenamingRecordingTextForTesting => renamingRecordingText;

        /// <summary>Runs the recording rename's own commit body over the live committed
        /// list, exactly as the Enter key and the click-away path do.</summary>
        internal void CommitRecordingRenameForTesting()
            => CommitRecordingRename(EffectiveState.ComputeERS());

        /// <summary>Arms the group rename editor on a group NAME (the key this editor
        /// uses) with a draft. False for an unknown group.</summary>
        internal bool TryBeginGroupRenameForTesting(string groupName, string draft)
        {
            if (string.IsNullOrEmpty(groupName)) return false;
            if (!EnumerateGroupNamesForTesting().Contains(groupName)) return false;
            // The mirror of the recording-rename arm: commit the rival draft, never
            // discard it.
            if (renamingRecordingId != null)
                CommitRecordingRename(EffectiveState.ComputeERS());
            renamingGroup = groupName;
            renamingGroupText = draft ?? string.Empty;
            renamingGroupFocused = false;
            return true;
        }

        internal string RenamingGroupForTesting => renamingGroup;

        /// <summary>The group rename's draw-produced focus sentinel. See
        /// <see cref="RenamingRecordingFocusedForTesting"/>.</summary>
        internal bool RenamingGroupFocusedForTesting => renamingGroupFocused;

        internal string RenamingGroupTextForTesting => renamingGroupText;

        internal void CommitGroupRenameForTesting(string oldName)
            => CommitGroupRename(oldName);

        // Shared clamp + log body for both clamp sites (deferred mode-apply and the
        // defensive on-draw pass). Logs at Verbose with old index, new index, and the
        // active tab count (design 12.2); silent when the clamp is a no-op, which is the
        // common case (Missions at index 0 makes this a rare correction, not a per-frame
        // one).
        private void ApplyTabClamp(UiComplexityMode mode, string origin)
        {
            int clamped = ClampTabIndexForMode(selectedTab, mode);
            if (clamped == selectedTab)
                return;

            ParsekLog.Verbose("UI",
                $"Recordings window: tab index clamped {selectedTab}->{clamped} " +
                $"(mode={mode}, activeTabs={VisibleTabCount(mode)}, origin={origin})");
            selectedTab = clamped;
        }

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
        // Keyed on the store's StateVersion, which every committed-list membership mutation
        // bumps; a count-only gate missed a same-count restructure (merge + split nets zero).
        private int lastSortedStateVersion = int.MinValue;
        // A vessel rename moves no StateVersion but can move a row under a Name sort.
        private int lastSortedDisplayNamesVersion = int.MinValue;

        /// <summary>
        /// The Recordings tab's sort column (as an INDEX into
        /// <see cref="SortColumn"/>'s declaration order) and direction, for the
        /// automation-only <c>UiAction op=sort</c> seam op.
        ///
        /// <para>An int rather than the enum for the <c>TierFilterModeIndexForTesting</c>
        /// reason: the seam's wire vocabulary is a string table it maps to an index, and the
        /// enum is this class's own. An out-of-range write is IGNORED rather than thrown -
        /// the seam validates against its own table first.</para>
        ///
        /// <para>The setter also does what the header click's <c>onChanged</c> callback does:
        /// <c>InvalidateSort()</c>. Without it the table keeps its stale
        /// <c>sortedIndices</c> until the next store mutation bumps
        /// <c>RecordingStore.StateVersion</c>, so a capture would photograph the OLD row
        /// order under the new arrow - the one failure mode a sort op has.</para>
        /// </summary>
        internal int SortColumnIndexForTesting
        {
            get { return (int)sortColumn; }
            set
            {
                if (value < (int)SortColumn.Index || value > (int)SortColumn.LaunchSite)
                    return;
                sortColumn = (SortColumn)value;
                InvalidateSort();
            }
        }

        internal bool SortAscendingForTesting
        {
            get { return sortAscending; }
            set
            {
                sortAscending = value;
                InvalidateSort();
            }
        }

        // Dock-partner naming (design-dock-event-graph.md 6.5 / 7.6): the global dock-event
        // graph, fetched at most once per frame so a per-row tooltip lookup is a dictionary hit
        // rather than a signature recompute. The host cache is itself signature-gated, so this
        // frame memo only removes the per-row string build, never a rebuild.
        private DockEventGraph dockEventGraphCache;
        private int dockEventGraphCacheFrame = -1;
        // Per-frame memo of the resolved tooltip text per recording id (null entries included):
        // the resolver behind it walks MissionStore per call, and OnGUI runs several passes per
        // frame over every visible row. Cleared with the graph reference above.
        private readonly Dictionary<string, string> dockPartnerTooltipCache =
            new Dictionary<string, string>();

        // Tooltip state
        private Rect scrollViewRect;
        // Bottom "hovered control help text" strip. See TooltipEchoBox for why it is a
        // permanently visible box of constant height. SINGLE line: this window is 1355 px
        // wide, so its whole help corpus fits one wrapped line; anything that overflows
        // (a long runtime-composed status or hold text) marquee-scrolls. The line count is
        // pinned against this constructor by TooltipEchoBudgetTests.
        private readonly TooltipEchoBox tooltipEcho =
            new TooltipEchoBox(SpacingSmall, TooltipEchoBox.SingleLine);
        // Window-computed hover text that outranks GUI.tooltip for the strip (the
        // clamped loop-period cell resolves its own string from a rect hit test).
        // Cleared at the top of every pass; set during the row draw, i.e. BEFORE
        // DrawRecordingsWindowTooltip runs.
        private string recordingsWindowTooltipText = "";

        // Loop period editing — buffer used while text field is focused
        private int loopPeriodFocusedRi = -1;
        private string loopPeriodEditText = "";
        private Rect loopPeriodEditRect;

        private const float ColW_Period = 90f;
        private const float SpacingSmall = 3f;
        private static readonly Color LoopPeriodClampColor = ParsekUI.CountdownTextColor;

        // Rewind/Forward button state tracking for transition logging
        private Dictionary<int, bool> lastCanRewind = new Dictionary<int, bool>();
        private Dictionary<int, bool> lastCanFF = new Dictionary<int, bool>();
        private Dictionary<int, bool> lastStashSealInReFlyColumn = new Dictionary<int, bool>();

        // Tracks rows where the legacy rewind-to-launch button is suppressed
        // because the recording is a non-owner tree branch (the rewind save
        // belongs to the tree root). Keyed by row index so the debounce
        // mirrors lastCanRewind. Logged once per transition so a tree merge
        // doesn't flood the log with one line per branch every frame.
        private Dictionary<int, bool> lastSuppressedTreeBranch = new Dictionary<int, bool>();

        // "Shown once" for the Rewind column: the committed index the nearest DRAWN enclosing
        // row (folder or block) offers R / FF to, or -1. A child whose own button would target
        // the same recording draws a blank cell instead. Set around each child draw pass and
        // restored in a finally, so they are -1 whenever no Recordings-tab parent is drawing
        // (the Missions tab reuses DrawLegacyRewindForwardCell and is never suppressed). The
        // STASH virtual group resets both: it draws a blank R, so its leaves keep theirs.
        private int enclosingRewindTargetIdx = -1;
        private int enclosingForwardTargetIdx = -1;
        // Transition-log dedup for that suppression, keyed "row:{ri}:{r|ff}" for a leaf and
        // "group:{name}" / "block:{id}" for an aggregate row. Cleared with the index-keyed
        // dicts on every committed-list membership change.
        private readonly Dictionary<string, bool> lastSuppressedByEnclosingRow =
            new Dictionary<string, bool>();
        // Transition-log dedup for the mission folder absorbing its launched vessel's block,
        // keyed by group name (same quiet-first-false rule as the suppression cache).
        private readonly Dictionary<string, bool> lastRootBlockAbsorbedByGroup =
            new Dictionary<string, bool>();

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
        private GUIStyle statusStyleStatic;
        private GUIStyle statusStyleStationary;

        private GUIStyle boldHeaderInnerLabel;
        private GUIStyle indexRowStyle;
        private GUIStyle bodyCellLabel;
        // Zero-horizontal-margin button style used inside DrawBodyCenteredButton's
        // BeginHorizontal wrap. With default 4/4 margin, a Space(N)+Button layout
        // overflows the container (margins are "outside" the content width). Zeroing
        // horizontal margin lets Space(N) + Button(Width=cellW-N) fit the cell exactly.
        private GUIStyle bodyCellButtonFlush;
        // Compact-padding variant for narrow action columns; keeps labels
        // readable inside the narrow Rewind (60px) and Re-Fly (90px) columns.
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

        // Tab-bar button style: the selected tab looks pressed (onNormal/onHover background
        // copied from GUI.skin.button.active). Mirrors KerbalsWindowUI.
        private GUIStyle toggleButtonStyle;

        // Window drag tracking for position logging
        private Rect lastRecordingsWindowRect;

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
        /// Cross-link entry point used by the Timeline GoTo button: opens this window on the
        /// MISSIONS tab and asks <see cref="MissionsWindowUI.RevealMissionForRecording"/> to
        /// scroll to the mission that owns <paramref name="recordingId"/>.
        /// <para>Works in BOTH complexity modes, which is the point: the Missions tab is the
        /// one Basic keeps, so unlike the old Recordings-tab cross-link this needs no gate and
        /// strands nobody (design `docs/dev/design-ui-basic-advanced.md` section 4.1a). The
        /// <see cref="TabMissions"/> write is Basic-valid by construction - it is what
        /// <see cref="ClampTabIndexForMode"/> clamps TO and what the Basic content dispatch is
        /// pinned to.</para>
        /// </summary>
        internal void ShowMissionForRecording(string recordingId)
        {
            if (!showRecordingsWindow) showRecordingsWindow = true;

            if (selectedTab != TabMissions)
            {
                ParsekLog.Verbose("UI",
                    $"Cross-link: selecting Missions tab (was {selectedTab})");
                selectedTab = TabMissions;
            }

            parentUI.GetMissionsUI().RevealMissionForRecording(recordingId);
        }

        /// <summary>
        /// Drops the Timeline's cached entry list after a write to
        /// <see cref="Recording.Hidden"/>, so a Timeline open alongside this window picks
        /// the change up on its next draw.
        /// <para>Needed because timeline invalidation otherwise fires only from
        /// <c>LedgerOrchestrator.OnTimelineDataChanged</c> and an archive is not a ledger
        /// event: without this, archiving a row while both windows are open leaves the
        /// flight on the Timeline until some unrelated recalc. Called only
        /// from the three change branches that actually write the flag (the per-row
        /// Archive checkbox and the two group aggregates), never per frame. The Archive
        /// header FILTER needs no call: the Timeline never shows archived recordings, so the
        /// filter does not reach it. See `docs/dev/design-ui-basic-advanced.md` section
        /// 4.4.</para>
        /// </summary>
        private void NotifyTimelineOfArchiveChange()
        {
            parentUI?.GetTimelineUI()?.InvalidateCache();
        }

        /// <summary>
        /// The Recordings tab's navigation API: opens the window on that tab, makes the target
        /// recording visible there (un-archives it, expands its parent groups and their
        /// ancestors, un-hides hidden groups), and schedules the scroll that the row draw
        /// consumes two passes later.
        /// <para>DECIDED (2026-08-01): kept, with no production caller. The Timeline GoTo button
        /// was the only one; it routes through <see cref="ShowMissionForRecording"/> since design
        /// 4.1a so it works in Basic too. This stays because "reveal a recording in the
        /// Recordings tab" is a coherent capability of that tab rather than scaffolding left over
        /// from GoTo, and deleting it would also mean unpicking
        /// <see cref="pendingScrollToRecordingId"/> / <see cref="pendingScrollRowIndex"/> /
        /// <see cref="renderedRowCounter"/> from the row-draw hot path for no behavioural gain.
        /// The obligation that comes with keeping it is COVERAGE: `RecordingsTableApiTests` drives
        /// the whole method, not just the tab flip the clamp tests use it for, so the machinery
        /// cannot rot silently behind three test names that only exercise its first three
        /// lines.</para>
        /// <para>It needs no complexity gate of its own: the defensive on-draw clamp in
        /// <c>DrawRecordingsWindow</c> (design 7.4) covers any caller, and the Basic dispatch is
        /// pinned to Missions regardless.</para>
        /// </summary>
        internal void ScrollToRecording(string recordingId)
        {
            if (!showRecordingsWindow) showRecordingsWindow = true;

            // The pending scroll id is consumed ONLY inside the Recordings-tab row draw,
            // which sits after the Missions-tab early return. Missions is the default tab,
            // so the cross-link must select the Recordings tab explicitly or the scroll
            // silently never lands and the pending id dangles.
            if (selectedTab != TabRecordings)
            {
                ParsekLog.Verbose("UI",
                    $"Cross-link: selecting Recordings tab (was {selectedTab})");
                selectedTab = TabRecordings;
            }

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

            // Un-archive if the recording itself is archived. Note this is all that is needed:
            // the HideActive filter only hides recordings whose flag is set, so clearing the flag
            // is what makes the row visible, and the filter itself is left as the player set it.
            // (A branch that also cleared HideActive used to sit here; it tested target.Hidden
            // immediately AFTER this block cleared it, so it could never run. Removed rather than
            // repaired - clearing a global filter is a bigger hammer than revealing one row
            // needs, and the group-level case below is handled separately.)
            if (target.Hidden)
            {
                target.Hidden = false;
                ParsekLog.Info("UI", $"Cross-link: unhid recording \"{target.VesselName}\"");
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
                    DefaultWindowWidth, recHeight);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI", $"Recordings window initial position: x={recordingsWindowRect.x.ToString("F0", ic)} y={recordingsWindowRect.y.ToString("F0", ic)}");
            }

            ParsekUI.HandleResizeDrag(ref recordingsWindowRect, ref isResizingRecordingsWindow,
                MinWindowWidth, MinWindowHeight, "Missions window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                recordingsWindowRect = ParsekUiScale.Window(
                    WindowIdKey.GetHashCode(),
                    recordingsWindowRect,
                    DrawRecordingsWindow,
                    "Parsek - Missions",
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
            groupPicker.Draw(recordingsWindowRect);

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

        private void HandleRecordingsDefocus(IReadOnlyList<Recording> committed)
        {
            // Click outside active rename field -> commit and close
            if (Event.current.type == EventType.MouseDown &&
                (renamingRecordingId != null || renamingGroup != null))
            {
                if (activeRenameRect.width > 0 && !activeRenameRect.Contains(Event.current.mousePosition))
                {
                    if (renamingRecordingId != null)
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

        // --- Group tree rendering helpers ---

        // Box-drawing tree-branch prefixes built from char codes so the source
        // stays ASCII (matching the \u arrow constants in this file and the
        // Kerbals roster tree). U+251C "├" + U+2500 "─" = tee; U+2514 "└" = corner.
        private static readonly string TreeConnectorMid =
            new string(new[] { (char)0x251C, (char)0x2500, ' ' });
        private static readonly string TreeConnectorLast =
            new string(new[] { (char)0x2514, (char)0x2500, ' ' });

        // Marginal pixel width the connector glyphs add inside a label, measured
        // once and cached. The per-level indent step equals this width so a
        // child's connector sits exactly under its parent's caret at every depth:
        // the connector occupies one indent step, the parent's caret is the first
        // glyph after the parent's own connector, and both land at the same x.
        // The legacy fixed 15px step did not match the glyph width and drifted
        // a few pixels per level in deeper subgroups.
        private static float cachedConnectorWidth = -1f;

        private const string StashUnfinishedFlightTooltip =
            "Stash this stable Rewind Point slot in STASH so it can be re-flown later";

        private const string SealStableSlotTooltip =
            "Close this Rewind Point slot permanently without changing the recording";

        // Per-slot last-emitted decision stateKey. Mirrors the `lastCanInvoke`
        // pattern at line ~2587 above so the on-change gate works reliably from
        // OnGUI per-frame draw loops. ParsekLog.VerboseOnChange has shown
        // production spam from this call site (1389 identical emits in 6s
        // observed in 2026-04-26_1025 playtest); a file-local dictionary is
        // empirically reliable in the same OnGUI hot path elsewhere in this
        // file.
        private static readonly Dictionary<string, string> slotCanInvokeLastStateKey =
            new Dictionary<string, string>();

        private void CommitRecordingRename(IReadOnlyList<Recording> committed)
        {
            string id = renamingRecordingId;
            renamingRecordingId = null;
            activeRenameRect = default;
            int ri = RecordingStore.IndexOfRecordingId(committed, id);
            if (ri < 0)
            {
                ParsekLog.Warn("UI", $"Recording rename dropped: id={id ?? "<null>"} is no longer in the committed list");
                return;
            }

            // An identity-wide vessel name decision: every leg of the row's vessel takes the
            // name and DisplayNames.Version moves (a rename moves no StateVersion). A blank /
            // unchanged name, an EVA row or a vessel with no launch guid is refused and logged.
            MissionVesselNaming.ApplyRecordingRename(committed[ri], renamingRecordingText);
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

            // A tree's root group is the same abstraction as its main mission in the Missions
            // window. Rename both together (and the auto "/ Debris" / "/ Crew" subgroups), atomically.
            RecordingTree missionTree = MissionGroupLink.FindTreeByRootGroup(oldName);
            if (missionTree != null)
            {
                if (!MissionGroupLink.RenameMissionGroup(missionTree, newName, out string reason))
                {
                    ParsekLog.Warn("UI", $"Group rename rejected: '{newName}' ({reason})");
                    return;
                }
                // Remap UI expansion / known-empty state for the root and any cascaded subgroups.
                RemapGroupUIState(oldName, newName);
                RemapGroupUIState(oldName + RecordingGroupStore.DebrisSubgroupSuffix,
                    newName + RecordingGroupStore.DebrisSubgroupSuffix);
                RemapGroupUIState(oldName + RecordingGroupStore.CrewSubgroupSuffix,
                    newName + RecordingGroupStore.CrewSubgroupSuffix);
                ParsekLog.Info("UI",
                    $"Group '{oldName}' renamed to '{newName}' (linked to main mission + subgroups)");
                return;
            }

            // Plain group (user group, a subgroup renamed directly, or a chain block): no mission link.
            if (!RecordingStore.RenameGroup(oldName, newName))
            {
                ParsekLog.Warn("UI", $"Group rename rejected: '{newName}' already exists");
                return;
            }

            GroupHierarchyStore.RenameGroupInHierarchy(oldName, newName);
            RemapGroupUIState(oldName, newName);

            ParsekLog.Info("UI", $"Group '{oldName}' renamed to '{newName}'");
        }

        // Carries a group's UI-only view state (expansion + known-empty bookkeeping) across a
        // rename so the renamed folder keeps its expanded/known state.
        private void RemapGroupUIState(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName)
                return;
            if (expandedGroups.Remove(oldName))
                expandedGroups.Add(newName);
            int emptyIdx = KnownEmptyGroups.IndexOf(oldName);
            if (emptyIdx >= 0) KnownEmptyGroups[emptyIdx] = newName;
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
                    "Confirm: Disband Group", HighLogic.UISkin,
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

        /// <summary>The prefix of the end-place clause in a Status hover.</summary>
        internal const string StatusEndPlacePrefix = "Ends: ";

        private static IReadOnlyList<RecordingSupersedeRelation> CurrentRecordingSupersedesForDisplay()
        {
            var scenario = ParsekScenario.Instance;
            return object.ReferenceEquals(null, scenario)
                ? null
                : scenario.RecordingSupersedes;
        }

        private static IReadOnlyList<RecordingRewindRetirement> CurrentRecordingRewindRetirementsForDisplay()
        {
            var scenario = ParsekScenario.Instance;
            return object.ReferenceEquals(null, scenario)
                ? null
                : scenario.RecordingRewindRetirements;
        }

        private static bool IsSupersededForDisplay(
            Recording rec,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            return EffectiveState.IsSupersededByRelation(rec, supersedes);
        }

        private static bool IsInactiveForDisplay(
            Recording rec,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            IReadOnlyList<RecordingRewindRetirement> retirements)
        {
            // Cascade overload: parent-anchored debris of a retired recording
            // renders as inactive in the recordings table so the orphan row
            // does not look like a separately-flyable lineage.
            return IsSupersededForDisplay(rec, supersedes)
                || EffectiveState.IsRewindRetired(rec, RecordingStore.CommittedRecordings, retirements);
        }
    }
}
