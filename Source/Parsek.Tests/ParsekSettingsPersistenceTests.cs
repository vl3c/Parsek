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
        public void GetStoredReadableSidecarMirrors_DefaultsNull()
        {
            Assert.Null(ParsekSettingsPersistence.GetStoredReadableSidecarMirrors());
        }

        [Fact]
        public void GetStoredGhostRenderTracing_DefaultsNull()
        {
            Assert.Null(ParsekSettingsPersistence.GetStoredGhostRenderTracing());
        }

        [Fact]
        public void SetStoredReadableSidecarMirrors_RoundTrips()
        {
            ParsekSettingsPersistence.SetStoredReadableSidecarMirrorsForTesting(false);
            Assert.False(ParsekSettingsPersistence.GetStoredReadableSidecarMirrors().Value);
        }

        [Fact]
        public void SetStoredGhostRenderTracing_RoundTrips()
        {
            ParsekSettingsPersistence.SetStoredGhostRenderTracingForTesting(true);
            Assert.True(ParsekSettingsPersistence.GetStoredGhostRenderTracing().Value);
        }

        [Fact]
        public void RecordGhostRenderTracing_UpdatesInMemoryStore()
        {
            ParsekSettingsPersistence.RecordGhostRenderTracing(true);

            Assert.True(ParsekSettingsPersistence.GetStoredGhostRenderTracing().Value);
        }

        [Fact]
        public void ResetForTesting_ClearsStoredValue()
        {
            ParsekSettingsPersistence.SetStoredReadableSidecarMirrorsForTesting(false);
            ParsekSettingsPersistence.SetStoredGhostRenderTracingForTesting(true);
            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredReadableSidecarMirrors());
            Assert.Null(ParsekSettingsPersistence.GetStoredGhostRenderTracing());
        }

        [Fact]
        public void ApplyTo_RestoresStoredGhostRenderTracing()
        {
            ParsekSettingsPersistence.SetStoredGhostRenderTracingForTesting(true);
            var settings = new ParsekSettings { ghostRenderTracing = false };

            ParsekSettingsPersistence.ApplyTo(settings);

            Assert.True(settings.ghostRenderTracing);
            Assert.Contains(logLines, l =>
                l.Contains("[SettingsStore]")
                && l.Contains("Restored ghostRenderTracing False -> True"));
        }

        // ----- verboseLogging / samplingDensity / ghostAudioVolume (install-wide 2026-09-26) -----

        private readonly List<string> tempDirs = new List<string>();

        private string NewTempSettingsPath()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "parsek-settings-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            tempDirs.Add(dir);
            return System.IO.Path.Combine(dir, "settings.cfg");
        }

        private void DeleteTempDirs()
        {
            foreach (string dir in tempDirs)
            {
                try { System.IO.Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        // catches: one of the three moved settings missing from Save or LoadIfNeeded, so it
        // still reverts on F9 / rewind. Drives a real file write and a cold re-read (the
        // path override stands in for GameData/Parsek/PluginData, absent headless).
        [Fact]
        public void MovedSettings_RoundTripThroughTheFile()
        {
            try
            {
                string path = NewTempSettingsPath();
                ParsekSettingsPersistence.SetFilePathOverrideForTesting(path);

                ParsekSettingsPersistence.RecordVerboseLogging(false);
                ParsekSettingsPersistence.RecordSamplingDensity(2);
                ParsekSettingsPersistence.RecordGhostAudioVolume(0.35f);
                Assert.True(System.IO.File.Exists(path));

                ParsekSettingsPersistence.ForgetLoadedStateForTesting();
                Assert.Null(ParsekSettingsPersistence.GetStoredVerboseLogging());

                var settings = new ParsekSettings
                {
                    verboseLogging = true,
                    samplingDensity = 1,
                    ghostAudioVolume = 0.7f,
                    uiComplexityMode = 1
                };
                ParsekSettingsPersistence.ApplyTo(settings);

                Assert.False(ParsekSettingsPersistence.GetStoredVerboseLogging().Value);
                Assert.Equal(2, ParsekSettingsPersistence.GetStoredSamplingDensity().Value);
                Assert.Equal(0.35, (double)ParsekSettingsPersistence.GetStoredGhostAudioVolume().Value, 6);
                Assert.False(settings.verboseLogging);
                Assert.Equal(SamplingDensity.High, settings.SamplingDensityLevel);
                Assert.Equal(0.35, (double)settings.ghostAudioVolume, 6);
                Assert.Contains(logLines, l => l.Contains("[SettingsStore]")
                    && l.Contains("Restored samplingDensity 1 -> 2"));
                Assert.Contains(logLines, l => l.Contains("[SettingsStore]")
                    && l.Contains("Restored ghostAudioVolume 0.7 -> 0.35"));
                Assert.Contains(logLines, l => l.Contains("[SettingsStore]")
                    && l.Contains("Restored verboseLogging True -> False"));
            }
            finally
            {
                DeleteTempDirs();
            }
        }

        // catches: the file carrying a culture-formatted float ("0,35") that the invariant
        // reader then rejects, which would silently drop the stored volume on de-DE / ro-RO.
        [Fact]
        public void GhostAudioVolume_IsWrittenInvariantUnderACommaCulture()
        {
            var prior = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                string path = NewTempSettingsPath();
                ParsekSettingsPersistence.SetFilePathOverrideForTesting(path);

                ParsekSettingsPersistence.RecordGhostAudioVolume(0.35f);

                string text = System.IO.File.ReadAllText(path);
                Assert.Contains("ghostAudioVolume = 0.35", text);
                Assert.DoesNotContain("0,35", text);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prior;
                DeleteTempDirs();
            }
        }

        // catches: a hand-edited or corrupt settings.cfg overriding every save with an
        // impossible preset or volume instead of being ignored or clamped.
        [Fact]
        public void MovedSettings_InvalidFileValuesAreIgnoredOrClamped()
        {
            try
            {
                string path = NewTempSettingsPath();
                System.IO.File.WriteAllText(path,
                    "verboseLogging = maybe\nsamplingDensity = 7\nghostAudioVolume = 3.5\n");
                ParsekSettingsPersistence.SetFilePathOverrideForTesting(path);

                ParsekSettingsPersistence.LoadIfNeeded();

                Assert.Null(ParsekSettingsPersistence.GetStoredVerboseLogging());
                Assert.Null(ParsekSettingsPersistence.GetStoredSamplingDensity());
                Assert.Equal(1.0, (double)ParsekSettingsPersistence.GetStoredGhostAudioVolume().Value, 6);
                Assert.Contains(logLines, l => l.Contains("[SettingsStore]")
                    && l.Contains("invalid samplingDensity='7'"));
            }
            finally
            {
                DeleteTempDirs();
            }
        }

        [Fact]
        public void RecordSamplingDensity_RefusesOutOfRange()
        {
            ParsekSettingsPersistence.SetStoredSamplingDensityForTesting(null);

            ParsekSettingsPersistence.RecordSamplingDensity(3);
            ParsekSettingsPersistence.RecordSamplingDensity(-1);

            Assert.Null(ParsekSettingsPersistence.GetStoredSamplingDensity());
            Assert.Contains(logLines, l => l.Contains("RecordSamplingDensity: refusing out-of-range value 3"));
        }

        [Fact]
        public void RecordGhostAudioVolume_ClampsAndRefusesNonFinite()
        {
            ParsekSettingsPersistence.SetStoredGhostAudioVolumeForTesting(null);

            ParsekSettingsPersistence.RecordGhostAudioVolume(float.NaN);
            Assert.Null(ParsekSettingsPersistence.GetStoredGhostAudioVolume());

            ParsekSettingsPersistence.RecordGhostAudioVolume(-0.5f);
            Assert.Equal(0.0, (double)ParsekSettingsPersistence.GetStoredGhostAudioVolume().Value, 6);
        }

        // catches: an absent key overriding the save. Only a stored value may win; an install
        // that never touched the setting keeps the per-save value.
        [Fact]
        public void ApplyTo_LeavesMovedSettingsAloneWhenNothingIsStored()
        {
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(1);
            var settings = new ParsekSettings
            {
                verboseLogging = false,
                samplingDensity = 0,
                ghostAudioVolume = 0.2f,
                uiComplexityMode = 1
            };

            ParsekSettingsPersistence.ApplyTo(settings);

            Assert.False(settings.verboseLogging);
            Assert.Equal(0, settings.samplingDensity);
            Assert.Equal(0.2, (double)settings.ghostAudioVolume, 6);
        }

        [Fact]
        public void HasAnyStoredValue_CountsTheMovedSettings()
        {
            ParsekSettingsPersistence.SetStoredSamplingDensityForTesting(null);
            Assert.False(ParsekSettingsPersistence.HasAnyStoredValue());
            ParsekSettingsPersistence.SetStoredGhostAudioVolumeForTesting(0.5f);
            Assert.True(ParsekSettingsPersistence.HasAnyStoredValue());
        }

        // ApplyTo paths that read ParsekSettings.Current are not driven here: Current
        // requires a live HighLogic.CurrentGame (Unity/KSP runtime). Disk I/O runs against
        // a temp file through SetFilePathOverrideForTesting.
    }
}
