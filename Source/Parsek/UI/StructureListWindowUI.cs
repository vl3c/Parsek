using System;
using System.Collections.Generic;
using System.Globalization;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Log window: a flat, chronological step list of one mission or supply route
    /// (launch, staging, dock / undock, deliveries, terminal), each with its time,
    /// status, and location. Opened from a mission's "Log" button (Missions tab) or a
    /// route's "Log (Route)" / "Log (Mission)" buttons (Logistics window). One reusable
    /// instance owned by <see cref="ParsekUI"/>; reopening retargets it. Read-only over
    /// already-recorded data; the ordered step list comes from the pure
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
        private const float ColW_Index = 28f;    // "#" step number, 1-based
        private const float ColW_Time = 110f;
        private const float ColW_Event = 0f;     // expand
        private const float ColW_Status = 95f;   // vessel situation (Orbiting / Landed / ...)
        private const float ColW_Location = 185f; // "SOI/body, biome"
        private const float ColW_Vessel = 140f;

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
        // path: only called on open (reopening retargets and rebuilds).
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
            // to read its immutable RouteConnectionWindow proof, not a visibility / supersede
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
                windowRect = new Rect(x, mainWindowRect.y, 770, 320);
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
                if (GUILayout.Button("Close"))
                    Close();
                GUI.DragWindow();
                return;
            }

            // Header row. Reserve the vertical-scrollbar gutter on the right so the fixed
            // column right-edges line up with the scrolled rows below (the same trick
            // RecordingsTableUI uses; the scrollview claims a fixed-width strip for its bar).
            float scrollbarWidth = GUI.skin.verticalScrollbar != null
                ? GUI.skin.verticalScrollbar.fixedWidth
                : 16f;
            if (scrollbarWidth <= 0f) scrollbarWidth = 16f;

            GUILayout.BeginHorizontal();
            GUILayout.Label("#", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Index));
            GUILayout.Label("Time", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Time));
            GUILayout.Label("Event", parentUI.GetColumnHeaderStyle(), GUILayout.ExpandWidth(true));
            GUILayout.Label("Status", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Status));
            GUILayout.Label("Location", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Location));
            GUILayout.Label("Vessel", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Vessel));
            GUILayout.Space(scrollbarWidth);
            GUILayout.EndHorizontal();

            // Step rows. Drawn directly in the scroll view (no GUI.skin.box wrapper, whose
            // left/right padding would shift the columns out of line with the header). The
            // vertical scrollbar is FORCED (alwaysShowVertical: true) so the gutter the
            // header reserved above is always actually taken; with auto scrollbars a short
            // list would show none and the rows would sit 16px right of the headers (same
            // reasoning as RecordingsTableUI's always-on vertical bar).
            scrollPos = GUILayout.BeginScrollView(scrollPos, false, true, GUILayout.ExpandHeight(true));
            for (int i = 0; i < steps.Count; i++)
            {
                StructureStep step = steps[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label((i + 1).ToString(CultureInfo.InvariantCulture), GUILayout.Width(ColW_Index));
                GUILayout.Label(FormatTime(step.UT), GUILayout.Width(ColW_Time));
                GUILayout.Label(step.Label ?? "", GUILayout.ExpandWidth(true));
                GUILayout.Label(step.Status ?? "", GUILayout.Width(ColW_Status));
                GUILayout.Label(step.Location ?? "", GUILayout.Width(ColW_Location));
                GUILayout.Label(step.VesselName ?? "", GUILayout.Width(ColW_Vessel));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            // Full-width Close, matching the other Parsek windows.
            if (GUILayout.Button("Close"))
                Close();

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
