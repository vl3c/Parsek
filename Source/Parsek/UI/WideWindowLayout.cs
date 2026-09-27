using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Pure screen-fit and horizontal-scroll arithmetic shared by every resizable Parsek
    /// window. No IMGUI calls, no Screen reads: callers pass the screen size in, so every
    /// rule here is unit-testable headless (<c>WideWindowLayoutTests</c>).
    ///
    /// <para><b>The problem it answers.</b> Two windows are laid out wider than a
    /// 1280 px game window: the Missions window (both tabs, 1355 px minimum) and Logistics
    /// (1410 px minimum). Before this helper nothing capped a window to the screen, so on a
    /// 1280x720 screen their right-hand columns were drawn off-screen and could not be
    /// reached at all. The fix has two halves: <see cref="FitToScreen"/> caps a window
    /// that cannot fit the screen to its width and moves it onto it (a window that fits
    /// is never touched), and a window whose content is
    /// designed wider than the width it now has scrolls that content horizontally
    /// (<see cref="WideWindowScroll"/>).</para>
    /// </summary>
    internal static class WideWindowLayout
    {
        /// <summary>A width within this of the natural width counts as fitting, so float
        /// noise in a resolved GUILayout rect never flips the scroll decision.</summary>
        internal const float FitTolerancePx = 0.5f;

        /// <summary>
        /// The resize floor a window can actually have on this screen: its own minimum, or
        /// the screen width when the screen is narrower than that minimum. A screen size of
        /// zero or less (headless, or a Screen read that failed) leaves the minimum as is.
        /// </summary>
        internal static float EffectiveMinWidth(float minWidth, float screenWidth)
        {
            if (screenWidth <= 0f) return minWidth;
            return minWidth > screenWidth ? screenWidth : minWidth;
        }

        /// <summary>
        /// The width a resize drag produces: the mouse's distance from the window's left
        /// edge, never below <see cref="EffectiveMinWidth"/> and never wider than the
        /// screen. On a screen the window fits this is the drag it always was (a window
        /// may still be dragged past the screen edge); only a width that would exceed the
        /// screen, or a minimum the screen cannot hold, is limited.
        /// </summary>
        internal static float ResizeDragWidth(float mouseX, float rectX, float minWidth,
            float screenWidth)
        {
            float width = mouseX - rectX;
            float floor = EffectiveMinWidth(minWidth, screenWidth);
            if (width < floor) width = floor;
            if (screenWidth > 0f && width > screenWidth) width = screenWidth;
            return width;
        }

        /// <summary>
        /// Whether a window must be fitted to the screen at all: only when it cannot fit
        /// at its own width (wider than the screen, or a minimum wider than the screen),
        /// or when an earlier narrow screen capped it below its minimum and the screen now
        /// allows that minimum back. A width below the minimum has no other source: every
        /// first-open default is at or above its window's minimum, a resize drag floors at
        /// it, and the automation seam's <c>op=rect</c> raises to it. On a screen the
        /// window fits, the answer is false and the player's placement is left alone,
        /// including a window parked partly off-screen.
        /// </summary>
        internal static bool NeedsScreenFit(Rect rect, float minWidth, float screenWidth)
        {
            if (screenWidth <= 0f) return false;
            if (rect.width > screenWidth || minWidth > screenWidth) return true;
            return minWidth > 0f && rect.width < minWidth;
        }

        /// <summary>
        /// Fits a window rect to the screen when <see cref="NeedsScreenFit"/> says it must:
        /// the width is raised to <see cref="EffectiveMinWidth"/> (the grow-back of a window
        /// an earlier narrow screen capped) and capped at the screen width, then the window
        /// is moved, not resized, so it lies fully on-screen horizontally and its top-left
        /// corner is on-screen with as much of its height as fits. Height is never changed:
        /// a GUILayout window resolves its own height from its content. A window that fits
        /// the screen at its own width is left untouched. Returns true when the rect
        /// changed.
        /// </summary>
        internal static bool FitToScreen(ref Rect rect, float minWidth, float screenWidth,
            float screenHeight)
        {
            if (screenWidth <= 0f || screenHeight <= 0f) return false;
            if (!NeedsScreenFit(rect, minWidth, screenWidth)) return false;

            Rect before = rect;
            float floor = EffectiveMinWidth(minWidth, screenWidth);
            if (floor > 0f && rect.width < floor) rect.width = floor;
            if (rect.width > screenWidth) rect.width = screenWidth;

            float maxX = screenWidth - rect.width;
            if (rect.x > maxX) rect.x = maxX;
            if (rect.x < 0f) rect.x = 0f;

            float maxY = screenHeight - rect.height;
            if (maxY < 0f) maxY = 0f;
            if (rect.y > maxY) rect.y = maxY;
            if (rect.y < 0f) rect.y = 0f;

            return rect.x != before.x || rect.y != before.y || rect.width != before.width;
        }

        /// <summary>
        /// Whether a window's content must scroll horizontally: true when the window is
        /// narrower than the width its content is laid out for. A window at or above its
        /// natural width draws exactly as it always did.
        /// </summary>
        internal static bool NeedsHorizontalScroll(float windowWidth, float naturalWidth)
        {
            if (naturalWidth <= 0f) return false;
            return windowWidth < naturalWidth - FitTolerancePx;
        }

        /// <summary>
        /// The largest horizontal scroll offset a view of <paramref name="viewWidth"/> over
        /// content of <paramref name="contentWidth"/> can take (zero when the content fits).
        /// </summary>
        internal static float MaxScrollX(float contentWidth, float viewWidth)
        {
            float max = contentWidth - viewWidth;
            return max > 0f ? max : 0f;
        }

        /// <summary>
        /// Clamps a horizontal scroll offset into [0, <see cref="MaxScrollX"/>]. Used when
        /// the window grows (the old offset may now point past the content) and on the
        /// automation seam's read-back. NaN clamps to zero.
        /// </summary>
        internal static float ClampScrollX(float scrollX, float contentWidth, float viewWidth)
        {
            if (float.IsNaN(scrollX) || scrollX < 0f) return 0f;
            float max = MaxScrollX(contentWidth, viewWidth);
            return scrollX > max ? max : scrollX;
        }

        /// <summary>The log line for a window rect moved or narrowed onto the screen.
        /// Invariant-culture, since a test reads it.</summary>
        internal static string FormatFitLog(string windowName, Rect before, Rect after,
            float screenWidth, float screenHeight)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return string.Format(ic,
                "{0} fitted to screen {1:F0}x{2:F0}: x={3:F0}->{4:F0} y={5:F0}->{6:F0} w={7:F0}->{8:F0}",
                windowName ?? "window", screenWidth, screenHeight,
                before.x, after.x, before.y, after.y, before.width, after.width);
        }

        /// <summary>The transition log line for a window's horizontal scroll turning on or
        /// off. Invariant-culture, since a test reads it.</summary>
        internal static string FormatScrollTransitionLog(string windowName, bool active,
            float windowWidth, float naturalWidth)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return active
                ? string.Format(ic,
                    "{0} horizontal scroll ON: window w={1:F0} < natural w={2:F0}",
                    windowName, windowWidth, naturalWidth)
                : string.Format(ic,
                    "{0} horizontal scroll OFF: window w={1:F0} >= natural w={2:F0}",
                    windowName, windowWidth, naturalWidth);
        }
    }

    /// <summary>
    /// One window's horizontal-scroll state for content laid out wider than the window.
    ///
    /// <para><b>Two shapes, one decision.</b> The decision (<see cref="Latch"/>) is the same
    /// for every window: scroll when the window is narrower than its natural width, which
    /// only happens once <see cref="WideWindowLayout.FitToScreen"/> has capped it to a
    /// narrow screen. The shape depends on how the window already scrolls:</para>
    /// <list type="bullet">
    /// <item><b>Pinned header</b> (<see cref="BeginPinnedHeaderArea"/>): a table whose
    /// column header sits ABOVE its vertical body scroll view (the Missions window's two
    /// tabs). The header and the body are wrapped together in ONE horizontal-only scroll
    /// view whose content is laid out at the natural width, so the header scrolls in
    /// lockstep with the body and every column keeps the x it has at the natural width.
    /// The body's own vertical scroll view is nested inside, unchanged.</item>
    /// <item><b>Existing 2D scroll view</b> (<see cref="BeginContentFloor"/>): a window
    /// whose headers already live inside its one scroll view (Logistics). Only a minimum
    /// content width is added, so the scroll view's own horizontal bar appears and the
    /// columns keep their natural share instead of squeezing the expanding one.</item>
    /// </list>
    ///
    /// <para><b>Nothing changes on a screen the window fits.</b> While the window is at or
    /// above its natural width both Begin methods draw nothing and add no control, so the
    /// layout is the one the window always had.</para>
    ///
    /// <para><b>Latched per frame.</b> The wrapper adds controls, so whether it is drawn
    /// must agree between a frame's Layout pass and the event pass that follows it. A
    /// resize drag changes the width BETWEEN those two passes, so the decision is taken on
    /// Layout only and held for the rest of the frame.</para>
    ///
    /// <para><b>No per-frame allocation.</b> The layout options and the content style are
    /// built once and reused.</para>
    /// </summary>
    internal sealed class WideWindowScroll
    {
        private readonly string windowName;
        private readonly float naturalWidth;
        private bool active;
        private Vector2 scrollPosition;

        private GUIStyle contentStyle;
        private GUIStyle contentStyleSource;
        private GUILayoutOption[] pinnedScrollOptions;
        private GUILayoutOption[] pinnedContentOptions;
        private GUILayoutOption[] floorContentOptions;
        private float floorContentWidth = -1f;

        internal WideWindowScroll(string windowName, float naturalWidth)
        {
            this.windowName = windowName;
            this.naturalWidth = naturalWidth;
        }

        /// <summary>The width the window's content is laid out for.</summary>
        internal float NaturalWidth => naturalWidth;

        /// <summary>Whether this frame draws the horizontal-scroll wrapper.</summary>
        internal bool Active => active;

        /// <summary>The horizontal scroll offset: the live one while <see cref="Active"/>,
        /// zero while the window fits (nothing is scrolled then). Automation seam
        /// (<c>op=state key=scrollX</c>) and tests only; the scroll view clamps a written
        /// offset to its content on its next draw.</summary>
        internal float ScrollX
        {
            get { return active ? scrollPosition.x : 0f; }
            set { if (active) scrollPosition.x = value; }
        }

        /// <summary>Takes the scroll decision on the Layout pass and holds it for the rest
        /// of the frame. Call at the top of the window function on every event.</summary>
        internal void Latch(float windowWidth)
        {
            if (Event.current == null || Event.current.type != EventType.Layout) return;
            Decide(windowWidth);
        }

        /// <summary>
        /// The latch body: sets <see cref="Active"/> and logs only when it changes. Leaving
        /// the scrolling state resets the offset, so a window that grows back to fit never
        /// keeps a stale offset for its next narrow spell. Returns the new state.
        /// </summary>
        internal bool Decide(float windowWidth)
        {
            bool next = WideWindowLayout.NeedsHorizontalScroll(windowWidth, naturalWidth);
            if (next != active)
            {
                active = next;
                if (!next) scrollPosition = Vector2.zero;
                ParsekLog.Verbose("UI", WideWindowLayout.FormatScrollTransitionLog(
                    windowName, next, windowWidth, naturalWidth));
            }
            return active;
        }

        /// <summary>
        /// Opens the pinned-header wrapper when <see cref="Active"/>: a horizontal-only
        /// scroll view (no vertical bar, no background) around a vertical group laid out at
        /// the natural window width, carrying the window style's own horizontal padding.
        /// With that padding every child is placed exactly as the window's own vertical
        /// group places it (each child's x is max(its margin, the padding), and the header
        /// and body margins both fit inside the padding), so the header and the body keep
        /// the column positions and widths they have at the natural width. No-op when
        /// inactive. Pair with <see cref="EndPinnedHeaderArea"/>.
        /// </summary>
        internal void BeginPinnedHeaderArea(GUIStyle windowStyle)
        {
            if (!active) return;
            EnsureContentStyle(windowStyle);
            if (pinnedScrollOptions == null)
                pinnedScrollOptions = new[] { GUILayout.ExpandHeight(true) };
            if (pinnedContentOptions == null)
                pinnedContentOptions = new[] { GUILayout.MinWidth(naturalWidth) };

            scrollPosition = GUILayout.BeginScrollView(scrollPosition, false, false,
                GUI.skin.horizontalScrollbar, GUIStyle.none, GUIStyle.none,
                pinnedScrollOptions);
            GUILayout.BeginVertical(contentStyle, pinnedContentOptions);
        }

        /// <summary>Closes <see cref="BeginPinnedHeaderArea"/>. No-op when inactive.</summary>
        internal void EndPinnedHeaderArea()
        {
            if (!active) return;
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        /// <summary>
        /// Opens a minimum-width content group INSIDE a window's existing scroll view when
        /// <see cref="Active"/>, so that scroll view scrolls horizontally rather than
        /// squeezing its expanding column. <paramref name="reservedWidth"/> is what the
        /// window spends outside that content at its natural width (its padding and the
        /// scroll view's vertical bar). No-op when inactive. Pair with
        /// <see cref="EndContentFloor"/>.
        /// </summary>
        internal void BeginContentFloor(float reservedWidth)
        {
            if (!active) return;
            float width = naturalWidth - reservedWidth;
            if (width < 0f) width = 0f;
            if (floorContentOptions == null || width != floorContentWidth)
            {
                floorContentOptions = new[] { GUILayout.MinWidth(width) };
                floorContentWidth = width;
            }
            GUILayout.BeginVertical(floorContentOptions);
        }

        /// <summary>Closes <see cref="BeginContentFloor"/>. No-op when inactive.</summary>
        internal void EndContentFloor()
        {
            if (!active) return;
            GUILayout.EndVertical();
        }

        private void EnsureContentStyle(GUIStyle windowStyle)
        {
            if (contentStyle != null && ReferenceEquals(contentStyleSource, windowStyle))
                return;
            RectOffset pad = windowStyle != null ? windowStyle.padding : null;
            contentStyle = new GUIStyle
            {
                padding = new RectOffset(pad != null ? pad.left : 0, pad != null ? pad.right : 0, 0, 0)
            };
            contentStyleSource = windowStyle;
        }
    }
}
