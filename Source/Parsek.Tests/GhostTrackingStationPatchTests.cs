using System;
using System.Collections.Generic;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostTrackingStationPatchTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostTrackingStationPatchTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            GhostTrackingStationSelection.ClearSelectedGhostForTesting();
            ParsekScenario.SetInstanceForTesting(null);
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            GhostTrackingStationSelection.ClearSelectedGhostForTesting();
            ParsekScenario.SetInstanceForTesting(null);
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void BuildVesselsListFinalizer_KnownGhostMissingRendererNre_Suppresses()
        {
            var exception = new NullReferenceException("orbit renderer click handler");

            Exception result = GhostTrackingBuildVesselsListPatch.FinalizeBuildVesselsListExceptionForTesting(
                exception,
                totalVessels: 4,
                ghostVesselCount: 1,
                ghostMissingOrbitRendererCount: 1,
                nonGhostMissingOrbitRendererCount: 0,
                firstMissingOrbitRendererIsGhost: true,
                exceptionStackTrace: "at KSP.UI.Screens.SpaceTracking.buildVesselsList () [0x000b9] in <filename unknown>:0");

            Assert.Null(result);
            Assert.Contains(logLines, line =>
                line.Contains("[GhostMap]")
                && line.Contains("Suppressed known ghost ProtoVessel")
                && line.Contains("ghostMissingOrbitRenderers=1"));
        }

        [Fact]
        public void BuildVesselsListFinalizer_UnrelatedException_ReturnsOriginalAndWarns()
        {
            var exception = new InvalidOperationException("stock list build failed");

            Exception result = GhostTrackingBuildVesselsListPatch.FinalizeBuildVesselsListExceptionForTesting(
                exception,
                totalVessels: 4,
                ghostVesselCount: 1,
                ghostMissingOrbitRendererCount: 1,
                nonGhostMissingOrbitRendererCount: 0,
                firstMissingOrbitRendererIsGhost: true,
                exceptionStackTrace: "at KSP.UI.Screens.SpaceTracking.buildVesselsList () [0x000b9] in <filename unknown>:0");

            Assert.Same(exception, result);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][GhostMap]")
                && line.Contains("exception left visible")
                && line.Contains("InvalidOperationException")
                && line.Contains("stock list build failed"));
            Assert.DoesNotContain(logLines, line => line.Contains("Suppressed known ghost ProtoVessel"));
        }

        [Fact]
        public void BuildVesselsListFinalizer_NreWithoutGhostMissingRenderer_ReturnsOriginalAndWarns()
        {
            var exception = new NullReferenceException("stock NRE");

            Exception result = GhostTrackingBuildVesselsListPatch.FinalizeBuildVesselsListExceptionForTesting(
                exception,
                totalVessels: 3,
                ghostVesselCount: 1,
                ghostMissingOrbitRendererCount: 0,
                nonGhostMissingOrbitRendererCount: 0,
                firstMissingOrbitRendererIsGhost: false,
                exceptionStackTrace: "at KSP.UI.Screens.SpaceTracking.buildVesselsList () [0x00063] in <filename unknown>:0");

            Assert.Same(exception, result);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][GhostMap]")
                && line.Contains("exception left visible")
                && line.Contains("ghostMissingOrbitRenderers=0"));
        }

        [Fact]
        public void BuildVesselsListFinalizer_NreWithEarlierNonGhostMissingRenderer_ReturnsOriginalAndWarns()
        {
            var exception = new NullReferenceException("ambiguous missing renderer");

            Exception result = GhostTrackingBuildVesselsListPatch.FinalizeBuildVesselsListExceptionForTesting(
                exception,
                totalVessels: 5,
                ghostVesselCount: 1,
                ghostMissingOrbitRendererCount: 1,
                nonGhostMissingOrbitRendererCount: 1,
                firstMissingOrbitRendererIsGhost: false,
                exceptionStackTrace: "at KSP.UI.Screens.SpaceTracking.buildVesselsList () [0x000b9] in <filename unknown>:0");

            Assert.Same(exception, result);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][GhostMap]")
                && line.Contains("exception left visible")
                && line.Contains("nonGhostMissingOrbitRenderers=1"));
        }

        [Fact]
        public void BuildVesselsListFinalizer_NreWithLaterNonGhostMissingRenderer_Suppresses()
        {
            var exception = new NullReferenceException("ghost missing renderer first");

            Exception result = GhostTrackingBuildVesselsListPatch.FinalizeBuildVesselsListExceptionForTesting(
                exception,
                totalVessels: 5,
                ghostVesselCount: 1,
                ghostMissingOrbitRendererCount: 1,
                nonGhostMissingOrbitRendererCount: 1,
                firstMissingOrbitRendererIsGhost: true,
                exceptionStackTrace: "at KSP.UI.Screens.SpaceTracking.buildVesselsList () [0x000b9] in <filename unknown>:0");

            Assert.Null(result);
            Assert.Contains(logLines, line =>
                line.Contains("[GhostMap]")
                && line.Contains("Suppressed known ghost ProtoVessel")
                && line.Contains("ghostMissingOrbitRenderers=1"));
        }

        [Fact]
        public void BuildVesselsListFinalizer_NreAtDifferentStockOffset_ReturnsOriginalAndWarns()
        {
            var exception = new NullReferenceException("unrelated stock NRE");

            Exception result = GhostTrackingBuildVesselsListPatch.FinalizeBuildVesselsListExceptionForTesting(
                exception,
                totalVessels: 4,
                ghostVesselCount: 1,
                ghostMissingOrbitRendererCount: 1,
                nonGhostMissingOrbitRendererCount: 0,
                firstMissingOrbitRendererIsGhost: true,
                exceptionStackTrace: "at KSP.UI.Screens.SpaceTracking.buildVesselsList () [0x00063] in <filename unknown>:0");

            Assert.Same(exception, result);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][GhostMap]")
                && line.Contains("exception left visible")
                && line.Contains("unrelated stock NRE")
                && line.Contains("firstMissingOrbitRenderer=ghost"));
        }

        [Fact]
        public void BuildVesselsListFinalizer_NreWithPriorStockNullCandidate_ReturnsOriginalAndWarns()
        {
            var exception = new NullReferenceException("earlier stock null");

            Exception result = GhostTrackingBuildVesselsListPatch.FinalizeBuildVesselsListExceptionForTesting(
                exception,
                totalVessels: 4,
                ghostVesselCount: 1,
                ghostMissingOrbitRendererCount: 1,
                nonGhostMissingOrbitRendererCount: 0,
                firstMissingOrbitRendererIsGhost: true,
                potentialEarlierStockNullCandidateCount: 1,
                exceptionStackTrace: "at KSP.UI.Screens.SpaceTracking.buildVesselsList () [0x000b9] in <filename unknown>:0");

            Assert.Same(exception, result);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][GhostMap]")
                && line.Contains("exception left visible")
                && line.Contains("priorStockNullCandidates=1"));
        }

        [Fact]
        public void TryClearSelectedVessel_WithPrivateSelection_ClearsAndReturnsPrevious()
        {
            var selected = new object();
            var tracking = new FakeTrackingStation(selected);

            bool cleared = GhostTrackingStationSelection.TryClearSelectedVessel(
                tracking,
                out object previousSelection,
                out string error);

            Assert.True(cleared);
            Assert.Same(selected, previousSelection);
            Assert.Null(tracking.SelectedForTesting);
            Assert.Null(error);
        }

        [Fact]
        public void TryClearSelectedVessel_WithSetVesselMethod_DoesNotCallSetVessel()
        {
            var selected = new object();
            var tracking = new FakeTrackingStationWithSetVessel(selected);

            bool cleared = GhostTrackingStationSelection.TryClearSelectedVessel(
                tracking,
                out object previousSelection,
                out string error);

            Assert.True(cleared);
            Assert.Same(selected, previousSelection);
            Assert.Null(tracking.SelectedForTesting);
            Assert.False(tracking.SetVesselCalled);
            Assert.Null(error);
        }

        [Fact]
        public void TryClearSelectedVessel_WithNoSelection_ReturnsFalseWithoutError()
        {
            var tracking = new FakeTrackingStation(null);

            bool cleared = GhostTrackingStationSelection.TryClearSelectedVessel(
                tracking,
                out object previousSelection,
                out string error);

            Assert.False(cleared);
            Assert.Null(previousSelection);
            Assert.Null(tracking.SelectedForTesting);
            Assert.Null(error);
        }

        [Fact]
        public void TryClearSelectedVessel_WithoutPrivateField_ReportsReflectionError()
        {
            bool cleared = GhostTrackingStationSelection.TryClearSelectedVessel(
                new object(),
                out object previousSelection,
                out string error);

            Assert.False(cleared);
            Assert.Null(previousSelection);
            Assert.Equal("selectedVessel field not found", error);
        }

        [Fact]
        public void TryGetSelectedVesselPid_WithPrivateSelection_ReturnsPid()
        {
            var tracking = new FakeTrackingStation(new FakeSelectedVessel(456u));

            bool ok = GhostTrackingStationSelection.TryGetSelectedVesselPid(
                tracking,
                out uint selectedPid,
                out string error);

            Assert.True(ok);
            Assert.Equal(456u, selectedPid);
            Assert.Null(error);
        }

        [Fact]
        public void TryGetSelectedVesselPid_WithNoSelection_ReturnsZeroWithoutError()
        {
            var tracking = new FakeTrackingStation(null);

            bool ok = GhostTrackingStationSelection.TryGetSelectedVesselPid(
                tracking,
                out uint selectedPid,
                out string error);

            Assert.True(ok);
            Assert.Equal(0u, selectedPid);
            Assert.Null(error);
        }

        [Fact]
        public void TrySelectTrackingStationVessel_WithSetVesselMethod_SelectsSpawnedVessel()
        {
            var spawned = new object();
            var tracking = new FakeTrackingStation(null);

            bool selected = GhostMapPresence.TrySelectTrackingStationVessel(
                tracking,
                spawned,
                out string error);

            Assert.True(selected);
            Assert.Same(spawned, tracking.SelectedForTesting);
            Assert.Equal(1, tracking.SetVesselCalls);
            Assert.Null(error);
        }

        [Fact]
        public void TrySelectTrackingStationVessel_WithTwoArgumentSetVessel_UsesStockSignatureAndDoesNotKeepFocus()
        {
            var spawned = new object();
            var tracking = new FakeTrackingStationWithTwoArgumentSetVessel();

            bool selected = GhostMapPresence.TrySelectTrackingStationVessel(
                tracking,
                spawned,
                out string error);

            Assert.True(selected);
            Assert.Same(spawned, tracking.SelectedForTesting);
            Assert.Equal(1, tracking.TwoArgumentSetVesselCalls);
            Assert.False(tracking.LastKeepFocus);
            Assert.Null(error);
        }

        [Fact]
        public void TrySelectTrackingStationVessel_WithBothSetVesselOverloads_PrefersTwoArgumentStockSignature()
        {
            var spawned = new object();
            var tracking = new FakeTrackingStationWithBothSetVesselOverloads();

            bool selected = GhostMapPresence.TrySelectTrackingStationVessel(
                tracking,
                spawned,
                out string error);

            Assert.True(selected);
            Assert.Same(spawned, tracking.SelectedForTesting);
            Assert.Equal(1, tracking.TwoArgumentSetVesselCalls);
            Assert.Equal(0, tracking.OneArgumentSetVesselCalls);
            Assert.False(tracking.LastKeepFocus);
            Assert.Null(error);
        }

        [Fact]
        public void BuildTrackingStationSetVesselArguments_TwoArgumentMethod_AppendsKeepFocus()
        {
            var method = typeof(FakeTrackingStationWithTwoArgumentSetVessel)
                .GetMethod("SetVessel");
            var spawned = new object();

            object[] args = GhostMapPresence.BuildTrackingStationSetVesselArguments(
                method,
                spawned,
                keepFocus: true);

            Assert.Equal(2, args.Length);
            Assert.Same(spawned, args[0]);
            Assert.Equal(true, args[1]);
        }

        [Fact]
        public void TryInvokeTrackingStationVesselListRefresh_WithBuildMethod_RebuildsListAndLogs()
        {
            var tracking = new FakeTrackingStation(null);

            bool refreshed = GhostMapPresence.TryInvokeTrackingStationVesselListRefresh(
                tracking,
                "test-refresh",
                out string error);

            Assert.True(refreshed);
            Assert.Equal(1, tracking.BuildVesselsListCalls);
            Assert.Null(error);
            Assert.Contains(logLines, line =>
                line.Contains("[INFO][GhostMap]")
                && line.Contains("Tracking Station vessel list refreshed")
                && line.Contains("reason=test-refresh"));
        }

        [Fact]
        public void TryInvokeTrackingStationVesselListRefresh_WithoutBuildMethod_ReturnsError()
        {
            bool refreshed = GhostMapPresence.TryInvokeTrackingStationVesselListRefresh(
                new object(),
                "missing-method",
                out string error);

            Assert.False(refreshed);
            Assert.Equal("buildVesselsList method not found", error);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][GhostMap]")
                && line.Contains("Tracking Station vessel list refresh failed")
                && line.Contains("missing-method")
                && line.Contains("buildVesselsList method not found"));
        }

        [Fact]
        public void TryInvokeTrackingStationVesselListRefresh_WhenBuildThrows_ReturnsError()
        {
            bool refreshed = GhostMapPresence.TryInvokeTrackingStationVesselListRefresh(
                new FakeTrackingStationWithThrowingBuildList(),
                "throwing-build",
                out string error);

            Assert.False(refreshed);
            Assert.Equal("buildVesselsList threw InvalidOperationException: stock rebuild failed", error);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][GhostMap]")
                && line.Contains("Tracking Station vessel list refresh failed")
                && line.Contains("throwing-build")
                && line.Contains("stock rebuild failed"));
        }

        [Theory]
        [InlineData(0, 0, 0, 0, false)]
        [InlineData(0, 0, 1, 0, true)]
        [InlineData(0, 0, 0, 1, true)]
        [InlineData(2, 3, 2, 3, false)]
        [InlineData(2, 3, 5, 3, true)]
        [InlineData(2, 3, 2, 4, true)]
        public void ShouldRefreshTrackingStationVesselListAfterLifecycleMutation_OnlyRefreshesOnCreateOrDestroy(
            int createdBefore,
            int destroyedBefore,
            int createdAfter,
            int destroyedAfter,
            bool expected)
        {
            Assert.Equal(expected,
                GhostMapPresence.ShouldRefreshTrackingStationVesselListAfterLifecycleMutation(
                    createdBefore,
                    destroyedBefore,
                    createdAfter,
                    destroyedAfter));
        }

        [Fact]
        public void TryClearSelectedVessel_WhenAlternatingStockSelections_ClearsEachPreviousStockTarget()
        {
            var asteroid = new object();
            var comet = new object();
            var tracking = new FakeTrackingStation(asteroid);

            bool clearedAsteroid = GhostTrackingStationSelection.TryClearSelectedVessel(
                tracking,
                out object previousAsteroid,
                out string asteroidError);

            tracking.SetSelectedForTesting(comet);

            bool clearedComet = GhostTrackingStationSelection.TryClearSelectedVessel(
                tracking,
                out object previousComet,
                out string cometError);

            Assert.True(clearedAsteroid);
            Assert.Same(asteroid, previousAsteroid);
            Assert.Null(asteroidError);
            Assert.True(clearedComet);
            Assert.Same(comet, previousComet);
            Assert.Null(cometError);
            Assert.Null(tracking.SelectedForTesting);
        }

        [Fact]
        public void ClearSelectedGhost_WhenStockSelectionArrives_RemovesParsekGhostSelection()
        {
            GhostTrackingStationSelection.SetSelectedGhostForTesting(
                new TrackingStationGhostSelectionInfo(
                    553u,
                    "Ghost: Probe",
                    3,
                    "rec-553",
                    100.0,
                    200.0,
                    TerminalState.Orbiting,
                    false,
                    0u,
                    hasRecording: true));

            GhostTrackingStationSelection.ClearSelectedGhost("stock asteroid selected");

            Assert.False(GhostTrackingStationSelection.HasSelectedGhost);
        }

        [Fact]
        public void BuildActionStates_WithEligibleRecording_EnablesOnlyMaterializeAndOmitsNonTsActions()
        {
            var context = new TrackingStationGhostActionContext(
                hasGhostVessel: true,
                canFocus: true,
                canSetTarget: true,
                recordingIndex: 2,
                hasRecording: true,
                materializeEligible: true,
                materializeReason: null,
                alreadyMaterialized: false);

            TrackingStationGhostActionState[] states =
                TrackingStationGhostActionPresentation.BuildActionStates(context);

            TrackingStationGhostActionState materialize = FindState(states, TrackingStationGhostActionKind.Materialize);

            Assert.True(materialize.Enabled);
            Assert.Equal(TrackingStationGhostActionSafety.SafeWhenEligible, materialize.Safety);

            // Native Tracking Station selection already focuses the ghost, and
            // permanently-disabled stock actions are no longer rendered, so
            // BuildActionStates must not return them at all — callers iterate
            // the array, so leftover entries would re-introduce the dead
            // button row.
            Assert.DoesNotContain(states, s => s.Kind == TrackingStationGhostActionKind.Focus);
            Assert.DoesNotContain(states, s => s.Kind == TrackingStationGhostActionKind.Fly);
            Assert.DoesNotContain(states, s => s.Kind == TrackingStationGhostActionKind.Delete);
            Assert.DoesNotContain(states, s => s.Kind == TrackingStationGhostActionKind.Recover);
            Assert.DoesNotContain(states, s => s.Kind == TrackingStationGhostActionKind.SetTarget);
            Assert.DoesNotContain(states, s => s.Kind == TrackingStationGhostActionKind.ShowRecording);
            Assert.Single(states);
        }

        [Fact]
        public void DisableStockActionButtons_NullTracking_ReturnsFalseFlags()
        {
            GhostTrackingStationSelection.DisableStockActionButtons(
                null,
                out bool flyDisabled,
                out bool deleteDisabled,
                out bool recoverDisabled);

            Assert.False(flyDisabled);
            Assert.False(deleteDisabled);
            Assert.False(recoverDisabled);
        }

        [Fact]
        public void BuildActionStates_BeforeRecordingEnd_EnablesMaterializeViaFastForward()
        {
            var context = new TrackingStationGhostActionContext(
                hasGhostVessel: true,
                canFocus: true,
                canSetTarget: true,
                recordingIndex: 1,
                hasRecording: true,
                materializeEligible: false,
                materializeReason: GhostMapPresence.TrackingStationSpawnSkipBeforeEnd,
                alreadyMaterialized: false,
                materializeFastForwardEligible: true);

            TrackingStationGhostActionState[] states =
                TrackingStationGhostActionPresentation.BuildActionStates(context);

            TrackingStationGhostActionState materialize = FindState(states, TrackingStationGhostActionKind.Materialize);

            Assert.True(materialize.Enabled);
            Assert.Contains("Fast-forward", materialize.Reason);
        }

        [Fact]
        public void BuildActionStates_BeforeRecordingEndWithoutEndpointEligibility_DisablesMaterialize()
        {
            var context = new TrackingStationGhostActionContext(
                hasGhostVessel: true,
                canFocus: true,
                canSetTarget: true,
                recordingIndex: 1,
                hasRecording: true,
                materializeEligible: false,
                materializeReason: GhostMapPresence.TrackingStationSpawnSkipBeforeEnd,
                alreadyMaterialized: false);

            TrackingStationGhostActionState[] states =
                TrackingStationGhostActionPresentation.BuildActionStates(context);

            TrackingStationGhostActionState materialize = FindState(states, TrackingStationGhostActionKind.Materialize);

            Assert.False(materialize.Enabled);
            Assert.Contains("endpoint is not eligible", materialize.Reason);
        }

        [Fact]
        public void BuildActionStates_ChainGhostWithoutRecording_DisablesMaterialize()
        {
            var context = new TrackingStationGhostActionContext(
                hasGhostVessel: true,
                canFocus: true,
                canSetTarget: true,
                recordingIndex: -1,
                hasRecording: false,
                materializeEligible: false,
                materializeReason: "no-recording",
                alreadyMaterialized: false);

            TrackingStationGhostActionState[] states =
                TrackingStationGhostActionPresentation.BuildActionStates(context);

            TrackingStationGhostActionState materialize = FindState(states, TrackingStationGhostActionKind.Materialize);

            Assert.DoesNotContain(states, s => s.Kind == TrackingStationGhostActionKind.ShowRecording);
            Assert.False(materialize.Enabled);
            Assert.Contains("No committed recording", materialize.Reason);
        }

        [Fact]
        public void SelectRecordingMarker_StoresSelectionWithoutGhostPid()
        {
            var rec = MakeEligibleTrackingStationRecording(id: "rec-marker", pid: 123);

            GhostTrackingStationSelection.SelectRecordingMarker(4, rec, "test marker");

            Assert.True(GhostTrackingStationSelection.HasSelectedGhost);
            TrackingStationGhostSelectionInfo selection = GhostTrackingStationSelection.SelectedGhost;
            Assert.Equal(0u, selection.GhostPid);
            Assert.Equal(4, selection.RecordingIndex);
            Assert.Equal("rec-marker", selection.RecordingId);
            Assert.True(selection.HasRecording);
            Assert.True(selection.ShowPopup);
            Assert.Contains(logLines, line =>
                line.Contains("[GhostMap]")
                && line.Contains("Selected Tracking Station ghost marker")
                && line.Contains("rec-marker")
                && line.Contains("showPopup=True"));
        }

        [Fact]
        public void ShouldShowPopupForSetVessel_UsesMatchingIconClickIntentWithinWindow()
        {
            GhostTrackingStationSelection.RememberSetVesselPopupIntent(
                ghostPid: 365u,
                source: "vessel icon click",
                currentFrame: 100);

            Assert.True(GhostTrackingStationSelection.ShouldShowPopupForSetVessel(
                ghostPid: 365u,
                currentFrame: 110,
                out string source));
            Assert.Equal("vessel icon click", source);

            Assert.True(GhostTrackingStationSelection.ShouldShowPopupForSetVessel(
                ghostPid: 365u,
                currentFrame: 130,
                out source));
            Assert.Equal("vessel icon click", source);
            Assert.Contains(logLines, line =>
                line.Contains("[GhostMap]")
                && line.Contains("Remembered Tracking Station ghost popup intent")
                && line.Contains("pid=365"));
            Assert.Contains(logLines, line =>
                line.Contains("[GhostMap]")
                && line.Contains("Applied Tracking Station ghost popup intent to SetVessel")
                && line.Contains("pid=365"));
        }

        [Fact]
        public void ShouldShowPopupForSetVessel_ExpiredOrDifferentGhost_ReturnsFalseAndClears()
        {
            GhostTrackingStationSelection.RememberSetVesselPopupIntent(
                ghostPid: 365u,
                source: "vessel icon click",
                currentFrame: 100);

            Assert.False(GhostTrackingStationSelection.ShouldShowPopupForSetVessel(
                ghostPid: 365u,
                currentFrame: 131,
                out string source));
            Assert.Null(source);
            Assert.Contains(logLines, line =>
                line.Contains("[GhostMap]")
                && line.Contains("Cleared Tracking Station ghost popup intent")
                && line.Contains("expired currentFrame=131 untilFrame=130"));

            GhostTrackingStationSelection.RememberSetVesselPopupIntent(
                ghostPid: 365u,
                source: "vessel icon click",
                currentFrame: 200);

            Assert.False(GhostTrackingStationSelection.ShouldShowPopupForSetVessel(
                ghostPid: 999u,
                currentFrame: 201,
                out source));
            Assert.Null(source);
            Assert.False(GhostTrackingStationSelection.ShouldShowPopupForSetVessel(
                ghostPid: 365u,
                currentFrame: 202,
                out source));
            Assert.Null(source);
            Assert.Contains(logLines, line =>
                line.Contains("[GhostMap]")
                && line.Contains("Cleared Tracking Station ghost popup intent")
                && line.Contains("different-ghost requestedPid=999"));
        }

        [Fact]
        public void ClearSelectedGhost_ClearsPendingPopupIntent()
        {
            GhostTrackingStationSelection.SetSelectedGhostForTesting(
                new TrackingStationGhostSelectionInfo(
                    365u,
                    "Ghost: Test",
                    0,
                    "rec-test",
                    0,
                    10,
                    TerminalState.Landed,
                    false,
                    0u,
                    hasRecording: true));
            GhostTrackingStationSelection.RememberSetVesselPopupIntent(
                ghostPid: 365u,
                source: "vessel icon click",
                currentFrame: 10);

            GhostTrackingStationSelection.ClearSelectedGhost("unit test clear");

            Assert.False(GhostTrackingStationSelection.ShouldShowPopupForSetVessel(
                ghostPid: 365u,
                currentFrame: 11,
                out string source));
            Assert.Null(source);
            Assert.Contains(logLines, line =>
                line.Contains("[GhostMap]")
                && line.Contains("Cleared Tracking Station ghost popup intent")
                && line.Contains("ghost-selection-cleared unit test clear"));
        }

        [Fact]
        public void TryFocusGhostMapObject_NullVessel_ReturnsFalseAndLogs()
        {
            bool focused = GhostTrackingStationSelection.TryFocusGhostMapObject(
                vessel: null,
                source: "unit test",
                error: out string error);

            Assert.False(focused);
            Assert.Equal("vessel-null", error);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][GhostMap]")
                && line.Contains("Failed to focus Tracking Station ghost via unit test")
                && line.Contains("vessel-null"));
        }

        [Fact]
        public void BuildActionContext_WithChainSuppressedRecording_DisablesMaterialize()
        {
            var rec = MakeEligibleTrackingStationRecording(id: "rec-claimed", pid: 555);
            RecordingStore.AddCommittedInternal(rec);
            var chains = new Dictionary<uint, GhostChain>
            {
                [555] = new GhostChain
                {
                    OriginalVesselPid = 555,
                    SpawnUT = rec.EndUT + 10,
                    TipRecordingId = "other-tip",
                    IsTerminated = false
                }
            };

            var selection = new TrackingStationGhostSelectionInfo(
                9001u,
                "Ghost: Claimed",
                0,
                rec.RecordingId,
                rec.StartUT,
                rec.EndUT,
                rec.TerminalStateValue,
                false,
                0u,
                hasRecording: true);

            TrackingStationGhostActionContext context =
                GhostTrackingStationSelection.BuildActionContext(
                    selection,
                    hasGhostVessel: true,
                    canFocus: true,
                    canSetTarget: true,
                    currentUT: rec.EndUT + 1,
                    chains: chains);

            Assert.False(context.MaterializeEligible);
            Assert.Equal(
                GhostMapPresence.TrackingStationSpawnSkipIntermediateGhostChainLink,
                context.MaterializeReason);
        }

        [Fact]
        public void BuildActionContext_BeforeRecordingEnd_EnablesMaterializeFastForwardWhenEndpointEligible()
        {
            var rec = MakeEligibleTrackingStationRecording(id: "rec-before-end", pid: 556);
            RecordingStore.AddCommittedInternal(rec);
            var selection = new TrackingStationGhostSelectionInfo(
                9003u,
                "Ghost: Before End",
                0,
                rec.RecordingId,
                rec.StartUT,
                rec.EndUT,
                rec.TerminalStateValue,
                false,
                0u,
                hasRecording: true);

            TrackingStationGhostActionContext context =
                GhostTrackingStationSelection.BuildActionContext(
                    selection,
                    hasGhostVessel: true,
                    canFocus: true,
                    canSetTarget: true,
                    currentUT: rec.EndUT - 10,
                    chains: new Dictionary<uint, GhostChain>());
            TrackingStationGhostActionState materialize = FindState(
                TrackingStationGhostActionPresentation.BuildActionStates(context),
                TrackingStationGhostActionKind.Materialize);

            Assert.False(context.MaterializeEligible);
            Assert.True(context.MaterializeFastForwardEligible);
            Assert.Equal(
                GhostMapPresence.TrackingStationSpawnSkipBeforeEnd,
                context.MaterializeReason);
            Assert.True(materialize.Enabled);
            Assert.Contains("Fast-forward", materialize.Reason);
        }

        [Fact]
        public void BuildActionContext_SupersededRelation_DisablesMaterialize()
        {
            var oldRec = MakeEligibleTrackingStationRecording(id: "rec-action-old", pid: 606);
            var newRec = MakeEligibleTrackingStationRecording(id: "rec-action-new", pid: 707);
            RecordingStore.AddCommittedInternal(oldRec);
            RecordingStore.AddCommittedInternal(newRec);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_action_context",
                        OldRecordingId = oldRec.RecordingId,
                        NewRecordingId = newRec.RecordingId
                    }
                }
            });
            var selection = new TrackingStationGhostSelectionInfo(
                9002u,
                "Ghost: Superseded",
                0,
                oldRec.RecordingId,
                oldRec.StartUT,
                oldRec.EndUT,
                oldRec.TerminalStateValue,
                false,
                0u,
                hasRecording: true);

            TrackingStationGhostActionContext context =
                GhostTrackingStationSelection.BuildActionContext(
                    selection,
                    hasGhostVessel: true,
                    canFocus: true,
                    canSetTarget: true,
                    currentUT: oldRec.EndUT + 1,
                    chains: new Dictionary<uint, GhostChain>());

            Assert.Equal(0, context.RecordingIndex);
            Assert.True(context.HasRecording);
            Assert.False(context.MaterializeEligible);
            Assert.Equal(
                GhostMapPresence.TrackingStationSpawnSkipSupersededByRelation,
                context.MaterializeReason);
        }

        [Fact]
        public void BuildActionContext_BeforeRecordingEndSupersededRelation_DisablesMaterializeFastForward()
        {
            var oldRec = MakeEligibleTrackingStationRecording(id: "rec-before-old", pid: 616);
            var newRec = MakeEligibleTrackingStationRecording(id: "rec-before-new", pid: 717);
            RecordingStore.AddCommittedInternal(oldRec);
            RecordingStore.AddCommittedInternal(newRec);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_before_endpoint",
                        OldRecordingId = oldRec.RecordingId,
                        NewRecordingId = newRec.RecordingId
                    }
                }
            });
            var selection = new TrackingStationGhostSelectionInfo(
                9004u,
                "Ghost: Before Superseded",
                0,
                oldRec.RecordingId,
                oldRec.StartUT,
                oldRec.EndUT,
                oldRec.TerminalStateValue,
                false,
                0u,
                hasRecording: true);

            TrackingStationGhostActionContext context =
                GhostTrackingStationSelection.BuildActionContext(
                    selection,
                    hasGhostVessel: true,
                    canFocus: true,
                    canSetTarget: true,
                    currentUT: oldRec.EndUT - 10,
                    chains: new Dictionary<uint, GhostChain>());
            TrackingStationGhostActionState materialize = FindState(
                TrackingStationGhostActionPresentation.BuildActionStates(context),
                TrackingStationGhostActionKind.Materialize);

            Assert.False(context.MaterializeEligible);
            Assert.False(context.MaterializeFastForwardEligible);
            Assert.Equal(
                GhostMapPresence.TrackingStationSpawnSkipSupersededByRelation,
                context.MaterializeReason);
            Assert.False(materialize.Enabled);
            Assert.Contains(
                GhostMapPresence.TrackingStationSpawnSkipSupersededByRelation,
                materialize.Reason);
        }

        [Fact]
        public void EvaluateMaterializeAtEndpoint_NoVesselSnapshot_ReturnsBlockedReason()
        {
            var rec = MakeEligibleTrackingStationRecording(id: "rec-no-snapshot", pid: 818);
            rec.VesselSnapshot = null;

            var result = GhostTrackingStationSelection.EvaluateMaterializeAtEndpoint(rec);

            Assert.False(result.needsSpawn);
            Assert.Equal("no vessel snapshot", result.reason);
        }

        [Fact]
        public void EvaluateMaterializeAtEndpoint_SupersededRelation_ReturnsSupersededReason()
        {
            var oldRec = MakeEligibleTrackingStationRecording(id: "rec-endpoint-old", pid: 819);
            var newRec = MakeEligibleTrackingStationRecording(id: "rec-endpoint-new", pid: 820);
            RecordingStore.AddCommittedInternal(oldRec);
            RecordingStore.AddCommittedInternal(newRec);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_endpoint",
                        OldRecordingId = oldRec.RecordingId,
                        NewRecordingId = newRec.RecordingId
                    }
                }
            });

            var result = GhostTrackingStationSelection.EvaluateMaterializeAtEndpoint(oldRec);

            Assert.False(result.needsSpawn);
            Assert.Equal(
                GhostMapPresence.TrackingStationSpawnSkipSupersededByRelation,
                result.reason);
        }

        [Fact]
        public void BuildActionContext_WithExistingRealVessel_MarksRecordingAlreadyMaterialized()
        {
            var rec = MakeEligibleTrackingStationRecording(id: "rec-live", pid: 777);
            RecordingStore.AddCommittedInternal(rec);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 777);
            var selection = new TrackingStationGhostSelectionInfo(
                7001u,
                "Ghost: Live",
                0,
                rec.RecordingId,
                rec.StartUT,
                rec.EndUT,
                rec.TerminalStateValue,
                false,
                0u,
                hasRecording: true);

            TrackingStationGhostActionContext context =
                GhostTrackingStationSelection.BuildActionContext(
                    selection,
                    hasGhostVessel: true,
                    canFocus: true,
                    canSetTarget: true,
                    currentUT: rec.EndUT + 1,
                    chains: new Dictionary<uint, GhostChain>());
            TrackingStationGhostActionState materialize = FindState(
                TrackingStationGhostActionPresentation.BuildActionStates(context),
                TrackingStationGhostActionKind.Materialize);

            Assert.True(context.AlreadyMaterialized);
            Assert.False(materialize.Enabled);
            Assert.Contains("already materialized", materialize.Reason);
        }

        [Fact]
        public void BuildActionContext_WhenRawIndexIsStale_ResolvesRecordingById()
        {
            var staleIndex = MakeEligibleTrackingStationRecording(id: "stale-index", pid: 111);
            staleIndex.ExplicitEndUT = 5000;
            var selected = MakeEligibleTrackingStationRecording(id: "selected-id", pid: 222);
            RecordingStore.AddCommittedInternal(staleIndex);
            RecordingStore.AddCommittedInternal(selected);
            var selection = new TrackingStationGhostSelectionInfo(
                7002u,
                "Ghost: Selected",
                0,
                selected.RecordingId,
                selected.StartUT,
                selected.EndUT,
                selected.TerminalStateValue,
                false,
                0u,
                hasRecording: true);

            TrackingStationGhostActionContext context =
                GhostTrackingStationSelection.BuildActionContext(
                    selection,
                    hasGhostVessel: true,
                    canFocus: true,
                    canSetTarget: true,
                    currentUT: selected.EndUT + 1,
                    chains: new Dictionary<uint, GhostChain>());

            Assert.Equal(1, context.RecordingIndex);
            Assert.True(context.HasRecording);
            Assert.True(context.MaterializeEligible);
        }

        private static TrackingStationGhostActionState FindState(
            TrackingStationGhostActionState[] states,
            TrackingStationGhostActionKind kind)
        {
            for (int i = 0; i < states.Length; i++)
                if (states[i].Kind == kind)
                    return states[i];

            throw new InvalidOperationException("Action state not found: " + kind);
        }

        private static Recording MakeEligibleTrackingStationRecording(
            string id = "rec-1",
            uint pid = 12345,
            string vesselName = "TestVessel")
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");
            snapshot.AddValue("type", "Ship");
            snapshot.AddValue("name", vesselName);

            return new Recording
            {
                RecordingId = id,
                VesselName = vesselName,
                VesselPersistentId = pid,
                ExplicitStartUT = 1000,
                ExplicitEndUT = 2000,
                VesselSnapshot = snapshot,
                VesselSpawned = false,
                VesselDestroyed = false,
                PlaybackEnabled = true,
                LoopPlayback = false,
                ChainBranch = 0,
                ChildBranchPointId = null,
                IsDebris = false,
                SpawnedVesselPersistentId = 0,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 250000,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 1000, bodyName = "Mun", latitude = 1.0, longitude = 2.0, altitude = 15000 },
                    new TrajectoryPoint { ut = 2000, bodyName = "Mun", latitude = 1.0, longitude = 2.0, altitude = 15000 }
                }
            };
        }

        private sealed class FakeTrackingStation
        {
            private object selectedVessel;

            public FakeTrackingStation(object selectedVessel)
            {
                this.selectedVessel = selectedVessel;
            }

            public object SelectedForTesting => selectedVessel;

            public int SetVesselCalls { get; private set; }

            public int BuildVesselsListCalls { get; private set; }

            public void SetSelectedForTesting(object selected)
            {
                selectedVessel = selected;
            }

            public void SetVessel(object selected)
            {
                SetVesselCalls++;
                selectedVessel = selected;
            }

            private void buildVesselsList()
            {
                BuildVesselsListCalls++;
            }
        }

        private sealed class FakeTrackingStationWithSetVessel
        {
            private object selectedVessel;

            public FakeTrackingStationWithSetVessel(object selectedVessel)
            {
                this.selectedVessel = selectedVessel;
            }

            public bool SetVesselCalled { get; private set; }

            public object SelectedForTesting => selectedVessel;

            private void SetVessel(object vessel, bool keepFocus)
            {
                SetVesselCalled = true;
                throw new InvalidOperationException("TryClearSelectedVessel should not call SetVessel");
            }
        }

        private sealed class FakeTrackingStationWithTwoArgumentSetVessel
        {
            public object SelectedForTesting { get; private set; }
            public int TwoArgumentSetVesselCalls { get; private set; }
            public bool LastKeepFocus { get; private set; }

            public void SetVessel(object vessel, bool keepFocus)
            {
                TwoArgumentSetVesselCalls++;
                SelectedForTesting = vessel;
                LastKeepFocus = keepFocus;
            }
        }

        private sealed class FakeTrackingStationWithBothSetVesselOverloads
        {
            public object SelectedForTesting { get; private set; }
            public int OneArgumentSetVesselCalls { get; private set; }
            public int TwoArgumentSetVesselCalls { get; private set; }
            public bool LastKeepFocus { get; private set; }

            public void SetVessel(object vessel)
            {
                OneArgumentSetVesselCalls++;
                SelectedForTesting = vessel;
            }

            public void SetVessel(object vessel, bool keepFocus)
            {
                TwoArgumentSetVesselCalls++;
                SelectedForTesting = vessel;
                LastKeepFocus = keepFocus;
            }
        }

        private sealed class FakeTrackingStationWithThrowingBuildList
        {
            private void buildVesselsList()
            {
                throw new InvalidOperationException("stock rebuild failed");
            }
        }

        private sealed class FakeSelectedVessel
        {
            public readonly uint persistentId;

            public FakeSelectedVessel(uint persistentId)
            {
                this.persistentId = persistentId;
            }
        }
    }
}
