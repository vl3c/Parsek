using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The Link picker window: opening it and drawing its frame and contents.
    internal partial class LogisticsWindowUI
    {
        /// <summary>
        /// Arms the M4c round-trip link picker for <paramref name="source"/> (the
        /// route whose "Link round-trip..." button was clicked). Sets the persistent
        /// popup state read by <see cref="DrawLinkPicker"/>; the popup is drawn from
        /// <see cref="DrawIfOpen"/> after the main window. Does NOT itself mutate the
        /// store — the partner is chosen + committed in the popup.
        /// <paramref name="clickScreenPoint"/> is the click in SCREEN space (the picker
        /// opens next to it), or <see cref="PickerWindowLayout.NoClickPoint"/> (the picker
        /// centres over this window).
        /// </summary>
        private void OpenLinkPicker(Route source, Vector2 clickScreenPoint)
        {
            if (source == null || string.IsNullOrEmpty(source.Id))
                return;
            linkPickerOpen = true;
            linkPickerSourceRouteId = source.Id;
            linkPickerSourceName = source.Name ?? "<unnamed>";
            linkPickerSelectedId = null;
            linkPickerPosition = clickScreenPoint;
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

            // Place the first-open rect BEFORE the resize/screen fit: the fit would widen an
            // unplaced zero rect to the minimum width at the screen origin, and the placement
            // would then never run (PickerWindowLayout).
            if (linkPickerRect.width < 1f)
            {
                linkPickerRect = PickerWindowLayout.PlaceOnOpen(
                    linkPickerPosition, windowRect,
                    LinkPickerDefaultW, LinkPickerDefaultH,
                    ParsekUiScale.ScaledScreenWidth, ParsekUiScale.ScaledScreenHeight);
                ParsekLog.Verbose("UI", PickerWindowLayout.FormatPlacementLog(
                    "Logistics link picker", linkPickerPosition, linkPickerRect,
                    ParsekUiScale.ScaledScreenWidth, ParsekUiScale.ScaledScreenHeight));
            }

            ParsekUI.HandleResizeDrag(ref linkPickerRect, ref linkPickerResizing,
                LinkPickerMinW, LinkPickerMinH, null);

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                linkPickerRect = ParsekUiScale.Window(
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
            // The house picker look (PickerWindowLayout): a heading in the shared table
            // section style below the main windows' title gap, then the entries inside the
            // shared dark table body box.
            PickerWindowLayout.DrawTitleGap();
            GUILayout.Label($"Link '{linkPickerSourceName}' with:", parentUI.GetTableSectionHeaderStyle());

            List<LogisticsLinkPresentation.LinkCandidate> candidates =
                LogisticsLinkPresentation.BuildLinkCandidates(RouteStore.CommittedRoutes, linkPickerSourceRouteId);

            linkPickerScroll = PickerWindowLayout.BeginEntryList(parentUI, linkPickerScroll);
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
                    PickerWindowLayout.BeginEntryRow();
                    bool now = GUILayout.Toggle(selected, "  " + c.Name);
                    GUILayout.EndHorizontal();
                    if (now && !selected)
                        linkPickerSelectedId = c.Id;
                    else if (!now && selected)
                        linkPickerSelectedId = null;
                }
            }
            PickerWindowLayout.EndEntryList();

            GUILayout.Space(3);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            bool prevEnabled = GUI.enabled;
            bool linkEnabled = !string.IsNullOrEmpty(linkPickerSelectedId);
            GUI.enabled = linkEnabled;
            bool linkClicked = GUILayout.Button(new GUIContent("Link",
                    "Pair these two routes as a round-trip: they alternate, each running only after its partner completes a run."),
                    GUILayout.Width(PickerWindowLayout.ButtonWidth));
            DisabledHoverEcho.CarryLastControl(linkEnabled, LinkButtonDisabledReason(linkPickerSelectedId));
            if (linkClicked)
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

            GUILayout.Space(PickerWindowLayout.ButtonGap);
            if (GUILayout.Button("Cancel", GUILayout.Width(PickerWindowLayout.ButtonWidth)))
            {
                ParsekLog.Verbose("UI", $"Logistics: link picker cancelled source={ShortId(linkPickerSourceRouteId)}");
                linkPickerOpen = false;
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            ParsekUI.DrawResizeHandle(linkPickerRect, ref linkPickerResizing, null);
            GUI.DragWindow();
        }
    }
}
