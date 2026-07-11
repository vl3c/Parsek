using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P5.8 coverage for the FlushAndQuit save gate + payload. A menu quit (no game
    /// loaded) has nothing to save; a flight quit with a resolved save folder forces a
    /// persistent save before quitting. Fails if the gate would attempt a save with no
    /// game / no save folder, or if the saved payload key drifts.
    /// </summary>
    public class TestCommandFlushAndQuitTests
    {
        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        [Theory]
        [InlineData(true, true, true)]    // game loaded + save folder -> save
        [InlineData(true, false, false)]  // game loaded but no save folder -> no save
        [InlineData(false, true, false)]  // no game (menu) -> no save
        [InlineData(false, false, false)] // no game, no folder -> no save
        public void ShouldSave_OnlyWhenGameLoadedAndSaveFolderPresent(
            bool gameLoaded, bool saveFolderPresent, bool expected)
        {
            Assert.Equal(expected, TestCommandFlushAndQuit.ShouldSave(gameLoaded, saveFolderPresent));
        }

        [Fact]
        public void BuildPayload_ReflectsSavedFlag()
        {
            Assert.Equal("true", Val(TestCommandFlushAndQuit.BuildPayload(true), "saved"));
            Assert.Equal("false", Val(TestCommandFlushAndQuit.BuildPayload(false), "saved"));
        }
    }
}
