using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The expanded detail: route detail and its notes, endpoint rescan, the detail slot buttons, route rename, flights used, the cadence and priority steppers, and the candidate detail lines.
    internal partial class LogisticsWindowUI
    {
        /// <summary>
        /// The expanded detail block: only what the row cannot say, every line participle-
        /// or noun-first with exact dates. Basic: what each run delivers and where, the next
        /// run, the dated hold / partial lines, the capacity context and Re-scan of a broken
        /// route, the last delivery from the route event log, the run cost, the mission the route was
        /// built from, the round-trip partner, and Rename / Delete / Update parts. Advanced
        /// adds the Every and Priority steppers, Flights used and Link round-trip. A route's runs live in
        /// its Route History window (the Log button), not here. Every
        /// conditional line is keyed on a CACHED legibility field (never on live route state
        /// combined with it) or on the latched drawTuning, so the IMGUI control count stays
        /// stable across Layout/Repaint while the ~1 Hz cache and the live route drift apart.
        /// </summary>
        private void DrawRouteDetail(Route route, double currentUT)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            RouteLegibility leg = GetLegibility(route);
            detailSlotRoute = route;
            detailSlotNext = 0;

            // While this route is being renamed, the name field leads the block. It takes
            // no Interact slot (its cell is a same-width space), so Rename stays on the
            // Delivers line below it.
            if (string.Equals(renamingRouteId, route.Id, System.StringComparison.Ordinal))
                DrawRouteRenameField(route);

            DetailLine(leg.DeliversLine ?? LogisticsRoutePresentation.FormatDeliversEachRun(null, null));
            if (leg.NextLine != null)
                DetailLine(leg.NextLine);

            // The last recorded hold, dated (Route.LastHoldUT is the last check, so the
            // line names the last held run, never when the hold began).
            if (leg.HoldText != null)
                DetailLine(leg.HoldText, statusStyleYellow);

            // Last-partial-delivery report: what a mid-transit capacity shrink actually
            // cost (the undelivered remainder is lost), dated.
            if (leg.PartialText != null)
                DetailLine(leg.PartialText, statusStyleYellow);

            // DestinationFull: the live free-capacity context ("Munar Station tanks full:
            // 0.0 of 150.0 LiquidFuel free"), so full tanks read apart from a misroute.
            if (route.Status == RouteStatus.DestinationFull && !string.IsNullOrEmpty(leg.CapacityContext))
                DetailLine(leg.CapacityContext, statusStyleYellow);

            // A recoverable surface EndpointLost route: "Re-scan for endpoint"; otherwise
            // the disabled-with-explanation note.
            if (route.Status == RouteStatus.EndpointLost)
                DrawEndpointRescan(route);

            // The last delivery, from the route's RouteCargoDelivered route events (the
            // route stores no delivered UT), plus the running total.
            if (leg.LastDeliveredLine != null)
                DetailLine(leg.LastDeliveredLine, leg.LastCycleShortfall ? statusStyleYellow : detailStyle);

            DrawRouteBuiltFromNote(route);

            // Round-trip pairing note, only when linked (a fact in both modes; linking and
            // unlinking are Advanced).
            DrawRouteLinkNote(route);

            if (drawTuning)
            {
                DrawCadenceStepper(route, leg);
                DrawPriorityStepper(route);

                DrawFlightsUsedLine(route);
            }

            // The buttons take detail lines and never sit on an empty one. A block short of
            // lines (no next-run line or no source mission to name) gets one more
            // INFORMATION line - the delivered total, or "Not run yet." - before anything
            // else; only a block still short after that (Basic with neither, three slots
            // against Delivers + this line; Advanced always has its two steppers) gets a
            // bare button line, so every slot button is always reachable.
            int required = RouteDetailSlotCount(drawTuning);
            if (detailSlotNext < required)
                DetailLine(LogisticsRoutePresentation.FormatFillerInfoLine(leg.HasDeliveries, leg.CumulativeText));
            while (detailSlotNext < required)
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                DrawDetailSlotCell();
                GUILayout.EndHorizontal();
            }
            detailSlotRoute = null;
            GUILayout.EndVertical();
        }

        /// <summary>
        /// "Built from mission 'X'." for the route's source mission (its ORIGINAL mission's
        /// name, else the mission's tree name, else the short id), plus the manual-looping
        /// clause in Advanced. Skipped on a degenerate route with no source. Drawn only on
        /// expand, so the by-id lookups stay off the per-frame path of collapsed rows.
        /// </summary>
        private void DrawRouteBuiltFromNote(Route route)
        {
            string treeId = ResolveRouteSourceTreeId(route);
            if (string.IsNullOrEmpty(treeId))
                return;
            DetailLine(LogisticsRoutePresentation.FormatBuiltFromMission(
                ResolveMissionDisplayName(treeId), drawTuning));
        }

        /// <summary>
        /// A source tree's player-facing mission name: the ORIGINAL mission's name (clones
        /// share the tree), else the tree's TreeName, else its short id.
        /// </summary>
        private static string ResolveMissionDisplayName(string treeId)
        {
            return ResolveTreeDisplayName(treeId);
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
        /// Draws the M4 EndpointLost re-scan affordance. For a recoverable surface
        /// endpoint (<see cref="LogisticsDeliveryPresentation.ShouldOfferEndpointRescan"/>)
        /// it renders a "Re-scan for endpoint" button; otherwise a disabled-with-note
        /// label explaining why re-scan cannot help (orbital endpoints can only be
        /// matched by the baked PID). The button does its work DIRECTLY in the click
        /// branch (this is the synchronous draw path, not an async dialog callback, so
        /// the QW2 frame-reset-field trap does not apply and an inline action is
        /// correct): it calls <see cref="RouteEndpointResolver.TryResolveEndpoint"/>,
        /// logs the outcome (the click body is <see cref="LogisticsEndpointRescan.Run(Route, double)"/>,
        /// which the seam's <c>RouteCommand action=rescan</c> also runs), and on success hands the route to
        /// <see cref="RouteOrchestrator.TryRecoverEndpointLostAfterRescan"/>, then dirties
        /// the legibility cache so the destination cell + capacity context refresh next
        /// frame. That call restarts a loop route (whose tick skips a stopped route, so
        /// nothing else would) and only clears a non-loop route's retry gate (its
        /// dispatch evaluator re-resolves the endpoint and recovers the status itself).
        /// A re-scan miss just re-logs and leaves the route EndpointLost.
        /// A multi-stop route (GUI-P20) keeps the ONE button: it is offered when any
        /// stop is a recoverable surface endpoint, the click re-scans EVERY stop (the
        /// dispatch gate needs all of them to resolve), logs each stop's outcome on one
        /// line, and recovers the route only when every stop resolved.
        /// </summary>
        private void DrawEndpointRescan(Route route)
        {
            if (route?.Stops != null && route.Stops.Count > 1)
            {
                DrawMultiStopEndpointRescan(route);
                return;
            }
            RouteStop stop = route?.Stops != null && route.Stops.Count > 0 ? route.Stops[0] : null;
            if (stop == null)
                return;
            RouteEndpoint endpoint = stop.Endpoint;

            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);

            if (LogisticsDeliveryPresentation.ShouldOfferEndpointRescan(route.Status, endpoint))
            {
                if (GUILayout.Button(new GUIContent("Re-scan for endpoint",
                        "Search the destination body for a surface vessel near the recorded endpoint, then restart the route if found."),
                        GUILayout.Width(180f)))
                {
                    // The click body is shared with the seam's RouteCommand action=rescan.
                    LogisticsEndpointRescan.Run(route, TryGetCurrentUT());
                    // Refresh the destination cell + capacity context next frame.
                    lastLegibilityComputeRealtime = -1f;
                }
                // Push the Interact slot to the right edge (the label branch already expands).
                GUILayout.FlexibleSpace();
            }
            else
            {
                bool prevEnabled = GUI.enabled;
                GUI.enabled = false;
                GUILayout.Button(new GUIContent("Re-scan for endpoint",
                    LogisticsDeliveryPresentation.RescanIneligibleReason(endpoint)),
                    GUILayout.Width(180f));
                DisabledHoverEcho.CarryLastControl(
                    false, LogisticsDeliveryPresentation.RescanIneligibleReason(endpoint));
                GUI.enabled = prevEnabled;
                GUILayout.Label(LogisticsDeliveryPresentation.RescanIneligibleReason(endpoint),
                    detailStyle, GUILayout.ExpandWidth(true));
            }

            DrawDetailSlotCell();
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// The multi-stop form of <see cref="DrawEndpointRescan"/>: the same single
        /// button and the same disabled-with-note fallback, decided over every stop
        /// (<see cref="LogisticsDeliveryPresentation.ShouldOfferRouteEndpointRescan"/>).
        /// The click re-scans every non-null stop, logs one summary line naming each
        /// stop's outcome (<see cref="LogisticsDeliveryPresentation.FormatRescanOutcome"/>),
        /// and recovers the route only when every stop resolved, because the
        /// dispatch gate refuses the cycle while any stop is lost.
        /// </summary>
        private void DrawMultiStopEndpointRescan(Route route)
        {
            if (LogisticsDeliveryPresentation.CountStops(route.Stops) == 0)
                return;

            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);

            if (LogisticsDeliveryPresentation.ShouldOfferRouteEndpointRescan(route.Status, route.Stops))
            {
                if (GUILayout.Button(new GUIContent("Re-scan for endpoint",
                        "Search each stop's body for a surface vessel near its recorded endpoint, then restart the route if every stop is found."),
                        GUILayout.Width(180f)))
                {
                    // The click body is shared with the seam's RouteCommand action=rescan.
                    LogisticsEndpointRescan.Run(route, TryGetCurrentUT());
                    lastLegibilityComputeRealtime = -1f;
                }
                // Push the Interact slot to the right edge (the label branch already expands).
                GUILayout.FlexibleSpace();
            }
            else
            {
                string reason = LogisticsDeliveryPresentation.RouteRescanIneligibleReason(route.Stops);
                bool prevEnabled = GUI.enabled;
                GUI.enabled = false;
                GUILayout.Button(new GUIContent("Re-scan for endpoint", reason), GUILayout.Width(180f));
                DisabledHoverEcho.CarryLastControl(false, reason);
                GUI.enabled = prevEnabled;
                GUILayout.Label(reason, detailStyle, GUILayout.ExpandWidth(true));
            }

            DrawDetailSlotCell();
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Resolves the route's source tree id for the M5 ownership note: the first
        /// non-empty <c>SourceRefs[].TreeId</c>, falling back to
        /// <see cref="Route.BackingMissionTreeId"/>. Returns null when neither is set.
        /// </summary>
        internal static string ResolveRouteSourceTreeId(Route route)
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
        /// Resolves a tree's player-visible display name by id (M5): its original
        /// mission's current name, else the committed tree's root group name, else its
        /// TreeName (<see cref="MissionStore.DisplayNameForTree(string, string, string)"/>), else the short
        /// tree id, and "&lt;unknown mission&gt;" for no id, so the ownership note / toast
        /// never shows a null name. Reads CommittedTrees (NOT the grep-gated
        /// CommittedRecordings list), so it stays off the ERS/ELS gate.
        /// </summary>
        internal static string ResolveTreeDisplayName(string treeId)
        {
            if (string.IsNullOrEmpty(treeId))
                return "<unknown mission>";
            string treeName = null;
            string rootGroupName = null;
            var trees = RecordingStore.CommittedTrees;
            if (trees != null)
            {
                for (int i = 0; i < trees.Count; i++)
                {
                    RecordingTree t = trees[i];
                    if (t != null && string.Equals(t.Id, treeId, System.StringComparison.Ordinal))
                    {
                        treeName = t.TreeName;
                        rootGroupName = t.AutoGeneratedRootGroupName;
                        break;
                    }
                }
            }
            string name = MissionStore.DisplayNameForTree(treeId, treeName, rootGroupName);
            return !string.IsNullOrEmpty(name) ? name : ShortId(treeId);
        }

        /// <summary>How many slots carry a button: Rename, Delete and Update parts, plus Link
        /// in Advanced. Update parts is in both modes (it changes which parts take cargo, not
        /// when a route runs) and precedes Link, so a mode switch never moves it. Pure.</summary>
        internal static int RouteDetailSlotCount(bool showsTuning)
        {
            return showsTuning ? 4 : 3;
        }

        // The slot cell's width: the row's Interact column, less how far the detail box's
        // content sits inside the section box's content on the right - its padding, plus
        // only the part of its margin the section box's own padding does not already cover
        // (GUILayout overlaps a child's margin with its parent's padding). Measured: the
        // naive margin + padding put every slot button 4 px right of Activate / Pause.
        private float DetailSlotCellWidth()
        {
            float w = interactColumnWidth;
            if (GUI.skin != null && GUI.skin.box != null)
                w -= GUI.skin.box.padding.right
                    + Mathf.Max(0, GUI.skin.box.margin.right - GUI.skin.box.padding.right);
            return Mathf.Max(w, MissionsWindowUI.InteractCellInset + InteractSingleWidth);
        }

        /// <summary>
        /// Ends a route-detail line with its Interact slot: the next slot's button, or a
        /// same-width space. Nothing outside DrawRouteDetail.
        /// </summary>
        private void DrawDetailSlotCell()
        {
            if (detailSlotRoute == null)
                return;
            int slot = detailSlotNext++;
            GUILayout.BeginHorizontal(GUILayout.Width(DetailSlotCellWidth()));
            GUILayout.Space(MissionsWindowUI.InteractCellInset);
            DrawDetailSlotButton(detailSlotRoute, slot);
            GUILayout.EndHorizontal();
        }

        // A line with no slot (the rename field): the same-width space, no slot consumed.
        private void DrawDetailSlotSpacer()
        {
            GUILayout.Space(DetailSlotCellWidth());
        }

        private void DrawDetailSlotButton(Route route, int slot)
        {
            float w = InteractSingleWidth;
            switch (slot)
            {
                case RenameSlot:
                {
                    bool editing = string.Equals(renamingRouteId, route.Id, System.StringComparison.Ordinal);
                    const string renamingReason = "Renaming: Enter saves, Escape cancels";
                    bool prev = GUI.enabled;
                    GUI.enabled = !editing;
                    bool clicked = GUILayout.Button(new GUIContent("Rename", "Edit this route's name"),
                        detailSlotButtonStyle, GUILayout.Width(w));
                    DisabledHoverEcho.CarryLastControl(!editing, renamingReason);
                    GUI.enabled = prev;
                    if (clicked && !editing)
                    {
                        // Editing the interval and the name at once would cross-wire the two
                        // deferred commits; the interval edit-start already suppresses itself
                        // while a rename is active, so clear any pending interval edit first.
                        ClearIntervalEdit();
                        renamingRouteId = route.Id;
                        renamingRouteText = route.Name ?? string.Empty;
                        renamingRouteFocused = false;
                        renamingRouteRect = default;
                        ParsekLog.Verbose("UI",
                            $"Logistics: rename started route={ShortId(route.Id)} current='{route.Name}'");
                    }
                    return;
                }
                case DeleteSlot:
                    if (GUILayout.Button(new GUIContent("Delete", "Delete this route (asks first)."),
                            detailSlotButtonStyle, GUILayout.Width(w)))
                        pendingConfirmDeleteRoute = route;
                    return;
                case UpdatePartsSlot:
                {
                    // Re-captures every endpoint's current parts as its own (owner ruling
                    // 2026-10-07). Greyed with its reason while a run is under way: that run's
                    // cargo was gated against the part sets it dispatched with. The action
                    // logs the per-endpoint counts; the hover dates the last press.
                    string reason = LogisticsRoutePresentation.UpdatePartsDisabledReason(route);
                    bool live = string.IsNullOrEmpty(reason);
                    RouteLegibility leg = GetLegibility(route);
                    bool prev = GUI.enabled;
                    GUI.enabled = live;
                    bool clicked = GUILayout.Button(new GUIContent(
                            LogisticsRoutePresentation.UpdatePartsButtonLabel,
                            leg.UpdatePartsTooltip ?? LogisticsRoutePresentation.UpdatePartsTooltip),
                        detailSlotButtonStyle, GUILayout.Width(w));
                    DisabledHoverEcho.CarryLastControl(live, reason);
                    GUI.enabled = prev;
                    if (clicked && live)
                    {
                        RouteEndpointPartAdoption.AdoptionResult result =
                            RouteEndpointPartAdoption.AdoptCurrentParts(route);
                        ParsekLog.Verbose("UI",
                            $"Logistics: update parts button route={ShortId(route.Id)} " +
                            $"adopted={result.AdoptedCount.ToString(CultureInfo.InvariantCulture)}/" +
                            $"{result.Endpoints.Count.ToString(CultureInfo.InvariantCulture)}");
                        // Refresh the hover's date and the capacity line next frame.
                        lastLegibilityComputeRealtime = -1f;
                    }
                    return;
                }
                case LinkSlot:
                    if (!drawTuning)
                    {
                        GUILayout.Space(w);
                        return;
                    }
                    // Round-trip link control (Advanced). Unlinked: "Link round-trip..." arms
                    // the partner picker. Linked: "Unlink" breaks the pair inline, DIRECTLY in
                    // the click branch (synchronous draw path, so the async-callback trap does
                    // not apply). Either way the legibility cache is dirtied so the row and
                    // detail refresh next frame.
                    if (string.IsNullOrEmpty(route.LinkedRouteId))
                    {
                        if (GUILayout.Button(new GUIContent("Link round-trip...",
                                "Pair this route with another so they alternate: each runs only after its partner completes a run (a single reused transport flying out and back)."),
                                detailSlotButtonStyle, GUILayout.Width(w)))
                            OpenLinkPicker(route, ParsekUiScale.GuiPointToScaledRoot(Event.current.mousePosition));
                    }
                    else if (GUILayout.Button(new GUIContent("Unlink",
                                 "Break this route's round-trip pairing; both routes return to running on their own schedule."),
                                 detailSlotButtonStyle, GUILayout.Width(w)))
                    {
                        ParsekLog.Info("UI",
                            $"Logistics: unlink button route={ShortId(route.Id)} partner={ShortId(route.LinkedRouteId)}");
                        RouteStore.UnlinkRoute(route.Id);
                        lastLegibilityComputeRealtime = -1f;
                    }
                    return;
                default:
                    GUILayout.Space(w);
                    return;
            }
        }

        /// <summary>
        /// The deferred-commit name field, drawn as the first line of the detail block
        /// while this route is renamed (the RecordingsTableUI idiom). Enter commits here, a
        /// click outside commits in <see cref="HandleLogisticsDefocus"/>, Escape cancels.
        /// Editing is keyed by <see cref="Route.Id"/> (NOT a row index) because routes are
        /// re-sectioned / added / removed between frames; the committed name shows in the
        /// row at once because <see cref="DrawRouteRow"/> reads <see cref="Route.Name"/>
        /// live. The Delete confirm dialog it sits above is unchanged.
        /// </summary>
        private void DrawRouteRenameField(Route route)
        {
            const string controlName = "LogiRouteRename";
            bool submit = Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            bool cancel = Event.current.type == EventType.KeyDown
                && Event.current.keyCode == KeyCode.Escape;

            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label("Name:", detailStyle, GUILayout.Width(46f));
            GUI.SetNextControlName(controlName);
            renamingRouteText = GUILayout.TextField(renamingRouteText ?? string.Empty, GUILayout.ExpandWidth(true));
            renamingRouteRect = GUILayoutUtility.GetLastRect();
            DrawDetailSlotSpacer();
            GUILayout.EndHorizontal();

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
        /// The Advanced "Flights used:" line: the vessels of the flights the route copies,
        /// named as the Missions tab names them (one number per physical vessel, "X",
        /// "X [2]"; <see cref="MissionVesselNaming"/>), instead of the old "rec N of tree
        /// 'X'" clauses. Each id resolves through the literal-free
        /// <see cref="RecordingStore.TryResolveRecordingDisplayInfo(string, out string, out string, out int, out Recording, out RecordingTree)"/>
        /// accessor (the raw committed-list read stays in that already-allowlisted file, so
        /// the ERS/ELS grep gate stays green); an unresolved id falls back to its short id.
        /// The short ids ride the hover for debugging. Drawn only on expand in Advanced.
        /// </summary>
        private void DrawFlightsUsedLine(Route route)
        {
            (string text, string tooltip) = BuildFlightsUsedContent(route);
            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label(new GUIContent(text, tooltip), detailStyle, GUILayout.ExpandWidth(true));
            DrawDetailSlotCell();
            GUILayout.EndHorizontal();
        }

        private Dictionary<string, string> GetFlightsUsedVesselNames(RecordingTree tree)
        {
            if (tree == null)
                return null;
            int frame = Time.frameCount;
            if (frame != flightsUsedSignatureFrame)
            {
                flightsUsedSignature = MissionVesselNaming.Cache.MissionSignature(MissionStore.Missions);
                flightsUsedSignatureFrame = frame;
            }
            return flightsUsedNamingCache.GetOrBuild(
                tree, FlightsUsedStructure, RecordingStore.CommittedTrees,
                RecordingStore.StateVersion, flightsUsedSignature, FlightsUsedMissionName);
        }

        /// <summary>
        /// Builds the "Flights used:" text and its short-id hover. Logs one Verbose batch
        /// summary (resolved-vs-total) for the route, not per id.
        /// </summary>
        private (string text, string tooltip) BuildFlightsUsedContent(Route route)
        {
            return BuildFlightsUsedContent(route, GetFlightsUsedVesselNames);
        }

        /// <summary>
        /// The Flights used wiring with the per-tree vessel-name source injected (the window
        /// passes its cross-frame cache; a test passes <see cref="MissionVesselNaming.Build"/>):
        /// each id resolves through <see cref="RecordingStore.TryResolveRecordingDisplayInfo(string, out string, out string, out int, out Recording, out RecordingTree)"/>,
        /// its tree's map numbers the vessel and keys it, and
        /// <see cref="LogisticsRoutePresentation.FormatFlightsUsed"/> lists each vessel once.
        /// </summary>
        internal static (string text, string tooltip) BuildFlightsUsedContent(
            Route route, System.Func<RecordingTree, Dictionary<string, string>> vesselNamesOfTree)
        {
            if (route?.RecordingIds == null || route.RecordingIds.Count == 0)
                return (LogisticsRoutePresentation.FormatFlightsUsed(null, null), string.Empty);

            var names = new List<string>(route.RecordingIds.Count);
            var vesselKeys = new List<string>(route.RecordingIds.Count);
            var shortIds = new List<string>(route.RecordingIds.Count);
            int resolved = 0;
            for (int i = 0; i < route.RecordingIds.Count; i++)
            {
                string id = route.RecordingIds[i];
                bool ok = RecordingStore.TryResolveRecordingDisplayInfo(
                    id, out string recName, out _, out _, out _, out RecordingTree tree);
                string vesselKey = null;
                string name = null;
                if (ok)
                {
                    resolved++;
                    name = LogisticsRoutePresentation.ResolveFlightUsedName(
                        id, recName, tree?.Id,
                        tree != null && vesselNamesOfTree != null ? vesselNamesOfTree(tree) : null,
                        out vesselKey);
                }
                names.Add(name);
                vesselKeys.Add(vesselKey);
                shortIds.Add(ShortId(id));
            }

            // Built on every draw pass of an expanded Advanced route, so the summary is
            // rate-limited per route; a changed resolved count prints at once (it is in the key).
            ParsekLog.VerboseRateLimited("UI",
                "flights-used-" + (route.Id ?? "<no-id>") + "-" + resolved.ToString(CultureInfo.InvariantCulture),
                $"Logistics: flights used line route={ShortId(route.Id)} " +
                $"resolved={resolved.ToString(CultureInfo.InvariantCulture)}/{route.RecordingIds.Count.ToString(CultureInfo.InvariantCulture)}",
                30.0);
            return (LogisticsRoutePresentation.FormatFlightsUsed(names, shortIds, vesselKeys), string.Join(", ", shortIds));
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

            GUILayout.BeginHorizontal(GUILayout.Height(stepperRowHeight));
            GUILayout.Space(24f);
            GUILayout.Label(
                new GUIContent("Every:",
                    "How often the route runs, as a multiple of the run duration. 1x is the floor (the fastest the run allows); raise it to run less often."),
                detailStyle, GUILayout.Width(LogisticsRoutePresentation.StepperLabelWidth));

            // "-" decrements (no-op + greyed at the 1x floor).
            bool atFloor = n <= 1;
            const string cadenceAtFloorReason =
                "Already at the minimum (1x = the fastest the run allows)";
            GUI.enabled = !atFloor;
            bool cadenceDownClicked = GUILayout.Button(new GUIContent("-",
                    atFloor ? cadenceAtFloorReason : "Run more often"),
                    GUI.skin.button, GUILayout.Width(LogisticsRoutePresentation.StepperButtonWidth));
            DisabledHoverEcho.CarryLastControl(!atFloor, cadenceAtFloorReason);
            if (cadenceDownClicked)
            {
                pendingCadenceRoute = route;
                pendingCadenceMultiplier = RouteCadence.StepMultiplier(n, -1);
            }
            GUI.enabled = true;

            // Current N x + the resulting human cadence (windowed wording for a
            // windowed basis), in the value cell both steppers share.
            GUILayout.Label(
                windowed ? RouteWindowBasisPresentation.FormatWindowedCadence(n) : FormatCadence(route),
                detailStyle, GUILayout.Width(stepperValueWidth));

            if (GUILayout.Button(new GUIContent("+", "Run less often"), GUI.skin.button,
                    GUILayout.Width(LogisticsRoutePresentation.StepperButtonWidth)))
            {
                pendingCadenceRoute = route;
                pendingCadenceMultiplier = RouteCadence.StepMultiplier(n, +1);
            }

            GUILayout.FlexibleSpace();
            DrawDetailSlotCell();
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

            GUILayout.BeginHorizontal(GUILayout.Height(stepperRowHeight));
            GUILayout.Space(24f);
            GUILayout.Label(
                new GUIContent("Priority:",
                    "When several routes are due at once, the lower number runs first. 0 is the highest priority (and the default)."),
                detailStyle, GUILayout.Width(LogisticsRoutePresentation.StepperLabelWidth));

            // "-" decrements (no-op + greyed at the 0 floor).
            bool atFloor = p <= 0;
            const string priorityAtFloorReason = "Already at the highest priority (0)";
            GUI.enabled = !atFloor;
            bool priorityDownClicked = GUILayout.Button(new GUIContent("-",
                    atFloor ? priorityAtFloorReason : "Run earlier when routes are due at once"),
                    GUI.skin.button, GUILayout.Width(LogisticsRoutePresentation.StepperButtonWidth));
            DisabledHoverEcho.CarryLastControl(!atFloor, priorityAtFloorReason);
            if (priorityDownClicked)
            {
                pendingPriorityRoute = route;
                pendingPriorityValue = RoutePriority.Step(p, -1);
            }
            GUI.enabled = true;

            GUILayout.Label(p.ToString(CultureInfo.InvariantCulture), detailStyle, GUILayout.Width(stepperValueWidth));

            if (GUILayout.Button(new GUIContent("+", "Run later when routes are due at once"), GUI.skin.button,
                    GUILayout.Width(LogisticsRoutePresentation.StepperButtonWidth)))
            {
                pendingPriorityRoute = route;
                pendingPriorityValue = RoutePriority.Step(p, +1);
            }

            GUILayout.FlexibleSpace();
            DrawDetailSlotCell();
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// The expanded candidate: only what the row cannot say, the mission the
        /// candidate comes from.
        /// </summary>
        private void DrawCandidateDetail(RouteCandidate candidate)
        {
            GUILayout.BeginVertical(GUI.skin.box);

            string treeId = candidate.Tree?.Id;
            string missionName = !string.IsNullOrEmpty(treeId)
                ? ResolveMissionDisplayName(treeId)
                : candidate.Tree?.TreeName;
            DetailLine(LogisticsRoutePresentation.FormatBuiltFromMission(missionName, false));
            GUILayout.EndVertical();
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
            DrawDetailSlotCell();
            GUILayout.EndHorizontal();
        }
    }
}
