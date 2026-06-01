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

        // Deferred mutations: collected during the draw loop and applied after the
        // scroll view so we never mutate RouteStore.CommittedRoutes mid-iteration.
        private Route pendingPause;
        private Route pendingActivate;
        private Route pendingSendOnce;
        private string pendingDeleteRouteId;
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
        private GUIStyle sectionHeaderStyle;
        private GUIStyle detailStyle;

        // Tab bar (GUILayout.Toolbar splits the window width across buttons).
        // Only Supply Routes today; reserved for future Schedule / Resources tabs.
        private GUIStyle toggleButtonStyle;
        private int selectedTab;
        private static readonly string[] TabLabels = new[] { "Supply Routes" };

        // Column widths. Header and rows use the same constants and live in the
        // same per-section box, so columns line up like the Recordings window.
        private const float ColW_Num = 30f;        // "#" row-index column (per section)
        private const float ColW_Origin = 120f;
        private const float ColW_Destination = 180f;
        private const float ColW_Interval = 70f;
        private const float ColW_Transit = 70f;
        private const float ColW_Cycles = 45f;
        private const float ColW_Status = 140f;
        private const float ColW_Actions = 190f;   // fixed action cell so Name-expand is identical every row

        private const float SpacingSmall = 3f;
        private const float SpacingLarge = 8f;
        private const float MinWindowWidth = 980f;
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
                windowRect = new Rect(x, mainWindowRect.y, 1000, 340);
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

            // Tab bar. GUILayout.Toolbar splits the full window width across the
            // buttons; with one label it is a single full-width button, and
            // future Schedule / Resources tabs split the width automatically.
            selectedTab = GUILayout.Toolbar(selectedTab, TabLabels, toggleButtonStyle);
            GUILayout.Space(SpacingSmall);

            // Reset deferred actions for this frame.
            pendingPause = null;
            pendingActivate = null;
            pendingSendOnce = null;
            pendingDeleteRouteId = null;
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
            GUILayout.Label("Status", h, GUILayout.Width(ColW_Status));
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

            GUILayout.Label(FormatOrigin(route), GUILayout.Width(ColW_Origin));
            GUILayout.Label(FormatDestination(route), GUILayout.Width(ColW_Destination));
            // Interval cell shows BOTH the cadence multiplier and the resulting human
            // cadence (e.g. "1x (~14m)"); the full stepper lives in the detail panel.
            GUILayout.Label(
                new GUIContent(FormatCadence(route),
                    "Dispatch cadence = N x run duration. 1x is the floor (fastest the run allows); raise N in the detail panel to launch less often."),
                GUILayout.Width(ColW_Interval));
            GUILayout.Label(FormatDuration(route.TransitDuration), GUILayout.Width(ColW_Transit));
            GUILayout.Label(route.CompletedCycles.ToString(CultureInfo.InvariantCulture), GUILayout.Width(ColW_Cycles));

            GUILayout.Label(new GUIContent(route.Status.ToString(), StatusReason(route.Status)),
                StatusStyleFor(route.Status), GUILayout.Width(ColW_Status));

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
                pendingDeleteRouteId = route.Id;
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
            GUILayout.Label(new GUIContent("eligible", "A sealed, valid Supply Run. Create Route promotes it to a Paused route you can Send Once / Activate."),
                statusStyleCyan, GUILayout.Width(ColW_Status));

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
            string deliv = FormatRouteDelivery(route);
            DetailLine($"Delivers per cycle: {deliv}");
            DetailLine($"Status: {route.Status} - {StatusReason(route.Status)}");
            if (route.Status == RouteStatus.Active || route.Status == RouteStatus.InTransit)
            {
                double until = route.NextDispatchUT - currentUT;
                DetailLine(until > 0
                    ? $"Next dispatch in {SelectiveSpawnUI.FormatCountdown(until)}"
                    : "Next dispatch due");
            }
            DetailLine($"Interval: {FormatDuration(route.DispatchInterval)}   Transit: {FormatDuration(route.TransitDuration)}   Cycles: {route.CompletedCycles}");
            DrawCadenceStepper(route);
            DetailLine($"Source recordings: {FormatSourceRecordingIds(route)}");
            GUILayout.EndVertical();
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
            string srcId = candidate.Analysis.SourceRecording?.RecordingId;
            DetailLine($"Source recording: {ShortId(srcId)}   Tree: {ShortId(candidate.Tree?.Id)}");
            GUILayout.EndVertical();
        }

        private void DetailLine(string text)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label(text, detailStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        private void DrawSectionHeader(string text)
        {
            GUILayout.Space(SpacingSmall);
            GUILayout.Label(text, sectionHeaderStyle);
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
            if (!string.IsNullOrEmpty(pendingDeleteRouteId))
            {
                bool ok = RouteStore.RemoveRoute(pendingDeleteRouteId);
                ParsekLog.Info("UI", $"Logistics: Delete route={ShortId(pendingDeleteRouteId)} result={(ok ? "removed" : "not-found")}");
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

            pendingPause = null;
            pendingActivate = null;
            pendingSendOnce = null;
            pendingDeleteRouteId = null;
            pendingCreate = null;
            pendingCadenceRoute = null;
            pendingCadenceMultiplier = 0;
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

            // v0 debug placeholder interval (30s) so cycles repeat quickly in
            // testing; created Paused so the player verifies via Send Once before
            // turning on periodic dispatch. allowIntervalBelowTransit because the
            // placeholder is intentionally shorter than a real flight's transit.
            var inputs = new RouteBuilder.RouteCreationInputs
            {
                Name = null, // RouteBuilder generates a default name
                DispatchIntervalSeconds = 30.0
            };

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                candidate.Analysis, candidate.Tree, inputs, mode,
                idFactory: null,
                initialStatus: RouteStatus.Paused,
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
                    $"Logistics: Create Route from candidate tree={ShortId(candidate.Tree.Id)} -> route={ShortId(outcome.Route.Id)} name='{outcome.Route.Name}' (Paused, interval=30s)");
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

        private static string FormatDestination(Route route)
        {
            if (route.Stops == null || route.Stops.Count == 0) return "-";
            RouteStop stop = route.Stops[0];
            if (stop == null) return "-";
            return FormatEndpointShort(stop.Endpoint);
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

        private static string FormatSourceRecordingIds(Route route)
        {
            if (route.RecordingIds == null || route.RecordingIds.Count == 0) return "-";
            var sb = new StringBuilder();
            for (int i = 0; i < route.RecordingIds.Count; i++)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(ShortId(route.RecordingIds[i]));
            }
            return sb.ToString();
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<none>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        // Human-readable reason for a status. For blocked-active states this
        // explains why a cycle is "visual-only" (ghost flies, nothing transfers).
        private static string StatusReason(RouteStatus status)
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

            sectionHeaderStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            sectionHeaderStyle.normal.textColor = new Color(0.85f, 0.9f, 1f);

            // Tab-bar button: selected tab looks pressed via onNormal.background
            // copied from GUI.skin.button.active (matches Kerbals/Career idiom).
            toggleButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            toggleButtonStyle.onNormal.background = GUI.skin.button.active.background;
            toggleButtonStyle.onHover.background = GUI.skin.button.active.background;
            toggleButtonStyle.onNormal.textColor = Color.white;
            toggleButtonStyle.onHover.textColor = Color.white;

            detailStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            detailStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
        }
    }
}
