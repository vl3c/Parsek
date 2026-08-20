using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the pure decision core of <see cref="TooltipEchoBox"/>, the shared bottom
    /// "hovered control help text" strip.
    ///
    /// <para>Only ONE decision in the helper is Unity-free, and it is all that is left
    /// after the strip became a fixed-height, always-visible box: what text to render.
    /// <see cref="TooltipEchoBox.ResolveCapturedText"/> is that rule - a window-computed
    /// manual override outranks the generic GUI.tooltip, because a window that resolved
    /// its own hover string (RecordingsTableUI's clamped loop-period cell, Real Spawn
    /// Control's bottom-row Warp button) has already answered more specifically.</para>
    ///
    /// <para>There is deliberately no show / hide or style branch to pin any more: the
    /// strip emits the same two controls at the same constant size on every pass, which
    /// is the property that keeps the window height and its bottom row from moving when
    /// the pointer crosses a control. That size is measured from a live
    /// <c>GUI.skin</c> and is pinned in-game by <c>LogisticsTooltipEchoImguiTest</c> and
    /// <c>TooltipEchoWrapSizingImguiTest</c>.</para>
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
    }
}
