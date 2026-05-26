using System.Collections.Generic;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The Missions window. A standalone class that reuses <see cref="RecordingsTableUI"/>'s
    /// visual style and rendering helpers (column-header style, dark table-body box,
    /// tree-branch connector glyphs, expand/collapse carets, section-header bar) without
    /// inheriting it. Unlike the recordings window (which groups individual recordings:
    /// groups -> chains -> recordings), this groups the higher mission abstraction: it
    /// lists saved <see cref="Mission"/>s (from <see cref="MissionStore"/>), and renders
    /// each one's tree as collapsed continuous-vessel through-lines (from
    /// <see cref="MissionThroughLineBuilder"/>) with the offshoots that left them as
    /// children. Per Mission: a name + Clone/Delete, and include checkboxes bound to that
    /// Mission's selection (persisted). Individual recording legs never appear here.
    /// Not yet wired: looping a Mission as a unit. See docs/dev/design-mission-abstractions.md.
    /// </summary>
    internal class MissionsWindowUI
    {
        private readonly ParsekUI parentUI;

        private bool showWindow;
        private Rect windowRect;
        private Rect lastWindowRect;
        private Vector2 scrollPos;
        private bool isResizing;
        private bool hasInputLock;

        // Collapsed through-line heads, keyed "missionId:headId" so two Missions over
        // the same tree collapse independently. Transient UI state (not persisted). The
        // include selection lives per-Mission in Mission.ExcludedThroughLineHeadIds.
        private readonly HashSet<string> collapsedLegs = new HashSet<string>();

        // Per-Mission loop-period edit buffer, keyed by Mission.Id. Transient UI state:
        // holds the in-progress text while the player types, committed on field change.
        private readonly Dictionary<string, string> loopPeriodEditBuffers =
            new Dictionary<string, string>();

        // Per-frame cache of the derived mission read model (structure + through-line
        // view) keyed by tree id. OnGUI fires several times per frame (Layout, Repaint,
        // input events) and multiple missions can target the same tree, so without this
        // the two builders ran 6-12 times per frame for unchanged trees. The cache is
        // cleared whenever a new frame starts, so it always reflects the current trees
        // (no invalidation logic, no staleness) and the Layout and Repaint passes of one
        // frame see identical data.
        private readonly Dictionary<string, (MissionStructure structure, MissionThroughLineView view)>
            missionViewCache =
                new Dictionary<string, (MissionStructure structure, MissionThroughLineView view)>();
        private int missionViewCacheFrame = -1;

        // Per-frame cache of the composition-over-time trees (one list of root nodes per tree id),
        // built from the same MissionStructure. Cleared alongside missionViewCache each frame.
        private readonly Dictionary<string, List<MissionCompositionNode>> compositionCache =
            new Dictionary<string, List<MissionCompositionNode>>();

        private const string InputLockId = "Parsek_MissionsWindow";
        private const float MinWindowWidth = 450f;
        private const float MinWindowHeight = 150f;
        // Default width: the prior 680 plus 30 px of breathing room plus the new
        // per-mission Watch button column plus the rightmost Archive column.
        private const float DefaultWidth = 680f + 30f + ColW_Watch + ColW_Archive;

        // Fixed-width columns to the right of the expanding "Missions and vessels" column,
        // mirroring the recordings window's fixed-width cells so the window reads as the
        // same table style. Layout: [index/check] Missions and vessels | Start time |
        // Start event | End event | End time. The first column doubles as the mission
        // index cell (on header bars) and the include checkbox (on through-line rows).
        private const float ColW_Index = 30f;
        private const float ColW_StartTime = 100f;
        private const float ColW_StartEvent = 85f;
        private const float ColW_EndEvent = 85f;
        private const float ColW_EndTime = 100f;
        private const float ColW_Action = 60f;
        private const float ColW_Watch = 50f;
        // Rightmost Archive column (mirrors the recordings window's Archive/Hide column): the
        // header carries the global "hide archived" toggle, each mission row a per-mission check.
        private const float ColW_Archive = 60f;

        // Mission-header loop controls (live on the header row, not the table columns).
        // Wide enough to fit the "Loop" label plus the trailing checkbox side by side.
        private const float ColW_Loop = 64f;
        private const float ColW_Period = 90f;

        // How a Mission row list is ordered. Index = the per-tree index number (clones of a
        // tree share it); Name = alphabetic mission name; StartTime = the mission span start.
        internal enum MissionSortColumn { Index, Name, StartTime }
        private MissionSortColumn sortColumn = MissionSortColumn.Index;
        private bool sortAscending = true;

        // Inline mission-title rename (double-click the name), mirroring the recordings
        // window group rename. renamingMissionId is the Mission.Id currently being edited.
        private string renamingMissionId;
        private string renamingMissionText = "";
        private bool renamingMissionFocused;
        private Rect activeMissionRenameRect;
        private string lastClickedMissionId;
        private float lastMissionClickTime;
        private const float DoubleClickThreshold = 0.3f;

        private static readonly Color DimColor = new Color(1f, 1f, 1f, 0.45f);

        // Tint for a loop-period value that the overlap cap raised above what was requested
        // (so the cell shows the real effective cadence in a distinct colour). Soft amber.
        private static readonly Color LoopPeriodClampColor = new Color(1f, 0.8f, 0.4f);

        // -- Recreated styles --
        // RecordingsTableUI builds these privately inside EnsurePhaseStyles();
        // we cannot reach those instances from a subclass, so we lazily rebuild
        // the same styles here (verbatim, adapted) to match the look exactly.
        private GUIStyle bodyCellLabel;
        private GUIStyle tableBodyBoxStyle;
        // Zero-horizontal-margin field/button styles for the loop-period cell, identical to
        // the recordings window's bodyCellTextFieldFlush / bodyCellButtonFlush so the value
        // box and unit button render the same as RecordingsTableUI.DrawLoopPeriodCell.
        private GUIStyle bodyCellTextFieldFlush;
        private GUIStyle bodyCellButtonFlush;
        // Boxed header-cell container + bold inner label for a header cell that shares its
        // dark box with a toggle (Archive), mirroring RecordingsTableUI.colHdrCellContainerStyle
        // / boldHeaderInnerLabel. The container's left/right margin is zeroed (so its footprint
        // is exactly Width(X), matching the body cells below it) while the box background spans
        // the whole cell, label + checkbox included.
        private GUIStyle colHdrCellContainerStyle;
        private GUIStyle boldHeaderInnerLabel;

        // Column-header text inset (matches RecordingsTableUI.BodyCellTextIndent so
        // body labels land under their header text).
        private const int BodyCellTextIndent = 5;

        // -- Caret glyphs (built from char codes so the source stays ASCII, the
        // same way RecordingsTableUI builds its TreeConnector glyphs) --
        // U+25BC down caret (expanded), U+25B6 right caret (collapsed),
        // U+21B3 downward-rightward arrow (merge reference row).
        private static readonly string CaretDown =
            new string(new[] { (char)0x25BC, ' ' });
        private static readonly string CaretRight =
            new string(new[] { (char)0x25B6, ' ' });
        private static readonly string MergeArrow =
            new string(new[] { (char)0x21B3, ' ' });

        public MissionsWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        public bool IsOpen
        {
            get { return showWindow; }
            set { showWindow = value; }
        }

        public void DrawIfOpen(Rect mainWindowRect)
        {
            if (!showWindow)
            {
                ReleaseInputLock();
                return;
            }

            if (windowRect.width < 1f)
            {
                float defaultH = parentUI.InFlightMode ? mainWindowRect.height : mainWindowRect.height * 2f;
                // Restore the player's last size when one was persisted (clamped to the minimums);
                // otherwise fall back to the default width and a height derived from the main window.
                float w = (!float.IsNaN(MissionStore.WindowWidth) && MissionStore.WindowWidth > 0f)
                    ? Mathf.Max(MinWindowWidth, MissionStore.WindowWidth)
                    : DefaultWidth;
                float h = (!float.IsNaN(MissionStore.WindowHeight) && MissionStore.WindowHeight > 0f)
                    ? Mathf.Max(MinWindowHeight, MissionStore.WindowHeight)
                    : defaultH;
                windowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10f,
                    mainWindowRect.y,
                    w, h);
            }

            ParsekUI.HandleResizeDrag(ref windowRect, ref isResizing,
                MinWindowWidth, MinWindowHeight, "Missions window");

            // Remember the current size so it survives a reload (persisted with the missions via
            // MissionStore.Save). Cheap two-field copy each frame; the scenario save reads it.
            MissionStore.WindowWidth = windowRect.width;
            MissionStore.WindowHeight = windowRect.height;

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;

            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackground, out Color prevContent);
            try
            {
                windowRect = ClickThruBlocker.GUILayoutWindow(
                    "ParsekMissions".GetHashCode(),
                    windowRect,
                    DrawWindow,
                    "Parsek - Missions",
                    opaqueWindowStyle,
                    GUILayout.Width(windowRect.width),
                    GUILayout.Height(windowRect.height));
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackground, prevContent);
            }

            parentUI.LogWindowPosition("Missions", ref lastWindowRect, windowRect);

            if (windowRect.Contains(Event.current.mousePosition))
            {
                if (!hasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, InputLockId);
                    hasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        internal void ReleaseInputLock()
        {
            if (!hasInputLock)
                return;
            InputLockManager.RemoveControlLock(InputLockId);
            hasInputLock = false;
        }

        // Lazily rebuilds the body-cell label and dark table-body box styles the
        // recordings window uses. Same definitions as RecordingsTableUI.EnsurePhaseStyles
        // (cell label padding = left-only indent matching the header text inset;
        // body box = dark background, zero horizontal padding so columns align with
        // the header above the scroll view).
        private void EnsureStyles()
        {
            if (bodyCellLabel != null) return;

            var cellLabelPadding = new RectOffset(BodyCellTextIndent, 0, 0, 0);
            bodyCellLabel = new GUIStyle(GUI.skin.label) { padding = cellLabelPadding };

            tableBodyBoxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(0, 0, 2, 2),
                margin = new RectOffset(0, 0, 0, 0)
            };

            bodyCellButtonFlush = new GUIStyle(GUI.skin.button)
            {
                margin = new RectOffset(
                    0, 0,
                    GUI.skin.button.margin.top, GUI.skin.button.margin.bottom)
            };
            bodyCellTextFieldFlush = new GUIStyle(GUI.skin.textField)
            {
                margin = new RectOffset(
                    0, 0,
                    GUI.skin.textField.margin.top, GUI.skin.textField.margin.bottom)
            };

            // Boxed container for the Archive header cell: clone the shared column-header
            // box but zero only the LEFT/RIGHT margin so the BeginHorizontal footprint is
            // exactly Width(ColW_Archive) (top/bottom margin kept for the header-to-list gap).
            // This is what lets the dark box wrap the whole cell (label + checkbox) without
            // shifting the column relative to the body rows.
            colHdrCellContainerStyle = new GUIStyle(parentUI.GetColumnHeaderStyle())
            {
                margin = new RectOffset(0, 0, 4, 4)
            };

            // Bold inner label for the Archive header cell: the container box already supplies
            // the dark background, so the label uses a plain bold style (boxing it again would
            // double-box).
            boldHeaderInnerLabel = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };
        }

        private void DrawWindow(int windowID)
        {
            EnsureStyles();

            // Click outside an active rename field -> commit and close (mirrors the
            // recordings window's defocus handling).
            if (Event.current.type == EventType.MouseDown && renamingMissionId != null
                && activeMissionRenameRect.width > 0
                && !activeMissionRenameRect.Contains(Event.current.mousePosition))
            {
                CommitMissionRenameById(renamingMissionId);
            }

            // Breathing room below the title bar (matches the recordings window).
            GUILayout.Space(5);

            GUILayout.BeginVertical();

            var trees = RecordingStore.CommittedTrees;
            MissionStore.EnsureDefaultsForTrees(trees);
            var missions = MissionStore.Missions;
            if (missions == null || missions.Count == 0)
            {
                GUILayout.Label("No missions recorded yet.");
                GUILayout.FlexibleSpace();
            }
            else
            {
                // Fixed column-header row (outside the scroll view), styled with the
                // recordings window's shared column-header style.
                DrawColumnHeader();

                scrollPos = GUILayout.BeginScrollView(scrollPos, false, true, GUILayout.ExpandHeight(true));

                // Dark list-area background (matches the recordings window) with no
                // horizontal padding so columns align with the header above.
                GUILayout.BeginVertical(tableBodyBoxStyle);

                // Per-tree index numbers (1-based, in committed-tree order). Clones share a
                // tree, so they share its index; renaming a mission never changes it.
                Dictionary<string, int> treeIndex = BuildTreeIndexMap(trees);

                // Snapshot + sort for display. Clone / Delete (which mutate the store) are
                // invoked from within the draw loop; the change takes effect next frame.
                var ordered = BuildSortedMissionRows(missions, trees, treeIndex);
                int missionCount = 0;
                int rowCount = 0;
                for (int i = 0; i < ordered.Count; i++)
                {
                    Mission mission = ordered[i].mission;
                    RecordingTree tree = FindTree(trees, mission.TreeId);
                    if (tree == null)
                        continue;
                    // Archive: when the window's Archive toggle is on, archived missions drop out
                    // of the list (mirrors the recordings window hiding Hidden rows). Their loop /
                    // ghost state is untouched; un-archive or toggle off to see them again.
                    if (MissionStore.HideArchived && mission.Archived)
                        continue;
                    missionCount++;

                    var (_, view) = GetMissionView(tree);

                    DrawMissionHeader(mission, ordered[i].index, view);

                    // Composition-over-time tree (the vessel rows). Each node is a structural
                    // interval / branch with its own independent include checkbox (interval-level
                    // start/end trim), bound to Mission.ExcludedIntervalKeys - no cascade.
                    var compRoots = GetCompositionRoots(tree);
                    for (int r = 0; r < compRoots.Count; r++)
                    {
                        bool isLast = r == compRoots.Count - 1;
                        rowCount += DrawCompositionNode(compRoots[r], mission, 1, isLast, false);
                    }
                }

                GUILayout.EndVertical();
                GUILayout.EndScrollView();

                ParsekLog.VerboseRateLimited("UI", "missions-window-draw",
                    $"Missions window: missions={missionCount} rows={rowCount}", 5.0);
            }

            // Full-width Close button at the bottom (matches the Timeline window).
            if (GUILayout.Button("Close"))
                IsOpen = false;

            GUILayout.EndVertical();
            ParsekUI.DrawResizeHandle(windowRect, ref isResizing, "Missions window");
            GUI.DragWindow();
        }

        private static RecordingTree FindTree(List<RecordingTree> trees, string treeId)
        {
            if (trees == null || string.IsNullOrEmpty(treeId))
                return null;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null && trees[i].Id == treeId)
                    return trees[i];
            return null;
        }

        // Returns the derived (structure, view) for a tree, building it at most once per
        // frame per tree id. The first lookup in a new frame clears the cache so the data
        // stays fresh; later lookups in the same frame (other OnGUI passes, or other
        // missions targeting the same tree) hit the cache instead of rebuilding.
        private (MissionStructure structure, MissionThroughLineView view) GetMissionView(RecordingTree tree)
        {
            int frame = Time.frameCount;
            if (frame != missionViewCacheFrame)
            {
                missionViewCache.Clear();
                compositionCache.Clear();
                missionViewCacheFrame = frame;
            }

            if (!missionViewCache.TryGetValue(tree.Id, out var cached))
            {
                MissionStructure structure = MissionStructureBuilder.Build(tree);
                MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);
                cached = (structure, view);
                missionViewCache[tree.Id] = cached;
            }

            return cached;
        }

        // Returns the cached composition-over-time root nodes for a tree (built once per frame
        // from the same MissionStructure that GetMissionView caches).
        private List<MissionCompositionNode> GetCompositionRoots(RecordingTree tree)
        {
            if (!compositionCache.TryGetValue(tree.Id, out var roots))
            {
                var (structure, _) = GetMissionView(tree);
                roots = MissionCompositionBuilder.Build(structure);
                compositionCache[tree.Id] = roots;
            }
            return roots;
        }

        // Renders one composition node (a structural interval, a peeled-off branch, or a roster
        // atom) and recurses into its children. Every selectable node (interval / branch) carries
        // an INDEPENDENT include checkbox bound to Mission.ExcludedIntervalKeys: unchecking it
        // start/end-trims just that segment, with NO cascade (excluding the launch interval keeps
        // the post-decouple survivor checked, which is the whole point). A roster atom carries no
        // checkbox and greys with its owning interval. Returns the number of rows drawn.
        private int DrawCompositionNode(MissionCompositionNode node,
            Mission mission, int depth, bool isLast, bool parentExcluded)
        {
            if (node == null)
                return 0;

            bool selectable = node.IsSelectable;
            bool selfExcluded = selectable && mission.ExcludedIntervalKeys.Contains(node.HeadLegId);
            // A selectable interval/branch greys only when IT is excluded (independent toggles);
            // a roster atom greys with its owning interval (parentExcluded).
            bool greyed = selectable ? selfExcluded : parentExcluded;
            bool hasChildren = node.Children.Count > 0;
            bool collapsed = hasChildren && collapsedLegs.Contains(CollapseKey(mission, node.HeadLegId));

            DrawCompositionRow(node, mission, depth, isLast, selectable, selfExcluded, greyed,
                hasChildren, collapsed);
            int rows = 1;

            if (hasChildren && !collapsed)
            {
                // Atoms grey with their interval; sub-intervals are independent (own state).
                bool childParentExcluded = selectable ? selfExcluded : parentExcluded;
                for (int i = 0; i < node.Children.Count; i++)
                {
                    bool childLast = i == node.Children.Count - 1;
                    rows += DrawCompositionNode(node.Children[i], mission,
                        depth + 1, childLast, childParentExcluded);
                }
            }
            return rows;
        }

        private void DrawCompositionRow(MissionCompositionNode node, Mission mission,
            int depth, bool isLast, bool selectable, bool selfExcluded, bool greyed,
            bool hasChildren, bool collapsed)
        {
            GUILayout.BeginHorizontal();

            // First column: an independent include checkbox on every interval / branch (no
            // cascade - unchecking drops just this segment); a blank cell on roster atoms.
            if (selectable)
            {
                bool shownChecked = !selfExcluded;
                bool toggled = GUILayout.Toggle(shownChecked, "", GUILayout.Width(ColW_Index));
                if (toggled != shownChecked)
                {
                    if (toggled) mission.ExcludedIntervalKeys.Remove(node.HeadLegId);
                    else mission.ExcludedIntervalKeys.Add(node.HeadLegId);
                    ParsekLog.Info("Mission",
                        $"Mission '{mission.Name}' interval '{node.HeadLegId}' included={toggled}");
                }
            }
            else
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Index));
            }

            Color prevColor = GUI.color;
            if (greyed)
                GUI.color = DimColor;

            float indent = RecordingsTableUI.SelfConnectorIndent(depth);
            if (indent > 0f)
                GUILayout.Space(indent);
            string connector = depth > 0 ? RecordingsTableUI.TreeConnector(isLast) : "";
            string caret = hasChildren ? (collapsed ? CaretRight : CaretDown) : "";
            // "Vessel (composition)" for vessel/interval rows; the bare label for atoms and for
            // single-piece rows whose name already equals the composition (e.g. an EVA kerbal).
            string label = (!string.IsNullOrEmpty(node.VesselName) && node.VesselName != node.CompositionLabel)
                ? node.VesselName + " (" + node.CompositionLabel + ")"
                : node.CompositionLabel;
            string wide = connector + caret + label;

            if (hasChildren)
            {
                if (GUILayout.Button(wide, bodyCellLabel, GUILayout.ExpandWidth(true)))
                {
                    string key = CollapseKey(mission, node.HeadLegId);
                    if (collapsedLegs.Contains(key)) collapsedLegs.Remove(key);
                    else collapsedLegs.Add(key);
                }
            }
            else
            {
                GUILayout.Label(wide, bodyCellLabel, GUILayout.ExpandWidth(true));
            }

            // Interval / vessel rows show their span + bounding events; roster atoms inherit the
            // parent's span, so their time columns stay blank.
            if (!node.IsAtom)
            {
                GUILayout.Label(KSPUtil.PrintDateCompact(node.StartUT, true), bodyCellLabel, GUILayout.Width(ColW_StartTime));
                GUILayout.Label(node.StartEvent ?? "", bodyCellLabel, GUILayout.Width(ColW_StartEvent));
                GUILayout.Label(node.EndEvent ?? "", bodyCellLabel, GUILayout.Width(ColW_EndEvent));
                GUILayout.Label(KSPUtil.PrintDateCompact(node.EndUT, true), bodyCellLabel, GUILayout.Width(ColW_EndTime));
            }
            else
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartTime));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartEvent));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndEvent));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndTime));
            }

            // Empty trailing cell so the vessel rows' right edge lines up with the Archive
            // column header above (the Archive checkbox itself lives only on the mission row).
            // This MUST be a margin-0 container (not a bodyCellLabel, which carries a 4px right
            // margin the header's margin-0 Archive cell lacks): the last cell's right margin
            // sets where the whole right-side column block sits relative to the scrollbar, so a
            // mismatch here drifts every time/event column ~4px left of its header.
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Archive));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUI.color = prevColor;
            GUILayout.EndHorizontal();
        }

        // Mission header bar: a non-modifiable index cell, the mission name (section-header
        // style, double-click to rename inline), a loop toggle + loop-period cell, then Watch,
        // Clone and Delete. Delete is disabled when this is the tree's last mission; Watch is
        // flight-only and enabled when a member ghost is watchable. Clone/Delete mutate
        // MissionStore; the draw loop iterates a snapshot so that is safe. The loop toggle goes
        // through MissionStore.SetLoopEnabled, which allows concurrent looping across trees but
        // at most one looping mission per tree (it only flips bools on same-tree siblings, never
        // adds/removes, so it is safe to call from inside the draw loop).
        private void DrawMissionHeader(Mission mission, int index, MissionThroughLineView view)
        {
            GUILayout.BeginHorizontal();

            // Index cell (first column): the per-tree number, non-modifiable. Shared by clones.
            GUILayout.Label(index > 0 ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                parentUI.GetSectionHeaderStyle(), GUILayout.Width(ColW_Index));

            DrawMissionTitleOrRename(mission);

            // "Loop [x]": the label first, then the checkbox (mirrors the recordings window's
            // "Loop" select-all header cell, which also reads label-then-toggle). A bare
            // Toggle(value, "Loop") would render "[x] Loop" instead.
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Loop));
            GUILayout.Label("Loop");
            bool loopNow = GUILayout.Toggle(mission.LoopPlayback, "");
            GUILayout.EndHorizontal();
            if (loopNow != mission.LoopPlayback)
                MissionStore.SetLoopEnabled(mission, loopNow, Planetarium.GetUniversalTime());

            DrawMissionLoopPeriodCell(mission, view);

            DrawMissionWatchButton(mission, view);

            if (GUILayout.Button("Clone", GUILayout.Width(ColW_Action)))
                MissionStore.Clone(mission);
            GUI.enabled = MissionStore.CanDelete(mission);
            if (GUILayout.Button("Delete", GUILayout.Width(ColW_Action)))
                MissionStore.Delete(mission);
            GUI.enabled = true;

            // Rightmost Archive checkbox: marks this mission for the list-hiding the Archive
            // header toggle controls. Centered in the column like the recordings window's cell.
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Archive));
            GUILayout.FlexibleSpace();
            bool archived = GUILayout.Toggle(mission.Archived, "");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (archived != mission.Archived)
            {
                mission.Archived = archived;
                ParsekLog.Info("Mission", $"Mission '{mission.Name}' archived={archived}");
            }

            GUILayout.EndHorizontal();
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

            if (GUILayout.Button(display, parentUI.GetSectionHeaderStyle(), GUILayout.ExpandWidth(true)))
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

        // Watch button: follows the whole mission (its currently-watchable member ghost), in
        // flight only. Mirrors the recordings-window Watch button but mission-scoped - it picks
        // the mission's live member and enters watch on it; for a looping mission the engine's
        // unit handoff then carries the camera across stages as the shared clock advances. "W*"
        // when already watching one of this mission's members.
        private void DrawMissionWatchButton(Mission mission, MissionThroughLineView view)
        {
            if (!parentUI.InFlightMode || parentUI.Flight == null)
            {
                // Keep the column width stable when not in flight (greyed placeholder).
                GUI.enabled = false;
                GUILayout.Button("Watch", GUILayout.Width(ColW_Watch));
                GUI.enabled = true;
                return;
            }

            var flight = parentUI.Flight;
            // [ERS-exempt] reason: the watch path feeds ghost-engine APIs (HasActiveGhost /
            // IsGhostOnSameBody / IsGhostWithinVisualRange / WatchedRecordingIndex /
            // EnterWatchMode) that are keyed on the RAW committed index, not the ERS index
            // (which shifts after a Re-Fly supersede). Same rationale as TimelineWindowUI.
            var committed = RecordingStore.CommittedRecordings;
            int watchTarget = ResolveMissionWatchTarget(mission, view, committed, flight,
                out bool isWatchingThisMission);

            bool canWatch = watchTarget >= 0 || isWatchingThisMission;
            GUI.enabled = canWatch;
            string label = isWatchingThisMission ? "W*" : "Watch";
            if (GUILayout.Button(label, GUILayout.Width(ColW_Watch)))
            {
                if (isWatchingThisMission)
                {
                    flight.ExitWatchMode();
                    ParsekLog.Info("Mission", $"Watch exited for mission '{mission.Name}'");
                }
                else if (watchTarget >= 0)
                {
                    flight.EnterWatchMode(watchTarget);
                    ParsekLog.Info("Mission",
                        $"Watch entered for mission '{mission.Name}' on member recording #{watchTarget}");
                }
            }
            GUI.enabled = true;
        }

        private void LogSortChanged()
        {
            ParsekLog.Verbose("UI",
                $"Missions sort column changed: {sortColumn} {(sortAscending ? "asc" : "desc")}");
        }

        // Per-tree index numbers, 1-based, assigned in committed-tree order. Every Mission of a
        // tree (the default plus any clones) shares the tree's number, so a clone reads as a copy
        // of the same indexed mission and renaming never renumbers anything.
        private static Dictionary<string, int> BuildTreeIndexMap(List<RecordingTree> trees)
        {
            var map = new Dictionary<string, int>(System.StringComparer.Ordinal);
            if (trees == null)
                return map;
            int n = 0;
            for (int i = 0; i < trees.Count; i++)
            {
                RecordingTree t = trees[i];
                if (t == null || string.IsNullOrEmpty(t.Id) || map.ContainsKey(t.Id))
                    continue;
                map[t.Id] = ++n;
            }
            return map;
        }

        // The mission span start UT = earliest root through-line start (offshoots leave later).
        private static double MissionSpanStartUT(MissionThroughLineView view)
        {
            double min = double.PositiveInfinity;
            if (view != null)
                for (int r = 0; r < view.RootHeadIds.Count; r++)
                    if (view.ByHeadId.TryGetValue(view.RootHeadIds[r], out MissionThroughLine tl)
                        && tl.StartUT < min)
                        min = tl.StartUT;
            return double.IsInfinity(min) ? 0.0 : min;
        }

        // The mission span in seconds = (max EndUT - min StartUT) over the COMMITTED member
        // recordings of the included through-lines, computed exactly the way
        // MissionLoopUnitBuilder derives the span it caps the overlap cadence to (only members
        // present in CommittedRecordings count, by RecordingId). Used only for the period cell's
        // effective-cadence display, so the shown value matches the cadence actually running.
        // Returns 0 when no committed member is included (no overlap; cell shows the raw period).
        private static double MissionSpanSeconds(
            MissionThroughLineView view, Mission mission, IReadOnlyList<Recording> committed)
        {
            if (view == null || committed == null)
                return 0.0;

            HashSet<string> included = MissionSelection.ComputeIncludedHeadIds(
                view, mission.ExcludedThroughLineHeadIds);
            var memberIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (string head in included)
                if (view.ByHeadId.TryGetValue(head, out MissionThroughLine tl))
                    for (int m = 0; m < tl.MemberLegIds.Count; m++)
                        if (!string.IsNullOrEmpty(tl.MemberLegIds[m]))
                            memberIds.Add(tl.MemberLegIds[m]);

            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording rec = committed[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)
                    || !memberIds.Contains(rec.RecordingId))
                    continue;
                if (rec.StartUT < min) min = rec.StartUT;
                if (rec.EndUT > max) max = rec.EndUT;
            }
            if (double.IsInfinity(min) || double.IsInfinity(max))
                return 0.0;
            return System.Math.Max(0.0, max - min);
        }

        // Builds the display-ordered (mission, index) list for the current sort column/direction.
        // Tiebreakers fall back to the tree index then the original list position so a tree's
        // clones stay grouped together (the clone was inserted right after its source).
        private List<(Mission mission, int index)> BuildSortedMissionRows(
            IReadOnlyList<Mission> missions, List<RecordingTree> trees, Dictionary<string, int> treeIndex)
        {
            var rows = new List<(Mission mission, int index, double startUT, int origPos)>();
            for (int i = 0; i < missions.Count; i++)
            {
                Mission m = missions[i];
                if (m == null)
                    continue;
                RecordingTree tree = FindTree(trees, m.TreeId);
                if (tree == null)
                    continue;
                int idx = treeIndex.TryGetValue(m.TreeId ?? "", out int ti) ? ti : 0;
                var (_, view) = GetMissionView(tree);
                rows.Add((m, idx, MissionSpanStartUT(view), i));
            }

            rows.Sort((a, b) => CompareMissionRows(
                a.mission.Name, a.index, a.startUT, a.origPos,
                b.mission.Name, b.index, b.startUT, b.origPos,
                sortColumn, sortAscending));

            var result = new List<(Mission, int)>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
                result.Add((rows[i].mission, rows[i].index));
            return result;
        }

        // Pure mission-row sort comparison (extracted for unit testing). Primary key per column;
        // tiebreak by tree index then original list position so a tree's clones stay grouped
        // (the clone is inserted right after its source). Descending negates the whole result.
        internal static int CompareMissionRows(
            string aName, int aIndex, double aStartUT, int aOrigPos,
            string bName, int bIndex, double bStartUT, int bOrigPos,
            MissionSortColumn column, bool ascending)
        {
            int cmp;
            switch (column)
            {
                case MissionSortColumn.Name:
                    cmp = string.Compare(aName ?? "", bName ?? "",
                        System.StringComparison.OrdinalIgnoreCase);
                    if (cmp == 0) cmp = aIndex.CompareTo(bIndex);
                    break;
                case MissionSortColumn.StartTime:
                    cmp = aStartUT.CompareTo(bStartUT);
                    if (cmp == 0) cmp = aIndex.CompareTo(bIndex);
                    break;
                default: // Index
                    cmp = aIndex.CompareTo(bIndex);
                    break;
            }
            if (cmp == 0) cmp = aOrigPos.CompareTo(bOrigPos);
            return ascending ? cmp : -cmp;
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
                string old = mission.Name;
                mission.Name = newName;
                ParsekLog.Info("Mission", $"Renamed mission '{old}' -> '{newName}'");
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

        // Picks the committed recording index to watch for a mission: the first watchable member
        // (active ghost, same body, in visual range) among the mission's included through-line
        // legs. Also reports whether the currently-watched recording is one of this mission's
        // members (for the "W*" toggle / exit). Returns -1 when nothing is watchable.
        private int ResolveMissionWatchTarget(Mission mission, MissionThroughLineView view,
            IReadOnlyList<Recording> committed, ParsekFlight flight, out bool isWatchingThisMission)
        {
            isWatchingThisMission = false;
            if (committed == null || view == null || flight == null)
                return -1;

            HashSet<string> includedHeads = MissionSelection.ComputeIncludedHeadIds(
                view, mission.ExcludedThroughLineHeadIds);
            var memberIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (string head in includedHeads)
                if (view.ByHeadId.TryGetValue(head, out MissionThroughLine tl))
                    for (int m = 0; m < tl.MemberLegIds.Count; m++)
                        if (!string.IsNullOrEmpty(tl.MemberLegIds[m]))
                            memberIds.Add(tl.MemberLegIds[m]);

            int watchedIdx = flight.WatchedRecordingIndex;
            int target = -1;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording rec = committed[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)
                    || !memberIds.Contains(rec.RecordingId))
                    continue;
                if (i == watchedIdx)
                    isWatchingThisMission = true;
                if (target < 0 && flight.HasActiveGhost(i) && flight.IsGhostOnSameBody(i)
                    && flight.IsGhostWithinVisualRange(i))
                    target = i;
            }
            return target;
        }

        // Loop-period cell: a value text field plus a unit button cycling Sec/Min/Hour/
        // Auto, mirroring the recordings window's DrawLoopPeriodCell. When loop is off the
        // controls grey out (matching the recordings window). Reuses ParsekUI's shared
        // loop-time helpers so the parse/format/convert rules stay identical. Commit on
        // text change: parse via TryParseLoopInput, reject negatives, clamp below
        // MinCycleDuration (same contract as RecordingsTableUI.CommitLoopPeriodEdit).
        private void DrawMissionLoopPeriodCell(Mission mission, MissionThroughLineView view)
        {
            const float unitBtnW = 40f; // match RecordingsTableUI.DrawLoopPeriodCell
            float valueW = ColW_Period - unitBtnW - 4f;

            bool enabled = mission.LoopPlayback;
            bool auto = mission.LoopTimeUnit == LoopTimeUnit.Auto;

            string controlName = "MissionLoopPeriod_" + mission.Id;

            // The EFFECTIVE launch cadence: the requested period (manual value, or the global
            // auto-loop interval for Auto) raised by the overlap cap to keep ceil(span/cadence)
            // within MaxOverlapMissionInstances. When the period is short relative to the mission
            // span this is larger than what was typed, so the cell shows what is actually running
            // (e.g. a 10 s period on a 15-min mission runs at ~44 s). Tinted when raised.
            var settings = ParsekSettings.Current;
            double requestedSeconds = auto
                ? (settings != null ? settings.autoLoopIntervalSeconds : LoopTiming.DefaultLoopIntervalSeconds)
                : mission.LoopIntervalSeconds;
            // [ERS-exempt] reason: span uses the RAW committed list to match MissionLoopUnitBuilder
            // (which keys members by committed RecordingId), so the displayed effective cadence
            // equals what actually runs. Display-only; file allowlisted (see watch button).
            double span = MissionSpanSeconds(view, mission, RecordingStore.CommittedRecordings);
            double effectiveSeconds = span > 0
                ? GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                    requestedSeconds, span, GhostPlayback.MaxOverlapMissionInstances)
                : System.Math.Max(requestedSeconds, LoopTiming.MinCycleDuration);
            bool clamped = effectiveSeconds > requestedSeconds + 1e-6;
            // Only surface the capped value when the mission is actually looping (a cadence is
            // only "running" then); a greyed-off cell shows the configured value as typed.
            bool showEffective = clamped && enabled;

            if (auto)
            {
                // Auto mode: the value is inherited from Settings > Looping (and then capped by
                // the span). Show the EFFECTIVE cadence in the global display unit plus a unit
                // suffix (the unit button reads "auto", so the suffix gives the real unit). The
                // field is non-editable.
                var globalDisplayUnit = settings != null ? settings.AutoLoopDisplayUnit : LoopTimeUnit.Sec;
                double autoShown = showEffective ? effectiveSeconds : requestedSeconds;
                string autoText = ParsekUI.FormatLoopValue(
                        ParsekUI.ConvertFromSeconds(autoShown, globalDisplayUnit), globalDisplayUnit)
                    + ParsekUI.UnitSuffix(globalDisplayUnit);
                // Drop any stale edit buffer so re-entering a manual unit re-seeds cleanly.
                loopPeriodEditBuffers.Remove(mission.Id);
                GUI.enabled = false;
                Color prevAuto = GUI.contentColor;
                if (showEffective) GUI.contentColor = LoopPeriodClampColor;
                GUILayout.TextField(autoText, bodyCellTextFieldFlush, GUILayout.Width(valueW));
                GUI.contentColor = prevAuto;
                GUI.enabled = true;
            }
            else
            {
                // Manual mode: while editing, the field shows the raw value the player is typing;
                // when not editing it shows the EFFECTIVE (capped) cadence so the real launch
                // interval is visible. The edit buffer is always seeded from the raw stored value
                // so focusing the field starts editing the typed value, not the capped one.
                bool focused = GUI.GetNameOfFocusedControl() == controlName;
                if (!focused || !loopPeriodEditBuffers.ContainsKey(mission.Id))
                {
                    loopPeriodEditBuffers[mission.Id] = ParsekUI.FormatLoopValue(
                        ParsekUI.ConvertFromSeconds(mission.LoopIntervalSeconds, mission.LoopTimeUnit),
                        mission.LoopTimeUnit);
                }

                string fieldText = (!focused && showEffective)
                    ? ParsekUI.FormatLoopValue(
                        ParsekUI.ConvertFromSeconds(effectiveSeconds, mission.LoopTimeUnit), mission.LoopTimeUnit)
                    : loopPeriodEditBuffers[mission.Id];

                GUI.enabled = enabled;
                GUI.SetNextControlName(controlName);
                Color prevManual = GUI.contentColor;
                if (!focused && showEffective) GUI.contentColor = LoopPeriodClampColor;
                string newText = GUILayout.TextField(
                    fieldText, bodyCellTextFieldFlush, GUILayout.Width(valueW));
                GUI.contentColor = prevManual;
                // Only treat edits as edits while focused (when unfocused+clamped the field shows
                // the computed value, which must not be written back as a typed period).
                if (focused && newText != loopPeriodEditBuffers[mission.Id])
                {
                    loopPeriodEditBuffers[mission.Id] = newText;
                    CommitMissionLoopPeriod(mission, newText);
                }
                GUI.enabled = true;
            }

            GUILayout.Space(4f);
            GUI.enabled = enabled;
            if (GUILayout.Button(ParsekUI.UnitLabel(mission.LoopTimeUnit), bodyCellButtonFlush, GUILayout.Width(unitBtnW)))
            {
                mission.LoopTimeUnit = RecordingsTableUI.CycleRecordingUnit(mission.LoopTimeUnit);
                loopPeriodEditBuffers.Remove(mission.Id);
                GUIUtility.keyboardControl = 0;
                ParsekLog.Info("Mission",
                    $"Loop unit for '{mission.Name}' changed to {mission.LoopTimeUnit}");
            }
            GUI.enabled = true;
        }

        // Parses an edited loop-period value and writes it to the Mission, mirroring
        // RecordingsTableUI.CommitLoopPeriodEdit: reject negatives, clamp below
        // MinCycleDuration. Invalid text is ignored (the field keeps the typed buffer so
        // the player can finish typing). Auto unit does not commit a value.
        private static void CommitMissionLoopPeriod(Mission mission, string text)
        {
            if (mission.LoopTimeUnit == LoopTimeUnit.Auto)
                return;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (!ParsekUI.TryParseLoopInput(text, mission.LoopTimeUnit, out double parsed))
                return;
            double newSeconds = ParsekUI.ConvertToSeconds(parsed, mission.LoopTimeUnit);
            if (newSeconds < 0)
            {
                ParsekLog.Warn("Mission",
                    $"Loop period for '{mission.Name}' rejected: " +
                    $"negative value {newSeconds.ToString("F1", ic)}s");
                return;
            }
            if (newSeconds < LoopTiming.MinCycleDuration)
            {
                ParsekLog.Info("Mission",
                    $"Loop period for '{mission.Name}' clamped from " +
                    $"{newSeconds.ToString("F1", ic)}s to " +
                    $"{LoopTiming.MinCycleDuration.ToString("F1", ic)}s (MinCycleDuration)");
                newSeconds = LoopTiming.MinCycleDuration;
            }
            mission.LoopIntervalSeconds = newSeconds;
            ParsekLog.Info("Mission",
                $"Loop period for '{mission.Name}' updated to " +
                newSeconds.ToString("F1", ic) + "s");
        }

        private static string CollapseKey(Mission mission, string headId)
        {
            return mission.Id + ":" + headId;
        }

        // Column-header row: a checkbox column, a wide expanding "Vessel" column, then
        // Start time / Start event / End event / End time, all in the recordings
        // window's shared column-header style.
        private void DrawColumnHeader()
        {
            var colHdr = parentUI.GetColumnHeaderStyle();
            GUILayout.BeginHorizontal();
            // Sortable headers (click to sort, click again to flip direction), mirroring the
            // recordings window. The first (index) column, the expanding name column, and the
            // Start time column sort Missions; the per-leg event columns stay static.
            parentUI.DrawSortableHeaderCore("#", MissionSortColumn.Index,
                ref sortColumn, ref sortAscending, ColW_Index, false, LogSortChanged);
            parentUI.DrawSortableHeaderCore("Missions and vessels", MissionSortColumn.Name,
                ref sortColumn, ref sortAscending, 0f, true, LogSortChanged);
            parentUI.DrawSortableHeaderCore("Start time", MissionSortColumn.StartTime,
                ref sortColumn, ref sortAscending, ColW_StartTime, false, LogSortChanged);
            GUILayout.Label("Start event", colHdr, GUILayout.Width(ColW_StartEvent));
            GUILayout.Label("End event", colHdr, GUILayout.Width(ColW_EndEvent));
            GUILayout.Label("End time", colHdr, GUILayout.Width(ColW_EndTime));

            // Archive column header + global toggle (mirrors the recordings window): label +
            // a checkbox bound to MissionStore.HideArchived. When on, archived missions drop
            // out of the list; the per-mission Archive checkbox lives on each mission's row.
            // The dark box (colHdrCellContainerStyle) wraps the WHOLE cell, label + checkbox,
            // matching RecordingsTableUI; boldHeaderInnerLabel keeps the label unboxed inside.
            GUILayout.BeginHorizontal(colHdrCellContainerStyle, GUILayout.Width(ColW_Archive));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Archive", boldHeaderInnerLabel);
            bool newHide = GUILayout.Toggle(MissionStore.HideArchived, "");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (newHide != MissionStore.HideArchived)
            {
                MissionStore.HideArchived = newHide;
                ParsekLog.Info("UI", $"Missions Archive toggle: hideArchived={newHide}");
            }

            // Reserve the vertical-scrollbar column so the fixed header's right edge
            // aligns with the row cells' right edges (the scroll view always shows a
            // vertical scrollbar). Same trick as the recordings window header.
            float scrollbarWidth = GUI.skin.verticalScrollbar != null
                ? GUI.skin.verticalScrollbar.fixedWidth
                : 16f;
            if (scrollbarWidth <= 0f) scrollbarWidth = 16f;
            GUILayout.Space(scrollbarWidth);

            GUILayout.EndHorizontal();
        }

        // Renders one leg, then recurses into its children. A leg's children, in
        // order, are: its SequenceNextId (if non-null) followed by its
        // BranchChildIds. Mirrors how RecordingsTableUI renders a group/chain tree
        // row (SelfConnectorIndent + TreeConnector + caret + label + fixed cells).
        // The visited set both prevents re-rendering a merge child reached from a
        // second parent (it gets a reference row instead) and guards malformed cycles.
        // Renders one through-line (a collapsed continuous vessel), then recurses into
        // its offshoots (the things that left it: EVA kerbals, decoupled children,
        // forks to other vessels). The visited set guards merges/cycles.
        private int DrawThroughLine(MissionStructure s, MissionThroughLineView v, Mission mission,
            HashSet<string> includedHeads, string headId, int depth, bool isLast, bool parentExcluded,
            HashSet<string> visited)
        {
            if (headId == null || !v.ByHeadId.TryGetValue(headId, out MissionThroughLine tl))
                return 0;

            if (visited.Contains(headId))
            {
                DrawReferenceRow(tl, depth, isLast);
                return 1;
            }
            visited.Add(headId);

            bool hasChildren = tl.OffshootHeadIds.Count > 0;
            bool collapsed = collapsedLegs.Contains(CollapseKey(mission, headId));

            DrawThroughLineRow(s, mission, tl, depth, isLast, hasChildren, collapsed, parentExcluded);
            int rows = 1;

            // Unchecking a through-line drops it and everything downstream. The shared
            // MissionSelection cascade is the single definition: a head is excluded for its
            // offshoots iff it is not in the included set (= self-unchecked OR ancestor
            // excluded), which is exactly the old parentExcluded || self-unchecked rule.
            bool childExcluded = !includedHeads.Contains(headId);
            if (hasChildren && !collapsed)
            {
                for (int i = 0; i < tl.OffshootHeadIds.Count; i++)
                {
                    bool childIsLast = i == tl.OffshootHeadIds.Count - 1;
                    rows += DrawThroughLine(s, v, mission, includedHeads, tl.OffshootHeadIds[i], depth + 1, childIsLast, childExcluded, visited);
                }
            }

            return rows;
        }

        private void DrawThroughLineRow(MissionStructure s, Mission mission, MissionThroughLine tl,
            int depth, bool isLast, bool hasChildren, bool collapsed, bool parentExcluded)
        {
            GUILayout.BeginHorizontal();

            // Include checkbox, keyed by the through-line's head leg in this Mission's
            // excluded set. Disabled when an ancestor is unchecked (this through-line is
            // downstream of a dropped one).
            bool selfUnchecked = mission.ExcludedThroughLineHeadIds.Contains(tl.HeadLegId);
            bool shownChecked = !parentExcluded && !selfUnchecked;
            GUI.enabled = !parentExcluded;
            bool toggled = GUILayout.Toggle(shownChecked, "", GUILayout.Width(ColW_Index));
            GUI.enabled = true;
            if (!parentExcluded && toggled != shownChecked)
            {
                if (toggled) mission.ExcludedThroughLineHeadIds.Remove(tl.HeadLegId);
                else mission.ExcludedThroughLineHeadIds.Add(tl.HeadLegId);
            }

            Color prevColor = GUI.color;
            if (parentExcluded || selfUnchecked)
                GUI.color = DimColor;

            float indent = RecordingsTableUI.SelfConnectorIndent(depth);
            if (indent > 0f)
                GUILayout.Space(indent);

            string connector = depth > 0 ? RecordingsTableUI.TreeConnector(isLast) : "";
            string caret = hasChildren ? (collapsed ? CaretRight : CaretDown) : "";
            string vessel = string.IsNullOrEmpty(tl.VesselName) ? "(vessel)" : tl.VesselName;
            string wide = connector + caret + vessel;

            if (hasChildren)
            {
                if (GUILayout.Button(wide, bodyCellLabel, GUILayout.ExpandWidth(true)))
                {
                    string key = CollapseKey(mission, tl.HeadLegId);
                    if (collapsedLegs.Contains(key))
                        collapsedLegs.Remove(key);
                    else
                        collapsedLegs.Add(key);
                }
            }
            else
            {
                GUILayout.Label(wide, bodyCellLabel, GUILayout.ExpandWidth(true));
            }

            // Start event from the head leg, end event from the tail leg.
            MissionLeg head = s.LegsById.TryGetValue(tl.HeadLegId, out MissionLeg h) ? h : null;
            MissionLeg tail = s.LegsById.TryGetValue(tl.TailLegId, out MissionLeg t2) ? t2 : null;
            GUILayout.Label(KSPUtil.PrintDateCompact(tl.StartUT, true), bodyCellLabel, GUILayout.Width(ColW_StartTime));
            GUILayout.Label(head != null ? StartEventText(head) : "", bodyCellLabel, GUILayout.Width(ColW_StartEvent));
            GUILayout.Label(tail != null ? EndEventText(tail) : "", bodyCellLabel, GUILayout.Width(ColW_EndEvent));
            GUILayout.Label(KSPUtil.PrintDateCompact(tl.EndUT, true), bodyCellLabel, GUILayout.Width(ColW_EndTime));

            GUI.color = prevColor;
            GUILayout.EndHorizontal();
        }

        // Reference row for a through-line reached a second time (a merge): shown once,
        // not recursed into.
        private void DrawReferenceRow(MissionThroughLine tl, int depth, bool isLast)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Index));

            float indent = RecordingsTableUI.SelfConnectorIndent(depth);
            if (indent > 0f)
                GUILayout.Space(indent);

            string connector = depth > 0 ? RecordingsTableUI.TreeConnector(isLast) : "";
            string vessel = string.IsNullOrEmpty(tl.VesselName) ? "(vessel)" : tl.VesselName;
            GUILayout.Label($"{connector}{MergeArrow}merges into {vessel} (above)",
                bodyCellLabel, GUILayout.ExpandWidth(true));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartTime));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartEvent));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndEvent));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndTime));

            GUILayout.EndHorizontal();
        }

        // How a leg began. The continuing primary vessel just "Continues"; only a
        // genuinely new craft gets a start event: an EVA kerbal says "EVA", a
        // separated offshoot (probe / decoupled / broken-off child) says how it
        // separated. EVA is a kerbal's start, never a vessel's start or end.
        private static string StartEventText(MissionLeg leg)
        {
            if (leg.SequencePrevId != null)
                return "Continues";
            if (!string.IsNullOrEmpty(leg.EvaCrewName))
                return "EVA";
            if (leg.IsAnchoredOffshoot && leg.OriginBranchPointType.HasValue)
                return EventLabel(leg.OriginBranchPointType.Value);
            if (leg.IsRoot || !leg.OriginBranchPointType.HasValue)
                return "Launched";
            return "Continues";
        }

        // How a leg ended. A vessel ends with a terminal state (Landed / Destroyed /
        // Recovered / ...). If the same vessel keeps going it "Continues". A branch
        // event (EVA, decouple) is where something LEFT the vessel, not where the
        // vessel ended, so it is never shown as an end.
        private static string EndEventText(MissionLeg leg)
        {
            if (leg.TerminalStateValue.HasValue)
                return leg.TerminalStateValue.Value.ToString();
            if (leg.ContinuesAsVessel)
                return "Continues";
            return "Active";
        }

        private static string EventLabel(BranchPointType type)
        {
            switch (type)
            {
                case BranchPointType.Launch: return "Launched";
                case BranchPointType.Undock: return "Undocked";
                case BranchPointType.Dock: return "Docked";
                case BranchPointType.EVA: return "EVA";
                case BranchPointType.Board: return "Boarded";
                case BranchPointType.JointBreak: return "Decoupled";
                case BranchPointType.Breakup: return "Broke up";
                case BranchPointType.Terminal: return "Ended";
                case BranchPointType.VesselSwitchContinuation: return "Switched";
                default: return type.ToString();
            }
        }
    }
}
