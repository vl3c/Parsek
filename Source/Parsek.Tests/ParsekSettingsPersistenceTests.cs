using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for ParsekSettingsPersistence — the external store that keeps
    /// user-intent settings alive across rewind, save/load, and session restart.
    /// </summary>
    [Collection("Sequential")]
    public class ParsekSettingsPersistenceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ParsekSettingsPersistenceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekSettingsPersistence.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekSettingsPersistence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void GetStoredGhostCameraCutoffKm_DefaultsNull()
        {
            Assert.Null(ParsekSettingsPersistence.GetStoredGhostCameraCutoffKm());
        }

        [Fact]
        public void SetStoredGhostCameraCutoffKm_RoundTrips()
        {
            ParsekSettingsPersistence.SetStoredGhostCameraCutoffKmForTesting(1234.5f);
            Assert.Equal(1234.5f, ParsekSettingsPersistence.GetStoredGhostCameraCutoffKm());
        }

        [Fact]
        public void ResetForTesting_ClearsStoredValue()
        {
            ParsekSettingsPersistence.SetStoredGhostCameraCutoffKmForTesting(500f);
            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredGhostCameraCutoffKm());
        }

        // Tests for ApplyTo / RecordGhostCameraCutoff that touch ParsekSettings.Current
        // or hit disk are deliberately omitted — ParsekSettings.Current requires a live
        // HighLogic.CurrentGame (Unity/KSP runtime), and Save() writes to GameData which
        // isn't present in CI. The store's pure state transitions above cover the
        // tested logic; end-to-end is verified by in-game playtest.
    }
}
