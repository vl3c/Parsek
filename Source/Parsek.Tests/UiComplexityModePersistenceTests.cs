using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
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
            ParsekUI.ResetUiComplexityModeForTesting();
        }

        public void Dispose()
        {
            ParsekUI.ResetUiComplexityModeForTesting();
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
        // (not a raw field write) is what persists the value. The Info line moved to the
        // APPLY step in phase 4 - it now marks the moment the mode takes effect, which is
        // the moment the player sees the buttons change.
        [Fact]
        public void ModeChangeLogsTransition()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
            ParsekUI.ApplyPendingUiComplexityModeIfAny();

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
            ParsekUI.ApplyPendingUiComplexityModeIfAny();

            Assert.Null(ParsekSettingsPersistence.GetStoredUiComplexityMode());
            Assert.Null(ParsekUI.PendingUiComplexityModeForTesting);
            Assert.DoesNotContain(logLines, l => l.Contains("Mode changed: uiComplexityMode="));
        }

        // ------------------------------------------------------------------
        // Frame-latched apply (design 7.2)
        // ------------------------------------------------------------------

        // The core IMGUI-stability contract: the setter persists immediately but must NOT
        // move the value the gates read. If this ever latches at the seam, a toggle click
        // changes the control count mid-frame and the Settings window throws
        // `ArgumentException: Getting control N's position in a group with only M controls`.
        [Fact]
        public void SeamQueuesPendingWithoutLatchingTheAppliedMode()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);

            // Persisted and pending, but the gates still see Advanced this frame.
            Assert.Equal(UiComplexityMode.Basic, settings.UiComplexityModeLevel);
            Assert.Equal(UiComplexityMode.Basic, ParsekUI.PendingUiComplexityModeForTesting);
            Assert.Equal(UiComplexityMode.Advanced, ParsekUI.AppliedUiComplexityMode);
            Assert.True(UiSurfaceVisibility.IsVisible(
                UiSurface.MainButtonKerbals, ParsekUI.AppliedUiComplexityMode));
            Assert.DoesNotContain(logLines, l => l.Contains("Mode changed: uiComplexityMode="));

            ParsekUI.ApplyPendingUiComplexityModeIfAny();

            Assert.Null(ParsekUI.PendingUiComplexityModeForTesting);
            Assert.Equal(UiComplexityMode.Basic, ParsekUI.AppliedUiComplexityMode);
            Assert.False(UiSurfaceVisibility.IsVisible(
                UiSurface.MainButtonKerbals, ParsekUI.AppliedUiComplexityMode));
        }

        // Both controllers can be alive across a scene handover and both call the apply
        // from Update(); the second call must not re-run the transition (and, once phase 7
        // hangs the window-close handler off it, must not re-close anything).
        [Fact]
        public void ApplyIsIdempotentAfterTheFirstLatch()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
            ParsekUI.ApplyPendingUiComplexityModeIfAny();
            logLines.Clear();

            ParsekUI.ApplyPendingUiComplexityModeIfAny();
            ParsekUI.ApplyPendingUiComplexityModeIfAny();

            Assert.Equal(UiComplexityMode.Basic, ParsekUI.AppliedUiComplexityMode);
            Assert.DoesNotContain(logLines, l => l.Contains("Mode changed: uiComplexityMode="));
            Assert.DoesNotContain(logLines, l => l.Contains("UI mode apply hook"));
        }

        [Fact]
        public void ApplyWithNothingPendingIsSilent()
        {
            ParsekUI.ApplyPendingUiComplexityModeIfAny();

            Assert.Equal(UiComplexityMode.Advanced, ParsekUI.AppliedUiComplexityMode);
            Assert.DoesNotContain(logLines, l => l.Contains("Mode changed: uiComplexityMode="));
            Assert.DoesNotContain(logLines, l => l.Contains("Pending UI mode"));
        }

        // Toggling away and back before the latch runs leaves a pending value equal to the
        // applied one. That must collapse to nothing, not log a Basic->Basic transition.
        [Fact]
        public void PendingModeEqualToTheAppliedModeCollapsesToNoTransition()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
            ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);
            ParsekUI.ApplyPendingUiComplexityModeIfAny();

            Assert.Equal(UiComplexityMode.Advanced, ParsekUI.AppliedUiComplexityMode);
            Assert.Null(ParsekUI.PendingUiComplexityModeForTesting);
            Assert.DoesNotContain(logLines, l => l.Contains("Mode changed: uiComplexityMode="));
            Assert.Contains(logLines, l =>
                l.Contains("[UI]") && l.Contains("already applied, nothing to latch"));
        }

        // Design 7.4 (phase 5): entering Basic clamps the Missions window's transient tab
        // off the hidden Recordings tab AT APPLY TIME, not at the window's next draw - the
        // window can stay closed for many frames after the switch, and reopening it must
        // never land on a tab Basic does not draw. Driven end-to-end through the setter
        // seam + the deferred latch, so it fails if the hook stops reaching the live window.
        [Fact]
        public void ApplyingBasicClampsTheMissionsWindowTab()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            var ui = new ParsekUI(UIMode.KSC);
            try
            {
                RecordingsTableUI table = ui.GetRecordingsTableUI();
                Assert.NotNull(table);

                // Land on the Recordings tab the way the Advanced-only GoTo cross-link does.
                table.ScrollToRecording("no-such-recording");
                Assert.Equal(RecordingsTableUI.TabRecordings, table.SelectedTabForTesting);

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);

                // Still on the hidden tab until the deferred latch runs.
                Assert.Equal(RecordingsTableUI.TabRecordings, table.SelectedTabForTesting);

                ParsekUI.ApplyPendingUiComplexityModeIfAny();

                Assert.Equal(UiComplexityMode.Basic, ParsekUI.AppliedUiComplexityMode);
                Assert.Equal(RecordingsTableUI.TabMissions, table.SelectedTabForTesting);
                Assert.Contains(logLines, l =>
                    l.Contains("[UI]")
                    && l.Contains("tab index clamped 1->0")
                    && l.Contains("activeTabs=0"));

                // Going back to Advanced must NOT move the selection: every Basic index is
                // valid there (design 7.4).
                logLines.Clear();
                ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);
                ParsekUI.ApplyPendingUiComplexityModeIfAny();

                Assert.Equal(RecordingsTableUI.TabMissions, table.SelectedTabForTesting);
                Assert.DoesNotContain(logLines, l => l.Contains("tab index clamped"));
            }
            finally
            {
                try { ui.Cleanup(); }
                catch (SecurityException)
                {
                    // Headless xUnit still lacks Unity GUI teardown; the clamp assertions
                    // above are what this test is about (precedent: ParsekUITests).
                }
            }
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
