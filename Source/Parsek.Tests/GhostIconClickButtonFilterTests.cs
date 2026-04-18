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
        public void TryPassThroughNonLeftClick_Right_ReturnsTrue_AndLogsVerbosePassThroughUnderGhostMapTag()
        {
            Mouse.Buttons btns = Mouse.Buttons.Right;
            bool passedThrough = GhostIconClickPatch.TryPassThroughNonLeftClick(btns);
            Assert.True(passedThrough);

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]")
                && l.Contains("[GhostMap]")
                && l.Contains("non-left click")
                && l.Contains("button=Right")
                && l.Contains("passing through"));
        }

        [Fact]
        public void TryPassThroughNonLeftClick_Left_ReturnsFalse_AndDoesNotLog()
        {
            Mouse.Buttons btns = Mouse.Buttons.Left;
            bool passedThrough = GhostIconClickPatch.TryPassThroughNonLeftClick(btns);
            Assert.False(passedThrough);

            Assert.DoesNotContain(logLines, l => l.Contains("non-left click"));
            Assert.DoesNotContain(logLines, l => l.Contains("passing through"));
        }
    }
}
