using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 12 of Rewind-to-Staging (design §6.7): live-scene verification
    /// that <see cref="RevertInterceptor"/> intercepts the stock
    /// <see cref="FlightDriver.RevertToLaunch"/> path and spawns the 3-option
    /// <see cref="ReFlyRevertDialog"/> when a re-fly session is active.
    ///
    /// <para>
    /// Runs as a synthetic in-memory fixture: the test installs a fake
    /// <see cref="ReFlySessionMarker"/> on the live scenario, calls the
    /// Harmony-prefix entry point via <see cref="RevertInterceptor.Prefix"/>,
    /// and asserts the dialog spawned (via the dialog's visibility flag +
    /// a test-seam hook). The synthetic marker is cleared on teardown so
    /// the player's save stays untouched. Does NOT mutate any on-disk files.
    /// </para>
    /// </summary>
    public class ReFlyRevertDialogTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Re-fly active: RevertInterceptor blocks stock revert and shows 3-option dialog")]
        public void ReFlyRevertDialog_BlocksStockRevert_AndShowsDialog()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var savedMarker = scenario.ActiveReFlySessionMarker;

            string fakeSessionId = "sess_phase12_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string fakeTreeId = "tree_phase12_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string fakeRpId = "rp_phase12_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var marker = new ReFlySessionMarker
            {
                SessionId = fakeSessionId,
                TreeId = fakeTreeId,
                ActiveReFlyRecordingId = "rec_phase12_igt_provisional",
                OriginChildRecordingId = "rec_phase12_igt_origin",
                RewindPointId = fakeRpId,
                InvokedUT = 0.0,
                InvokedRealTime = DateTime.UtcNow.ToString("o"),
            };

            scenario.ActiveReFlySessionMarker = marker;

            // Short-circuit the actual PopupDialog spawn — the in-game test
            // runner can't afford to leave a modal over the flight scene.
            // The hook still asserts that Show was called with the right
            // marker, proving the interceptor wiring reached the dialog.
            bool hookFired = false;
            string hookSessionId = null;
            ReFlyRevertDialog.ShowHookForTesting = s =>
            {
                hookFired = true;
                hookSessionId = s;
            };

            try
            {
                ParsekLog.Info("RewindTest",
                    $"ReFlyRevertDialog_BlocksStockRevert_AndShowsDialog: " +
                    $"synthetic marker sess={fakeSessionId} tree={fakeTreeId} rp={fakeRpId}");

                // Invoke the Harmony-prefix entry point directly. Returns
                // false to block the stock RevertToLaunch.
                bool prefixResult = RevertInterceptor.Prefix();

                InGameAssert.IsFalse(prefixResult,
                    "RevertInterceptor.Prefix should return false when a re-fly session is active");
                InGameAssert.IsTrue(hookFired,
                    "ReFlyRevertDialog.Show hook should have fired via the interceptor prefix");
                InGameAssert.AreEqual(fakeSessionId, hookSessionId,
                    "Dialog hook should receive the marker's session id");
            }
            finally
            {
                ReFlyRevertDialog.ResetForTesting();
                RevertInterceptor.ResetTestOverrides();
                scenario.ActiveReFlySessionMarker = savedMarker;
            }
        }

        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "No re-fly active: RevertInterceptor allows stock revert through")]
        public void ReFlyRevertDialog_NoSession_AllowsStockRevert()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var savedMarker = scenario.ActiveReFlySessionMarker;

            try
            {
                scenario.ActiveReFlySessionMarker = null;

                // Hook should never fire when the prefix lets stock revert through.
                bool hookFired = false;
                ReFlyRevertDialog.ShowHookForTesting = _ => hookFired = true;

                bool prefixResult = RevertInterceptor.Prefix();

                InGameAssert.IsTrue(prefixResult,
                    "RevertInterceptor.Prefix should return true when no re-fly session is active");
                InGameAssert.IsFalse(hookFired,
                    "ReFlyRevertDialog.Show hook should NOT have fired when no marker is present");
            }
            finally
            {
                ReFlyRevertDialog.ResetForTesting();
                RevertInterceptor.ResetTestOverrides();
                scenario.ActiveReFlySessionMarker = savedMarker;
            }
        }
    }
}
