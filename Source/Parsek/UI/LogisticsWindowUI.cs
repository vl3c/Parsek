using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Logistics window: a Supply Routes management surface with three sections
    /// driven by route enablement.
    /// <list type="bullet">
    ///   <item><b>Active Routes</b> - enabled, auto-dispatching (including the
    ///   blocked-but-active wait states and the hard-broken states, distinguished
    ///   by status color). Action: Pause. A blocked cycle still flies the ghost
    ///   (the world looks busy) but transfers/charges nothing; the status text
    ///   names the blocking reason.</item>
    ///   <item><b>Paused Routes</b> - stored but not auto-dispatching. Actions:
    ///   Send Once (fire one cycle when conditions allow, then stay Paused),
    ///   Activate (turn on periodic dispatch), Delete.</item>
    ///   <item><b>Candidates</b> - derived (not stored) from fully-sealed,
    ///   eligible Supply Run trees that are not already promoted. Action: Create
    ///   Route (promotes to a Paused route).</item>
    /// </list>
    /// Rows use the Recordings-window caret style (click the name to expand a
    /// detail panel). Available in both FLIGHT and SPACECENTER. Mirrors the
    /// ClickThruBlocker / resize / input-lock pattern from <see cref="SpawnControlUI"/>.
    /// </summary>
    internal class LogisticsWindowUI
    {
        private readonly ParsekUI parentUI;

        private bool showWindow;
        private Rect windowRect;
        private bool windowHasInputLock;
        private const string InputLockId = "Parsek_LogisticsWindow";

        private bool isResizing;
        private Vector2 scrollPos;
        private Rect lastWindowRect;

        // Expand/collapse state, keyed by route Id (routes) or "cand:"+treeId (candidates).
        private readonly HashSet<string> expandedRows = new HashSet<string>();

        // Throttled candidate cache: deriving candidates walks every committed
        // tree through RouteAnalysisEngine, so recompute at most once per second
        // while the window is open rather than every IMGUI frame.
        private List<RouteCandidate> cachedCandidates = new List<RouteCandidate>();
        private float lastCandidateComputeRealtime = -1f;
        private const float CandidateRecomputeIntervalSeconds = 1.0f;

        // Throttled per-route legibility cache (Phase 2 H1/H2/H3). Recomputing the
        // next-dock-crossing countdown (H1, a LoopUnit build) and the realized /
        // cumulative delivery summary (H2/H3, an ELS scan) is NOT free, and many
        // routes can be open at once, so we recompute the WHOLE dictionary on a
        // realtime timer (mirrors the candidate cache) once per ~1s rather than per
        // route per IMGUI frame. The draw path only READS the cache by route.Id.
        // In-memory instance state only: it is rebuilt from RouteStore + the ledger
        // and persists nothing across save/load.
        private readonly Dictionary<string, RouteLegibility> legibilityCache =
            new Dictionary<string, RouteLegibility>();
        private float lastLegibilityComputeRealtime = -1f;
        private const float LegibilityRecomputeIntervalSeconds = 1.0f;

        /// <summary>
        /// One route's recomputed-on-timer legibility values: the H1 next-delivery
        /// countdown (seconds + which branch), the H2 last-cycle realized line + its
        /// shortfall tint flag + the cumulative total line, and the H3 delivery
        /// badge. Built once per cache refresh, read by route.Id while drawing.
        /// </summary>
        private struct RouteLegibility
        {
            // H1: next-delivery / rechecks-in countdown.
            public LogisticsCountdownPresentation.CountdownBranch CountdownBranch;
            public double CountdownSeconds;

            // H2: realized delivery for the latest cycle + cumulative total.
            public bool HasDeliveries;
            public string LastCycleText;     // "delivered 40.0 of 150.0 LiquidFuel (110.0 did not fit)"
            public bool LastCycleShortfall;  // drives the yellow tint
            public string CumulativeText;    // "1240.0 LiquidFuel, 30.0 Oxidizer" or "(none)"

            // H3: delivering vs flying-not-delivering badge.
            public LogisticsDeliveryPresentation.DeliveryBadge Badge;

            // H4: destination cell. Resolved here (not on the draw path) because the
            // unresolved-surface-endpoint fallback does an O(vessels) FlightGlobals
            // scan that must not run every IMGUI frame.
            public string DestinationText;     // resolved vessel name, or coords fallback
            public string DestinationTooltip;  // coords when the name resolved, else empty
        }

        // Deferred mutations: collected during the draw loop and applied after the
        // scroll view so we never mutate RouteStore.CommittedRoutes mid-iteration.
        private Route pendingPause;
        private Route pendingActivate;
        private Route pendingSendOnce;
        // Two-step delete: the X button captures the route to confirm here during
        // the draw loop; ApplyPendingActions spawns the confirm dialog (once) and
        // clears the field. The dialog's Delete button calls RouteStore.RemoveRoute
        // directly in its callback (the Wipe-All precedent, ParsekUI), which is safe
        // because the callback fires outside the draw-loop route iteration. Deletion
        // never happens without the player confirming.
        private Route pendingConfirmDeleteRoute;
        private RouteCandidate pendingCreate;
        // Deferred cadence edit: the stepper records (route, new N) during the draw
        // loop; ApplyPendingActions recomputes DispatchInterval = N x span after the
        // scroll view (same deferred-mutation discipline as the action buttons).
        private Route pendingCadenceRoute;
        private int pendingCadenceMultiplier;

        // Status text styles (lazy; mirrors RecordingsTableUI.EnsureStatusStyles).
        private GUIStyle statusStyleGreen;   // Active / InTransit
        private GUIStyle statusStyleYellow;  // WaitingForResources / WaitingForFunds / DestinationFull
        private GUIStyle statusStyleRed;     // EndpointLost / MissingSourceRecording / SourceChanged
        private GUIStyle statusStyleGrey;    // Paused
        private GUIStyle statusStyleCyan;    // Candidate "eligible"
        private GUIStyle detailStyle;

        // Column widths. Header and rows use the same constants and live in the
        // same per-section box, so columns line up like the Recordings window.
        private const float ColW_Num = 30f;        // "#" row-index column (per section)
        private const float ColW_Origin = 95f;      // compacted (QW-origin short: "KSC (funds)" / "depot pid=N")
        private const float ColW_Destination = 180f;
        private const float ColW_Interval = 70f;
        private const float ColW_Transit = 70f;
        private const float ColW_Cycles = 80f;     // "3 / 1 skipped" fits without clipping (QW5)
        private const float ColW_NextDelivery = 90f; // H1 "Next delivery" countdown ("T-12m 5s")
        private const float ColW_Status = 240f;     // plain-English reason text; H3 badge now carries the at-a-glance verdict so the reason can wrap
        private const float ColW_Badge = 120f;      // H3 "Flying, not delivering" / "Delivering" badge
        private const float ColW_Actions = 190f;   // fixed action cell so Name-expand is identical every row

        private const float SpacingSmall = 3f;
        private const float SpacingLarge = 8f;
        // Fixed columns now total ~1165px (Status/Origin compacted to claw back
        // ~105px, plus the two new H1 Next-delivery + H3 badge columns), so the
        // window floor is raised in step with them to keep the expanding Name column
        // a usable share instead of crushing it.
        private const float MinWindowWidth = 1300f;
        private const float MinWindowHeight = 220f;

        public bool IsOpen
        {
            get { return showWindow; }
            set { showWindow = value; }
        }

        internal LogisticsWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
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
                float x = mainWindowRect.x + mainWindowRect.width + 10;
                windowRect = new Rect(x, mainWindowRect.y, 1380, 340);
                ParsekLog.Verbose("UI",
                    $"Logistics window initial position: x={windowRect.x.ToString("F0", CultureInfo.InvariantCulture)} y={windowRect.y.ToString("F0", CultureInfo.InvariantCulture)}");
            }

            ParsekUI.HandleResizeDrag(ref windowRect, ref isResizing,
                MinWindowWidth, MinWindowHeight, "Logistics window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                windowRect = ClickThruBlocker.GUILayoutWindow(
                    "ParsekLogistics".GetHashCode(),
                    windowRect,
                    DrawWindow,
                    "Parsek - Logistics",
                    opaqueWindowStyle,
                    GUILayout.Width(windowRect.width),
                    GUILayout.Height(windowRect.height));
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
            parentUI.LogWindowPosition("Logistics", ref lastWindowRect, windowRect);

            if (windowRect.Contains(Event.current.mousePosition))
            {
                if (!windowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, InputLockId);
                    windowHasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        public void ReleaseInputLock()
        {
            if (!windowHasInputLock) return;
            InputLockManager.RemoveControlLock(InputLockId);
            windowHasInputLock = false;
        }

        private void DrawWindow(int windowID)
        {
            EnsureStyles();
            GUILayout.Space(5);

            double currentUT = TryGetCurrentUT();
            IReadOnlyList<Route> routes = RouteStore.CommittedRoutes;
            List<RouteCandidate> candidates = GetCandidates();

            // Throttled per-route legibility recompute (H1/H2/H3). Runs at most once
            // per ~1s over the committed routes, NOT per row per IMGUI frame; the
            // row/detail draw paths only read the cache afterward.
            RefreshLegibilityCacheIfDue(routes, currentUT);

            // Split stored routes by enablement. Active section holds everything
            // that is not Paused (running, blocked-active, and hard-broken).
            var activeRoutes = new List<Route>();
            var pausedRoutes = new List<Route>();
            int routeCount = routes?.Count ?? 0;
            for (int i = 0; i < routeCount; i++)
            {
                Route r = routes[i];
                if (r == null) continue;
                if (r.Status == RouteStatus.Paused) pausedRoutes.Add(r);
                else activeRoutes.Add(r);
            }

            // Reset deferred actions for this frame.
            pendingPause = null;
            pendingActivate = null;
            pendingSendOnce = null;
            pendingConfirmDeleteRoute = null;
            pendingCreate = null;
            pendingCadenceRoute = null;
            pendingCadenceMultiplier = 0;

            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.ExpandHeight(true));

            // Each section is its own gray bubble with its own header row, so the
            // header and data columns share the box and line up exactly.
            DrawRouteSectionBubble($"Active Routes ({activeRoutes.Count})", activeRoutes, RouteSection.Active, currentUT);
            DrawRouteSectionBubble($"Paused Routes ({pausedRoutes.Count})", pausedRoutes, RouteSection.Paused, currentUT);
            DrawCandidateSectionBubble($"Candidates ({candidates.Count})", candidates);

            GUILayout.EndScrollView();

            // Tooltip echo box (matches SpawnControlUI / SettingsWindowUI house
            // style). Read GUI.tooltip AFTER all controls have drawn this frame so
            // it reflects the currently hovered control, then echo it in a box so
            // hovering any cell / button shows its help text in-window. When no
            // control is hovered, emit a zero-height placeholder label so the
            // window keeps a layout entry here and does not pop its height as the
            // box appears and disappears (same approach as SpawnControlUI).
            (bool showEcho, string echoText) = ResolveTooltipEcho(GUI.tooltip);
            if (showEcho)
            {
                GUILayout.Space(SpacingSmall);
                GUILayout.Label(echoText, GUI.skin.box);
            }
            else
            {
                GUILayout.Label("", GUILayout.Height(0));
            }

            // Full-width Close button at the bottom (matches Kerbals / Settings windows).
            GUILayout.Space(SpacingSmall);
            if (GUILayout.Button("Close"))
            {
                showWindow = false;
                ParsekLog.Verbose("UI", "Logistics window closed");
            }

            ParsekUI.DrawResizeHandle(windowRect, ref isResizing, "Logistics window");
            GUI.DragWindow();

            // Apply deferred mutations now that the draw loop is done.
            ApplyPendingActions(currentUT);
        }

        private void DrawRouteSectionBubble(string title, List<Route> rows, RouteSection section, double currentUT)
        {
            DrawSectionHeader(title);
            GUILayout.BeginVertical(GUI.skin.box);
            DrawColumnHeader();
            if (rows.Count == 0)
                GUILayout.Label("  (none)", detailStyle);
            for (int i = 0; i < rows.Count; i++)
                DrawRouteRow(rows[i], section, i + 1, currentUT);
            GUILayout.EndVertical();
            GUILayout.Space(SpacingSmall);
        }

        private void DrawCandidateSectionBubble(string title, List<RouteCandidate> rows)
        {
            DrawSectionHeader(title);
            GUILayout.BeginVertical(GUI.skin.box);
            DrawColumnHeader();
            if (rows.Count == 0)
                GUILayout.Label("  No eligible Supply Runs. Fly a one-way transport that docks, transfers cargo to the destination, and undocks, then commit and seal the recording.", detailStyle);
            for (int i = 0; i < rows.Count; i++)
                DrawCandidateRow(rows[i], i + 1);
            GUILayout.EndVertical();
            GUILayout.Space(SpacingSmall);
        }

        private void DrawColumnHeader()
        {
            GUIStyle h = parentUI.GetColumnHeaderStyle();
            GUILayout.BeginHorizontal();
            GUILayout.Label("#", h, GUILayout.Width(ColW_Num));
            GUILayout.Label("Name", h, GUILayout.ExpandWidth(true));
            GUILayout.Label("Origin", h, GUILayout.Width(ColW_Origin));
            GUILayout.Label("Destination", h, GUILayout.Width(ColW_Destination));
            GUILayout.Label("Interval", h, GUILayout.Width(ColW_Interval));
            GUILayout.Label("Transit", h, GUILayout.Width(ColW_Transit));
            GUILayout.Label("Cyc", h, GUILayout.Width(ColW_Cycles));
            GUILayout.Label("Next", h, GUILayout.Width(ColW_NextDelivery));
            GUILayout.Label("Status", h, GUILayout.Width(ColW_Status));
            GUILayout.Label("Delivery", h, GUILayout.Width(ColW_Badge));
            GUILayout.Label("Actions", h, GUILayout.Width(ColW_Actions));
            GUILayout.EndHorizontal();
        }

        private enum RouteSection { Active, Paused }

        private void DrawRouteRow(Route route, RouteSection section, int rowNum, double currentUT)
        {
            if (route == null) return;
            string rowKey = route.Id ?? "<no-id>";
            bool expanded = expandedRows.Contains(rowKey);

            GUILayout.BeginHorizontal();

            GUILayout.Label(rowNum.ToString(CultureInfo.InvariantCulture), GUILayout.Width(ColW_Num));

            // Name with caret (Recordings-window style).
            string arrow = expanded ? "\u25bc" : "\u25b6";
            if (GUILayout.Button($"{arrow} {route.Name ?? "<unnamed>"}", GUI.skin.label, GUILayout.ExpandWidth(true)))
                ToggleExpanded(rowKey, route.Name);

            // Read the throttled per-route legibility values once for this row (H1
            // countdown, H3 badge, H4 destination); the draw path never recomputes.
            RouteLegibility leg = GetLegibility(route);

            GUILayout.Label(FormatOrigin(route), GUILayout.Width(ColW_Origin));
            // H4: name the destination vessel (resolved from the endpoint PID) instead
            // of bare coords; coords move to the hover tooltip on fallback. Resolved on
            // the ~1 Hz cache refresh, so this cell just reads the cached strings.
            GUILayout.Label(
                new GUIContent(leg.DestinationText ?? "-", leg.DestinationTooltip ?? string.Empty),
                GUILayout.Width(ColW_Destination));
            // Interval cell shows BOTH the cadence multiplier and the resulting human
            // cadence (e.g. "1x (~14m)"); the full stepper lives in the detail panel.
            GUILayout.Label(
                new GUIContent(FormatCadence(route),
                    "Dispatch cadence = N x run duration. 1x is the floor (fastest the run allows); raise N in the detail panel to launch less often."),
                GUILayout.Width(ColW_Interval));
            GUILayout.Label(FormatDuration(route.TransitDuration), GUILayout.Width(ColW_Transit));
            // Completed deliveries, plus "/ N skipped" when cycles were blocked
            // (ghost flew but delivered nothing). Tooltip spells out the semantics.
            GUILayout.Label(
                new GUIContent(FormatCycleCount(route.CompletedCycles, route.SkippedCycles),
                    "Completed deliveries / blocked cycles (the ghost flew but delivered nothing)."),
                GUILayout.Width(ColW_Cycles));

            // H1 "Next" cell: time to the next dock crossing (a delivery), or the
            // wait-state retry countdown when blocked. Read from the throttled cache
            // (leg, fetched above); both branches show the bare countdown here, the
            // detail line names which.
            GUILayout.Label(
                new GUIContent(NextDeliveryCellText(leg),
                    "Time until this route's next scheduled delivery (next dock crossing). A blocked route shows when it next rechecks eligibility."),
                GUILayout.Width(ColW_NextDelivery));

            // Show the plain-English reason IN the cell; keep the raw enum name in
            // the hover tooltip (a one-word state token for players who want it).
            GUILayout.Label(new GUIContent(StatusReason(route.Status), route.Status.ToString()),
                StatusStyleFor(route.Status), GUILayout.Width(ColW_Status));

            // H3 "Delivery" badge: the at-a-glance verdict (green Delivering /
            // yellow Flying-not-delivering / grey Paused / cyan New).
            GUILayout.Label(
                new GUIContent(LogisticsDeliveryPresentation.DeliveryBadgeLabel(leg.Badge),
                    "Whether the route's last cycle actually delivered cargo, or the ghost flew but transferred nothing."),
                BadgeStyleFor(leg.Badge), GUILayout.Width(ColW_Badge));

            // Fixed-width action cell so every row's Name-expand is identical and
            // the data columns stay aligned across sections and the header.
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Actions));
            if (ShouldShowSendingButton(route))
            {
                // Armed one-shot / in-flight cycle (Send Once, or Pause while
                // InTransit): the route will dispatch one cycle at the next
                // dispatch window, then return to Paused. Show a disabled
                // "Sending..." button so the click reads as registered and the
                // route reads as armed-and-waiting rather than idle.
                bool prevEnabled = GUI.enabled;
                GUI.enabled = false;
                GUILayout.Button(new GUIContent("Sending...",
                        "Armed: this route will dispatch one cycle at the next dispatch window (funds, resources, endpoint, and alignment permitting), then return to Paused."),
                    GUILayout.Width(74));
                GUI.enabled = prevEnabled;
            }
            else if (section == RouteSection.Active)
            {
                if (GUILayout.Button("Pause", GUILayout.Width(58)))
                    pendingPause = route;
            }
            else // Paused
            {
                if (GUILayout.Button(new GUIContent("Send Once",
                        "Fire one cycle at the next moment conditions allow (funds, resources, endpoint, alignment), then stay Paused."),
                        GUILayout.Width(74)))
                    pendingSendOnce = route;
                if (GUILayout.Button(new GUIContent("Activate",
                        "Turn on periodic auto-dispatch on this route's interval."),
                        GUILayout.Width(64)))
                    pendingActivate = route;
            }
            if (GUILayout.Button(new GUIContent("X", "Delete this route"), GUILayout.Width(22)))
                pendingConfirmDeleteRoute = route;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.EndHorizontal();

            if (expanded)
                DrawRouteDetail(route, currentUT);
        }

        /// <summary>
        /// True when a route's action cell should show the disabled "Sending..."
        /// affordance instead of a live action button. A route carrying
        /// <see cref="Route.PauseAfterCurrentCycle"/> has a one-shot / in-flight
        /// cycle committed (armed by Send Once, which un-pauses the route to
        /// Active, or by Pause-while-InTransit): it will dispatch one cycle at the
        /// next dispatch window, then return to Paused. While it is armed and has
        /// not yet landed back in Paused (and is not in a hard-broken
        /// endpoint/source state that cannot send), the player should see that the
        /// click registered and the route is waiting for its dispatch window
        /// rather than idle. Pure for unit testing.
        /// </summary>
        internal static bool ShouldShowSendingButton(Route route)
        {
            if (route == null || !route.PauseAfterCurrentCycle)
                return false;
            switch (route.Status)
            {
                case RouteStatus.Active:
                case RouteStatus.InTransit:
                case RouteStatus.WaitingForResources:
                case RouteStatus.WaitingForFunds:
                case RouteStatus.DestinationFull:
                    return true;
                default:
                    // Paused (the cycle landed / the route is idle) or a
                    // hard-broken endpoint/source state: not actively sending,
                    // so show the normal action buttons.
                    return false;
            }
        }

        private void DrawCandidateRow(RouteCandidate candidate, int rowNum)
        {
            if (candidate?.Analysis == null) return;
            string treeId = candidate.Tree?.Id ?? "<no-tree>";
            string rowKey = "cand:" + treeId;
            bool expanded = expandedRows.Contains(rowKey);

            string name = RouteCreationFormatters.GenerateDefaultRouteName(candidate.Analysis);

            GUILayout.BeginHorizontal();

            GUILayout.Label(rowNum.ToString(CultureInfo.InvariantCulture), GUILayout.Width(ColW_Num));

            string arrow = expanded ? "\u25bc" : "\u25b6";
            if (GUILayout.Button($"{arrow} {name}", GUI.skin.label, GUILayout.ExpandWidth(true)))
                ToggleExpanded(rowKey, name);

            GUILayout.Label(FormatCandidateOrigin(candidate.Analysis), GUILayout.Width(ColW_Origin));
            GUILayout.Label(FormatEndpointShort(candidate.Analysis.ConnectionWindow?.EndpointAtDock), GUILayout.Width(ColW_Destination));
            GUILayout.Label("-", GUILayout.Width(ColW_Interval));
            GUILayout.Label(FormatDuration(CandidateTransit(candidate)), GUILayout.Width(ColW_Transit));
            GUILayout.Label("-", GUILayout.Width(ColW_Cycles));
            // Next-delivery placeholder: candidates have not been promoted to a route
            // yet, so there is no dispatch schedule (keeps the cell count == header).
            GUILayout.Label("-", GUILayout.Width(ColW_NextDelivery));
            GUILayout.Label(new GUIContent("eligible", "A sealed, valid Supply Run. Create Route promotes it to a Paused route you can Send Once / Activate."),
                statusStyleCyan, GUILayout.Width(ColW_Status));
            // Delivery-badge placeholder for the same reason.
            GUILayout.Label("-", GUILayout.Width(ColW_Badge));

            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Actions));
            if (GUILayout.Button(new GUIContent("Create Route",
                    "Promote this Supply Run to a stored route (created Paused; use Send Once to test, then Activate)."),
                    GUILayout.Width(100)))
                pendingCreate = candidate;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.EndHorizontal();

            if (expanded)
                DrawCandidateDetail(candidate);
        }

        private void DrawRouteDetail(Route route, double currentUT)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            RouteLegibility leg = GetLegibility(route);

            string deliv = FormatRouteDelivery(route);
            DetailLine($"Delivers per cycle: {deliv}");
            DetailLine($"Status: {route.Status} - {StatusReason(route.Status)}");

            // H1: live next-dock-crossing countdown (replaces the dead NextDispatchUT
            // self-timer). For a blocked wait-state route the cache yields the
            // "Rechecks in" branch (NextEligibilityCheckUT retry) instead. The branch
            // wording + the real FormatCountdown formatting are paired here; the cache
            // only stored the branch + seconds.
            string countdownLine = LogisticsCountdownPresentation.FormatDetailCountdownLine(
                leg.CountdownBranch, SelectiveSpawnUI.FormatCountdown(leg.CountdownSeconds));
            if (countdownLine != null)
                DetailLine(countdownLine);

            // H2: realized delivery for the latest cycle (yellow when something did not
            // fit) plus the cumulative total across all cycles. Both come from the
            // throttled ELS scan in the legibility cache.
            if (leg.HasDeliveries)
            {
                DetailLine($"Last cycle: {leg.LastCycleText}",
                    leg.LastCycleShortfall ? statusStyleYellow : detailStyle);
                DetailLine($"Total delivered: {leg.CumulativeText}");
            }

            DetailLine($"Interval: {FormatDuration(route.DispatchInterval)}   Transit: {FormatDuration(route.TransitDuration)}   Cycles: {route.CompletedCycles}");
            DrawCadenceStepper(route);

            // H5: resolved recording / tree (mission) names instead of 8-char GUID
            // fragments; the short id moves to the hover tooltip.
            DrawSourceRecordingsLine(route);
            GUILayout.EndVertical();
        }

        /// <summary>
        /// Draws the "Source recordings:" detail line (H5) with resolved recording +
        /// owning-tree (mission) names. Each id is resolved through the literal-free
        /// <see cref="RecordingStore.TryResolveRecordingDisplayInfo"/> accessor (the raw
        /// committed-list read stays in that already-allowlisted file, so no raw
        /// committed-list literal lands here and the ERS/ELS grep gate stays
        /// green). The 8-char short id is kept as the cell's hover tooltip only. Drawn
        /// only when a row is expanded, so the per-id store lookups are not per-frame
        /// for collapsed rows.
        /// </summary>
        private void DrawSourceRecordingsLine(Route route)
        {
            (string text, string tooltip) = BuildSourceRecordingsContent(route);
            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label(new GUIContent(text, tooltip), detailStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Builds the H5 "Source recordings:" line text plus its short-id hover
        /// tooltip. Resolves each route source id to a display name + tree position +
        /// tree/mission name via the literal-free RecordingStore accessor and the pure
        /// <see cref="LogisticsDeliveryPresentation.FormatSourceRecordingDisplay"/>
        /// formatter; unresolved ids (not in the committed store) fall back to the short
        /// id verbatim. The tooltip carries the comma-joined short ids so the raw
        /// identifiers stay reachable on hover. Logs one Verbose batch summary
        /// (resolved-vs-total) for the route, not per id.
        /// </summary>
        private static (string text, string tooltip) BuildSourceRecordingsContent(Route route)
        {
            if (route?.RecordingIds == null || route.RecordingIds.Count == 0)
                return ("Source recordings: -", string.Empty);

            var textSb = new StringBuilder("Source recordings: ");
            var tipSb = new StringBuilder();
            int resolved = 0;
            for (int i = 0; i < route.RecordingIds.Count; i++)
            {
                string id = route.RecordingIds[i];
                string shortId = ShortId(id);
                bool ok = RecordingStore.TryResolveRecordingDisplayInfo(
                    id, out string recName, out string treeName, out int treeOrder);
                if (ok) resolved++;
                // TreeOrder is the 0-based persisted order within the tree; humans read
                // "rec 3", so display the 1-based position (0-based -1 unassigned -> 0,
                // which the formatter drops as an unknown position).
                int humanPos = treeOrder >= 0 ? treeOrder + 1 : 0;
                string display = LogisticsDeliveryPresentation.FormatSourceRecordingDisplay(
                    shortId, ok ? recName : null, treeName, humanPos);

                if (i > 0) { textSb.Append(", "); tipSb.Append(", "); }
                textSb.Append(display);
                tipSb.Append(shortId);
            }

            ParsekLog.Verbose("UI",
                $"Logistics: source recordings line route={ShortId(route.Id)} " +
                $"resolved={resolved.ToString(CultureInfo.InvariantCulture)}/{route.RecordingIds.Count.ToString(CultureInfo.InvariantCulture)}");
            return (textSb.ToString(), tipSb.ToString());
        }

        // Cadence stepper (Phase 6): "- N x (~human) +" where N is the dispatch
        // cadence multiplier (>= 1) and the human duration is N x the run span.
        // 1x is the floor (the route cannot dispatch faster). Records the edit into
        // the deferred-mutation fields; ApplyPendingActions recomputes the interval.
        private void DrawCadenceStepper(Route route)
        {
            int n = Route.ClampCadenceMultiplier(route.CadenceMultiplier);

            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label(
                new GUIContent("Cadence:",
                    "How often the route dispatches, as a multiple of the run duration. 1x is the floor (the fastest the run allows); raise it to launch less often."),
                detailStyle, GUILayout.Width(70f));

            // "-" decrements (no-op + greyed at the 1x floor).
            bool atFloor = n <= 1;
            GUI.enabled = !atFloor;
            if (GUILayout.Button(new GUIContent("-",
                    atFloor ? "Already at the minimum (1x = the fastest the run allows)" : "Dispatch less often"),
                    GUILayout.Width(24f)))
            {
                pendingCadenceRoute = route;
                pendingCadenceMultiplier = RouteCadence.StepMultiplier(n, -1);
            }
            GUI.enabled = true;

            // Current N x + the resulting human cadence.
            GUILayout.Label(FormatCadence(route), detailStyle, GUILayout.Width(110f));

            if (GUILayout.Button(new GUIContent("+", "Dispatch less often"), GUILayout.Width(24f)))
            {
                pendingCadenceRoute = route;
                pendingCadenceMultiplier = RouteCadence.StepMultiplier(n, +1);
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawCandidateDetail(RouteCandidate candidate)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            DetailLine($"Would deliver per cycle: {FormatManifest(candidate.Analysis.ResourceDeliveryManifest, candidate.Analysis.InventoryDeliveryManifest)}");
            DetailLine($"Transit: {FormatDuration(CandidateTransit(candidate))}");

            // H5: the candidate already holds its source Recording + owning Tree in
            // hand, so resolve display names directly (no store lookup needed). Short
            // id moves to the hover tooltip.
            (string text, string tooltip) = BuildCandidateSourceContent(candidate);
            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label(new GUIContent(text, tooltip), detailStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        /// <summary>
        /// Builds the candidate-detail "Source recording:" line text + short-id tooltip
        /// (H5). The candidate's <see cref="RouteCandidate.Analysis"/> SourceRecording
        /// and <see cref="RouteCandidate.Tree"/> are already in hand, so the recording
        /// name (VesselName, "Untitled" when empty), the 1-based tree position
        /// (TreeOrder + 1), and the tree/mission name (TreeName) come straight off them
        /// with no committed-store lookup. Routes through the same pure
        /// <see cref="LogisticsDeliveryPresentation.FormatSourceRecordingDisplay"/>
        /// formatter as the route line.
        /// </summary>
        private static (string text, string tooltip) BuildCandidateSourceContent(RouteCandidate candidate)
        {
            Recording src = candidate?.Analysis?.SourceRecording;
            string srcId = src?.RecordingId;
            string shortId = ShortId(srcId);

            if (src == null)
                return ($"Source recording: {shortId}", shortId);

            string recName = string.IsNullOrEmpty(src.VesselName) ? "Untitled" : src.VesselName;
            string treeName = candidate.Tree?.TreeName;
            int humanPos = src.TreeOrder >= 0 ? src.TreeOrder + 1 : 0;
            string display = LogisticsDeliveryPresentation.FormatSourceRecordingDisplay(
                shortId, recName, treeName, humanPos);
            return ($"Source recording: {display}", shortId);
        }

        private void DetailLine(string text)
        {
            DetailLine(text, detailStyle);
        }

        // Detail line with an explicit style (e.g. statusStyleYellow for the H2
        // shortfall line). Same indented full-width layout as the default overload.
        private void DetailLine(string text, GUIStyle style)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label(text, style ?? detailStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        private void DrawSectionHeader(string text)
        {
            // Use the shared house section-header bar (bold label in a box,
            // left-aligned, full-width) so Logistics headers match Settings /
            // Recordings / Timeline / Missions instead of the old tinted-label
            // look. GetSectionHeaderStyle lazily builds the style each call, so it
            // is safe to call straight from the draw path (matches SettingsWindowUI).
            GUILayout.Space(SpacingSmall);
            GUILayout.Label(text, parentUI.GetSectionHeaderStyle());
        }

        private void ToggleExpanded(string key, string nameForLog)
        {
            if (expandedRows.Contains(key))
            {
                expandedRows.Remove(key);
                ParsekLog.Verbose("UI", $"Logistics row collapsed: '{nameForLog}'");
            }
            else
            {
                expandedRows.Add(key);
                ParsekLog.Verbose("UI", $"Logistics row expanded: '{nameForLog}'");
            }
        }

        private void ApplyPendingActions(double currentUT)
        {
            // Any of these mutate route state (status, cadence, membership), which
            // changes the legibility cache inputs (H1 countdown, H3 badge). Force a
            // recompute next frame so a player action refreshes the cells immediately
            // instead of waiting out the ~1s timer (mirrors the candidate-cache
            // invalidation idiom below for Create Route).
            bool routeStateMutated =
                pendingPause != null || pendingActivate != null || pendingSendOnce != null
                || pendingConfirmDeleteRoute != null || pendingCreate != null
                || pendingCadenceRoute != null;

            if (pendingPause != null)
            {
                bool ok = RouteOrchestrator.TryPause(pendingPause);
                ParsekLog.Info("UI", $"Logistics: Pause route={ShortId(pendingPause.Id)} result={(ok ? "paused" : "rejected")}");
            }
            if (pendingActivate != null)
            {
                bool ok = RouteOrchestrator.TryActivate(pendingActivate, currentUT);
                ParsekLog.Info("UI", $"Logistics: Activate route={ShortId(pendingActivate.Id)} result={(ok ? "active" : "rejected")}");
            }
            if (pendingSendOnce != null)
            {
                bool ok = RouteOrchestrator.TrySendOneCycleNow(pendingSendOnce, currentUT);
                ParsekLog.Info("UI", $"Logistics: Send Once route={ShortId(pendingSendOnce.Id)} result={(ok ? "armed" : "rejected")}");
            }
            if (pendingConfirmDeleteRoute != null)
            {
                // Spawns the modal confirm once on the click frame; the actual
                // RouteStore.RemoveRoute runs in the dialog's Delete callback (see
                // SpawnDeleteRouteConfirmation), never here, so deletion is gated on
                // an explicit confirm.
                SpawnDeleteRouteConfirmation(pendingConfirmDeleteRoute);
            }
            if (pendingCreate != null)
            {
                CreateRouteFromCandidate(pendingCreate);
                // Force a candidate recompute next frame so the promoted run leaves the list.
                lastCandidateComputeRealtime = -1f;
            }
            if (pendingCadenceRoute != null)
            {
                bool changed = RouteCadence.ApplyMultiplier(pendingCadenceRoute, pendingCadenceMultiplier);
                ParsekLog.Info("UI",
                    $"Logistics: Cadence route={ShortId(pendingCadenceRoute.Id)} N={pendingCadenceMultiplier} " +
                    $"result={(changed ? "applied" : "unchanged")}");
            }

            if (routeStateMutated)
                lastLegibilityComputeRealtime = -1f;

            pendingPause = null;
            pendingActivate = null;
            pendingSendOnce = null;
            pendingConfirmDeleteRoute = null;
            pendingCreate = null;
            pendingCadenceRoute = null;
            pendingCadenceMultiplier = 0;
        }

        /// <summary>
        /// Spawns the modal "Delete route '...'? This cannot be undone." confirm
        /// (mirrors the Wipe-All confirmation idiom in
        /// <see cref="ParsekUI.ShowWipeRecordingsConfirmation"/>). The Delete button
        /// calls <see cref="RouteStore.RemoveRoute"/> directly in its callback, the
        /// same way Wipe-All performs its destructive action in-callback; this is
        /// safe because the callback fires outside the window's route iteration, and
        /// it avoids the frame-top deferred-field reset that would otherwise clobber
        /// an asynchronously set delete request. Cancel only logs. Deletion never
        /// happens without a confirm.
        /// </summary>
        private void SpawnDeleteRouteConfirmation(Route route)
        {
            if (route == null)
                return;

            // Capture the id locally so the Delete closure does not depend on a
            // mutable instance field that ApplyPendingActions nulls this frame.
            string routeId = route.Id;
            string body = BuildDeleteConfirmBody(route);

            ParsekLog.Info("UI",
                $"Logistics: Delete route={ShortId(routeId)} confirm dialog spawned");

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekLogisticsDeleteRouteConfirm",
                    body,
                    "Confirm: Delete Route",
                    HighLogic.UISkin,
                    new DialogGUIButton("Delete", () =>
                    {
                        bool ok = RouteStore.RemoveRoute(routeId);
                        ParsekLog.Info("UI",
                            $"Logistics: Delete route={ShortId(routeId)} confirmed result={(ok ? "removed" : "not-found")}");
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Verbose("UI",
                            $"Logistics: Delete route={ShortId(routeId)} cancelled");
                    })),
                false, HighLogic.UISkin);
        }

        /// <summary>
        /// Builds the body text for the route-delete confirm dialog. Uses the
        /// route's display name when present, falling back to its short id so the
        /// player always sees an identifier (never the literal "null"). Pure and
        /// Unity-free for unit testing.
        /// </summary>
        internal static string BuildDeleteConfirmBody(Route route)
        {
            string name = route != null && !string.IsNullOrEmpty(route.Name)
                ? route.Name
                : ShortId(route?.Id);
            return $"Delete route '{name}'?\n\nThis cannot be undone.";
        }

        /// <summary>
        /// The default dispatch interval a window-created route gets, resolved
        /// through the SAME span helper the Create Route dialog feeds into its
        /// <see cref="RouteBuilder.RouteCreationInputs.DispatchIntervalSeconds"/>
        /// (<see cref="RouteCreationDialog.ComputeRootToUndockSpan"/>): the rendered
        /// [root..dock] span (== the route's TransitDuration, the N=1 cadence floor),
        /// floored at 1.0. Routing both the window and the dialog through one helper
        /// guarantees a window create and a dialog create produce the identical
        /// interval for the same candidate analysis. Pure for unit testing; returns
        /// the helper's floor (1.0) when the candidate / analysis / tree is null.
        /// </summary>
        internal static double ResolveWindowCreateInterval(RouteCandidate candidate)
        {
            double interval = RouteCreationDialog.ComputeRootToUndockSpan(
                candidate?.Analysis, candidate?.Tree);
            ParsekLog.Verbose("UI",
                $"Logistics: window create interval resolved tree={ShortId(candidate?.Tree?.Id)} " +
                $"interval={interval.ToString("R", CultureInfo.InvariantCulture)}s");
            return interval;
        }

        private void CreateRouteFromCandidate(RouteCandidate candidate)
        {
            if (candidate?.Analysis == null || candidate.Tree == null)
            {
                ParsekLog.Warn("UI", "Logistics: Create Route - null candidate/analysis/tree, ignored");
                return;
            }

            Game.Modes mode = HighLogic.CurrentGame != null
                ? HighLogic.CurrentGame.Mode
                : Game.Modes.SANDBOX;

            // Window-created routes use the SAME default interval the Create Route
            // dialog computes: the rendered [root..dock] span (== TransitDuration,
            // N=1 cadence floor). Both paths funnel through
            // RouteCreationDialog.ComputeRootToUndockSpan so a route created from
            // the window is identical (in interval) to one created from the dialog
            // for the same candidate analysis. Created Paused so the player verifies
            // via Send Once before turning on periodic dispatch.
            double interval = ResolveWindowCreateInterval(candidate);
            var inputs = new RouteBuilder.RouteCreationInputs
            {
                Name = null, // RouteBuilder generates a default name
                DispatchIntervalSeconds = interval
            };

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                candidate.Analysis, candidate.Tree, inputs, mode,
                idFactory: null,
                initialStatus: RouteStatus.Paused,
                // Belt-and-suspenders: the dock-based span should never be below
                // transit, but keep the window path permissive so a degenerate
                // span can never reject a player-initiated create.
                allowIntervalBelowTransit: true);

            if (outcome.Route != null)
            {
                RouteStore.AddRoute(outcome.Route);
                // Mutual exclusion (design §0.6): a tree is EITHER a supply route OR a
                // manually looped recording/mission. Activating a route turns OFF any
                // pre-existing manual loop (mission + per-recording) on its tree so the
                // single loop owner is never contended. Route looping wins.
                RouteTreeGuard.ForceClearManualLoopForRoute(outcome.Route, TryGetCurrentUT());
                ParsekLog.Info("UI",
                    $"Logistics: Create Route from candidate tree={ShortId(candidate.Tree.Id)} -> route={ShortId(outcome.Route.Id)} name='{outcome.Route.Name}' (Paused, interval={interval.ToString("R", CultureInfo.InvariantCulture)}s)");
            }
            else
            {
                ParsekLog.Info("UI",
                    $"Logistics: Create Route rejected tree={ShortId(candidate.Tree.Id)} reason={outcome.RejectReason ?? "<none>"}");
            }
        }

        // ------------------------------------------------------------------
        // Candidate cache (throttled)
        // ------------------------------------------------------------------

        private List<RouteCandidate> GetCandidates()
        {
            float now = Time.realtimeSinceStartup;
            if (lastCandidateComputeRealtime < 0f
                || now - lastCandidateComputeRealtime >= CandidateRecomputeIntervalSeconds)
            {
                cachedCandidates = RouteCandidateFinder.DeriveCandidates();
                lastCandidateComputeRealtime = now;
            }
            return cachedCandidates ?? new List<RouteCandidate>();
        }

        // ------------------------------------------------------------------
        // Per-route legibility cache (throttled): H1 next-delivery countdown,
        // H2 realized / cumulative delivery, H3 delivery badge.
        // ------------------------------------------------------------------

        /// <summary>
        /// Recomputes the whole per-route legibility cache on the realtime timer
        /// (mirrors <see cref="GetCandidates"/>). For each committed route it builds:
        /// the H1 next-dock-crossing countdown (throttled <see cref="RouteOrchestrator"/>
        /// accessor + the wait-state retry fallback branch), and the H2/H3 realized /
        /// cumulative delivery summary + badge from a SINGLE ELS scan of the route's
        /// <c>RouteCargoDelivered</c> rows (H3 reuses H2's scan). Emits one batch-summary
        /// Verbose line after the pass (route count + how many had deliveries), never
        /// per route. Called once per frame at the top of <see cref="DrawWindow"/>; the
        /// timer / dirty-flag gate keeps the LoopUnit build + ledger scan to ~1 Hz.
        /// </summary>
        private void RefreshLegibilityCacheIfDue(IReadOnlyList<Route> routes, double currentUT)
        {
            float now = Time.realtimeSinceStartup;
            if (lastLegibilityComputeRealtime >= 0f
                && now - lastLegibilityComputeRealtime < LegibilityRecomputeIntervalSeconds)
                return;
            lastLegibilityComputeRealtime = now;

            legibilityCache.Clear();
            int routeCount = routes?.Count ?? 0;
            int withCountdown = 0;
            int withDeliveries = 0;
            for (int i = 0; i < routeCount; i++)
            {
                Route route = routes[i];
                if (route == null || string.IsNullOrEmpty(route.Id)) continue;

                RouteLegibility leg = ComputeRouteLegibility(route, currentUT);
                legibilityCache[route.Id] = leg;
                if (leg.CountdownBranch != LogisticsCountdownPresentation.CountdownBranch.None)
                    withCountdown++;
                if (leg.HasDeliveries)
                    withDeliveries++;
            }

            ParsekLog.Verbose("UI",
                $"Logistics legibility cache refreshed routes={routeCount.ToString(CultureInfo.InvariantCulture)} " +
                $"withCountdown={withCountdown.ToString(CultureInfo.InvariantCulture)} " +
                $"withDeliveries={withDeliveries.ToString(CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Builds one route's legibility values. H1: asks the throttled read-only
        /// orchestrator accessor for the next dock crossing, then picks the
        /// next-delivery vs wait-state "rechecks in" branch via
        /// <see cref="LogisticsCountdownPresentation.ResolveDetailCountdown"/>. H2/H3:
        /// scans the ledger once for this route's realized deliveries and reduces them
        /// to the latest-cycle line, its shortfall flag, the cumulative total, and the
        /// delivery badge.
        /// </summary>
        private RouteLegibility ComputeRouteLegibility(Route route, double currentUT)
        {
            var leg = new RouteLegibility();

            // H1: next dock crossing (read-only; the LoopUnit build stays behind the
            // allowlisted RouteOrchestrator accessor).
            bool hasCrossing = RouteOrchestrator.TryComputeSecondsToNextDockCrossing(
                route, currentUT, out double secondsToCrossing);
            LogisticsCountdownPresentation.CountdownDecision countdown =
                LogisticsCountdownPresentation.ResolveDetailCountdown(
                    route.Status, route.NextEligibilityCheckUT,
                    hasCrossing, secondsToCrossing, currentUT);
            leg.CountdownBranch = countdown.Branch;
            leg.CountdownSeconds = countdown.Seconds;

            // H2 / H3: one ELS scan -> realized + cumulative + badge.
            LogisticsDeliveryPresentation.RouteDeliverySummary summary =
                CollectRouteDeliverySummary(route.Id);
            leg.HasDeliveries = summary.HasAny;
            leg.LastCycleText = summary.HasAny
                ? LogisticsDeliveryPresentation.FormatRealizedDelivery(summary.LastRequested, summary.LastActual)
                : null;
            leg.LastCycleShortfall = summary.HasAny
                && LogisticsDeliveryPresentation.HasShortfall(summary.LastRequested, summary.LastActual);
            leg.CumulativeText = LogisticsDeliveryPresentation.FormatCumulativeTotal(summary.CumulativeTotal);

            bool ghostDriving = RouteStatusPolicy.GhostDriving(route.Status);
            LogisticsDeliveryPresentation.DeliveryOutcome lastOutcome = ClassifyLastOutcome(route.Status, summary);
            leg.Badge = LogisticsDeliveryPresentation.ClassifyDeliveryBadge(
                ghostDriving, lastOutcome, route.CompletedCycles, route.SkippedCycles);

            // H4: resolve the destination cell here (it touches FlightGlobals and, on an
            // unresolved surface endpoint, scans all vessels), so the draw path only
            // reads the cached strings.
            ResolveDestinationCell(route, out string destText, out string destTooltip);
            leg.DestinationText = destText;
            leg.DestinationTooltip = destTooltip;

            return leg;
        }

        /// <summary>
        /// Derives the H3 last-cycle delivery outcome for the badge from a route's
        /// status and its realized-delivery summary. A blocked-but-flying wait state
        /// (<see cref="LogisticsCountdownPresentation.IsWaitState"/>) forces
        /// <see cref="LogisticsDeliveryPresentation.DeliveryOutcome.None"/> regardless
        /// of any stale row, because this cycle is delivering nothing. Otherwise a full
        /// fill (no requested manifest) is Full and a recorded shortfall is Partial; no
        /// delivered row at all is None. Pure for unit testing.
        /// </summary>
        internal static LogisticsDeliveryPresentation.DeliveryOutcome ClassifyLastOutcome(
            RouteStatus status,
            LogisticsDeliveryPresentation.RouteDeliverySummary summary)
        {
            if (LogisticsCountdownPresentation.IsWaitState(status))
                return LogisticsDeliveryPresentation.DeliveryOutcome.None;
            if (summary == null || !summary.HasAny || summary.LastActual == null || summary.LastActual.Count == 0)
                return LogisticsDeliveryPresentation.DeliveryOutcome.None;
            return summary.LastRequested != null && summary.LastRequested.Count > 0
                ? LogisticsDeliveryPresentation.DeliveryOutcome.Partial
                : LogisticsDeliveryPresentation.DeliveryOutcome.Full;
        }

        /// <summary>
        /// Scans the effective ledger state (<see cref="EffectiveState.ComputeELS"/>,
        /// gate-safe and already tombstone-filtered) for this route's
        /// <see cref="GameActionType.RouteCargoDelivered"/> rows and reduces them to a
        /// <see cref="LogisticsDeliveryPresentation.RouteDeliverySummary"/> (latest
        /// cycle + cumulative total). Mirrors
        /// <c>RouteOrchestrator.IsDeliveryAlreadyInLedger</c>'s scan idiom and
        /// catch-as-empty convention. Matches rows by RouteId only (all cycles), NOT by
        /// cycle id. This is the one non-pure piece of H2/H3 (it needs a live Scenario
        /// for ComputeELS), so it stays in the window file and feeds the pure
        /// summary/format helpers; called only on the ~1 Hz cache refresh.
        /// </summary>
        private static LogisticsDeliveryPresentation.RouteDeliverySummary CollectRouteDeliverySummary(string routeId)
        {
            var rows = new List<LogisticsDeliveryPresentation.DeliveryRow>();
            if (string.IsNullOrEmpty(routeId))
                return LogisticsDeliveryPresentation.SummarizeRouteDeliveries(rows);

            IReadOnlyList<GameAction> els;
            try
            {
                els = EffectiveState.ComputeELS();
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("UI",
                    $"Logistics delivery scan: ComputeELS threw {ex.GetType().Name}: {ex.Message}; treating as no deliveries");
                return LogisticsDeliveryPresentation.SummarizeRouteDeliveries(rows);
            }

            if (els != null)
            {
                for (int i = 0; i < els.Count; i++)
                {
                    GameAction a = els[i];
                    if (a == null) continue;
                    if (a.Type != GameActionType.RouteCargoDelivered) continue;
                    if (!string.Equals(a.RouteId, routeId, System.StringComparison.Ordinal)) continue;
                    rows.Add(new LogisticsDeliveryPresentation.DeliveryRow(
                        a.RouteResourceManifest, a.RouteRequestedResourceManifest, a.UT));
                }
            }
            return LogisticsDeliveryPresentation.SummarizeRouteDeliveries(rows);
        }

        /// <summary>
        /// Reads a route's cached legibility values by id. Returns a default
        /// (no-countdown / no-deliveries / <see cref="LogisticsDeliveryPresentation.DeliveryBadge.Paused"/>)
        /// struct when the route is absent from the cache (e.g. the very first frame
        /// before the timer has fired). The draw path never recomputes; it only reads
        /// here.
        /// </summary>
        private RouteLegibility GetLegibility(Route route)
        {
            if (route != null && !string.IsNullOrEmpty(route.Id)
                && legibilityCache.TryGetValue(route.Id, out RouteLegibility leg))
                return leg;
            return default(RouteLegibility);
        }

        // The H1 "Next" cell text for a cached countdown: the bare formatted countdown
        // (next delivery or wait-state recheck), or "-" when there is no countdown.
        // Uses the established FormatCountdown idiom (T-/T+); the pure branch->text
        // mapping is in LogisticsCountdownPresentation.
        private static string NextDeliveryCellText(RouteLegibility leg)
        {
            return LogisticsCountdownPresentation.FormatNextDeliveryCell(
                leg.CountdownBranch, SelectiveSpawnUI.FormatCountdown(leg.CountdownSeconds));
        }

        /// <summary>
        /// Resolves the H4 Destination cell strings for the legibility cache: the
        /// resolved destination vessel name as the text (coords in the hover tooltip),
        /// or the coords string as the text with no extra tooltip when the vessel
        /// cannot be resolved (e.g. it is unloaded / out of range). The live
        /// <see cref="RouteEndpointResolver.TryResolveEndpoint"/> call (which touches
        /// FlightGlobals and, on an unresolved surface endpoint, scans every vessel) is
        /// why this runs on the ~1 Hz cache refresh and NOT per IMGUI frame; the pure
        /// <see cref="LogisticsDeliveryPresentation.FormatDestinationDisplay"/> picks
        /// name-vs-coords. Logs the resolve decision (rate-limited per route).
        /// </summary>
        private void ResolveDestinationCell(Route route, out string text, out string tooltip)
        {
            if (route?.Stops == null || route.Stops.Count == 0 || route.Stops[0] == null)
            {
                text = "-";
                tooltip = string.Empty;
                return;
            }

            RouteEndpoint endpoint = route.Stops[0].Endpoint;
            string coords = LogisticsDeliveryPresentation.FormatEndpointCoords(endpoint);

            string resolvedName = null;
            bool resolved = RouteEndpointResolver.TryResolveEndpoint(endpoint, out Vessel v, out string reason);
            if (resolved && v != null)
                resolvedName = v.vesselName;

            text = LogisticsDeliveryPresentation.FormatDestinationDisplay(resolvedName, endpoint);
            // When the name resolved, the coords go to the tooltip; on the coords
            // fallback the cell already shows the coords, so no extra tooltip.
            tooltip = resolved ? coords : string.Empty;

            ParsekLog.VerboseRateLimited("UI", "dest-resolve-" + route.Id,
                resolved
                    ? $"Logistics: destination route={ShortId(route.Id)} resolved=true name='{resolvedName}'"
                    : $"Logistics: destination route={ShortId(route.Id)} resolved=false reason='{reason}' (showing coords)",
                5.0);
        }

        // ------------------------------------------------------------------
        // Formatting helpers
        // ------------------------------------------------------------------

        private static double TryGetCurrentUT()
        {
            try
            {
                return Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0;
            }
            catch
            {
                return 0.0;
            }
        }

        private static double CandidateTransit(RouteCandidate candidate)
        {
            // CRE-2: the player-shown transit must match the route's ACTUAL span,
            // which the builder computes as [root.StartUT .. undockUT] (the full
            // rendered path), NOT the leaf dock-child span (src.EndUT - src.StartUT).
            // Reuse the single span helper so display and creation never diverge.
            if (candidate?.Analysis == null) return 0.0;
            return RouteCreationDialog.ComputeRootToUndockSpan(candidate.Analysis, candidate.Tree);
        }

        private static string FormatOrigin(Route route)
        {
            if (route.IsKscOrigin) return "KSC (funds)";
            if (route.Origin.VesselPersistentId != 0u || !string.IsNullOrEmpty(route.Origin.BodyName))
                return FormatEndpointShort(route.Origin);
            return "-";
        }

        private static string FormatCandidateOrigin(RouteAnalysisResult analysis)
        {
            Recording src = analysis?.SourceRecording;
            if (src != null && !string.IsNullOrEmpty(src.LaunchSiteName)
                && string.Equals(src.StartBodyName, "Kerbin", System.StringComparison.Ordinal))
                return "KSC (funds)";
            if (src != null && src.RouteOriginProof != null
                && src.RouteOriginProof.StartDockedOriginVesselPid != 0)
                return "depot pid=" + src.RouteOriginProof.StartDockedOriginVesselPid.ToString(CultureInfo.InvariantCulture);
            return "-";
        }

        private static string FormatEndpointShort(RouteEndpoint? ep)
        {
            if (!ep.HasValue) return "-";
            return FormatEndpointShort(ep.Value);
        }

        private static string FormatEndpointShort(RouteEndpoint ep)
        {
            if (string.IsNullOrEmpty(ep.BodyName)) return "-";
            string sit = ep.IsSurface ? "surface" : "orbit";
            return string.Format(CultureInfo.InvariantCulture,
                "{0} ({1}) {2:F2},{3:F2}",
                ep.BodyName, sit, ep.Latitude, ep.Longitude);
        }

        // "Nx (~human)" — the cadence multiplier plus the resulting dispatch
        // interval as a human duration (Phase 6). When the span is unknown the
        // duration falls back to a dash from FormatDuration.
        internal static string FormatCadence(Route route)
        {
            int n = Route.ClampCadenceMultiplier(route.CadenceMultiplier);
            string human = FormatDuration(route.DispatchInterval);
            return string.Format(CultureInfo.InvariantCulture, "{0}x (~{1})", n, human);
        }

        internal static string FormatDuration(double seconds)
        {
            if (seconds <= 0.0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
                return "-";
            if (seconds < 60.0)
                return string.Format(CultureInfo.InvariantCulture, "{0:F0}s", seconds);
            if (seconds < 3600.0)
                return string.Format(CultureInfo.InvariantCulture, "{0:F1}m", seconds / 60.0);
            if (seconds < 86400.0)
                return string.Format(CultureInfo.InvariantCulture, "{0:F1}h", seconds / 3600.0);
            return string.Format(CultureInfo.InvariantCulture, "{0:F1}d", seconds / 21600.0); // Kerbin days
        }

        // Cyc-column text (Phase QW5): completed deliveries, plus a "/ N skipped"
        // suffix when any cycle was blocked (ghost flew, delivered nothing). When
        // nothing was skipped, just the completed count so the common case stays
        // compact. Both numbers format with InvariantCulture. Pure for unit testing.
        internal static string FormatCycleCount(int completed, int skipped)
        {
            if (skipped > 0)
                return completed.ToString(CultureInfo.InvariantCulture)
                    + " / " + skipped.ToString(CultureInfo.InvariantCulture) + " skipped";
            return completed.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatRouteDelivery(Route route)
        {
            if (route.Stops == null || route.Stops.Count == 0) return "-";
            RouteStop stop = route.Stops[0];
            if (stop == null) return "-";
            return FormatManifest(stop.DeliveryManifest, stop.InventoryDeliveryManifest);
        }

        private static string FormatManifest(
            Dictionary<string, double> resources,
            List<InventoryPayloadItem> inventory)
        {
            var sb = new StringBuilder();
            if (resources != null)
            {
                foreach (KeyValuePair<string, double> kv in resources)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(kv.Key).Append(' ')
                      .Append(kv.Value.ToString("F1", CultureInfo.InvariantCulture));
                }
            }
            int invCount = inventory?.Count ?? 0;
            if (invCount > 0)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(invCount.ToString(CultureInfo.InvariantCulture)).Append(" inventory item(s)");
            }
            return sb.Length > 0 ? sb.ToString() : "(nothing)";
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<none>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        /// <summary>
        /// Decides whether the bottom tooltip echo box should render and with what
        /// text. Returns (false, "") when there is no hovered-control tooltip this
        /// frame (the caller then emits a zero-height placeholder so the IMGUI
        /// control count stays stable), or (true, tooltip) when a control is
        /// hovered. Pure and Unity-free for unit testing.
        /// </summary>
        internal static (bool show, string text) ResolveTooltipEcho(string guiTooltip)
        {
            if (string.IsNullOrEmpty(guiTooltip))
                return (false, string.Empty);
            return (true, guiTooltip);
        }

        // Human-readable reason for a status. For blocked-active states this
        // explains why a cycle is "visual-only" (ghost flies, nothing transfers).
        // Rendered IN the Status cell (the raw enum moves to the hover tooltip), so
        // every RouteStatus must map to a non-empty player-readable string. Pure and
        // Unity-free for unit testing.
        internal static string StatusReason(RouteStatus status)
        {
            switch (status)
            {
                case RouteStatus.Active: return "Dispatching on schedule";
                case RouteStatus.InTransit: return "Ghost in transit";
                case RouteStatus.WaitingForResources: return "Origin lacks resources - ghost flies but delivers nothing this cycle";
                case RouteStatus.WaitingForFunds: return "Insufficient funds - ghost flies but delivers nothing this cycle";
                case RouteStatus.DestinationFull: return "Destination full - ghost flies but delivers nothing this cycle";
                case RouteStatus.EndpointLost: return "Destination vessel lost - re-target or recreate the route";
                case RouteStatus.MissingSourceRecording: return "Source recording missing - restore it or recreate the route";
                case RouteStatus.SourceChanged: return "Source recording changed - recreate the route";
                case RouteStatus.Paused: return "Paused - not auto-dispatching";
                default: return status.ToString();
            }
        }

        // H3 badge color: green delivering / yellow flying-not-delivering / cyan new /
        // grey paused. Reuses the existing status-text styles so the badge column
        // matches the Status cell palette.
        private GUIStyle BadgeStyleFor(LogisticsDeliveryPresentation.DeliveryBadge badge)
        {
            switch (badge)
            {
                case LogisticsDeliveryPresentation.DeliveryBadge.Delivering:
                    return statusStyleGreen;
                case LogisticsDeliveryPresentation.DeliveryBadge.FlyingNotDelivering:
                    return statusStyleYellow;
                case LogisticsDeliveryPresentation.DeliveryBadge.New:
                    return statusStyleCyan;
                case LogisticsDeliveryPresentation.DeliveryBadge.Paused:
                default:
                    return statusStyleGrey;
            }
        }

        private GUIStyle StatusStyleFor(RouteStatus status)
        {
            switch (status)
            {
                case RouteStatus.Active:
                case RouteStatus.InTransit:
                    return statusStyleGreen;
                case RouteStatus.WaitingForResources:
                case RouteStatus.WaitingForFunds:
                case RouteStatus.DestinationFull:
                    return statusStyleYellow;
                case RouteStatus.EndpointLost:
                case RouteStatus.MissingSourceRecording:
                case RouteStatus.SourceChanged:
                    return statusStyleRed;
                case RouteStatus.Paused:
                    return statusStyleGrey;
                default:
                    return statusStyleGrey;
            }
        }

        private void EnsureStyles()
        {
            if (statusStyleGreen != null) return;

            statusStyleGreen = new GUIStyle(GUI.skin.label);
            statusStyleGreen.normal.textColor = new Color(0.55f, 1f, 0.55f);
            statusStyleYellow = new GUIStyle(GUI.skin.label);
            statusStyleYellow.normal.textColor = new Color(1f, 1f, 0.4f);
            statusStyleRed = new GUIStyle(GUI.skin.label);
            statusStyleRed.normal.textColor = new Color(1f, 0.4f, 0.4f);
            statusStyleGrey = new GUIStyle(GUI.skin.label);
            statusStyleGrey.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            statusStyleCyan = new GUIStyle(GUI.skin.label);
            statusStyleCyan.normal.textColor = new Color(0.65f, 0.85f, 1f);

            detailStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            detailStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
        }
    }
}
