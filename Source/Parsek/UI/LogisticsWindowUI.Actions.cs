using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Deferred actions and confirmations: the pending action queue, the delete and delete-dormant confirmations, route creation from a candidate and its interval.
    internal partial class LogisticsWindowUI
    {
        private void ApplyPendingActions(double currentUT)
        {
            // These four mutate route state HERE (status / cadence), which changes the
            // legibility cache inputs (the countdown and the merged Status cell). Force a recompute next
            // frame so a player action refreshes the cells immediately instead of
            // waiting out the ~1s timer. Create and Delete are deliberately NOT listed:
            // in this method they only SPAWN their confirm dialogs, and the actual
            // mutation (plus its cache invalidation) happens later in the dialog
            // callbacks (Create dirties both caches on a successful build; a Delete just
            // removes the route, whose stale cache entry is never drawn again). Listing
            // them here would force a wasted full recompute on every dialog open / Cancel.
            bool routeStateMutated =
                pendingPause != null || pendingCancelSend != null
                || pendingActivate != null || pendingSendOnce != null
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
            if (pendingCancelSend != null)
            {
                bool ok = RouteOrchestrator.TryCancelSendOnce(pendingCancelSend);
                if (ok && !string.IsNullOrEmpty(pendingCancelSend.Id))
                    sendOnceArmedRouteIds.Remove(pendingCancelSend.Id);
                ParsekLog.Info("UI", $"Logistics: Cancel Send route={ShortId(pendingCancelSend.Id)} result={(ok ? "cancelled" : "rejected")}");
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
            if (pendingConfirmDeleteDormantRoute != null)
            {
                // Same one-shot confirm shape as the committed-route delete;
                // the actual RouteStore.RemoveDormantRoute runs in the dialog's
                // Delete callback, never here. No cache dirtying needed: dormant
                // routes feed no legibility / candidate cache.
                SpawnDeleteDormantRouteConfirmation(pendingConfirmDeleteDormantRoute);
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
                // input (no countdown / status / cost change), so forcing a recompute
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

            if (pendingSectionToggle != null)
            {
                ApplySectionToggle(pendingSectionToggle);
                pendingSectionToggle = null;
            }

            if (routeStateMutated)
                lastLegibilityComputeRealtime = -1f;

            pendingPause = null;
            pendingCancelSend = null;
            pendingActivate = null;
            pendingSendOnce = null;
            pendingConfirmDeleteRoute = null;
            pendingConfirmDeleteDormantRoute = null;
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
        /// Spawns the modal "Delete route '...'? This cannot be undone." confirm.
        /// The Delete button calls <see cref="RouteStore.RemoveRoute"/> directly in
        /// its callback, performing the destructive action in-callback; this is
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
        /// Spawns the modal delete confirm for a DORMANT route (mirrors
        /// <see cref="SpawnDeleteRouteConfirmation"/>: id captured into a local so
        /// the closure survives the frame-top field reset; the destructive
        /// <see cref="RouteStore.RemoveDormantRoute"/> runs in the Delete callback
        /// only, so deletion never happens without an explicit confirm).
        /// </summary>
        private void SpawnDeleteDormantRouteConfirmation(Route route)
        {
            if (route == null)
                return;

            string routeId = route.Id;
            string display = LogisticsDormantPresentation.DormantRouteDisplayName(route.Name, route.Id);
            string body = LogisticsDormantPresentation.BuildDeleteDormantConfirmBody(display);

            ParsekLog.Info("UI",
                $"Logistics: Delete dormant route={ShortId(routeId)} confirm dialog spawned");

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekLogisticsDeleteDormantRouteConfirm",
                    body,
                    "Confirm: Delete Dormant Route",
                    HighLogic.UISkin,
                    new DialogGUIButton("Delete", () =>
                    {
                        bool ok = RouteStore.RemoveDormantRoute(routeId);
                        ParsekLog.Info("UI",
                            $"Logistics: Delete dormant route={ShortId(routeId)} confirmed result={(ok ? "removed" : "not-found")}");
                        // Delete-after-materialize race: the route can leave the
                        // dormant list between dialog spawn and Confirm (the
                        // orchestrator tick reached its CreatedUT). Tell the
                        // player where it went instead of silently doing nothing;
                        // the normal section's own Delete handles it from here.
                        if (!ok && RouteStore.TryGetRoute(routeId, out _))
                        {
                            ParsekLog.Info("UI",
                                $"Logistics: dormant route={ShortId(routeId)} re-materialized before " +
                                "delete confirm; directing player to the routes section");
                            try
                            {
                                ScreenMessages.PostScreenMessage(
                                    $"[Parsek] Supply route '{display}' re-materialized while the " +
                                    "confirmation was open. Delete it from its routes section instead.", 6f);
                            }
                            catch (System.Exception ex)
                            {
                                ParsekLog.Verbose("UI",
                                    $"Logistics: screen message unavailable ({ex.GetType().Name})");
                            }
                        }
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Verbose("UI",
                            $"Logistics: Delete dormant route={ShortId(routeId)} cancelled");
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
            // A PopupDialog body: the stock TMP font has no route-name arrow glyph.
            return StockFontText.ForStockUi($"Delete route '{name}'?\n\nThis cannot be undone.");
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
        /// legibility cache (the new route's cells appear immediately). This is now the
        /// ONLY route-creation confirm in the mod: the post-commit auto-dialog that used to
        /// share the geometry was deleted 2026-09-11 as unreachable (GUI census D4), leaving
        /// <see cref="RouteCreationDialog"/> as a pure span helper.
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
            // Same summary call the post-commit auto-dialog makes (analysis +
            // tree); passing cand.Tree makes the Transit line resolve the real
            // [root..dock] span so the dialog matches the route that gets built.
            candidateDisplayCache.TryGetValue(cand.Tree.Id ?? "<no-tree>", out CandidateDisplay display);
            string body = StockFontText.ForStockUi(RouteCreationFormatters.BuildSummaryBlock(cand.Analysis, cand.Tree,
                StructureListWindowUI.ResolvePartTitle, display?.DestinationName));

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
        /// <see cref="RouteCreationService.CreatePausedFromCandidate"/> funnel and
        /// returns the built <see cref="Route"/> (or null on a null candidate or a
        /// builder reject). The H6 confirm callbacks call this to build, then the
        /// "Create and Activate" branch additionally calls
        /// <see cref="RouteOrchestrator.TryActivate"/> on the returned non-null
        /// route; returning the route keeps a single AddRoute +
        /// manual-loop-clear funnel and guarantees the window-created route's
        /// interval / geometry stays identical to today.
        ///
        /// <para>The build + store + manual-loop-clear sequence itself lives in
        /// <see cref="RouteCreationService"/> so the <c>RouteCommand action=create</c>
        /// seam verb drives the SAME production path a player click does rather than
        /// re-deriving it. What stays here is presentation: the interval resolve, the
        /// one-shot "manual loop turned off" toast, and this window's own grep-stable
        /// Info lines.</para>
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

            RouteCreationService.RouteCreateOutcome outcome =
                RouteCreationService.CreatePausedFromCandidate(
                    candidate,
                    name: null, // RouteBuilder generates a default name
                    intervalSeconds: interval,
                    mode: mode,
                    currentUT: TryGetCurrentUT());

            if (outcome.Route != null)
            {
                // M5: a manual loop was actually turned off by this create, so tell the
                // player with a one-shot toast (and the always-visible detail note). The
                // toast fires ONLY when something was cleared, never on every create.
                // Done DIRECTLY here in the synchronous create path (no PopupDialog
                // callback / frame-reset field), so the QW2 trap does not apply.
                if (LogisticsCreatePresentation.ShouldToastManualLoopCleared(outcome.ManualLoopsCleared))
                {
                    // The ORIGINAL mission's name (clones share the tree), else the tree's name.
                    string sourceTreeId = ResolveRouteSourceTreeId(outcome.Route);
                    string missionName = ResolveMissionDisplayName(sourceTreeId);
                    string toast = LogisticsCreatePresentation.FormatMissionNowRepeatsOnRouteToast(missionName);
                    ParsekLog.ScreenMessage(toast, 5f);
                    ParsekLog.Info("UI",
                        $"Logistics: manual loop turned off by create route={ShortId(outcome.Route.Id)} tree={ShortId(sourceTreeId)} mission='{missionName}' cleared={outcome.ManualLoopsCleared.ToString(CultureInfo.InvariantCulture)} (toast posted)");
                }
                // A partner mission whose included partner journey reached the route's tree lost
                // its loop too; name it, not the route's own mission (that one did not change).
                if (outcome.PartnerMissionsCleared != null)
                {
                    for (int pi = 0; pi < outcome.PartnerMissionsCleared.Count; pi++)
                    {
                        string partnerName = outcome.PartnerMissionsCleared[pi];
                        ParsekLog.ScreenMessage(
                            LogisticsCreatePresentation.FormatMissionNowRepeatsOnRouteToast(partnerName), 5f);
                        ParsekLog.Info("UI",
                            $"Logistics: partner mission loop turned off by create route={ShortId(outcome.Route.Id)} mission='{partnerName}' (toast posted)");
                    }
                }
                ParsekLog.Info("UI",
                    $"Logistics: Create Route from candidate tree={ShortId(candidate.Tree.Id)} -> route={ShortId(outcome.Route.Id)} name='{outcome.Route.Name}' (Paused, interval={interval.ToString("R", CultureInfo.InvariantCulture)}s)");
                return outcome.Route;
            }

            ParsekLog.Info("UI",
                $"Logistics: Create Route rejected tree={ShortId(candidate.Tree.Id)} reason={outcome.RejectReason ?? "<none>"}");
            return null;
        }
    }
}
