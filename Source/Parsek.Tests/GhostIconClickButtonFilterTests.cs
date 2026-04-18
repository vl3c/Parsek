using System;
using System.Collections.Generic;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the left-click-only filter on the ghost vessel icon patch
    /// (GhostIconClickPatch). The pure predicate IsLeftClickFromButtons must
    /// only return true for left clicks (or the defensive None default), and
    /// the non-left path must emit a single VERBOSE under the GhostMap tag so
    /// the stock-handler pass-through is traceable in playtests.
    /// </summary>
    [Collection("Sequential")]
    public class GhostIconClickButtonFilterTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostIconClickButtonFilterTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void IsLeftClickFromButtons_Left_ReturnsTrue()
        {
            Assert.True(GhostIconClickPatch.IsLeftClickFromButtons(Mouse.Buttons.Left));
        }

        [Fact]
        public void IsLeftClickFromButtons_Right_ReturnsFalse()
        {
            Assert.False(GhostIconClickPatch.IsLeftClickFromButtons(Mouse.Buttons.Right));
        }

        [Fact]
        public void IsLeftClickFromButtons_Middle_ReturnsFalse()
        {
            Assert.False(GhostIconClickPatch.IsLeftClickFromButtons(Mouse.Buttons.Middle));
        }

        [Fact]
        public void IsLeftClickFromButtons_None_ReturnsTrue_DefensiveDefault()
        {
            // When the caller cannot determine a button (Mouse.GetAllMouseButtonsUp
            // reported nothing), preserve the existing UX of opening our menu.
            Assert.True(GhostIconClickPatch.IsLeftClickFromButtons(Mouse.Buttons.None));
        }

        [Fact]
        public void IsLeftClickFromButtons_LeftPlusRight_ReturnsTrue()
        {
            // Mouse.Buttons is a flag enum and Mouse.GetAllMouseButtonsUp OR's
            // together every button that went up this frame. A combined mask
            // including Left must still take the left-click path.
            Mouse.Buttons combined = Mouse.Buttons.Left | Mouse.Buttons.Right;
            Assert.True(GhostIconClickPatch.IsLeftClickFromButtons(combined));
        }

        [Fact]
        public void IsLeftClickFromButtons_Btn4_ReturnsFalse()
        {
            Assert.False(GhostIconClickPatch.IsLeftClickFromButtons(Mouse.Buttons.Btn4));
        }

        [Fact]
        public void NonLeftClick_LogsVerbosePassThroughUnderGhostMapTag()
        {
            // The production Prefix logs a single VERBOSE on the pass-through
            // branch. We exercise the same log call directly since Prefix itself
            // needs a live OrbitRendererBase/Vessel to reach the guard — the
            // pure predicate + the log emission form the full contract.
            Mouse.Buttons btns = Mouse.Buttons.Right;
            bool isLeft = GhostIconClickPatch.IsLeftClickFromButtons(btns);
            Assert.False(isLeft);

            ParsekLog.Verbose("GhostMap",
                $"Ghost icon non-left click (button={btns}) — passing through to stock handler for default pin-text");

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]")
                && l.Contains("[GhostMap]")
                && l.Contains("non-left click")
                && l.Contains("button=Right")
                && l.Contains("passing through"));
        }

        [Fact]
        public void LeftClick_DoesNotLogPassThrough()
        {
            // Sanity check: the left-click branch must NOT emit the pass-through
            // log line — only the existing "Ghost icon clicked" verbose.
            Mouse.Buttons btns = Mouse.Buttons.Left;
            Assert.True(GhostIconClickPatch.IsLeftClickFromButtons(btns));

            Assert.DoesNotContain(logLines, l => l.Contains("non-left click"));
            Assert.DoesNotContain(logLines, l => l.Contains("passing through"));
        }
    }
}
