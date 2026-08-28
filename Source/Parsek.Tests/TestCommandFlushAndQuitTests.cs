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

        // The suppression must own only the file the restore actually wrote, and only
        // while nothing has mutated since. Anything wider silently discards a real save.
        [Theory]
        [InlineData(true, "campaign", "campaign", true)]        // same folder, still latched
        [InlineData(true, "campaign", "other-campaign", false)] // nothing restored THAT file
        [InlineData(true, "Campaign", "campaign", false)]       // different folder when case matters
        [InlineData(false, "campaign", "campaign", false)]      // cleared / never latched
        [InlineData(true, null, "campaign", false)]             // unresolvable -> fail-safe: save
        [InlineData(true, "campaign", null, false)]
        [InlineData(true, "", "campaign", false)]
        [InlineData(true, "campaign", "", false)]
        public void ShouldSuppressSaveAfterBatchRestore_OnlyForTheRestoredFolder(
            bool latched, string restoredFolder, string currentFolder, bool expected)
        {
            Assert.Equal(expected, TestCommandFlushAndQuit.ShouldSuppressSaveAfterBatchRestore(
                latched, restoredFolder, currentFolder));
        }

        // catches: S4.2-refly-world-preservation losing its merge tail. Its steps are
        // RunTests -> AnswerMergeDialog{merge} -> FlushAndQuit and the produced save MUST
        // be the merge written AFTER the batch, so AnswerMergeDialog has to count as
        // mutating (the dispatch hook then clears the latch). The verbs that cannot change
        // what a save captures must NOT clear it, or the H38-H41 teardown -> FlushAndQuit
        // suppression could never hold either.
        [Theory]
        [InlineData("AnswerMergeDialog", true)]
        [InlineData("RunTests", true)]
        [InlineData("LoadGame", true)]
        [InlineData("CommitTree", true)]
        [InlineData("SaveGame", true)]
        [InlineData("InvokeRewind", true)]
        [InlineData("RecordingState", false)]
        [InlineData("ExportRenderManifest", false)]
        [InlineData("FlushAndQuit", false)]
        public void IsStateMutatingVerb_ClassifiesTheSeamVerbs(string verb, bool expected)
        {
            Assert.Equal(expected, TestCommandVerbs.IsStateMutatingVerb(verb));
        }

        // Fail-safe direction: an unknown or missing verb counts as mutating, so a verb
        // added later without touching the table clears the latch (the save happens)
        // rather than silently suppressing someone's save.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("SomeVerbAddedLater")]
        public void IsStateMutatingVerb_UnknownVerbsCountAsMutating(string verb)
        {
            Assert.True(TestCommandVerbs.IsStateMutatingVerb(verb));
        }

        // Every non-mutating name must be a real implemented verb: a typo there would
        // never match anything, so the exemption it was meant to grant would silently
        // not exist.
        [Fact]
        public void NonMutatingVerbs_AreAllImplementedVerbs()
        {
            foreach (string verb in TestCommandVerbs.NonMutatingVerbNames)
                Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify(verb));
        }

        [Fact]
        public void BuildPayload_ReflectsSavedFlag()
        {
            Assert.Equal("true", Val(TestCommandFlushAndQuit.BuildPayload(true), "saved"));
            Assert.Equal("false", Val(TestCommandFlushAndQuit.BuildPayload(false), "saved"));
        }
    }
}
