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
        public void BuildActionStates_WithEligibleRecording_EnablesSafeActionsAndOmitsBlockedStockActions()
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

            TrackingStationGhostActionState focus = FindState(states, TrackingStationGhostActionKind.Focus);
            TrackingStationGhostActionState target = FindState(states, TrackingStationGhostActionKind.SetTarget);
            TrackingStationGhostActionState recording = FindState(states, TrackingStationGhostActionKind.ShowRecording);
            TrackingStationGhostActionState materialize = FindState(states, TrackingStationGhostActionKind.Materialize);

            Assert.True(focus.Enabled);
            Assert.Equal(TrackingStationGhostActionSafety.SafeOnGhost, focus.Safety);
            Assert.True(target.Enabled);
            Assert.Equal(TrackingStationGhostActionSafety.SafeOnGhost, target.Safety);
            Assert.True(recording.Enabled);
            Assert.Equal(TrackingStationGhostActionSafety.SafeOnGhost, recording.Safety);
            Assert.True(materialize.Enabled);
            Assert.Equal(TrackingStationGhostActionSafety.SafeWhenEligible, materialize.Safety);

            // Permanently-disabled stock actions are no longer rendered, so
            // BuildActionStates must not return them at all — callers iterate
            // the array, so leftover entries would re-introduce the dead
            // button row.
            Assert.DoesNotContain(states, s => s.Kind == TrackingStationGhostActionKind.Fly);
            Assert.DoesNotContain(states, s => s.Kind == TrackingStationGhostActionKind.Delete);
            Assert.DoesNotContain(states, s => s.Kind == TrackingStationGhostActionKind.Recover);
            Assert.Equal(4, states.Length);
        }

        [Fact]
        public void BuildActionStates_BeforeRecordingEnd_DisablesMaterializeAndExplainsReason()
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
            Assert.Contains("endpoint", materialize.Reason);
        }

        [Fact]
        public void BuildActionStates_ChainGhostWithoutRecording_DisablesRecordingAndMaterialize()
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

            TrackingStationGhostActionState recording = FindState(states, TrackingStationGhostActionKind.ShowRecording);
            TrackingStationGhostActionState materialize = FindState(states, TrackingStationGhostActionKind.Materialize);

            Assert.False(recording.Enabled);
            Assert.Contains("no direct committed recording", recording.Reason);
            Assert.False(materialize.Enabled);
            Assert.Contains("No committed recording", materialize.Reason);
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

            public void SetSelectedForTesting(object selected)
            {
                selectedVessel = selected;
            }

            public void SetVessel(object selected)
            {
                SetVesselCalls++;
                selectedVessel = selected;
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
    }
}
