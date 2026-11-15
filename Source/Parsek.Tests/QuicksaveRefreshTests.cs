using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The save-refresh seam of RecordingStore. A merge writes no save at all
    /// (design-load-restores-the-save.md 4.6); RefreshSaveAndQuicksaveAfterDiscard refreshes
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

            TreeDiscardService.RefreshSaveAndQuicksaveAfterDiscard("discard reason", 3);

            Assert.Single(saveCalls);
            Assert.Equal("persistent", saveCalls[0].saveName);
            Assert.Equal(SaveMode.OVERWRITE, saveCalls[0].mode);
            Assert.Contains(logLines, l =>
                l.Contains("[Quicksave]") && l.Contains("Refreshed persistent.sfs after discard reason"));
            Assert.Contains(logLines, l =>
                l.Contains("[Quicksave]")
                && l.Contains("Quicksave not refreshed after discard (the player's F5 point is kept): reason=discard reason"));
        }
    }
}
