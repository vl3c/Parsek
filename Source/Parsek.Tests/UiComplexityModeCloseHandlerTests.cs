using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 7 tests for the Advanced -> Basic mode-change close handler and the Gloops
    /// in-progress guard (design `docs/dev/design-ui-basic-advanced.md` sections 7.2, 12.2,
    /// 13.1, 13.2, edge cases 1, 2, 4, 11, 13).
    ///
    /// <para>The close handler is the highest-risk item in this feature: a gated window
    /// force-closed WITHOUT releasing its KSP input lock soft-locks the player's mouse for
    /// the rest of the scene session. Headless xUnit cannot observe `InputLockManager`
    /// (the release is an idempotent no-op when the lock is not held, and no lock is ever
    /// acquired without a draw), so these tests pin the two things that CAN drift here: the
    /// close SET itself, and the fact that the handler actually reaches every member of it.
    /// The live lock assertion is the in-game `BasicModeReleasesInputLocks` test.</para>
    ///
    /// <para>Touches shared static state (`ParsekLog`, `ParsekSettings.CurrentOverrideForTesting`,
    /// `ParsekSettingsPersistence`, `ParsekUI`'s static mode latch), hence
    /// [Collection("Sequential")].</para>
    /// </summary>
    [Collection("Sequential")]
    public class UiComplexityModeCloseHandlerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public UiComplexityModeCloseHandlerTests()
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

        // Every gated, lock-owning window plus the two review additions that map to no
        // UiSurface (design 7.2: the Settings-launched test runner and the group picker).
        private static readonly string[] ExpectedCloseSet =
        {
            "CareerState", "Kerbals", "GloopsRecorder", "SpawnControl", "TestRunner", "GroupPicker"
        };

        // ------------------------------------------------------------------
        // The close set itself (design 13.1 CloseHandlerCoversEveryGatedLockOwner)
        // ------------------------------------------------------------------

        /// <summary>
        /// Design 13.1: pins the section 7.2 close set. Every lock-owning window whose
        /// launcher Basic hides must be in the handler's list, plus the group picker close.
        /// This guards the exact drift failure `ParsekUI.Cleanup()` already exhibits (it
        /// omits gloops, logistics and the test runner), and it fails in BOTH directions -
        /// adding a window to the set without updating the design, or silently dropping one.
        /// </summary>
        [Fact]
        public void CloseHandlerCoversEveryGatedLockOwner()
        {
            var ui = new ParsekUI(UIMode.KSC);
            try
            {
                IReadOnlyList<ParsekUI.GatedWindowCloseTarget> set = ui.BuildGatedWindowCloseSet();

                Assert.Equal(ExpectedCloseSet, set.Select(t => t.Name).ToArray());

                // Every entry must be fully wired: a missing delegate would throw inside the
                // handler's per-window try/catch and be SWALLOWED, leaving the window open.
                foreach (ParsekUI.GatedWindowCloseTarget target in set)
                {
                    Assert.NotNull(target.IsOpen);
                    Assert.NotNull(target.HeldInputLock);
                    Assert.NotNull(target.CloseAndReleaseLock);
                }

                // The five windows own a distinct KSP input lock; the group picker owns none
                // (edge case 4: a reachability rule, not a lock rule).
                string[] lockOwners = set.Where(t => t.OwnsInputLock).Select(t => t.Name).ToArray();
                Assert.Equal(
                    new[] { "CareerState", "Kerbals", "GloopsRecorder", "SpawnControl", "TestRunner" },
                    lockOwners);
                Assert.Null(set.Single(t => t.Name == "GroupPicker").InputLockId);

                string[] lockIds = set.Where(t => t.OwnsInputLock).Select(t => t.InputLockId).ToArray();
                Assert.Equal(lockIds.Length, lockIds.Distinct(StringComparer.Ordinal).Count());
                Assert.All(lockIds, id => Assert.StartsWith("Parsek_", id, StringComparison.Ordinal));

                // Edge case 13: the SETTINGS-launched runner, never the global Ctrl+Shift+T
                // ParsekTestRunnerGlobal window (separate window, separate lock, never gated).
                Assert.Contains("Parsek_TestRunnerWindow", lockIds);
                Assert.DoesNotContain("ParsekTestRunnerGlobal", lockIds);
            }
            finally
            {
                CleanupIgnoringUnityTeardown(ui);
            }
        }

        // ------------------------------------------------------------------
        // The handler actually closes them (design 7.2 step 2, edge case 1)
        // ------------------------------------------------------------------

        /// <summary>
        /// Drives the REAL path end to end: setter seam -> deferred latch -> close handler.
        /// Writing the mode field directly would bypass the handler and test nothing
        /// (design 13.3 states this rule for the in-game tests; it applies here too).
        /// </summary>
        [Fact]
        public void ApplyingBasicClosesEveryGatedWindowAndTheGroupPicker()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            var ui = new ParsekUI(UIMode.KSC);
            try
            {
                OpenEveryGatedSurface(ui);

                IReadOnlyList<ParsekUI.GatedWindowCloseTarget> set = ui.BuildGatedWindowCloseSet();
                Assert.All(set, t => Assert.True(t.IsOpen(), $"{t.Name} should be open before the switch"));

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);

                // Frame-latched (design 7.2): the queue alone must not close anything.
                Assert.All(set, t => Assert.True(t.IsOpen(), $"{t.Name} closed before the deferred latch"));

                ParsekUI.ApplyPendingUiComplexityModeIfAny();

                Assert.Equal(UiComplexityMode.Basic, ParsekUI.AppliedUiComplexityMode);
                Assert.All(set, t => Assert.False(t.IsOpen(), $"{t.Name} still open after entering Basic"));
                Assert.False(ui.GetRecordingsTableUI().IsGroupPickerOpen);
            }
            finally
            {
                CleanupIgnoringUnityTeardown(ui);
            }
        }

        /// <summary>
        /// Design 7.2 step 3 and philosophy 2: Basic -> Advanced closes nothing. Also pins
        /// that the Advanced -> Basic handler leaves the KEPT windows alone - the Missions
        /// table and the Timeline are not in the close set, and closing them would be a
        /// silent regression no other test would catch.
        /// </summary>
        [Fact]
        public void EnteringAdvancedClosesNothingAndBasicSparesTheKeptWindows()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            var ui = new ParsekUI(UIMode.KSC);
            try
            {
                TimelineWindowUI timeline = ui.GetTimelineUI();
                RecordingsTableUI table = ui.GetRecordingsTableUI();
                timeline.IsOpen = true;
                table.IsOpen = true;
                ui.GetCareerStateUI().IsOpen = true;

                // Advanced -> Basic: the gated window goes, the kept ones stay.
                ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
                ParsekUI.ApplyPendingUiComplexityModeIfAny();

                Assert.False(ui.GetCareerStateUI().IsOpen);
                Assert.True(timeline.IsOpen, "Timeline is not gated and must survive the switch to Basic");
                Assert.True(table.IsOpen, "the Missions window survives as the Missions window (design 7.2)");

                // Basic -> Advanced: nothing to close.
                logLines.Clear();
                ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);
                ParsekUI.ApplyPendingUiComplexityModeIfAny();

                Assert.Equal(UiComplexityMode.Advanced, ParsekUI.AppliedUiComplexityMode);
                Assert.True(timeline.IsOpen);
                Assert.True(table.IsOpen);
                Assert.DoesNotContain(logLines, l => l.Contains("Window auto-closed on mode change"));
                Assert.Contains(logLines, l =>
                    l.Contains("[UI]") && l.Contains("revealing surfaces, nothing to close"));
            }
            finally
            {
                CleanupIgnoringUnityTeardown(ui);
            }
        }

        /// <summary>
        /// Edge case 1 / philosophy 2: nothing is destroyed, only hidden. The window's own
        /// transient state must survive the force-close so the next Advanced session finds
        /// it where the player left it. (The in-game `ModeRoundTripPreservesWindowState`
        /// asserts the same contract against the live window.)
        /// </summary>
        [Fact]
        public void ForceCloseDoesNotResetTheWindowState()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            var ui = new ParsekUI(UIMode.KSC);
            try
            {
                CareerStateWindowUI career = ui.GetCareerStateUI();
                career.IsOpen = true;
                career.SelectedTabForTesting = CareerStateWindowUI.TabCountForTesting - 1;
                int expectedTab = career.SelectedTabForTesting;

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
                ParsekUI.ApplyPendingUiComplexityModeIfAny();
                Assert.False(career.IsOpen);

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);
                ParsekUI.ApplyPendingUiComplexityModeIfAny();
                career.IsOpen = true;

                Assert.Equal(expectedTab, career.SelectedTabForTesting);
            }
            finally
            {
                CleanupIgnoringUnityTeardown(ui);
            }
        }

        // ------------------------------------------------------------------
        // Settings window height re-measure (design 7.2, playtest 2026-07-28)
        // ------------------------------------------------------------------

        /// <summary>
        /// The Settings window is the ONE window whose own content changes with the mode: it
        /// hosts the two gated sections (Diagnostics, Sample Density). Its height is fixed
        /// and has no resize handle, so without a re-measure request the window keeps its old
        /// size - dead space under the buttons in Basic, clipped content back in Advanced.
        /// Both directions must request it; only the Basic direction closing windows is not
        /// enough. Consumption is an OnGUI Layout-pass concern xUnit cannot drive, so the
        /// test drives the REAL seam -> latch -> hook path and pins the request.
        /// </summary>
        [Fact]
        public void ModeApplyRequestsSettingsWindowHeightRemeasureInBothDirections()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            var ui = new ParsekUI(UIMode.KSC);
            try
            {
                SettingsWindowUI settingsWindow = ui.GetSettingsWindowUI();
                Assert.False(settingsWindow.HeightRemeasurePendingForTesting);

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);

                // Frame-latched (design 7.2): queuing the mode must not touch the rect.
                Assert.False(settingsWindow.HeightRemeasurePendingForTesting,
                    "the re-measure belongs to the deferred latch, not the setter");

                ParsekUI.ApplyPendingUiComplexityModeIfAny();

                Assert.True(settingsWindow.HeightRemeasurePendingForTesting,
                    "Basic drops two sections, so the window must re-measure to fit");
                Assert.Contains(logLines, l =>
                    l.Contains("[UI]") && l.Contains("Settings window height re-measure requested"));

                // The next draw consumes the request; the reveal direction must raise its own.
                settingsWindow.HeightRemeasurePendingForTesting = false;
                logLines.Clear();

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);
                ParsekUI.ApplyPendingUiComplexityModeIfAny();

                Assert.True(settingsWindow.HeightRemeasurePendingForTesting,
                    "Advanced restores the two sections, so the window must re-measure too");
                Assert.Contains(logLines, l =>
                    l.Contains("[UI]") && l.Contains("Settings window height re-measure requested"));
            }
            finally
            {
                CleanupIgnoringUnityTeardown(ui);
            }
        }

        /// <summary>
        /// A no-op latch (the player toggled back before the frame ran) must not request a
        /// re-measure: the hook never runs, and re-measuring a window whose content did not
        /// change would be a pointless one-frame auto-size.
        /// </summary>
        [Fact]
        public void NoOpLatchDoesNotRequestAHeightRemeasure()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            var ui = new ParsekUI(UIMode.KSC);
            try
            {
                SettingsWindowUI settingsWindow = ui.GetSettingsWindowUI();

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
                ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);
                logLines.Clear();

                ParsekUI.ApplyPendingUiComplexityModeIfAny();

                Assert.Equal(UiComplexityMode.Advanced, ParsekUI.AppliedUiComplexityMode);
                Assert.False(settingsWindow.HeightRemeasurePendingForTesting);
                Assert.DoesNotContain(logLines, l =>
                    l.Contains("Settings window height re-measure requested"));
            }
            finally
            {
                CleanupIgnoringUnityTeardown(ui);
            }
        }

        // ------------------------------------------------------------------
        // Log assertions (design 13.2 AutoCloseLogsPerWindow, 12.2)
        // ------------------------------------------------------------------

        /// <summary>
        /// Design 13.2: one Verbose line per CLOSED window, naming the window, whether it was
        /// open, and whether it held a lock. Deliberately not one line per close-set MEMBER:
        /// five no-op lines on every mode switch would drown the diagnostic that matters.
        /// </summary>
        [Fact]
        public void AutoCloseLogsPerWindow()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            var ui = new ParsekUI(UIMode.KSC);
            try
            {
                OpenEveryGatedSurface(ui);
                logLines.Clear();

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
                ParsekUI.ApplyPendingUiComplexityModeIfAny();

                foreach (string window in ExpectedCloseSet)
                {
                    Assert.Contains(logLines, l =>
                        l.Contains("[UI]")
                        && l.Contains("Window auto-closed on mode change:")
                        && l.Contains($"window={window}")
                        && l.Contains("wasOpen=True")
                        && l.Contains("heldLock="));
                }

                Assert.Equal(
                    ExpectedCloseSet.Length,
                    logLines.Count(l => l.Contains("Window auto-closed on mode change:")));

                Assert.Contains(logLines, l =>
                    l.Contains("[UI]")
                    && l.Contains("UI mode close handler:")
                    && l.Contains($"closed={ExpectedCloseSet.Length}")
                    && l.Contains("alreadyClosed=0")
                    && l.Contains("failed=0"));
            }
            finally
            {
                CleanupIgnoringUnityTeardown(ui);
            }
        }

        /// <summary>
        /// A mode switch with nothing open must not log a single per-window close line - the
        /// common case by far, and the reason the log is per CLOSED window.
        /// </summary>
        [Fact]
        public void AutoCloseIsSilentWhenNothingWasOpen()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            var ui = new ParsekUI(UIMode.KSC);
            try
            {
                ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
                ParsekUI.ApplyPendingUiComplexityModeIfAny();

                Assert.DoesNotContain(logLines, l => l.Contains("Window auto-closed on mode change"));
                Assert.Contains(logLines, l =>
                    l.Contains("[UI]")
                    && l.Contains("UI mode close handler:")
                    && l.Contains("closed=0")
                    && l.Contains($"alreadyClosed={ExpectedCloseSet.Length}"));
            }
            finally
            {
                CleanupIgnoringUnityTeardown(ui);
            }
        }

        /// <summary>
        /// The mode can be changed with no live <see cref="ParsekUI"/> (between scenes). That
        /// must be a logged no-op, not an NRE that aborts the latch and strands the mode.
        /// </summary>
        [Fact]
        public void ApplyWithNoLiveUiIsALoggedNoOp()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);

            // ResetUiComplexityModeForTesting (constructor) already cleared activeInstance.
            ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
            ParsekUI.ApplyPendingUiComplexityModeIfAny();

            Assert.Equal(UiComplexityMode.Basic, ParsekUI.AppliedUiComplexityMode);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]") && l.Contains("no live ParsekUI, close handler and tab clamp skipped"));
        }

        // ------------------------------------------------------------------
        // Gloops in-progress guard (design 7.2, edge case 11)
        // ------------------------------------------------------------------

        // nextIsBasic / gloopsRecording / expected refusal. Advanced only ever reveals
        // surfaces, so it is never refused - not even mid-recording.
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, false)]
        public void ShouldRefuseModeChangeOnlyBlocksBasicWhileGloopsRecords(
            bool nextIsBasic, bool gloopsRecording, bool expected)
        {
            UiComplexityMode next = nextIsBasic ? UiComplexityMode.Basic : UiComplexityMode.Advanced;
            Assert.Equal(expected, ParsekUI.ShouldRefuseModeChange(next, gloopsRecording));
        }

        /// <summary>
        /// The seam-level refusal, driven end to end. `ParsekFlight.IsGloopsRecording` is a
        /// computed property over a live recorder on a MonoBehaviour xUnit cannot construct,
        /// so the probe seam stands in for it; everything downstream of the probe is the real
        /// code path. Nothing may be written OR persisted on refusal - a persisted Basic with
        /// no applied Basic would resurrect on the next scene load.
        /// </summary>
        [Fact]
        public void SeamRefusesBasicWhileGloopsRecordingAndWritesNothing()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Advanced };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);
            ParsekUI.GloopsRecordingProbeForTesting = () => true;

            ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);

            Assert.Equal(UiComplexityMode.Advanced, settings.UiComplexityModeLevel);
            Assert.Null(ParsekSettingsPersistence.GetStoredUiComplexityMode());
            Assert.Null(ParsekUI.PendingUiComplexityModeForTesting);

            ParsekUI.ApplyPendingUiComplexityModeIfAny();
            Assert.Equal(UiComplexityMode.Advanced, ParsekUI.AppliedUiComplexityMode);

            Assert.Contains(logLines, l =>
                l.Contains("[UI]") && l.Contains("Mode switch refused: Gloops recording in progress"));
        }

        /// <summary>The guard must not block the reveal direction (edge case 11).</summary>
        [Fact]
        public void SeamAllowsAdvancedWhileGloopsRecording()
        {
            var settings = new ParsekSettings { uiComplexityMode = (int)UiComplexityMode.Basic };
            ParsekSettings.CurrentOverrideForTesting = settings;
            ParsekSettingsPersistence.SetStoredUiComplexityModeForTesting(null);
            ParsekUI.GloopsRecordingProbeForTesting = () => true;

            ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);
            ParsekUI.ApplyPendingUiComplexityModeIfAny();

            Assert.Equal(UiComplexityMode.Advanced, ParsekUI.AppliedUiComplexityMode);
            Assert.DoesNotContain(logLines, l => l.Contains("Mode switch refused"));
        }

        // The UI half of the guard: the Basic option is disabled, Advanced never is.
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, false)]
        public void ModeOptionIsDisabledOnlyForBasicWhileGloopsRecords(
            bool optionIsBasic, bool gloopsRecording, bool expected)
        {
            UiComplexityMode mode = optionIsBasic ? UiComplexityMode.Basic : UiComplexityMode.Advanced;
            Assert.Equal(expected, SettingsWindowUI.IsModeOptionDisabled(mode, gloopsRecording));
        }

        [Fact]
        public void InterfaceHintCarriesTheGloopsReasonOnlyWhileRecording()
        {
            string idle = SettingsWindowUI.InterfaceSectionHint(false);
            string recording = SettingsWindowUI.InterfaceSectionHint(true);

            Assert.DoesNotContain("Gloops", idle);
            Assert.Contains("Stop the Gloops recording first", recording);
            // One label either way: the reason is appended to the SAME control, never added
            // as a second one, so the IMGUI control count cannot change mid-frame.
            Assert.StartsWith(idle, recording, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static void OpenEveryGatedSurface(ParsekUI ui)
        {
            ui.GetCareerStateUI().IsOpen = true;
            ui.GetKerbalsUI().IsOpen = true;
            ui.GetGloopsUI().IsOpen = true;
            ui.GetSpawnControlUI().IsOpen = true;
            ui.GetTestRunnerUI().IsOpen = true;
            // Opened the way a Recordings-tab row's `G` button opens it (edge case 4).
            ui.GetRecordingsTableUI().GroupPickerForTesting.OpenForGroup("test-group", Vector2.zero);
        }

        // ParsekUI.Cleanup resets cached Unity GUI styles, which headless xUnit cannot do
        // (precedent: UiComplexityModePersistenceTests.ApplyingBasicClampsTheMissionsWindowTab).
        // The assertions above are what these tests are about.
        private static void CleanupIgnoringUnityTeardown(ParsekUI ui)
        {
            try { ui.Cleanup(); }
            catch (SecurityException) { }
        }
    }
}
