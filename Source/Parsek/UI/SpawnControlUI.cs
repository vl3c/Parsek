using System.Collections.Generic;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Real Spawn Control window extracted from ParsekUI.
    /// Shows nearby spawn candidates with sort, warp, and departure controls.
    /// </summary>
    internal class SpawnControlUI
    {
        private readonly ParsekUI parentUI;

        // Spawn Control window
        private bool showSpawnControlWindow;
        private Rect spawnControlWindowRect;
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
            get { return spawnControlWindowRect; }
            set { spawnControlWindowRect = value; }
        }

        private bool spawnControlWindowHasInputLock;
        // Internal so the design-7.2 close set (ParsekUI.BuildGatedWindowCloseSet) can name it.
        internal const string SpawnControlInputLockId = "Parsek_SpawnControlWindow";

        // Spawn Control sort state
        private SpawnControlSortColumn spawnSortColumn = SpawnControlSortColumn.Distance;
        private bool spawnSortAscending = true;

        /// <summary>
        /// The candidate table's sort column (as an INDEX into
        /// <see cref="SpawnControlSortColumn"/>'s declaration order) and direction, for the
        /// automation-only <c>UiAction op=sort</c> seam op. An int for the reason
        /// <c>RecordingsTableUI.SortColumnIndexForTesting</c> is one.
        ///
        /// <para>No explicit invalidation: this window re-sorts whenever the live tuple
        /// differs from <c>cachedSortColumn</c> / <c>cachedSortAscending</c>, so writing the
        /// live fields is itself the trigger - which is also all the header click's own
        /// callback does beyond logging.</para>
        /// </summary>
        internal int SortColumnIndexForTesting
        {
            get { return (int)spawnSortColumn; }
            set
            {
                if (value < (int)SpawnControlSortColumn.Name
                    || value > (int)SpawnControlSortColumn.Status)
                    return;
                spawnSortColumn = (SpawnControlSortColumn)value;
            }
        }

        internal bool SortAscendingForTesting
        {
            get { return spawnSortAscending; }
            set { spawnSortAscending = value; }
        }
        private bool isResizingSpawnControlWindow;
        private Vector2 spawnControlScrollPos;

        // Cached sorted candidate list -- re-sorted only when source data or sort state changes
        private List<NearbySpawnCandidate> cachedSortedCandidates = new List<NearbySpawnCandidate>();
        private int cachedCandidateCount = -1;
        private int cachedProximityGeneration = -1;
        private SpawnControlSortColumn cachedSortColumn = SpawnControlSortColumn.Distance;
        private bool cachedSortAscending = true;

        // Window drag tracking for position logging
        private Rect lastSpawnControlWindowRect;

        // Spawn Control column widths (matches recordings window style)
        /// <summary>
        /// The key this window's IMGUI window id is hashed from. Named once and used at
        /// BOTH the <c>ClickThruBlocker.GUILayoutWindow</c> call below and
        /// <c>UiWindowHandle.GetWindowId</c> (wired by
        /// <c>ParsekTestCommandAddon.ResolveWindowHandle</c>), which the <c>UiAction op=find</c>
        /// seam uses to scope a captured GUI tree to THIS window's subtree. Two copies of
        /// the literal would let the seam search the wrong window's children and answer a
        /// plausible rect for a control in another window.
        /// </summary>
        internal const string WindowIdKey = "ParsekSpawnControl";

        // Craft expands; the Actions column holds one 100 px single, the house row-action
        // width. Spawn date fits a full "Y12, D426, 14:05".
        private const float SpawnColW_Dist = 60f;
        private const float SpawnColW_Speed = 70f;
        private const float SpawnColW_Spawns = 90f;
        private const float SpawnColW_SpawnDate = 110f;
        private const float SpawnColW_Status = 70f;
        private const float SpawnColW_Actions = 100f;

        // Bottom "hovered control help text" strip. See TooltipEchoBox for why it is a
        // permanently visible box of constant height. Single-line: at this window's
        // 750px first-open width every hover (Status cell, row Warp, Warp to Next Spawn)
        // fits a line for an ordinary craft name; long runtime vessel names overflow into
        // the strip's marquee instead.
        private readonly TooltipEchoBox tooltipEcho =
            new TooltipEchoBox(SpacingSmall, TooltipEchoBox.SingleLine);

        // Rect of the bottom row's "Warp to Next Spawn" button, captured on Repaint.
        // The strip draws above that row, so this is how its tooltip reaches the strip.
        private Rect warpButtonRect;

        private const float SpacingSmall = 3f;
        internal const float MinWindowWidth = 350f;
        internal const float DefaultWindowWidth = 750f;
        internal const float MinWindowHeight = 150f;

        public bool IsOpen
        {
            get { return showSpawnControlWindow; }
            set { showSpawnControlWindow = value; }
        }

        internal SpawnControlUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        internal static string ResolveAutoCloseReason(
            bool inFlight,
            bool hasFlight,
            int candidateCount)
        {
            if (!inFlight)
                return "not-in-flight";
            if (!hasFlight)
                return "flight-null";
            if (candidateCount <= 0)
                return "zero-candidates";
            return null;
        }

        internal static string FormatAutoCloseSummary(string reason, int candidateCount)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Real Spawn Control auto-close: reason={0} candidates={1}",
                string.IsNullOrEmpty(reason) ? "none" : reason,
                candidateCount);
        }

        private static void LogAutoClose(string reason, int candidateCount)
        {
            if (string.IsNullOrEmpty(reason))
                return;

            ParsekLog.VerboseOnChange("UI",
                "spawn-control-auto-close",
                reason,
                FormatAutoCloseSummary(reason, candidateCount));
        }

        public void DrawIfOpen(Rect mainWindowRect, ParsekFlight flight, bool inFlight)
        {
            if (!showSpawnControlWindow)
            {
                ReleaseInputLock();
                return;
            }

            // Auto-close when no nearby candidates
            int candidateCount = flight?.NearbySpawnCandidates?.Count ?? 0;
            string autoCloseReason = ResolveAutoCloseReason(
                inFlight,
                flight != null,
                candidateCount);
            if (!string.IsNullOrEmpty(autoCloseReason))
            {
                LogAutoClose(autoCloseReason, candidateCount);
                showSpawnControlWindow = false;
                ReleaseInputLock();
                return;
            }

            if (spawnControlWindowRect.width < 1f)
            {
                // First open: tall enough for the rows it opens with, no taller.
                float x = mainWindowRect.x + mainWindowRect.width + 10;
                float h = SpawnControlPresentation.FirstOpenHeight(candidateCount);
                spawnControlWindowRect = new Rect(x, mainWindowRect.y, DefaultWindowWidth, h);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI",
                    $"Real Spawn Control window initial position: x={spawnControlWindowRect.x.ToString("F0", ic)} y={spawnControlWindowRect.y.ToString("F0", ic)} h={h.ToString("F0", ic)} rows={candidateCount.ToString(ic)}");
            }

            ParsekUI.HandleResizeDrag(ref spawnControlWindowRect, ref isResizingSpawnControlWindow,
                MinWindowWidth, MinWindowHeight, "Real Spawn Control window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                spawnControlWindowRect = ClickThruBlocker.GUILayoutWindow(
                    WindowIdKey.GetHashCode(),
                    spawnControlWindowRect,
                    (id) => DrawSpawnControlWindow(id, flight),
                    "Parsek - Real Spawn Control",
                    opaqueWindowStyle,
                    GUILayout.Width(spawnControlWindowRect.width),
                    GUILayout.Height(spawnControlWindowRect.height)
                );
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
            parentUI.LogWindowPosition("SpawnControl", ref lastSpawnControlWindowRect, spawnControlWindowRect);

            if (spawnControlWindowRect.Contains(Event.current.mousePosition))
            {
                if (!spawnControlWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, SpawnControlInputLockId);
                    spawnControlWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        /// <summary>
        /// Whether this window currently holds its KSP input lock (diagnostic read for the
        /// design-7.2 close handler; see ParsekUI.BuildGatedWindowCloseSet).
        /// </summary>
        internal bool HasInputLock => spawnControlWindowHasInputLock;

        public void ReleaseInputLock()
        {
            if (!spawnControlWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(SpawnControlInputLockId);
            spawnControlWindowHasInputLock = false;
        }

        private void DrawSpawnSortableHeader(string label, SpawnControlSortColumn col, float width)
        {
            DrawSpawnSortableHeader(label, col, false, width);
        }

        private void DrawSpawnSortableHeader(string label, SpawnControlSortColumn col, bool expand)
        {
            DrawSpawnSortableHeader(label, col, expand, 0);
        }

        private void DrawSpawnSortableHeader(string label, SpawnControlSortColumn col, bool expand, float width)
        {
            parentUI.DrawSortableHeaderCore(label, col, ref spawnSortColumn, ref spawnSortAscending, width, expand, () =>
            {
                ParsekLog.Verbose("UI", $"Spawn sort changed: column={spawnSortColumn}, ascending={spawnSortAscending}");
            });
        }

        private void DrawSpawnControlWindow(int windowID, ParsekFlight flight)
        {
            // Breathing room below the title bar - matches Timeline's visual spacing.
            GUILayout.Space(5);

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            double currentUT = Planetarium.GetUniversalTime();
            var candidates = flight.NearbySpawnCandidates;

            // Re-sort when candidate list, sort state, or departure info changes
            int gen = flight.ProximityCheckGeneration;
            if (candidates.Count != cachedCandidateCount
                || gen != cachedProximityGeneration
                || spawnSortColumn != cachedSortColumn
                || spawnSortAscending != cachedSortAscending)
            {
                cachedSortedCandidates = SpawnControlPresentation.SortCandidates(
                    candidates,
                    spawnSortColumn,
                    spawnSortAscending,
                    currentUT,
                    ParsekFlight.NearbySpawnRadius,
                    ParsekFlight.MaxRelativeSpeed);
                cachedCandidateCount = candidates.Count;
                cachedProximityGeneration = gen;
                cachedSortColumn = spawnSortColumn;
                cachedSortAscending = spawnSortAscending;
            }

            DrawSpawnCandidateTable(cachedSortedCandidates, currentUT, ic, flight);

            DrawSpawnControlBottomBar(candidates, currentUT, flight);
        }

        /// <summary>
        /// The candidate table: ONE dark body box holding the pinned column-header row and
        /// the scroll view of candidate rows, so the header and the rows share one
        /// container and the box frames both (a box around the rows alone started 4px
        /// left of the header cells above it). The header row reserves the vertical
        /// scrollbar gutter the scroll view below it forces on, and the body cells use
        /// the shared table cell style, so every cell's text starts at its header's text
        /// x. Contract: ParsekUI.TableRowHorizontalInsetPx / GetTableCellStyle.
        /// </summary>
        private void DrawSpawnCandidateTable(List<NearbySpawnCandidate> sorted,
            double currentUT, System.Globalization.CultureInfo ic, ParsekFlight flight)
        {
            GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle(), GUILayout.ExpandHeight(true));
            DrawSpawnColumnHeader();
            // The vertical scrollbar is FORCED (alwaysShowVertical: true) so the gutter
            // the header reserved above is always actually taken; with auto scrollbars a
            // short list would show none and the rows would sit a scrollbar-width right
            // of the headers. The scroll view's own style carries no horizontal margin, so
            // inside the zero-padding box it starts at the box's edge as the header does.
            spawnControlScrollPos = GUILayout.BeginScrollView(
                spawnControlScrollPos, false, true,
                GUI.skin.horizontalScrollbar, GUI.skin.verticalScrollbar,
                parentUI.GetTableScrollViewStyle(), GUILayout.ExpandHeight(true));
            // An empty list keeps its column headers over one grey sentence row (the house
            // empty-table shape). The window self-closes on zero candidates before this
            // draws, so the row shows only if that rule ever changes.
            if (sorted.Count == 0)
                DrawSpawnEmptyRow();
            else
                DrawSpawnCandidateRows(sorted, currentUT, ic, flight);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        // Sortable column headers on distinct keys. Spawn date shows the same moment as
        // Spawns, so it is a plain header rather than a second header for one key; Actions
        // heads the row's Warp button. Opened with the header-row container (shared inset +
        // scrollbar gutter).
        private void DrawSpawnColumnHeader()
        {
            GUILayout.BeginHorizontal(parentUI.GetTableHeaderRowStyle());
            DrawSpawnSortableHeader("Craft", SpawnControlSortColumn.Name, true);
            DrawSpawnSortableHeader("Dist", SpawnControlSortColumn.Distance, SpawnColW_Dist);
            DrawSpawnSortableHeader("Speed", SpawnControlSortColumn.RelativeSpeed, SpawnColW_Speed);
            DrawSpawnSortableHeader("Spawns", SpawnControlSortColumn.SpawnTime, SpawnColW_Spawns);
            GUILayout.Label(SpawnDateColumnHeaderText, parentUI.GetColumnHeaderStyle(), GUILayout.Width(SpawnColW_SpawnDate));
            DrawSpawnSortableHeader("Status", SpawnControlSortColumn.Status, SpawnColW_Status);
            GUILayout.Label(ActionsColumnHeaderText, parentUI.GetColumnHeaderStyle(), GUILayout.Width(SpawnColW_Actions));
            GUILayout.EndHorizontal();
        }

        /// <summary>Header of the column holding each row's Warp button.</summary>
        internal const string ActionsColumnHeaderText = "Actions";

        /// <summary>Header of the exact-date column (not sortable: Spawns sorts the same
        /// moment).</summary>
        internal const string SpawnDateColumnHeaderText = "Spawn date";

        // The empty table's one body row, under the column headers: one grey sentence.
        private void DrawSpawnEmptyRow()
        {
            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            GUILayout.Label("No nearby craft to spawn.", parentUI.GetEmptyStateStyle());
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// The house calendar date for a row's time cells and hovers, guarded so a caller
        /// off the KSP runtime never throws (the presentation then prints the raw UT).
        /// </summary>
        internal static string FormatRowDate(double ut)
        {
            try { return KSPUtil.PrintDateCompact(ut, true); }
            catch (System.Exception) { return null; }
        }

        private static readonly System.Func<double, string> RowDateFormatter = FormatRowDate;

        private void DrawSpawnCandidateRows(List<NearbySpawnCandidate> sorted,
            double currentUT, System.Globalization.CultureInfo ic, ParsekFlight flight)
        {
            GUIStyle cellStyle = parentUI.GetTableCellStyle();
            for (int i = 0; i < sorted.Count; i++)
            {
                var cand = sorted[i];
                SpawnCandidateRowPresentation row =
                    SpawnControlPresentation.BuildRowPresentation(
                        cand, currentUT,
                        ParsekFlight.NearbySpawnRadius,
                        ParsekFlight.MaxRelativeSpeed,
                        RowDateFormatter);
                // Both time cells show the moment the row's Warp acts on (a departure for a
                // craft that leaves first), the same value the Spawns sort reads.
                double delta = row.EffectiveUT - currentUT;

                // Same shared row container as the header row above (one inset).
                GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
                GUILayout.Label(cand.vesselName, cellStyle, GUILayout.ExpandWidth(true));
                GUILayout.Label(
                    SpawnControlPresentation.FormatDistance(cand.distance, ic),
                    cellStyle, GUILayout.Width(SpawnColW_Dist));
                GUILayout.Label(
                    SpawnControlPresentation.FormatRelativeSpeed(cand.relativeSpeed, ic),
                    cellStyle, GUILayout.Width(SpawnColW_Speed));

                // The house countdown, in the house countdown amber.
                Color savedCountdownColor = GUI.contentColor;
                GUI.contentColor = ParsekUI.CountdownTextColor;
                GUILayout.Label(ParsekTimeFormat.FormatCountdown(delta),
                    cellStyle, GUILayout.Width(SpawnColW_Spawns));
                GUI.contentColor = savedCountdownColor;

                GUILayout.Label(row.DateText, cellStyle, GUILayout.Width(SpawnColW_SpawnDate));

                // Status: one word with its reason on hover. Ready is green, Leaves takes
                // the countdown amber (a departure still ahead), Leaving is orange.
                Color savedStatusColor = GUI.contentColor;
                GUI.contentColor = StatusWordColor(row.Status, savedStatusColor);
                GUILayout.Label(new GUIContent(row.StatusText, row.StatusHover),
                    cellStyle, GUILayout.Width(SpawnColW_Status));
                GUI.contentColor = savedStatusColor;

                // Actions: one Warp button. Live, its hover says where it jumps; greyed,
                // DisabledHoverEcho carries the Status reason (a disabled button's own
                // tooltip is not proven to publish).
                GUI.enabled = row.WarpButtonEnabled;
                bool warpClicked = GUILayout.Button(
                    new GUIContent(row.WarpButtonLabel, row.WarpButtonHover),
                    GUILayout.Width(SpawnColW_Actions));
                DisabledHoverEcho.CarryLastControl(
                    row.WarpButtonEnabled, row.WarpButtonDisabledReason);
                if (warpClicked)
                    ExecuteRowWarp(cand, row, flight);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
        }

        /// <summary>The Status word's colour: green Ready, amber Leaves, orange Leaving,
        /// the cell's own colour otherwise.</summary>
        private static Color StatusWordColor(SpawnCandidateStatus status, Color plain)
        {
            switch (status)
            {
                case SpawnCandidateStatus.Ready:
                    return ParsekUI.StatusColor(ParsekUI.StatusColorKind.Green);
                case SpawnCandidateStatus.Leaves:
                    return ParsekUI.CountdownTextColor;
                case SpawnCandidateStatus.Leaving:
                    return LeavingTextColor;
                default:
                    return plain;
            }
        }

        /// <summary>The orange of a departure that is due now.</summary>
        internal static readonly Color LeavingTextColor = new Color(1f, 0.65f, 0.2f);

        /// <summary>
        /// The row warp button's click body: log the warp and hand it to the flight
        /// controller. The ONE body behind both the drawn button and the automation-only
        /// <c>UiAction op=warp</c> seam op (<see cref="TryPressFirstRowWarpForTesting"/>), so
        /// the seam cannot drift from what a player's click does.
        /// </summary>
        private static void ExecuteRowWarp(NearbySpawnCandidate cand,
            SpawnCandidateRowPresentation row, ParsekFlight flight)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (row.UsesDepartureWarp)
            {
                ParsekLog.Info("UI",
                    string.Format(ic,
                        "Real Spawn Control: warp to departure '{0}' recording #{1} depUT={2:F1}",
                        cand.vesselName, cand.recordingIndex, cand.departureUT));
                flight.WarpToDeparture(cand.recordingIndex, cand.departureUT);
            }
            else
            {
                ParsekLog.Info("UI",
                    string.Format(ic,
                        "Real Spawn Control: warp to '{0}' recording #{1}",
                        cand.vesselName, cand.recordingIndex));
                flight.WarpToRecordingEnd(cand.recordingIndex);
            }
        }

        /// <summary>
        /// Automation-only: press the FIRST row's warp button (first in the table's current
        /// sort order, which <c>UiAction op=sort</c> can set), exactly as a click would.
        /// Refuses, pressing nothing, when there is no row or when the button is drawn
        /// disabled - a greyed button cannot be clicked, so the seam must not click it
        /// either. The rows come from the same sort and the same row presentation the draw
        /// pass uses.
        /// </summary>
        /// <param name="refusal">Null on a press; otherwise
        /// <see cref="WarpRefusalNoRow"/> or <see cref="WarpRefusalButtonDisabled"/>.</param>
        /// <param name="disabledReason">The button's own disabled-hover text on a
        /// <see cref="WarpRefusalButtonDisabled"/> refusal.</param>
        internal bool TryPressFirstRowWarpForTesting(ParsekFlight flight,
            out NearbySpawnCandidate cand, out SpawnCandidateRowPresentation row,
            out string refusal, out string disabledReason)
        {
            cand = default(NearbySpawnCandidate);
            row = default(SpawnCandidateRowPresentation);
            refusal = null;
            disabledReason = null;
            var candidates = flight?.NearbySpawnCandidates;
            if (candidates == null || candidates.Count == 0)
            {
                refusal = WarpRefusalNoRow;
                return false;
            }
            double currentUT = ReadCurrentUT();
            List<NearbySpawnCandidate> sorted = SpawnControlPresentation.SortCandidates(
                candidates, spawnSortColumn, spawnSortAscending, currentUT,
                ParsekFlight.NearbySpawnRadius, ParsekFlight.MaxRelativeSpeed);
            cand = sorted[0];
            row = SpawnControlPresentation.BuildRowPresentation(
                cand, currentUT,
                ParsekFlight.NearbySpawnRadius,
                ParsekFlight.MaxRelativeSpeed,
                RowDateFormatter);
            if (!row.WarpButtonEnabled)
            {
                refusal = WarpRefusalButtonDisabled;
                disabledReason = row.WarpButtonDisabledReason;
                return false;
            }
            ExecuteRowWarp(cand, row, flight);
            return true;
        }

        /// <summary>
        /// Automation-only (the <c>RealSpawn</c> seam verb): find the table row of ONE
        /// recording by id and build its presentation exactly as the draw pass does (same
        /// candidate list, same <see cref="SpawnControlPresentation.BuildRowPresentation"/>
        /// gates). Presses nothing; false when the table draws no row for the recording.
        /// </summary>
        internal static bool TryFindRowForRecordingForTesting(ParsekFlight flight,
            string recordingId, double currentUT,
            out NearbySpawnCandidate cand, out SpawnCandidateRowPresentation row,
            out int candidateCount)
        {
            cand = default(NearbySpawnCandidate);
            row = default(SpawnCandidateRowPresentation);
            var candidates = flight?.NearbySpawnCandidates;
            candidateCount = candidates?.Count ?? 0;
            if (candidates == null || string.IsNullOrEmpty(recordingId))
                return false;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].recordingId != recordingId)
                    continue;
                cand = candidates[i];
                row = SpawnControlPresentation.BuildRowPresentation(
                    cand, currentUT,
                    ParsekFlight.NearbySpawnRadius,
                    ParsekFlight.MaxRelativeSpeed,
                    RowDateFormatter);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Automation-only: press one row's warp button through its own click body
        /// (<see cref="ExecuteRowWarp"/>). The caller has already refused a disabled
        /// button, as <see cref="TryPressFirstRowWarpForTesting"/> does.
        /// </summary>
        internal static void PressRowWarpForTesting(NearbySpawnCandidate cand,
            SpawnCandidateRowPresentation row, ParsekFlight flight)
            => ExecuteRowWarp(cand, row, flight);

        // Kept out of TryPressFirstRowWarpForTesting's body so the headless no-row path
        // never JITs a Planetarium reference (mono runs a failing KSP static initializer at
        // JIT of the CALLING method).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static double ReadCurrentUT() => Planetarium.GetUniversalTime();

        /// <summary><see cref="TryPressFirstRowWarpForTesting"/>: the table has no row.</summary>
        internal const string WarpRefusalNoRow = "warp-no-candidate-row";

        /// <summary><see cref="TryPressFirstRowWarpForTesting"/>: the first row's warp
        /// button is drawn disabled (too fast, leaving now, or already past its UT).</summary>
        internal const string WarpRefusalButtonDisabled = "warp-button-disabled";

        private void DrawSpawnControlBottomBar(List<NearbySpawnCandidate> candidates,
            double currentUT, ParsekFlight flight)
        {
            // Bottom section -- pinned to window bottom
            GUILayout.FlexibleSpace();

            var next = SelectiveSpawnUI.FindNextSpawnCandidate(candidates, currentUT, ParsekFlight.NearbySpawnRadius, ParsekFlight.MaxRelativeSpeed);
            string tooltip = next != null
                ? SelectiveSpawnUI.FormatNextSpawnTooltip(next, currentUT) : "";

            // Bottom "hovered control help text" strip (shared house helper). Fixed
            // single-line height, always present, drawn directly above the button row so
            // Close stays the window's last content row.
            //
            // The row Status cells and Warp buttons draw above the strip and reach it
            // through GUI.tooltip. "Warp to Next Spawn" sits BELOW it, so its hover cannot
            // (the hovered control fills GUI.tooltip in as it draws). Hand that text in
            // explicitly instead, using the button's rect from the previous pass against
            // the live pointer - the same hover test IMGUI itself does. The rect is captured on
            // Repaint below and only moves when the window is resized.
            string bottomBarEcho =
                warpButtonRect.width > 0f && warpButtonRect.Contains(Event.current.mousePosition)
                    ? tooltip
                    : null;
            tooltipEcho.Draw(bottomBarEcho);

            GUILayout.BeginHorizontal();
            GUI.enabled = next != null;
            if (GUILayout.Button(new GUIContent("Warp to Next Spawn", tooltip),
                GUILayout.ExpandWidth(true)))
            {
                ParsekLog.Info("UI", "Real Spawn Control: Warp to Next Spawn clicked");
                flight.WarpToNextCraftSpawn();
            }
            if (Event.current.type == EventType.Repaint)
                warpButtonRect = GUILayoutUtility.GetLastRect();
            GUI.enabled = true;
            if (GUILayout.Button("Close", GUILayout.Width(132)))
            {
                showSpawnControlWindow = false;
                ParsekLog.Verbose("UI", "Real Spawn Control window closed");
            }
            GUILayout.EndHorizontal();

            ParsekUI.DrawResizeHandle(spawnControlWindowRect, ref isResizingSpawnControlWindow,
                "Real Spawn Control window");

            GUI.DragWindow();
        }
    }
}
