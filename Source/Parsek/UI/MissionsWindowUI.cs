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
        private const float ColW_Action = 60f;

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

                // Snapshot so Clone / Delete (which mutate the store) can be invoked
                // from within the draw loop; the change takes effect on the next frame.
                var snapshot = new List<Mission>(missions);
                int missionCount = 0;
                int rowCount = 0;
                for (int i = 0; i < snapshot.Count; i++)
                {
                    Mission mission = snapshot[i];
                    if (mission == null)
                        continue;
                    RecordingTree tree = FindTree(trees, mission.TreeId);
                    if (tree == null)
                        continue;
                    missionCount++;

                    MissionStructure structure = MissionStructureBuilder.Build(tree);
                    MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);

                    DrawMissionHeader(mission);

                    var visited = new HashSet<string>();
                    for (int r = 0; r < view.RootHeadIds.Count; r++)
                    {
                        bool isLast = r == view.RootHeadIds.Count - 1;
                        rowCount += DrawThroughLine(structure, view, mission, view.RootHeadIds[r], 1, isLast, false, visited);
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

        // Mission header bar: the mission name (section-header style) plus Clone and
        // Delete. Delete is disabled when this is the tree's last mission. Clone/Delete
        // mutate MissionStore; the draw loop iterates a snapshot so that is safe.
        private void DrawMissionHeader(Mission mission)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(string.IsNullOrEmpty(mission.Name) ? "(mission)" : mission.Name,
                parentUI.GetSectionHeaderStyle(), GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Clone", GUILayout.Width(ColW_Action)))
                MissionStore.Clone(mission);
            GUI.enabled = MissionStore.CanDelete(mission);
            if (GUILayout.Button("Delete", GUILayout.Width(ColW_Action)))
                MissionStore.Delete(mission);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
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
        // Renders one through-line (a collapsed continuous vessel), then recurses into
        // its offshoots (the things that left it: EVA kerbals, decoupled children,
        // forks to other vessels). The visited set guards merges/cycles.
        private int DrawThroughLine(MissionStructure s, MissionThroughLineView v, Mission mission,
            string headId, int depth, bool isLast, bool parentExcluded, HashSet<string> visited)
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

            // Unchecking a through-line drops it and everything downstream.
            bool childExcluded = parentExcluded || mission.ExcludedThroughLineHeadIds.Contains(headId);
            if (hasChildren && !collapsed)
            {
                for (int i = 0; i < tl.OffshootHeadIds.Count; i++)
                {
                    bool childIsLast = i == tl.OffshootHeadIds.Count - 1;
                    rows += DrawThroughLine(s, v, mission, tl.OffshootHeadIds[i], depth + 1, childIsLast, childExcluded, visited);
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
            bool toggled = GUILayout.Toggle(shownChecked, "", GUILayout.Width(ColW_Check));
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
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Check));

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
