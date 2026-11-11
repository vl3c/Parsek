using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The Logistics window's part of the MAIN Parsek window: the Supply Route candidate
    // banner and the Logistics launcher, drawn by ParsekUI.DrawWindow between the Missions
    // and Kerbals launchers. It lives here because the window owns the route prompt and the
    // route store reads it draws. Its tooltips echo in the MAIN window's strip, so
    // TooltipEchoBudgetTests budgets this file at the main window's width, not this window's.
    internal partial class LogisticsWindowUI
    {
        public void DrawMainWindowSection(UiComplexityMode complexity)
        {
            // --- M6 Record-Supply-Run helper banner ---
            // Non-blocking prompt armed by RouteRunPrompt at commit time when an
            // eligible route candidate landed. Drawn here (the main window)
            // rather than as a modal so it never interrupts gameplay - the
            // commit-time ScreenMessage announces it while the window is
            // closed, and this banner carries the Open / Dismiss actions.
            if (RouteRunPrompt.HasPendingPrompt)
            {
                // Staleness guard (cheap id-membership only): dismissed
                // elsewhere (Logistics window row button) drops the banner.
                // Cleared ONLY on the Layout event so the same frame's Repaint
                // pass sees the identical control count (IMGUI stability).
                if (Event.current.type == EventType.Layout
                    && RouteStore.IsCandidateDismissed(RouteRunPrompt.PendingPromptTreeId))
                {
                    RouteRunPrompt.ClearPendingPrompt("dismissed-elsewhere");
                }
                if (RouteRunPrompt.HasPendingPrompt)
                {
                    GUILayout.Label(
                        $"Supply Route candidate:\n'{RouteRunPrompt.PendingPromptLabel ?? "<unnamed>"}'",
                        GUI.skin.box, GUILayout.ExpandWidth(true));
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Open Logistics"))
                    {
                        IsOpen = true;
                        ParsekLog.Info("UI",
                            $"RouteRunPrompt banner: Open Logistics clicked tree={RouteRunPrompt.PendingPromptTreeId}");
                        RouteRunPrompt.ClearPendingPrompt("opened-logistics");
                    }
                    if (GUILayout.Button(new GUIContent(
                        "Dismiss",
                        "Drops the suggestion; undo it in Logistics > Dismissed.")))
                    {
                        // Dismiss here also dismisses the candidate (the tree
                        // leaves the Logistics Candidates section; reversible
                        // from its "Dismissed (N)" subsection).
                        RouteStore.DismissCandidateTree(
                            RouteRunPrompt.PendingPromptTreeId, RouteRunPrompt.PendingPromptLabel);
                        RouteRunPrompt.ClearPendingPrompt("dismissed-from-banner");
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Space(ParsekUI.SpacingSmall);
                }
            }

            // --- Logistics (v0, available in both Flight and KSC) ---
            // Grouped with Timeline + Recordings as the primary navigation set.
            // The whole button tints red when any route is hard-broken (EndpointLost /
            // MissingSourceRecording / SourceChanged) so a problem is visible without
            // opening the window. The label is just "Logistics" (no live count); the
            // route count is still computed for the broken-tint diagnostic log only.
            IReadOnlyList<Route> logisticsRoutes = RouteStore.CommittedRoutes;
            int logisticsRouteCount = logisticsRoutes != null ? logisticsRoutes.Count : 0;
            bool anyLogisticsBroken = LogisticsButtonState.AnyRouteHardBroken(
                logisticsRoutes != null
                    ? logisticsRoutes.Select(r => r.Status)
                    : Enumerable.Empty<RouteStatus>());
            if (anyLogisticsBroken)
            {
                ParsekLog.VerboseRateLimited("UI", "logistics-button-broken-tint",
                    string.Format(CultureInfo.InvariantCulture,
                        "Logistics button broken-state tint applied (routeCount={0})",
                        logisticsRouteCount));
            }
            Color prevLogisticsColor = GUI.color;
            if (anyLogisticsBroken)
                GUI.color = ParsekUI.StatusColor(ParsekUI.StatusColorKind.Red);
            else if (RouteRunPrompt.HasPendingPrompt)
                // M6 Record-Supply-Run helper: cyan highlight while the banner
                // above is pending (broken-red wins - an error outranks a hint).
                GUI.color = ParsekUI.StatusColor(ParsekUI.StatusColorKind.Cyan);
            try
            {
                if (UiSurfaceVisibility.IsVisible(UiSurface.MainButtonLogistics, complexity)
                    && GUILayout.Button(new GUIContent(
                        "Logistics",
                        "Supply routes that repeat a delivery you already flew.")))
                {
                    IsOpen = !IsOpen;
                    ParsekLog.Verbose("UI",
                        $"Logistics window toggled: {(IsOpen ? "open" : "closed")}");
                }
            }
            finally
            {
                GUI.color = prevLogisticsColor;
            }
        }
    }
}
