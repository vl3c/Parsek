using System;
using System.Collections;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-IMGUI guard that the tooltip echo strip is exactly TWO text lines tall at a
    /// realistic window width - no taller when a long tooltip wraps past two lines, and
    /// no shorter when nothing is hovered.
    ///
    /// <para>This is the half of the shape contract the control-count guard
    /// (<see cref="LogisticsTooltipEchoImguiTest"/>) cannot see. That one proves the two
    /// states agree with EACH OTHER; this one proves they both agree with the two-line
    /// height the helper measured from <c>GUI.skin.box</c> - i.e. that the constant is
    /// the intended constant, at a width where a >200-character tooltip genuinely wraps.
    /// Together they are why a window's height and its bottom row no longer move when
    /// the pointer crosses a control.</para>
    ///
    /// <para>Cannot be a headless unit test: GUIStyle.CalcHeight and the GUILayout group
    /// both need a live Unity GUI context.</para>
    /// </summary>
    public sealed class TooltipEchoWrapSizingImguiTest
    {
        // Category is `Settings`, not the sibling guard's `Logistics`: the strip is
        // shared window chrome (six windows host it, the Settings window included), and
        // `Logistics` is pinned by the committed H34 / H35 batch tallies, whose passed= /
        // skipped= splits are only re-measurable from a live flight.
        [InGameTest(Category = "Settings",
            Description = "Tooltip echo strip reserves exactly its fixed two-line height in a narrow window whether it is empty or clipping a >200-character tooltip, so hovering never changes a window's height")]
        public IEnumerator TooltipEchoBox_NarrowWindow_ReservesWrappedHeight()
        {
            var go = new GameObject("ParsekTooltipEchoWrapProbe");
            UnityEngine.Object.DontDestroyOnLoad(go);
            TooltipEchoWrapProbe probe = go.AddComponent<TooltipEchoWrapProbe>();

            try
            {
                int guardFrames = 0;
                while (!probe.Completed && guardFrames < 240)
                {
                    guardFrames++;
                    yield return null;
                }

                if (probe.RepaintPasses == 0)
                {
                    InGameAssert.Skip(
                        $"probe never observed an IMGUI Repaint pass (frames={guardFrames}); cannot measure the strip in this context");
                    yield break;
                }

                InGameAssert.IsFalse(probe.Faulted,
                    "Tooltip echo strip threw an IMGUI exception while drawing a long tooltip in a narrow area: "
                        + probe.FaultMessage);

                var ic = System.Globalization.CultureInfo.InvariantCulture;

                if (probe.FixedHeight <= 0f || probe.EmptyRectHeight <= 0f || probe.LongRectHeight <= 0f)
                {
                    InGameAssert.Skip(
                        $"probe could not measure the strip (fixed={probe.FixedHeight.ToString("F2", ic)} "
                        + $"empty={probe.EmptyRectHeight.ToString("F2", ic)} "
                        + $"long={probe.LongRectHeight.ToString("F2", ic)}); nothing to assert");
                    yield break;
                }

                // A rounded layout rect can differ from the measured style height by a
                // fraction of a pixel; anything larger means the text moved the box.
                const float Tolerance = 0.51f;

                InGameAssert.ApproxEqual(probe.FixedHeight, probe.LongRectHeight, Tolerance,
                    "Tooltip echo strip reserved "
                        + probe.LongRectHeight.ToString("F2", ic) + "px at width "
                        + probe.MeasuredRectWidth.ToString("F2", ic) + "px for a "
                        + probe.TooltipLength + "-character tooltip, against its fixed two-line height of "
                        + probe.FixedHeight.ToString("F2", ic)
                        + "px; long text must CLIP inside the two lines, not grow the strip");

                InGameAssert.ApproxEqual(probe.FixedHeight, probe.EmptyRectHeight, Tolerance,
                    "Tooltip echo strip reserved "
                        + probe.EmptyRectHeight.ToString("F2", ic)
                        + "px while empty against its fixed two-line height of "
                        + probe.FixedHeight.ToString("F2", ic)
                        + "px; an empty strip must hold the same two lines open, not collapse");

                // The constant must be a real two lines, not one: a single-line box would
                // satisfy both equalities above and still clip half the help text.
                InGameAssert.IsGreaterThan(probe.FixedHeight, probe.SingleLineHeight,
                    "the strip's fixed height "
                        + probe.FixedHeight.ToString("F2", ic)
                        + "px is not taller than a single line of the same style ("
                        + probe.SingleLineHeight.ToString("F2", ic) + "px)");

                ParsekLog.Info("TestRunner",
                    $"TooltipEchoWrapSizing_InGame: PASS fixed={probe.FixedHeight.ToString("F2", ic)} " +
                    $"empty={probe.EmptyRectHeight.ToString("F2", ic)} long={probe.LongRectHeight.ToString("F2", ic)} " +
                    $"rectWidth={probe.MeasuredRectWidth.ToString("F2", ic)} " +
                    $"singleLine={probe.SingleLineHeight.ToString("F2", ic)} " +
                    $"chars={probe.TooltipLength} layoutPasses={probe.LayoutPasses} repaintPasses={probe.RepaintPasses}");
            }
            finally
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        /// <summary>
        /// Probe MonoBehaviour: draws a <see cref="TooltipEchoBox"/> inside a narrow
        /// (220px) GUILayout area, empty for the first frames and then fed a long
        /// multi-word tooltip. On Repaint it reads GUILayoutUtility.GetLastRect() (valid
        /// only on Repaint) for the rect Layout actually reserved, and also records the
        /// helper's own fixed two-line height plus the single-line height of the same
        /// style at the same width.
        /// </summary>
        private sealed class TooltipEchoWrapProbe : MonoBehaviour
        {
            internal bool Faulted;
            internal string FaultMessage = string.Empty;
            internal int RepaintPasses;
            internal int LayoutPasses;
            internal float EmptyRectHeight;
            internal float LongRectHeight;
            internal float MeasuredRectWidth;
            internal float FixedHeight;
            internal float SingleLineHeight;
            internal int TooltipLength;
            internal bool Completed;

            private const float AreaWidth = 220f;

            // >200 characters of ordinary words, so it wraps to many lines at 220px
            // regardless of the skin's font size.
            private const string LongTooltip =
                "This route delivers ore and liquid fuel from the surface mining outpost to the " +
                "orbital depot on every scheduled cycle, and will wait at the origin whenever the " +
                "stored resources or the available funds are not enough to fill the manifest in full.";

            private readonly TooltipEchoBox echo = new TooltipEchoBox();
            private bool longFrame;

            private void OnGUI()
            {
                if (Completed)
                    return;

                EventType evt = Event.current.type;
                if (evt != EventType.Layout && evt != EventType.Repaint)
                    return;

                if (evt == EventType.Layout)
                {
                    LayoutPasses++;
                    // Whole frames are one state or the other, so the rect measured on
                    // Repaint is the one this frame's Layout reserved for that state.
                    longFrame = LayoutPasses > 2;
                }

                GUILayout.BeginArea(new Rect(0f, 0f, AreaWidth, 400f));
                try
                {
                    echo.Draw(longFrame ? LongTooltip : null);
                    if (evt == EventType.Repaint)
                    {
                        Rect labelRect = GUILayoutUtility.GetLastRect();
                        MeasuredRectWidth = labelRect.width;
                        if (longFrame)
                        {
                            LongRectHeight = labelRect.height;
                            TooltipLength = LongTooltip.Length;
                        }
                        else
                        {
                            EmptyRectHeight = labelRect.height;
                        }

                        FixedHeight = echo.FixedStripHeight;
                        GUIStyle wrapped = echo.WrappedStyle;
                        if (wrapped != null && labelRect.width > 0f)
                            SingleLineHeight = wrapped.CalcHeight(new GUIContent("Wm"), labelRect.width);
                    }
                    // Trailing control mirrors the real Close button block, so a
                    // control-count desync would surface here too.
                    GUILayout.Button("probe-close");
                }
                catch (Exception ex)
                {
                    if (!Faulted)
                    {
                        Faulted = true;
                        FaultMessage = ex.GetType().Name + ": " + ex.Message;
                        ParsekLog.Warn("TestRunner",
                            "TooltipEchoWrapSizing_InGame probe caught IMGUI exception: " + FaultMessage);
                    }
                }
                finally
                {
                    GUILayout.EndArea();
                }

                if (evt == EventType.Repaint)
                {
                    RepaintPasses++;
                    // Four full frames: two empty, two showing the long tooltip.
                    if (RepaintPasses >= 4 || Faulted)
                        Completed = true;
                }
            }
        }
    }
}
