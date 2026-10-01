using System;
using System.Collections.Generic;
using System.Globalization;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Log window: a flat, chronological step list of one mission or supply route
    /// (launch, staging, dock / undock, deliveries, terminal), each with its time,
    /// location and vessel. Opened from a mission's "Log" button (Missions tab) or a
    /// route's "Log (Route)" / "Log (Mission)" buttons (Logistics window). One reusable
    /// instance owned by <see cref="ParsekUI"/>; reopening retargets it. Read-only over
    /// already-recorded data; the ordered step list comes from the pure
    /// <see cref="MissionStructureListBuilder"/> / <see cref="Logistics.RouteStructureListBuilder"/>.
    /// </summary>
    internal class StructureListWindowUI
    {
        /// <summary>Internal rather than private because the gallery snapshot below
        /// carries one, and an internal struct cannot have a private-typed field.</summary>
        internal enum TargetMode { None, Mission, Route }

        private readonly ParsekUI parentUI;

        private bool isOpen;
        private Rect windowRect;
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
            get { return windowRect; }
            set { windowRect = value; }
        }

        private bool hasInputLock;
        private bool isResizing;
        private Vector2 scrollPos;
        private Rect lastWindowRect;

        private const string InputLockId = "Parsek_StructureListWindow";

        // Current target + cached built step list (rebuilt when the target changes). `title`
        // is the TARGET's name (the mission or route); the window title is built from it by
        // BuildWindowTitle.
        private TargetMode mode = TargetMode.None;
        private string targetId;
        private string title = "";
        private List<StructureStep> steps = new List<StructureStep>();
        // Time cells, derived once whenever `steps` is (re)assigned, never per frame: the
        // first row's date, then each later row's elapsed time from it.
        private string[] timeCells = new string[0];

        // Bottom hover-help strip: the Event cell carries the FULL piece list as its
        // tooltip when the cell had to shorten it. Single-line (see TooltipEchoBox).
        private readonly TooltipEchoBox tooltipEcho =
            new TooltipEchoBox(TooltipEchoBox.DefaultSpacing, TooltipEchoBox.SingleLine);

        // Row-cell label: the shared table cell style (the boxed column header's own
        // horizontal padding, so body text starts at the header text's x) with the
        // vertical padding dropped, which keeps this log's compact row pitch. Built
        // lazily in OnGUI from the shared style, rebuilt when that style is rebuilt.
        private GUIStyle bodyCellLabel;
        private GUIStyle bodyCellLabelSource;

        // Column widths (match the recordings / spawn window conventions).
        /// <summary>
        /// The key this window's IMGUI window id is hashed from. Named once and used at
        /// BOTH the <c>ClickThruBlocker.GUILayoutWindow</c> call below and
        /// <c>UiWindowHandle.GetWindowId</c> (wired by
        /// <c>ParsekTestCommandAddon.ResolveWindowHandle</c>), which the <c>UiAction op=find</c>
        /// seam uses to scope a captured GUI tree to THIS window's subtree. Two copies of
        /// the literal would let the seam search the wrong window's children and answer a
        /// plausible rect for a control in another window.
        /// </summary>
        internal const string WindowIdKey = "ParsekStructureList";

        private const float ColW_Time = 110f;    // first row: date; later rows: "T+h:mm:ss"
        private const float ColW_Event = 0f;     // expand
        private const float ColW_Location = 185f; // "Kerbin, Launch Pad" / "Kerbin orbit"
        private const float ColW_Vessel = 160f;

        /// <summary>First-open width. The Event column gets what the three fixed columns
        /// leave, which holds <see cref="MissionStructureListBuilder.EventCellCharBudget"/>
        /// characters unshortened.</summary>
        internal const float DefaultWindowWidth = 900f;
        private const float DefaultWindowHeight = 320f;

        /// <summary>The empty-state line of a mission Log.</summary>
        internal const string EmptyMissionText = "This mission has no recorded flight.";
        /// <summary>The empty-state line of a route Log.</summary>
        internal const string EmptyRouteText = "Nothing to show (source recording unavailable).";
        /// <summary>The empty-state line before any target was given (the bare seam open).</summary>
        internal const string EmptyNoTargetText = "Nothing to show.";

        /// <summary>The empty-state line for a target mode. Pure.</summary>
        internal static string EmptyText(TargetMode targetMode)
        {
            if (targetMode == TargetMode.Route) return EmptyRouteText;
            if (targetMode == TargetMode.Mission) return EmptyMissionText;
            return EmptyNoTargetText;
        }

        internal const float MinWindowWidth = 420f;
        internal const float MinWindowHeight = 160f;

        /// <summary>The window title's fixed part: this window is the Log both the Missions
        /// "Log" button and the Logistics "Log (Route)" / "Log (Mission)" buttons open. (Its
        /// seam token stays <c>structure</c>, the name of the class it draws from.)</summary>
        internal const string WindowTitlePrefix = "Parsek - Log";

        /// <summary>
        /// The window title for a target name: <c>"Parsek - Log: Kerbal X"</c> for a mission,
        /// <c>"Parsek - Log: Mun Supply"</c> for a route, the bare <c>"Parsek - Log"</c> when
        /// there is no name. Pure.
        /// </summary>
        internal static string BuildWindowTitle(string targetName)
        {
            return string.IsNullOrEmpty(targetName)
                ? WindowTitlePrefix
                : WindowTitlePrefix + ": " + targetName;
        }

        /// <summary>The title the window draws right now, for tests and the census.</summary>
        internal string WindowTitleForTesting => BuildWindowTitle(title);

        public bool IsOpen
        {
            get { return isOpen; }
            set { isOpen = value; }
        }

        internal StructureListWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        /// <summary>
        /// How many rows the last <c>OpenFor*</c> rebuilt. The difference between the empty
        /// chrome the first census photographed and a populated Log: <c>op=target</c>
        /// reports it so a lane can tell "opened on a target that resolved to nothing" from
        /// "opened on a target with 14 steps" without reading the PNG.
        /// </summary>
        internal int StepCountForTesting => steps != null ? steps.Count : 0;

        /// <summary>Which target mode the window is in, for the same report. Empty when it
        /// has never been opened on a target.</summary>
        internal string TargetModeForTesting => mode.ToString();

        internal void OpenForMission(string treeId, string displayTitle)
        {
            mode = TargetMode.Mission;
            targetId = treeId;
            title = displayTitle ?? "";
            Rebuild();
            isOpen = true;
            ParsekLog.Info("UI",
                $"Structure window opened: mode=Mission tree={treeId ?? "<null>"} steps={steps.Count} " +
                $"title='{BuildWindowTitle(title)}'");
        }

        internal void OpenForRoute(string routeId, string displayTitle)
        {
            mode = TargetMode.Route;
            targetId = routeId;
            title = displayTitle ?? "";
            Rebuild();
            isOpen = true;
            ParsekLog.Info("UI",
                $"Structure window opened: mode=Route route={routeId ?? "<null>"} steps={steps.Count} " +
                $"title='{BuildWindowTitle(title)}'");
        }

        // ------------------------- the GUI state gallery seam -------------------------
        //
        // This window has NO invalidation key at all - `steps` is rebuilt only by
        // OpenForMission / OpenForRoute - so a mocked step list survives indefinitely and
        // needs no cache suppression. What it does need is a way IN that does not resolve
        // a target out of the store, which is what these three give the automation-only
        // `UiAction op=mock` applier. They are unreachable in a player build: nothing
        // calls them but that applier, which is inert unless PARSEK_TEST_COMMANDS=1.

        /// <summary>Everything the target state consists of, so a mock's restore is a
        /// complete inverse rather than a guess.</summary>
        internal struct GalleryTargetSnapshot
        {
            internal TargetMode Mode;
            internal string TargetId;
            internal string Title;
            internal List<StructureStep> Steps;
            internal bool IsOpen;
        }

        /// <summary>Captures the current target BEFORE a mock overwrites it.</summary>
        internal GalleryTargetSnapshot CaptureGalleryTarget()
            => new GalleryTargetSnapshot
            {
                Mode = mode,
                TargetId = targetId,
                Title = title,
                Steps = steps,
                IsOpen = isOpen,
            };

        /// <summary>Puts a captured target back.</summary>
        internal void RestoreGalleryTarget(GalleryTargetSnapshot snapshot)
        {
            mode = snapshot.Mode;
            targetId = snapshot.TargetId;
            title = snapshot.Title ?? "";
            SetSteps(snapshot.Steps ?? new List<StructureStep>());
            isOpen = snapshot.IsOpen;
        }

        /// <summary>
        /// Opens the window on a supplied step list, bypassing <see cref="Rebuild"/>.
        ///
        /// <para>It sets <c>targetId</c> to null on purpose: there is no real target, and
        /// a fabricated id would be a token some later reader could try to resolve. Every
        /// other field the draw method reads - the mode (which picks the empty-list
        /// wording), the title and the steps - is supplied.</para>
        /// </summary>
        internal void OpenWithGallerySteps(bool routeMode, string displayTitle,
                                           List<StructureStep> mockedSteps)
        {
            mode = routeMode ? TargetMode.Route : TargetMode.Mission;
            targetId = null;
            title = displayTitle ?? "";
            SetSteps(mockedSteps ?? new List<StructureStep>());
            isOpen = true;
        }

        private void SetSteps(List<StructureStep> newSteps)
        {
            steps = newSteps ?? new List<StructureStep>();
            timeCells = StructureTimeFormatter.FormatStepTimes(steps, FormatDate);
        }

        /// <summary>The Time cells the window draws right now, for tests and the census.</summary>
        internal string[] TimeCellsForTesting => timeCells;

        // Resolves the target's data and (re)builds the step list. Kept off the per-frame
        // path: only called on open (reopening retargets and rebuilds).
        private void Rebuild()
        {
            var built = new List<StructureStep>();
            if (mode == TargetMode.Mission)
            {
                RecordingTree tree = FindTree(targetId);
                if (tree != null)
                {
                    MissionStructure structure = MissionStructureBuilder.Build(tree);
                    string treeId = tree.Id;
                    built = MissionStructureListBuilder.Build(
                        tree, structure, ResolvePartTitle,
                        (bp, viewerId) => ResolveMergePartner(treeId, bp, viewerId));
                }
            }
            else if (mode == TargetMode.Route)
            {
                if (Logistics.RouteStore.TryGetRoute(targetId, out Logistics.Route route))
                    built = RouteStructureListBuilder.Build(
                        route, FindCommittedRecording, VesselSpawner.TryResolveBiome);
            }
            SetSteps(built);
        }

        // Internal part name -> the player-facing part title. Part names in recordings are
        // already the runtime dot-form; the replace keeps a cfg-form name resolving too.
        // Called once per distinct name per build (the builder caches), never per frame.
        private static string ResolvePartTitle(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return null;
            AvailablePart info = PartLoader.getPartInfoByName(partName.Replace('_', '.'));
            return info != null && !string.IsNullOrEmpty(info.title) ? info.title : null;
        }

        // The other side of a Dock branch point, through the dock-event graph (ERS-scoped by
        // its host cache, which rebuilds only when the committed topology moved).
        private static string ResolveMergePartner(string treeId, BranchPoint bp, string viewerId)
        {
            return MissionStructureListBuilder.DescribeMergePartnerFromGraph(
                DockEventGraphCache.GetOrBuild(), treeId, bp, viewerId,
                MissionsWindowUI.ResolvePartnerMissionName);
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
                windowRect = new Rect(x, mainWindowRect.y, DefaultWindowWidth, DefaultWindowHeight);
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
                    WindowIdKey.GetHashCode(),
                    windowRect,
                    DrawWindow,
                    BuildWindowTitle(title),
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
            GUIStyle sharedCell = parentUI.GetTableCellStyle();
            if (bodyCellLabel == null || !ReferenceEquals(bodyCellLabelSource, sharedCell))
            {
                bodyCellLabel = new GUIStyle(sharedCell)
                {
                    padding = new RectOffset(
                        sharedCell.padding.left, sharedCell.padding.right, 0, 0)
                };
                bodyCellLabelSource = sharedCell;
            }

            GUILayout.Space(5);

            if (steps.Count == 0)
            {
                GUILayout.Label(EmptyText(mode));
                if (GUILayout.Button("Close"))
                    Close();
                GUI.DragWindow();
                return;
            }

            DrawStepTable();

            // Hover-help strip, drawn after the rows so the live GUI.tooltip read sees a
            // hovered Event cell, and directly above Close (the house ordering).
            tooltipEcho.Draw();

            // Full-width Close, matching the other Parsek windows.
            if (GUILayout.Button("Close"))
                Close();

            ParsekUI.DrawResizeHandle(windowRect, ref isResizing, "Structure window");
            GUI.DragWindow();
        }

        /// <summary>
        /// The step table: ONE dark body box holding the pinned column-header row and the
        /// scroll view of step rows, so the header and the rows share one container and
        /// the box frames both. The header row reserves the vertical-scrollbar gutter the
        /// scroll view below it forces on, so the fixed column edges AND the expanding
        /// Event column line up with the scrolled rows, and the body cells take the shared
        /// table cell style, so every cell's text starts at its header's text x.
        /// Contract: ParsekUI.TableRowHorizontalInsetPx / GetTableCellStyle.
        /// </summary>
        private void DrawStepTable()
        {
            GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle(), GUILayout.ExpandHeight(true));
            DrawColumnHeader();
            // The vertical scrollbar is FORCED (alwaysShowVertical: true) so the gutter the
            // header reserved above is always actually taken; with auto scrollbars a short
            // list would show none and the rows would sit 16px right of the headers. The
            // scroll view's own style carries no horizontal margin, so inside the
            // zero-padding box it starts at the box's edge as the header row does.
            scrollPos = GUILayout.BeginScrollView(scrollPos, false, true,
                GUI.skin.horizontalScrollbar, GUI.skin.verticalScrollbar,
                parentUI.GetTableScrollViewStyle(), GUILayout.ExpandHeight(true));
            DrawStepRows();
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawColumnHeader()
        {
            GUILayout.BeginHorizontal(parentUI.GetTableHeaderRowStyle());
            GUILayout.Label("Time", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Time));
            GUILayout.Label("Event", parentUI.GetColumnHeaderStyle(), GUILayout.ExpandWidth(true));
            GUILayout.Label("Location", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Location));
            GUILayout.Label("Vessel", parentUI.GetColumnHeaderStyle(), GUILayout.Width(ColW_Vessel));
            GUILayout.EndHorizontal();
        }

        private void DrawStepRows()
        {
            GUIStyle cellStyle = bodyCellLabel;
            for (int i = 0; i < steps.Count; i++)
            {
                StructureStep step = steps[i];
                // Same shared row container as the header row above (one inset).
                GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
                string time = i < timeCells.Length ? timeCells[i] : "";
                GUILayout.Label(time, cellStyle, GUILayout.Width(ColW_Time));
                GUILayout.Label(new GUIContent(step.Label ?? "", step.Tooltip ?? ""),
                    cellStyle, GUILayout.ExpandWidth(true));
                GUILayout.Label(step.Location ?? "", cellStyle, GUILayout.Width(ColW_Location));
                GUILayout.Label(step.VesselName ?? "", cellStyle, GUILayout.Width(ColW_Vessel));
                GUILayout.EndHorizontal();
            }
        }

        private void Close()
        {
            isOpen = false;
            ReleaseInputLock();
            ParsekLog.Verbose("UI", $"Structure window closed: mode={mode} target={targetId ?? "<null>"}");
        }

        // The first row's date: the Missions start-time cell's formatter.
        private static string FormatDate(double ut)
        {
            return KSPUtil.PrintDateCompact(ut, true);
        }
    }
}
