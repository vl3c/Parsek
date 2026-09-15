using System;
using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The hover-echo strip's observability latch: the line that turns "the four census
    /// hover captures photographed an unhovered window" from a hand-made dump / pixel
    /// comparison into something a lane can pin as a log contract.
    ///
    /// <para>Static state, so <c>[Collection("Sequential")]</c> and a reset per
    /// cell.</para>
    /// </summary>
    [Collection("Sequential")]
    public class TooltipEchoStripLatchTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TooltipEchoStripLatchTests()
        {
            TooltipEchoStripLatch.ResetForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            TooltipEchoStripLatch.ResetForTesting();
        }

        // ----- the log decision -----

        [Fact]
        public void ShouldLog_OnlyOnAChangeToANonEmptyTextItHasNotSeen()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Assert.True(TooltipEchoStripLatch.ShouldLogText("a tooltip", "", seen, 10));

            // The empty state is deliberately SILENT: the strip empties every time the
            // pointer leaves a control, so logging it would double every line - and "the
            // strip went blank" is exactly what the ABSENCE of a line says.
            Assert.False(TooltipEchoStripLatch.ShouldLogText("", "a tooltip", seen, 10));
            Assert.False(TooltipEchoStripLatch.ShouldLogText(null, "a tooltip", seen, 10));

            // Unchanged is silent too: the strip redraws this text every frame.
            Assert.False(TooltipEchoStripLatch.ShouldLogText("a", "a", seen, 10));

            seen.Add("a tooltip");
            Assert.False(TooltipEchoStripLatch.ShouldLogText("a tooltip", "", seen, 10));
        }

        [Fact]
        public void ShouldLog_StopsAtTheCap_SoASweepAcrossATableCannotFloodTheLog()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { "one", "two" };
            Assert.False(TooltipEchoStripLatch.ShouldLogText("three", "", seen, 2));
            Assert.True(TooltipEchoStripLatch.ShouldLogText("three", "", seen, 3));
        }

        [Fact]
        public void ShouldLog_IsOrdinal_SoACaseChangeIsANewText()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { "Timeline" };
            Assert.True(TooltipEchoStripLatch.ShouldLogText("timeline", "", seen, 10));
        }

        // ----- formatting -----

        [Fact]
        public void FormatForLog_FlattensANewlineSoTheLineStaysOneLine()
        {
            // A two-line tooltip must not split the line a log contract matches against.
            Assert.Equal("first\\nsecond",
                TooltipEchoStripLatch.FormatForLog("first\nsecond"));
            Assert.Equal("a\\rb", TooltipEchoStripLatch.FormatForLog("a\rb"));
            Assert.Equal("a\\tb", TooltipEchoStripLatch.FormatForLog("a\tb"));
        }

        [Fact]
        public void FormatForLog_SpellsTheEmptyTextWithTheSentinel()
        {
            Assert.Equal(TooltipEchoStripLatch.EmptyTextToken,
                TooltipEchoStripLatch.FormatForLog(null));
            Assert.Equal(TooltipEchoStripLatch.EmptyTextToken,
                TooltipEchoStripLatch.FormatForLog(""));
            Assert.Equal("-", TooltipEchoStripLatch.EmptyTextToken);
        }

        [Fact]
        public void FormatForLog_TruncatesWithAMarkerRatherThanWrappingAParagraph()
        {
            string longText = new string('x', TooltipEchoStripLatch.MaxLoggedTextLength + 50);
            string formatted = TooltipEchoStripLatch.FormatForLog(longText);
            Assert.Equal(TooltipEchoStripLatch.MaxLoggedTextLength
                         + TooltipEchoStripLatch.TruncationMarker.Length,
                         formatted.Length);
            Assert.EndsWith(TooltipEchoStripLatch.TruncationMarker, formatted);
        }

        // ----- observation -----

        [Fact]
        public void Observe_LogsTheTextOnceAndLatchesItForThePointerOpToRead()
        {
            TooltipEchoStripLatch.Observe("Supply routes that repeat a delivery.", 4242);

            Assert.Equal("Supply routes that repeat a delivery.",
                TooltipEchoStripLatch.LastText);
            Assert.Equal(4242, TooltipEchoStripLatch.LastFrame);
            Assert.Contains(logLines, l => l.Contains("[UI]")
                && l.Contains("tooltip echo strip text=Supply routes that repeat a delivery.")
                && l.Contains("frame=4242"));

            int after = logLines.Count;
            TooltipEchoStripLatch.Observe("Supply routes that repeat a delivery.", 4243);
            Assert.Equal(after, logLines.Count);
        }

        [Fact]
        public void Observe_LeavesTheFrameAloneWhenTheStripGoesBlank()
        {
            // The frame stamp is what lets a reader tell a live hover from a value left
            // over from a strip that drew minutes ago, so it must track the last NON-EMPTY
            // text rather than the last draw.
            TooltipEchoStripLatch.Observe("a tooltip", 100);
            TooltipEchoStripLatch.Observe("", 101);
            Assert.Equal("", TooltipEchoStripLatch.LastText);
            Assert.Equal(100, TooltipEchoStripLatch.LastFrame);
        }

        [Fact]
        public void Observe_ReportsTheCapOnceAndThenGoesQuiet()
        {
            for (int i = 0; i < TooltipEchoStripLatch.MaxLoggedTexts; i++)
                TooltipEchoStripLatch.Observe("text " + i, i);
            int atCap = logLines.Count;

            TooltipEchoStripLatch.Observe("one past the cap", 9001);
            Assert.Contains(logLines, l => l.Contains("tooltip echo strip log cap reached"));
            int afterCapLine = logLines.Count;
            Assert.Equal(atCap + 1, afterCapLine);

            // And the cap line itself does not repeat.
            TooltipEchoStripLatch.Observe("two past the cap", 9002);
            Assert.Equal(afterCapLine, logLines.Count);
        }

        // ----- the mouse-position probe -----

        [Fact]
        public void Probe_FiresOnceWithBothPositions_AndOnlyWhenArmed()
        {
            // Unarmed: the common case, and it must cost nothing and say nothing.
            TooltipEchoStripLatch.SampleMousePositionProbe(
                1f, 2f, 3f, 4f, 5f, 6f, 720, 7);
            Assert.DoesNotContain(logLines, l => l.Contains("pointer probe"));

            TooltipEchoStripLatch.ArmMousePositionProbe();
            Assert.True(TooltipEchoStripLatch.ProbeArmed);
            TooltipEchoStripLatch.SampleMousePositionProbe(
                115f, 11f, 133f, 196f, 133f, 524f, 720, 8899);
            Assert.False(TooltipEchoStripLatch.ProbeArmed);

            // Both frames on one line, plus the flip of the Unity value, so the reader can
            // compare a window-local IMGUI point with a screen-space cursor without doing
            // the arithmetic.
            Assert.Contains(logLines, l => l.Contains("[UI]")
                && l.Contains("eventLocal=115.0,11.0")
                && l.Contains("eventScreen=133.0,196.0")
                && l.Contains("input=133.0,524.0")
                && l.Contains("inputGuiY=196.0")
                && l.Contains("screenH=720"));

            // And it does not fire a second time on the next strip repaint.
            int after = logLines.Count;
            TooltipEchoStripLatch.SampleMousePositionProbe(
                115f, 11f, 133f, 196f, 133f, 524f, 720, 8900);
            Assert.Equal(after, logLines.Count);
        }

        [Fact]
        public void Probe_ReportsInvariantDecimals_EvenUnderACommaCulture()
        {
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                TooltipEchoStripLatch.ArmMousePositionProbe();
                TooltipEchoStripLatch.SampleMousePositionProbe(
                    1.5f, 2.5f, 3.5f, 4.5f, 5.5f, 6.5f, 720, 1);
                Assert.Contains(logLines, l => l.Contains("eventLocal=1.5,2.5"));
                Assert.DoesNotContain(logLines, l => l.Contains("eventLocal=1,5"));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }
    }
}
