using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The save-refresh seams of RecordingStore. RefreshQuicksaveAfterMerge (bug #292) is
    /// no longer called by the merge dialog (design-load-restores-the-save.md 4.6) but stays
    /// until the Phase 3 deletion; RefreshSaveAndQuicksaveAfterDiscard refreshes
    /// persistent.sfs only and never writes the player's quicksave.sfs.
    /// </summary>
    [Collection("Sequential")]
    public class QuicksaveRefreshTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<(string saveName, string saveFolder, SaveMode mode)> saveCalls
            = new List<(string, string, SaveMode)>();

        public QuicksaveRefreshTests()
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
            RecordingStore.SaveGameForTesting = null;
        }

        /// <summary>
        /// Bug #292: normal call invokes the SaveGame seam with the right args
        /// and logs an Info line including the reason and recording count.
        /// </summary>
        [Fact]
        public void RefreshQuicksaveAfterMerge_NormalCall_InvokesSaveGame_LogsAtInfo()
        {
            RecordingStore.SaveGameForTesting = (saveName, saveFolder, mode) =>
            {
                saveCalls.Add((saveName, saveFolder, mode));
                return "fake-result-not-empty";
            };

            RecordingStore.RefreshQuicksaveAfterMerge("test reason", 17);

            Assert.Single(saveCalls);
            Assert.Equal("quicksave", saveCalls[0].saveName);
            Assert.Equal(SaveMode.OVERWRITE, saveCalls[0].mode);
            // Note: saveFolder will be HighLogic.SaveFolder which may be null in tests;
            // we don't assert its exact value, only that the seam was invoked.
            Assert.Contains(logLines, l =>
                l.Contains("[Quicksave]") && l.Contains("Refreshed quicksave.sfs") &&
                l.Contains("test reason") && l.Contains("17"));
        }

        /// <summary>
        /// Bug #292: SaveGame returning null is treated as failure — log a warn and
        /// don't claim success. Merge still completes, but quicksave was not refreshed.
        /// </summary>
        [Fact]
        public void RefreshQuicksaveAfterMerge_SaveGameReturnsNull_LogsWarn()
        {
            RecordingStore.SaveGameForTesting = (saveName, saveFolder, mode) =>
            {
                saveCalls.Add((saveName, saveFolder, mode));
                return null;
            };

            RecordingStore.RefreshQuicksaveAfterMerge("test reason", 5);

            Assert.Single(saveCalls);
            Assert.Contains(logLines, l =>
                l.Contains("[Quicksave]") && l.Contains("returned null") &&
                l.Contains("WARN"));
            // The Info "Refreshed" line should NOT have fired
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Quicksave]") && l.Contains("Refreshed quicksave.sfs"));
        }

        /// <summary>
        /// Bug #292: SaveGame throwing is caught — log a warn and continue.
        /// </summary>
        [Fact]
        public void RefreshQuicksaveAfterMerge_SaveGameThrows_LogsWarnAndDoesNotPropagate()
        {
            RecordingStore.SaveGameForTesting = (saveName, saveFolder, mode) =>
            {
                throw new InvalidOperationException("simulated KSP failure");
            };

            // Must not throw
            RecordingStore.RefreshQuicksaveAfterMerge("test reason", 5);

            Assert.Contains(logLines, l =>
                l.Contains("[Quicksave]") && l.Contains("Exception refreshing quicksave") &&
                l.Contains("InvalidOperationException") &&
                l.Contains("simulated KSP failure"));
        }

        /// <summary>
        /// Bug #292: the seam is invoked exactly once per call (no double-save).
        /// </summary>
        [Fact]
        public void RefreshQuicksaveAfterMerge_InvokesSaveGameExactlyOnce()
        {
            int callCount = 0;
            RecordingStore.SaveGameForTesting = (saveName, saveFolder, mode) =>
            {
                callCount++;
                return "fake-ok";
            };

            RecordingStore.RefreshQuicksaveAfterMerge("test", 1);

            Assert.Equal(1, callCount);
        }

        /// <summary>
        /// Design 4.6: a player discard refreshes the game's current save and keeps the
        /// player's F5 point, saying so in one Verbose line.
        /// </summary>
        [Fact]
        public void RefreshSaveAndQuicksaveAfterDiscard_WritesPersistentOnly_KeepsTheQuicksave()
        {
            RecordingStore.SaveGameForTesting = (saveName, saveFolder, mode) =>
            {
                saveCalls.Add((saveName, saveFolder, mode));
                return "fake-ok";
            };

            RecordingStore.RefreshSaveAndQuicksaveAfterDiscard("discard reason", 3);

            Assert.Single(saveCalls);
            Assert.Equal("persistent", saveCalls[0].saveName);
            Assert.Equal(SaveMode.OVERWRITE, saveCalls[0].mode);
            Assert.Contains(logLines, l =>
                l.Contains("[Quicksave]") && l.Contains("Refreshed persistent.sfs after discard reason"));
            Assert.Contains(logLines, l =>
                l.Contains("[Quicksave]")
                && l.Contains("Quicksave not refreshed after discard (the player's F5 point is kept): reason=discard reason"));
            Assert.DoesNotContain(logLines, l => l.Contains("Refreshed quicksave.sfs"));
        }
    }
}
