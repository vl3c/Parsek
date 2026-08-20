using System;
using System.Collections;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-IMGUI guard that the tooltip echo strip actually RESERVES its wrapped
    /// height in a narrow window, rather than reserving one line and clipping the
    /// rest.
    ///
    /// <para>This is the half of the sizing contract that the control-count guard
    /// (<see cref="LogisticsTooltipEchoImguiTest"/>) cannot see. IMGUI computes a
    /// control's size during the Layout event only; Repaint reuses that cached rect,
    /// so the wrapped height a word-wrapping style needs must be computed during
    /// Layout FROM THE SAME STRING Repaint paints. <see cref="TooltipEchoBox"/> gets
    /// that by construction - both passes render from a Repaint-captured cache - and
    /// this test proves it at the width where it matters: a narrowed window, where a
    /// long tooltip needs several lines.</para>
    ///
    /// <para>Cannot be a headless unit test: GUIStyle.CalcHeight and the GUILayout
    /// group both need a live Unity GUI context.</para>
    /// </summary>
    public sealed class TooltipEchoWrapSizingImguiTest
    {
        // Category is `Settings`, not the sibling guard's `Logistics`: the strip is
        // shared window chrome (six windows host it, and the Settings window's height
        // re-measure gate reads its ShownLastDraw), and `Logistics` is pinned by the
        // committed H34 / H35 batch tallies, whose passed= / skipped= splits are only
        // re-measurable from a live flight.
        [InGameTest(Category = "Settings",
            Description = "Tooltip echo strip reserves its full word-wrapped height in a narrow window, so a long multi-line tooltip renders whole instead of clipping to one line")]
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
                        $"probe never observed an IMGUI Repaint pass (frames={guardFrames}); cannot measure the wrapped strip in this context");
                    yield break;
                }

                InGameAssert.IsFalse(probe.Faulted,
                    "Tooltip echo strip threw an IMGUI exception while drawing a long wrapped tooltip in a narrow area: "
                        + probe.FaultMessage);

                if (probe.MeasuredRectHeight <= 0f || probe.SingleLineHeight <= 0f)
                {
                    InGameAssert.Skip(
                        $"probe could not measure the strip (rectHeight={probe.MeasuredRectHeight.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"singleLine={probe.SingleLineHeight.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}); nothing to assert");
                    yield break;
                }

                // A >200-character tooltip at ~200px of content width wraps to far more
                // than two lines; 2x the single-line height of the SAME style at the
                // SAME width is a deliberately loose floor that still fails outright if
                // Layout reserved only one line.
                InGameAssert.IsGreaterThan(probe.MeasuredRectHeight, probe.SingleLineHeight * 2f,
                    "Tooltip echo strip reserved only " +
                    probe.MeasuredRectHeight.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    "px at width " +
                    probe.MeasuredRectWidth.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    "px for a " + probe.TooltipLength + "-character tooltip, against a single line of " +
                    probe.SingleLineHeight.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    "px; the wrapped height was not reserved during Layout");

                ParsekLog.Info("TestRunner",
                    $"TooltipEchoWrapSizing_InGame: PASS rectHeight={probe.MeasuredRectHeight.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"rectWidth={probe.MeasuredRectWidth.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"singleLine={probe.SingleLineHeight.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"chars={probe.TooltipLength} layoutPasses={probe.LayoutPasses} repaintPasses={probe.RepaintPasses}");
            }
            finally
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        /// <summary>
        /// Probe MonoBehaviour: draws a <see cref="TooltipEchoBox"/> inside a narrow
        /// (220px) GUILayout area with its cache primed to a long multi-word tooltip,
        /// so from the very first Layout pass the strip is in the wrapped state. On
        /// Repaint it reads GUILayoutUtility.GetLastRect() (valid only on Repaint) for
        /// the rect Layout actually reserved, and measures the single-line height of
        /// the same style at the same width for comparison.
        /// </summary>
        private sealed class TooltipEchoWrapProbe : MonoBehaviour
        {
            internal bool Faulted;
            internal string FaultMessage = string.Empty;
            internal int RepaintPasses;
            internal int LayoutPasses;
            internal float MeasuredRectHeight;
            internal float MeasuredRectWidth;
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
            private bool primed;

            private void OnGUI()
            {
                if (Completed)
                    return;

                EventType evt = Event.current.type;
                if (evt != EventType.Layout && evt != EventType.Repaint)
                    return;

                if (!primed)
                {
                    // Prime the cache BEFORE the first Layout pass so the strip is in
                    // its wrapped state from the very first sizing pass; a real window
                    // reaches the same state one frame after the hover starts.
                    echo.PrimeCachedTextForTesting(LongTooltip);
                    TooltipLength = LongTooltip.Length;
                    primed = true;
                }

                if (evt == EventType.Layout)
                    LayoutPasses++;

                GUILayout.BeginArea(new Rect(0f, 0f, AreaWidth, 400f));
                try
                {
                    echo.Draw(LongTooltip);
                    if (evt == EventType.Repaint)
                    {
                        Rect labelRect = GUILayoutUtility.GetLastRect();
                        MeasuredRectHeight = labelRect.height;
                        MeasuredRectWidth = labelRect.width;
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
                    // Two full frames: the first proves the primed cache sizes, the
                    // second proves the Repaint capture kept it there.
                    if (RepaintPasses >= 2 || Faulted)
                        Completed = true;
                }
            }
        }
    }
}
