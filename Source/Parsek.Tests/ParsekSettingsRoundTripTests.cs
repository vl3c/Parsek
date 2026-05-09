using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Round-trip tests for settings fields that are mirrored through
    /// <see cref="ParsekSettingsPersistence"/> so user intent survives
    /// rewind, quickload, and KSP session restart.
    /// </summary>
    [Collection("Sequential")]
    public class ParsekSettingsRoundTripTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ParsekSettingsRoundTripTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekSettingsPersistence.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekSettingsPersistence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void ShowCommittedFutureOverlays_RoundTripsThroughPersistentStore()
        {
            var settings = new ParsekSettings { showCommittedFutureOverlays = true };
            ParsekSettingsPersistence.SetStoredShowCommittedFutureOverlaysForTesting(false);

            ParsekSettingsPersistence.ApplyTo(settings);

            Assert.False(settings.showCommittedFutureOverlays);
            Assert.Equal(false, ParsekSettingsPersistence.GetStoredShowCommittedFutureOverlays());
            Assert.Contains(logLines, line =>
                line.Contains("[SettingsStore]") &&
                line.Contains("showCommittedFutureOverlays") &&
                line.Contains("True -> False"));
        }

        [Fact]
        public void BlockCommittedActions_RoundTripsThroughPersistentStore()
        {
            var settings = new ParsekSettings { blockCommittedActions = true };
            ParsekSettingsPersistence.SetStoredBlockCommittedActionsForTesting(false);

            ParsekSettingsPersistence.ApplyTo(settings);

            Assert.False(settings.blockCommittedActions);
            Assert.Equal(false, ParsekSettingsPersistence.GetStoredBlockCommittedActions());
            Assert.Contains(logLines, line =>
                line.Contains("[SettingsStore]") &&
                line.Contains("blockCommittedActions") &&
                line.Contains("True -> False"));
        }
    }
}
