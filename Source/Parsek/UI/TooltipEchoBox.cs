using System;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Shared per-window-instance renderer for the bottom "hovered control help text"
    /// strip used by the Settings, Recordings, Logistics, Real Spawn Control and both
    /// test-runner windows.
    ///
    /// <para><b>The shape contract.</b> The strip is PERMANENTLY visible and always
    /// exactly one or two text lines tall (per-window choice; see the line-count
    /// constructor), empty when nothing is hovered. Every draw emits exactly one
    /// <c>GUILayout.Space</c> plus one <c>GUILayout.Label</c>, with the same spacing,
    /// the same style and the same explicit height option every time. Control count,
    /// control shape and reserved size are therefore constant across both IMGUI
    /// passes of a frame AND across frames.</para>
    ///
    /// <para><b>Why constant size is the mechanism.</b> IMGUI sizes a control during the
    /// Layout event and REUSES that cached rect during Repaint
    /// (GUILayoutUtility.DoGetRect), while <see cref="GUI.tooltip"/> is populated by the
    /// hovered control DURING Repaint. A strip whose size depends on the tooltip text
    /// therefore sized itself for one string and painted another on every hover-start
    /// frame (a one-frame dark sliver of box background), and a strip that varied its
    /// CONTROL COUNT by whether a tooltip was present overran its layout group outright
    /// ("Getting control N's position in a group with only N controls when doing
    /// repaint", thrown every Repaint while hovering and aborting the rest of that
    /// window's draw). Pinning the height removes both at the source: the reserved rect
    /// does not depend on the text at all, so the text is read LIVE each pass and the
    /// two passes cannot disagree about anything that matters. It also removes the
    /// player-visible cost of a variable strip - the window no longer changes height,
    /// and its bottom row no longer shuffles, when the pointer crosses a control.</para>
    ///
    /// <para><b>One vs two lines.</b> A window whose every help text fits ONE wrapped
    /// line at its width (wide windows: Career State, Timeline, Logistics, Real Spawn
    /// Control) constructs the box with <see cref="SingleLine"/>; narrower windows keep
    /// <see cref="DoubleLine"/> because their texts genuinely wrap. TooltipEchoBudgetTests
    /// pins which windows use which height alongside the per-window text budgets.</para>
    ///
    /// <para><b>The overflow marquee.</b> Text wider than the laid-out strip does not
    /// clip silently: on Repaint the rendered line shifts left over time (via the
    /// style's <c>contentOffset</c>, set BEFORE the label draws, so both IMGUI passes
    /// still emit identical controls and reserve the identical rect). The motion curve
    /// lives in the pure <see cref="TooltipMarquee"/>; this class only owns the clock -
    /// realtime seconds accumulated against the CURRENT text, reset whenever the text
    /// changes so a new tooltip always starts readable from its left edge.</para>
    ///
    /// <para>One instance per window (styles are built from <see cref="GUI.skin"/>, so
    /// a window whose skin is scene-scoped must call <see cref="ResetStyles"/> on a
    /// scene change).</para>
    /// </summary>
    internal sealed class TooltipEchoBox
    {
        /// <summary>House spacing above the strip; matches every window's SpacingSmall.</summary>
        internal const float DefaultSpacing = 3f;

        /// <summary>Strip heights: one wrapped text line (wide windows) or two.</summary>
        internal const int SingleLine = 1;
        internal const int DoubleLine = 2;

        /// <summary>
        /// Explicit probe lines measured at a width nothing can wrap at, so the fixed
        /// height is exactly N lines of the box style plus its own padding.
        /// </summary>
        private const string OneLineProbeText = "Ay";
        private const string TwoLineProbeText = "Ay\nAy";

        /// <summary>Width used for the probe measurement; far wider than any Parsek window.</summary>
        private const float NoWrapMeasureWidth = 10000f;

        private readonly float spacing;
        private readonly int lines;

        private GUIStyle wrappedTooltipStyle;
        private GUIStyle nowrapMeasureStyle;
        private float fixedStripHeight;

        // Marquee state. Elapsed accumulates realtime deltas while the SAME text shows
        // (numbers stay small forever, unlike a raw Time.realtimeSinceStartup whose
        // float precision decays over long sessions); any text change resets it so the
        // new tooltip starts readable from its left edge.
        private string scrollText;
        private double scrollElapsedSeconds;
        private float lastRealtime;

        // The strip width measured on the previous Repaint. One frame stale after a
        // resize - invisible at marquee speeds.
        private float cachedStripWidth;

        internal TooltipEchoBox() : this(DefaultSpacing)
        {
        }

        internal TooltipEchoBox(float spacing) : this(spacing, DoubleLine)
        {
        }

        /// <summary>
        /// Creates a strip reserving <paramref name="lines"/> text lines (1 or 2; any
        /// other value falls back to 2). Wide windows whose entire help corpus fits one
        /// line pass <see cref="SingleLine"/>.
        /// </summary>
        internal TooltipEchoBox(float spacing, int lines)
        {
            this.spacing = spacing;
            this.lines = NormalizeLines(lines);
        }

        /// <summary>
        /// The only two legal strip heights; anything but 1 reads as 2.
        /// </summary>
        internal static int NormalizeLines(int requested)
        {
            return requested == SingleLine ? SingleLine : DoubleLine;
        }

        /// <summary>
        /// The explicit-height probe for a strip of <paramref name="lines"/> lines,
        /// measured at a width nothing can wrap at.
        /// </summary>
        internal static string ProbeText(int lines)
        {
            return NormalizeLines(lines) == SingleLine ? OneLineProbeText : TwoLineProbeText;
        }

        /// <summary>The wrapped box style, once built. Null before the first draw.</summary>
        internal GUIStyle WrappedStyle
        {
            get { return wrappedTooltipStyle; }
        }

        /// <summary>
        /// The constant height every draw reserves. Zero before the first draw
        /// (it is measured from <see cref="GUI.skin"/> the first time the strip renders).
        /// </summary>
        internal float FixedStripHeight
        {
            get { return fixedStripHeight; }
        }

        /// <summary>
        /// Drops the cached styles and their measured height (and the marquee clock) so
        /// the next draw rebuilds them from the current <see cref="GUI.skin"/>. Windows
        /// whose skin is scene-scoped call this on a scene change (see
        /// <c>TestRunnerShortcut.ResetSceneScopedWindowState</c>).
        /// </summary>
        internal void ResetStyles()
        {
            wrappedTooltipStyle = null;
            nowrapMeasureStyle = null;
            fixedStripHeight = 0f;
            scrollText = null;
            scrollElapsedSeconds = 0.0;
            lastRealtime = 0f;
            cachedStripWidth = 0f;
        }

        /// <summary>
        /// What the strip renders: a window-supplied manual override wins over
        /// <see cref="GUI.tooltip"/>, because a window that computes its own hover string
        /// (RecordingsTableUI's clamped loop-period cell, Real Spawn Control's bottom-row
        /// Warp button) has already resolved a more specific answer than the generic
        /// GUIContent tooltip. Pure and Unity-free.
        /// </summary>
        internal static string ResolveCapturedText(string manualOverride, string guiTooltip)
        {
            if (!string.IsNullOrEmpty(manualOverride))
                return manualOverride;
            return guiTooltip ?? string.Empty;
        }

        /// <summary>
        /// Emits the strip. Call once per draw pass, at the point in the window where the
        /// strip belongs - by house convention directly above the Close button row, which
        /// is always the window's last content row.
        ///
        /// <para>The text is read LIVE: during Layout it is empty or one frame stale, and
        /// that is irrelevant, because the rect Layout reserves is the same fixed height
        /// either way. Only controls drawn BEFORE this call can feed
        /// <see cref="GUI.tooltip"/> in time; a tooltipped control that sits below the
        /// strip must hand its text in through <paramref name="manualOverride"/>.</para>
        /// </summary>
        /// <param name="manualOverride">
        /// Optional window-computed hover text that outranks <see cref="GUI.tooltip"/>.
        /// </param>
        internal void Draw(string manualOverride = null)
        {
            EnsureStyles();

            string text = ResolveCapturedText(manualOverride, GUI.tooltip);
            bool repaint = Event.current.type == EventType.Repaint;

            // contentOffset is a paint-time shift only: Layout never sees a nonzero
            // value, and on Repaint it is set BEFORE the label draws, so the label both
            // reserves and paints in the same call with the same rect as always.
            float shiftX = repaint ? AdvanceScroll(text) : 0f;
            wrappedTooltipStyle.contentOffset = new Vector2(-shiftX, 0f);

            GUILayout.Space(spacing);
            GUILayout.Label(
                text,
                wrappedTooltipStyle,
                GUILayout.Height(fixedStripHeight),
                GUILayout.ExpandWidth(true));

            if (repaint)
                cachedStripWidth = GUILayoutUtility.GetLastRect().width;
        }

        /// <summary>
        /// Advances the marquee clock against <paramref name="text"/> and returns how far
        /// the rendered line should shift LEFT this frame (0 when the text fits or no
        /// width has been laid out yet). REPAINT ONLY - it reads the realtime clock.
        /// </summary>
        private float AdvanceScroll(string text)
        {
            float now = Time.realtimeSinceStartup;
            if (!string.Equals(text, scrollText, StringComparison.Ordinal))
            {
                scrollText = text;
                scrollElapsedSeconds = 0.0;
            }
            else if (lastRealtime > 0f && now > lastRealtime)
            {
                scrollElapsedSeconds += now - lastRealtime;
            }
            lastRealtime = now;

            if (string.IsNullOrEmpty(text) || cachedStripWidth <= 0f || nowrapMeasureStyle == null)
                return 0f;

            // Single-line advance width (wordWrap off): the marquee reveals horizontally,
            // so wrapping is irrelevant to whether the text overflows the strip.
            float textWidth = nowrapMeasureStyle.CalcSize(new GUIContent(text)).x;
            if (!TooltipMarquee.ShouldScroll(textWidth, cachedStripWidth))
                return 0f;

            return TooltipMarquee.OffsetFor(
                scrollElapsedSeconds,
                textWidth - cachedStripWidth,
                TooltipMarquee.SpeedPxPerSecond,
                TooltipMarquee.HoldSeconds);
        }

        private void EnsureStyles()
        {
            if (wrappedTooltipStyle == null)
            {
                wrappedTooltipStyle = new GUIStyle(GUI.skin.box)
                {
                    wordWrap = true,
                    alignment = TextAnchor.UpperLeft,
                    // Explicit rather than inherited: the strip's whole contract is that
                    // over-long text CLIPS instead of growing the box (the marquee then
                    // scrolls it into view rather than letting the clip hide it).
                    clipping = TextClipping.Clip
                };
            }

            if (nowrapMeasureStyle == null)
            {
                nowrapMeasureStyle = new GUIStyle(wrappedTooltipStyle)
                {
                    wordWrap = false
                };
            }

            if (fixedStripHeight <= 0f)
            {
                fixedStripHeight = wrappedTooltipStyle.CalcHeight(
                    new GUIContent(ProbeText(lines)), NoWrapMeasureWidth);
            }
        }
    }
}
