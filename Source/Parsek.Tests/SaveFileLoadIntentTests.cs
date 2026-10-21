using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure cells for <see cref="SaveFileLoadIntent"/>: arm, consume, the staleness window, the
    /// main-menu clear and the flight-state UT read from the node stock passes to
    /// <c>onGameStatePostLoad</c>. Design: docs/dev/design-load-restores-the-save.md 4.1.
    /// </summary>
    [Collection("Sequential")]
    public class SaveFileLoadIntentTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SaveFileLoadIntentTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            SaveFileLoadIntent.ResetForTesting();
        }

        public void Dispose()
        {
            SaveFileLoadIntent.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void Consume_WithNothingArmed_ReturnsFalse()
        {
            Assert.False(SaveFileLoadIntent.TryConsume(10.0, 100, "test", out double ut, out int frames));
            Assert.True(double.IsNaN(ut));
            Assert.Equal(-1, frames);
        }

        [Fact]
        public void Arm_ThenConsume_ReturnsTrueOnceWithTheNodeUT()
        {
            SaveFileLoadIntent.Arm(2000.5, "FLIGHT", 50.0, 400);
            Assert.True(SaveFileLoadIntent.IsArmed);
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]")
                && l.Contains("Save-file load intent armed: nodeUT=2000.50 scene=FLIGHT"));

            Assert.True(SaveFileLoadIntent.TryConsume(51.0, 403, "OnLoad", out double ut, out int frames));
            Assert.Equal(2000.5, ut);
            Assert.Equal(3, frames);
            Assert.False(SaveFileLoadIntent.IsArmed);
            Assert.False(SaveFileLoadIntent.TryConsume(52.0, 404, "OnLoad", out _, out _));
        }

        [Fact]
        public void Consume_AfterTheWindow_DropsTheIntentAsStale()
        {
            SaveFileLoadIntent.Arm(100.0, "SPACECENTER", 10.0, 1);
            Assert.False(SaveFileLoadIntent.TryConsume(
                10.0 + SaveFileLoadIntent.StaleAfterRealtimeSeconds + 1.0, 5000, "OnLoad", out _, out _));
            Assert.False(SaveFileLoadIntent.IsArmed);
            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("Save-file load intent dropped reason=stale"));
        }

        [Theory]
        [InlineData(0.0, false)]
        [InlineData(119.9, false)]
        [InlineData(120.0, false)]
        [InlineData(120.1, true)]
        [InlineData(-0.5, true)]
        [InlineData(double.NaN, true)]
        [InlineData(double.PositiveInfinity, true)]
        public void IsStale_Window(double ageSeconds, bool expected)
        {
            Assert.Equal(expected, SaveFileLoadIntent.IsStale(ageSeconds));
        }

        [Fact]
        public void Clear_DropsAnArmedIntent()
        {
            SaveFileLoadIntent.Arm(5.0, "MAINMENU", 1.0, 1);
            SaveFileLoadIntent.Clear("main-menu");
            Assert.False(SaveFileLoadIntent.IsArmed);
            Assert.Contains(logLines, l => l.Contains("Save-file load intent dropped reason=main-menu"));
            Assert.False(SaveFileLoadIntent.TryConsume(2.0, 2, "OnLoad", out _, out _));
        }

        [Fact]
        public void Rearm_BeforeConsume_WarnsAndKeepsTheNewest()
        {
            SaveFileLoadIntent.Arm(5.0, "FLIGHT", 1.0, 1);
            SaveFileLoadIntent.Arm(9.0, "FLIGHT", 2.0, 2);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("re-armed"));
            Assert.True(SaveFileLoadIntent.TryConsume(3.0, 3, "OnLoad", out double ut, out _));
            Assert.Equal(9.0, ut);
        }

        [Fact]
        public void ReadNodeUT_ReadsFlightStateFromTheGameNodeOrAWrappingRoot()
        {
            var game = new ConfigNode("GAME");
            game.AddNode("FLIGHTSTATE").AddValue("UT", "1234.5");
            Assert.Equal(1234.5, SaveFileLoadIntent.ReadNodeUT(game));

            var root = new ConfigNode();
            root.AddNode(game);
            Assert.Equal(1234.5, SaveFileLoadIntent.ReadNodeUT(root));

            Assert.True(double.IsNaN(SaveFileLoadIntent.ReadNodeUT(null)));
            Assert.True(double.IsNaN(SaveFileLoadIntent.ReadNodeUT(new ConfigNode("GAME"))));
        }
    }
}
