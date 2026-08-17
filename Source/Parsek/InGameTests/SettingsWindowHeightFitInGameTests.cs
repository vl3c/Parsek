using System.Collections;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Runtime gate for the Settings window's height fit across a Basic / Advanced switch
    /// (design `docs/dev/design-ui-basic-advanced.md` section 7.2, step 4).
    ///
    /// <para>This covers what headless xUnit structurally cannot. A GUILayout window's new
    /// height is <c>Mathf.Clamp(passedHeight, contentMin, contentMax)</c>
    /// (`GUILayoutUtility.LayoutSingleGroup`), and that clamp only exists inside a live
    /// IMGUI pass. The original fix dropped the <c>GUILayout.Height</c> option and looked
    /// correct in review, but kept passing the CURRENT height in the rect - and because
    /// this window's <c>contentMax</c> sits above the height an over-tall window already
    /// has, the clamp returned that same height. Growth still worked (<c>contentMin</c>
    /// pushes a too-short window up), so only the shrink direction was broken, which is
    /// exactly the half no headless test could see.</para>
    ///
    /// <para>NOTE: in-game test (Ctrl+Shift+T / Settings &gt; Diagnostics); FLIGHT only, to
    /// match the rest of the mode gates. Career-independent and non-destructive: it opens
    /// the Settings window and flips the mode, both restored in a finally.</para>
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

            SettingsWindowUI settings = ui.GetSettingsWindowUI();
            if (settings == null)
            {
                InGameAssert.Skip("No Settings window on the live ParsekUI");
                yield break;
            }

            if (IsGloopsRecording())
            {
                // Edge case 11: the seam legitimately REFUSES Basic while a manual Gloops
                // recording runs, so this test cannot drive its own precondition.
                InGameAssert.Skip("Gloops recording in progress - the switch to Basic is refused by design");
                yield break;
            }

            UiComplexityMode originalMode = ParsekUI.AppliedUiComplexityMode;
            bool originalOpen = settings.IsOpen;

            try
            {
                settings.IsOpen = true;

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
                    InGameAssert.Skip("The Settings window has not been laid out yet (height=0)");
                    yield break;
                }

                if (settings.HeightRemeasurePendingForTesting)
                {
                    // The fit is held while the bottom tooltip box is showing. That is the
                    // designed behaviour, not a defect - but it means no measurement ran,
                    // so there is nothing to assert against.
                    InGameAssert.Skip(
                        "The baseline height fit never ran (held by the tooltip gate) - "
                        + "move the pointer off the Settings window and re-run");
                    yield break;
                }

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
                yield return WaitForAppliedMode(UiComplexityMode.Basic);
                yield return WaitForHeightFit(settings);

                if (settings.HeightRemeasurePendingForTesting)
                {
                    InGameAssert.Skip(
                        "The Basic height fit never ran (held by the tooltip gate) - "
                        + "move the pointer off the Settings window and re-run");
                    yield break;
                }

                float basicHeight = settings.WindowRectForTesting.height;
                ParsekLog.Info("TestRunner",
                    $"Settings height fit: advanced={advancedHeight:F0} basic={basicHeight:F0} "
                    + $"shrink={(advancedHeight - basicHeight):F0}px");

                InGameAssert.IsLessThan(
                    basicHeight,
                    advancedHeight - MinimumShrinkPixels,
                    $"the Settings window must shrink on entering Basic - Basic drops the "
                    + $"Diagnostics and Sample Density sections, yet the window is "
                    + $"{basicHeight:F0}px against an Advanced {advancedHeight:F0}px "
                    + $"(an unchanged height means the GUILayout clamp kept the taller one)");
            }
            finally
            {
                settings.IsOpen = originalOpen;
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
