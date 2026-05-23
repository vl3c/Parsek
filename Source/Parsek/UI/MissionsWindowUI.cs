using System.Collections.Generic;
using System.Globalization;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Read-only Missions window (phase 2 of the mission-abstraction feature).
    /// Renders each committed mission tree's controlled-leg fork-tree
    /// (from MissionStructureBuilder) as an indented outline: a run's env-split
    /// legs stack at one depth, forks indent. No checkboxes / persistence yet.
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

        // Run-tail leg ids whose forks are collapsed in the outline.
        private readonly HashSet<string> collapsedLegs = new HashSet<string>();

        private const string InputLockId = "Parsek_MissionsWindow";
        private const float MinWindowWidth = 320f;
        private const float MinWindowHeight = 150f;
        private const float DefaultWidth = 480f;

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

        private void DrawWindow(int windowID)
        {
            GUILayout.BeginVertical();

            var trees = RecordingStore.CommittedTrees;
            if (trees == null || trees.Count == 0)
            {
                GUILayout.Label("No missions recorded yet.");
            }
            else
            {
                scrollPos = GUILayout.BeginScrollView(scrollPos, false, true, GUILayout.ExpandHeight(true));

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
                        legRows += DrawRun(structure, structure.RootLegIds[r], 0, isLast, visited);
                    }
                }

                GUILayout.EndScrollView();

                ParsekLog.VerboseRateLimited("UI", "missions-window-draw",
                    $"Missions window: trees={treeCount} legRows={legRows}", 5.0);
            }

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        // Renders one run (a sequence of SequenceNext-linked legs at the same
        // depth), then recurses into the run-tail's forks at depth+1. The visited
        // set both prevents re-rendering a merge child reached from two parents
        // (it gets a reference row instead) and guards against malformed cycles.
        private int DrawRun(MissionStructure s, string headId, int depth, bool isLast,
            HashSet<string> visited)
        {
            int rows = 0;
            string legId = headId;
            bool isHeadRow = true;
            MissionLeg tail = null;

            while (legId != null && s.LegsById.TryGetValue(legId, out MissionLeg leg))
            {
                if (visited.Contains(legId))
                {
                    DrawReferenceRow(leg, depth, isLast);
                    return rows + 1;
                }
                visited.Add(legId);
                tail = leg;

                bool isTail = leg.SequenceNextId == null;
                bool drawCaret = isTail && leg.BranchChildIds.Count > 0;
                DrawLegRow(leg, depth, isHeadRow, isLast, drawCaret);
                rows++;
                isHeadRow = false;

                if (isTail)
                    break;
                legId = leg.SequenceNextId;
            }

            if (tail != null && tail.BranchChildIds.Count > 0
                && !collapsedLegs.Contains(tail.RecordingId))
            {
                for (int i = 0; i < tail.BranchChildIds.Count; i++)
                {
                    bool childIsLast = i == tail.BranchChildIds.Count - 1;
                    rows += DrawRun(s, tail.BranchChildIds[i], depth + 1, childIsLast, visited);
                }
            }

            return rows;
        }

        private void DrawLegRow(MissionLeg leg, int depth, bool isHeadRow, bool isLast, bool drawCaret)
        {
            float connW = RecordingsTableUI.ConnectorWidth();
            string caret = drawCaret
                ? (collapsedLegs.Contains(leg.RecordingId) ? "▶ " : "▼ ")
                : "";

            GUILayout.BeginHorizontal();
            if (isHeadRow)
            {
                if (depth > 0)
                    GUILayout.Space(depth * connW);
                string connector = depth > 0 ? RecordingsTableUI.TreeConnector(isLast) : "";
                DrawLabelOrToggle(connector + caret + FormatLeg(leg), drawCaret, leg.RecordingId);
            }
            else
            {
                // Sequence-continuation rows stack under the run head, label-aligned
                // (one connector width in) and marked with a bullet, not a fork tee.
                GUILayout.Space(depth * connW + connW);
                DrawLabelOrToggle("· " + caret + FormatLeg(leg), drawCaret, leg.RecordingId);
            }
            GUILayout.EndHorizontal();
        }

        private void DrawLabelOrToggle(string label, bool toggle, string legId)
        {
            if (toggle)
            {
                if (GUILayout.Button(label, GUI.skin.label, GUILayout.ExpandWidth(true)))
                {
                    if (collapsedLegs.Contains(legId))
                        collapsedLegs.Remove(legId);
                    else
                        collapsedLegs.Add(legId);
                }
            }
            else
            {
                GUILayout.Label(label, GUILayout.ExpandWidth(true));
            }
        }

        private static void DrawReferenceRow(MissionLeg leg, int depth, bool isLast)
        {
            GUILayout.BeginHorizontal();
            float connW = RecordingsTableUI.ConnectorWidth();
            if (depth > 0)
                GUILayout.Space(depth * connW);
            string connector = depth > 0 ? RecordingsTableUI.TreeConnector(isLast) : "";
            string vessel = string.IsNullOrEmpty(leg.VesselName) ? "(vessel)" : leg.VesselName;
            GUILayout.Label($"{connector}↳ merges into {vessel} (shown above)",
                GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        private static string FormatLeg(MissionLeg leg)
        {
            var ic = CultureInfo.InvariantCulture;
            string vessel = string.IsNullOrEmpty(leg.VesselName) ? "(vessel)" : leg.VesselName;
            double duration = leg.EndUT - leg.StartUT;
            if (duration < 0)
                duration = 0;
            string ev = leg.TerminalStateValue.HasValue
                ? leg.TerminalStateValue.Value.ToString()
                : leg.EndBranchPointType.HasValue
                    ? leg.EndBranchPointType.Value.ToString()
                    : "in progress";
            return $"{vessel}   {duration.ToString("F0", ic)}s   {ev}";
        }
    }
}
