using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the motion curve of <see cref="TooltipMarquee"/>, the pure decision core
    /// behind the tooltip echo strip's overflow scroll. The strip's IMGUI glue (clock
    /// accumulation, width caching, contentOffset application) is deliberately thin;
    /// everything a player perceives - when it starts, how fast it moves, where it
    /// pauses, how it wraps - is decided here and is pinned exactly.
    ///
    /// <para>Reference cycle for these cells: 120px of overflow at 60 px/s with 1.6s
    /// holds gives hold(1.6) + scroll(2.0) + hold(1.6) = 5.2s per loop.</para>
    /// </summary>
    public class TooltipMarqueeTests
    {
        private const float Speed = TooltipMarquee.SpeedPxPerSecond;
        private const double Hold = TooltipMarquee.HoldSeconds;

        // ------------------------------------------------------------------
        // NeedsMarquee
        // ------------------------------------------------------------------

        // Reference geometry for these cells: one text line measures 20px, so a
        // one-line strip is 20px tall and a two-line strip 40px. Wrapped height is
        // what the text measures at the box width; nowrap width is its unwrapped
        // single-line advance.
        [Theory]
        // Fits on one line: never scrolls, in either strip height.
        [InlineData(200f, 300f, 20f, 20f, false)]
        [InlineData(200f, 300f, 20f, 40f, false)]
        // One-line strip, multi-word overflow: wraps to 2 lines, the 2nd is clipped
        // vertically - marquee.
        [InlineData(400f, 300f, 40f, 20f, true)]
        // One-line strip, unbreakable token: renders as ONE clipped line - marquee.
        [InlineData(400f, 300f, 20f, 20f, true)]
        // Two-line strip, text wraps to EXACTLY two visible lines: fully readable,
        // must NOT scroll even though its unwrapped width exceeds the box (the
        // regression the wrapped-height predicate exists to prevent).
        [InlineData(400f, 300f, 40f, 40f, false)]
        // Two-line strip, wraps to three lines: the 3rd is clipped - marquee.
        [InlineData(700f, 300f, 60f, 40f, true)]
        // Two-line strip, unbreakable token on one line: clips horizontally - marquee.
        [InlineData(400f, 300f, 20f, 40f, true)]
        // Nothing laid out yet / degenerate box: never scroll.
        [InlineData(400f, 0f, 40f, 20f, false)]
        [InlineData(400f, -5f, 40f, 20f, false)]
        public void NeedsMarquee_OnlyWhenTheStripCannotShowTheWholeText(
            float nowrapWidth, float boxWidth, float wrappedHeight, float stripHeight, bool expected)
        {
            const float OneLine = 20f;
            Assert.Equal(expected, TooltipMarquee.NeedsMarquee(
                nowrapWidth, boxWidth, wrappedHeight, stripHeight, OneLine));
        }

        // ------------------------------------------------------------------
        // ScrollKeyFor - the clock identity
        // ------------------------------------------------------------------

        // catches: the clock resetting on every tick of a countdown tooltip (the text
        // changes each second, so a full reset would hold the marquee at offset 0
        // forever), while still resetting for a genuinely different tooltip.
        [Fact]
        public void ScrollKeyFor_IsStableAcrossCountdownTicks_AndDistinctAcrossTexts()
        {
            string t1 = "Warp to Big Freighter Seven (spawns in 4m 32s)";
            string t2 = "Warp to Big Freighter Seven (spawns in 4m 31s)";
            string other = "Warp to Little Lander (spawns in 4m 31s)";
            Assert.Equal(TooltipMarquee.ScrollKeyFor(t1), TooltipMarquee.ScrollKeyFor(t2));
            Assert.NotEqual(TooltipMarquee.ScrollKeyFor(t1), TooltipMarquee.ScrollKeyFor(other));
            // Digit-free text keys as itself; null/empty collapse to empty.
            Assert.Equal("plain text", TooltipMarquee.ScrollKeyFor("plain text"));
            Assert.Equal(string.Empty, TooltipMarquee.ScrollKeyFor(null));
            Assert.Equal(string.Empty, TooltipMarquee.ScrollKeyFor(string.Empty));
        }

        // ------------------------------------------------------------------
        // OffsetFor - phase boundaries on the reference cycle
        // ------------------------------------------------------------------

        [Fact]
        public void OffsetFor_NonPositiveInputs_AreZero()
        {
            Assert.Equal(0f, TooltipMarquee.OffsetFor(10.0, 0f, Speed, Hold));   // no overflow
            Assert.Equal(0f, TooltipMarquee.OffsetFor(-1.0, 120f, Speed, Hold)); // clock not started
            Assert.Equal(0f, TooltipMarquee.OffsetFor(0.0, 120f, Speed, Hold));
        }

        [Fact]
        public void OffsetFor_LeadingHold_StaysAtZero()
        {
            // The reader gets HoldSeconds to read the beginning before any movement.
            Assert.Equal(0f, TooltipMarquee.OffsetFor(0.01, 120f, Speed, Hold));
            Assert.Equal(0f, TooltipMarquee.OffsetFor(Hold / 2, 120f, Speed, Hold));
            Assert.Equal(0f, TooltipMarquee.OffsetFor(Hold - 0.001, 120f, Speed, Hold));
        }

        [Fact]
        public void OffsetFor_ScrollPhase_MovesLinearlyRightToLeft()
        {
            double t0 = Hold;             // scroll starts now
            double tMid = Hold + 1.0;     // 1s in: 60px shifted
            double tEnd = Hold + 2.0;     // fully revealed

            float o0 = TooltipMarquee.OffsetFor(t0, 120f, Speed, Hold);
            float oMid = TooltipMarquee.OffsetFor(tMid, 120f, Speed, Hold);
            float oEnd = TooltipMarquee.OffsetFor(tEnd, 120f, Speed, Hold);

            Assert.Equal(0f, o0);
            Assert.Equal(60f, oMid);
            Assert.Equal(120f, oEnd);
            // Monotonic non-decreasing across the scroll.
            Assert.True(o0 <= oMid && oMid <= oEnd);
        }

        [Fact]
        public void OffsetFor_TrailingHold_ParksAtFullOverflow()
        {
            // Tail revealed and HELD so it stays readable before the wrap.
            double t = Hold + 120f / Speed + Hold / 2;
            Assert.Equal(120f, TooltipMarquee.OffsetFor(t, 120f, Speed, Hold));
        }

        [Fact]
        public void OffsetFor_WrapsBackToTheStartAfterOneCycle()
        {
            double cycle = Hold + 120f / Speed + Hold;
            Assert.Equal(0f, TooltipMarquee.OffsetFor(cycle + 0.01, 120f, Speed, Hold));
            // And the second lap looks like the first.
            Assert.Equal(
                TooltipMarquee.OffsetFor(Hold + 0.5, 120f, Speed, Hold),
                TooltipMarquee.OffsetFor(cycle + Hold + 0.5, 120f, Speed, Hold));
        }

        [Fact]
        public void OffsetFor_OffsetNeverExceedsTheOverflow()
        {
            // Sampling densely across several cycles: the shift must stay within
            // [0, overflow] or the text would scroll off its own tail / jump left
            // past its head.
            for (double t = 0.0; t < 30.0; t += 0.05)
            {
                float offset = TooltipMarquee.OffsetFor(t, 137f, Speed, Hold);
                Assert.True(offset >= 0f && offset <= 137f,
                    $"offset {offset} out of range at t={t}");
            }
        }

        [Fact]
        public void OffsetFor_DegenerateSpeed_DoesNotDivideByZero()
        {
            // A zero/negative speed constant must degrade to a finite crawl (the
            // implementation floors it at 1 px/s), never to NaN/Infinity.
            float offset = TooltipMarquee.OffsetFor(10.0, 120f, 0f, Hold);
            Assert.False(float.IsNaN(offset) || float.IsInfinity(offset));
            Assert.InRange(offset, 0f, 120f);
        }
    }
}
