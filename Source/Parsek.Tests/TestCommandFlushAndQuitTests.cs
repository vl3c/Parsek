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

        // catches: FlushAndQuit's SaveGame landing AFTER an in-game batch teardown has
        // reverted persistent.sfs, writing the batch's leftover in-memory state back over
        // the restore. The teardown's revert must be the last write to that file.
        [Theory]
        [InlineData(true, true, true, false)]   // batch restored -> suppressed
        [InlineData(true, true, false, true)]   // non-batch quit -> saves as before
        [InlineData(true, false, true, false)]  // suppression composes with the folder gate
        [InlineData(false, true, true, false)]  // and with the no-game gate
        public void ShouldSave_SuppressedAfterBatchBaselineRestore(
            bool gameLoaded, bool saveFolderPresent, bool batchBaselineRestored, bool expected)
        {
            Assert.Equal(expected, TestCommandFlushAndQuit.ShouldSave(
                gameLoaded, saveFolderPresent, batchBaselineRestored));
        }

        // The default keeps every pre-existing (non-batch) caller byte-identical.
        [Fact]
        public void ShouldSave_DefaultsToNotSuppressed()
        {
            Assert.True(TestCommandFlushAndQuit.ShouldSave(gameLoaded: true, saveFolderPresent: true));
        }

        [Fact]
        public void BuildPayload_ReflectsSavedFlag()
        {
            Assert.Equal("true", Val(TestCommandFlushAndQuit.BuildPayload(true), "saved"));
            Assert.Equal("false", Val(TestCommandFlushAndQuit.BuildPayload(false), "saved"));
        }
    }
}
