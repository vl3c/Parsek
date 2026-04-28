using System;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-scene verification that <see cref="RevertInterceptor"/> intercepts
    /// the stock <see cref="FlightDriver.RevertToPrelaunch"/> path (Esc &gt;
    /// Revert to VAB / Revert to SPH) and spawns the 3-option
    /// <see cref="ReFlyRevertDialog"/> when a re-fly session is active. Mirrors
    /// <see cref="ReFlyRevertDialogTest"/> for the Launch-revert path.
    ///
    /// <para>
    /// Runs as a synthetic in-memory fixture: installs a fake
    /// <see cref="ReFlySessionMarker"/> on the live scenario, calls the
    /// Prelaunch-interceptor prefix entry point via
    /// <see cref="RevertInterceptor.Prefix(RevertTarget, EditorFacility)"/>,
    /// and asserts the dialog spawned with the Prelaunch body variant.
    /// The synthetic marker is cleared on teardown so the player's save
    /// stays untouched.
    /// </para>
    /// </summary>
    public class ReFlyRevertDialogPrelaunchTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Re-fly active: RevertInterceptor blocks stock RevertToPrelaunch (VAB/SPH) and shows 3-option dialog")]
        public void ReFlyRevertDialog_Prelaunch_BlocksStockRevert_AndShowsDialog()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var savedMarker = scenario.ActiveReFlySessionMarker;

            string fakeSessionId = "sess_p12v2_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string fakeTreeId = "tree_p12v2_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string fakeRpId = "rp_p12v2_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var marker = new ReFlySessionMarker
            {
                SessionId = fakeSessionId,
                TreeId = fakeTreeId,
                ActiveReFlyRecordingId = "rec_p12v2_igt_provisional",
                OriginChildRecordingId = "rec_p12v2_igt_origin",
                RewindPointId = fakeRpId,
                InvokedUT = 0.0,
                InvokedRealTime = DateTime.UtcNow.ToString("o"),
            };

            scenario.ActiveReFlySessionMarker = marker;

            // Short-circuit the actual PopupDialog spawn so the in-game test
            // runner never leaves a modal up over the flight scene.
            bool hookFired = false;
            string hookSessionId = null;
            string capturedBody = null;
            ReFlyRevertDialog.ShowHookForTesting = s =>
            {
                hookFired = true;
                hookSessionId = s;
            };
            ReFlyRevertDialog.BodyHookForTesting = (_, body) => capturedBody = body;

            try
            {
                ParsekLog.Info("RewindTest",
                    $"ReFlyRevertDialog_Prelaunch_BlocksStockRevert_AndShowsDialog: " +
                    $"synthetic marker sess={fakeSessionId} tree={fakeTreeId} rp={fakeRpId}");

                // Drive the Prelaunch prefix directly with EditorFacility.VAB.
                bool prefixResult = RevertInterceptor.Prefix(RevertTarget.Prelaunch, EditorFacility.VAB);

                InGameAssert.IsFalse(prefixResult,
                    "RevertInterceptor.Prefix(Prelaunch) should return false when a re-fly session is active");
                InGameAssert.IsTrue(hookFired,
                    "ReFlyRevertDialog.Show hook should have fired via the Prelaunch interceptor prefix");
                InGameAssert.AreEqual(fakeSessionId, hookSessionId,
                    "Dialog hook should receive the marker's session id");
                InGameAssert.IsNotNull(capturedBody,
                    "BodyHookForTesting should have captured the composed dialog body");
                InGameAssert.IsTrue(capturedBody.Contains("VAB"),
                    "Prelaunch body copy should mention VAB");
                InGameAssert.IsTrue(capturedBody.Contains("SPH"),
                    "Prelaunch body copy should mention SPH");
                InGameAssert.IsTrue(capturedBody.Contains("FLIGHT"),
                    "Prelaunch body copy should clarify Retry still returns to FLIGHT");
            }
            finally
            {
                ReFlyRevertDialog.ResetForTesting();
                RevertInterceptor.ResetTestOverrides();
                scenario.ActiveReFlySessionMarker = savedMarker;
            }
        }

        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "DiscardReFlyHandler (Prelaunch context) dispatches EDITOR with the clicked facility")]
        public void DiscardReFly_PrelaunchContext_DispatchesEditorWithFacility()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var savedMarker = scenario.ActiveReFlySessionMarker;
            var savedRpsSnapshot = scenario.RewindPoints != null
                ? new System.Collections.Generic.List<RewindPoint>(scenario.RewindPoints)
                : new System.Collections.Generic.List<RewindPoint>();

            string sessId = "sess_pd_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string rpId = "rp_pd_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var originRp = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = "bp_pd_igt",
                UT = 0.0,
                QuicksaveFilename = rpId + ".sfs",
                SessionProvisional = true,
                CreatingSessionId = sessId,
                ChildSlots = new System.Collections.Generic.List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = "rec_origin_pd_igt",
                        Controllable = true,
                    },
                },
            };

            if (scenario.RewindPoints == null)
                scenario.RewindPoints = new System.Collections.Generic.List<RewindPoint>();
            scenario.RewindPoints.Add(originRp);

            var marker = new ReFlySessionMarker
            {
                SessionId = sessId,
                TreeId = "tree_pd_igt",
                ActiveReFlyRecordingId = "rec_prov_pd_igt",
                OriginChildRecordingId = "rec_origin_pd_igt",
                RewindPointId = rpId,
                InvokedUT = 0.0,
                InvokedRealTime = DateTime.UtcNow.ToString("o"),
            };
            scenario.ActiveReFlySessionMarker = marker;

            GameScenes? sceneSeen = null;
            EditorFacility facilitySeen = EditorFacility.None;
            int loadCalls = 0;
            RevertInterceptor.DiscardReFlyLoadGameForTesting = (_, __) => loadCalls++;
            RevertInterceptor.DiscardReFlyLoadSceneForTesting = (scene, facility) =>
            {
                sceneSeen = scene;
                facilitySeen = facility;
            };
            RevertInterceptor.DiscardReFlyQuicksaveExistsForTesting = _ => true;

            try
            {
                ParsekLog.Info("RewindTest",
                    $"DiscardReFly_PrelaunchContext: synthetic sess={sessId} rp={rpId}");

                RevertInterceptor.DiscardReFlyHandler(
                    marker, RevertTarget.Prelaunch, EditorFacility.SPH);

                InGameAssert.AreEqual(1, loadCalls,
                    "DiscardReFlyLoadGameForTesting should fire exactly once");
                InGameAssert.IsTrue(sceneSeen.HasValue,
                    "Scene seam should have received a value");
                InGameAssert.AreEqual(GameScenes.EDITOR, sceneSeen.Value,
                    "Prelaunch context should transition to EDITOR");
                InGameAssert.AreEqual(EditorFacility.SPH, facilitySeen,
                    "Prelaunch context should pass the clicked facility (SPH)");
                InGameAssert.IsFalse(originRp.SessionProvisional,
                    "Origin RP should be promoted to persistent");
            }
            finally
            {
                RevertInterceptor.ResetTestOverrides();
                scenario.ActiveReFlySessionMarker = savedMarker;
                scenario.RewindPoints.Clear();
                scenario.RewindPoints.AddRange(savedRpsSnapshot);
            }
        }

        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "No re-fly active: Prelaunch interceptor allows stock RevertToPrelaunch through")]
        public void ReFlyRevertDialog_Prelaunch_NoSession_AllowsStockRevert()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var savedMarker = scenario.ActiveReFlySessionMarker;

            try
            {
                scenario.ActiveReFlySessionMarker = null;
                Parsek.Rendering.RenderSessionState.Clear("ingame-test-teardown");

                bool hookFired = false;
                ReFlyRevertDialog.ShowHookForTesting = _ => hookFired = true;

                bool prefixResult = RevertInterceptor.Prefix(RevertTarget.Prelaunch, EditorFacility.SPH);

                InGameAssert.IsTrue(prefixResult,
                    "RevertInterceptor.Prefix(Prelaunch) should return true when no re-fly session is active");
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
