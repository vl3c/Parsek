using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Persistence + first-run-resolution tests for the Basic / Advanced UI complexity
    /// mode (design `docs/dev/design-ui-basic-advanced.md` sections 6.2, 7.3, 13.1, 13.2).
    /// The pure `UiSurfaceVisibility` decisions live in `UiComplexityModeTests`; this file
    /// covers the wiring around them.
    ///
    /// <para>Ordering mirrors design 7.3: the STORED-value cases come first, because an
    /// absent key must be the only path that can ever reach footprint resolution.</para>
    ///
    /// <para>Touches shared static state (`ParsekSettingsPersistence`, `ParsekLog`,
    /// `ParsekSettings.CurrentOverrideForTesting`), hence [Collection("Sequential")].</para>
    /// </summary>
    [Collection("Sequential")]
    public class UiComplexityModePersistenceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public UiComplexityModePersistenceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekSettingsPersistence.ResetForTesting();
            ParsekSettings.CurrentOverrideForTesting = null;
        }

        public void Dispose()
        {
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekSettingsPersistence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Stored-value path (design 7.3, written and tested BEFORE the footprint path)
        // ------------------------------------------------------------------

        [Fact]
        public void GetStoredUiComplexityMode_DefaultsNull()
        {
            Assert.Null(ParsekSettingsPersistence.GetStoredUiComplexityMode());
        }

        // A stored key must survive Save()-shaped recording and come back out of ApplyTo
        // onto the settings object. Catches a missing AddValue / restore branch.
        [Fact]
        public void StoredUiComplexityModeRoundTripsThroughApplyTo()
        {
            ParsekSettingsPersistence.RecordUiComplexityMode((int)UiComplexityMode.Basic);
            Assert.Equal(0, ParsekSettingsPersistence.GetStoredUiComplexityMode());

            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettingsPersistence.ApplyTo(settings);

            Assert.Equal(UiComplexityMode.Basic, settings.UiComplexityModeLevel);
            Assert.Contains(logLines, l =>
                l.Contains("[SettingsStore]")
                && l.Contains("Restored uiComplexityMode 1 -> 0"));
        }

        // The precedence guard: a stored value wins even when every footprint signal is
        // screaming the other way. Fails if the footprint branch is ever allowed to run
        // ahead of, or on top of, a saved preference.
        [Fact]
        public void StoredValueWinsOverFootprintInApplyTo()
        {
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting((int)UiComplexityMode.Basic);
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };

            ParsekSettingsPersistence.ApplyTo(settings, scenarioNodePopulated: true);

            Assert.Equal(UiComplexityMode.Basic, settings.UiComplexityModeLevel);
            Assert.DoesNotContain(logLines, l => l.Contains("First-run default resolved"));
        }

        // ------------------------------------------------------------------
        // First-run footprint resolution (design 7.3)
        // ------------------------------------------------------------------

        // The session-2-flip guard of design 7.3: resolution must PERSIST, so the second
        // load sees a stored value and the footprint stops mattering. Without the
        // persist-on-resolve step a fresh install resolves Basic in session 1, then
        // silently flips itself to Advanced in session 2 once it has grown its own
        // footprint - the exact failure section 7.3 exists to prevent.
        [Fact]
        public void ResolutionIsSticky()
        {
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            var firstLoad = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Basic };
            ParsekSettingsPersistence.ApplyTo(firstLoad, scenarioNodePopulated: true);

            Assert.Equal(UiComplexityMode.Advanced, firstLoad.UiComplexityModeLevel);
            Assert.Equal((int)UiComplexityMode.Advanced,
                ParsekSettingsPersistence.GetStoredUiComplexityMode());

            // Session 2: footprint signals all absent, but the stored value now decides.
            logLines.Clear();
            var secondLoad = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Basic };
            ParsekSettingsPersistence.ApplyTo(secondLoad, scenarioNodePopulated: false);

            Assert.Equal(UiComplexityMode.Advanced, secondLoad.UiComplexityModeLevel);
            Assert.DoesNotContain(logLines, l => l.Contains("First-run default resolved"));
        }

        // No stored key and no footprint = a genuinely new player: Basic, and persisted
        // so it stays Basic next session.
        [Fact]
        public void NoFootprintResolvesAndPersistsBasic()
        {
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };

            ParsekSettingsPersistence.ApplyTo(settings, scenarioNodePopulated: false);

            Assert.Equal(UiComplexityMode.Basic, settings.UiComplexityModeLevel);
            Assert.Equal((int)UiComplexityMode.Basic,
                ParsekSettingsPersistence.GetStoredUiComplexityMode());
        }

        // Footprint signal 1, driven with a temp saves root so the dir walk is genuinely
        // exercised rather than asserted about.
        [Fact]
        public void SavesRootParsekDirectoryIsFootprintSignalOne()
        {
            string savesRoot = Path.Combine(
                Path.GetTempPath(), "parsek-ui-mode-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(savesRoot, "Career One"));
                Directory.CreateDirectory(Path.Combine(savesRoot, "Sandbox"));

                Assert.False(ParsekSettingsPersistence.SavesRootHasParsekDirectory(savesRoot));

                Directory.CreateDirectory(Path.Combine(savesRoot, "Sandbox", "Parsek"));
                Assert.True(ParsekSettingsPersistence.SavesRootHasParsekDirectory(savesRoot));

                // And the signal, once true, picks Advanced through the pure seam.
                Assert.Equal(
                    UiComplexityMode.Advanced,
                    UiSurfaceVisibility.ResolveMode(null, installHasParsekFootprint: true));
            }
            finally
            {
                try { Directory.Delete(savesRoot, true); } catch { /* temp cleanup only */ }
            }
        }

        [Fact]
        public void SavesRootHasParsekDirectory_MissingOrEmptyRootIsNoFootprint()
        {
            Assert.False(ParsekSettingsPersistence.SavesRootHasParsekDirectory(null));
            Assert.False(ParsekSettingsPersistence.SavesRootHasParsekDirectory(""));
            Assert.False(ParsekSettingsPersistence.SavesRootHasParsekDirectory(
                Path.Combine(Path.GetTempPath(), "parsek-nonexistent-" + Guid.NewGuid().ToString("N"))));
        }

        // Footprint signal 2, on its own.
        [Fact]
        public void ScenarioNodePopulatedAloneResolvesAdvanced()
        {
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);
            Assert.False(ParsekSettingsPersistence.HasAnyStoredValue());

            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Basic };
            ParsekSettingsPersistence.ApplyTo(settings, scenarioNodePopulated: true);

            Assert.Equal(UiComplexityMode.Advanced, settings.UiComplexityModeLevel);
        }

        // Footprint signal 3, on its own: any other stored settings key proves Parsek ran
        // on this install before the feature existed.
        [Fact]
        public void AnyOtherStoredSettingsKeyAloneResolvesAdvanced()
        {
            ParsekSettingsPersistence.SetStoredShowRouteLinesForTesting(true);
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);
            Assert.True(ParsekSettingsPersistence.HasAnyStoredValue());

            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Basic };
            ParsekSettingsPersistence.ApplyTo(settings, scenarioNodePopulated: false);

            Assert.Equal(UiComplexityMode.Advanced, settings.UiComplexityModeLevel);
        }

        [Fact]
        public void HasAnyStoredValue_FalseOnEmptyStore()
        {
            ParsekSettingsPersistence.ResetForTesting();
            Assert.False(ParsekSettingsPersistence.HasAnyStoredValue());
        }

        [Fact]
        public void ResetForTesting_ClearsStoredUiComplexityMode()
        {
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting((int)UiComplexityMode.Basic);
            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredUiComplexityMode());
        }

        // ------------------------------------------------------------------
        // Typed accessor (design 6.2, fail-open clamp)
        // ------------------------------------------------------------------

        [Fact]
        public void OutOfRangeStoredValueResolvesToAdvanced()
        {
            foreach (int raw in new[] { -1, 2, 7, int.MaxValue, int.MinValue })
            {
                var settings = new ParsekSettings { uiComplexityMode = raw };
                Assert.Equal(UiComplexityMode.Advanced, settings.UiComplexityModeLevel);
            }
        }

        [Fact]
        public void TypedAccessorRoundTripsInRangeValues()
        {
            var settings = new ParsekSettings();
            settings.UiComplexityModeLevel = UiComplexityMode.Basic;
            Assert.Equal(0, settings.uiComplexityMode);
            Assert.Equal(UiComplexityMode.Basic, settings.UiComplexityModeLevel);

            settings.UiComplexityModeLevel = UiComplexityMode.Advanced;
            Assert.Equal(1, settings.uiComplexityMode);
            Assert.Equal(UiComplexityMode.Advanced, settings.UiComplexityModeLevel);
        }

        // The raw field default is Advanced (fail-open), documented in ParsekSettings.
        // Pinned so a future edit cannot quietly make an unrestored read hide windows.
        [Fact]
        public void RawFieldDefaultIsAdvancedFailOpen()
        {
            Assert.Equal((int)UiComplexityMode.Advanced, new ParsekSettings().uiComplexityMode);
        }

        // ------------------------------------------------------------------
        // Log assertions (design 13.2)
        // ------------------------------------------------------------------

        // Catches silent removal of the transition diagnostic, and pins that the seam
        // (not a raw field write) is what persists the value.
        [Fact]
        public void ModeChangeLogsTransition()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);

            Assert.Equal(UiComplexityMode.Basic, settings.UiComplexityModeLevel);
            Assert.Equal((int)UiComplexityMode.Basic,
                ParsekSettingsPersistence.GetStoredUiComplexityMode());
            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("Mode changed: uiComplexityMode=Advanced->Basic"));
        }

        [Fact]
        public void ModeChangeToTheActiveModeIsANoOp()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);

            Assert.Null(ParsekSettingsPersistence.GetStoredUiComplexityMode());
            Assert.DoesNotContain(logLines, l => l.Contains("Mode changed: uiComplexityMode="));
        }

        // The first-run line must name WHICH signal fired, the chosen mode, and that it
        // was persisted (design 12.2) - otherwise a wrong default is undiagnosable from
        // a player's KSP.log.
        [Fact]
        public void FirstRunDefaultResolvedLogsSignalsModeAndPersisted()
        {
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Basic };

            ParsekSettingsPersistence.ApplyTo(settings, scenarioNodePopulated: true);

            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("First-run default resolved")
                && l.Contains("uiComplexityMode=Advanced")
                && l.Contains("installHasParsekFootprint=True")
                && l.Contains("scenarioNodePopulated=True")
                && l.Contains("persisted=true"));
        }

        [Fact]
        public void FirstRunDefaultResolvedLogsBasicWhenNoSignalFired()
        {
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };

            ParsekSettingsPersistence.ApplyTo(settings, scenarioNodePopulated: false);

            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("First-run default resolved")
                && l.Contains("uiComplexityMode=Basic")
                && l.Contains("installHasParsekFootprint=False")
                && l.Contains("savesParsekDir=False")
                && l.Contains("scenarioNodePopulated=False")
                && l.Contains("storedSettingsKeys=False")
                && l.Contains("persisted=true"));
        }
    }
}
