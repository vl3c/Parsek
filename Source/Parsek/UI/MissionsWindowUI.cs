using System.Collections.Generic;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Read-only Missions window (phase 2 of the mission-abstraction feature).
    /// Derived from <see cref="RecordingsTableUI"/> so it reuses that window's
    /// exact visual style and rendering scaffolding (column-header style, dark
    /// table-body box, tree-branch connector glyphs, expand/collapse carets, and
    /// the section-header bar). The ONLY conceptual difference from the recordings
    /// window: the recordings window groups individual recordings
    /// (groups -> chains -> recordings); the Missions window groups the higher
    /// mission abstraction, rendering each committed mission tree's controlled-leg
    /// fork-tree (from <see cref="MissionStructureBuilder"/>) as an indented
    /// chronological outline. A run's env-split legs stack at one depth; forks
    /// indent. No checkboxes / persistence / loop / clone / delete yet.
    /// See docs/dev/design-mission-abstractions.md.
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

        // Leg ids (RecordingIds, unique GUIDs) whose children are collapsed in the
        // outline. Shared across trees: GUIDs never collide, so one set is safe.
        private readonly HashSet<string> collapsedLegs = new HashSet<string>();

        // Leg ids the player has unchecked. Unchecking a leg drops it and its whole
        // downstream subtree from the mission (the agreed trim rule); those rows grey
        // out and their checkboxes disable. In-memory for now (no persistence yet).
        private readonly HashSet<string> uncheckedLegs = new HashSet<string>();

        private const string InputLockId = "Parsek_MissionsWindow";
        private const float MinWindowWidth = 450f;
        private const float MinWindowHeight = 150f;
        private const float DefaultWidth = 680f;

        // Fixed-width columns to the right of the expanding "Vessel" column, mirroring
        // the recordings window's fixed-width cells so the window reads as the same
        // table style. Layout: [check] Vessel | Start time | Start event | End event | End time.
        private const float ColW_Check = 22f;
        private const float ColW_StartTime = 100f;
        private const float ColW_StartEvent = 85f;
        private const float ColW_EndEvent = 85f;
        private const float ColW_EndTime = 100f;

        private static readonly Color DimColor = new Color(1f, 1f, 1f, 0.45f);

        // -- Recreated styles --
        // RecordingsTableUI builds these privately inside EnsurePhaseStyles();
        // we cannot reach those instances from a subclass, so we lazily rebuild
        // the same styles here (verbatim, adapted) to match the look exactly.
        private GUIStyle bodyCellLabel;
        private GUIStyle tableBodyBoxStyle;

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
                float h = parentUI.InFlightMode ? mainWindowRect.height : mainWindowRect.height * 2f;
                windowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10f,
                    mainWindowRect.y,
                    DefaultWidth, h);
            }

            ParsekUI.HandleResizeDrag(ref windowRect, ref isResizing,
                MinWindowWidth, MinWindowHeight, "Missions window");

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
        }

        private void DrawWindow(int windowID)
        {
            EnsureStyles();

            // Breathing room below the title bar (matches the recordings window).
            GUILayout.Space(5);

            GUILayout.BeginVertical();

            var trees = RecordingStore.CommittedTrees;
            if (trees == null || trees.Count == 0)
            {
                GUILayout.Label("No missions recorded yet.");
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

                int treeCount = 0;
                int legRows = 0;
                for (int t = 0; t < trees.Count; t++)
                {
                    RecordingTree tree = trees[t];
                    if (tree == null)
                        continue;
                    treeCount++;

                    MissionStructure structure = MissionStructureBuilder.Build(tree);
                    string treeName = string.IsNullOrEmpty(tree.TreeName) ? "(mission)" : tree.TreeName;
                    GUILayout.Label(
                        $"{treeName}  -  {structure.LegsById.Count} legs",
                        parentUI.GetSectionHeaderStyle());

                    var visited = new HashSet<string>();
                    for (int r = 0; r < structure.RootLegIds.Count; r++)
                    {
                        bool isLast = r == structure.RootLegIds.Count - 1;
                        legRows += DrawLeg(structure, structure.RootLegIds[r], 1, isLast, false, visited);
                    }

                    if (visited.Count < structure.LegsById.Count)
                    {
                        ParsekLog.VerboseRateLimited("UI", $"missions-dropped-{tree.Id}",
                            $"Missions window: tree={tree.Id} rendered {visited.Count}/{structure.LegsById.Count} " +
                            $"legs; {structure.LegsById.Count - visited.Count} unreachable from roots " +
                            $"(malformed branch-point cycle?)", 30.0);
                    }
                }

                GUILayout.EndVertical();
                GUILayout.EndScrollView();

                ParsekLog.VerboseRateLimited("UI", "missions-window-draw",
                    $"Missions window: trees={treeCount} legRows={legRows}", 5.0);
            }

            GUILayout.EndVertical();
            ParsekUI.DrawResizeHandle(windowRect, ref isResizing, "Missions window");
            GUI.DragWindow();
        }

        // Column-header row: a checkbox column, a wide expanding "Vessel" column, then
        // Start time / Start event / End event / End time, all in the recordings
        // window's shared column-header style.
        private void DrawColumnHeader()
        {
            var colHdr = parentUI.GetColumnHeaderStyle();
            GUILayout.BeginHorizontal();
            GUILayout.Label("", colHdr, GUILayout.Width(ColW_Check));
            GUILayout.Label("Vessel", colHdr, GUILayout.ExpandWidth(true));
            GUILayout.Label("Start time", colHdr, GUILayout.Width(ColW_StartTime));
            GUILayout.Label("Start event", colHdr, GUILayout.Width(ColW_StartEvent));
            GUILayout.Label("End event", colHdr, GUILayout.Width(ColW_EndEvent));
            GUILayout.Label("End time", colHdr, GUILayout.Width(ColW_EndTime));

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
        private int DrawLeg(MissionStructure s, string legId, int depth, bool isLast,
            bool parentExcluded, HashSet<string> visited)
        {
            if (legId == null || !s.LegsById.TryGetValue(legId, out MissionLeg leg))
                return 0;

            if (visited.Contains(legId))
            {
                DrawReferenceRow(leg, depth, isLast);
                return 1;
            }
            visited.Add(legId);

            // Children: sequence-next first, then branch children.
            var children = new List<string>();
            if (leg.SequenceNextId != null)
                children.Add(leg.SequenceNextId);
            for (int i = 0; i < leg.BranchChildIds.Count; i++)
                children.Add(leg.BranchChildIds[i]);

            bool hasChildren = children.Count > 0;
            bool collapsed = collapsedLegs.Contains(leg.RecordingId);

            DrawLegRow(leg, depth, isLast, hasChildren, collapsed, parentExcluded);
            int rows = 1;

            // Unchecking a leg drops it and everything downstream: children inherit
            // the excluded flag once this leg (or any ancestor) is unchecked.
            bool childExcluded = parentExcluded || uncheckedLegs.Contains(leg.RecordingId);
            if (hasChildren && !collapsed)
            {
                for (int i = 0; i < children.Count; i++)
                {
                    bool childIsLast = i == children.Count - 1;
                    rows += DrawLeg(s, children[i], depth + 1, childIsLast, childExcluded, visited);
                }
            }

            return rows;
        }

        private void DrawLegRow(MissionLeg leg, int depth, bool isLast, bool hasChildren,
            bool collapsed, bool parentExcluded)
        {
            GUILayout.BeginHorizontal();

            // Include checkbox. Disabled when an ancestor is unchecked (this leg is
            // downstream of a dropped leg, so it can only be re-included by re-checking
            // the ancestor). Toggling adds/removes this leg from the unchecked set.
            bool selfUnchecked = uncheckedLegs.Contains(leg.RecordingId);
            bool shownChecked = !parentExcluded && !selfUnchecked;
            GUI.enabled = !parentExcluded;
            bool toggled = GUILayout.Toggle(shownChecked, "", GUILayout.Width(ColW_Check));
            GUI.enabled = true;
            if (!parentExcluded && toggled != shownChecked)
            {
                if (toggled) uncheckedLegs.Remove(leg.RecordingId);
                else uncheckedLegs.Add(leg.RecordingId);
            }

            // Dim rows dropped from the mission (downstream of an unchecked leg, or
            // unchecked themselves).
            Color prevColor = GUI.color;
            if (parentExcluded || selfUnchecked)
                GUI.color = DimColor;

            float indent = RecordingsTableUI.SelfConnectorIndent(depth);
            if (indent > 0f)
                GUILayout.Space(indent);

            string connector = depth > 0 ? RecordingsTableUI.TreeConnector(isLast) : "";
            string caret = hasChildren ? (collapsed ? CaretRight : CaretDown) : "";
            string vessel = string.IsNullOrEmpty(leg.VesselName) ? "(vessel)" : leg.VesselName;
            string wide = connector + caret + vessel;

            // Clickable label (toggles collapse) when the leg has children;
            // otherwise a plain body-cell label.
            if (hasChildren)
            {
                if (GUILayout.Button(wide, bodyCellLabel, GUILayout.ExpandWidth(true)))
                {
                    if (collapsedLegs.Contains(leg.RecordingId))
                        collapsedLegs.Remove(leg.RecordingId);
                    else
                        collapsedLegs.Add(leg.RecordingId);
                }
            }
            else
            {
                GUILayout.Label(wide, bodyCellLabel, GUILayout.ExpandWidth(true));
            }

            GUILayout.Label(KSPUtil.PrintDateCompact(leg.StartUT, true), bodyCellLabel, GUILayout.Width(ColW_StartTime));
            GUILayout.Label(StartEventText(leg), bodyCellLabel, GUILayout.Width(ColW_StartEvent));
            GUILayout.Label(EndEventText(leg), bodyCellLabel, GUILayout.Width(ColW_EndEvent));
            GUILayout.Label(KSPUtil.PrintDateCompact(leg.EndUT, true), bodyCellLabel, GUILayout.Width(ColW_EndTime));

            GUI.color = prevColor;
            GUILayout.EndHorizontal();
        }

        // Reference row for a merge child reached from a second parent: shown once,
        // not recursed into. Wide cell carries the merge arrow; time/status empty.
        private void DrawReferenceRow(MissionLeg leg, int depth, bool isLast)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Check));

            float indent = RecordingsTableUI.SelfConnectorIndent(depth);
            if (indent > 0f)
                GUILayout.Space(indent);

            string connector = depth > 0 ? RecordingsTableUI.TreeConnector(isLast) : "";
            string vessel = string.IsNullOrEmpty(leg.VesselName) ? "(vessel)" : leg.VesselName;
            GUILayout.Label($"{connector}{MergeArrow}merges into {vessel} (above)",
                bodyCellLabel, GUILayout.ExpandWidth(true));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartTime));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartEvent));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndEvent));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndTime));

            GUILayout.EndHorizontal();
        }

        // How a leg began: its origin branch event (decoupled / EVA / docked / etc.),
        // or "continues" for an env-split continuation of the same vessel, or
        // "launched" for the mission root.
        private static string StartEventText(MissionLeg leg)
        {
            if (leg.OriginBranchPointType.HasValue)
                return EventLabel(leg.OriginBranchPointType.Value);
            if (leg.SequencePrevId != null)
                return "continues";
            if (leg.IsRoot)
                return "launched";
            return "";
        }

        // How a leg ended: its terminal state (Landed / Destroyed / ...), or the
        // branch event it ended at, or "continues"/"active".
        private static string EndEventText(MissionLeg leg)
        {
            if (leg.TerminalStateValue.HasValue)
                return leg.TerminalStateValue.Value.ToString();
            if (leg.EndBranchPointType.HasValue)
                return EventLabel(leg.EndBranchPointType.Value);
            if (leg.SequenceNextId != null)
                return "continues";
            return "active";
        }

        private static string EventLabel(BranchPointType type)
        {
            switch (type)
            {
                case BranchPointType.Launch: return "launched";
                case BranchPointType.Undock: return "undocked";
                case BranchPointType.Dock: return "docked";
                case BranchPointType.EVA: return "EVA";
                case BranchPointType.Board: return "boarded";
                case BranchPointType.JointBreak: return "decoupled";
                case BranchPointType.Breakup: return "broke up";
                case BranchPointType.Terminal: return "ended";
                case BranchPointType.VesselSwitchContinuation: return "switched";
                default: return type.ToString();
            }
        }
    }
}
