using System.Collections;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Runtime gate for the Settings window's height fit across a Basic / Advanced switch
    /// (design `docs/dev/design-ui-basic-advanced.md` section 7.2, step 4).
    ///
    /// <para>This covers what headless xUnit structurally cannot: a GUILayout window resolves
    /// to <c>Max(passedHeight, contentMin)</c>, and that only happens inside a live IMGUI
    /// pass. The original fix dropped the <c>GUILayout.Height</c> option and looked correct
    /// in review, but kept passing the CURRENT height in the rect, which is what that
    /// grow-only resolution reads - so the window never shrank. Growth still worked, being
    /// the direction that grows, so only the shrink half was broken: exactly the half no
    /// headless test could see.</para>
    ///
    /// <para>NOTE: in-game test (Ctrl+Shift+T / Settings &gt; Diagnostics); FLIGHT only, to
    /// match the rest of the mode gates - the bug was reported from the Space Centre, but the
    /// window and its fit path are the same object in both scenes
    /// (<c>SettingsWindowUI.DrawIfOpen</c> has no scene branch). Career-independent and
    /// non-destructive: it shows the Parsek UI, opens the Settings window and flips the mode,
    /// all three restored in a finally.</para>
    /// </summary>
    public class SettingsWindowHeightFitInGameTests
    {
        // Bounded wait for the controller's Update() to latch a queued mode (design 7.2).
        private const int MaxFramesToWaitForApply = 10;

        // Bounded wait for a released height to be measured and carried back into the
        // stored rect. GUILayout applies it within the same frame; the slack is for a
        // tooltip-gated pass or two (the fit is held while the bottom tooltip box is up).
        private const int MaxFramesToWaitForFit = 30;

        // Shrink must be unmistakable, not float noise: Basic drops two whole sections.
        private const float MinimumShrinkPixels = 20f;

        [InGameTest(Category = "Settings", Scene = GameScenes.FLIGHT,
            Description = "Switching Advanced -> Basic shrinks the Settings window to its "
                + "Basic content height instead of keeping the taller Advanced height "
                + "(design 7.2 step 4; guards the GUILayout clamp that only ever grew)")]
        public IEnumerator SettingsWindowShrinksWhenEnteringBasic()
        {
            ParsekUI ui = ParsekUI.ActiveInstance;
            if (ui == null)
            {
                InGameAssert.Skip("No live ParsekUI in this scene");
                yield break;
            }

            // Opening the window is not enough to DRAW it: every window draw sits inside
            // ParsekFlight's `if (showUI)`, and nothing clicks the toolbar in an unattended
            // batch. Without this the fit pass never runs and the test skips itself into
            // uselessness in exactly the run that would catch a regression.
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null)
            {
                InGameAssert.Skip("No live ParsekFlight - the Parsek UI cannot be shown");
                yield break;
            }

            SettingsWindowUI settings = ui.GetSettingsWindowUI();

            if (IsGloopsRecording())
            {
                // Edge case 11: the seam legitimately REFUSES Basic while a manual Gloops
                // recording runs, so this test cannot drive its own precondition.
                InGameAssert.Skip("Gloops recording in progress - the switch to Basic is refused by design");
                yield break;
            }

            UiComplexityMode originalMode = ParsekUI.AppliedUiComplexityMode;
            bool originalOpen = settings.IsOpen;
            bool originalShowUi = flight.ShowUIForTesting;

            try
            {
                flight.ShowUIForTesting = true;
                settings.IsOpen = true;
                // One frame so the window is drawn (and, on first open, positioned) before
                // anything below reads or fits its rect.
                yield return null;

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);
                yield return WaitForAppliedMode(UiComplexityMode.Advanced);

                // Baseline through the SAME production path the switch uses, so the number
                // compared below is a real fitted Advanced height and not whatever size the
                // window happened to be carrying from an earlier session.
                settings.RequestHeightRemeasure();
                yield return WaitForHeightFit(settings);
                float advancedHeight = settings.WindowRectForTesting.height;

                if (advancedHeight <= 0f)
                {
                    InGameAssert.Skip("The Settings window was never laid out (height=0)");
                    yield break;
                }

                if (settings.HeightRemeasurePendingForTesting)
                {
                    // The fit is held while the bottom tooltip box is showing. That is the
                    // designed behaviour, not a defect - but it means no measurement ran,
                    // so there is nothing to assert against.
                    InGameAssert.Skip(
                        "The baseline height fit never ran - the pointer is resting on a "
                        + "Settings control whose tooltip holds the fit back; move it off "
                        + "the window and re-run");
                    yield break;
                }

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
                yield return WaitForAppliedMode(UiComplexityMode.Basic);
                yield return WaitForHeightFit(settings);

                if (settings.HeightRemeasurePendingForTesting)
                {
                    InGameAssert.Skip(
                        "The Basic height fit never ran - the pointer is resting on a "
                        + "Settings control whose tooltip holds the fit back; move it off "
                        + "the window and re-run");
                    yield break;
                }

                float basicHeight = settings.WindowRectForTesting.height;
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Info("TestRunner",
                    $"Settings height fit: advanced={advancedHeight.ToString("F0", ic)} "
                    + $"basic={basicHeight.ToString("F0", ic)} "
                    + $"shrink={(advancedHeight - basicHeight).ToString("F0", ic)}px");

                InGameAssert.IsLessThan(
                    basicHeight,
                    advancedHeight - MinimumShrinkPixels,
                    $"the Settings window must shrink on entering Basic - Basic drops the "
                    + $"Diagnostics and Sample Density sections, yet the window is "
                    + $"{basicHeight.ToString("F0", ic)}px against an Advanced "
                    + $"{advancedHeight.ToString("F0", ic)}px (an unchanged height means the "
                    + $"grow-only GUILayout resolution kept the taller one)");
            }
            finally
            {
                settings.IsOpen = originalOpen;
                flight.ShowUIForTesting = originalShowUi;
                RestoreMode(originalMode);
                // Leave the window fitted to whatever mode the player is back on.
                settings.RequestHeightRemeasure();
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static bool IsGloopsRecording()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            return flight != null && flight.IsGloopsRecording;
        }

        private static IEnumerator WaitForAppliedMode(UiComplexityMode expected)
        {
            for (int frame = 0; frame < MaxFramesToWaitForApply; frame++)
            {
                if (ParsekUI.AppliedUiComplexityMode == expected)
                    yield break;
                yield return null;
            }

            ParsekLog.Warn("TestRunner",
                $"Settings height fit: mode {expected} not latched within "
                + $"{MaxFramesToWaitForApply} frames (applied={ParsekUI.AppliedUiComplexityMode})");
        }

        /// <summary>
        /// Waits for a requested fit to be consumed by a Layout pass, then gives the fitted
        /// height a few more frames to be carried back into the stored rect (GUILayout
        /// applies it after the measuring call returns).
        /// </summary>
        private static IEnumerator WaitForHeightFit(SettingsWindowUI settings)
        {
            for (int frame = 0; frame < MaxFramesToWaitForFit; frame++)
            {
                if (!settings.HeightRemeasurePendingForTesting)
                    break;
                yield return null;
            }

            // The measuring pass itself still returns the pre-fit height.
            yield return null;
            yield return null;
        }

        /// <summary>
        /// Restores the player's mode in the finally block. A finally cannot yield, so the
        /// deferred apply is driven directly here - acceptable for CLEANUP.
        /// </summary>
        private static void RestoreMode(UiComplexityMode originalMode)
        {
            ParsekUI.SetUiComplexityMode(originalMode);
            ParsekUI.ApplyPendingUiComplexityModeIfAny();
        }
    }
}
