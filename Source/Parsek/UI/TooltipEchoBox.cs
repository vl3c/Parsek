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
    /// <para><b>The overflow marquee.</b> Text the strip cannot fully display does not
    /// clip silently: the strip renders it through the NOWRAP style as one long line
    /// (word wrap would push the tail onto a vertically-clipped extra line, where no
    /// horizontal shift could ever reveal it) and shifts it left over time via that
    /// style's <c>contentOffset</c>. The label keeps the same control count and the
    /// same reserved rect in marquee mode: explicit height as always, and the width
    /// pinned to the previously measured strip width so the unwrapped text's huge
    /// minimum width can never widen the window. Whether a text overflows is decided
    /// by the pure <see cref="TooltipMarquee.NeedsMarquee"/> from the WRAPPED height
    /// (a text that wraps fully inside a two-line strip is visible and must not
    /// scroll); the decision and both measurements are cached and recomputed only when
    /// the text or the strip width changes. The clock accumulates realtime seconds
    /// against the current text's digit-free <see cref="TooltipMarquee.ScrollKeyFor"/>
    /// key, so live countdown tooltips keep their cycle across per-second re-renders
    /// while any real text change still restarts readable from the left edge.</para>
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
        private GUIStyle nowrapScrollStyle;
        private float fixedStripHeight;
        private float oneLineProbeHeight;

        // Marquee state. Elapsed accumulates realtime deltas while the text's
        // digit-free ScrollKeyFor key is unchanged (so countdown tooltips keep their
        // cycle across per-second re-renders); a key change resets it so a new tooltip
        // starts readable from its left edge.
        private string scrollText;
        private string scrollKey;
        private double scrollElapsedSeconds;
        private float lastRealtime;

        // The strip width measured on the previous Repaint. One frame stale after a
        // resize - invisible at marquee speeds.
        private float cachedStripWidth;

        // Cached overflow measurements + decision, recomputed only when the text or
        // the laid-out strip width changes (never per frame): the text's unwrapped
        // single-line advance, the width both were measured against, and whether the
        // current text must marquee-scroll. marqueeActive is a FIELD (not a per-pass
        // derivation) so Layout and Repaint of one frame agree on the label's style
        // and options even while the decision is changing.
        private float nowrapTextWidth;
        private float measuredStripWidth;
        private bool measurementsDirty;
        private bool marqueeActive;

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
            nowrapScrollStyle = null;
            fixedStripHeight = 0f;
            oneLineProbeHeight = 0f;
            scrollText = null;
            scrollKey = null;
            scrollElapsedSeconds = 0.0;
            lastRealtime = 0f;
            cachedStripWidth = 0f;
            nowrapTextWidth = 0f;
            measuredStripWidth = 0f;
            measurementsDirty = false;
            marqueeActive = false;
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

            // The scroll shift is paint-time only (contentOffset never affects layout);
            // it is computed BEFORE the label so the same call reserves and paints.
            // AdvanceScroll also refreshes marqueeActive, but only ON REPAINT - the
            // field is what the style/option choice below reads, so Layout and Repaint
            // of one frame always pick the same control shape.
            if (repaint)
            {
                float shiftX = AdvanceScroll(text);
                nowrapScrollStyle.contentOffset = new Vector2(-shiftX, 0f);
            }

            GUILayout.Space(spacing);
            if (marqueeActive && cachedStripWidth > 0f)
            {
                // Marquee mode: render as ONE unwrapped line (the wrapped render would
                // hide the tail on a vertically-clipped extra line) inside the exact
                // rect the strip always occupies. Width is PINNED to the measured strip
                // width - never ExpandWidth - because the unwrapped text's minimum
                // width would otherwise widen the whole window during Layout.
                GUILayout.Label(
                    text,
                    nowrapScrollStyle,
                    GUILayout.Height(fixedStripHeight),
                    GUILayout.Width(cachedStripWidth));
            }
            else
            {
                GUILayout.Label(
                    text,
                    wrappedTooltipStyle,
                    GUILayout.Height(fixedStripHeight),
                    GUILayout.ExpandWidth(true));
            }

            if (repaint)
                cachedStripWidth = GUILayoutUtility.GetLastRect().width;
        }

        /// <summary>
        /// Advances the marquee clock against <paramref name="text"/>, refreshes the
        /// cached overflow measurements + <see cref="marqueeActive"/> when the text or
        /// the laid-out strip width changed, and returns how far the rendered line
        /// should shift LEFT this frame (0 when the text fits or no width has been
        /// laid out yet). REPAINT ONLY - it reads the realtime clock.
        /// </summary>
        private float AdvanceScroll(string text)
        {
            float now = Time.realtimeSinceStartup;
            if (!string.Equals(text, scrollText, StringComparison.Ordinal))
            {
                // Clock identity is the digit-free key: a countdown tooltip re-rendering
                // each second keeps its cycle; any other change restarts it.
                string key = TooltipMarquee.ScrollKeyFor(text);
                if (!string.Equals(key, scrollKey, StringComparison.Ordinal))
                    scrollElapsedSeconds = 0.0;
                scrollText = text;
                scrollKey = key;
                measurementsDirty = true;
            }
            else if (lastRealtime > 0f && now > lastRealtime)
            {
                scrollElapsedSeconds += now - lastRealtime;
            }
            lastRealtime = now;

            if (string.IsNullOrEmpty(text) || cachedStripWidth <= 0f || nowrapScrollStyle == null)
            {
                marqueeActive = false;
                return 0f;
            }

            // Measure ONLY when the text or the strip width changed - never per frame.
            // The unwrapped advance says how far there is to scroll; the WRAPPED height
            // against the actual strip width feeds the overflow decision (a text that
            // wraps fully inside a two-line strip is visible and must not scroll).
            if (measurementsDirty || measuredStripWidth != cachedStripWidth)
            {
                var content = new GUIContent(text);
                nowrapTextWidth = nowrapScrollStyle.CalcSize(content).x;
                float wrappedHeight = wrappedTooltipStyle.CalcHeight(content, cachedStripWidth);
                marqueeActive = TooltipMarquee.NeedsMarquee(
                    nowrapTextWidth, cachedStripWidth,
                    wrappedHeight, fixedStripHeight, oneLineProbeHeight);
                measuredStripWidth = cachedStripWidth;
                measurementsDirty = false;
            }

            if (!marqueeActive)
                return 0f;

            return TooltipMarquee.OffsetFor(
                scrollElapsedSeconds,
                nowrapTextWidth - cachedStripWidth,
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

            if (nowrapScrollStyle == null)
            {
                // Doubles as the overflow MEASURING style (unwrapped advance width)
                // and the marquee DRAW style (one long clipped line, shifted via its
                // contentOffset). Kept a clone of the wrapped style so font/padding
                // edits there keep applying to both roles.
                nowrapScrollStyle = new GUIStyle(wrappedTooltipStyle)
                {
                    wordWrap = false
                };
            }

            if (fixedStripHeight <= 0f)
            {
                fixedStripHeight = wrappedTooltipStyle.CalcHeight(
                    new GUIContent(ProbeText(lines)), NoWrapMeasureWidth);
                oneLineProbeHeight = wrappedTooltipStyle.CalcHeight(
                    new GUIContent(OneLineProbeText), NoWrapMeasureWidth);
            }
        }
    }
}
