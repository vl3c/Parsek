namespace Parsek
{
    /// <summary>
    /// Pure decision core for the tooltip echo strip's overflow marquee
    /// (<see cref="TooltipEchoBox"/>). When the hovered control's help text cannot be
    /// fully shown in the strip, the strip renders it as ONE unwrapped line and scrolls
    /// it right-to-left so the clipped tail stays readable; this class owns the
    /// OVERFLOW PREDICATE, the CLOCK KEY and the MOTION CURVE and nothing else - no
    /// Unity types, no clocks, no state - so headless tests can pin the exact behavior.
    ///
    /// <para><b>The curve.</b> Hold the text at its left edge (<see cref="HoldSeconds"/>,
    /// time to read the beginning), scroll left at <see cref="SpeedPxPerSecond"/> until
    /// the tail is fully revealed, hold again at the end, then snap back to the start
    /// and repeat. A pause at both ends reads far better than a continuous carousel -
    /// the reader chooses when to follow the text instead of chasing it.</para>
    /// </summary>
    internal static class TooltipMarquee
    {
        /// <summary>Scroll speed while moving, in px/s (~9 characters/s at the default font).</summary>
        internal const float SpeedPxPerSecond = 60f;

        /// <summary>Seconds held motionless at each end before scrolling / wrapping.</summary>
        internal const double HoldSeconds = 1.6;

        /// <summary>
        /// True when the text cannot be fully displayed in the strip's reserved rect and
        /// must be marquee-scrolled as a single unwrapped line. Two overflow shapes:
        ///
        /// <para>(1) The word-wrapped render needs MORE LINES than the strip reserves
        /// (<paramref name="wrappedHeightPx"/> &gt; <paramref name="stripHeightPx"/>) -
        /// the extra lines are clipped vertically, so no amount of horizontal shifting
        /// of the wrapped block could reveal them.</para>
        ///
        /// <para>(2) The text renders as a SINGLE line (word wrap cannot break an
        /// unbreakable run: <paramref name="wrappedHeightPx"/> &lt;=
        /// <paramref name="oneLineHeightPx"/>) that is wider than the strip - it clips
        /// horizontally in place.</para>
        ///
        /// <para>The deliberate non-case: a text that wraps to exactly the reserved line
        /// count (e.g. two wrapped lines in a two-line strip) is FULLY VISIBLE even
        /// though its unwrapped width exceeds the strip - it must never scroll.
        /// Zero-width boxes (nothing laid out yet) never scroll.</para>
        /// </summary>
        internal static bool NeedsMarquee(
            float nowrapWidthPx, float boxWidthPx,
            float wrappedHeightPx, float stripHeightPx, float oneLineHeightPx)
        {
            if (boxWidthPx <= 0f || nowrapWidthPx <= boxWidthPx)
                return false;
            bool needsMoreLines = wrappedHeightPx > stripHeightPx;
            bool rendersAsOneClippedLine = wrappedHeightPx <= oneLineHeightPx;
            return needsMoreLines || rendersAsOneClippedLine;
        }

        /// <summary>
        /// The identity the marquee clock accumulates against: <paramref name="text"/>
        /// with every decimal digit removed. Live tooltips that embed a countdown
        /// ("Warp to X (spawns in 4m 32s)") re-render every second; keying the clock on
        /// the digit-free skeleton keeps it running across those ticks, so an over-long
        /// countdown text still reaches the end of its leading hold and scrolls. Any
        /// non-digit change (a different control's tooltip) still resets the cycle so a
        /// new text always starts readable from its left edge.
        /// </summary>
        internal static string ScrollKeyFor(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            bool hasDigit = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] >= '0' && text[i] <= '9') { hasDigit = true; break; }
            }
            if (!hasDigit)
                return text;
            var sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9')
                    sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The left-shift offset in px for <paramref name="seconds"/> into the current
        /// text's cycle, in [0, <paramref name="overflowPx"/>]. Piecewise over one cycle
        /// of <c>hold + scroll + hold</c>: 0 during the leading hold, linear during the
        /// scroll, the full overflow during the trailing hold, then it wraps.
        /// </summary>
        internal static float OffsetFor(
            double seconds, float overflowPx, float speedPxPerSecond, double holdSeconds)
        {
            if (overflowPx <= 0f || seconds <= 0.0)
                return 0f;

            double speed = speedPxPerSecond > 0f ? (double)speedPxPerSecond : 1.0;
            double hold = System.Math.Max(0.0, holdSeconds);
            double scrollSeconds = overflowPx / speed;
            double cycle = hold + scrollSeconds + hold;

            double t = seconds % cycle;
            if (t < hold)
                return 0f;
            t -= hold;
            if (t < scrollSeconds)
                return (float)(t * speed);
            return overflowPx;
        }
    }
}
