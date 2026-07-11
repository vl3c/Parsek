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
        // Persist the window rect (position + size) across scene changes. ParsekUI
        // and all its sub-windows are re-instantiated per scene (the two ParsekUI
        // constructors each do `new LogisticsWindowUI(this)`), so an instance-only
        // rect would reset to the first-open default on every KSC <-> flight
        // transition. A static survives the re-instantiation for the whole KSP
        // session, so a window the player resized or moved reappears unchanged after
        // a scene change. Stays default-zero until the window is drawn once, which
        // keeps the first-open default-init intact.
        private static Rect sessionWindowRect;
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

        // Run-cost (Phase 3.4): per-candidate net funds cost, keyed by tree id and
        // recomputed on the SAME ~1 Hz candidate-cache timer as cachedCandidates.
        // The candidate cost is an O(actions) ELS scan plus a snapshot part-cost
        // walk, so it MUST NOT run on the IMGUI draw path; the "Would deliver" cell
        // only READS this cache. Empty until the first candidate refresh; a cache
        // miss draws no suffix (the cell is unchanged).
        private readonly Dictionary<string, RouteRunCostCalculator.RouteRunCost> candidateRunCostCache =
            new Dictionary<string, RouteRunCostCalculator.RouteRunCost>();

        // M3 near-miss cache: the "recently committed trees not yet eligible" list
        // (DeriveNearMisses walks every committed tree through RouteAnalysisEngine,
        // exactly like DeriveCandidates), throttled on the SAME ~1 Hz timer as the
        // candidate cache so the subsection never scans live on the IMGUI draw path.
        // The draw path only READS this cached list.
        private List<RouteNearMiss> cachedNearMisses = new List<RouteNearMiss>();
        private float lastNearMissComputeRealtime = -1f;

        // M6 candidate intent helper: cached display rows for the collapsed
        // "Dismissed (N)" subsection, rebuilt on the SAME ~1 Hz timer as the
        // candidate cache (inside GetCandidates) so tree display names are never
        // resolved live on the IMGUI draw path. Sorted by label (then id) for a
        // stable row order across HashSet iteration.
        private readonly List<(string treeId, string label)> cachedDismissedRows =
            new List<(string treeId, string label)>();

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

        // L2 route-table sort state. Shared by both the Active and the Paused section
        // (the column the player clicks sorts both tables the same way). Default is
        // Name ascending so the table reads alphabetically until the player sorts.
        private LogisticsRouteSortColumn routeSortColumn = LogisticsRouteSortColumn.Name;
        private bool routeSortAscending = true;

        // L2 cached-sorted route lists, one independent cache PER section (Active and
        // Paused are two disjoint row sets), mirroring the SpawnControlUI cached-sorted
        // idiom (cachedSortedCandidates + invalidation tuple). A section re-sorts ONLY
        // when its row count changes, the sort key/direction changes, OR the throttled
        // legibility cache was refreshed this tick (so the NextDelivery / Destination
        // sort keys, which live in that cache, do not go stale). NEVER per IMGUI frame.
        // The legibility-refresh token is lastLegibilityComputeRealtime, bumped once per
        // ~1 Hz refresh (and reset to -1 on any dirtying mutation), so comparing it to a
        // cached copy detects exactly the refreshes that could move a sort key.
        private List<Route> cachedSortedActive = new List<Route>();
        private int cachedActiveCount = -1;
        private List<Route> cachedSortedPaused = new List<Route>();
        private int cachedPausedCount = -1;
        private LogisticsRouteSortColumn cachedRouteSortColumn = LogisticsRouteSortColumn.Name;
        private bool cachedRouteSortAscending = true;
        // Per-section legibility stamps: the Active and Paused tables are drawn in the
        // same IMGUI pass (Active first), so a SHARED stamp would be consumed by Active
        // and leave Paused sorting on stale dynamic keys after a ~1 Hz refresh. Keep one
        // stamp per section (mirroring cachedActiveCount / cachedPausedCount).
        private float cachedActiveLegibilityStamp = -2f;
        private float cachedPausedLegibilityStamp = -2f;

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

            // M5 (D8): the route's derived window basis + the label appended
            // after the cadence ("(Duna transfer)" / "(launch window schedule)";
            // null for a flat route - drawn nowhere, flat rows unchanged).
            // Computed on the ~1 Hz refresh so the per-frame stepper / row draw
            // never resolves a LoopUnit.
            public RouteWindowBasis Basis;
            public string BasisLabel;

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

            // M4: DestinationFull free-capacity context line, e.g. "Munar Station
            // tanks full: 0.0 of 150.0 LiquidFuel free". Built ONLY for a
            // DestinationFull route, from a LIVE LiveDeliveryCapacityProbe over the
            // resolved destination Vessel, here on the ~1 Hz refresh (never per
            // IMGUI frame). Null for any other status; the detail draw path reads
            // this cached string.
            public string CapacityContext;

            // L1: Paused-section "never run yet" vs "deliberately paused" Status-cell
            // distinction. Computed only for Paused routes (CompletedCycles == 0 reads
            // cyan "New (not yet run)" plus the "Send Once to test" guidance; cycles > 0
            // reads grey "Paused"). The draw path reads these cached values so the
            // classifier decision is logged once on the ~1 Hz refresh, not per frame.
            public LogisticsDeliveryPresentation.PausedRouteLabel PausedLabel;
            public string PausedLabelText;        // resolved cell text for the New / Paused label
            public bool ShowSendOnceGuidance;     // true only on a never-run paused row

            // Run-cost (Phase 2): the per-run net funds cost (launch - recovered),
            // computed on the ~1 Hz timer from ComputeELS() + the route's resolved
            // tree-member id set, NEVER on the IMGUI draw path (the recovery sum is an
            // O(actions) scan). Career + KSC-origin only: Applicable / CostKnown are
            // false otherwise and the detail draw path then renders nothing.
            public RouteRunCostCalculator.RouteRunCost RunCost;

            // M6 hold reasons: the persisted last-hold reason rendered to player
            // language on the ~1 Hz pass. HoldText is the full detail-panel line
            // (including the "checked {age} ago" suffix); HoldShort is the
            // one-clause status-cell tooltip augmentation. Both null when no
            // hold is recorded OR the status is MissingSourceRecording /
            // SourceChanged (LogisticsHoldPresentation.ShouldDisplayHold - those
            // statuses already explain themselves). The cache-miss default in
            // GetLegibility leaves both null, so a not-yet-refreshed row never
            // flashes a hold line.
            public string HoldText;
            public string HoldShort;

            // Last-partial-delivery report (destination-capacity gate
            // follow-up): the persisted Route.LastPartialDelivery* fields
            // rendered on the ~1 Hz pass. The full detail-panel line
            // ("Last delivery was partial: ... (age ago)"); null when the most
            // recent delivery was full (the orchestrator clears the report) or
            // none has happened. Displayed on every status - it reports a past
            // physical loss, so no status makes it misleading.
            public string PartialText;

            // M6 closeout (row-level hold treatment): the compact truncated
            // "Held: <specific reason>" string shown IN the Status cell,
            // replacing the generic per-status sentence while a displayable
            // hold is recorded (non-Paused sections only; the Paused cell
            // keeps its L1 New/Paused label). Null when no hold displays -
            // the cell then falls back to StatusReason. Built on the ~1 Hz
            // pass from the same persisted Route.LastHold* fields as
            // HoldShort; the full clause stays in the tooltip.
            public string HoldCellText;

            // M6 per-cycle flow: one compact line per recent completed cycle
            // ("Cycle 3 (2.1h ago): paid 500 funds at KSC; delivered 150.0
            // LiquidFuel to Munar Station"), newest first, bounded to the last
            // LogisticsFlowPresentation.MaxCyclesShown cycles. Built on the
            // ~1 Hz pass from the SAME ELS walk as the H2/H3 delivery summary
            // (LogisticsFlowPresentation.CollectRows). Null when the route has
            // no cycle-scoped cargo rows yet, so the detail panel renders no
            // header for a never-run route.
            public List<LogisticsFlowPresentation.CycleFlowLine> FlowLines;
        }

        // Deferred mutations: collected during the draw loop and applied after the
        // scroll view so we never mutate RouteStore.CommittedRoutes mid-iteration.
        private Route pendingPause;
        private Route pendingActivate;
        private Route pendingSendOnce;
        // M6: which routes were armed via Send Once (not Pause). Both arming paths set
        // the same Route.PauseAfterCurrentCycle flag and a Send-Once arm un-pauses
        // Paused -> Active -> InTransit while still armed, so route.Status alone cannot
        // tell a Send-Once-in-transit from a Pause-armed-in-transit. This UI-side set
        // (populated when a Send Once succeeds, pruned on the ~1 Hz refresh) records the
        // provenance so the disabled-button label is correct for the whole cycle; the
        // status heuristic stays the fallback for entries lost across save/reload.
        private readonly HashSet<string> sendOnceArmedRouteIds = new HashSet<string>();
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
        // Deferred priority edit (M1 dispatch priority): the stepper records
        // (route, new value) during the draw loop; ApplyPendingActions commits via
        // RoutePriority.Apply after the scroll view (same deferred-mutation
        // discipline as the cadence stepper).
        private Route pendingPriorityRoute;
        private int pendingPriorityValue;
        // M6 candidate intent helper: deferred dismiss / restore of a candidate
        // tree. The row buttons record the tree id + display label during the
        // draw loop; ApplyPendingActions commits through
        // RouteStore.DismissCandidateTree / RestoreCandidateTree after the
        // scroll view (same deferred-mutation discipline as the action buttons)
        // and dirties the candidate + near-miss caches so the row disappears /
        // reappears immediately instead of waiting out the ~1s timer.
        private string pendingDismissTreeId;
        private string pendingDismissLabel;
        private string pendingRestoreTreeId;
        private string pendingRestoreLabel;

        // M1 inline interval text field (deferred-commit, SettingsWindowUI idiom).
        // Keyed by route.Id (NOT a row index) because routes are re-sectioned /
        // added / removed between frames. While editing, the typed text is held here
        // and committed on Enter or click-outside through ParseAndSnapInterval ->
        // ApplyMultiplier (run DIRECTLY in the commit, not via a frame-reset pending
        // field, per the QW2 lesson). intervalEditRect is the field's screen rect for
        // the click-outside-to-commit hit test.
        private string intervalEditRouteId;
        private string intervalEditText;
        private bool intervalEditFocused;
        private Rect intervalEditRect;

        // M2 detail-panel route rename (deferred-commit, RecordingsTableUI idiom).
        // Keyed by route.Id for the same re-sectioning reason as the interval edit.
        // The rename commit writes Route.Name directly (already persisted via the
        // existing codec, so no schema work); empty / whitespace / unchanged input is
        // rejected by the pure LogisticsRenamePresentation.ComputeRouteRename helper.
        private string renamingRouteId;
        private string renamingRouteText;
        private bool renamingRouteFocused;
        private Rect renamingRouteRect;

        // M4c round-trip link picker (synchronous IMGUI popup, GroupPickerUI idiom).
        // Persistent state (deliberately NOT in the frame-top pending reset): armed by
        // the detail-panel "Link round-trip..." button, drawn as its OWN window from
        // DrawIfOpen after the main window (a nested GUILayoutWindow is illegal), and
        // committed inline on the Link button (RouteStore.LinkRoutes) so the QW2
        // deferred-field trap does not apply (the commit runs inside the draw pass).
        private bool linkPickerOpen;
        private string linkPickerSourceRouteId;
        private string linkPickerSourceName;
        private string linkPickerSelectedId;
        private Vector2 linkPickerPosition;
        private Rect linkPickerRect;
        private bool linkPickerResizing;
        private Vector2 linkPickerScroll;
        private const float LinkPickerMinW = 240f;
        private const float LinkPickerMinH = 180f;

        // Status text styles (lazy; mirrors RecordingsTableUI.EnsureStatusStyles).
        private GUIStyle statusStyleGreen;   // Active / InTransit
        private GUIStyle statusStyleYellow;  // WaitingForResources / WaitingForFunds / DestinationFull
        private GUIStyle statusStyleRed;     // EndpointLost / MissingSourceRecording / SourceChanged
        private GUIStyle statusStyleGrey;    // Paused
        private GUIStyle statusStyleCyan;    // L1 paused-route "New (not yet run)" label
        private GUIStyle detailStyle;

        // Column widths. Header and rows use the same constants and live in the
        // same per-section box, so columns line up like the Recordings window.
        private const float ColW_Num = 30f;        // "#" row-index column (per section)
        private const float ColW_Origin = 95f;      // compacted (QW-origin short: "KSC (funds)" / "depot pid=N")
        private const float ColW_Destination = 180f;
        // Widened from 70f for the M1 inline cadence control: a compact "[-] field
        // [+]" stepper (decrement, an editable target-seconds TextField, increment)
        // plus a small "Nx" multiplier label needs the extra width; the read-only
        // "Nx (~human)" label no longer lives here.
        private const float ColW_Interval = 150f;
        // L2: the standalone Transit column (ColW_Transit, 70px) was removed to narrow
        // the window; the transit value now rides in the Interval cell's "Nx" tooltip
        // and the expand-panel detail line.
        private const float ColW_Cycles = 80f;     // "3 / 1 skipped" fits without clipping (QW5)
        private const float ColW_NextDelivery = 135f; // H1 "Next delivery" countdown ("T-12m 5s"); +50% for room
        // Uniform width for the route-detail Name-row buttons (Rename / Log (Route) /
        // Log (Mission)) so they read as one group; sized for the widest label.
        private const float RouteDetailButtonWidth = 104f;
        private const float ColW_Status = 240f;     // plain-English reason text; H3 badge now carries the at-a-glance verdict so the reason can wrap
        private const float ColW_Badge = 120f;      // H3 "Flying, not delivering" / "Delivering" badge
        private const float ColW_Actions = 190f;   // fixed action cell so Name-expand is identical every row
        // L3: the Candidates section has its own purpose-built header (Name / Origin /
        // Destination / Would deliver / Transit / Actions); the route-only columns
        // (Interval / Cyc / Next / Status / Delivery) do not apply to a candidate, so
        // they were dropped. The Would-deliver cell holds the per-cycle delivery
        // manifest text ("LiquidFuel 150.0, 2 inventory item(s)"), which can be long, so
        // it gets a wide cell. The candidates bubble is a separate box and does not have
        // to match the route bubble width, so this column does not push MinWindowWidth.
        private const float ColW_WouldDeliver = 260f;
        // L3: the Candidates Transit cell shows the candidate's natural run duration in
        // its own column (the route tables fold transit into the Interval tooltip; a
        // candidate has no Interval, so transit gets a real cell here).
        private const float ColW_CandidateTransit = 80f;

        private const float SpacingSmall = 3f;
        private const float SpacingLarge = 8f;
        // L2: fixed columns now total ~1175px after dropping the 70px Transit column
        // (Num 30 + Origin 95 + Destination 180 + Interval 150 + Cyc 80 + Next 90 +
        // Status 240 + Delivery 120 + Actions 190), so the window floor drops in step to
        // keep the expanding Name column a usable share without leaving the window wider
        // than its content. The remaining columns (Status / Destination) are candidates
        // for a further fold in a later pass; this L2 step does a conservative one-column
        // compression that is safe without in-game validation.
        private const float MinWindowWidth = 1410f;
        private const float MinWindowHeight = 500f;

        public bool IsOpen
        {
            get { return showWindow; }
            // M4c: closing the window must also dismiss the round-trip link picker
            // (it draws as its own window from DrawIfOpen, which is skipped while the
            // host is closed). Without this, re-opening the window the same scene
            // re-pops a picker armed with stale source state — the leak the mirrored
            // GroupPickerUI idiom avoids via its host's Close() calls.
            set { showWindow = value; if (!value) linkPickerOpen = false; }
        }

        internal LogisticsWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
            // Restore the rect saved before the last scene change. If the window has
            // never been drawn this session, sessionWindowRect is default-zero
            // (width < 1) so the first-open default-init in DrawIfOpen still runs.
            windowRect = sessionWindowRect;
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
                // First-open default. MUST be >= MinWindowWidth/Height: HandleResizeDrag only
                // enforces the minimum during a resize drag, so a smaller default would open
                // the window below its own minimum and snap on first touch (the old 1360
                // default predated the 1410 minimum bump and did exactly that). 1556 is the
                // playtest-preferred width (2026-06-10 log: last resize ended w=1556 h=500),
                // which also fits the widened Next column.
                float x = mainWindowRect.x + mainWindowRect.width + 10;
                windowRect = new Rect(x, mainWindowRect.y, 1556, 500);
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
            // Capture the (possibly moved or resized) rect so it survives the next
            // scene change and is restored by the constructor of the next instance.
            sessionWindowRect = windowRect;

            // M4c: the round-trip link picker draws as its own window on top of the
            // logistics window (the GroupPickerUI idiom — drawn after the host window).
            // Armed from the detail panel; commits synchronously on Link.
            DrawLinkPicker();

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
            // M3: committed-but-not-yet-eligible trees, throttled on the same ~1 Hz
            // timer as the candidates above (never derived on the draw path).
            List<RouteNearMiss> nearMisses = GetNearMisses();

            // Click outside an active inline edit field (M1 interval / M2 rename) ->
            // commit it. Runs BEFORE the rows draw (matching the RecordingsTableUI
            // defocus ordering) so the commit lands this frame; the rect was captured
            // on the previous frame's draw. Both commits run their action directly
            // (no frame-reset pending field), so an async click cannot be clobbered.
            HandleLogisticsDefocus(routes);

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
            pendingPriorityRoute = null;
            pendingPriorityValue = 0;
            pendingDismissTreeId = null;
            pendingDismissLabel = null;
            pendingRestoreTreeId = null;
            pendingRestoreLabel = null;

            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.ExpandHeight(true));

            // Each section is its own gray bubble with its own header row, so the
            // header and data columns share the box and line up exactly. Titles are the
            // plain section name (no count), centered (see DrawSectionHeader).
            DrawRouteSectionBubble("Active Routes", activeRoutes, RouteSection.Active, currentUT);
            DrawRouteSectionBubble("Paused Routes", pausedRoutes, RouteSection.Paused, currentUT);
            DrawCandidateSectionBubble("Candidates", candidates, nearMisses);

            GUILayout.EndScrollView();

            // Tooltip echo box (matches SettingsWindowUI house style). Read
            // GUI.tooltip AFTER all controls have drawn this frame so it reflects
            // the currently hovered control, then echo it in a box so hovering any
            // cell / button shows its help text in-window.
            DrawTooltipEchoBox(GUI.tooltip);

            // Full-width Close button at the bottom (matches Kerbals / Settings windows).
            GUILayout.Space(SpacingSmall);
            if (GUILayout.Button("Close"))
            {
                showWindow = false;
                linkPickerOpen = false; // M4c: dismiss the link picker with the window.
                ParsekLog.Verbose("UI", "Logistics window closed");
            }

            ParsekUI.DrawResizeHandle(windowRect, ref isResizing, "Logistics window");
            GUI.DragWindow();

            // Apply deferred mutations now that the draw loop is done.
            ApplyPendingActions(currentUT);
        }

        /// <summary>
        /// Commits an active inline edit (M1 interval field / M2 rename field) when
        /// the player clicks OUTSIDE its field rect, mirroring
        /// <c>RecordingsTableUI.HandleRecordingsDefocus</c> and the SettingsWindowUI
        /// click-outside-commit. Only acts on a MouseDown whose position is outside
        /// the captured field rect (a non-zero-width rect from the previous frame's
        /// draw). The route is resolved by the stored edit id from
        /// <paramref name="routes"/>; both commit helpers run their action directly,
        /// so a resolved-null id just clears the edit state.
        /// </summary>
        private void HandleLogisticsDefocus(IReadOnlyList<Route> routes)
        {
            if (Event.current.type != EventType.MouseDown)
                return;

            Vector2 mouse = Event.current.mousePosition;

            if (renamingRouteId != null && renamingRouteRect.width > 0
                && !renamingRouteRect.Contains(mouse))
            {
                CommitRouteRename(FindRouteById(routes, renamingRouteId));
            }
            else if (intervalEditRouteId != null && intervalEditRect.width > 0
                && !intervalEditRect.Contains(mouse))
            {
                Route route = FindRouteById(routes, intervalEditRouteId);
                if (route != null)
                    CommitIntervalEdit(route);
                else
                    ClearIntervalEdit();
            }
        }

        private static Route FindRouteById(IReadOnlyList<Route> routes, string id)
        {
            if (routes == null || string.IsNullOrEmpty(id))
                return null;
            for (int i = 0; i < routes.Count; i++)
            {
                Route r = routes[i];
                if (r != null && string.Equals(r.Id, id, System.StringComparison.Ordinal))
                    return r;
            }
            return null;
        }

        private void DrawRouteSectionBubble(string title, List<Route> rows, RouteSection section, double currentUT)
        {
            DrawSectionHeader(title);
            GUILayout.BeginVertical(GUI.skin.box);
            // L2: clickable sort headers (cached re-sort). The Active and Paused tables
            // share the sort column / direction; clicking a header re-sorts both.
            DrawRouteSortableHeader();
            if (rows.Count == 0)
                GUILayout.Label("  (none)", detailStyle);
            else
            {
                // Draw the cached-sorted rows (re-sorted only on count / sort-state /
                // legibility-stamp change, never per frame). Row index is the display
                // position in the sorted list.
                List<Route> sorted = GetSortedRoutesForSection(rows, section);
                for (int i = 0; i < sorted.Count; i++)
                    DrawRouteRow(sorted[i], section, i + 1, currentUT);
            }
            GUILayout.EndVertical();
            GUILayout.Space(SpacingSmall);
        }

        private void DrawCandidateSectionBubble(string title, List<RouteCandidate> rows, List<RouteNearMiss> nearMisses)
        {
            DrawSectionHeader(title);
            GUILayout.BeginVertical(GUI.skin.box);
            DrawCandidateColumnHeader();
            if (rows.Count == 0)
                GUILayout.Label("  No eligible Supply Runs. Fly a one-way transport that docks, transfers cargo to the destination, and undocks, then commit and seal the recording.", detailStyle);
            for (int i = 0; i < rows.Count; i++)
                DrawCandidateRow(rows[i], i + 1);

            // M3: a collapsible subsection that surfaces WHY a recently committed tree
            // is NOT a candidate (not fully sealed, or sealed-but-ineligible), so the
            // player can tell "I have not committed/sealed it yet" apart from "the run
            // does not match the dock-deliver-undock proof". Reads only the cached list.
            DrawNearMissSubsection(nearMisses);

            // M6 candidate intent helper: the collapsed "Dismissed (N)" subsection
            // listing player-dismissed trees, each with a Restore control. Reads
            // only the cached rows (rebuilt on the ~1 Hz candidate refresh).
            DrawDismissedSubsection();

            GUILayout.EndVertical();
            GUILayout.Space(SpacingSmall);
        }

        // M3 near-miss subsection key (collapsible via the shared expandedRows set).
        private const string NearMissSectionKey = "nearmiss:section";

        /// <summary>
        /// Draws the M3 "Recently committed trees not yet eligible" collapsible
        /// subsection at the bottom of the Candidates bubble. A caret header toggled
        /// through the shared <see cref="expandedRows"/> / <see cref="ToggleExpanded"/>
        /// idiom (fixed key <see cref="NearMissSectionKey"/>); when expanded, one line
        /// per <see cref="RouteNearMiss"/> showing the tree display name and its
        /// blocking reason from the pure
        /// <see cref="LogisticsRejectPresentation.DescribeNearMiss"/>. Nothing renders
        /// when the cached near-miss list is empty. All reads are against the cached
        /// list (built on the ~1 Hz timer in <see cref="GetNearMisses"/>), never live
        /// on the IMGUI draw path.
        /// </summary>
        private void DrawNearMissSubsection(List<RouteNearMiss> nearMisses)
        {
            if (nearMisses == null || nearMisses.Count == 0)
                return;

            bool expanded = expandedRows.Contains(NearMissSectionKey);
            string arrow = expanded ? "\u25bc" : "\u25b6";
            GUILayout.Space(SpacingSmall);
            GUILayout.BeginHorizontal();
            GUILayout.Space(8f);
            if (GUILayout.Button(
                    new GUIContent(
                        $"{arrow} Recently committed trees not yet eligible ({nearMisses.Count.ToString(CultureInfo.InvariantCulture)})",
                        "Committed trees that are not Supply Run candidates yet, with the reason: not fully sealed, or sealed but the run does not match the dock-deliver-undock proof."),
                    GUI.skin.label, GUILayout.ExpandWidth(true)))
                ToggleExpanded(NearMissSectionKey, "near-miss subsection");
            GUILayout.EndHorizontal();

            if (!expanded)
                return;

            for (int i = 0; i < nearMisses.Count; i++)
            {
                RouteNearMiss nm = nearMisses[i];
                if (nm == null) continue;
                string treeName = NearMissTreeLabel(nm.Tree);
                string reason = LogisticsRejectPresentation.DescribeNearMiss(
                    nm.Status, nm.NotSealed, nm.ReflyableCount, nm.RejectDetail);
                // M6 candidate intent helper: near-miss rows get the same
                // Dismiss control as candidate rows (a dismissed tree leaves
                // the WHOLE Candidates section). Row layout mirrors DetailLine
                // (24px indent + detailStyle label) with the button appended;
                // the button only draws for a resolvable tree id, a per-row
                // condition that is stable within a frame (cached list), so the
                // IMGUI control count stays consistent across Layout/Repaint.
                GUILayout.BeginHorizontal();
                GUILayout.Space(24f);
                GUILayout.Label($"{treeName} - {reason}", detailStyle, GUILayout.ExpandWidth(true));
                string nmTreeId = nm.Tree?.Id;
                if (!string.IsNullOrEmpty(nmTreeId)
                    && GUILayout.Button(new GUIContent("Dismiss",
                        "Hide this tree from the Candidates section (it is not meant to become a route). Restore it any time from the Dismissed list below."),
                        GUILayout.Width(70)))
                {
                    pendingDismissTreeId = nmTreeId;
                    pendingDismissLabel = treeName;
                }
                GUILayout.EndHorizontal();
            }
        }

        // M6 dismissed-candidates subsection key (collapsible via the shared
        // expandedRows set, mirroring NearMissSectionKey).
        private const string DismissedSectionKey = "dismissed:section";

        /// <summary>
        /// Draws the M6 "Dismissed (N)" collapsible subsection at the bottom of
        /// the Candidates bubble: the trees the player dismissed as route
        /// candidates, each with a Restore control. Mirrors the near-miss
        /// subsection idiom exactly (caret header via the shared
        /// <see cref="expandedRows"/> / <see cref="ToggleExpanded"/>, fixed key
        /// <see cref="DismissedSectionKey"/>); nothing renders when nothing is
        /// dismissed. Reads only <see cref="cachedDismissedRows"/> (labels
        /// resolved on the ~1 Hz candidate-cache refresh, never live on the
        /// IMGUI draw path); Restore defers through
        /// <see cref="pendingRestoreTreeId"/> to <see cref="ApplyPendingActions"/>.
        /// </summary>
        private void DrawDismissedSubsection()
        {
            if (cachedDismissedRows.Count == 0)
                return;

            bool expanded = expandedRows.Contains(DismissedSectionKey);
            string arrow = expanded ? "▼" : "▶";
            GUILayout.Space(SpacingSmall);
            GUILayout.BeginHorizontal();
            GUILayout.Space(8f);
            if (GUILayout.Button(
                    new GUIContent(
                        $"{arrow} Dismissed ({cachedDismissedRows.Count.ToString(CultureInfo.InvariantCulture)})",
                        "Trees you dismissed as route candidates. A dismissed tree is hidden from the candidates and near-miss lists; Restore brings it back."),
                    GUI.skin.label, GUILayout.ExpandWidth(true)))
                ToggleExpanded(DismissedSectionKey, "dismissed subsection");
            GUILayout.EndHorizontal();

            if (!expanded)
                return;

            for (int i = 0; i < cachedDismissedRows.Count; i++)
            {
                (string treeId, string label) = cachedDismissedRows[i];
                GUILayout.BeginHorizontal();
                GUILayout.Space(24f);
                GUILayout.Label(label, detailStyle, GUILayout.ExpandWidth(true));
                if (GUILayout.Button(new GUIContent("Restore",
                        "Offer this tree as a Supply Run candidate again."),
                        GUILayout.Width(70)))
                {
                    pendingRestoreTreeId = treeId;
                    pendingRestoreLabel = label;
                }
                GUILayout.EndHorizontal();
            }
        }

        // Display label for a near-miss tree: the player-visible TreeName, falling
        // back to the short tree id and then "<unnamed>" so a row is never blank.
        private static string NearMissTreeLabel(RecordingTree tree)
        {
            if (tree == null)
                return "<unnamed>";
            if (!string.IsNullOrEmpty(tree.TreeName))
                return tree.TreeName;
            if (!string.IsNullOrEmpty(tree.Id))
                return ShortId(tree.Id);
            return "<unnamed>";
        }

        // L3: purpose-built static header for the Candidates section. A candidate is a
        // sealed-but-not-yet-promoted Supply Run, so the route-only columns (Interval /
        // Cyc / Next / Status / Delivery) do not apply and used to render literal "-" /
        // "eligible" placeholders. This header carries only the columns that mean
        // something for a candidate: # / Name / Origin / Destination / Would deliver /
        // Transit / Actions. The "eligible" / sealed explanation that used to live in
        // the dropped Status cell is relocated to the section-header "Would deliver"
        // tooltip so the copy is not lost. The Candidates section is now INDEPENDENT of
        // the route DrawRouteSortableHeader / DrawRouteRow pair; DrawCandidateRow must add /
        // drop the SAME cells in the SAME order as this header to stay column-aligned.
        private void DrawCandidateColumnHeader()
        {
            GUIStyle h = parentUI.GetColumnHeaderStyle();
            GUILayout.BeginHorizontal();
            GUILayout.Label("#", h, GUILayout.Width(ColW_Num));
            GUILayout.Label("Name", h, GUILayout.ExpandWidth(true));
            GUILayout.Label("Origin", h, GUILayout.Width(ColW_Origin));
            GUILayout.Label("Destination", h, GUILayout.Width(ColW_Destination));
            GUILayout.Label(
                new GUIContent("Would deliver",
                    "Each candidate is a sealed, valid Supply Run: resources / inventory it would deliver to the destination per cycle. Create Route promotes it to a Paused route you can Send Once / Activate."),
                h, GUILayout.Width(ColW_WouldDeliver));
            GUILayout.Label("Transit", h, GUILayout.Width(ColW_CandidateTransit));
            GUILayout.Label("Actions", h, GUILayout.Width(ColW_Actions));
            GUILayout.EndHorizontal();
        }

        // L2: the clickable sort header for the Active / Paused route tables. Each
        // sortable column routes through parentUI.DrawSortableHeaderCore (the shared
        // generic header used by SpawnControlUI / RecordingsTableUI), toggling the shared
        // routeSortColumn / routeSortAscending and invalidating the cached-sorted lists
        // on change. The "#" index, the (non-data) Transit-less layout, and the Actions
        // cell are NOT sortable, so they draw as plain header labels; the cell order
        // matches DrawRouteRow exactly.
        private void DrawRouteSortableHeader()
        {
            GUIStyle h = parentUI.GetColumnHeaderStyle();
            GUILayout.BeginHorizontal();
            GUILayout.Label("#", h, GUILayout.Width(ColW_Num));
            DrawRouteSortColumn("Name", LogisticsRouteSortColumn.Name, 0f, true);
            DrawRouteSortColumn("Origin", LogisticsRouteSortColumn.Origin, ColW_Origin, false);
            DrawRouteSortColumn("Destination", LogisticsRouteSortColumn.Destination, ColW_Destination, false);
            DrawRouteSortColumn("Interval", LogisticsRouteSortColumn.Interval, ColW_Interval, false);
            DrawRouteSortColumn("Cyc", LogisticsRouteSortColumn.Cycles, ColW_Cycles, false);
            DrawRouteSortColumn("Next", LogisticsRouteSortColumn.NextDelivery, ColW_NextDelivery, false);
            DrawRouteSortColumn("Status", LogisticsRouteSortColumn.Status, ColW_Status, false);
            DrawRouteSortColumn("Delivery", LogisticsRouteSortColumn.Delivery, ColW_Badge, false);
            GUILayout.Label("Actions", h, GUILayout.Width(ColW_Actions));
            GUILayout.EndHorizontal();
        }

        // Thin per-window wrapper around the shared sortable-header helper (mirrors
        // SpawnControlUI.DrawSpawnSortableHeader). On a click that changes the sort, the
        // helper flips routeSortColumn / routeSortAscending and the onChanged callback
        // logs the decision (once, on the click, not per frame) and dirties the cached
        // sort tuple so both sections re-sort on the next draw.
        private void DrawRouteSortColumn(string label, LogisticsRouteSortColumn col, float width, bool expand)
        {
            parentUI.DrawSortableHeaderCore(
                label, col, ref routeSortColumn, ref routeSortAscending, width, expand,
                () =>
                {
                    // Force a re-sort next draw: clearing the cached counts guarantees
                    // both section caches miss even if the row count is unchanged.
                    cachedActiveCount = -1;
                    cachedPausedCount = -1;
                    ParsekLog.Verbose("UI",
                        $"Logistics route sort changed column={routeSortColumn} ascending={routeSortAscending}");
                });
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

            // A route armed via Send Once is doing a single one-shot cycle, so its
            // cadence/interval is irrelevant: hide the Interval cell while it sends once
            // (the disabled "Sending one cycle..." action still shows in the Actions cell).
            bool sendOnceArmed = !string.IsNullOrEmpty(route.Id)
                && sendOnceArmedRouteIds.Contains(route.Id);
            bool sendingOnce = ShouldShowSendingButton(route)
                && ResolveArmedKind(sendOnceArmed, route.Status) == ArmedSendKind.SendOnce;

            GUILayout.Label(FormatOrigin(route), GUILayout.Width(ColW_Origin));
            // H4: name the destination vessel (resolved from the endpoint PID) instead
            // of bare coords; coords move to the hover tooltip on fallback. Resolved on
            // the ~1 Hz cache refresh, so this cell just reads the cached strings.
            GUILayout.Label(
                new GUIContent(leg.DestinationText ?? "-", leg.DestinationTooltip ?? string.Empty),
                GUILayout.Width(ColW_Destination));
            // M1: inline cadence control in the Interval cell. A compact "[-] field
            // [+]" stepper plus a small "Nx" multiplier label. The -/+ buttons reuse
            // the existing deferred-mutation fields (committed synchronously in
            // ApplyPendingActions the same frame); the editable field types a target
            // interval and commits on Enter / click-outside through
            // ParseAndSnapInterval -> ApplyMultiplier (run directly in the commit).
            // Hidden (empty cell, same width to keep columns aligned) while sending once.
            if (sendingOnce)
            {
                // If an interval edit was in progress for this route, clear it: the cell
                // (and its Enter-commit path) is now gone, so the edit state would
                // otherwise linger with a stale rect until the next click-outside.
                if (string.Equals(intervalEditRouteId, route.Id, System.StringComparison.Ordinal))
                    ClearIntervalEdit();
                GUILayout.Label(GUIContent.none, GUILayout.Width(ColW_Interval));
            }
            else
                DrawIntervalCell(route, leg);
            // L2: the standalone Transit column was dropped to narrow the window; the
            // transit value now rides in the Interval cell's "Nx" tooltip (DrawIntervalCell)
            // and the expand-panel detail line, so no Transit cell is drawn here.
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
                    "Time until this route's next scheduled delivery (next dock crossing). A blocked route shows when it next rechecks eligibility. An inter-body route counts down to its next launch window."),
                GUILayout.Width(ColW_NextDelivery));

            // Show the plain-English reason IN the cell; keep the raw enum name in
            // the hover tooltip (a one-word state token for players who want it). L1:
            // in the Paused section the cell instead distinguishes a never-run route
            // (cyan "New (not yet run)") from a deliberately-paused one (grey "Paused"),
            // reading the classification cached in the ~1 Hz legibility pass; the H3
            // Delivery badge column stays grey "Paused" for both.
            // M6 hold reasons + closeout row-level treatment: while a
            // displayable hold is recorded, the non-Paused cell carries the
            // compact SPECIFIC reason ("Held: origin out of LiquidFuel",
            // truncated) instead of the generic per-status sentence; the full
            // clause rides the tooltip's second line under the raw enum name.
            if (section == RouteSection.Paused)
            {
                GUIStyle pausedStyle = leg.PausedLabel == LogisticsDeliveryPresentation.PausedRouteLabel.New
                    ? statusStyleCyan
                    : statusStyleGrey;
                GUILayout.Label(
                    new GUIContent(leg.PausedLabelText ?? StatusReason(route.Status),
                        LogisticsHoldPresentation.StatusCellTooltip(route.Status, leg.HoldShort)),
                    pausedStyle, GUILayout.Width(ColW_Status));
            }
            else
            {
                GUIStyle statusStyle = StatusStyleFor(route.Status);
                // A held row never reads green: an Active loop route blocked at
                // its last crossing (e.g. WaitingForPartner, escrow short) keeps
                // Status=Active, so tint the held cell yellow. Red hard-broken
                // statuses keep their red.
                if (leg.HoldCellText != null && statusStyle == statusStyleGreen)
                    statusStyle = statusStyleYellow;
                GUILayout.Label(
                    new GUIContent(leg.HoldCellText ?? StatusReason(route.Status),
                        LogisticsHoldPresentation.StatusCellTooltip(route.Status, leg.HoldShort)),
                    statusStyle, GUILayout.Width(ColW_Status));
            }

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
                // Armed one-shot / in-flight cycle. Both arming paths set the same
                // PauseAfterCurrentCycle flag, so M6 resolves which action armed it
                // from the send-once provenance set first (authoritative within the
                // session) and falls back to the route's status only for entries lost
                // across save/reload: a Pause requested while InTransit reads "Pausing
                // after this cycle..." (it finishes the current cycle then stops); a
                // Send Once arm reads "Sending one cycle...". The button stays disabled
                // either way so the click reads as registered and the route reads as
                // armed-and-waiting rather than idle. Wider than the old "Sending..."
                // cell so the longer pause label does not clip. (sendOnceArmed was
                // computed once at the top of the row.)
                ArmedSendKind armedKind = ResolveArmedKind(sendOnceArmed, route.Status);
                bool prevEnabled = GUI.enabled;
                GUI.enabled = false;
                GUILayout.Button(new GUIContent(
                        LabelForArmedState(armedKind),
                        TooltipForArmedState(armedKind)),
                    GUILayout.Width(160f));
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
                        GUILayout.Width(79)))
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

            // L1: a one-line "Send Once to test" guidance under a never-run paused row
            // only. Rendered as a full-width line inside this section box (NOT a column
            // cell), so the header / route-row / candidate-row column counts stay
            // aligned. Drawn for every never-run paused row whether or not it is
            // expanded, since the cue is most useful before the player opens the detail.
            if (section == RouteSection.Paused && leg.ShowSendOnceGuidance)
                GUILayout.Label(
                    new GUIContent(LogisticsDeliveryPresentation.SendOnceGuidanceText,
                        "This route has never run. Use Send Once to fire one test cycle without activating periodic dispatch."),
                    detailStyle);

            if (expanded)
                DrawRouteDetail(route, currentUT);
        }

        /// <summary>
        /// Draws the M1 inline cadence control inside the Interval cell of a route
        /// row: a compact "[-] field [+]" stepper plus a small "Nx" multiplier label.
        /// The "-" / "+" buttons step the multiplier through the existing
        /// deferred-mutation fields (committed synchronously the same frame in
        /// <see cref="ApplyPendingActions"/>), so the +/- path is NOT the QW2
        /// frame-reset trap. The editable TextField holds a typed target interval in
        /// seconds using the SettingsWindowUI deferred-commit idiom
        /// (<see cref="GUI.SetNextControlName"/> keyed per route, edit-start on focus,
        /// commit on Enter / click-outside); its commit runs
        /// <see cref="RouteCadence.ParseAndSnapInterval"/> -> the snapped N ->
        /// <see cref="RouteCadence.ApplyMultiplier"/> DIRECTLY in
        /// <see cref="CommitIntervalEdit"/> (never via a frame-reset pending field).
        /// </summary>
        private void DrawIntervalCell(Route route, RouteLegibility leg)
        {
            string controlName = "LogiInterval_" + (route.Id ?? "<no-id>");
            bool editingThis = renamingRouteId == null
                && string.Equals(intervalEditRouteId, route.Id, System.StringComparison.Ordinal);
            int n = Route.ClampCadenceMultiplier(route.CadenceMultiplier);

            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Interval));

            // "-" decrements the multiplier (no-op + greyed at the 1x floor). Routes
            // through the synchronous deferred fields like the detail-panel stepper.
            bool atFloor = n <= 1;
            GUI.enabled = !atFloor;
            if (GUILayout.Button(new GUIContent("-",
                    atFloor ? "Already at the minimum (1x = the fastest the run allows)" : "Dispatch more often"),
                    GUILayout.Width(20f)))
            {
                pendingCadenceRoute = route;
                pendingCadenceMultiplier = RouteCadence.StepMultiplier(n, -1);
            }
            GUI.enabled = true;

            // Editable target-interval field (seconds). Deferred commit: while not
            // editing this route we show the live interval value and arm editing when
            // the field gains focus; while editing we hold the typed text and commit
            // on Enter (focus-loss / click-outside is handled in HandleLogisticsDefocus).
            if (!editingThis)
            {
                string display = FormatIntervalFieldValue(route.DispatchInterval);
                GUI.SetNextControlName(controlName);
                string newText = GUILayout.TextField(display, GUILayout.Width(64f));
                if (GUI.GetNameOfFocusedControl() == controlName)
                {
                    intervalEditRouteId = route.Id;
                    intervalEditText = newText;
                    intervalEditFocused = true;
                    intervalEditRect = GUILayoutUtility.GetLastRect();
                    ParsekLog.Verbose("UI",
                        $"Logistics: interval edit started route={ShortId(route.Id)} value='{newText}'");
                }
            }
            else
            {
                bool submit = Event.current.type == EventType.KeyDown
                    && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
                bool cancel = Event.current.type == EventType.KeyDown
                    && Event.current.keyCode == KeyCode.Escape;

                GUI.SetNextControlName(controlName);
                string newText = GUILayout.TextField(intervalEditText, GUILayout.Width(64f));
                intervalEditRect = GUILayoutUtility.GetLastRect();
                if (newText != intervalEditText)
                    intervalEditText = newText;

                if (!intervalEditFocused)
                {
                    GUI.FocusControl(controlName);
                    intervalEditFocused = true;
                }

                if (submit)
                {
                    CommitIntervalEdit(route);
                    Event.current.Use();
                }
                else if (cancel)
                {
                    ParsekLog.Verbose("UI",
                        $"Logistics: interval edit cancelled route={ShortId(route.Id)}");
                    ClearIntervalEdit();
                    Event.current.Use();
                }
            }

            if (GUILayout.Button(new GUIContent("+", "Dispatch less often"), GUILayout.Width(20f)))
            {
                pendingCadenceRoute = route;
                pendingCadenceMultiplier = RouteCadence.StepMultiplier(n, +1);
            }

            // Compact "Nx" multiplier readout (the human duration moves to the tooltip
            // so the cell stays narrow); hovering shows the cadence + the transit time
            // (which no longer has its own column after the L2 narrowing). M5 (D8):
            // for a WINDOWED basis the tooltip leads with the windowed wording
            // ("2x (every 2nd window)") - interval arithmetic is actively
            // misleading on synodic spacing - and a small basis label follows the
            // Nx readout. Flat rows draw the pre-M5 content byte-identically.
            bool windowed = RouteWindowBasisPresentation.IsWindowedBasis(leg.Basis);
            string nxTooltip = windowed
                ? string.Format(CultureInfo.InvariantCulture,
                    "Dispatch cadence: {0} {1}. The route delivers on the launch windows its ghost flies; use -/+ to deliver every Nth window (1x is the floor).",
                    RouteWindowBasisPresentation.FormatWindowedCadence(n),
                    leg.BasisLabel ?? string.Empty)
                : string.Format(CultureInfo.InvariantCulture,
                    "Dispatch cadence = N x run duration (transit {0}). Type a target interval (e.g. 30m, 2h, 1d, or a plain number = seconds) in the field, or use -/+; the value snaps up to the next whole run-multiple (1x is the floor, the fastest the run allows).",
                    FormatDuration(route.TransitDuration));
            GUILayout.Label(
                new GUIContent(
                    string.Format(CultureInfo.InvariantCulture, "{0}x", n),
                    nxTooltip),
                GUILayout.Width(28f));
            if (windowed && !string.IsNullOrEmpty(leg.BasisLabel))
                GUILayout.Label(leg.BasisLabel, GUILayout.ExpandWidth(false));

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Commits the M1 inline interval edit for <paramref name="route"/>: parses
        /// the typed target seconds and snaps it to a cadence multiplier via
        /// <see cref="RouteCadence.ParseAndSnapInterval"/>, then applies it through
        /// <see cref="RouteCadence.ApplyMultiplier"/> DIRECTLY here (not via a
        /// frame-reset pending field). A rejected parse (empty / garbage /
        /// non-positive) leaves the route unchanged and Warn-logs (mirrors
        /// <c>CommitAutoLoopEdit</c>'s reject branch). On a real change the legibility
        /// cache is dirtied (<c>lastLegibilityComputeRealtime = -1f</c>) so the
        /// route's cells refresh immediately rather than waiting out the ~1s timer.
        /// Always clears the edit state.
        /// </summary>
        private void CommitIntervalEdit(Route route)
        {
            string typed = intervalEditText;
            double span = route != null ? route.TransitDuration : 0.0;
            if (RouteCadence.ParseAndSnapInterval(typed, span, out int multiplier))
            {
                bool changed = RouteCadence.ApplyMultiplier(route, multiplier);
                ParsekLog.Info("UI",
                    $"Logistics: interval edit committed route={ShortId(route?.Id)} typed='{typed}' " +
                    $"N={multiplier.ToString(CultureInfo.InvariantCulture)} result={(changed ? "applied" : "unchanged")}");
                if (changed)
                    lastLegibilityComputeRealtime = -1f;
            }
            else
            {
                ParsekLog.Warn("UI",
                    $"Logistics: interval edit rejected route={ShortId(route?.Id)} typed='{typed}' " +
                    $"span={span.ToString("R", CultureInfo.InvariantCulture)} (route unchanged)");
            }
            ClearIntervalEdit();
        }

        private void ClearIntervalEdit()
        {
            intervalEditRouteId = null;
            intervalEditText = null;
            intervalEditFocused = false;
            intervalEditRect = default;
            GUIUtility.keyboardControl = 0;
        }

        /// <summary>
        /// Formats the editable interval field's display value: a friendly duration
        /// WITH a unit (e.g. "14.0m", "1.6d") via <see cref="FormatDuration"/>, so the
        /// player reads the cadence at a glance. It round-trips through the unit-aware
        /// <see cref="RouteCadence.ParseAndSnapInterval"/> (which accepts that same
        /// "Nm"/"Nh"/"Nd"/"Ns"/plain-number form). A non-positive / non-finite interval
        /// shows "0" so the field is always editable. Pure for unit testing.
        /// </summary>
        internal static string FormatIntervalFieldValue(double seconds)
        {
            if (seconds <= 0.0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
                return "0";
            return FormatDuration(seconds);
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

        /// <summary>
        /// Which action armed a route's <see cref="Route.PauseAfterCurrentCycle"/>
        /// flag (M6). There is no separately tracked armedBy field; both arming
        /// paths set the same bool, so the armer is inferred from the route's status
        /// at the moment the disabled affordance is shown.
        /// </summary>
        internal enum ArmedSendKind
        {
            /// <summary>Armed by Send Once (Active / blocked-active dispatchable wait).</summary>
            SendOnce = 0,

            /// <summary>Armed by Pause requested while the cycle was InTransit.</summary>
            PauseAfterCycle = 1,
        }

        /// <summary>
        /// HEURISTIC fallback for which action armed
        /// <see cref="Route.PauseAfterCurrentCycle"/> when the send-once provenance is
        /// unknown (e.g. lost across save/reload). The two orchestrator paths leave
        /// disjoint statuses AT ARM TIME: <c>TrySendOneCycleNow</c> leaves the route
        /// Active (un-pausing from Paused) or a blocked-active wait; <c>TryPause</c>
        /// sets the flag only on the InTransit branch. But a Send-Once arm then
        /// dispatches Active -> InTransit while still armed, so once the cycle is in
        /// flight status alone reads InTransit for BOTH paths. That is why the live
        /// label uses <see cref="ResolveArmedKind"/> with the
        /// <c>sendOnceArmedRouteIds</c> provenance set as the authoritative source and
        /// this status heuristic only as the post-reload fallback.
        /// </summary>
        internal static ArmedSendKind ClassifyArmedSend(RouteStatus priorStatus)
        {
            return priorStatus == RouteStatus.InTransit
                ? ArmedSendKind.PauseAfterCycle
                : ArmedSendKind.SendOnce;
        }

        /// <summary>
        /// Resolves the armed-state kind for the disabled-button label (M6). When the
        /// route is known to have been armed by Send Once (<paramref name="sendOnceArmed"/>,
        /// from the UI provenance set) the answer is unambiguously
        /// <see cref="ArmedSendKind.SendOnce"/> regardless of the route's current status,
        /// which fixes the mislabel where a Send-Once cycle that has dispatched to
        /// InTransit would otherwise read "Pausing after this cycle...". Otherwise it
        /// falls back to the <see cref="ClassifyArmedSend"/> status heuristic. Pure.
        /// </summary>
        internal static ArmedSendKind ResolveArmedKind(bool sendOnceArmed, RouteStatus priorStatus)
        {
            return sendOnceArmed ? ArmedSendKind.SendOnce : ClassifyArmedSend(priorStatus);
        }

        /// <summary>
        /// The disabled-button label for an armed state (M6). A Pause-armed route
        /// shows "Pausing after this cycle..."; a Send-Once-armed route shows
        /// "Sending one cycle...". Plain ASCII (literal three dots), no glyphs.
        /// </summary>
        internal static string LabelForArmedState(ArmedSendKind kind)
        {
            switch (kind)
            {
                case ArmedSendKind.PauseAfterCycle:
                    return "Pausing after this cycle...";
                case ArmedSendKind.SendOnce:
                default:
                    return "Sending one cycle...";
            }
        }

        /// <summary>
        /// The hover tooltip for an armed state (M6). The Send-Once branch reuses the
        /// existing Send-Once "Sending..." tooltip text verbatim so the prior
        /// behavior is preserved; the Pause branch explains the finish-current-cycle
        /// -then-stop semantics. Plain ASCII.
        /// </summary>
        internal static string TooltipForArmedState(ArmedSendKind kind)
        {
            switch (kind)
            {
                case ArmedSendKind.PauseAfterCycle:
                    return "Pause requested: this route finishes its current cycle, "
                        + "then stops auto-dispatching.";
                case ArmedSendKind.SendOnce:
                default:
                    return "Armed: this route will dispatch one cycle at the next dispatch "
                        + "window (funds, resources, endpoint, and alignment permitting), "
                        + "then return to Paused.";
            }
        }

        private void DrawCandidateRow(RouteCandidate candidate, int rowNum)
        {
            if (candidate?.Analysis == null) return;
            string treeId = candidate.Tree?.Id ?? "<no-tree>";
            string rowKey = "cand:" + treeId;
            bool expanded = expandedRows.Contains(rowKey);

            string name = RouteCreationFormatters.GenerateDefaultRouteName(candidate.Analysis, candidate.Tree);

            GUILayout.BeginHorizontal();

            GUILayout.Label(rowNum.ToString(CultureInfo.InvariantCulture), GUILayout.Width(ColW_Num));

            string arrow = expanded ? "\u25bc" : "\u25b6";
            if (GUILayout.Button($"{arrow} {name}", GUI.skin.label, GUILayout.ExpandWidth(true)))
                ToggleExpanded(rowKey, name);

            GUILayout.Label(FormatCandidateOrigin(candidate.Analysis, candidate.Tree), GUILayout.Width(ColW_Origin));
            GUILayout.Label(FormatEndpointShort(candidate.Analysis.ConnectionWindow?.EndpointAtDock), GUILayout.Width(ColW_Destination));
            // L3: Would-deliver cell. The per-cycle manifest text comes from the shared
            // pure LogisticsDeliveryPresentation.FormatWouldDeliver, the same formatter
            // the candidate detail line uses, so the cell and the detail never diverge.
            // The "eligible" / sealed copy that used to ride the dropped Status cell now
            // lives in this cell's tooltip plus the section-header tooltip.
            //
            // Run-cost (Phase 3.4): a compact net-cost suffix + tooltip detail is added
            // ONLY for Career + KSC origin with a known launch cost. The cost is read
            // from candidateRunCostCache (computed on the ~1 Hz candidate refresh,
            // never here on the draw path); a cache miss or a not-applicable / unknown
            // cost leaves the cell exactly as before (no suffix, base tooltip).
            string wouldDeliverText = LogisticsDeliveryPresentation.FormatWouldDeliver(
                candidate.Analysis.ResourceDeliveryManifest,
                candidate.Analysis.InventoryDeliveryManifest);
            string wouldDeliverTip =
                "A sealed, valid Supply Run: what it would deliver to the destination per cycle. Create Route promotes it to a Paused route you can Send Once / Activate.";
            if (candidateRunCostCache.TryGetValue(treeId, out RouteRunCostCalculator.RouteRunCost candCost)
                && candCost.Applicable && candCost.CostKnown)
            {
                wouldDeliverText += LogisticsCostPresentation.FormatCandidateSuffix(candCost);
                wouldDeliverTip = LogisticsCostPresentation.FormatDetailLine(candCost)
                    + "\n" + LogisticsCostPresentation.FormatDetailTooltip(candCost);
            }
            GUILayout.Label(
                new GUIContent(wouldDeliverText, wouldDeliverTip),
                GUILayout.Width(ColW_WouldDeliver));
            // L3: Transit cell (the candidate's natural run duration) now has its own
            // column in the candidate header, so it draws a real value instead of riding
            // a placeholder tooltip.
            GUILayout.Label(FormatDuration(CandidateTransit(candidate)), GUILayout.Width(ColW_CandidateTransit));

            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Actions));
            if (GUILayout.Button(new GUIContent("Create Route",
                    "Promote this Supply Run to a stored route (created Paused; use Send Once to test, then Activate)."),
                    GUILayout.Width(100)))
                pendingCreate = candidate;
            // M6 candidate intent helper: hide a tree the player never intends
            // as a route. Deferred through pendingDismissTreeId (applied in
            // ApplyPendingActions); always reversible from the Dismissed
            // subsection at the bottom of this section.
            if (GUILayout.Button(new GUIContent("Dismiss",
                    "Hide this tree from the Candidates section (it is not meant to become a route). Restore it any time from the Dismissed list below."),
                    GUILayout.Width(70)))
            {
                pendingDismissTreeId = candidate.Tree?.Id;
                pendingDismissLabel = name;
            }
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

            // M2: rename affordance as the panel header. Renaming writes Route.Name
            // (already persisted), so no schema change.
            DrawRouteRenameRow(route);

            string deliv = FormatRouteDelivery(route);
            DetailLine($"Delivers per cycle: {deliv}");
            DetailLine($"Status: {route.Status} - {StatusReason(route.Status)}");

            // M6 hold reasons: the last recorded hold in player language, with
            // how long ago it was checked. Conditioned ONLY on the cached
            // leg.HoldText (never on live route.* state combined with it) so
            // the IMGUI control count stays stable across Layout/Repaint while
            // the ~1 Hz cache and the live route drift apart.
            if (leg.HoldText != null)
                DetailLine(leg.HoldText, statusStyleYellow);

            // Last-partial-delivery report: what a mid-transit capacity shrink
            // actually cost (the undelivered remainder is lost). Same
            // cached-field-only conditioning as HoldText so the IMGUI control
            // count stays stable across Layout/Repaint.
            if (leg.PartialText != null)
                DetailLine(leg.PartialText, statusStyleYellow);

            // M5: one-line ownership note whenever the route binds a tree (it always
            // does for a live route). Tells the player that creating this route
            // disabled any manual loop on its source tree, mirroring the toast shown
            // once at create time. Resolved from CommittedTrees by id (cheap, drawn
            // only on expand).
            DrawRouteOwnsTreeNote(route);

            // M4c: round-trip pairing note, shown only when this route is linked.
            // Resolves the partner's display name from the store (cheap, drawn only on
            // expand).
            DrawRouteLinkNote(route);

            // M4: for a DestinationFull route, the live free-capacity context line
            // ("Munar Station tanks full: 0.0 of 150.0 LiquidFuel free") so the player
            // can tell full tanks apart from a misrouted delivery. Computed in the
            // ~1 Hz legibility cache (CapacityContext), so this just reads the string.
            if (route.Status == RouteStatus.DestinationFull && !string.IsNullOrEmpty(leg.CapacityContext))
                DetailLine(leg.CapacityContext, statusStyleYellow);

            // M4: for a recoverable surface EndpointLost route, a "Re-scan for endpoint"
            // button; otherwise a disabled-with-explanation note (an orbital endpoint
            // can only be matched by its baked PID, so re-scan cannot recover it).
            if (route.Status == RouteStatus.EndpointLost)
                DrawEndpointRescan(route);

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

            // M6 per-cycle flow: what each recent completed cycle debited where,
            // picked up where, and delivered where, newest first, bounded to the
            // last LogisticsFlowPresentation.MaxCyclesShown cycles. Read straight
            // from the ~1 Hz cache (built in the SAME ELS walk as the H2 summary);
            // conditioned ONLY on the cached list so the IMGUI control count stays
            // stable across Layout/Repaint. A route with no completed cycles has a
            // null list and renders nothing (no empty header). Shortfall cycles
            // tint yellow, matching the H2 realized-delivery line.
            if (leg.FlowLines != null && leg.FlowLines.Count > 0)
            {
                DetailLine(LogisticsFlowPresentation.RecentCyclesHeader);
                for (int i = 0; i < leg.FlowLines.Count; i++)
                {
                    LogisticsFlowPresentation.CycleFlowLine flowLine = leg.FlowLines[i];
                    DetailLine("  " + flowLine.Text,
                        flowLine.Shortfall ? statusStyleYellow : detailStyle);
                }
            }

            // M5 (D8): the basis label rides the Interval line for a windowed
            // route ("Interval: 14d (Duna transfer)"); flat routes unchanged.
            string basisSuffix = string.IsNullOrEmpty(leg.BasisLabel) ? string.Empty : " " + leg.BasisLabel;
            DetailLine($"Interval: {FormatDuration(route.DispatchInterval)}{basisSuffix}   Transit: {FormatDuration(route.TransitDuration)}   Cycles: {route.CompletedCycles}");

            // Run-cost (Phase 3, decision D3): one detail line + tooltip, drawn ONLY
            // when the cost applies (Career + KSC origin) AND is known (the source
            // snapshot resolved a launch cost > 0). Outside that, draw NOTHING (no
            // "n/a", no "0 funds", gotcha G7). The line + tooltip text are shaped by
            // the pure LogisticsCostPresentation; this path only draws.
            if (leg.RunCost.Applicable && leg.RunCost.CostKnown)
            {
                DetailLine(new GUIContent(
                    LogisticsCostPresentation.FormatDetailLine(leg.RunCost),
                    LogisticsCostPresentation.FormatDetailTooltip(leg.RunCost)));
            }

            DrawCadenceStepper(route, leg);
            DrawPriorityStepper(route);

            // H5: resolved recording / tree (mission) names instead of 8-char GUID
            // fragments; the short id moves to the hover tooltip.
            DrawSourceRecordingsLine(route);
            GUILayout.EndVertical();
        }

        /// <summary>
        /// Draws the M5 detail-panel ownership note: "This route owns tree '...';
        /// manual looping is disabled while it exists." for the route's source tree.
        /// The tree name is resolved from <see cref="RecordingStore.CommittedTrees"/>
        /// by the route's first source-ref tree id (or the backing-mission tree id),
        /// falling back to the short tree id when the name cannot resolve, so the note
        /// is never blank. Drawn only when the route resolves a source tree (skipped on
        /// a degenerate route with no tree). Cheap (a by-id store lookup) and drawn
        /// only on expand, mirroring the H5 source-recordings line.
        /// </summary>
        private void DrawRouteOwnsTreeNote(Route route)
        {
            string treeId = ResolveRouteSourceTreeId(route);
            if (string.IsNullOrEmpty(treeId))
                return;
            string treeName = ResolveTreeDisplayName(treeId);
            DetailLine(LogisticsCreatePresentation.FormatRouteOwnsTreeNote(treeName));
        }

        /// <summary>
        /// Draws the M4c round-trip pairing note when <paramref name="route"/> is
        /// linked to a partner. Resolves the partner's display name via
        /// <see cref="RouteStore.TryGetRoute"/> (falling back to the short id when the
        /// partner cannot resolve, e.g. it was just deleted and the dangling link not
        /// yet swept), then renders the pure
        /// <see cref="LogisticsLinkPresentation.FormatLinkedNote"/> line. Drawn only on
        /// expand, so the by-id store lookup is off the per-frame hot path.
        /// </summary>
        private void DrawRouteLinkNote(Route route)
        {
            if (route == null || string.IsNullOrEmpty(route.LinkedRouteId))
                return;
            string partnerName =
                RouteStore.TryGetRoute(route.LinkedRouteId, out Route partner)
                    && partner != null && !string.IsNullOrEmpty(partner.Name)
                ? partner.Name
                : ShortId(route.LinkedRouteId);
            DetailLine(LogisticsLinkPresentation.FormatLinkedNote(partnerName));
        }

        /// <summary>
        /// Arms the M4c round-trip link picker for <paramref name="source"/> (the
        /// route whose "Link round-trip..." button was clicked). Sets the persistent
        /// popup state read by <see cref="DrawLinkPicker"/>; the popup is drawn from
        /// <see cref="DrawIfOpen"/> after the main window. Does NOT itself mutate the
        /// store — the partner is chosen + committed in the popup.
        /// </summary>
        private void OpenLinkPicker(Route source, Vector2 mousePos)
        {
            if (source == null || string.IsNullOrEmpty(source.Id))
                return;
            linkPickerOpen = true;
            linkPickerSourceRouteId = source.Id;
            linkPickerSourceName = source.Name ?? "<unnamed>";
            linkPickerSelectedId = null;
            linkPickerPosition = mousePos;
            linkPickerRect = new Rect(0, 0, 0, 0);
            linkPickerResizing = false;
            linkPickerScroll = Vector2.zero;
            ParsekLog.Verbose("UI", $"Logistics: link picker opened source={ShortId(source.Id)}");
        }

        /// <summary>
        /// Draws the M4c round-trip link picker as its own window on top of the
        /// logistics window when armed (mirrors <c>GroupPickerUI.Draw</c>: a separate
        /// <see cref="ClickThruBlocker.GUILayoutWindow"/> rather than a nested one).
        /// Synchronous — the Link button commits through <see cref="RouteStore.LinkRoutes"/>
        /// inside the draw pass, so the deferred-field trap does not apply.
        /// </summary>
        private void DrawLinkPicker()
        {
            if (!linkPickerOpen) return;

            ParsekUI.HandleResizeDrag(ref linkPickerRect, ref linkPickerResizing,
                LinkPickerMinW, LinkPickerMinH, null);

            if (linkPickerRect.width < 1f)
            {
                linkPickerRect = new Rect(
                    Mathf.Clamp(linkPickerPosition.x, 0, Screen.width - 340f),
                    Mathf.Clamp(linkPickerPosition.y, 0, Screen.height - 380f),
                    340f, 380f);
            }

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                linkPickerRect = ClickThruBlocker.GUILayoutWindow(
                    "ParsekLogiLinkPicker".GetHashCode(),
                    linkPickerRect,
                    DrawLinkPickerContents,
                    "Link round-trip partner",
                    opaqueWindowStyle,
                    GUILayout.Width(linkPickerRect.width),
                    GUILayout.Height(linkPickerRect.height));
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
        }

        /// <summary>
        /// Window-body callback for the link picker: a single-select list of eligible
        /// partner routes (<see cref="LogisticsLinkPresentation.BuildLinkCandidates"/>:
        /// other routes that are not already linked) plus Link / Cancel. Link commits
        /// <see cref="RouteStore.LinkRoutes"/> directly and dirties the legibility
        /// cache so both routes' rows refresh; Cancel just closes. Both close the popup.
        /// </summary>
        private void DrawLinkPickerContents(int windowID)
        {
            EnsureStyles();
            GUILayout.Label($"Link '{linkPickerSourceName}' with:", detailStyle);
            GUILayout.Space(3);

            List<LogisticsLinkPresentation.LinkCandidate> candidates =
                LogisticsLinkPresentation.BuildLinkCandidates(RouteStore.CommittedRoutes, linkPickerSourceRouteId);

            linkPickerScroll = GUILayout.BeginScrollView(linkPickerScroll, GUILayout.ExpandHeight(true));
            if (candidates.Count == 0)
            {
                GUILayout.Label(
                    "No eligible routes. A partner must be another route that is not already linked.",
                    detailStyle);
            }
            else
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    LogisticsLinkPresentation.LinkCandidate c = candidates[i];
                    bool selected = string.Equals(linkPickerSelectedId, c.Id, System.StringComparison.Ordinal);
                    bool now = GUILayout.Toggle(selected, "  " + c.Name);
                    if (now && !selected)
                        linkPickerSelectedId = c.Id;
                    else if (!now && selected)
                        linkPickerSelectedId = null;
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Space(3);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            bool prevEnabled = GUI.enabled;
            GUI.enabled = !string.IsNullOrEmpty(linkPickerSelectedId);
            if (GUILayout.Button(new GUIContent("Link",
                    "Pair these two routes as a round-trip: they alternate, each dispatching only after its partner completes a run."),
                    GUILayout.Width(70)))
            {
                string src = linkPickerSourceRouteId;
                string sel = linkPickerSelectedId;
                if (RouteStore.LinkRoutes(src, sel))
                {
                    ParsekLog.Info("UI",
                        $"Logistics: round-trip linked source={ShortId(src)} partner={ShortId(sel)} (via picker)");
                    lastLegibilityComputeRealtime = -1f;
                }
                linkPickerOpen = false;
            }
            GUI.enabled = prevEnabled;

            if (GUILayout.Button("Cancel", GUILayout.Width(70)))
            {
                ParsekLog.Verbose("UI", $"Logistics: link picker cancelled source={ShortId(linkPickerSourceRouteId)}");
                linkPickerOpen = false;
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            ParsekUI.DrawResizeHandle(linkPickerRect, ref linkPickerResizing, null);
            GUI.DragWindow();
        }

        /// <summary>
        /// Draws the M4 EndpointLost re-scan affordance. For a recoverable surface
        /// endpoint (<see cref="LogisticsDeliveryPresentation.ShouldOfferEndpointRescan"/>)
        /// it renders a "Re-scan for endpoint" button; otherwise a disabled-with-note
        /// label explaining why re-scan cannot help (orbital endpoints can only be
        /// matched by the baked PID). The button does its work DIRECTLY in the click
        /// branch (this is the synchronous draw path, not an async dialog callback, so
        /// the QW2 frame-reset-field trap does not apply and an inline action is
        /// correct): it calls <see cref="RouteEndpointResolver.TryResolveEndpoint"/>,
        /// logs the outcome, and on success clears
        /// <see cref="Route.NextEligibilityCheckUT"/> so the orchestrator re-attempts
        /// the cycle on the next tick (instead of waiting out the 30s retry interval),
        /// then dirties the legibility cache so the destination cell + capacity context
        /// refresh next frame. It does NOT flip the route status: the orchestrator's
        /// dispatch evaluator re-resolves the endpoint every retry tick and recovers
        /// the status itself. A re-scan miss just re-logs and leaves the route
        /// EndpointLost (the orchestrator keeps retrying on its own clock).
        /// </summary>
        private void DrawEndpointRescan(Route route)
        {
            RouteStop stop = route?.Stops != null && route.Stops.Count > 0 ? route.Stops[0] : null;
            if (stop == null)
                return;
            RouteEndpoint endpoint = stop.Endpoint;

            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);

            if (LogisticsDeliveryPresentation.ShouldOfferEndpointRescan(route.Status, endpoint))
            {
                if (GUILayout.Button(new GUIContent("Re-scan for endpoint",
                        "Search the destination body for a surface vessel near the recorded endpoint, then retry delivery immediately if found."),
                        GUILayout.Width(180f)))
                {
                    bool resolved = RouteEndpointResolver.TryResolveEndpoint(
                        endpoint, out Vessel v, out string reason);
                    if (resolved && v != null)
                    {
                        // Clear the retry rate-limit so the orchestrator re-resolves and
                        // recovers the status on the very next tick (it owns the status
                        // flip; we never set Active here).
                        route.NextEligibilityCheckUT = null;
                        ParsekLog.Info("UI",
                            $"Logistics: endpoint re-scan route={ShortId(route.Id)} resolved=true name='{v.vesselName}' (cleared retry gate)");
                    }
                    else
                    {
                        ParsekLog.Info("UI",
                            $"Logistics: endpoint re-scan route={ShortId(route.Id)} resolved=false reason='{reason}' (still EndpointLost)");
                    }
                    // Refresh the destination cell + capacity context next frame.
                    lastLegibilityComputeRealtime = -1f;
                }
            }
            else
            {
                bool prevEnabled = GUI.enabled;
                GUI.enabled = false;
                GUILayout.Button(new GUIContent("Re-scan for endpoint",
                    LogisticsDeliveryPresentation.RescanIneligibleReason(endpoint)),
                    GUILayout.Width(180f));
                GUI.enabled = prevEnabled;
                GUILayout.Label(LogisticsDeliveryPresentation.RescanIneligibleReason(endpoint),
                    detailStyle, GUILayout.ExpandWidth(true));
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Resolves the route's source tree id for the M5 ownership note: the first
        /// non-empty <c>SourceRefs[].TreeId</c>, falling back to
        /// <see cref="Route.BackingMissionTreeId"/>. Returns null when neither is set.
        /// </summary>
        private static string ResolveRouteSourceTreeId(Route route)
        {
            if (route == null)
                return null;
            if (route.SourceRefs != null)
            {
                for (int s = 0; s < route.SourceRefs.Count; s++)
                {
                    RouteSourceRef sref = route.SourceRefs[s];
                    if (sref != null && !string.IsNullOrEmpty(sref.TreeId))
                        return sref.TreeId;
                }
            }
            return string.IsNullOrEmpty(route.BackingMissionTreeId) ? null : route.BackingMissionTreeId;
        }

        /// <summary>
        /// Resolves a tree's player-visible display name from
        /// <see cref="RecordingStore.CommittedTrees"/> by id (M5). Falls back to the
        /// short tree id, then "<unknown tree>", so the ownership note / toast never
        /// shows a null name. Reads CommittedTrees (NOT the grep-gated
        /// CommittedRecordings list), so it stays off the ERS/ELS gate.
        /// </summary>
        private static string ResolveTreeDisplayName(string treeId)
        {
            if (string.IsNullOrEmpty(treeId))
                return "<unknown tree>";
            var trees = RecordingStore.CommittedTrees;
            if (trees != null)
            {
                for (int i = 0; i < trees.Count; i++)
                {
                    RecordingTree t = trees[i];
                    if (t != null && string.Equals(t.Id, treeId, System.StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrEmpty(t.TreeName))
                            return t.TreeName;
                        break;
                    }
                }
            }
            return ShortId(treeId);
        }

        /// <summary>
        /// Draws the M2 detail-panel rename row: a "Name:" label plus either a
        /// "Rename" button (when not editing this route) or a deferred-commit
        /// TextField (when editing), ported from the RecordingsTableUI rename idiom.
        /// Editing is keyed by <see cref="Route.Id"/> (NOT a row index) because routes
        /// are re-sectioned / added / removed between frames. Commit lands on Enter
        /// (here) or click-outside (<see cref="HandleLogisticsDefocus"/>); Escape
        /// cancels. The committed name immediately shows in the row because
        /// <see cref="DrawRouteRow"/> reads <see cref="Route.Name"/> live.
        /// </summary>
        private void DrawRouteRenameRow(Route route)
        {
            const string controlName = "LogiRouteRename";
            bool editingThis = string.Equals(renamingRouteId, route.Id, System.StringComparison.Ordinal);

            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label("Name:", detailStyle, GUILayout.Width(46f));

            if (!editingThis)
            {
                GUILayout.Label(route.Name ?? "<unnamed>", detailStyle, GUILayout.ExpandWidth(true));
                if (GUILayout.Button(new GUIContent("Rename", "Edit this route's name"), GUILayout.Width(RouteDetailButtonWidth)))
                {
                    // Editing the interval and the name at once would cross-wire the
                    // two deferred commits; the interval edit-start already suppresses
                    // itself while a rename is active, so clear any pending interval
                    // edit before arming the rename.
                    ClearIntervalEdit();
                    renamingRouteId = route.Id;
                    renamingRouteText = route.Name ?? string.Empty;
                    renamingRouteFocused = false;
                    renamingRouteRect = default;
                    ParsekLog.Verbose("UI",
                        $"Logistics: rename started route={ShortId(route.Id)} current='{route.Name}'");
                }

                // Two log buttons next to Rename: the route's own step log, and the source
                // mission's step log (identical to the Missions-tab Log for that tree).
                if (GUILayout.Button(new GUIContent("Log (Route)",
                        "Step-by-step log of this route: origin, dock, delivery, undock"),
                        GUILayout.Width(RouteDetailButtonWidth)))
                {
                    ParsekLog.Info("UI",
                        $"Route Log (Route) button: route={(string.IsNullOrEmpty(route.Id) ? "<null>" : route.Id)} name='{route.Name ?? ""}'");
                    parentUI.OpenStructureWindowForRoute(route.Id, route.Name);
                }

                string sourceTreeId = ResolveRouteSourceTreeId(route);
                GUI.enabled = !string.IsNullOrEmpty(sourceTreeId);
                if (GUILayout.Button(new GUIContent("Log (Mission)",
                        "Step-by-step log of the source mission this route was built from"),
                        GUILayout.Width(RouteDetailButtonWidth)))
                {
                    ParsekLog.Info("UI",
                        $"Route Log (Mission) button: route={(string.IsNullOrEmpty(route.Id) ? "<null>" : route.Id)} tree={sourceTreeId ?? "<null>"}");
                    parentUI.OpenStructureWindowForMission(sourceTreeId, ResolveTreeDisplayName(sourceTreeId));
                }
                GUI.enabled = true;

                // M4c round-trip link control (the missing C1 deliverable). When the
                // route is unlinked, a "Link round-trip..." button arms the partner
                // picker; when linked, an "Unlink" button breaks the pair inline. The
                // unlink runs DIRECTLY in the click branch (synchronous draw path, like
                // the EndpointRescan inline action), so the QW2 async-callback trap does
                // not apply; the link goes through the picker (also synchronous). After
                // either, dirty the legibility cache so the row/detail refresh next frame.
                if (string.IsNullOrEmpty(route.LinkedRouteId))
                {
                    if (GUILayout.Button(new GUIContent("Link round-trip...",
                            "Pair this route with another so they alternate: each dispatches only after its partner completes a run (a single reused transport flying out and back)."),
                            GUILayout.Width(RouteDetailButtonWidth)))
                    {
                        OpenLinkPicker(route, Event.current.mousePosition);
                    }
                }
                else
                {
                    if (GUILayout.Button(new GUIContent("Unlink",
                            "Break this route's round-trip pairing; both routes return to dispatching on their own schedule."),
                            GUILayout.Width(RouteDetailButtonWidth)))
                    {
                        ParsekLog.Info("UI",
                            $"Logistics: unlink button route={ShortId(route.Id)} partner={ShortId(route.LinkedRouteId)}");
                        RouteStore.UnlinkRoute(route.Id);
                        lastLegibilityComputeRealtime = -1f;
                    }
                }
            }
            else
            {
                bool submit = Event.current.type == EventType.KeyDown
                    && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
                bool cancel = Event.current.type == EventType.KeyDown
                    && Event.current.keyCode == KeyCode.Escape;

                GUI.SetNextControlName(controlName);
                renamingRouteText = GUILayout.TextField(renamingRouteText ?? string.Empty, GUILayout.ExpandWidth(true));
                renamingRouteRect = GUILayoutUtility.GetLastRect();

                if (!renamingRouteFocused)
                {
                    GUI.FocusControl(controlName);
                    renamingRouteFocused = true;
                }

                if (submit)
                {
                    CommitRouteRename(route);
                    Event.current.Use();
                }
                else if (cancel)
                {
                    ParsekLog.Verbose("UI",
                        $"Logistics: rename cancelled route={ShortId(route.Id)}");
                    ClearRouteRename();
                    Event.current.Use();
                }
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Commits the M2 rename for <paramref name="route"/>: routes the typed text
        /// through the pure <see cref="LogisticsRenamePresentation.ComputeRouteRename"/>
        /// (trim + empty-guard + unchanged-guard), and on a real change writes
        /// <see cref="Route.Name"/> directly and Info-logs the from/to. Empty /
        /// whitespace / unchanged input leaves the name untouched. Always clears the
        /// rename edit state. Tolerates a null route (the field id resolved to nothing)
        /// by just clearing.
        /// </summary>
        private void CommitRouteRename(Route route)
        {
            if (route == null)
            {
                ClearRouteRename();
                return;
            }

            if (LogisticsRenamePresentation.ComputeRouteRename(route.Name, renamingRouteText, out string committed))
            {
                ParsekLog.Info("UI",
                    $"Logistics: route renamed from '{route.Name}' to '{committed}' (route={ShortId(route.Id)})");
                route.Name = committed;
                // Dirty the legibility cache so a sort-by-Name re-sorts immediately on
                // rename rather than lagging until the next ~1 Hz refresh (mirrors
                // CommitIntervalEdit's invalidation for a sort-by-Interval change).
                lastLegibilityComputeRealtime = -1f;
            }
            else
            {
                ParsekLog.Verbose("UI",
                    $"Logistics: rename no-op route={ShortId(route.Id)} typed='{renamingRouteText}' (empty/whitespace/unchanged)");
            }
            ClearRouteRename();
        }

        private void ClearRouteRename()
        {
            renamingRouteId = null;
            renamingRouteText = null;
            renamingRouteFocused = false;
            renamingRouteRect = default;
            GUIUtility.keyboardControl = 0;
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
        private void DrawCadenceStepper(Route route, RouteLegibility leg)
        {
            int n = Route.ClampCadenceMultiplier(route.CadenceMultiplier);
            // M5 (D8/OQ2): a windowed basis swaps the readout to per-window
            // wording ("2x (every 2nd window)") - the interval arithmetic is
            // actively misleading on synodic spacing. Flat routes unchanged.
            bool windowed = RouteWindowBasisPresentation.IsWindowedBasis(leg.Basis);

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
                    atFloor ? "Already at the minimum (1x = the fastest the run allows)" : "Dispatch more often"),
                    GUILayout.Width(24f)))
            {
                pendingCadenceRoute = route;
                pendingCadenceMultiplier = RouteCadence.StepMultiplier(n, -1);
            }
            GUI.enabled = true;

            // Current N x + the resulting human cadence (windowed wording for a
            // windowed basis; wider cell to fit "2x (every 2nd window)").
            GUILayout.Label(
                windowed ? RouteWindowBasisPresentation.FormatWindowedCadence(n) : FormatCadence(route),
                detailStyle, GUILayout.Width(windowed ? 170f : 110f));

            if (GUILayout.Button(new GUIContent("+", "Dispatch less often"), GUILayout.Width(24f)))
            {
                pendingCadenceRoute = route;
                pendingCadenceMultiplier = RouteCadence.StepMultiplier(n, +1);
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // Priority stepper (M1 dispatch priority): "- N +" where N is the route's
        // dispatch priority (>= 0). Lower values dispatch first when several routes
        // contend in the same orchestrator tick; 0 is the floor (and the default).
        // Records the edit into the deferred-mutation fields; ApplyPendingActions
        // commits it via RoutePriority.Apply.
        private void DrawPriorityStepper(Route route)
        {
            int p = Route.ClampPriority(route.DispatchPriority);

            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label(
                new GUIContent("Priority:",
                    "Lower priority number dispatches first when several routes contend in the same tick. 0 is the highest priority (and the default)."),
                detailStyle, GUILayout.Width(70f));

            // "-" decrements (no-op + greyed at the 0 floor).
            bool atFloor = p <= 0;
            GUI.enabled = !atFloor;
            if (GUILayout.Button(new GUIContent("-",
                    atFloor ? "Already at the highest priority (0)" : "Dispatch earlier on contention"),
                    GUILayout.Width(24f)))
            {
                pendingPriorityRoute = route;
                pendingPriorityValue = RoutePriority.Step(p, -1);
            }
            GUI.enabled = true;

            GUILayout.Label(p.ToString(CultureInfo.InvariantCulture), detailStyle, GUILayout.Width(110f));

            if (GUILayout.Button(new GUIContent("+", "Dispatch later on contention"), GUILayout.Width(24f)))
            {
                pendingPriorityRoute = route;
                pendingPriorityValue = RoutePriority.Step(p, +1);
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

        // Detail line carrying a hover tooltip (GUIContent). Same indented full-width
        // layout as the string overloads; used by the run-cost line so the net =
        // launch - recovered explanation + D1 caveat surface on hover.
        private void DetailLine(GUIContent content)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label(content, detailStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        private void DrawSectionHeader(string text)
        {
            // Use the shared house section-header bar (bold label in a box, full-width)
            // so Logistics headers match Settings / Recordings / Timeline / Missions,
            // but CENTER the text via a local clone so the shared (left-aligned) style
            // those other windows use is not changed. Built once and reused.
            if (sectionHeaderCenteredStyle == null)
            {
                sectionHeaderCenteredStyle = new GUIStyle(parentUI.GetSectionHeaderStyle())
                {
                    alignment = TextAnchor.MiddleCenter
                };
            }
            GUILayout.Space(SpacingSmall);
            // Nest the header label inside a skin box so the section-subtitle cell carries
            // the SAME dark "box-on-box" background the column-header row has. The column
            // header draws its box-styled cells INSIDE a BeginVertical(GUI.skin.box)
            // container (two box layers, reading as a solid dark bar); a bare full-width
            // box-label sits on only the window background (one layer) and looks lighter.
            // Wrapping the label in a box horizontal adds the second box layer so the
            // subtitle matches the table-header shade.
            GUILayout.BeginHorizontal(GUI.skin.box);
            GUILayout.Label(text, sectionHeaderCenteredStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }
        private GUIStyle sectionHeaderCenteredStyle;

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
            // These four mutate route state HERE (status / cadence), which changes the
            // legibility cache inputs (H1 countdown, H3 badge). Force a recompute next
            // frame so a player action refreshes the cells immediately instead of
            // waiting out the ~1s timer. Create and Delete are deliberately NOT listed:
            // in this method they only SPAWN their confirm dialogs, and the actual
            // mutation (plus its cache invalidation) happens later in the dialog
            // callbacks (Create dirties both caches on a successful build; a Delete just
            // removes the route, whose stale cache entry is never drawn again). Listing
            // them here would force a wasted full recompute on every dialog open / Cancel.
            bool routeStateMutated =
                pendingPause != null || pendingActivate != null || pendingSendOnce != null
                || pendingCadenceRoute != null;

            if (pendingPause != null)
            {
                bool ok = RouteOrchestrator.TryPause(pendingPause);
                // An explicit Pause overrides any prior Send-Once provenance (both leave
                // PauseAfterCurrentCycle set): drop the id so a Send-Once-then-Pause
                // armed cycle reads "Pausing after this cycle..." not "Sending one cycle...".
                if (ok && !string.IsNullOrEmpty(pendingPause.Id))
                    sendOnceArmedRouteIds.Remove(pendingPause.Id);
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
                // Record the Send-Once provenance (M6) so the armed-button label stays
                // "Sending one cycle..." even after the cycle dispatches to InTransit.
                if (ok && !string.IsNullOrEmpty(pendingSendOnce.Id))
                    sendOnceArmedRouteIds.Add(pendingSendOnce.Id);
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
                // Spawns the informed confirm once on the click frame; the actual
                // RouteBuilder.BuildRoute (+ TryActivate on "Create and Activate")
                // runs in the dialog button callbacks (see
                // SpawnCreateRouteConfirmation), never here, so a route is created
                // only on an explicit confirm and the candidate / legibility caches
                // are dirtied in-callback once the route actually exists.
                SpawnCreateRouteConfirmation(pendingCreate);
            }
            if (pendingCadenceRoute != null)
            {
                bool changed = RouteCadence.ApplyMultiplier(pendingCadenceRoute, pendingCadenceMultiplier);
                ParsekLog.Info("UI",
                    $"Logistics: Cadence route={ShortId(pendingCadenceRoute.Id)} N={pendingCadenceMultiplier} " +
                    $"result={(changed ? "applied" : "unchanged")}");
            }
            if (pendingPriorityRoute != null)
            {
                // Deliberately NOT in routeStateMutated: priority feeds only the
                // orchestrator's per-tick processing order, never a legibility-cache
                // input (no countdown / badge / cost change), so forcing a recompute
                // here would be wasted work.
                bool changed = RoutePriority.Apply(pendingPriorityRoute, pendingPriorityValue);
                ParsekLog.Info("UI",
                    $"Logistics: Priority route={ShortId(pendingPriorityRoute.Id)} value={pendingPriorityValue} " +
                    $"result={(changed ? "applied" : "unchanged")}");
            }
            if (pendingDismissTreeId != null)
            {
                // M6 candidate intent helper: pure UI-intent mutation (no route /
                // ledger effect, always reversible). RouteStore logs the
                // authoritative Info line with tree id + display name. On success
                // both derivation caches are dirtied so the tree vanishes from the
                // candidates AND near-miss lists this second, not after ~1s.
                bool ok = RouteStore.DismissCandidateTree(pendingDismissTreeId, pendingDismissLabel);
                ParsekLog.Info("UI",
                    $"Logistics: Dismiss candidate tree={ShortId(pendingDismissTreeId)} " +
                    $"name='{pendingDismissLabel}' result={(ok ? "dismissed" : "no-op")}");
                if (ok)
                {
                    lastCandidateComputeRealtime = -1f;
                    lastNearMissComputeRealtime = -1f;
                }
            }
            if (pendingRestoreTreeId != null)
            {
                bool ok = RouteStore.RestoreCandidateTree(pendingRestoreTreeId, pendingRestoreLabel);
                ParsekLog.Info("UI",
                    $"Logistics: Restore candidate tree={ShortId(pendingRestoreTreeId)} " +
                    $"name='{pendingRestoreLabel}' result={(ok ? "restored" : "no-op")}");
                if (ok)
                {
                    lastCandidateComputeRealtime = -1f;
                    lastNearMissComputeRealtime = -1f;
                }
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
            pendingPriorityRoute = null;
            pendingPriorityValue = 0;
            pendingDismissTreeId = null;
            pendingDismissLabel = null;
            pendingRestoreTreeId = null;
            pendingRestoreLabel = null;
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
        /// Spawns the informed "Create Supply Route?" confirm for a candidate: a
        /// window-owned <see cref="MultiOptionDialog"/> rendering
        /// <see cref="RouteCreationFormatters.BuildSummaryBlock"/> (the SAME summary
        /// the post-commit auto-dialog shows) with three buttons: "Create Paused"
        /// (the existing window-create behavior), "Create and Activate" (build then
        /// <see cref="RouteOrchestrator.TryActivate"/>), and "Cancel". Mirrors
        /// <see cref="SpawnDeleteRouteConfirmation"/>: the candidate is captured into
        /// a LOCAL so the closures do not depend on the <c>pendingCreate</c> instance
        /// field that <see cref="ApplyPendingActions"/> nulls this frame, and each
        /// create callback runs the build DIRECTLY in the closure (the
        /// dialog-callback fires asynchronously, outside the draw pass, so setting a
        /// frame-reset deferred field would be silently clobbered before
        /// ApplyPendingActions reads it). After a build the callback dirties both
        /// the candidate cache (the promoted run leaves the Candidates list) and the
        /// legibility cache (the new route's cells appear immediately). This does NOT
        /// touch <see cref="RouteCreationDialog"/>: that post-commit auto-dialog is
        /// unchanged.
        /// </summary>
        private void SpawnCreateRouteConfirmation(RouteCandidate candidate)
        {
            if (candidate?.Analysis == null || candidate.Tree == null)
            {
                ParsekLog.Warn("UI", "Logistics: Create Route confirm - null candidate/analysis/tree, ignored");
                return;
            }

            // Capture into locals so the (asynchronous) button closures do not depend
            // on a mutable instance field that ApplyPendingActions nulls this frame.
            RouteCandidate cand = candidate;
            Game.Modes mode = HighLogic.CurrentGame != null
                ? HighLogic.CurrentGame.Mode
                : Game.Modes.SANDBOX;
            // Same summary call the post-commit auto-dialog makes (analysis + mode +
            // tree); passing cand.Tree makes the Transit line resolve the real
            // [root..dock] span so the dialog matches the route that gets built.
            // The run-cost block (Career + KSC origin) is computed here from the
            // candidate's source recording + tree (no Route exists yet) and passed in.
            RouteRunCostCalculator.RouteRunCost runCost =
                ComputeCandidateRunCost(cand.Analysis, cand.Tree);
            string body = RouteCreationFormatters.BuildSummaryBlock(cand.Analysis, mode, cand.Tree, runCost);

            ParsekLog.Info("UI",
                $"Logistics: Create Route confirm dialog spawned tree={ShortId(cand.Tree.Id)} mode={mode}");

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekLogisticsCreateRouteConfirm",
                    body,
                    "Create Supply Route?",
                    HighLogic.UISkin,
                    new DialogGUIButton("Create Paused", () =>
                        HandleCreateRouteChoice(cand, LogisticsCreatePresentation.CreateRouteChoice.CreatePaused)),
                    new DialogGUIButton("Create and Activate", () =>
                        HandleCreateRouteChoice(cand, LogisticsCreatePresentation.CreateRouteChoice.CreateAndActivate)),
                    new DialogGUIButton("Cancel", () =>
                        HandleCreateRouteChoice(cand, LogisticsCreatePresentation.CreateRouteChoice.Cancel))),
                false, HighLogic.UISkin);
        }

        /// <summary>
        /// Runs the in-callback work for a Create Route confirm button. The pure
        /// <see cref="LogisticsCreatePresentation"/> decision drives the branch:
        /// <see cref="LogisticsCreatePresentation.ShouldBuild"/> gates the build and
        /// <see cref="LogisticsCreatePresentation.ShouldActivate"/> gates the
        /// post-build <see cref="RouteOrchestrator.TryActivate"/>, so the button ->
        /// effect mapping is unit-tested off the IMGUI path. The build runs DIRECTLY
        /// here (this is a dialog-button callback, fired asynchronously outside the
        /// draw pass; the SpawnDeleteRouteConfirmation precedent) so a frame-reset
        /// deferred field would be clobbered at frame top before ApplyPendingActions
        /// reads it. After a build both caches are dirtied so the promoted run leaves
        /// the Candidates list and the new route's cells appear immediately.
        /// </summary>
        private void HandleCreateRouteChoice(RouteCandidate cand, LogisticsCreatePresentation.CreateRouteChoice choice)
        {
            if (!LogisticsCreatePresentation.ShouldBuild(choice))
            {
                ParsekLog.Verbose("UI",
                    $"Logistics: Create Route cancelled tree={ShortId(cand?.Tree?.Id)}");
                return;
            }

            Route r = CreateRouteFromCandidate(cand);
            bool activated = false;
            if (r != null && LogisticsCreatePresentation.ShouldActivate(choice))
                activated = RouteOrchestrator.TryActivate(r, TryGetCurrentUT());

            // Only dirty the caches when a route was actually created: the promoted run
            // then leaves the Candidates list and the new route's H1/H2/H3/H4 cells
            // appear immediately. On a builder reject (r == null) nothing changed, so a
            // forced recompute would be wasted work.
            if (r != null)
            {
                lastCandidateComputeRealtime = -1f;
                lastLegibilityComputeRealtime = -1f;
                // M6 Record-Supply-Run helper: the candidate was promoted to a
                // route, so a still-pending main-window banner for this tree is
                // stale - drop it.
                RouteRunPrompt.ClearPendingPromptIfTree(cand?.Tree?.Id, "route-created");
            }

            ParsekLog.Info("UI",
                $"Logistics: Create Route choice={choice} tree={ShortId(cand?.Tree?.Id)} result={(r != null ? "created" : "rejected")} activated={activated}");
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

        /// <summary>
        /// Builds (and stores) a Paused route from a candidate via the single
        /// <see cref="RouteBuilder.BuildRoute"/> funnel and returns the built
        /// <see cref="Route"/> (or null on a null candidate or a builder reject).
        /// The H6 confirm callbacks call this to build, then the "Create and
        /// Activate" branch additionally calls
        /// <see cref="RouteOrchestrator.TryActivate"/> on the returned non-null
        /// route; returning the route keeps a single AddRoute +
        /// manual-loop-clear funnel and guarantees the window-created route's
        /// interval / geometry stays identical to today.
        /// </summary>
        private Route CreateRouteFromCandidate(RouteCandidate candidate)
        {
            if (candidate?.Analysis == null || candidate.Tree == null)
            {
                ParsekLog.Warn("UI", "Logistics: Create Route - null candidate/analysis/tree, ignored");
                return null;
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
                int cleared = RouteTreeGuard.ForceClearManualLoopForRoute(outcome.Route, TryGetCurrentUT());
                // M5: a manual loop was actually turned off by this create, so tell the
                // player with a one-shot toast (and the always-visible detail note). The
                // toast fires ONLY when something was cleared, never on every create.
                // Done DIRECTLY here in the synchronous create path (no PopupDialog
                // callback / frame-reset field), so the QW2 trap does not apply.
                if (LogisticsCreatePresentation.ShouldToastManualLoopCleared(cleared))
                {
                    string treeName = ResolveTreeDisplayName(ResolveRouteSourceTreeId(outcome.Route));
                    string toast = LogisticsCreatePresentation.FormatManualLoopTurnedOffToast(treeName);
                    ParsekLog.ScreenMessage(toast, 5f);
                    ParsekLog.Info("UI",
                        $"Logistics: manual loop turned off by create route={ShortId(outcome.Route.Id)} tree='{treeName}' cleared={cleared.ToString(CultureInfo.InvariantCulture)} (toast posted)");
                }
                ParsekLog.Info("UI",
                    $"Logistics: Create Route from candidate tree={ShortId(candidate.Tree.Id)} -> route={ShortId(outcome.Route.Id)} name='{outcome.Route.Name}' (Paused, interval={interval.ToString("R", CultureInfo.InvariantCulture)}s)");
                return outcome.Route;
            }

            ParsekLog.Info("UI",
                $"Logistics: Create Route rejected tree={ShortId(candidate.Tree.Id)} reason={outcome.RejectReason ?? "<none>"}");
            return null;
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

                // Run-cost (Phase 3.4): recompute each candidate's net cost on this
                // ~1 Hz refresh and stash it by tree id so DrawCandidateRow reads it
                // off the draw path. Rebuilt wholesale each refresh (the candidate
                // set is small and can change), so stale tree ids never linger.
                candidateRunCostCache.Clear();
                int costed = 0;
                for (int i = 0; i < cachedCandidates.Count; i++)
                {
                    RouteCandidate cand = cachedCandidates[i];
                    string treeId = cand?.Tree?.Id;
                    if (cand?.Analysis == null || string.IsNullOrEmpty(treeId))
                        continue;
                    candidateRunCostCache[treeId] = ComputeCandidateRunCost(cand.Analysis, cand.Tree);
                    costed++;
                }
                ParsekLog.Verbose("UI",
                    $"Logistics candidate run-cost cache refreshed candidates={cachedCandidates.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"costed={costed.ToString(CultureInfo.InvariantCulture)}");

                // M6: rebuild the Dismissed-subsection display rows on the same
                // refresh (tree names resolved here, never on the draw path).
                // Sorted by label then id so the row order is stable across
                // HashSet iteration order changes.
                cachedDismissedRows.Clear();
                foreach (string dismissedId in RouteStore.DismissedCandidateTreeIds)
                    cachedDismissedRows.Add((dismissedId, ResolveTreeDisplayName(dismissedId)));
                cachedDismissedRows.Sort((a, b) =>
                {
                    int byLabel = string.CompareOrdinal(a.label, b.label);
                    return byLabel != 0 ? byLabel : string.CompareOrdinal(a.treeId, b.treeId);
                });
            }
            return cachedCandidates ?? new List<RouteCandidate>();
        }

        // M3: the throttled "recently committed trees not yet eligible" near-miss
        // list. Same ~1 Hz timer as GetCandidates (both derive from the same
        // committed trees through RouteAnalysisEngine), so the subsection never
        // scans live on the IMGUI draw path. Gate-safe: DeriveNearMisses reads only
        // RecordingTree.Recordings[].MergeState + RouteAnalysisEngine, no raw
        // committed-recording / ledger read.
        private List<RouteNearMiss> GetNearMisses()
        {
            float now = Time.realtimeSinceStartup;
            if (lastNearMissComputeRealtime < 0f
                || now - lastNearMissComputeRealtime >= CandidateRecomputeIntervalSeconds)
            {
                cachedNearMisses = RouteCandidateFinder.DeriveNearMisses();
                lastNearMissComputeRealtime = now;
            }
            return cachedNearMisses ?? new List<RouteNearMiss>();
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
            // M6: prune the Send-Once provenance set to routes that still exist and are
            // still armed, so a disarmed/deleted route never leaks a stale entry. Only
            // allocate when the set is non-empty (the common case is empty).
            bool pruneArmed = sendOnceArmedRouteIds.Count > 0;
            HashSet<string> stillArmed = pruneArmed ? new HashSet<string>() : null;
            int routeCount = routes?.Count ?? 0;
            int withCountdown = 0;
            int withDeliveries = 0;
            int withHold = 0;
            int withFlow = 0;
            for (int i = 0; i < routeCount; i++)
            {
                Route route = routes[i];
                if (route == null || string.IsNullOrEmpty(route.Id)) continue;
                if (pruneArmed && route.PauseAfterCurrentCycle)
                    stillArmed.Add(route.Id);

                RouteLegibility leg = ComputeRouteLegibility(route, currentUT);
                legibilityCache[route.Id] = leg;
                if (leg.CountdownBranch != LogisticsCountdownPresentation.CountdownBranch.None)
                    withCountdown++;
                if (leg.HasDeliveries)
                    withDeliveries++;
                // M6 hold reasons: batch counter, one summary line below.
                if (leg.HoldText != null)
                    withHold++;
                // M6 per-cycle flow: batch counter, same summary line.
                if (leg.FlowLines != null)
                    withFlow++;
            }
            if (pruneArmed)
                sendOnceArmedRouteIds.RemoveWhere(id => !stillArmed.Contains(id));

            ParsekLog.Verbose("UI",
                $"Logistics legibility cache refreshed routes={routeCount.ToString(CultureInfo.InvariantCulture)} " +
                $"withCountdown={withCountdown.ToString(CultureInfo.InvariantCulture)} " +
                $"withDeliveries={withDeliveries.ToString(CultureInfo.InvariantCulture)} " +
                $"withHold={withHold.ToString(CultureInfo.InvariantCulture)} " +
                $"withFlow={withFlow.ToString(CultureInfo.InvariantCulture)}");
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
            // M5 (D8): basis-aware next-dispatch-window countdown for the
            // windowed bases the flat helper refuses, plus the basis label. Both
            // accessors share the signature-cached LoopUnit resolve, so this
            // stays a ~1 Hz cost. A flat route yields hasWindow=false + a null
            // label - its branch selection and cells are byte-identical.
            bool hasWindow = RouteOrchestrator.TryComputeSecondsToNextDispatchWindow(
                route, currentUT, out double secondsToWindow,
                out RouteWindowBasis basis, out string targetBody);
            leg.Basis = basis;
            leg.BasisLabel = RouteWindowBasisPresentation.BasisLabel(basis, targetBody);
            LogisticsCountdownPresentation.CountdownDecision countdown =
                LogisticsCountdownPresentation.ResolveDetailCountdown(
                    route.Status, route.NextEligibilityCheckUT,
                    hasCrossing, secondsToCrossing,
                    hasWindow, secondsToWindow, currentUT);
            leg.CountdownBranch = countdown.Branch;
            leg.CountdownSeconds = countdown.Seconds;

            // H2 / H3: one ELS scan -> realized + cumulative + badge. M6 per-cycle
            // flow: the SAME scan also collects the debit / pickup / delivery rows
            // the flow display buckets by cycle below (no second ledger walk).
            var flowRows = new List<LogisticsFlowPresentation.FlowRow>();
            LogisticsDeliveryPresentation.RouteDeliverySummary summary =
                CollectRouteDeliverySummary(route.Id, flowRows);
            leg.HasDeliveries = summary.HasAny;
            leg.LastCycleText = summary.HasAny
                ? LogisticsDeliveryPresentation.FormatRealizedDelivery(summary.LastRequested, summary.LastActual)
                : null;
            leg.LastCycleShortfall = summary.HasAny
                && LogisticsDeliveryPresentation.HasShortfall(summary.LastRequested, summary.LastActual);
            leg.CumulativeText = LogisticsDeliveryPresentation.FormatCumulativeTotal(summary.CumulativeTotal);

            // M6 per-cycle flow: bucket the collected rows by cycle and render the
            // bounded newest-first lines HERE on the ~1 Hz pass (endpoint name
            // resolution touches FlightGlobals, never the IMGUI draw path). Null
            // when the route has no cycle-scoped rows yet.
            leg.FlowLines = BuildPerCycleFlowLines(route, flowRows, currentUT);

            bool ghostDriving = RouteStatusPolicy.GhostDriving(route.Status);
            LogisticsDeliveryPresentation.DeliveryOutcome lastOutcome = ClassifyLastOutcome(route.Status, summary);
            leg.Badge = LogisticsDeliveryPresentation.ClassifyDeliveryBadge(
                ghostDriving, lastOutcome, route.CompletedCycles, route.SkippedCycles);

            // H4: resolve the destination cell here (it touches FlightGlobals and, on an
            // unresolved surface endpoint, scans all vessels), so the draw path only
            // reads the cached strings. The resolved Vessel is surfaced too so the M4
            // capacity probe below reuses this single TryResolveEndpoint pass instead
            // of a second O(vessels) scan.
            ResolveDestinationCell(route, out string destText, out string destTooltip, out Vessel destVessel);
            leg.DestinationText = destText;
            leg.DestinationTooltip = destTooltip;

            // M4: when the route is DestinationFull, append a live free-capacity
            // context line so the player can tell "tanks are full" apart from a
            // misrouted delivery. The LIVE LiveDeliveryCapacityProbe read runs HERE in
            // the ~1 Hz pass (never per IMGUI frame) and only for the DestinationFull
            // status; every other status leaves CapacityContext null.
            if (route.Status == RouteStatus.DestinationFull)
                leg.CapacityContext = ResolveCapacityContext(route, destVessel, destText);

            // L1: for a Paused route, classify the Status cell as never-run "New" vs
            // deliberately-paused "Paused" from the completed-cycle count, and decide
            // whether the "Send Once to test" guidance shows. Computed only here (the
            // ~1 Hz refresh) so the decision logs once, not per IMGUI frame. Non-Paused
            // routes keep the StatusReason cell and never read these fields.
            if (route.Status == RouteStatus.Paused)
            {
                leg.PausedLabel = LogisticsDeliveryPresentation.ClassifyPausedRoute(route.CompletedCycles);
                leg.PausedLabelText = LogisticsDeliveryPresentation.PausedRouteLabelText(leg.PausedLabel);
                leg.ShowSendOnceGuidance =
                    LogisticsDeliveryPresentation.ShouldShowSendOnceGuidance(route.CompletedCycles);
                ParsekLog.Verbose("UI",
                    $"Logistics paused-route label route={ShortId(route.Id)} " +
                    $"completedCycles={route.CompletedCycles.ToString(CultureInfo.InvariantCulture)} " +
                    $"label={leg.PausedLabel}");
            }

            // Run-cost (Phase 2): per-run net funds cost. Computed HERE on the ~1 Hz
            // refresh because SumRecoveredCredits is an O(actions) ELS scan that must
            // never run on the IMGUI draw path. ComputeELS is memoized (elsCache), so
            // re-calling it after the H2/H3 delivery scan above adds no second ledger
            // walk. Applicable / CostKnown are false outside Career + KSC origin, so
            // the detail draw path then renders nothing.
            leg.RunCost = ComputeRouteRunCost(route);

            // Last-partial-delivery report: rendered on the same ~1 Hz pass.
            // Not gated by ShouldDisplayHold - it reports a past physical loss
            // (what a mid-transit capacity shrink actually cost), which no
            // status makes misleading; the orchestrator clears it on the next
            // full delivery.
            leg.PartialText = LogisticsHoldPresentation.FormatPartialDeliveryLine(
                route.LastPartialDeliverySummary,
                route.LastPartialDeliveryUT >= 0.0 ? currentUT - route.LastPartialDeliveryUT : -1.0);

            // M6 hold reasons: render the persisted Route.LastHold* fields to
            // player language HERE on the ~1 Hz pass (never per IMGUI frame).
            // ShouldDisplayHold gates DISPLAY only (persistence is
            // unconditional): MissingSourceRecording / SourceChanged rows
            // suppress an older hold that would mislead, mirroring the M4
            // CapacityContext status gate above. HoldText carries the
            // "checked {age} ago" suffix from LastHoldUT vs the current UT.
            if (LogisticsHoldPresentation.ShouldDisplayHold(route.Status, route.LastHoldKind))
            {
                leg.HoldShort = LogisticsHoldPresentation.DescribeHold(
                    route.LastHoldKind, route.LastHoldDetail, route.LastHoldShortfall);
                leg.HoldText = LogisticsHoldPresentation.FormatHoldDetailLine(
                    leg.HoldShort,
                    route.LastHoldUT >= 0.0 ? currentUT - route.LastHoldUT : -1.0);
                // M6 closeout (row-level treatment): the compact Status-cell
                // override, same display gate and same ~1 Hz pass as the
                // tooltip clause above. Paused rows compute it too but their
                // cell keeps the L1 New/Paused label (the draw path only
                // consumes this in the non-Paused sections).
                leg.HoldCellText = LogisticsHoldPresentation.StatusCellText(
                    route.LastHoldKind, route.LastHoldDetail, route.LastHoldShortfall);
            }

            return leg;
        }

        /// <summary>
        /// Computes one route's per-run net funds cost
        /// (<see cref="RouteRunCostCalculator.RouteRunCost"/>) for the legibility
        /// cache. This is the one non-pure piece (it needs the live
        /// <see cref="EffectiveState.ComputeELS"/> and the live Career probe), so it
        /// stays in the window file and feeds the pure calculator + presentation
        /// helpers; called only on the ~1 Hz refresh, never per IMGUI frame.
        ///
        /// <para>Career is probed with the same defensively-wrapped shape as
        /// <see cref="LiveRouteRuntimeEnvironment.IsCareer"/>
        /// (<c>HighLogic.CurrentGame.Mode == Game.Modes.CAREER</c>). ComputeELS is
        /// wrapped in a try/catch (it can throw during early load before the scenario
        /// module is published) and treated as no recoveries on failure, mirroring
        /// <see cref="CollectRouteDeliverySummary"/>. The tree-member id set is
        /// resolved once per route per refresh via
        /// <see cref="RouteRunCostCalculator.ResolveTreeRecordingIds(Route)"/>.</para>
        /// </summary>
        private static RouteRunCostCalculator.RouteRunCost ComputeRouteRunCost(Route route)
        {
            bool isCareer;
            try
            {
                isCareer = HighLogic.CurrentGame != null
                    && HighLogic.CurrentGame.Mode == Game.Modes.CAREER;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("UI",
                    $"Logistics run-cost: IsCareer probe threw {ex.GetType().Name}: {ex.Message}; defaulting false");
                isCareer = false;
            }

            IReadOnlyList<GameAction> els;
            try
            {
                els = EffectiveState.ComputeELS();
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("UI",
                    $"Logistics run-cost: ComputeELS threw {ex.GetType().Name}: {ex.Message}; treating as no recoveries");
                els = null;
            }

            HashSet<string> treeRecordingIds = RouteRunCostCalculator.ResolveTreeRecordingIds(route);
            return RouteRunCostCalculator.Compute(route, isCareer, els, treeRecordingIds);
        }

        /// <summary>
        /// Computes a CANDIDATE's per-run net funds cost (Phase 3.2 / 3.4): the
        /// route-creation summary and the candidate row run before any
        /// <see cref="Route"/> exists, so this derives the inputs from the
        /// candidate's source recording + owning tree instead of a Route. KSC origin
        /// is decided exactly as <c>RouteBuilder</c> decides the built
        /// <see cref="Route.IsKscOrigin"/>: from the tree ROOT recording
        /// (<c>LaunchSiteName</c> set AND <c>StartBodyName == "Kerbin"</c>), NOT from
        /// the dock-child <c>analysis.SourceRecording</c> (which carries a null
        /// launch site), via <see cref="RouteRunCostCalculator.IsCandidateKscOrigin"/>.
        /// Career and ELS are probed with the same defensive wrap + memoized
        /// <see cref="EffectiveState.ComputeELS"/> as the route path. Returns a
        /// not-applicable / unknown cost (which the UI then suppresses) when the
        /// analysis or source recording is null. Called only off the IMGUI draw path
        /// (dialog spawn + the ~1 Hz candidate cache), never per frame.
        /// </summary>
        internal static RouteRunCostCalculator.RouteRunCost ComputeCandidateRunCost(
            RouteAnalysisResult analysis, RecordingTree tree)
        {
            Recording source = analysis?.SourceRecording;

            bool isCareer;
            try
            {
                isCareer = HighLogic.CurrentGame != null
                    && HighLogic.CurrentGame.Mode == Game.Modes.CAREER;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("UI",
                    $"Logistics candidate run-cost: IsCareer probe threw {ex.GetType().Name}: {ex.Message}; defaulting false");
                isCareer = false;
            }

            // KSC origin: derived from the tree ROOT recording, exactly as
            // RouteBuilder derives the built Route.IsKscOrigin. The launch-site /
            // start-body info lives on the first recording of the flight (the
            // tree root), NOT on analysis.SourceRecording: that child started
            // mid-flight at the dock and carries LaunchSiteName == null, so a
            // source-only check would suppress the cost block on the common
            // docking case (G5 / D2). The helper falls back to the source only
            // when the tree has no resolvable root (legacy single-recording).
            bool isKscOrigin = RouteRunCostCalculator.IsCandidateKscOrigin(source, tree);

            IReadOnlyList<GameAction> els;
            try
            {
                els = EffectiveState.ComputeELS();
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("UI",
                    $"Logistics candidate run-cost: ComputeELS threw {ex.GetType().Name}: {ex.Message}; treating as no recoveries");
                els = null;
            }

            return RouteRunCostCalculator.ComputeForCandidate(
                source, tree, isCareer, isKscOrigin, els,
                LiveRouteRuntimeEnvironment.LookupPartCost,
                LiveRouteRuntimeEnvironment.LookupResourceUnitCost);
        }

        /// <summary>
        /// Builds the M4 DestinationFull free-capacity context line for the legibility
        /// cache. Reads the route's first stop's <see cref="RouteStop.DeliveryManifest"/>
        /// (the requested amounts) and, for each resource, the LIVE free capacity on the
        /// resolved destination <paramref name="destVessel"/> via a
        /// <see cref="LiveDeliveryCapacityProbe"/> constructed with the same
        /// <c>loaded &amp;&amp; !packed</c> gate the orchestrator uses (so the reported
        /// number matches what a real delivery would fill). When the vessel could not be
        /// resolved or the manifest is empty, the entry list is empty and the pure
        /// <see cref="LogisticsDeliveryPresentation.FormatCapacityContext"/> renders a
        /// "(capacity unknown)" line. This is the one non-pure piece of M4 (it touches a
        /// live Vessel), so it stays in the window file and runs only on the ~1 Hz
        /// refresh; the draw path reads the cached string. Logs the probe decision
        /// (rate-limited per route).
        /// </summary>
        private string ResolveCapacityContext(Route route, Vessel destVessel, string destName)
        {
            var entries = new List<LogisticsDeliveryPresentation.CapacityEntry>();
            Dictionary<string, double> manifest =
                route?.Stops != null && route.Stops.Count > 0 && route.Stops[0] != null
                    ? route.Stops[0].DeliveryManifest
                    : null;

            if (destVessel != null && manifest != null && manifest.Count > 0)
            {
                bool destinationIsLoaded = destVessel.loaded && !destVessel.packed;
                var probe = new LiveDeliveryCapacityProbe(destVessel, destinationIsLoaded);
                foreach (KeyValuePair<string, double> kv in manifest)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    double free = probe.ProbeResourceFreeCapacity(kv.Key);
                    entries.Add(new LogisticsDeliveryPresentation.CapacityEntry(kv.Key, kv.Value, free));
                }
            }

            ParsekLog.VerboseRateLimited("UI", "dest-capacity-" + route.Id,
                $"Logistics: capacity context route={ShortId(route.Id)} dest='{destName}' " +
                $"resolved={(destVessel != null ? "true" : "false")} resources={entries.Count.ToString(CultureInfo.InvariantCulture)}",
                5.0);

            return LogisticsDeliveryPresentation.FormatCapacityContext(destName, entries);
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
        /// M6 per-cycle flow: the SAME single walk also fills
        /// <paramref name="flowRows"/> (debit / pickup / delivery rows bucketed
        /// later by cycle) via <see cref="LogisticsFlowPresentation.CollectRows"/>,
        /// so the flow display adds NO second ledger walk; pass null to skip.
        /// </summary>
        private static LogisticsDeliveryPresentation.RouteDeliverySummary CollectRouteDeliverySummary(
            string routeId, List<LogisticsFlowPresentation.FlowRow> flowRows)
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

            LogisticsFlowPresentation.CollectRows(els, routeId, rows, flowRows);
            return LogisticsDeliveryPresentation.SummarizeRouteDeliveries(rows);
        }

        /// <summary>
        /// Builds the M6 per-cycle flow lines for the legibility cache from the
        /// rows collected in the shared ELS walk. This is the non-pure half (it
        /// resolves live vessel names), so it stays in the window file and runs
        /// only on the ~1 Hz refresh: endpoint pids on debit / pickup rows
        /// resolve through the O(1) <c>FlightGlobals.FindVessel</c> (the
        /// RouteEndpointResolver.ResolveByPid idiom - a vanished vessel misses
        /// the map and the pure formatter renders its "vessel pid=N" fallback,
        /// never blank), and per-stop delivery destinations resolve to the live
        /// vessel name by the stop endpoint's baked pid with the recorded
        /// coords as the fallback (the H4 name-else-coords contract, without
        /// the O(vessels) surface re-scan). The pure
        /// <see cref="LogisticsFlowPresentation.FormatPerCycleFlow"/> does the
        /// bucketing / bounding / wording. Returns null when there is nothing
        /// to show so the detail panel draws no header.
        /// </summary>
        private static List<LogisticsFlowPresentation.CycleFlowLine> BuildPerCycleFlowLines(
            Route route, List<LogisticsFlowPresentation.FlowRow> flowRows, double currentUT)
        {
            if (flowRows == null || flowRows.Count == 0)
                return null;

            // Live names for the endpoint pids that appear on the rows.
            var endpointNames = new Dictionary<uint, string>();
            for (int i = 0; i < flowRows.Count; i++)
            {
                uint pid = flowRows[i].EndpointPid;
                if (pid == 0u || endpointNames.ContainsKey(pid)) continue;
                string name = TryResolveLiveVesselName(pid);
                if (!string.IsNullOrEmpty(name))
                    endpointNames[pid] = name;
            }

            // Per-stop delivery destination names, indexed by stop index.
            List<string> stopDestinations = null;
            if (route?.Stops != null && route.Stops.Count > 0)
            {
                stopDestinations = new List<string>(route.Stops.Count);
                for (int i = 0; i < route.Stops.Count; i++)
                {
                    RouteStop stop = route.Stops[i];
                    if (stop == null)
                    {
                        stopDestinations.Add(null);
                        continue;
                    }
                    string name = TryResolveLiveVesselName(stop.Endpoint.VesselPersistentId);
                    string destination = !string.IsNullOrEmpty(name)
                        ? name
                        : LogisticsDeliveryPresentation.FormatEndpointCoords(stop.Endpoint);
                    destination += RouteCreationFormatters.ConnectionKindSuffix(stop.ConnectionKind);
                    stopDestinations.Add(destination);
                }
            }

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    flowRows, endpointNames, FormatOrigin(route), stopDestinations,
                    currentUT, LogisticsFlowPresentation.MaxCyclesShown);
            return lines.Count > 0 ? lines : null;
        }

        /// <summary>
        /// Live vessel name by persistent id, or null when the pid is 0 / the
        /// vessel is gone / FlightGlobals is unavailable. O(1) per pid
        /// (FlightGlobals.PersistentVesselIds lookup); defensively wrapped like
        /// <c>RouteEndpointResolver.ResolveByPid</c> so a stock-side teardown
        /// null-deref degrades to the pid fallback instead of a crash.
        /// </summary>
        private static string TryResolveLiveVesselName(uint pid)
        {
            if (pid == 0u) return null;
            try
            {
                if (FlightGlobals.fetch != null
                    && FlightGlobals.FindVessel(pid, out Vessel found)
                    && found != null)
                    return found.vesselName;
            }
            catch
            {
                // Best-effort name only; the formatter's pid fallback covers a miss.
            }
            return null;
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
            // Cache miss (route not yet refreshed this cycle, or a null/empty id the
            // refresh loop skipped): return an explicit "unknown" struct. Badge MUST be
            // set to Paused, NOT left at the struct default, because DeliveryBadge
            // default is Delivering (= 0) and an unknown route must never flash the
            // green "Delivering" verdict (wrong-direction failure). DestinationText "-"
            // matches the empty-cell convention. L1: PausedLabel MUST be Paused (grey),
            // NOT the struct default New (= 0, cyan), so an unknown paused route never
            // flashes the cyan "New" treatment or the Send Once guidance until the cache
            // fills; PausedLabelText null falls back to StatusReason in the cell.
            return new RouteLegibility
            {
                Badge = LogisticsDeliveryPresentation.DeliveryBadge.Paused,
                DestinationText = "-",
                PausedLabel = LogisticsDeliveryPresentation.PausedRouteLabel.Paused,
                ShowSendOnceGuidance = false
            };
        }

        // ------------------------------------------------------------------
        // L2: cached-sorted route tables. Re-sort a section only when its row
        // count changes, the sort key/direction changes, or the throttled
        // legibility cache was refreshed (the NextDelivery / Destination sort
        // keys live in that cache). Never per IMGUI frame.
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the cached, sorted list for one route section, re-sorting only when
        /// the section's row count, the shared sort column/direction, or the legibility
        /// stamp changed since the last sort (the SpawnControlUI cached-sorted idiom,
        /// one cache per section). The sort keys for Origin / Destination / NextDelivery
        /// / Status / Delivery are projected from the throttled legibility cache through
        /// <see cref="BuildRouteSortKeys"/>, so the pure comparer never recomputes them.
        /// Logs the decision (the column / direction) once per actual re-sort, NOT per
        /// frame, via a Verbose line.
        /// </summary>
        private List<Route> GetSortedRoutesForSection(List<Route> rows, RouteSection section)
        {
            bool active = section == RouteSection.Active;
            int rowCount = rows?.Count ?? 0;
            int cachedCount = active ? cachedActiveCount : cachedPausedCount;
            float cachedStamp = active ? cachedActiveLegibilityStamp : cachedPausedLegibilityStamp;

            bool sortStateChanged = routeSortColumn != cachedRouteSortColumn
                || routeSortAscending != cachedRouteSortAscending
                || cachedStamp != lastLegibilityComputeRealtime;

            if (rowCount != cachedCount || sortStateChanged)
            {
                Dictionary<string, RouteSortKeys> keys = BuildRouteSortKeys(rows);
                List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                    rows, routeSortColumn, routeSortAscending, keys);

                if (active)
                {
                    cachedSortedActive = sorted;
                    cachedActiveCount = rowCount;
                    cachedActiveLegibilityStamp = lastLegibilityComputeRealtime;
                }
                else
                {
                    cachedSortedPaused = sorted;
                    cachedPausedCount = rowCount;
                    cachedPausedLegibilityStamp = lastLegibilityComputeRealtime;
                }
                // The sort column/direction is genuinely shared (both sections sort the
                // same way); a header click resets BOTH counts so both re-sort. The
                // legibility freshness is per-section (stamped above) so a ~1 Hz refresh
                // re-sorts each section independently.
                cachedRouteSortColumn = routeSortColumn;
                cachedRouteSortAscending = routeSortAscending;

                ParsekLog.Verbose("UI",
                    $"Logistics route sort applied section={section} " +
                    $"column={routeSortColumn} ascending={routeSortAscending} " +
                    $"rows={rowCount.ToString(CultureInfo.InvariantCulture)}");
            }

            return active ? cachedSortedActive : cachedSortedPaused;
        }

        /// <summary>
        /// Projects the per-route sort keys (Origin / Destination / NextDelivery /
        /// Status / Delivery display values) from the throttled legibility cache into a
        /// plain dictionary the pure <see cref="LogisticsSortPresentation.SortRoutes"/>
        /// comparer reads. Built only when a section actually re-sorts (throttled), not
        /// per frame. Origin and Status come from the same pure formatters the cells
        /// render, so a column sorts by exactly what the player sees.
        /// </summary>
        private Dictionary<string, RouteSortKeys> BuildRouteSortKeys(List<Route> rows)
        {
            var keys = new Dictionary<string, RouteSortKeys>();
            if (rows == null) return keys;
            for (int i = 0; i < rows.Count; i++)
            {
                Route route = rows[i];
                if (route == null || string.IsNullOrEmpty(route.Id)) continue;

                RouteLegibility leg = GetLegibility(route);
                bool hasNext = leg.CountdownBranch
                    != LogisticsCountdownPresentation.CountdownBranch.None;
                // M6 closeout: the held cell shows HoldCellText, so the Status
                // sort key follows it - "a column sorts by exactly what the
                // player sees" (the contract in this method's doc comment).
                string statusText = route.Status == RouteStatus.Paused && leg.PausedLabelText != null
                    ? leg.PausedLabelText
                    : (leg.HoldCellText ?? StatusReason(route.Status));

                keys[route.Id] = new RouteSortKeys
                {
                    OriginText = FormatOrigin(route),
                    DestinationText = leg.DestinationText ?? string.Empty,
                    NextDeliverySeconds = leg.CountdownSeconds,
                    HasNextDelivery = hasNext,
                    StatusText = statusText,
                    DeliveryText = LogisticsDeliveryPresentation.DeliveryBadgeLabel(leg.Badge)
                };
            }
            return keys;
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
        private void ResolveDestinationCell(Route route, out string text, out string tooltip, out Vessel resolvedVessel)
        {
            resolvedVessel = null;
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
            {
                resolvedName = v.vesselName;
                // Surface the resolved Vessel so the M4 capacity probe reuses this one
                // resolve pass instead of a second O(vessels) surface scan.
                resolvedVessel = v;
            }

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
            // M2 harvest origin (plan D7): no origin vessel, nothing debited.
            if (route.IsHarvestOrigin) return "harvested en route";
            if (route.Origin.VesselPersistentId != 0u || !string.IsNullOrEmpty(route.Origin.BodyName))
                return FormatEndpointShort(route.Origin);
            return "-";
        }

        internal static string FormatCandidateOrigin(RouteAnalysisResult analysis, RecordingTree tree)
        {
            // Resolve origin off the tree ROOT (the launch) via the shared helper,
            // not the dock-child source: the source started mid-flight at the dock
            // and has no LaunchSiteName, so reading it directly mis-reports a KSC
            // route as "-". Keeps this cell in step with the dialog summary, the
            // default name, and the built Route.IsKscOrigin.
            RouteCreationFormatters.RouteOriginIdentity id =
                RouteCreationFormatters.ResolveOriginIdentity(analysis, tree);
            switch (id.Kind)
            {
                case RouteCreationFormatters.RouteOriginKind.Ksc:
                    return "KSC (funds)";
                case RouteCreationFormatters.RouteOriginKind.Depot:
                    return "depot pid=" + id.DepotVesselPid.ToString(CultureInfo.InvariantCulture);
                case RouteCreationFormatters.RouteOriginKind.Harvest:
                    return "harvested en route";
                default:
                    return "-";
            }
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

        // L3: the manifest formatting moved to the pure
        // LogisticsDeliveryPresentation.FormatWouldDeliver so the Candidates section
        // "Would deliver" cell and the route / candidate detail lines share one
        // unit-tested formatter. This thin window-side forwarder keeps the existing
        // callers (route delivery, candidate detail) on the same text.
        private static string FormatManifest(
            Dictionary<string, double> resources,
            List<InventoryPayloadItem> inventory)
        {
            return LogisticsDeliveryPresentation.FormatWouldDeliver(resources, inventory);
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<none>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        /// <summary>
        /// Decides whether the bottom tooltip echo box should render boxed help
        /// text. Returns (false, "") when there is no hovered-control tooltip this
        /// frame (the echo box collapses to zero height), or (true, tooltip) when a
        /// control is hovered. The boolean drives the box's spacing / content /
        /// style only, never the number of IMGUI controls emitted (see
        /// <see cref="DrawTooltipEchoBox"/>). Pure and Unity-free for unit testing.
        /// </summary>
        internal static (bool show, string text) ResolveTooltipEcho(string guiTooltip)
        {
            if (string.IsNullOrEmpty(guiTooltip))
                return (false, string.Empty);
            return (true, guiTooltip);
        }

        /// <summary>
        /// Draws the bottom tooltip echo box with a control count that is invariant
        /// across the IMGUI Layout and Repaint passes: it ALWAYS emits exactly one
        /// GUILayout.Space plus one GUILayout.Label, and only varies their spacing,
        /// content, and style by whether a control is hovered. This matters because
        /// GUI.tooltip is empty during Layout and only becomes populated during
        /// Repaint (after a hovered control draws), so any branch here that emits a
        /// DIFFERENT number of controls between the two passes desyncs the layout
        /// group and makes the next control (the Close button) overrun it, throwing
        /// "Getting control N's position in a group with only N controls when doing
        /// repaint". Mirrors the invariant-count pattern in SettingsWindowUI.
        /// </summary>
        internal static void DrawTooltipEchoBox(string guiTooltip)
        {
            (bool showEcho, string echoText) = ResolveTooltipEcho(guiTooltip);
            // Always one Space + always one Label. When there is no tooltip the
            // Space collapses to 0 and the Label renders empty at zero height, so
            // the box disappears visually without changing the control count.
            GUILayout.Space(showEcho ? SpacingSmall : 0f);
            GUILayout.Label(
                showEcho ? echoText : string.Empty,
                showEcho ? GUI.skin.box : GUI.skin.label,
                showEcho ? GUILayout.ExpandWidth(true) : GUILayout.Height(0f));
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

            // L4: the five status-text colors come from the one shared ParsekUI
            // palette (the house source) so the literals live in a single place; this
            // window keeps its own GUIStyle objects (no padding here, unlike the
            // Recordings table) and only the text color is centralized. detailStyle's
            // (0.8, 0.8, 0.8) is Logistics-only and stays a local literal.
            statusStyleGreen = new GUIStyle(GUI.skin.label);
            statusStyleGreen.normal.textColor = parentUI.GetStatusColor(ParsekUI.StatusColorKind.Green);
            statusStyleYellow = new GUIStyle(GUI.skin.label);
            statusStyleYellow.normal.textColor = parentUI.GetStatusColor(ParsekUI.StatusColorKind.Yellow);
            statusStyleRed = new GUIStyle(GUI.skin.label);
            statusStyleRed.normal.textColor = parentUI.GetStatusColor(ParsekUI.StatusColorKind.Red);
            statusStyleGrey = new GUIStyle(GUI.skin.label);
            statusStyleGrey.normal.textColor = parentUI.GetStatusColor(ParsekUI.StatusColorKind.Grey);
            statusStyleCyan = new GUIStyle(GUI.skin.label);
            statusStyleCyan.normal.textColor = parentUI.GetStatusColor(ParsekUI.StatusColorKind.Cyan);

            detailStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            detailStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);

            ParsekLog.Verbose("UI", "Logistics status styles built from shared ParsekUI palette");
        }
    }
}
