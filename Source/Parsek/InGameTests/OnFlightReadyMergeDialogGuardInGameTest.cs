using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-scene verification that the OnFlightReady tree-merge-dialog
    /// fallback respects the active Re-Fly session guard added alongside
    /// <see cref="ParsekFlight.ShouldShowOnFlightReadyMergeDialog"/>. The
    /// pure decision function is covered by xUnit; this fixture exercises
    /// the call-site wiring against a real <see cref="ParsekScenario"/>
    /// + a real <see cref="ParsekFlight"/> instance, so a regression where
    /// <see cref="ParsekFlight.MaybeShowPendingTreeMergeDialogOnFlightReady"/>
    /// stops invoking the helper or the scenario plumbing for
    /// <see cref="ParsekScenario.IsReFlySessionActiveForQuickloadDiscard"/>
    /// is broken would surface here even when the unit suite passes.
    ///
    /// <para>
    /// The active-Re-Fly case fabricates a synthetic
    /// <see cref="ReFlySessionMarker"/> + pending tree, drives the
    /// extracted dispatch method via reflection, and asserts no
    /// <c>ParsekMerge</c> popup spawns. The positive control case clears
    /// the marker, drives the same path, asserts the popup does spawn,
    /// then dismisses it. Both cases restore the scenario marker / pending
    /// tree state on teardown so the player's session stays clean.
    /// </para>
    /// </summary>
    public class OnFlightReadyMergeDialogGuardInGameTest
    {
        private const string DialogName = "ParsekMerge";

        private static readonly FieldInfo PopupDialogToDisplayField =
            typeof(PopupDialog).GetField("dialogToDisplay",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo MultiOptionDialogNameField =
            typeof(MultiOptionDialog).GetField("name",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo MaybeShowDialogMethod =
            typeof(ParsekFlight).GetMethod("MaybeShowPendingTreeMergeDialogOnFlightReady",
                BindingFlags.Instance | BindingFlags.NonPublic);

        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason =
                "Isolated-run only — installs a synthetic Re-Fly marker + pending tree " +
                "and drives the OnFlightReady merge-dialog fallback. Use Run All + Isolated or the row play button.",
            Description = "OnFlightReady merge-dialog fallback skips when an active Re-Fly session owns the pending tree")]
        public IEnumerator OnFlightReady_ActiveReFlySession_SkipsMergeDialog()
        {
            if (PopupDialogToDisplayField == null
                || MultiOptionDialogNameField == null
                || MaybeShowDialogMethod == null)
            {
                InGameAssert.Skip("merge dialog reflection helpers are unavailable");
                yield break;
            }

            if (RecordingStore.HasPendingTree)
            {
                InGameAssert.Skip("requires no existing pending tree");
                yield break;
            }

            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            // Skip when a real Re-Fly session is in flight: swapping a
            // synthetic marker over the live one would leave the
            // RenderSessionState anchor map indexed by the real marker's
            // recording ids — and single-test runs do not get the batch
            // flight baseline restore that would put things back. Cleanly
            // refusing to run is safer than trying to round-trip live
            // session state across a test.
            if (scenario.ActiveReFlySessionMarker != null
                || RewindInvokeContext.Pending)
            {
                InGameAssert.Skip(
                    "requires no active Re-Fly session (would corrupt live RenderSessionState)");
                yield break;
            }

            ParsekFlight flight = UnityEngine.Object.FindObjectOfType<ParsekFlight>();
            if (flight == null)
            {
                InGameAssert.Skip("ParsekFlight instance is unavailable in FLIGHT");
                yield break;
            }

            // savedMarker is guaranteed null by the skip check above — the
            // explicit save keeps the finally block symmetric with the
            // control test and documents the contract.
            ReFlySessionMarker savedMarker = scenario.ActiveReFlySessionMarker;
            bool savedMergePending = ParsekScenario.MergeDialogPending;
            RecordingTree tree = BuildSyntheticPendingTree("ingame-onflightready-refly-guard");

            string fakeSession = "sess_onflightready_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                MergeDialog.DismissAndClearPendingFlag("OnFlightReady Re-Fly guard test setup");
                RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);

                scenario.ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = fakeSession,
                    TreeId = tree.Id,
                    ActiveReFlyRecordingId = tree.ActiveRecordingId,
                    OriginChildRecordingId = tree.ActiveRecordingId,
                    RewindPointId = "rp_onflightready_igt",
                    InvokedUT = Planetarium.GetUniversalTime(),
                    InvokedRealTime = DateTime.UtcNow.ToString("o"),
                };

                InGameAssert.IsTrue(
                    ParsekScenario.IsReFlySessionActiveForQuickloadDiscard(),
                    "IsReFlySessionActiveForQuickloadDiscard should report true with synthetic marker installed");

                MaybeShowDialogMethod.Invoke(flight, null);

                // Give Unity one frame in case any deferred popup was queued
                // — the fallback is synchronous, but the assertion is more
                // robust if we let any latent OnDismiss handlers run.
                yield return null;

                PopupDialog popup = FindPopupDialog(DialogName);
                InGameAssert.IsNull(popup,
                    "Merge dialog should NOT spawn while an active Re-Fly session owns the pending tree");
                InGameAssert.IsFalse(ParsekScenario.MergeDialogPending,
                    "MergeDialogPending should stay false on the Re-Fly skip path");
            }
            finally
            {
                MergeDialog.DismissAndClearPendingFlag("OnFlightReady Re-Fly guard test cleanup");
                if (RecordingStore.HasPendingTree
                    && object.ReferenceEquals(RecordingStore.PendingTree, tree))
                {
                    RecordingStore.DiscardPendingTree();
                }
                // Restore the marker, then re-align RenderSessionState with
                // it. Skip-on-active-session guarantees savedMarker is null
                // here; the explicit Rebuild keeps the cleanup defensive in
                // case the precondition relaxes later or the test was
                // entered with a stray pending invocation.
                scenario.ActiveReFlySessionMarker = savedMarker;
                Parsek.Rendering.RenderSessionState.RebuildFromMarker(savedMarker);
                ParsekScenario.MergeDialogPending = savedMergePending;
            }
        }

        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason =
                "Isolated-run only — fabricates a pending tree and drives the OnFlightReady merge-dialog fallback. " +
                "Use Run All + Isolated or the row play button.",
            Description = "OnFlightReady merge-dialog fallback DOES open the dialog when no Re-Fly session is active (control)")]
        public IEnumerator OnFlightReady_NoReFlySession_ShowsMergeDialog()
        {
            if (PopupDialogToDisplayField == null
                || MultiOptionDialogNameField == null
                || MaybeShowDialogMethod == null)
            {
                InGameAssert.Skip("merge dialog reflection helpers are unavailable");
                yield break;
            }

            if (RecordingStore.HasPendingTree)
            {
                InGameAssert.Skip("requires no existing pending tree");
                yield break;
            }

            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            // Skip when a real Re-Fly session is in flight: clearing the
            // live marker + wiping RenderSessionState mid-test would corrupt
            // anchor state for any rendering code that reads it, and
            // single-test runs do not get the batch flight baseline restore
            // that would put things back.
            if (scenario.ActiveReFlySessionMarker != null
                || RewindInvokeContext.Pending)
            {
                InGameAssert.Skip(
                    "requires no active Re-Fly session (would corrupt live RenderSessionState)");
                yield break;
            }

            ParsekFlight flight = UnityEngine.Object.FindObjectOfType<ParsekFlight>();
            if (flight == null)
            {
                InGameAssert.Skip("ParsekFlight instance is unavailable in FLIGHT");
                yield break;
            }

            // savedMarker is guaranteed null by the skip check above.
            ReFlySessionMarker savedMarker = scenario.ActiveReFlySessionMarker;
            bool savedMergePending = ParsekScenario.MergeDialogPending;
            RecordingTree tree = BuildSyntheticPendingTree("ingame-onflightready-control");

            try
            {
                MergeDialog.DismissAndClearPendingFlag("OnFlightReady control test setup");
                RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
                scenario.ActiveReFlySessionMarker = null;
                // Pair the marker-null with the anchor-map clear so the T4
                // grep scan (AnchorCorrectionLifecycleTests.Marker_NullAssignmentSites_AllPairWithClearCall)
                // accepts this site. The skip-on-active-session check above
                // guarantees there was no live anchor state to lose; this
                // Clear is a no-op in the canonical case and a defensive
                // wipe if a stale anchor map slipped past the precondition.
                Parsek.Rendering.RenderSessionState.Clear("ingame-test-control-setup");

                InGameAssert.IsFalse(
                    ParsekScenario.IsReFlySessionActiveForQuickloadDiscard(),
                    "IsReFlySessionActiveForQuickloadDiscard should report false with no marker / no pending invoke");

                MaybeShowDialogMethod.Invoke(flight, null);

                yield return WaitForPopupDialog(DialogName, 2f);

                PopupDialog popup = FindPopupDialog(DialogName);
                InGameAssert.IsNotNull(popup,
                    "Merge dialog SHOULD spawn when no Re-Fly session owns the pending tree (positive control)");
                InGameAssert.IsTrue(ParsekScenario.MergeDialogPending,
                    "MergeDialogPending should be set to true after the fallback opens the dialog");
            }
            finally
            {
                MergeDialog.DismissAndClearPendingFlag("OnFlightReady control test cleanup");
                if (RecordingStore.HasPendingTree
                    && object.ReferenceEquals(RecordingStore.PendingTree, tree))
                {
                    RecordingStore.DiscardPendingTree();
                }
                // Restore the marker, then re-align RenderSessionState with
                // it. Skip-on-active-session guarantees savedMarker is null
                // here, so RebuildFromMarker(null) re-clears the anchor map
                // we wiped above; the explicit call keeps the cleanup
                // defensive in case the precondition relaxes later.
                scenario.ActiveReFlySessionMarker = savedMarker;
                Parsek.Rendering.RenderSessionState.RebuildFromMarker(savedMarker);
                ParsekScenario.MergeDialogPending = savedMergePending;
            }
        }

        // ------------------------------------------------------------------
        // Helpers (duplicated from RuntimeTests to keep this fixture
        // self-contained; the helpers there are private static and meant
        // for the larger merge-dialog suite. Keeping a local copy avoids
        // exposing implementation details just for a sibling fixture.)
        // ------------------------------------------------------------------

        private static RecordingTree BuildSyntheticPendingTree(string suffix)
        {
            string treeId = "runtime-tree-" + suffix;
            string recordingId = "runtime-rec-" + suffix;
            double startUt = Planetarium.GetUniversalTime();
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            string vesselName = activeVessel?.vesselName ?? "Runtime Test Vessel";
            CelestialBody body = activeVessel?.mainBody;
            string bodyName = body?.bodyName ?? "Kerbin";
            double baseLatitude = activeVessel != null ? activeVessel.latitude : 0.0;
            double baseLongitude = activeVessel != null ? activeVessel.longitude : 0.0;
            double bodyRadius = body != null && body.Radius > 0.0 ? body.Radius : 600000.0;
            double degreesPerMeter = 180.0 / (Math.PI * bodyRadius);
            double firstLatitude = baseLatitude + 40.0 * degreesPerMeter;
            double secondLatitude = baseLatitude + 80.0 * degreesPerMeter;

            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = vesselName,
                TreeId = treeId,
                VesselPersistentId = 910000u,
                TerminalStateValue = TerminalState.Landed,
                MaxDistanceFromLaunch = 100.0,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = startUt,
                        bodyName = bodyName,
                        latitude = firstLatitude,
                        longitude = baseLongitude,
                        altitude = 10.0
                    },
                    new TrajectoryPoint
                    {
                        ut = startUt + 5.0,
                        bodyName = bodyName,
                        latitude = secondLatitude,
                        longitude = baseLongitude,
                        altitude = 12.0
                    }
                }
            };

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "OnFlightReady Guard " + suffix,
                RootRecordingId = recordingId,
                ActiveRecordingId = recordingId
            };
            tree.Recordings[recordingId] = rec;
            return tree;
        }

        private static PopupDialog FindPopupDialog(string dialogName)
        {
            if (string.IsNullOrEmpty(dialogName))
                return null;

            PopupDialog[] popups = UnityEngine.Object.FindObjectsOfType<PopupDialog>();
            for (int i = 0; i < popups.Length; i++)
            {
                MultiOptionDialog dialog = PopupDialogToDisplayField.GetValue(popups[i]) as MultiOptionDialog;
                if (dialog == null)
                    continue;

                string currentName = MultiOptionDialogNameField.GetValue(dialog) as string;
                if (currentName == dialogName)
                    return popups[i];
            }

            return null;
        }

        private static IEnumerator WaitForPopupDialog(string dialogName, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (FindPopupDialog(dialogName) != null)
                    yield break;

                yield return null;
            }

            InGameAssert.Fail(
                $"WaitForPopupDialog timed out after {timeoutSeconds:F0}s (dialog='{dialogName}')");
        }
    }
}
