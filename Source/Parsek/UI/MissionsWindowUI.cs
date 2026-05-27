using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The Missions tab content, drawn INSIDE the Recordings window (<see cref="RecordingsTableUI"/>
    /// hosts the window chrome and a two-tab bar; this class draws the body of the "Missions" tab
    /// via <see cref="DrawMissionsTabContent"/>). It reuses RecordingsTableUI's visual style and
    /// rendering helpers (column-header style, dark table-body box, tree-branch connector glyphs,
    /// expand/collapse carets, section-header bar) without inheriting it. Where the recordings tab
    /// groups individual recordings (groups -> chains -> recordings), this groups the higher
    /// mission abstraction: it lists saved <see cref="Mission"/>s (from <see cref="MissionStore"/>),
    /// and renders each tree's continuous-vessel composition over time with the offshoots that left
    /// each vessel as children. Per Mission: a name + Loop/period + Watch + Clone/Delete + Archive,
    /// and include checkboxes bound to that Mission's selection (persisted). A Mission loops as a
    /// single unit. See docs/dev/design-mission-abstractions.md.
    /// </summary>
    internal class MissionsWindowUI
    {
        private readonly ParsekUI parentUI;

        // Scroll position of the Missions tab's list. The host Recordings window owns the
        // window rect, resize, drag, and input lock; this tab only keeps its own scroll.
        private Vector2 scrollPos;

        // Collapsed through-line heads, keyed "missionId:headId" so two Missions over
        // the same tree collapse independently. Transient UI state (not persisted). The
        // include selection lives per-Mission in Mission.ExcludedThroughLineHeadIds.
        private readonly HashSet<string> collapsedLegs = new HashSet<string>();

        // Loop-period inline edit state (mirrors RecordingsTableUI's loopPeriodFocusedRi /
        // loopPeriodEditText / loopPeriodEditRect). At most one period field is being edited at a
        // time, so a single set of fields suffices. The value is held in the buffer while the
        // player types and COMMITTED on Enter or click-away (a window-level MouseDown outside the
        // field rect) - NOT on every keystroke, so it does not depend on per-frame keyboard-focus
        // detection staying stable (which the old commit-on-type path relied on and which broke
        // when the header layout changed). loopPeriodFocusedMissionId == null means not editing.
        private string loopPeriodFocusedMissionId;
        private string loopPeriodEditText = "";
        private Rect loopPeriodEditRect;

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

        // Per-frame cache of the REAL Mission LoopUnitSet (the SAME one the scene drivers build via
        // MissionLoopUnitBuilder.Build with FlightGlobalsBodyInfo.Instance), so the T- countdown
        // points to the engine's ACTUAL next relaunch (PhaseAnchorUT + n*relaunchCadence) instead of
        // the next faithful P-window (which the engine SKIPS when the relaunch cadence is a multiple
        // m*P with m>=2). Built at most once per frame (keyed by Time.frameCount, like the view cache
        // above) and read-only: this is a DISPLAY mirror of the engine's schedule and never feeds the
        // engine/scene drivers, so it cannot affect playback. Lazily computed by GetLoopUnitSet.
        private GhostPlaybackLogic.LoopUnitSet loopUnitSetCache;
        private int loopUnitSetCacheFrame = -1;

        // Fixed-width columns to the right of the expanding "Missions and vessels" column,
        // mirroring the recordings window's fixed-width cells so the window reads as the
        // same table style. Layout: [index/check] Missions and vessels | Start time |
        // Start event | End event | End time. The first column doubles as the mission
        // index cell (on header bars) and the include checkbox (on through-line rows).
        // ColW_Enable mirrors the recordings tab's leading enable-toggle column (which the Missions
        // tab has no equivalent for): a blank cell of this width precedes the index/checkbox in
        // every Missions row, and the header's "#" cell is the same merged [enable+index] width, so
        // the "Missions and vessels" column lines up with the recordings tab's "Name" column.
        private const float ColW_Enable = 20f;
        private const float ColW_Index = 30f;
        private const float ColW_StartTime = 120f;
        private const float ColW_StartEvent = 85f;
        private const float ColW_EndEvent = 85f;
        private const float ColW_EndTime = 120f;
        // Uniform width for the mission-header-bar buttons (Clone, Delete, Watch, Rewind/Forward):
        // the old Clone width (60) + 10 px, so they all read as one button group.
        private const float ColW_HeaderButton = 70f;
        // Width of the mission-header bar's right-side control block (Clone..Rewind + Archive). It
        // equals the data columns' total footprint (the 7 cells right of "Missions and vessels" plus
        // their 6 inter-cell margins), so the expanding title fills exactly the same width as the
        // data rows' name column and the buttons begin at the data-column boundary (where "Start
        // time" starts) instead of being pushed to the far right.
        private const float MissionHeaderRightBlockWidth =
            ColW_StartTime + ColW_StartEvent + ColW_EndEvent + ColW_EndTime + ColW_TMinus + ColW_ReFly + ColW_Archive + 6 * 4f;
        // Re-Fly column (mirrors the recordings window's Re-Fly/Fly-Seal column width): a per-vessel
        // Fly / Seal cell for unfinished-flight recordings, drawn by reusing RecordingsTableUI.
        private const float ColW_ReFly = 90f;
        // "T- to launch" column (mission periodicity, design doc UX): a live countdown to the next
        // faithful launch window, shown ONLY on the per-mission header bar (the vessel rows leave a
        // blank cell of this width so the data columns stay aligned). Sits between "End time" and
        // "Re-Fly" in the header, and is drawn on the mission bar right after the period cell.
        private const float ColW_TMinus = 90f;
        // Fixed column-header height so every Missions header cell is the same height (matches
        // RecordingsTableUI.ColHeaderHeight); the toggle-bearing Archive cell would otherwise be
        // taller than the plain-label cells.
        private const float ColHeaderHeight = 32f;
        // Minimum height for each composition (vessel) row, matching the recordings table's per-row
        // stride (RecordingsTableUI scrolls at 22 px/row). The composition cells are mostly plain
        // labels, which alone measure shorter than a recordings row; without this floor the rows
        // pack too tightly and leave no room for the per-row Fly / Seal button.
        private const float CompositionRowMinHeight = 22f;
        // Rightmost Archive column (mirrors the recordings window's Archive/Hide column): the
        // header carries the global "hide archived" toggle, each mission row a per-mission check.
        // Width matches RecordingsTableUI.ColW_Hide (80) so the column reads the same on both tabs.
        private const float ColW_Archive = 80f;

        // Mission-header loop-period cell width (lives on the header row, not the table columns).
        // The "Loop" label + checkbox are emitted as bare siblings (no fixed width), so the only
        // sized loop control here is the period cell.
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
        // Composition (vessel) row cell label: same as bodyCellLabel but vertically centered and
        // stretched to the row height, so the name + time text sit centered in the (MinHeight'd)
        // row instead of floating at the top - matching how the recordings rows read (their cells
        // fill the row height via the row's buttons). The include checkbox is centered separately
        // (ExpandHeight on the toggle).
        private GUIStyle compositionCellLabel;
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
        // Mission-header ROW bubble: the dark section-header box used as the background of the
        // WHOLE mission header row (index, title, Loop, period, Watch, Clone, Delete, Archive),
        // so the bubble spans the full row width with every control sitting on it. Cloned from
        // the shared section-header box with left/right margin + padding zeroed so the bubble
        // reaches the row edges and the contents are not inset (keeps the Archive checkbox under
        // its column header). The index + title use missionHeaderTextStyle (bold transparent
        // text) instead of their own boxes so they don't double-box on top of the bubble.
        private GUIStyle missionHeaderRowStyle;
        private GUIStyle missionHeaderTextStyle;

        // Column-header text inset (matches RecordingsTableUI.BodyCellTextIndent so
        // body labels land under their header text).
        private const int BodyCellTextIndent = 5;

        // -- Caret glyphs (built from char codes so the source stays ASCII, the
        // same way RecordingsTableUI builds its TreeConnector glyphs) --
        // U+25BC down caret (expanded), U+25B6 right caret (collapsed).
        private static readonly string CaretDown =
            new string(new[] { (char)0x25BC, ' ' });
        private static readonly string CaretRight =
            new string(new[] { (char)0x25B6, ' ' });

        public MissionsWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
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

            // Composition-row cell: same padding, but vertically centered + stretched to the row
            // height so the text sits centered in the MinHeight'd row (not floating at the top).
            compositionCellLabel = new GUIStyle(bodyCellLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                stretchHeight = true
            };

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

            // Mission-header row bubble: the section-header box stretched across the whole row.
            // L/R margin + padding are zeroed so the dark bar reaches the row edges and its contents
            // are not inset (the Archive checkbox stays under its column header). A small bottom
            // padding gives the title text breathing room under it (the box's own border supplies the
            // top space); without it the title sat too close to the bubble's bottom edge.
            var sectionHeader = parentUI.GetSectionHeaderStyle();
            missionHeaderRowStyle = new GUIStyle(sectionHeader)
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 4)
            };

            // Bold transparent text for the index + title sitting on the row bubble (no box of
            // their own, so they don't double-box over the bubble background).
            missionHeaderTextStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
        }

        /// <summary>
        /// Draws the Missions tab's body inside the host Recordings window. The host
        /// (<see cref="RecordingsTableUI"/>) supplies the window chrome (title, tab bar,
        /// breathing-room space, bottom Close button, resize handle, drag, and input lock);
        /// this method only draws the column header + the scrollable mission list, mirroring
        /// the recordings tab's own header-plus-scroll structure (no outer vertical, no Close,
        /// no resize/drag of its own).
        /// </summary>
        internal void DrawMissionsTabContent()
        {
            EnsureStyles();

            // Click outside an active rename field -> commit (mirrors the recordings
            // window's defocus handling).
            if (Event.current.type == EventType.MouseDown && renamingMissionId != null
                && activeMissionRenameRect.width > 0
                && !activeMissionRenameRect.Contains(Event.current.mousePosition))
            {
                CommitMissionRenameById(renamingMissionId);
            }

            // Click outside an active loop-period field -> commit (mirrors RecordingsTableUI's
            // window-level loop-period commit). Uses the field rect captured last frame; runs
            // before the cells are drawn so the in-progress buffer is committed on click-away.
            if (Event.current.type == EventType.MouseDown && loopPeriodFocusedMissionId != null
                && !loopPeriodEditRect.Contains(Event.current.mousePosition))
            {
                CommitMissionLoopPeriodEdit(FindMissionById(loopPeriodFocusedMissionId));
            }

            var trees = RecordingStore.CommittedTrees;
            MissionStore.EnsureDefaultsForTrees(trees);
            var missions = MissionStore.Missions;
            if (missions == null || missions.Count == 0)
            {
                GUILayout.Label("No missions recorded yet.");
                return;
            }

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
                // Archive: when the Archive toggle is on, archived missions drop out of the
                // list (mirrors the recordings tab hiding Hidden rows). Their loop / ghost
                // state is untouched; un-archive or toggle off to see them again.
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

            ParsekLog.VerboseRateLimited("UI", "missions-tab-draw",
                $"Missions tab: missions={missionCount} rows={rowCount}", 5.0);
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

        // Returns the REAL Mission LoopUnitSet, built at most once per frame, EXACTLY as the scene
        // drivers build it (MissionLoopUnitBuilder.Build over MissionStore.Missions +
        // RecordingStore.CommittedTrees/CommittedRecordings, with the global auto-loop interval and
        // FlightGlobalsBodyInfo.Instance). The unit the engine actually relaunches on is read off
        // this set so the T- countdown is drift-free against playback. Display-only: never pushed to
        // the engine. Build's per-build Verbose summary is suppressed (the UI runs every frame).
        // [ERS-exempt] reason: this reads the RAW committed list to match MissionLoopUnitBuilder
        // (which keys loop members by committed RecordingId); display-only, file allowlisted.
        private GhostPlaybackLogic.LoopUnitSet GetLoopUnitSet()
        {
            int frame = Time.frameCount;
            if (frame != loopUnitSetCacheFrame || loopUnitSetCache == null)
            {
                var settings = ParsekSettings.Current;
                double autoLoopIntervalSeconds = settings != null
                    ? settings.autoLoopIntervalSeconds
                    : LoopTiming.DefaultLoopIntervalSeconds;
                bool prevSuppress = MissionLoopUnitBuilder.SuppressLogging;
                MissionLoopUnitBuilder.SuppressLogging = true;
                try
                {
                    loopUnitSetCache = MissionLoopUnitBuilder.Build(
                        MissionStore.Missions, RecordingStore.CommittedTrees,
                        RecordingStore.CommittedRecordings, autoLoopIntervalSeconds,
                        FlightGlobalsBodyInfo.Instance);
                }
                finally
                {
                    MissionLoopUnitBuilder.SuppressLogging = prevSuppress;
                }
                loopUnitSetCacheFrame = frame;
            }
            return loopUnitSetCache ?? GhostPlaybackLogic.LoopUnitSet.Empty;
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
            // MinHeight floors the row at the recordings-table row stride so the rows do not pack
            // too tightly and the per-row Fly / Seal button has room (label-only cells alone measure
            // shorter than a recordings row).
            GUILayout.BeginHorizontal(GUILayout.MinHeight(CompositionRowMinHeight));

            // Blank enable slot (no per-row enable in missions) so the first column totals the
            // recordings tab's [enable+index] width and the vessel name lines up with the
            // recordings "Name" column. The include checkbox then sits in the index slot, under "#".
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Enable));

            // Include checkbox on every interval / branch (no cascade - unchecking drops just this
            // segment); a blank cell on roster atoms.
            if (selectable)
            {
                bool shownChecked = !selfExcluded;
                // ExpandHeight so the checkbox fills the row height and the toggle style centers it
                // vertically (matching the vertically-centered name text), rather than top-aligning.
                bool toggled = GUILayout.Toggle(shownChecked, "",
                    GUILayout.Width(ColW_Index), GUILayout.ExpandHeight(true));
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
                if (GUILayout.Button(wide, compositionCellLabel, GUILayout.ExpandWidth(true)))
                {
                    string key = CollapseKey(mission, node.HeadLegId);
                    if (collapsedLegs.Contains(key)) collapsedLegs.Remove(key);
                    else collapsedLegs.Add(key);
                }
            }
            else
            {
                GUILayout.Label(wide, compositionCellLabel, GUILayout.ExpandWidth(true));
            }

            // Interval / vessel rows show their span + bounding events; roster atoms inherit the
            // parent's span, so their time columns stay blank.
            if (!node.IsAtom)
            {
                GUILayout.Label(KSPUtil.PrintDateCompact(node.StartUT, true), compositionCellLabel, GUILayout.Width(ColW_StartTime));
                GUILayout.Label(node.StartEvent ?? "", compositionCellLabel, GUILayout.Width(ColW_StartEvent));
                GUILayout.Label(node.EndEvent ?? "", compositionCellLabel, GUILayout.Width(ColW_EndEvent));
                GUILayout.Label(KSPUtil.PrintDateCompact(node.EndUT, true), compositionCellLabel, GUILayout.Width(ColW_EndTime));
            }
            else
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartTime));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartEvent));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndEvent));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndTime));
            }

            // Blank "T- to launch" cell on vessel rows: the T- countdown is a per-mission value, so
            // it lives only on the mission header bar; the vessel rows leave a same-width blank cell
            // so the Re-Fly + Archive columns stay aligned under their headers. A bodyCellLabel is
            // fine here (the margin-0 caveat only applies to the right-EDGE cell, the trailing
            // Archive cell below; this cell is followed by the Re-Fly cell).
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_TMinus));

            // Restore the normal colour before the actionable Re-Fly cell + the trailing Archive
            // cell, so an excluded (greyed) interval's Fly / Seal buttons are not dimmed (the
            // recording's re-fly state is independent of whether the interval is looped).
            GUI.color = prevColor;

            // Re-Fly cell (per-vessel Fly / Seal for unfinished-flight recordings), left of Archive.
            // Only real (non-atom) rows map to a recording; resolve node.HeadLegId to its committed
            // recording and reuse the recordings tab's Re-Fly cell (it shows Fly / Seal only when the
            // recording is an unfinished flight, otherwise a blank cell). Atoms + unresolved rows get
            // a blank ColW_ReFly cell so the Archive column stays aligned.
            if (!node.IsAtom
                && TryResolveCommittedRecording(node.HeadLegId, out int reFlyIdx, out Recording reFlyRec))
            {
                parentUI.GetRecordingsTableUI().DrawReFlyColumnCell(
                    reFlyRec, reFlyIdx, Planetarium.GetUniversalTime());
            }
            else
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_ReFly));
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

            GUILayout.EndHorizontal();
        }

        // Linear lookup of a committed recording by id (the Missions tab maps a composition row to
        // its recording for the Re-Fly cell). Returns false when the id is empty or not committed.
        // [ERS-exempt] reason: Re-Fly is keyed on the RAW committed index, same as the watch /
        // rewind buttons (which shift under an ERS supersede); MissionsWindowUI.cs is allowlisted.
        private static bool TryResolveCommittedRecording(
            string recordingId, out int index, out Recording rec)
        {
            index = -1;
            rec = null;
            if (string.IsNullOrEmpty(recordingId))
                return false;
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording r = committed[i];
                if (r != null && r.RecordingId == recordingId)
                {
                    index = i;
                    rec = r;
                    return true;
                }
            }
            return false;
        }

        // Mission header bar: a dark section-header bubble spanning the WHOLE row (index cell,
        // the mission name, a loop toggle + loop-period cell, then Watch, Clone, Delete, and the
        // Archive checkbox all sit on it). Delete is disabled when this is the tree's last
        // mission; Watch is flight-only and enabled when a member ghost is watchable. Clone/Delete
        // mutate MissionStore; the draw loop iterates a snapshot so that is safe. The loop toggle
        // goes through MissionStore.SetLoopEnabled, which allows concurrent looping across trees
        // but at most one looping mission per tree (it only flips bools on same-tree siblings,
        // never adds/removes, so it is safe to call from inside the draw loop).
        private void DrawMissionHeader(Mission mission, int index, MissionThroughLineView view)
        {
            // The whole row's background is the dark section-header bubble (missionHeaderRowStyle),
            // so the bubble spans index -> Archive and every control sits inside it.
            GUILayout.BeginHorizontal(missionHeaderRowStyle);

            // First column = blank enable slot + the index number, totalling the recordings tab's
            // [enable+index] width so the title lines up with the recordings "Name" column.
            GUILayout.Label("", missionHeaderTextStyle, GUILayout.Width(ColW_Enable));
            // Index cell: the per-tree number, non-modifiable. Shared by clones. Bold transparent
            // text on the row bubble (no box of its own).
            GUILayout.Label(index > 0 ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                missionHeaderTextStyle, GUILayout.Width(ColW_Index));

            // Small left inset on the title so its text starts at the same x as the "Missions and
            // vessels" column header text (the header box insets its label; the bare title label
            // does not), then the title expands to fill the name column.
            GUILayout.Space(BodyCellTextIndent);
            DrawMissionTitleOrRename(mission);

            // Right-side control block, a FIXED width equal to the data columns' footprint, so the
            // expanding title above fills exactly the data rows' name-column width and the buttons
            // begin at the data-column boundary (just right of where "Missions and vessels" ends)
            // instead of being shoved to the far right. Inside: the buttons left-aligned, then a
            // FlexibleSpace, then the Archive checkbox pinned to the right (under the Archive header).
            GUILayout.BeginHorizontal(GUILayout.Width(MissionHeaderRightBlockWidth));

            // Clone / Delete first. Delete is disabled when this is the tree's last mission. Clone,
            // Delete, Watch, and Rewind/Forward all share ColW_HeaderButton so they read as one group.
            if (GUILayout.Button("Clone", GUILayout.Width(ColW_HeaderButton)))
                MissionStore.Clone(mission);
            GUI.enabled = MissionStore.CanDelete(mission);
            if (GUILayout.Button("Delete", GUILayout.Width(ColW_HeaderButton)))
                MissionStore.Delete(mission);
            GUI.enabled = true;

            // "Loop [x]": label then checkbox (bare siblings, normal ~4 px margins; a fixed-width
            // wrapper left slack that widened the gap before the period field).
            GUILayout.Label("Loop");
            bool loopNow = GUILayout.Toggle(mission.LoopPlayback, "");
            if (loopNow != mission.LoopPlayback)
            {
                MissionStore.SetLoopEnabled(mission, loopNow, Planetarium.GetUniversalTime());
                // Turning loop off disables the period field; end any in-progress edit on it.
                if (!loopNow && loopPeriodFocusedMissionId == mission.Id)
                    loopPeriodFocusedMissionId = null;
            }

            // Mission periodicity (Phase-1 / Tier-1 solution), computed ONCE per mission here and
            // shared by the period cell + the T- cell so we extract/solve only once per frame per
            // mission (and log one rate-limited summary, not the per-call Verbose lines). Only
            // looping missions need it; a non-looping mission gets the no-solution default.
            MissionPeriodicityDisplay periodicity = mission.LoopPlayback
                ? ComputeMissionPeriodicity(mission, view)
                : default;

            DrawMissionLoopPeriodCell(mission, view, periodicity);

            // "T- to launch" cell: the live countdown to the next faithful launch window (mission
            // periodicity, design doc UX), sitting right after the period cell. Shows the countdown
            // / continuous / not-aligned state. Non-looping missions show a blank cell of the same
            // width.
            DrawMissionTMinusCell(mission, periodicity);

            DrawMissionWatchButton(mission, view);

            // Rewind / Forward button (right of Watch): a plain fixed-width button (matching the
            // other header buttons) labelled "Rewind" / "Forward", scoped to the mission's root
            // (launch) recording, so it rewinds the game to the mission's launch (or fast-forwards
            // to it when the launch is still in the future). Reuses the recordings-tab rewind/forward
            // decision + confirmation logic via DrawMissionRewindForwardButton.
            // [ERS-exempt] reason: the rewind/forward path is keyed on the RAW committed index (it
            // takes a committed index + recording and resolves the rewind owner / save by
            // identity), not the ERS index; same rationale as the watch button above.
            var rewindCommitted = RecordingStore.CommittedRecordings;
            int rootIdx = ResolveMissionRootRecordingIndex(view, rewindCommitted);
            parentUI.GetRecordingsTableUI().DrawMissionRewindForwardButton(
                rootIdx >= 0 ? rewindCommitted[rootIdx] : null,
                rootIdx, Planetarium.GetUniversalTime(), parentUI.Flight, ColW_HeaderButton);

            // FlexibleSpace fills the gap between the buttons and the right-pinned Archive checkbox
            // (the buttons are narrower than the data-column block they sit in).
            GUILayout.FlexibleSpace();

            // Rightmost Archive checkbox: marks this mission for the list-hiding the Archive header
            // toggle controls. Centered in the column like the recordings window's cell, and under
            // the Archive column header.
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

            // Bold transparent text on the row bubble (clickable for rename); no box of its own,
            // since the row's missionHeaderRowStyle already supplies the dark background.
            if (GUILayout.Button(display, missionHeaderTextStyle, GUILayout.ExpandWidth(true)))
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
                GUILayout.Button("Watch", GUILayout.Width(ColW_HeaderButton));
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
            if (GUILayout.Button(label, GUILayout.Width(ColW_HeaderButton)))
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

        // ===================== Mission periodicity display (Phase 3 UI) =====================

        // The computed Phase-1 periodicity solution for one looping mission, plus the dominant
        // constraint's kind/body (for the period-cell basis label). Computed once per mission per
        // frame by ComputeMissionPeriodicity and shared by the period cell + the T- cell. The
        // default value (Solved == false) means "not computed / not looping" -> blank cells.
        private struct MissionPeriodicityDisplay
        {
            public bool Solved;                  // false on the default (not looping / no data)
            public PeriodicitySolution Solution;
            public double NowUT;                 // the reference UT used for the live countdown
            public ConstraintKind DominantKind;  // valid only when IsPhaseLockedConstrained
            public string DominantBodyName;      // valid only when IsPhaseLockedConstrained

            // The engine's ACTUAL next relaunch UT for this mission, read off the REAL LoopUnitSet
            // (PhaseAnchorUT + n*relaunchCadence). This is what the T- countdown targets - NOT the
            // periodicity solution's NextWindowUT (the next faithful P-window), which the engine
            // SKIPS whenever the relaunch cadence is a multiple m*P with m>=2 (the recording span or
            // the user period exceeds P). The two coincide only when the relaunch cadence == P.
            // NaN when no real unit was built for this mission (see UnitBuilt).
            public double NextRelaunchUT;

            // True when MissionLoopUnitBuilder built a real unit for this mission (the mission maps
            // to at least one committed loop member). When false the engine relaunches nothing, so
            // the T- cell reads "not aligned" - the same word an unsupported config gets, since both
            // mean "this mission is not on a faithful launch schedule".
            public bool UnitBuilt;

            // Supported + constrained: the loop is phase-locked to a real period P (not the free
            // MinCycleDuration). This is the state where the period cell shows P + basis label and
            // the T- cell shows a live countdown.
            public bool IsPhaseLockedConstrained =>
                Solved && Solution.ShouldPhaseLock
                && !double.IsNaN(Solution.P) && Solution.P > LoopTiming.MinCycleDuration + 1e-6;
        }

        // Computes the Phase-1 (Tier-1) periodicity solution for one looping mission, mirroring the
        // loop builder's Extract+Solve, but referenced to the LIVE clock (Planetarium UT) so the T-
        // countdown is "the next window from NOW". Reads RecordingStore.CommittedRecordings + the
        // mission's interval trim + the live bodies via FlightGlobalsBodyInfo. The per-call Verbose
        // summaries in ExtractConstraints/Solve are suppressed (the UI runs every frame); a single
        // rate-limited summary is logged here instead.
        // [ERS-exempt] reason: this reads the RAW committed list to match MissionLoopUnitBuilder
        // (which keys loop members by committed RecordingId); display-only, file allowlisted.
        private MissionPeriodicityDisplay ComputeMissionPeriodicity(
            Mission mission, MissionThroughLineView view)
        {
            var result = new MissionPeriodicityDisplay { Solved = false };
            if (mission == null || view == null)
                return result;
            RecordingTree tree = FindTree(RecordingStore.CommittedTrees, mission.TreeId);
            if (tree == null)
                return result;

            var committed = RecordingStore.CommittedRecordings;
            var compRoots = GetCompositionRoots(tree);
            double nowUT = Planetarium.GetUniversalTime();

            // Suppress the per-call Verbose lines (every frame) and emit one rate-limited summary.
            bool prevSuppress = MissionPeriodicity.SuppressLogging;
            MissionPeriodicity.SuppressLogging = true;
            ConstraintExtraction extraction;
            PeriodicitySolution solution;
            try
            {
                extraction = MissionPeriodicity.ExtractConstraints(
                    view, compRoots, committed, mission.ExcludedIntervalKeys,
                    FlightGlobalsBodyInfo.Instance);
                solution = MissionPeriodicity.Solve(
                    extraction.Constraints, extraction.Support, extraction.UT0, nowUT,
                    FlightGlobalsBodyInfo.Instance);
            }
            finally
            {
                MissionPeriodicity.SuppressLogging = prevSuppress;
            }

            result.Solved = true;
            result.Solution = solution;
            result.NowUT = nowUT;

            // Dominant constraint (kind/body) for the period-cell basis label, picked the same way
            // the solver picks the constraint whose period sets P.
            if (solution.ShouldPhaseLock && extraction.Constraints != null
                && extraction.Constraints.Count > 0)
            {
                int di = MissionPeriodicity.SelectDominantConstraintIndex(extraction.Constraints);
                PhaseConstraint dom = extraction.Constraints[di];
                result.DominantKind = dom.Kind;
                result.DominantBodyName = dom.BodyName;
            }

            // The ENGINE's actual next relaunch UT for this mission. The countdown must point here,
            // NOT at solution.NextWindowUT: the engine relaunches every effectiveOverlapCadence /
            // effectiveCadence (a MULTIPLE m*P of the period when the span / user period exceeds P),
            // so it SKIPS the in-between P-windows. Read the REAL unit (the same one the scene
            // drivers build) for this mission and derive the next relaunch from its schedule, so the
            // displayed countdown can never drift away from when a ghost actually launches.
            result.UnitBuilt = TryResolveMissionUnit(
                tree, committed, mission.ExcludedIntervalKeys, out GhostPlaybackLogic.LoopUnit unit);
            result.NextRelaunchUT = result.UnitBuilt
                ? ComputeNextRelaunchUT(unit, nowUT)
                : double.NaN;

            ParsekLog.VerboseRateLimited("MissionPeriodicity", "missions-ui-solve",
                $"Missions UI: mission='{mission.Name}' tree={tree.Id} " +
                $"support={solution.Support} P={solution.P.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"lock={(solution.ShouldPhaseLock ? "yes" : "no")} " +
                $"nextWindow={solution.NextWindowUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"unitBuilt={(result.UnitBuilt ? "yes" : "no")} " +
                $"nextRelaunch={result.NextRelaunchUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"now={nowUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}",
                3.0);

            return result;
        }

        // Resolves the REAL LoopUnit the engine built for this mission, by mapping the mission's
        // trimmed loop members to committed indices (the SAME ComputeTrimmedMemberWindows the builder
        // keys members on) and looking any one of them up in the per-frame LoopUnitSet. Returns false
        // (unit = default) when the mission has no committed members or no unit was built for it (an
        // unsupported config / phase-lock skip still builds a unit; "no unit" means the mission maps
        // to no live loop member at all - e.g. every member was trimmed off). Display-only.
        // [ERS-exempt] reason: maps members via the RAW committed list to match MissionLoopUnitBuilder
        // (keyed by committed RecordingId); display-only, file allowlisted.
        private bool TryResolveMissionUnit(
            RecordingTree tree, IReadOnlyList<Recording> committed,
            ICollection<string> excludedIntervalKeys, out GhostPlaybackLogic.LoopUnit unit)
        {
            unit = default;
            if (tree == null || committed == null)
                return false;

            var (_, view) = GetMissionView(tree);
            var windows = MissionLoopUnitBuilder.ComputeTrimmedMemberWindows(
                view, GetCompositionRoots(tree), committed, excludedIntervalKeys,
                null, out _, out _);

            GhostPlaybackLogic.LoopUnitSet units = GetLoopUnitSet();
            foreach (int idx in windows.Keys)
                if (units.TryGetUnitForMember(idx, out unit))
                    return true;
            return false;
        }

        // Draws the "T- to launch" cell on the mission header bar (design doc UX): a live countdown
        // to the engine's ACTUAL next relaunch, or one of the state words (continuous / not aligned).
        // The countdown reads NextRelaunchUT - now (PhaseAnchorUT + n*relaunchCadence off the REAL
        // loop unit), NOT the periodicity solution's next P-window, so it never ticks to "T- 0s" on a
        // window the engine skips (relaunch cadence = m*P with m>=2).
        private void DrawMissionTMinusCell(Mission mission, MissionPeriodicityDisplay periodicity)
        {
            string text = BuildTMinusCellText(
                mission != null && mission.LoopPlayback,
                periodicity.Solved,
                periodicity.Solution.ShouldPhaseLock,
                periodicity.UnitBuilt,
                periodicity.Solution.P,
                periodicity.NextRelaunchUT,
                periodicity.NowUT);

            // Tint a live countdown amber when the best-effort window misses its physics tolerance
            // (over-constrained config - the user may want to re-trim), matching the design's
            // green/amber readout intent. Continuous / not-aligned / blank states read plain.
            bool amber = periodicity.IsPhaseLockedConstrained && !periodicity.Solution.WithinTolerance;
            Color prev = GUI.contentColor;
            if (amber)
                GUI.contentColor = LoopPeriodClampColor;
            GUILayout.Label(text, missionHeaderTextStyle, GUILayout.Width(ColW_TMinus));
            GUI.contentColor = prev;
        }

        // ----- Pure display helpers (unit-tested; the IMGUI layout above is playtest-verified) -----

        /// <summary>
        /// The engine's ACTUAL next relaunch UT for a built loop unit, derived from the SAME schedule
        /// <see cref="MissionLoopUnitBuilder.TryBuildMissionUnit"/> produces, so the T- countdown can
        /// never drift away from when a ghost really launches. The engine relaunches the whole mission
        /// every <see cref="GhostPlaybackLogic.LoopUnit.OverlapCadenceSeconds"/> when that overlaps the
        /// span (it is &lt; the span duration), else once per <see cref="GhostPlaybackLogic.LoopUnit.CadenceSeconds"/>
        /// (the single span instance). Either cadence is already quantized to a multiple m*P of the
        /// faithful period P, so the relaunches land on every m-th P-window - the in-between windows
        /// are SKIPPED. Next relaunch from now = <c>PhaseAnchorUT + n * interval</c> with
        /// <c>n = max(0, ceil((now - PhaseAnchorUT) / interval))</c> (n == 0 while the loop is still
        /// parked before its forward-snapped anchor). Pure; a degenerate interval (&lt;= 0 / NaN /
        /// infinity) falls back to the anchor itself.
        /// </summary>
        internal static double ComputeNextRelaunchUT(GhostPlaybackLogic.LoopUnit unit, double nowUT)
        {
            double anchor = unit.PhaseAnchorUT;
            if (double.IsNaN(anchor) || double.IsInfinity(anchor))
                return double.NaN;

            double span = unit.SpanEndUT - unit.SpanStartUT;
            // The engine overlaps the whole mission with itself when the overlap cadence is shorter
            // than the span (relaunch on the overlap cadence); otherwise there is a single span
            // instance that relaunches once per span-clock cadence. Mirror that exact choice.
            double interval = unit.OverlapCadenceSeconds < span
                ? unit.OverlapCadenceSeconds
                : unit.CadenceSeconds;
            if (double.IsNaN(interval) || double.IsInfinity(interval) || interval <= 0.0)
                return anchor; // degenerate cadence: the anchor is the only launch we can name

            // n = ceil((now - anchor) / interval), clamped at 0 so a future-snapped anchor (loop
            // parked, now < anchor) reports the anchor itself rather than a negative cycle.
            double n = System.Math.Ceiling((nowUT - anchor) / interval - 1e-9);
            if (n < 0.0)
                n = 0.0;
            double next = anchor + n * interval;
            // Floating-point guard: keep the result at or after now (and not a whole interval past).
            if (next < nowUT - 1e-6)
                next += interval;
            return next;
        }

        /// <summary>
        /// The "T- to launch" cell text for the four states (design doc UX):
        /// - not looping / not solved -> "" (blank);
        /// - unsupported (cross-parent / rendezvous; the no-lock sentinel, ShouldPhaseLock==false)
        ///   OR no engine unit built for this mission -> "not aligned";
        /// - unconstrained (P == MinCycleDuration) -> "continuous";
        /// - supported + constrained -> "T-" + a compact countdown to the ENGINE's next relaunch.
        /// The countdown targets <paramref name="nextRelaunchUT"/> (the engine's actual next
        /// relaunch, PhaseAnchorUT + n*relaunchCadence), NOT the periodicity solution's next faithful
        /// P-window: when the relaunch cadence is m*P (m&gt;=2) the engine launches only every m-th
        /// window, so a P-window countdown would tick to "T- 0s" with no launch. Pure (no Unity).
        /// </summary>
        internal static string BuildTMinusCellText(
            bool looping, bool solved, bool shouldPhaseLock, bool unitBuilt,
            double p, double nextRelaunchUT, double nowUT)
        {
            if (!looping || !solved)
                return "";
            if (!shouldPhaseLock || !unitBuilt)
                // Unsupported (cross-parent / rendezvous, the no-lock sentinel) or no live loop
                // member maps to a unit: this mission is not on a faithful launch schedule.
                return "not aligned";
            if (double.IsNaN(p) || p <= LoopTiming.MinCycleDuration + 1e-6)
                return "continuous";    // unconstrained free loop (nothing to line up)
            if (double.IsNaN(nextRelaunchUT))
                return "continuous";    // defensive: a locked P with no relaunch resolves as free
            double delta = nextRelaunchUT - nowUT;
            if (delta < 0.0)
                delta = 0.0;            // relaunch is at/behind now -> launching now
            return "T- " + FormatCountdownCompact(delta);
        }

        /// <summary>
        /// Compact top-two-units duration: "12m 30s", "2h 14m", "3d 5h", "1y 42d", or "0s" / "5s"
        /// for sub-minute. Respects the player's Kerbin/Earth day length (via ParsekTimeFormat).
        /// Pure; negative / NaN / infinity clamp to 0. This is the NEW formatter the T- column uses
        /// (kept separate from ParsekTimeFormat.FormatCountdown, which prints ALL components with a
        /// T-/T+ prefix - here we want a bare, compact two-unit value).
        /// </summary>
        internal static string FormatCountdownCompact(double seconds)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
                seconds = 0.0;
            long total = (long)seconds;
            if (total < 60)
                return total.ToString(ic) + "s";
            if (total < 3600)
                return (total / 60).ToString(ic) + "m " + (total % 60).ToString(ic) + "s";

            long secsPerDay = ParsekTimeFormat.SecsPerDay;
            long secsPerYear = ParsekTimeFormat.SecsPerYear;
            if (total < secsPerDay)
                return (total / 3600).ToString(ic) + "h " + ((total % 3600) / 60).ToString(ic) + "m";
            if (total < secsPerYear)
            {
                long days = total / secsPerDay;
                long hours = (total % secsPerDay) / 3600;
                return hours > 0
                    ? days.ToString(ic) + "d " + hours.ToString(ic) + "h"
                    : days.ToString(ic) + "d";
            }
            long years = total / secsPerYear;
            long remDays = (total % secsPerYear) / secsPerDay;
            return remDays > 0
                ? years.ToString(ic) + "y " + remDays.ToString(ic) + "d"
                : years.ToString(ic) + "y";
        }

        /// <summary>
        /// A single-unit "~" approximate period: "~6h", "~1.6d", "~36m", "~9s", "~2.1y". Picks the
        /// largest unit at which the value is &gt;= 1 and shows one decimal (dropping a trailing
        /// ".0"). Respects the player's day length. Pure; non-positive / NaN -&gt; "~0s".
        /// </summary>
        internal static string FormatPeriodCompact(double seconds)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0.0)
                return "~0s";
            double secsPerDay = ParsekTimeFormat.SecsPerDay;
            double secsPerYear = ParsekTimeFormat.SecsPerYear;
            double value;
            string unit;
            if (seconds >= secsPerYear) { value = seconds / secsPerYear; unit = "y"; }
            else if (seconds >= secsPerDay) { value = seconds / secsPerDay; unit = "d"; }
            else if (seconds >= 3600.0) { value = seconds / 3600.0; unit = "h"; }
            else if (seconds >= 60.0) { value = seconds / 60.0; unit = "m"; }
            else { value = seconds; unit = "s"; }
            string num = value.ToString("0.0", ic);
            if (num.EndsWith(".0"))
                num = num.Substring(0, num.Length - 2);
            return "~" + num + unit;
        }

        /// <summary>
        /// The basis label for a phase-locked period cell: "(Kerbin rot)" for a Rotation constraint
        /// (the launch/landing body's rotation realigns the orbit over the site) or "(Mun window)"
        /// for an Orbital constraint (the next intercept window). Pure; empty body -> empty label.
        /// </summary>
        internal static string BuildPeriodBasisLabel(ConstraintKind kind, string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName))
                return "";
            return kind == ConstraintKind.Rotation
                ? "(" + bodyName + " rot)"
                : "(" + bodyName + " window)";
        }

        /// <summary>
        /// The phase-locked period-cell display: the faithful period P + its basis label, e.g.
        /// "~6h (Kerbin rot)" or "~1.6d (Mun window)". Pure. A null/empty body (no dominant
        /// constraint) drops the basis suffix and shows just the period.
        /// </summary>
        internal static string BuildPeriodCellDisplay(double p, ConstraintKind kind, string bodyName)
        {
            string period = FormatPeriodCompact(p);
            string basis = BuildPeriodBasisLabel(kind, bodyName);
            return string.IsNullOrEmpty(basis) ? period : period + " " + basis;
        }

        // The mission span in seconds = (max trimmed end - min trimmed start) over the COMMITTED
        // loop members, computed via the SAME MissionLoopUnitBuilder.ComputeTrimmedMemberWindows the
        // loop builder derives its span from - so the period cell's effective-cadence display matches
        // the cadence actually running, including interval-level start/end trims (the old path keyed
        // off ExcludedThroughLineHeadIds, which the UI never writes, so it ignored interval trims).
        // Returns 0 when no committed member is included (no overlap; cell shows the raw period).
        private double MissionSpanSeconds(
            MissionThroughLineView view, Mission mission, IReadOnlyList<Recording> committed)
        {
            if (view == null || committed == null)
                return 0.0;
            RecordingTree tree = FindTree(RecordingStore.CommittedTrees, mission.TreeId);
            if (tree == null)
                return 0.0;

            var windows = MissionLoopUnitBuilder.ComputeTrimmedMemberWindows(
                view, GetCompositionRoots(tree), committed, mission.ExcludedIntervalKeys,
                null, out _, out _);

            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            foreach (var w in windows.Values)
            {
                if (w.StartUT < min) min = w.StartUT;
                if (w.EndUT > max) max = w.EndUT;
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
            RecordingTree tree = FindTree(RecordingStore.CommittedTrees, mission.TreeId);
            if (tree == null)
                return -1;

            // The TRIMMED loop members keyed by committed index - the SAME set
            // MissionLoopUnitBuilder actually spawns - so a watch target (and the "watching this
            // mission" check) never picks a vessel that the interval-level trim dropped from the
            // loop. (The old path keyed off ExcludedThroughLineHeadIds, which the UI never writes,
            // so it considered every vessel regardless of interval trims.)
            var windows = MissionLoopUnitBuilder.ComputeTrimmedMemberWindows(
                view, GetCompositionRoots(tree), committed, mission.ExcludedIntervalKeys,
                null, out _, out _);

            int watchedIdx = flight.WatchedRecordingIndex;
            int target = -1;
            for (int i = 0; i < committed.Count; i++)
            {
                if (!windows.ContainsKey(i))
                    continue;
                if (i == watchedIdx)
                    isWatchingThisMission = true;
                if (target < 0 && flight.HasActiveGhost(i) && flight.IsGhostOnSameBody(i)
                    && flight.IsGhostWithinVisualRange(i))
                    target = i;
            }
            return target;
        }

        // The committed index of the mission's ROOT (launch) recording = the earliest-StartUT
        // committed member across all of the mission's through-lines. Used by the header's
        // Rewind/Forward cell so it rewinds the game to the mission's launch (or fast-forwards
        // to it). The legacy R/FF path resolves the rewind owner / save by identity from there,
        // so passing the launch member is correct for both directions. Returns -1 when no
        // committed member is found (the R/FF cell then renders blank).
        private static int ResolveMissionRootRecordingIndex(
            MissionThroughLineView view, IReadOnlyList<Recording> committed)
        {
            if (view == null || committed == null)
                return -1;

            var memberIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var kv in view.ByHeadId)
            {
                MissionThroughLine tl = kv.Value;
                if (tl == null) continue;
                for (int m = 0; m < tl.MemberLegIds.Count; m++)
                    if (!string.IsNullOrEmpty(tl.MemberLegIds[m]))
                        memberIds.Add(tl.MemberLegIds[m]);
            }

            int best = -1;
            double bestStart = double.PositiveInfinity;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording rec = committed[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)
                    || !memberIds.Contains(rec.RecordingId))
                    continue;
                if (rec.StartUT < bestStart)
                {
                    bestStart = rec.StartUT;
                    best = i;
                }
            }
            return best;
        }

        // Loop-period cell: a value text field plus a unit button cycling Sec/Min/Hour/
        // Auto, mirroring the recordings window's DrawLoopPeriodCell. When loop is off the
        // controls grey out (matching the recordings window). Reuses ParsekUI's shared
        // loop-time helpers so the parse/format/convert rules stay identical. Commit on
        // text change: parse via TryParseLoopInput, reject negatives, clamp below
        // MinCycleDuration (same contract as RecordingsTableUI.CommitLoopPeriodEdit).
        private void DrawMissionLoopPeriodCell(
            Mission mission, MissionThroughLineView view, MissionPeriodicityDisplay periodicity)
        {
            const float unitBtnW = 40f; // match RecordingsTableUI.DrawLoopPeriodCell
            float valueW = ColW_Period - unitBtnW - 4f;

            bool enabled = mission.LoopPlayback;
            bool auto = mission.LoopTimeUnit == LoopTimeUnit.Auto;

            string controlName = "MissionLoopPeriod_" + mission.Id;

            // Phase-locked + constrained (supported, P != MinCycleDuration): the cadence is
            // determined by physics (quantized to a multiple of P), not freely editable, so show the
            // faithful period P + its basis label ("~6h (Kerbin rot)" / "~1.6d (Mun window)") as a
            // read-only tinted cell instead of the raw overlap-cap cadence. The unit button is
            // dropped here (the value is a fixed physical period, not a user-typed value in a unit).
            // An unconstrained (continuous) or unsupported (not-aligned) config falls through to the
            // normal editable cell below. End any stale edit focus on this field first.
            if (enabled && periodicity.IsPhaseLockedConstrained)
            {
                if (loopPeriodFocusedMissionId == mission.Id)
                    loopPeriodFocusedMissionId = null;
                string locked = BuildPeriodCellDisplay(
                    periodicity.Solution.P, periodicity.DominantKind, periodicity.DominantBodyName);
                GUI.enabled = false;
                Color prevLocked = GUI.contentColor;
                GUI.contentColor = LoopPeriodClampColor;
                GUILayout.Label(locked, bodyCellLabel, GUILayout.Width(ColW_Period));
                GUI.contentColor = prevLocked;
                GUI.enabled = true;
                return;
            }

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
                // A non-editable field cannot be the one being edited; drop any stale edit focus.
                if (loopPeriodFocusedMissionId == mission.Id)
                    loopPeriodFocusedMissionId = null;
                GUI.enabled = false;
                Color prevAuto = GUI.contentColor;
                if (showEffective) GUI.contentColor = LoopPeriodClampColor;
                GUILayout.TextField(autoText, bodyCellTextFieldFlush, GUILayout.Width(valueW));
                GUI.contentColor = prevAuto;
                GUI.enabled = true;
            }
            else if (loopPeriodFocusedMissionId != mission.Id)
            {
                // Manual mode, NOT editing this field: show the EFFECTIVE (capped) cadence (or the
                // raw stored value), and begin editing when the field gains keyboard focus (seeding
                // the buffer from the raw stored value). Commit happens on Enter / click-away, not
                // on every keystroke, so it does not rely on per-frame focus detection.
                string shownText = showEffective
                    ? ParsekUI.FormatLoopValue(
                        ParsekUI.ConvertFromSeconds(effectiveSeconds, mission.LoopTimeUnit), mission.LoopTimeUnit)
                    : ParsekUI.FormatLoopValue(
                        ParsekUI.ConvertFromSeconds(mission.LoopIntervalSeconds, mission.LoopTimeUnit), mission.LoopTimeUnit);

                GUI.enabled = enabled;
                GUI.SetNextControlName(controlName);
                Color prevManual = GUI.contentColor;
                if (showEffective) GUI.contentColor = LoopPeriodClampColor;
                GUILayout.TextField(shownText, bodyCellTextFieldFlush, GUILayout.Width(valueW));
                GUI.contentColor = prevManual;
                loopPeriodEditRect = GUILayoutUtility.GetLastRect();
                if (enabled && GUI.GetNameOfFocusedControl() == controlName)
                {
                    loopPeriodFocusedMissionId = mission.Id;
                    loopPeriodEditText = ParsekUI.FormatLoopValue(
                        ParsekUI.ConvertFromSeconds(mission.LoopIntervalSeconds, mission.LoopTimeUnit),
                        mission.LoopTimeUnit);
                    ParsekLog.Verbose("Mission",
                        $"Loop period edit started for '{mission.Name}': value='{loopPeriodEditText}'");
                }
                GUI.enabled = true;
            }
            else
            {
                // Manual mode, editing THIS field: show the in-progress buffer. Commit on Enter
                // (checked before the TextField, which consumes the KeyDown); click-away commit is
                // handled at the window level in DrawMissionsTabContent.
                bool submit = Event.current.type == EventType.KeyDown &&
                    (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

                GUI.enabled = enabled;
                GUI.SetNextControlName(controlName);
                string newText = GUILayout.TextField(
                    loopPeriodEditText, bodyCellTextFieldFlush, GUILayout.Width(valueW));
                loopPeriodEditRect = GUILayoutUtility.GetLastRect();
                if (newText != loopPeriodEditText)
                    loopPeriodEditText = newText;
                if (submit)
                {
                    CommitMissionLoopPeriodEdit(mission);
                    Event.current.Use();
                }
                GUI.enabled = true;
            }

            GUILayout.Space(4f);
            GUI.enabled = enabled;
            if (GUILayout.Button(ParsekUI.UnitLabel(mission.LoopTimeUnit), bodyCellButtonFlush, GUILayout.Width(unitBtnW)))
            {
                mission.LoopTimeUnit = RecordingsTableUI.CycleRecordingUnit(mission.LoopTimeUnit);
                if (loopPeriodFocusedMissionId == mission.Id)
                    loopPeriodFocusedMissionId = null;
                GUIUtility.keyboardControl = 0;
                ParsekLog.Info("Mission",
                    $"Loop unit for '{mission.Name}' changed to {mission.LoopTimeUnit}");
            }
            GUI.enabled = true;
        }

        // Commits the in-progress loop-period buffer to the mission (parse / clamp via
        // CommitMissionLoopPeriod), then ends the edit (clears focus + keyboard control). Safe to
        // call with a null mission (just ends the edit).
        private void CommitMissionLoopPeriodEdit(Mission mission)
        {
            if (mission != null)
                CommitMissionLoopPeriod(mission, loopPeriodEditText);
            loopPeriodFocusedMissionId = null;
            loopPeriodEditText = "";
            GUIUtility.keyboardControl = 0;
        }

        // Finds a mission by id (for the window-level click-away commit). Null if not found.
        private static Mission FindMissionById(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            var ms = MissionStore.Missions;
            for (int i = 0; i < ms.Count; i++)
                if (ms[i] != null && ms[i].Id == id)
                    return ms[i];
            return null;
        }

        // Parses an edited loop-period value and writes it to the Mission, mirroring
        // RecordingsTableUI.CommitLoopPeriodEdit: reject negatives, clamp below
        // MinCycleDuration. Invalid text is ignored (no write); the caller
        // (CommitMissionLoopPeriodEdit) then ends the edit and the field reverts to the stored
        // value display, exactly as the Recordings tab does on Enter / click-away. Auto unit
        // does not commit a value. (While editing, the TextField keeps whatever you type between
        // frames; this method only runs at the commit, not per keystroke.)
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
            // Every header cell is forced to ColHeaderHeight so the row reads as one uniform band
            // (the Archive cell carries a toggle and would otherwise be taller), matching the
            // recordings tab. The sortable headers take the height via DrawSortableHeaderCore.
            //
            // First column = a merged [enable+index] cell of the SAME width as the recordings tab's
            // first header (Width(ColW_Enable + ColW_Index + 8)), so the expanding "Missions and
            // vessels" column starts at the same x as the recordings "Name" column. There is no
            // per-row enable in missions, so the enable slot is blank; the sortable "#" lives inline
            // (boldHeaderInnerLabel) in the index slot so the dark container is not double-boxed.
            GUILayout.BeginHorizontal(colHdrCellContainerStyle,
                GUILayout.Width(ColW_Enable + ColW_Index + 8f), GUILayout.Height(ColHeaderHeight));
            GUILayout.Label("", GUILayout.Width(ColW_Enable));
            string hashArrow = (sortColumn == MissionSortColumn.Index)
                ? (sortAscending ? " \u25b2" : " \u25bc") : "";
            if (GUILayout.Button("#" + hashArrow, boldHeaderInnerLabel, GUILayout.Width(ColW_Index)))
            {
                if (sortColumn == MissionSortColumn.Index) sortAscending = !sortAscending;
                else { sortColumn = MissionSortColumn.Index; sortAscending = true; }
                LogSortChanged();
            }
            GUILayout.EndHorizontal();
            parentUI.DrawSortableHeaderCore("Missions and vessels", MissionSortColumn.Name,
                ref sortColumn, ref sortAscending, 0f, true, LogSortChanged, ColHeaderHeight);
            parentUI.DrawSortableHeaderCore("Start time", MissionSortColumn.StartTime,
                ref sortColumn, ref sortAscending, ColW_StartTime, false, LogSortChanged, ColHeaderHeight);
            GUILayout.Label("Start event", colHdr, GUILayout.Width(ColW_StartEvent), GUILayout.Height(ColHeaderHeight));
            GUILayout.Label("End event", colHdr, GUILayout.Width(ColW_EndEvent), GUILayout.Height(ColHeaderHeight));
            GUILayout.Label("End time", colHdr, GUILayout.Width(ColW_EndTime), GUILayout.Height(ColHeaderHeight));

            // "T- to launch" column header (left of Re-Fly): the per-mission header bar shows a
            // live countdown to the next faithful launch window (the vessel rows leave it blank).
            GUILayout.Label("T- to launch", colHdr, GUILayout.Width(ColW_TMinus), GUILayout.Height(ColHeaderHeight));

            // Re-Fly column header (left of Archive): the per-vessel rows show Fly / Seal for
            // unfinished-flight recordings (reusing the recordings tab's Re-Fly cell).
            GUILayout.Label("Re-Fly", colHdr, GUILayout.Width(ColW_ReFly), GUILayout.Height(ColHeaderHeight));

            // Archive column header + global toggle (mirrors the recordings window): label +
            // a checkbox bound to MissionStore.HideArchived. When on, archived missions drop
            // out of the list; the per-mission Archive checkbox lives on each mission's row.
            // The dark box (colHdrCellContainerStyle) wraps the WHOLE cell, label + checkbox,
            // matching RecordingsTableUI; boldHeaderInnerLabel keeps the label unboxed inside.
            GUILayout.BeginHorizontal(colHdrCellContainerStyle, GUILayout.Width(ColW_Archive), GUILayout.Height(ColHeaderHeight));
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

    }
}
