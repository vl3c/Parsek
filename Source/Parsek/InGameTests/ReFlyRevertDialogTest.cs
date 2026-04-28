using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 12 of Rewind-to-Staging (design §6.7 + §6.14): live-scene
    /// verification that <see cref="RevertInterceptor"/> intercepts the stock
    /// <see cref="FlightDriver.RevertToLaunch"/> path and spawns the 3-option
    /// <see cref="ReFlyRevertDialog"/> when a re-fly session is active. Also
    /// exercises the new Discard Re-fly handler via test seams: asserts the
    /// session artifacts are cleared, the origin RP is promoted to persistent,
    /// sibling state (supersede relations, other RPs) survives, and the
    /// scene seam received <see cref="GameScenes.SPACECENTER"/>.
    ///
    /// <para>
    /// Runs as a synthetic in-memory fixture: the test installs a fake
    /// <see cref="ReFlySessionMarker"/> + fake RP on the live scenario, calls
    /// the Harmony-prefix entry point via <see cref="RevertInterceptor.Prefix"/>,
    /// and asserts the dialog spawned (via the dialog's visibility flag +
    /// a test-seam hook). The synthetic marker + RP are cleared on teardown
    /// so the player's save stays untouched. Does NOT mutate any on-disk
    /// files (LoadGame + LoadScene are short-circuited by test seams).
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
            Description = "DiscardReFlyHandler (Launch context) preserves sibling state and dispatches SPACECENTER")]
        public void DiscardReFly_LaunchContext_PreservesSiblingState_DispatchesSpaceCenter()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var savedMarker = scenario.ActiveReFlySessionMarker;
            var savedRpsSnapshot = scenario.RewindPoints != null
                ? new List<RewindPoint>(scenario.RewindPoints)
                : new List<RewindPoint>();
            var savedSupersedesSnapshot = scenario.RecordingSupersedes != null
                ? new List<RecordingSupersedeRelation>(scenario.RecordingSupersedes)
                : new List<RecordingSupersedeRelation>();

            string sessId = "sess_discard_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string rpId = "rp_discard_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string siblingRpId = "rp_sibling_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string relId = "rsr_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var originRp = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = "bp_discard_igt",
                UT = 0.0,
                QuicksaveFilename = rpId + ".sfs",
                SessionProvisional = true,
                CreatingSessionId = sessId,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = "rec_origin_igt",
                        Controllable = true,
                    },
                },
            };
            var siblingRp = new RewindPoint
            {
                RewindPointId = siblingRpId,
                BranchPointId = "bp_sibling_igt",
                UT = 1.0,
                QuicksaveFilename = siblingRpId + ".sfs",
                SessionProvisional = false,
            };
            var rel = new RecordingSupersedeRelation
            {
                RelationId = relId,
                OldRecordingId = "rec_old_igt",
                NewRecordingId = "rec_new_igt",
                UT = 0.0,
            };

            if (scenario.RewindPoints == null) scenario.RewindPoints = new List<RewindPoint>();
            if (scenario.RecordingSupersedes == null)
                scenario.RecordingSupersedes = new List<RecordingSupersedeRelation>();

            scenario.RewindPoints.Add(originRp);
            scenario.RewindPoints.Add(siblingRp);
            scenario.RecordingSupersedes.Add(rel);

            var marker = new ReFlySessionMarker
            {
                SessionId = sessId,
                TreeId = "tree_discard_igt",
                ActiveReFlyRecordingId = "rec_provisional_igt",
                OriginChildRecordingId = "rec_origin_igt",
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
                    $"DiscardReFly_LaunchContext: synthetic sess={sessId} rp={rpId}");

                RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

                InGameAssert.IsNull(scenario.ActiveReFlySessionMarker,
                    "Marker should be cleared after Discard Re-fly");
                InGameAssert.IsFalse(originRp.SessionProvisional,
                    "Origin RP should be promoted to persistent");
                InGameAssert.AreEqual(1, loadCalls,
                    "DiscardReFlyLoadGameForTesting should fire exactly once");
                InGameAssert.IsTrue(sceneSeen.HasValue,
                    "Scene seam should have received a value");
                InGameAssert.AreEqual(GameScenes.SPACECENTER, sceneSeen.Value,
                    "Launch context should transition to SPACECENTER");

                bool siblingStillPresent = false;
                for (int i = 0; i < scenario.RewindPoints.Count; i++)
                {
                    if (scenario.RewindPoints[i]?.RewindPointId == siblingRpId)
                    {
                        siblingStillPresent = true;
                        break;
                    }
                }
                InGameAssert.IsTrue(siblingStillPresent,
                    "Sibling RP should survive Discard Re-fly");

                bool relStillPresent = false;
                for (int i = 0; i < scenario.RecordingSupersedes.Count; i++)
                {
                    if (scenario.RecordingSupersedes[i]?.RelationId == relId)
                    {
                        relStillPresent = true;
                        break;
                    }
                }
                InGameAssert.IsTrue(relStillPresent,
                    "Sibling supersede relation should survive Discard Re-fly");
            }
            finally
            {
                RevertInterceptor.ResetTestOverrides();
                scenario.ActiveReFlySessionMarker = savedMarker;
                // Restore the pre-test RP + supersede lists.
                scenario.RewindPoints.Clear();
                scenario.RewindPoints.AddRange(savedRpsSnapshot);
                scenario.RecordingSupersedes.Clear();
                scenario.RecordingSupersedes.AddRange(savedSupersedesSnapshot);
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
                Parsek.Rendering.RenderSessionState.Clear("ingame-test-teardown");

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
