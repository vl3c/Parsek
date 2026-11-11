using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The table rows: column headers and route sorting, the route row and its interact and Go To cells, the interval cell and its edit, the armed Send / Link button states, and the candidate row.
    internal partial class LogisticsWindowUI
    {
        // L3: purpose-built static header for the Candidates section. A candidate is a
        // sealed-but-not-yet-promoted Supply Run, so the route-only columns (Interval /
        // Cycle / Next / Status / Delivery) do not apply and used to render literal "-" /
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
            GUILayout.Label(new GUIContent("Route",
                    "A finished supply run that can become a route: its name over where it runs from and to."),
                h, GUILayout.ExpandWidth(true));
            GUILayout.Label(
                new GUIContent("Would deliver",
                    "What each run would deliver or pick up. Create route makes it a Paused route you can Send or Activate."),
                h, GUILayout.Width(ColW_WouldDeliver));
            GUILayout.Label(new GUIContent("Transit", LogisticsRoutePresentation.CandidateTransitTooltip),
                h, GUILayout.Width(ColW_Interval));
            DrawInteractHeaderCell();
            GUILayout.EndHorizontal();
        }

        // The centred "Interact" header cell: a zero-margin dark box exactly the measured
        // Interact column wide (the Missions header shape), shared by the route tables and
        // the candidates table so both columns line up.
        private void DrawInteractHeaderCell()
        {
            GUILayout.BeginHorizontal(interactHeaderContainerStyle,
                GUILayout.Width(interactColumnWidth));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Interact", interactHeaderLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // L2: the clickable sort header for the Active / Paused route tables. Each
        // sortable column routes through parentUI.DrawSortableHeaderCore (the shared
        // generic header used by SpawnControlUI / RecordingsTableUI / Missions), toggling
        // the shared routeSortColumn / routeSortAscending and invalidating the cached-sorted
        // lists on change. The "#" index, Delivers and Interact are not sortable. Cell
        // order and widths match DrawRouteRow exactly, and both read the same latched
        // drawTuning, so the Advanced-only Runs column is added or dropped in both.
        private void DrawRouteSortableHeader()
        {
            GUIStyle h = parentUI.GetColumnHeaderStyle();
            GUILayout.BeginHorizontal();
            GUILayout.Label("#", h, GUILayout.Width(ColW_Num));
            DrawRouteSortColumn("Route", LogisticsRouteSortColumn.Name, 0f, true,
                LogisticsRoutePresentation.RouteHeaderTooltip);
            GUILayout.Label(new GUIContent("Delivers",
                    "What each run delivers. The detail below the row names the destination."),
                h, GUILayout.Width(ColW_Delivers));
            DrawRouteSortColumn("Every", LogisticsRouteSortColumn.Interval, ColW_Interval, false,
                LogisticsRoutePresentation.EveryHeaderTooltip);
            if (drawTuning)
                DrawRouteSortColumn("Runs", LogisticsRouteSortColumn.Cycles, ColW_Runs, false,
                    LogisticsRoutePresentation.RunsTooltip);
            DrawRouteSortColumn("Next", LogisticsRouteSortColumn.NextDelivery, ColW_NextDelivery, false,
                LogisticsRoutePresentation.NextHeaderTooltip);
            DrawRouteSortColumn("Status", LogisticsRouteSortColumn.Status, ColW_Status, false,
                LogisticsRoutePresentation.StatusHeaderTooltip);
            // Interact: a centred label exactly the measured column wide, like each row's
            // zero-margin Interact cell below it.
            DrawInteractHeaderCell();
            GUILayout.EndHorizontal();
        }

        // Thin per-window wrapper around the shared sortable-header helper (mirrors
        // SpawnControlUI.DrawSpawnSortableHeader). The arrow follows the EFFECTIVE sort
        // column (a Runs sort reads as a Route sort in Basic, where Runs has no header), and
        // a click writes the new column back into the stored routeSortColumn. The onChanged
        // callback logs the decision (once, on the click) and dirties the cached sort tuple
        // so both sections re-sort on the next draw.
        private void DrawRouteSortColumn(string label, LogisticsRouteSortColumn col, float width, bool expand,
            string tooltip)
        {
            LogisticsRouteSortColumn shown =
                LogisticsRoutePresentation.EffectiveSortColumn(routeSortColumn, drawTuning);
            parentUI.DrawSortableHeaderCore(
                label, col, ref shown, ref routeSortAscending, width, expand,
                () =>
                {
                    routeSortColumn = shown;
                    // Force a re-sort next draw: clearing the cached counts guarantees
                    // both section caches miss even if the row count is unchanged.
                    cachedActiveCount = -1;
                    cachedPausedCount = -1;
                    ParsekLog.Verbose("UI",
                        $"Logistics route sort changed column={routeSortColumn} ascending={routeSortAscending}");
                },
                tooltip: tooltip);
        }

        private enum RouteSection { Active, Paused }

        private void DrawRouteRow(Route route, RouteSection section, int rowNum, double currentUT)
        {
            if (route == null) return;
            string rowKey = route.Id ?? "<no-id>";
            bool expanded = expandedRows.Contains(rowKey);

            // Read the throttled per-route legibility values once for this row; the draw
            // path never recomputes them.
            RouteLegibility leg = GetLegibility(route);
            bool sendingOnce = IsSendingOnce(route);
            bool pausingAfterRun = IsPausingAfterRun(route);

            GUILayout.BeginHorizontal();

            GUILayout.Label(rowNum.ToString(CultureInfo.InvariantCulture), GUILayout.Width(ColW_Num));

            // Route: line 1 the caret + name (click to expand), line 2 the grey
            // "KSC [U+2192] Depot Station Duna I" at the same font size; the hover carries the
            // full origin and destination including coordinates.
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            string arrow = expanded ? "▼" : "▶";
            if (GUILayout.Button($"{arrow} {route.Name ?? "<unnamed>"}", routeNameStyle, GUILayout.ExpandWidth(true)))
                ToggleExpanded(rowKey, route.Name);
            GUILayout.Label(new GUIContent("   " + (leg.FromToText ?? "-"), leg.FromToTooltip ?? string.Empty),
                routeFromToStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndVertical();

            GUILayout.Label(
                new GUIContent(leg.DeliversCellText ?? "-", leg.DeliversLine ?? string.Empty),
                wrapCellStyle, GUILayout.Width(ColW_Delivers));

            // Every: the Advanced inline stepper, or Basic's read-only "every 4.0d". A route
            // armed by Send shows the read-only form in both modes: its one run ignores the
            // schedule, and an edit in progress on it is dropped (its field is gone).
            if (drawTuning && !sendingOnce)
                DrawIntervalCell(route, leg);
            else
            {
                if (sendingOnce && string.Equals(intervalEditRouteId, route.Id, System.StringComparison.Ordinal))
                    ClearIntervalEdit();
                GUILayout.Label(new GUIContent(leg.EveryText ?? "-", leg.EveryTooltip ?? string.Empty),
                    GUILayout.Width(ColW_Interval));
            }

            if (drawTuning)
                GUILayout.Label(
                    new GUIContent(LogisticsRoutePresentation.FormatRunsCell(route.CompletedCycles, route.SkippedCycles),
                        LogisticsRoutePresentation.RunsTooltip),
                    GUILayout.Width(ColW_Runs));

            // Next: the Missions countdown exactly (amber "T- " + two units, " (!)" when
            // warned); "-" in grey when no run is scheduled. The exact date is the hover.
            bool hasCountdown = leg.NextCellText != null && leg.NextCellText != "-";
            GUILayout.Label(new GUIContent(leg.NextCellText ?? "-", leg.NextTooltip ?? string.Empty),
                hasCountdown ? nextAmberStyle : statusStyleGrey, GUILayout.Width(ColW_NextDelivery));

            // Status: ONE colour-coded word + a short reason (merges the old Status and
            // Delivery columns); the hover is the full dated sentence.
            GUILayout.Label(new GUIContent(leg.Status.Text ?? "-", leg.StatusTooltip ?? string.Empty),
                WrapStatusStyleFor(leg.Status.Color), GUILayout.Width(ColW_Status));

            DrawRouteInteractCell(route, section, sendingOnce, pausingAfterRun,
                LogisticsRoutePresentation.BrokenShortReason(leg.Status), leg);

            GUILayout.EndHorizontal();

            if (expanded)
                DrawRouteDetail(route, currentUT);
        }

        /// <summary>
        /// The Interact column: a 2x2 grid of pair buttons, every cell the same measured
        /// width (<see cref="interactPairWidth"/>, the widest label in the skin's button
        /// style) and the column exactly two cells, a gap and the two insets wide
        /// (<see cref="interactColumnWidth"/>).
        /// <list type="bullet">
        ///   <item>Line 1, the state and Send: [Activate][Send] on a paused route,
        ///   [Pause][Send] on an active one, [Cancel][Send] while a Send counts down to its
        ///   window, greyed [Delivering...][Send] while it is in flight, greyed
        ///   [Pausing...][Send] for a Pause armed in flight. Send is live only on an
        ///   unarmed Paused route; otherwise it is greyed with its reason (running,
        ///   sending, armed, or "Stopped: ..." on a broken route).</item>
        ///   <item>Line 2: [Go to] (the mission the route repeats, on the Missions tab)
        ///   and [Log] (the route's Route History window).</item>
        /// </list>
        /// Every cell is exactly one button in every state, so a state change never
        /// changes the control count between a frame's Layout and Repaint passes; every
        /// greyed button carries its reason to the hover strip (DisabledHoverEcho).
        /// </summary>
        private void DrawRouteInteractCell(Route route, RouteSection section, bool sendingOnce, bool pausingAfterRun,
            string brokenReason, RouteLegibility leg)
        {
            bool armed = sendingOnce || pausingAfterRun;
            float pair = interactPairWidth;
            GUILayout.BeginVertical(GUILayout.Width(interactColumnWidth));

            // ---- line 1: the state button, then Send ----
            GUILayout.BeginHorizontal();
            GUILayout.Space(MissionsWindowUI.InteractCellInset);
            LogisticsRoutePresentation.ArmedLine1 line1 =
                LogisticsRoutePresentation.ResolveArmedLine1(sendingOnce, pausingAfterRun, route.Status);
            switch (line1)
            {
                case LogisticsRoutePresentation.ArmedLine1.Cancel:
                    // The countdown phase: nothing has launched, so the arm can be taken
                    // back. RouteOrchestrator.TryCancelSendOnce returns the route to Paused
                    // and writes no route event (the arm wrote none).
                    if (GUILayout.Button(new GUIContent(LogisticsRoutePresentation.CancelButtonLabel,
                            LogisticsRoutePresentation.CancelButtonTooltip),
                            interactPairButtonStyle, GUILayout.Width(pair)))
                        pendingCancelSend = route;
                    break;
                case LogisticsRoutePresentation.ArmedLine1.Delivering:
                case LogisticsRoutePresentation.ArmedLine1.Pausing:
                {
                    // A launched run is paid for and on the timeline, and the backend has
                    // no abort path, so the in-flight states are greyed with their reason.
                    string reason = line1 == LogisticsRoutePresentation.ArmedLine1.Delivering
                        ? (leg.DeliveringTooltip ?? TooltipForArmedState(ArmedSendKind.SendOnce))
                        : TooltipForArmedState(ArmedSendKind.PauseAfterCycle);
                    string label = line1 == LogisticsRoutePresentation.ArmedLine1.Delivering
                        ? LogisticsRoutePresentation.DeliveringButtonLabel
                        : LogisticsRoutePresentation.PausingButtonLabel;
                    bool prevEnabled = GUI.enabled;
                    GUI.enabled = false;
                    GUILayout.Button(new GUIContent(label, reason),
                        interactPairButtonStyle, GUILayout.Width(pair));
                    DisabledHoverEcho.CarryLastControl(false, reason);
                    GUI.enabled = prevEnabled;
                    break;
                }
                default:
                    if (section == RouteSection.Active)
                    {
                        if (GUILayout.Button(new GUIContent(LogisticsRoutePresentation.PauseButtonLabel,
                                "Stop running this route on its schedule. A run already in flight finishes first."),
                                interactPairButtonStyle, GUILayout.Width(pair)))
                            pendingPause = route;
                    }
                    else if (GUILayout.Button(new GUIContent(LogisticsRoutePresentation.ActivateButtonLabel,
                                 "Run this route on its schedule."),
                                 interactPairButtonStyle, GUILayout.Width(pair)))
                        pendingActivate = route;
                    break;
            }
            GUILayout.Space(MissionsWindowUI.InteractButtonGap);
            string sendReason = LogisticsRoutePresentation.SendDisabledReason(
                section == RouteSection.Active, armed, brokenReason, sendingOnce);
            bool sendEnabled = string.IsNullOrEmpty(sendReason);
            bool prev = GUI.enabled;
            GUI.enabled = sendEnabled;
            bool sendClicked = GUILayout.Button(new GUIContent(LogisticsRoutePresentation.SendButtonLabel,
                    sendEnabled
                        ? "Make one run at the next moment conditions allow (cargo, destination, launch window), then stay Paused."
                        : sendReason),
                interactPairButtonStyle, GUILayout.Width(pair));
            DisabledHoverEcho.CarryLastControl(sendEnabled, sendReason);
            GUI.enabled = prev;
            if (sendClicked && sendEnabled)
                pendingSendOnce = route;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // ---- line 2: Go to, then Log (the Route History) ----
            GUILayout.BeginHorizontal();
            GUILayout.Space(MissionsWindowUI.InteractCellInset);
            DrawRouteGoToButton(route, leg, pair);
            GUILayout.Space(MissionsWindowUI.InteractButtonGap);
            if (GUILayout.Button(new GUIContent(LogisticsRoutePresentation.LogButtonLabel,
                    "This route's history: every run it sent, picked up and delivered, with its pauses."),
                    interactPairButtonStyle, GUILayout.Width(pair)))
            {
                ParsekLog.Info("UI", $"Route Log button: route={(string.IsNullOrEmpty(route.Id) ? "<null>" : route.Id)} opens Route History");
                parentUI.OpenRouteHistoryWindow(route.Id, route.Name);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        /// <summary>
        /// Go to: opens the Missions tab on the mission the route repeats, through the same
        /// cross-link the Timeline and the Missions partner rows use
        /// (<see cref="RecordingsTableUI.ShowMissionForRecording"/> ->
        /// <see cref="MissionsWindowUI.RevealMissionForRecording"/>, which resolves a
        /// recording to its tree's ORIGINAL mission). The recording it names and the
        /// greyed reason are resolved on the ~1 Hz refresh, never here.
        /// </summary>
        private void DrawRouteGoToButton(Route route, RouteLegibility leg, float width)
        {
            string reason = LogisticsRoutePresentation.GoToDisabledReason(leg.GoToRecordingId != null);
            bool enabled = string.IsNullOrEmpty(reason);
            bool prevEnabled = GUI.enabled;
            GUI.enabled = enabled;
            bool clicked = GUILayout.Button(new GUIContent(LogisticsRoutePresentation.GoToButtonLabel,
                    enabled ? "Show the mission this route repeats on the Missions tab." : reason),
                interactPairButtonStyle, GUILayout.Width(width));
            DisabledHoverEcho.CarryLastControl(enabled, reason);
            GUI.enabled = prevEnabled;
            if (clicked && enabled)
            {
                ParsekLog.Info("UI",
                    $"Route Go to: route={ShortId(route.Id)} recording={leg.GoToRecordingId}");
                ParsekWindowSet.Of(parentUI).GetRecordingsTableUI().ShowMissionForRecording(leg.GoToRecordingId);
            }
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
            const string cadenceAtFloorReason =
                "Already at the minimum (1x = the fastest the run allows)";
            GUI.enabled = !atFloor;
            bool cadenceDownClicked = GUILayout.Button(new GUIContent("-",
                    atFloor ? cadenceAtFloorReason : "Run more often"),
                    GUILayout.Width(20f));
            DisabledHoverEcho.CarryLastControl(!atFloor, cadenceAtFloorReason);
            if (cadenceDownClicked)
            {
                pendingCadenceRoute = route;
                pendingCadenceMultiplier = RouteCadence.StepMultiplier(n, -1);
            }
            GUI.enabled = true;

            // Editable target-interval field (seconds). Deferred commit: while not
            // editing this route we show the live interval value and arm editing when
            // the field gains focus; while editing we hold the typed text and commit
            // on Enter (focus-loss / click-outside is handled in HandleLogisticsDefocus).
            // On a launch-window route the interval is not a time: the route delivers every
            // Nth window, so a typed duration would set N from N x transit and the shown
            // value means nothing. The field is greyed there and -/+ set N.
            bool windowed = RouteWindowBasisPresentation.IsWindowedBasis(leg.Basis);
            if (windowed)
            {
                GUI.enabled = false;
                GUILayout.TextField(FormatIntervalFieldValue(route.DispatchInterval), GUILayout.Width(64f));
                DisabledHoverEcho.CarryLastControl(false, WindowedIntervalFieldDisabledReason);
                GUI.enabled = true;
                if (editingThis)
                    ClearIntervalEdit();
            }
            else if (!editingThis)
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

            if (GUILayout.Button(new GUIContent("+", "Run less often"), GUILayout.Width(20f)))
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
            // Pure builder in LogisticsIntervalPresentation: the flat variant is the
            // window's longest runtime-composed tooltip, so its length is pinned by
            // TooltipEchoBudgetTests against the single-line strip budget.
            // Transit is formatted only for the flat branch - the windowed wording
            // never reads it, so a windowed row should not pay the string.Format.
            string nxTooltip = LogisticsIntervalPresentation.BuildNxCellTooltip(
                windowed, n, leg.BasisLabel ?? string.Empty,
                windowed ? null : FormatDuration(route.TransitDuration));
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
        /// Why the round-trip "Link" button is greyed out. The picker commits a PAIR, so
        /// it stays inert until a partner row is actually selected. Pure for unit testing.
        /// </summary>
        internal static string LinkButtonDisabledReason(string selectedPartnerRouteId)
        {
            return string.IsNullOrEmpty(selectedPartnerRouteId)
                ? "Pick a route above to pair this one with"
                : string.Empty;
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
                    return "Pause requested: this route finishes the run in flight, "
                        + "then stops running on its schedule.";
                case ArmedSendKind.SendOnce:
                default:
                    return "Sending one run at the next window (cargo, destination "
                        + "and launch window permitting), then pausing again.";
            }
        }

        private void DrawCandidateRow(RouteCandidate candidate, int rowNum)
        {
            if (candidate?.Analysis == null) return;
            string treeId = candidate.Tree?.Id ?? "<no-tree>";
            string rowKey = CandidateRowKey(candidate);
            bool expanded = expandedRows.Contains(rowKey);

            candidateDisplayCache.TryGetValue(treeId, out CandidateDisplay display);
            string name = display?.Name
                ?? RouteCreationFormatters.GenerateDefaultRouteName(candidate.Analysis, candidate.Tree);

            GUILayout.BeginHorizontal();

            GUILayout.Label(rowNum.ToString(CultureInfo.InvariantCulture), GUILayout.Width(ColW_Num));

            // Route: line 1 the caret + name, line 2 the grey "KSC [U+2192] Depot" in the
            // route rows' styles; the hover names the full origin and destination.
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            string arrow = expanded ? "\u25bc" : "\u25b6";
            if (GUILayout.Button($"{arrow} {name}", routeNameStyle, GUILayout.ExpandWidth(true)))
                ToggleExpanded(rowKey, name);
            GUILayout.Label(new GUIContent("   " + (display?.FromTo ?? "-"), display?.FromToTooltip ?? string.Empty),
                routeFromToStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndVertical();

            // Would deliver: the per-run manifest (pickups named) from the cached display.
            string wouldDeliverText = display?.Cargo
                ?? LogisticsDeliveryPresentation.FormatWouldDeliver(
                    candidate.Analysis.ResourceDeliveryManifest,
                    candidate.Analysis.InventoryDeliveryManifest);
            string wouldDeliverTip =
                "A finished supply run: what it would deliver each run. Create route makes it a Paused route you can Send or Activate.";
            GUILayout.Label(
                new GUIContent(wouldDeliverText, wouldDeliverTip),
                wrapCellStyle, GUILayout.Width(ColW_WouldDeliver));

            // Transit: read-only, under the route tables' Every column.
            GUILayout.Label(new GUIContent(FormatDuration(CandidateTransit(candidate)),
                    LogisticsRoutePresentation.CandidateTransitTooltip),
                GUILayout.Width(ColW_Interval));

            DrawCandidateInteractCell(candidate, name);

            GUILayout.EndHorizontal();

            if (expanded)
                DrawCandidateDetail(candidate);
        }

        /// <summary>
        /// The candidates' Interact column, on the route tables' grid: the same measured
        /// width and inset, line 1 [Create route] and line 2 [Dismiss], each a single as wide
        /// as two grid cells and the gap (the detail block's Rename / Delete width). Exactly
        /// two buttons on every row, so the control count never depends on state.
        /// </summary>
        private void DrawCandidateInteractCell(RouteCandidate candidate, string name)
        {
            float single = InteractSingleWidth;
            GUILayout.BeginVertical(GUILayout.Width(interactColumnWidth));

            GUILayout.BeginHorizontal();
            GUILayout.Space(MissionsWindowUI.InteractCellInset);
            if (GUILayout.Button(new GUIContent(LogisticsRoutePresentation.CreateRouteButtonLabel,
                    LogisticsRoutePresentation.CreateRouteButtonTooltip),
                    detailSlotButtonStyle, GUILayout.Width(single)))
                pendingCreate = candidate;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Dismiss hides a tree the player never intends as a route, deferred through
            // pendingDismissTreeId; Restore in Hidden missions takes it back.
            GUILayout.BeginHorizontal();
            GUILayout.Space(MissionsWindowUI.InteractCellInset);
            if (GUILayout.Button(new GUIContent(LogisticsRoutePresentation.DismissButtonLabel,
                    LogisticsRoutePresentation.DismissButtonTooltip),
                    detailSlotButtonStyle, GUILayout.Width(single)))
            {
                pendingDismissTreeId = candidate.Tree?.Id;
                pendingDismissLabel = name;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }
    }
}
