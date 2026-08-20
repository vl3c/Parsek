using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the pure decision core of <see cref="TooltipEchoBox"/>, the shared bottom
    /// "hovered control help text" strip.
    ///
    /// <para>Two decisions live here and both are Unity-free. <see cref="TooltipEchoBox.ResolveEchoLayout"/>
    /// maps the frame's cached text to the strip's spacing / content / style / layout
    /// option - the branch that must never change the CONTROL COUNT, only what those
    /// two controls look like. <see cref="TooltipEchoBox.ResolveCapturedText"/> is the
    /// Repaint-time capture that decides what the NEXT frame renders: a window-computed
    /// manual override outranks the generic GUI.tooltip.</para>
    ///
    /// <para>The live sizing property the helper exists for - both passes of a frame
    /// rendering from the same string, so a wrapped height cannot be reserved for one
    /// string and painted with another - needs a real IMGUI Layout+Repaint cycle and is
    /// pinned in-game by <c>LogisticsTooltipEchoImguiTest</c> and
    /// <c>TooltipEchoWrapSizingImguiTest</c>.</para>
    /// </summary>
    public class TooltipEchoBoxTests
    {
        [Fact]
        public void ResolveTooltipEcho_Null_DoesNotShow()
        {
            (bool show, string text) = TooltipEchoBox.ResolveTooltipEcho(null);
            Assert.False(show);
            Assert.Equal(string.Empty, text);
        }

        [Fact]
        public void ResolveTooltipEcho_Empty_DoesNotShow()
        {
            (bool show, string text) = TooltipEchoBox.ResolveTooltipEcho("");
            Assert.False(show);
            Assert.Equal(string.Empty, text);
        }

        [Fact]
        public void ResolveTooltipEcho_NonEmpty_ShowsSameText()
        {
            const string tip = "Delete this route";
            (bool show, string text) = TooltipEchoBox.ResolveTooltipEcho(tip);
            Assert.True(show);
            Assert.Equal(tip, text);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ResolveEchoLayout_NoText_CollapsesToZeroHeightLabel(string cached)
        {
            TooltipEchoLayout layout = TooltipEchoBox.ResolveEchoLayout(cached, 3f);
            Assert.False(layout.Show);
            Assert.Equal(string.Empty, layout.Text);
            Assert.Equal(0f, layout.Spacing);
            Assert.Equal(TooltipEchoStyleKind.ZeroHeightLabel, layout.StyleKind);
            Assert.Equal(TooltipEchoWidthOption.ZeroHeight, layout.WidthOption);
        }

        [Fact]
        public void ResolveEchoLayout_WithText_UsesWrappedBoxAndSpacing()
        {
            const string tip = "Hovering any cell shows its help text here";
            TooltipEchoLayout layout = TooltipEchoBox.ResolveEchoLayout(tip, 3f);
            Assert.True(layout.Show);
            Assert.Equal(tip, layout.Text);
            Assert.Equal(3f, layout.Spacing);
            Assert.Equal(TooltipEchoStyleKind.WrappedBox, layout.StyleKind);
            Assert.Equal(TooltipEchoWidthOption.ExpandWidth, layout.WidthOption);
        }

        [Fact]
        public void ResolveEchoLayout_SpacingIsPerWindow()
        {
            // Every window passes its own SpacingSmall; the helper never hardcodes one
            // for the visible branch.
            Assert.Equal(7f, TooltipEchoBox.ResolveEchoLayout("tip", 7f).Spacing);
            // The collapsed branch always spaces 0 regardless of what was passed.
            Assert.Equal(0f, TooltipEchoBox.ResolveEchoLayout("", 7f).Spacing);
        }

        [Fact]
        public void ResolveEchoLayout_SameInputProducesSameLayout()
        {
            // The whole point of rendering both IMGUI passes from one cached string:
            // the decision is a pure function of that string, so Layout and Repaint
            // cannot disagree on size or style.
            TooltipEchoLayout a = TooltipEchoBox.ResolveEchoLayout("a long wrapped tooltip", 3f);
            TooltipEchoLayout b = TooltipEchoBox.ResolveEchoLayout("a long wrapped tooltip", 3f);
            Assert.Equal(a.Show, b.Show);
            Assert.Equal(a.Text, b.Text);
            Assert.Equal(a.Spacing, b.Spacing);
            Assert.Equal(a.StyleKind, b.StyleKind);
            Assert.Equal(a.WidthOption, b.WidthOption);
        }

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
            // The draw path branches on Length, so a null capture would throw.
            Assert.Equal(string.Empty, TooltipEchoBox.ResolveCapturedText(null, null));
            Assert.Equal(string.Empty, TooltipEchoBox.ResolveCapturedText("", null));
            Assert.Equal(string.Empty, TooltipEchoBox.ResolveCapturedText(null, ""));
        }
    }
}
