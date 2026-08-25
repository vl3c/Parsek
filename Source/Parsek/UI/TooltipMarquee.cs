namespace Parsek
{
    /// <summary>
    /// Pure decision core for the tooltip echo strip's overflow marquee
    /// (<see cref="TooltipEchoBox"/>). When the hovered control's help text is wider
    /// than the strip, the strip scrolls it right-to-left so the clipped tail stays
    /// readable; this class owns the MOTION CURVE and nothing else - no Unity types,
    /// no clocks, no state - so headless tests can pin the exact movement.
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
        /// True when the text needs more room than the strip offers and should scroll.
        /// Zero-width boxes (nothing laid out yet) never scroll.
        /// </summary>
        internal static bool ShouldScroll(float textWidthPx, float boxWidthPx)
        {
            return boxWidthPx > 0f && textWidthPx > boxWidthPx;
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
