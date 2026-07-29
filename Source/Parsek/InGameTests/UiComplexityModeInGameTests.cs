using System;
using System.Collections;
using System.Collections.Generic;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Runtime gates for the Basic / Advanced UI complexity mode
    /// (design `docs/dev/design-ui-basic-advanced.md` sections 7.2, 8, 13.3).
    ///
    /// <para>These cover what headless xUnit structurally cannot: the LIVE
    /// <c>InputLockManager</c>. The unit tests pin the close SET and prove the handler
    /// reaches every member of it, but no lock is ever acquired without a real IMGUI draw
    /// under a real mouse, so only an in-game run can show that entering Basic leaves no
    /// Parsek lock held. A leaked lock soft-locks the player's mouse for the rest of the
    /// scene session, which design section 8 edge case 2 names the highest-risk defect in
    /// this feature.</para>
    ///
    /// <para>Design 13.3 rule, load-bearing: every mode flip here goes through the
    /// <see cref="ParsekUI.SetUiComplexityMode"/> SEAM. Writing the settings field directly
    /// would bypass the deferred latch and the close handler entirely, and the tests would
    /// pass while testing nothing.</para>
    ///
    /// <para>NOTE: in-game tests (Ctrl+Shift+T / Settings &gt; Diagnostics); FLIGHT only
    /// (the SPACECENTER UI has no <c>ParsekFlight</c>, and the gated Spawn Control / Gloops
    /// launchers are InFlight-only anyway). Career-independent, non-destructive: they touch
    /// window open flags and the mode setting, both restored in a finally.</para>
    /// </summary>
    public class UiComplexityModeInGameTests
    {
        // Bounded wait for the controller's Update() to latch a queued mode (design 7.2).
        // The apply is DEFERRED on purpose, so the test must wait for it rather than call it,
        // otherwise it would not prove the ParsekFlight.Update wiring exists at all.
        private const int MaxFramesToWaitForApply = 10;

        [InGameTest(Category = "UiComplexityMode", Scene = GameScenes.FLIGHT,
            Description = "Switching to Basic force-closes every gated window and leaves no "
                + "Parsek input lock held (design 7.2, edge case 2)")]
        public IEnumerator BasicModeReleasesInputLocks()
        {
            ParsekUI ui = ParsekUI.ActiveInstance;
            if (ui == null)
            {
                InGameAssert.Skip("No live ParsekUI in this scene");
                yield break;
            }

            if (IsGloopsRecording())
            {
                // Edge case 11: the seam legitimately REFUSES Basic while a manual Gloops
                // recording runs, so this test cannot drive its own precondition. Skipping is
                // correct; SeamRefusesBasicWhileGloopsRecording covers the refusal headless.
                InGameAssert.Skip("Gloops recording in progress - the switch to Basic is refused by design");
                yield break;
            }

            UiComplexityMode originalMode = ParsekUI.AppliedUiComplexityMode;

            try
            {
                // 1. Establish Advanced through the seam, so the gated windows are reachable.
                ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);
                yield return WaitForAppliedMode(UiComplexityMode.Advanced);
                InGameAssert.AreEqual(UiComplexityMode.Advanced, ParsekUI.AppliedUiComplexityMode,
                    "the seam + deferred latch must reach Advanced before the gated windows are opened");

                // 2. Open every gated surface and let a frame pass. If the player's mouse
                // happens to sit over one of them it will acquire its input lock during that
                // frame's draw - that is the interesting case, and it is deliberately not
                // fought here. The assertion is on the END state, not on how many locks were
                // actually taken.
                IReadOnlyList<ParsekUI.GatedWindowCloseTarget> closeSet = ui.BuildGatedWindowCloseSet();
                OpenEveryGatedWindow(ui);
                yield return null;

                int heldBeforeSwitch = 0;
                for (int i = 0; i < closeSet.Count; i++)
                {
                    if (closeSet[i].OwnsInputLock
                        && InputLockManager.GetControlLock(closeSet[i].InputLockId) != ControlTypes.None)
                    {
                        heldBeforeSwitch++;
                    }
                }
                ParsekLog.Info("TestRunner",
                    $"UiComplexityMode: opened {closeSet.Count} gated surfaces, "
                    + $"locksHeldBeforeSwitch={heldBeforeSwitch}");

                // 3. Switch to Basic through the seam and wait for the deferred apply.
                ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
                yield return WaitForAppliedMode(UiComplexityMode.Basic);
                InGameAssert.AreEqual(UiComplexityMode.Basic, ParsekUI.AppliedUiComplexityMode,
                    "the controller's Update() must latch the queued Basic mode");

                // 4. Every gated surface closed, and no Parsek lock left behind. This is the
                // assertion the whole test exists for.
                for (int i = 0; i < closeSet.Count; i++)
                {
                    ParsekUI.GatedWindowCloseTarget target = closeSet[i];
                    InGameAssert.IsFalse(target.IsOpen(),
                        $"gated surface {target.Name} is still open after entering Basic");

                    if (!target.OwnsInputLock)
                        continue;

                    ControlTypes held = InputLockManager.GetControlLock(target.InputLockId);
                    InGameAssert.AreEqual(ControlTypes.None, held,
                        $"input lock {target.InputLockId} ({target.Name}) is STILL HELD after "
                        + "entering Basic - this soft-locks the player's mouse");
                }

                // The self-heal backstop must not be what saved us: assert again after a frame
                // so a pass here cannot be attributed to the ungated DrawIfOpen prologue alone.
                yield return null;
                for (int i = 0; i < closeSet.Count; i++)
                {
                    if (closeSet[i].OwnsInputLock)
                    {
                        InGameAssert.AreEqual(ControlTypes.None,
                            InputLockManager.GetControlLock(closeSet[i].InputLockId),
                            $"input lock {closeSet[i].InputLockId} reacquired after entering Basic");
                    }
                }
            }
            finally
            {
                RestoreMode(originalMode);
            }
        }

        [InGameTest(Category = "UiComplexityMode", Scene = GameScenes.FLIGHT,
            Description = "A Basic round trip preserves a gated window's state - nothing is "
                + "destroyed, only hidden (design philosophy 2, edge case 1)")]
        public IEnumerator ModeRoundTripPreservesWindowState()
        {
            ParsekUI ui = ParsekUI.ActiveInstance;
            if (ui == null)
            {
                InGameAssert.Skip("No live ParsekUI in this scene");
                yield break;
            }

            if (IsGloopsRecording())
            {
                InGameAssert.Skip("Gloops recording in progress - the switch to Basic is refused by design");
                yield break;
            }

            UiComplexityMode originalMode = ParsekUI.AppliedUiComplexityMode;
            CareerStateWindowUI career = ui.GetCareerStateUI();
            int originalTab = career.SelectedTabForTesting;
            bool originalOpen = career.IsOpen;

            try
            {
                ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);
                yield return WaitForAppliedMode(UiComplexityMode.Advanced);

                // A distinguishable bit of state: the LAST tab, which is never the default.
                int markerTab = CareerStateWindowUI.TabCountForTesting - 1;
                career.IsOpen = true;
                career.SelectedTabForTesting = markerTab;
                yield return null;

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
                yield return WaitForAppliedMode(UiComplexityMode.Basic);
                InGameAssert.IsFalse(career.IsOpen,
                    "the Career window must be force-closed on entering Basic");

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);
                yield return WaitForAppliedMode(UiComplexityMode.Advanced);

                // Reopening is the player's act; the mode never reopens a window for them.
                career.IsOpen = true;
                yield return null;

                InGameAssert.AreEqual(markerTab, career.SelectedTabForTesting,
                    "the Career window's tab selection must survive the Basic round trip "
                    + "(the close handler only clears IsOpen and releases the lock)");
                ParsekLog.Info("TestRunner",
                    $"UiComplexityMode: round trip preserved Career tab={career.SelectedTabForTesting}");
            }
            finally
            {
                career.SelectedTabForTesting = originalTab;
                career.IsOpen = originalOpen;
                RestoreMode(originalMode);
            }
        }

        [InGameTest(Category = "UiComplexityMode", Scene = GameScenes.FLIGHT,
            Description = "After a Basic round trip, Advanced is fully restored: every "
                + "UiSurface is visible again (design philosophy 2, 6)")]
        public IEnumerator AdvancedRenderParityAfterRoundTrip()
        {
            if (ParsekUI.ActiveInstance == null)
            {
                InGameAssert.Skip("No live ParsekUI in this scene");
                yield break;
            }

            if (IsGloopsRecording())
            {
                InGameAssert.Skip("Gloops recording in progress - the switch to Basic is refused by design");
                yield break;
            }

            UiComplexityMode originalMode = ParsekUI.AppliedUiComplexityMode;

            try
            {
                ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);
                yield return WaitForAppliedMode(UiComplexityMode.Advanced);

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
                yield return WaitForAppliedMode(UiComplexityMode.Basic);
                InGameAssert.AreEqual(UiComplexityMode.Basic, ParsekUI.AppliedUiComplexityMode,
                    "the round trip must actually pass through Basic");

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);
                yield return WaitForAppliedMode(UiComplexityMode.Advanced);

                InGameAssert.AreEqual(UiComplexityMode.Advanced, ParsekUI.AppliedUiComplexityMode,
                    "the latch must return to Advanced after the round trip");

                // A literal IMGUI control-count diff is not reachable from a test (the counts
                // only exist inside a live OnGUI pass). The honest proxy is the gate itself:
                // every surface the main window can draw must report visible again, walked by
                // reflection so a surface added later is covered without editing this test.
                int surfaces = 0;
                foreach (UiSurface surface in Enum.GetValues(typeof(UiSurface)))
                {
                    InGameAssert.IsTrue(
                        UiSurfaceVisibility.IsVisible(surface, ParsekUI.AppliedUiComplexityMode),
                        $"UiSurface {surface} is hidden in Advanced after a Basic round trip");
                    surfaces++;
                }

                InGameAssert.IsTrue(surfaces > 0, "the UiSurface reflection walk found no surfaces");
                ParsekLog.Info("TestRunner",
                    $"UiComplexityMode: {surfaces} surfaces visible in Advanced after the round trip");
            }
            finally
            {
                RestoreMode(originalMode);
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

        private static void OpenEveryGatedWindow(ParsekUI ui)
        {
            ui.GetCareerStateUI().IsOpen = true;
            ui.GetKerbalsUI().IsOpen = true;
            ui.GetGloopsUI().IsOpen = true;
            ui.GetSpawnControlUI().IsOpen = true;
            ui.GetTestRunnerUI().IsOpen = true;
            ui.GetRecordingsTableUI().GroupPickerForTesting
                .OpenForGroup("ParsekUiModeTest", UnityEngine.Vector2.zero);
        }

        /// <summary>
        /// Waits (bounded) for the controller's <c>Update()</c> to latch a mode queued through
        /// the seam. Deliberately NOT a direct
        /// <c>ParsekUI.ApplyPendingUiComplexityModeIfAny()</c> call: the point is to prove the
        /// real ParsekFlight.Update wiring runs the apply.
        /// </summary>
        private static IEnumerator WaitForAppliedMode(UiComplexityMode expected)
        {
            for (int frame = 0; frame < MaxFramesToWaitForApply; frame++)
            {
                if (ParsekUI.AppliedUiComplexityMode == expected)
                    yield break;
                yield return null;
            }

            ParsekLog.Warn("TestRunner",
                $"UiComplexityMode: mode {expected} not latched within {MaxFramesToWaitForApply} frames "
                + $"(applied={ParsekUI.AppliedUiComplexityMode}, "
                + $"pending={ParsekUI.PendingUiComplexityModeForTesting})");
        }

        /// <summary>
        /// Restores the player's mode in the finally block. A finally cannot yield, so the
        /// deferred apply is driven directly here - acceptable for CLEANUP (the assertions
        /// above all went through the real deferred path), and it guarantees the player is
        /// not left on a mode the test chose even if the controller's next Update never runs.
        /// </summary>
        private static void RestoreMode(UiComplexityMode originalMode)
        {
            ParsekUI.SetUiComplexityMode(originalMode);
            ParsekUI.ApplyPendingUiComplexityModeIfAny();
        }
    }
}
