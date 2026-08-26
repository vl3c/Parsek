using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the pure decision core of <see cref="TooltipEchoBox"/>, the shared bottom
    /// "hovered control help text" strip.
    ///
    /// <para>The Unity-free decisions: what text to render
    /// (<see cref="TooltipEchoBox.ResolveCapturedText"/> - a window-computed manual
    /// override outranks the generic GUI.tooltip, because a window that resolved its own
    /// hover string has already answered more specifically) and how many lines a strip
    /// reserves (<see cref="TooltipEchoBox.NormalizeLines"/> /
    /// <see cref="TooltipEchoBox.ProbeText"/> - the per-window one-or-two line choice,
    /// clamped so an out-of-range value can only ever produce the taller, safer strip).</para>
    ///
    /// <para>The strip emits the same two controls at the same constant size on every
    /// pass, which is the property that keeps the window height and its bottom row from
    /// moving when the pointer crosses a control. That size is measured from a live
    /// <c>GUI.skin</c> and is pinned in-game by <c>LogisticsTooltipEchoImguiTest</c> and
    /// <c>TooltipEchoWrapSizingImguiTest</c>; the marquee motion is pinned headlessly in
    /// <see cref="TooltipMarqueeTests"/>.</para>
    /// </summary>
    public class TooltipEchoBoxTests
    {
        [Fact]
        public void ResolveCapturedText_ManualOverrideWins()
        {
            Assert.Equal("clamped to 8 ghosts",
                TooltipEchoBox.ResolveCapturedText("clamped to 8 ghosts", "generic row tooltip"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ResolveCapturedText_NoOverride_FallsBackToGuiTooltip(string manualOverride)
        {
            Assert.Equal("generic row tooltip",
                TooltipEchoBox.ResolveCapturedText(manualOverride, "generic row tooltip"));
        }

        [Fact]
        public void ResolveCapturedText_NeitherPresent_IsEmptyNotNull()
        {
            // The strip always draws, so an absent tooltip must resolve to the empty
            // string a GUILayout.Label can render, never to null.
            Assert.Equal(string.Empty, TooltipEchoBox.ResolveCapturedText(null, null));
            Assert.Equal(string.Empty, TooltipEchoBox.ResolveCapturedText("", null));
            Assert.Equal(string.Empty, TooltipEchoBox.ResolveCapturedText(null, ""));
        }

        [Fact]
        public void ResolveCapturedText_IsPureAndRepeatable()
        {
            // Read live on every pass, so the same inputs must resolve identically no
            // matter which IMGUI event is asking.
            const string tip = "Hovering any cell shows its help text here";
            Assert.Equal(
                TooltipEchoBox.ResolveCapturedText(null, tip),
                TooltipEchoBox.ResolveCapturedText(null, tip));
            Assert.Equal(tip, TooltipEchoBox.ResolveCapturedText(null, tip));
        }

        // ------------------------------------------------------------------
        // Strip height (one or two lines)
        // ------------------------------------------------------------------

        // catches: a host typo constructing its strip with a bogus line count - the
        // clamp must fall back to the TALLER (safer, two-line) strip, never invent
        // a third height.
        [Theory]
        [InlineData(TooltipEchoBox.SingleLine, TooltipEchoBox.SingleLine)]
        [InlineData(TooltipEchoBox.DoubleLine, TooltipEchoBox.DoubleLine)]
        [InlineData(0, TooltipEchoBox.DoubleLine)]
        [InlineData(-3, TooltipEchoBox.DoubleLine)]
        [InlineData(99, TooltipEchoBox.DoubleLine)]
        public void NormalizeLines_OnlyOneOrTwo_OtherwiseTaller(int requested, int expected)
        {
            Assert.Equal(expected, TooltipEchoBox.NormalizeLines(requested));
        }

        // catches: the probe text drifting apart from the line count it measures -
        // a one-line strip must measure ONE "Ay" line, a two-line strip exactly two.
        [Fact]
        public void ProbeText_MatchesTheStripHeight()
        {
            Assert.Equal("Ay", TooltipEchoBox.ProbeText(TooltipEchoBox.SingleLine));
            Assert.Equal("Ay\nAy", TooltipEchoBox.ProbeText(TooltipEchoBox.DoubleLine));
            // The clamp applies here too: a bogus count measures the two-line probe,
            // matching what NormalizeLines-fed construction would reserve.
            Assert.Equal("Ay\nAy", TooltipEchoBox.ProbeText(0));
        }
    }
}
