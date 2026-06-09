using System;
using System.Collections.Generic;
using System.Globalization;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Structure-list window: a flat, chronological step list of one mission or supply
    /// route (launch, staging, dock / undock, deliveries, terminal), each with its time
    /// and location. Opened from a mission's "Structure" button (Missions tab) or a
    /// route's "Structure" button (Logistics window). One reusable instance owned by
    /// <see cref="ParsekUI"/>; reopening retargets it. Read-only over already-recorded
    /// data; the ordered step list comes from the pure
    /// <see cref="MissionStructureListBuilder"/> / <see cref="RouteStructureListBuilder"/>.
    /// </summary>
    internal class StructureListWindowUI
    {
        private enum TargetMode { None, Mission, Route }

        private readonly ParsekUI parentUI;

        private bool isOpen;
        private Rect windowRect;
        private bool hasInputLock;
        private bool isResizing;
        private Vector2 scrollPos;
        private Rect lastWindowRect;

        private const string InputLockId = "Parsek_StructureListWindow";

        // Current target + cached built step list (rebuilt when the target changes).
        private TargetMode mode = TargetMode.None;
        private string targetId;
        private string title = "Structure";
        private List<StructureStep> steps = new List<StructureStep>();

        // Column widths (match the recordings / spawn window conventions).
        private const float ColW_Time = 115f;
        private const float ColW_Event = 0f;     // expand
        private const float ColW_Location = 190f;
        private const float ColW_Vessel = 150f;

        private const float MinWindowWidth = 420f;
        private const float MinWindowHeight = 160f;

        public bool IsOpen
        {
            get { return isOpen; }
            set { isOpen = value; }
        }

        internal StructureListWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        internal void OpenForMission(string treeId, string displayTitle)
        {
            mode = TargetMode.Mission;
            targetId = treeId;
            title = string.IsNullOrEmpty(displayTitle) ? "Mission structure" : displayTitle;
            Rebuild();
            isOpen = true;
            ParsekLog.Info("UI",
                $"Structure window opened: mode=Mission tree={treeId ?? "<null>"} steps={steps.Count}");
        }

        internal void OpenForRoute(string routeId, string displayTitle)
        {
            mode = TargetMode.Route;
            targetId = routeId;
            title = string.IsNullOrEmpty(displayTitle) ? "Route structure" : displayTitle;
            Rebuild();
            isOpen = true;
            ParsekLog.Info("UI",
                $"Structure window opened: mode=Route route={routeId ?? "<null>"} steps={steps.Count}");
        }

        // Resolves the target's data and (re)builds the step list. Kept off the per-frame
        // path: only called on open (and a defensive rebuild when the window first draws).
        private void Rebuild()
        {
            steps = new List<StructureStep>();
            if (mode == TargetMode.Mission)
            {
                RecordingTree tree = FindTree(targetId);
                if (tree != null)
                {
                    MissionStructure structure = MissionStructureBuilder.Build(tree);
                    steps = MissionStructureListBuilder.Build(tree, structure);
                }
            }
            else if (mode == TargetMode.Route)
            {
                if (Logistics.RouteStore.TryGetRoute(targetId, out Logistics.Route route))
                    steps = RouteStructureListBuilder.Build(route, FindCommittedRecording);
            }
        }

        private static RecordingTree FindTree(string treeId)
        {
            if (string.IsNullOrEmpty(treeId)) return null;
            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null && string.Equals(trees[i].Id, treeId, StringComparison.Ordinal))
                    return trees[i];
            return null;
        }

        private static Recording FindCommittedRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            // [ERS-exempt] Physical by-id resolve of a route's bound dock-member recording
            // to read its immutable RouteConnectionWindow proof — not a visibility / supersede
            // scoped enumeration. Same physical-data-lookup rationale as MissionsWindowUI /
            // TimelineWindowUI / RecordingsTableUI (see scripts/ers-els-audit-allowlist.txt).
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
                if (committed[i] != null && string.Equals(committed[i].RecordingId, recordingId, StringComparison.Ordinal))
                    return committed[i];
            return null;
        }

        public void DrawIfOpen(Rect mainWindowRect)
        {
            if (!isOpen)
            {
                ReleaseInputLock();
                return;
            }

            if (windowRect.width < 1f)
            {
                float x = mainWindowRect.x + mainWindowRect.width + 10;
                windowRect = new Rect(x, mainWindowRect.y, 720, 320);
            }

            ParsekUI.HandleResizeDrag(ref windowRect, ref isResizing,
                MinWindowWidth, MinWindowHeight, "Structure window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                windowRect = ClickThruBlocker.GUILayoutWindow(
                    "ParsekStructureList".GetHashCode(),
                    windowRect,
                    DrawWindow,
                    "Parsek - " + title,
                    opaqueWindowStyle,
                    GUILayout.Width(windowRect.width),
                    GUILayout.Height(windowRect.height)
                );
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
            parentUI.LogWindowPosition("StructureList", ref lastWindowRect, windowRect);

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

        public void ReleaseInputLock()
        {
            if (!hasInputLock) return;
            InputLockManager.RemoveControlLock(InputLockId);
            hasInputLock = false;
        }

        private void DrawWindow(int windowID)
        {
            GUILayout.Space(5);

            if (steps.Count == 0)
            {
                GUILayout.Label(mode == TargetMode.Route
                    ? "Nothing to show (source recording unavailable)."
                    : "Nothing to show.");
                if (GUILayout.Button("Close", GUILayout.Width(132)))
                    Close();
                GUI.DragWindow();
                return;
            }

            // Header row.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Time", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Time));
            GUILayout.Label("Event", parentUI.GetColumnHeaderStyle(), GUILayout.ExpandWidth(true));
            GUILayout.Label("Location", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Location));
            GUILayout.Label("Vessel", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Vessel));
            GUILayout.EndHorizontal();

            // Step rows.
            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.ExpandHeight(true));
            GUILayout.BeginVertical(GUI.skin.box);
            for (int i = 0; i < steps.Count; i++)
            {
                StructureStep step = steps[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label(FormatTime(step.UT), GUILayout.Width(ColW_Time));
                GUILayout.Label(step.Label ?? "", GUILayout.ExpandWidth(true));
                GUILayout.Label(step.Location ?? "", GUILayout.Width(ColW_Location));
                GUILayout.Label(step.VesselName ?? "", GUILayout.Width(ColW_Vessel));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
            GUILayout.EndScrollView();

            // Bottom bar.
            GUILayout.BeginHorizontal();
            GUILayout.Label(string.Format(CultureInfo.InvariantCulture, "{0} steps", steps.Count));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(132)))
                Close();
            GUILayout.EndHorizontal();

            ParsekUI.DrawResizeHandle(windowRect, ref isResizing, "Structure window");
            GUI.DragWindow();
        }

        private void Close()
        {
            isOpen = false;
            ReleaseInputLock();
            ParsekLog.Verbose("UI", $"Structure window closed: mode={mode} target={targetId ?? "<null>"}");
        }

        private static string FormatTime(double ut)
        {
            // The route Origin pseudo-step has no single UT.
            if (double.IsNaN(ut)) return "-";
            return KSPUtil.PrintDateCompact(ut, true);
        }
    }
}
