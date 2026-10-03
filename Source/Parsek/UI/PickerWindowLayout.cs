using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Shared size, placement and table chrome for Parsek's small selector windows: the
    /// group picker (<c>Manage Groups</c> / <c>Set Parent Group</c>,
    /// <see cref="GroupPickerUI"/>) and the Logistics round-trip link picker
    /// (<c>Link round-trip partner</c>, <see cref="LogisticsWindowUI"/>).
    ///
    /// <para><b>Look.</b> A picker draws like the main windows' tables: the main windows'
    /// gap under the title bar (<see cref="DrawTitleGap"/>), then an optional heading
    /// in the shared table section-header style, then its entries inside the shared dark
    /// table body box (<see cref="ParsekUI.GetTableBodyBoxStyle"/>) holding a scroll view on
    /// the shared table scroll style, one row per entry at the Missions tab's expanded
    /// sub-row spacing (<see cref="BeginEntryRow"/>), entry text in the shared table cell
    /// style, and buttons at the Missions Interact widths (<see cref="ButtonWidth"/> single,
    /// <see cref="SmallButtonWidth"/> for the one-glyph "+").</para>
    ///
    /// <para><b>Placement.</b> A picker opened by a click opens next to the clicked control
    /// (<see cref="PlaceNextToClick"/>); one opened with no click point (the automation
    /// seam) opens centred over its parent window (<see cref="CenterOver"/>). Either way the
    /// whole rect is clamped onto the screen (<see cref="ClampOnScreen"/>). The first-open
    /// rect is placed BEFORE <see cref="ParsekUI.HandleResizeDrag"/> runs: that call fits
    /// an unplaced zero-width rect to the window's minimum width at the screen origin, which
    /// is how both pickers used to open shrunk into the screen's top-left corner.</para>
    ///
    /// <para>The placement rules are pure (screen size passed in, no IMGUI or Screen reads)
    /// so they are unit-tested headless (<c>PickerWindowLayoutTests</c>).</para>
    /// </summary>
    internal static class PickerWindowLayout
    {
        /// <summary>A single picker button (OK, Cancel, Link): the Missions Interact single.</summary>
        internal const float ButtonWidth = MissionsWindowUI.InteractButtonWidth;

        /// <summary>The gap between two picker buttons: the Missions Interact gap.</summary>
        internal const float ButtonGap = MissionsWindowUI.InteractButtonGap;

        /// <summary>A one-glyph picker button (the new-group "+"): the Missions Interact pair
        /// half.</summary>
        internal const float SmallButtonWidth = MissionsWindowUI.InteractPairButtonWidth;

        /// <summary>Floor on each entry row's height: the Missions tab's expanded sub-row
        /// (vessel / segment row) floor.</summary>
        internal const float RowMinHeight = MissionsWindowUI.CompositionRowMinHeight;

        /// <summary>How far from the click point a click-opened picker's nearest edge
        /// sits, so the picker does not open under the cursor.</summary>
        internal const float ClickOffsetPx = 8f;

        /// <summary>The "no click point" an opener passes when nothing was clicked (the
        /// automation seam): the picker then centres over its parent window.</summary>
        internal static readonly Vector2 NoClickPoint = new Vector2(float.NaN, float.NaN);

        /// <summary>Whether <paramref name="point"/> is a real click point rather than
        /// <see cref="NoClickPoint"/> (or any other non-finite value).</summary>
        internal static bool HasClickPoint(Vector2 point)
        {
            return !float.IsNaN(point.x) && !float.IsNaN(point.y)
                && !float.IsInfinity(point.x) && !float.IsInfinity(point.y);
        }

        /// <summary>
        /// Moves <paramref name="rect"/> (never resizes it) so it lies fully on a screen of
        /// the given size; a rect larger than the screen on an axis is pinned to that axis's
        /// origin. A non-positive screen size (headless, or a failed Screen read) leaves the
        /// rect unchanged.
        /// </summary>
        internal static Rect ClampOnScreen(Rect rect, float screenWidth, float screenHeight)
        {
            if (screenWidth <= 0f || screenHeight <= 0f) return rect;
            float maxX = screenWidth - rect.width;
            float maxY = screenHeight - rect.height;
            if (maxX < 0f) maxX = 0f;
            if (maxY < 0f) maxY = 0f;
            if (rect.x > maxX) rect.x = maxX;
            if (rect.y > maxY) rect.y = maxY;
            if (rect.x < 0f) rect.x = 0f;
            if (rect.y < 0f) rect.y = 0f;
            return rect;
        }

        /// <summary>
        /// A picker of the given size opened by a click at <paramref name="click"/>
        /// (screen space): to the RIGHT of the click, its top level with the click, or to
        /// the LEFT when the right side has no room for it (a control near the screen's
        /// right edge, like the Logistics Interact column); then clamped on screen.
        /// </summary>
        internal static Rect PlaceNextToClick(Vector2 click, float width, float height,
            float screenWidth, float screenHeight)
        {
            float x = click.x + ClickOffsetPx;
            if (screenWidth > 0f && x + width > screenWidth)
                x = click.x - ClickOffsetPx - width;
            float y = click.y - ClickOffsetPx;
            return ClampOnScreen(new Rect(x, y, width, height), screenWidth, screenHeight);
        }

        /// <summary>
        /// A picker of the given size centred over <paramref name="parent"/>, clamped on
        /// screen. A parent with no size (not yet drawn) centres the picker on the screen.
        /// </summary>
        internal static Rect CenterOver(Rect parent, float width, float height,
            float screenWidth, float screenHeight)
        {
            float cx, cy;
            if (parent.width >= 1f && parent.height >= 1f)
            {
                cx = parent.x + parent.width * 0.5f;
                cy = parent.y + parent.height * 0.5f;
            }
            else
            {
                cx = screenWidth * 0.5f;
                cy = screenHeight * 0.5f;
            }
            return ClampOnScreen(
                new Rect(cx - width * 0.5f, cy - height * 0.5f, width, height),
                screenWidth, screenHeight);
        }

        /// <summary>
        /// The first-open rect of a picker: next to <paramref name="click"/> when the opener
        /// had one (<see cref="HasClickPoint"/>), else centred over
        /// <paramref name="parent"/>.
        /// </summary>
        internal static Rect PlaceOnOpen(Vector2 click, Rect parent, float width, float height,
            float screenWidth, float screenHeight)
        {
            return HasClickPoint(click)
                ? PlaceNextToClick(click, width, height, screenWidth, screenHeight)
                : CenterOver(parent, width, height, screenWidth, screenHeight);
        }

        /// <summary>The one-shot placement log line. Invariant-culture, since a test
        /// reads it.</summary>
        internal static string FormatPlacementLog(string pickerName, Vector2 click, Rect placed,
            float screenWidth, float screenHeight)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            string anchor = HasClickPoint(click)
                ? string.Format(ic, "next to click ({0:F0},{1:F0})", click.x, click.y)
                : "centred over its parent window";
            return string.Format(ic,
                "{0} placed {1}: x={2:F0} y={3:F0} w={4:F0} h={5:F0} screen={6:F0}x{7:F0}",
                pickerName ?? "Picker", anchor, placed.x, placed.y, placed.width, placed.height,
                screenWidth, screenHeight);
        }

        /// <summary>
        /// The gap between a picker's title bar and its heading box: the same
        /// <see cref="ParsekUI.WindowContentTopGapPx"/> the main windows leave above their
        /// first box or row. Drawn first in every picker's window body.
        /// </summary>
        internal static void DrawTitleGap()
        {
            GUILayout.Space(ParsekUI.WindowContentTopGapPx);
        }

        /// <summary>
        /// Opens a picker's entry list: the shared dark table body box, then a scroll view on
        /// the shared table scroll style inside it. Pair with <see cref="EndEntryList"/>.
        /// </summary>
        internal static Vector2 BeginEntryList(ParsekUI ui, Vector2 scroll)
        {
            GUILayout.BeginVertical(ui.GetTableBodyBoxStyle(), GUILayout.ExpandHeight(true));
            return GUILayout.BeginScrollView(scroll, false, false,
                GUI.skin.horizontalScrollbar, GUI.skin.verticalScrollbar,
                ui.GetTableScrollViewStyle(), GUILayout.ExpandHeight(true));
        }

        /// <summary>Closes what <see cref="BeginEntryList"/> opened, scroll view then box.</summary>
        internal static void EndEntryList()
        {
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        /// <summary>Opens one entry row the way the Missions tab opens its expanded vessel /
        /// segment sub-rows: a style-less horizontal group at <see cref="RowMinHeight"/>, so
        /// it adds none of the shared table row style's 4 px vertical margin and the rows
        /// keep the Missions sub-rows' tighter spacing. Pair with
        /// <c>GUILayout.EndHorizontal()</c>.</summary>
        internal static void BeginEntryRow()
        {
            GUILayout.BeginHorizontal(GUILayout.MinHeight(RowMinHeight));
        }
    }
}
