using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TooltipBubbleTests
    {
        public TooltipBubbleTests()
        {
            TooltipBubble.ResetForTesting();
        }

        [Fact]
        public void ShouldShow_WaitsForOneSecondOfStableHover()
        {
            Assert.False(TooltipBubble.ShouldShow("Help text", 10f, 10.999f));
            Assert.True(TooltipBubble.ShouldShow("Help text", 10f, 11f));
            Assert.False(TooltipBubble.ShouldShow(string.Empty, 10f, 12f));
        }

        [Fact]
        public void ShouldResetHover_TracksPointerAndTooltipChanges()
        {
            Assert.False(TooltipBubble.ShouldResetHover("Help text", "Help text", pointerInWindow: true));
            Assert.True(TooltipBubble.ShouldResetHover("Help text", "Help text", pointerInWindow: false));
            Assert.True(TooltipBubble.ShouldResetHover("Help text", string.Empty, pointerInWindow: true));
            Assert.True(TooltipBubble.ShouldResetHover("Old help", "New help", pointerInWindow: true));
        }

        [Fact]
        public void ComputeTextWidth_ClampsToUsableWindowWidth()
        {
            float textWidth = TooltipBubble.ComputeTextWidth(naturalWidth: 500f, windowWidth: 180f);

            Assert.Equal(152f, textWidth);
        }

        [Fact]
        public void ComputeBubbleRect_KeepsBubbleInsideWindow()
        {
            Rect rect = TooltipBubble.ComputeBubbleRect(
                new Vector2(195f, 115f),
                new Vector2(80f, 40f),
                new Rect(0f, 0f, 200f, 120f));

            Assert.InRange(rect.xMin, 6f, 114f);
            Assert.InRange(rect.yMin, 6f, 74f);
            Assert.InRange(rect.xMax, 86f, 194f);
            Assert.InRange(rect.yMax, 46f, 114f);
        }

        [Fact]
        public void ComputeLocalBounds_InfersAutoSizedGUILayoutWindowHeight()
        {
            Rect bounds = TooltipBubble.ComputeLocalBounds(
                new Rect(10f, 20f, 250f, 0f),
                new Rect(8f, 120f, 220f, 24f));

            Assert.Equal(250f, bounds.width);
            Assert.Equal(150f, bounds.height);
        }

        [Fact]
        public void ComputeLocalBounds_PreservesKnownWindowHeight()
        {
            Rect bounds = TooltipBubble.ComputeLocalBounds(
                new Rect(10f, 20f, 250f, 180f),
                new Rect(8f, 120f, 220f, 24f));

            Assert.Equal(250f, bounds.width);
            Assert.Equal(180f, bounds.height);
        }
    }
}
