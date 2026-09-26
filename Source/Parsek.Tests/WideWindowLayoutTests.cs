using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek;
using Parsek.TestCommands;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for <see cref="WideWindowLayout"/> and the scroll decision of
    /// <see cref="WideWindowScroll"/>: the screen width cap, the on-screen clamp, the
    /// resize floor on a narrow screen, the natural-versus-available decision, the scroll
    /// offset clamp, and the transition-only logging.
    ///
    /// <para>The property that carries the weight is the NO-OP one: on a screen the window
    /// fits, neither the fit nor the scroll decision may change anything, because the owner
    /// requirement is that such a player sees the same pixels as before.</para>
    /// </summary>
    [Collection("Sequential")]
    public class WideWindowLayoutTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public WideWindowLayoutTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ----- EffectiveMinWidth -----

        [Theory]
        [InlineData(1355f, 1920f, 1355f)] // fits: the window's own minimum
        [InlineData(1355f, 1280f, 1280f)] // narrow screen: the screen width
        [InlineData(610f, 1024f, 610f)]
        [InlineData(1410f, 1024f, 1024f)]
        [InlineData(1355f, 0f, 1355f)]   // no screen read: unchanged
        public void EffectiveMinWidth_IsTheSmallerOfMinimumAndScreen(float min, float screen,
            float expected)
        {
            Assert.Equal(expected, WideWindowLayout.EffectiveMinWidth(min, screen));
        }

        // ----- ResizeDragWidth -----

        [Fact]
        public void ResizeDrag_StopsAtTheScreenRightEdge()
        {
            // Window at x=100 on a 1280 screen, mouse dragged past the edge to 1500: the
            // right edge stops at 1280, so the width is 1180 and the left edge stays put.
            Assert.Equal(1180f, WideWindowLayout.ResizeDragWidth(1500f, 100f, 350f, 1280f));
        }

        [Fact]
        public void ResizeDrag_NeverGoesBelowTheScreenCappedMinimum()
        {
            // Missions (min 1355) on a 1280 screen: the floor is 1280, not 1355.
            Assert.Equal(1280f, WideWindowLayout.ResizeDragWidth(500f, 0f, 1355f, 1280f));
            // On a wide screen the window's own minimum still holds.
            Assert.Equal(1355f, WideWindowLayout.ResizeDragWidth(500f, 0f, 1355f, 1920f));
        }

        [Fact]
        public void ResizeDrag_InsideTheScreenIsTheMouseDistance()
        {
            Assert.Equal(900f, WideWindowLayout.ResizeDragWidth(1000f, 100f, 350f, 1920f));
        }

        // ----- FitToScreen -----

        [Fact]
        public void Fit_AWindowAlreadyOnScreenIsUntouched()
        {
            var rect = new Rect(0f, 8f, 1355f, 700f);
            Assert.False(WideWindowLayout.FitToScreen(ref rect, 0f, 1920f, 1080f));
            Assert.Equal(new Rect(0f, 8f, 1355f, 700f), rect);
        }

        [Fact]
        public void Fit_AWindowWiderThanTheScreenIsCappedToItAndMovedToZero()
        {
            var rect = new Rect(260f, 8f, 1355f, 700f);
            Assert.True(WideWindowLayout.FitToScreen(ref rect, 0f, 1280f, 720f));
            Assert.Equal(1280f, rect.width);
            Assert.Equal(0f, rect.x);
            Assert.Equal(8f, rect.y);
            Assert.Equal(700f, rect.height);
        }

        [Fact]
        public void Fit_AWindowPastTheRightEdgeIsMovedLeftNotNarrowed()
        {
            var rect = new Rect(1500f, 40f, 820f, 400f);
            Assert.True(WideWindowLayout.FitToScreen(ref rect, 0f, 1920f, 1080f));
            Assert.Equal(1100f, rect.x);
            Assert.Equal(820f, rect.width);
        }

        [Fact]
        public void Fit_NegativePositionsAreClampedToZero()
        {
            var rect = new Rect(-50f, -20f, 400f, 300f);
            Assert.True(WideWindowLayout.FitToScreen(ref rect, 0f, 1280f, 720f));
            Assert.Equal(0f, rect.x);
            Assert.Equal(0f, rect.y);
        }

        [Fact]
        public void Fit_ABottomOverhangIsMovedUpButTheHeightIsKept()
        {
            var rect = new Rect(10f, 600f, 400f, 300f);
            Assert.True(WideWindowLayout.FitToScreen(ref rect, 0f, 1280f, 720f));
            Assert.Equal(420f, rect.y);
            Assert.Equal(300f, rect.height);
        }

        [Fact]
        public void Fit_AWindowTallerThanTheScreenPinsItsTopAtZero()
        {
            var rect = new Rect(10f, 100f, 400f, 900f);
            Assert.True(WideWindowLayout.FitToScreen(ref rect, 0f, 1280f, 720f));
            Assert.Equal(0f, rect.y);
            Assert.Equal(900f, rect.height);
        }

        [Fact]
        public void Fit_AnUnknownScreenChangesNothing()
        {
            var rect = new Rect(5000f, 5000f, 5000f, 5000f);
            Assert.False(WideWindowLayout.FitToScreen(ref rect, 1355f, 0f, 0f));
            Assert.Equal(new Rect(5000f, 5000f, 5000f, 5000f), rect);
        }

        [Fact]
        public void Fit_ACappedWindowGrowsBackToItsMinimumOnAWiderScreen()
        {
            // Capped to 1280 on a narrow screen, then the player switches to 1920: the
            // window returns to its own 1355 floor instead of scrolling forever.
            var rect = new Rect(0f, 8f, 1280f, 700f);
            Assert.True(WideWindowLayout.FitToScreen(ref rect, 1355f, 1920f, 1080f));
            Assert.Equal(1355f, rect.width);
        }

        [Fact]
        public void Fit_AWindowAtOrAboveItsMinimumIsNotRaised()
        {
            var rect = new Rect(0f, 8f, 1400f, 700f);
            Assert.False(WideWindowLayout.FitToScreen(ref rect, 1355f, 1920f, 1080f));
            Assert.Equal(1400f, rect.width);
        }

        [Fact]
        public void Fit_OnANarrowScreenTheRaiseStopsAtTheScreenWidth()
        {
            var rect = new Rect(0f, 8f, 900f, 700f);
            Assert.True(WideWindowLayout.FitToScreen(ref rect, 1355f, 1024f, 768f));
            Assert.Equal(1024f, rect.width);
        }

        [Fact]
        public void Fit_IsIdempotent()
        {
            var rect = new Rect(900f, 500f, 1556f, 500f);
            Assert.True(WideWindowLayout.FitToScreen(ref rect, 0f, 1280f, 720f));
            Rect once = rect;
            Assert.False(WideWindowLayout.FitToScreen(ref rect, 0f, 1280f, 720f));
            Assert.Equal(once, rect);
        }

        // ----- NeedsHorizontalScroll -----

        [Theory]
        [InlineData(1355f, 1355f, false)] // at the natural width: unchanged layout
        [InlineData(1600f, 1355f, false)] // wider: unchanged layout
        [InlineData(1354.8f, 1355f, false)] // float noise inside the tolerance
        [InlineData(1280f, 1355f, true)]
        [InlineData(1280f, 1410f, true)]
        [InlineData(1280f, 0f, false)]    // no natural width declared
        public void NeedsHorizontalScroll_OnlyBelowTheNaturalWidth(float window, float natural,
            bool expected)
        {
            Assert.Equal(expected, WideWindowLayout.NeedsHorizontalScroll(window, natural));
        }

        // ----- scroll offset clamp -----

        [Fact]
        public void MaxScrollX_IsTheOverflowOrZero()
        {
            Assert.Equal(95f, WideWindowLayout.MaxScrollX(1355f, 1260f));
            Assert.Equal(0f, WideWindowLayout.MaxScrollX(1355f, 1400f));
        }

        [Theory]
        [InlineData(50f, 1355f, 1260f, 50f)]
        [InlineData(500f, 1355f, 1260f, 95f)]  // past the content: the far edge
        [InlineData(-5f, 1355f, 1260f, 0f)]
        [InlineData(80f, 1355f, 1400f, 0f)]    // the window grew to fit: back to zero
        public void ClampScrollX_StaysInsideTheContent(float x, float content, float view,
            float expected)
        {
            Assert.Equal(expected, WideWindowLayout.ClampScrollX(x, content, view));
        }

        [Fact]
        public void ClampScrollX_NaNIsZero()
        {
            Assert.Equal(0f, WideWindowLayout.ClampScrollX(float.NaN, 1355f, 1260f));
        }

        // ----- the latch -----

        [Fact]
        public void Decide_AtTheNaturalWidthStaysOffAndLogsNothing()
        {
            var wide = new WideWindowScroll("Missions window", 1355f);
            Assert.False(wide.Decide(1355f));
            Assert.False(wide.Decide(1920f));
            Assert.DoesNotContain(logLines, l => l.Contains("horizontal scroll"));
        }

        [Fact]
        public void Decide_LogsOnTransitionsOnly()
        {
            var wide = new WideWindowScroll("Missions window", 1355f);
            Assert.True(wide.Decide(1280f));
            Assert.True(wide.Decide(1280f));
            Assert.True(wide.Decide(1300f));
            Assert.False(wide.Decide(1355f));

            Assert.Equal(1, logLines.FindAll(l => l.Contains("[UI]")
                && l.Contains("Missions window horizontal scroll ON: window w=1280 < natural w=1355")).Count);
            Assert.Equal(1, logLines.FindAll(l => l.Contains("[UI]")
                && l.Contains("Missions window horizontal scroll OFF: window w=1355 >= natural w=1355")).Count);
            Assert.Equal(2, logLines.FindAll(l => l.Contains("horizontal scroll")).Count);
        }

        [Fact]
        public void ScrollX_ReadsZeroAndDropsWritesWhileTheWindowFits()
        {
            var wide = new WideWindowScroll("Missions window", 1355f);
            wide.ScrollX = 60f;
            Assert.Equal(0f, wide.ScrollX);

            wide.Decide(1280f);
            wide.ScrollX = 60f;
            Assert.Equal(60f, wide.ScrollX);
        }

        [Fact]
        public void LeavingTheScrollingStateResetsTheOffset()
        {
            // A window that grows back to fit must not keep a stale offset for its next
            // narrow spell.
            var wide = new WideWindowScroll("Logistics window", 1410f);
            wide.Decide(1280f);
            wide.ScrollX = 100f;
            wide.Decide(1500f);
            wide.Decide(1280f);
            Assert.Equal(0f, wide.ScrollX);
        }

        // ----- log formatting is culture-invariant -----

        [Fact]
        public void TheLogLinesAreInvariantCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string fit = WideWindowLayout.FormatFitLog("Logistics window",
                    new Rect(1500.4f, 8f, 1556f, 500f), new Rect(0f, 8f, 1280f, 500f),
                    1280f, 720f);
                Assert.Equal("Logistics window fitted to screen 1280x720: x=1500->0 y=8->8 w=1556->1280", fit);
                string on = WideWindowLayout.FormatScrollTransitionLog("Missions window", true,
                    1280.4f, 1355f);
                Assert.Equal("Missions window horizontal scroll ON: window w=1280 < natural w=1355", on);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ----- the automation seam's rect op takes the same fit -----

        [Fact]
        public void SeamRectFit_CapsAResizableWindowToTheScreen()
        {
            var want = new UiActionRect { X = 0f, Y = 8f, W = 1355f, H = 700f };
            UiActionRect applied = TestCommandUiAction.FitRectToScreen(
                want, 1355f, 150f, 1280f, 720f, out bool fitted);
            Assert.True(fitted);
            Assert.Equal(1280f, applied.W);
            Assert.Equal(0f, applied.X);
        }

        [Fact]
        public void SeamRectFit_LeavesAWindowThatFitsAlone()
        {
            var want = new UiActionRect { X = 0f, Y = 8f, W = 1355f, H = 700f };
            UiActionRect applied = TestCommandUiAction.FitRectToScreen(
                want, 1355f, 150f, 1920f, 1080f, out bool fitted);
            Assert.False(fitted);
            Assert.Equal(1355f, applied.W);
        }

        [Fact]
        public void SeamRectFit_SkipsAWindowWithNoResizeHandle()
        {
            // main / settings / gloops have no minimum and are not fitted in-game either.
            var want = new UiActionRect { X = 1200f, Y = 8f, W = 400f, H = 700f };
            UiActionRect applied = TestCommandUiAction.FitRectToScreen(
                want, 0f, 0f, 1280f, 720f, out bool fitted);
            Assert.False(fitted);
            Assert.Equal(1200f, applied.X);
        }
    }
}
