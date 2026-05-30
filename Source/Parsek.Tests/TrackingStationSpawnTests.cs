using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TrackingStationSpawnTests : IDisposable
    {
        public TrackingStationSpawnTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GhostMapPresence.ResetForTesting();
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            ParsekSettingsPersistence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekScenario.SetInstanceForTesting(null);
        }

        public void Dispose()
        {
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            GhostMapPresence.ResetForTesting();
            ParsekSettingsPersistence.ResetForTesting();
            ParsekScenario.SetInstanceForTesting(null);
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
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

        [Fact]
        public void ShouldSpawnAtTrackingStationEnd_OrbitingEligible_ReturnsTrue()
        {
            var rec = MakeEligibleTrackingStationRecording();

            var (needsSpawn, reason) = GhostMapPresence.ShouldSpawnAtTrackingStationEnd(rec, rec.EndUT + 1);

            Assert.True(needsSpawn);
            Assert.Equal("", reason);
        }

        [Fact]
        public void ShouldSpawnAtTrackingStationEnd_BeforeEndUT_ReturnsFalse()
        {
            var rec = MakeEligibleTrackingStationRecording();

            var (needsSpawn, reason) = GhostMapPresence.ShouldSpawnAtTrackingStationEnd(rec, rec.EndUT - 1);

            Assert.False(needsSpawn);
            Assert.Equal(GhostMapPresence.TrackingStationSpawnSkipBeforeEnd, reason);
        }

        [Fact]
        public void ShouldSpawnAtTrackingStationEnd_RewindPending_ReturnsFalse()
        {
            var rec = MakeEligibleTrackingStationRecording();
            RecordingStore.RewindUTAdjustmentPending = true;

            var (needsSpawn, reason) = GhostMapPresence.ShouldSpawnAtTrackingStationEnd(rec, rec.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Equal(GhostMapPresence.TrackingStationSpawnSkipRewindPending, reason);
        }

        [Fact]
        public void ShouldSpawnAtTrackingStationEnd_IntermediateChainSegment_ReturnsFalse()
        {
            var mid = MakeEligibleTrackingStationRecording("rec-mid", 111);
            mid.ChainId = "chain-1";
            mid.ChainIndex = 0;

            var tip = MakeEligibleTrackingStationRecording("rec-tip", 111);
            tip.ChainId = "chain-1";
            tip.ChainIndex = 1;

            RecordingStore.AddCommittedInternal(mid);
            RecordingStore.AddCommittedInternal(tip);

            var (needsSpawn, reason) = GhostMapPresence.ShouldSpawnAtTrackingStationEnd(mid, mid.EndUT + 1);

            Assert.False(needsSpawn);
            Assert.Equal(GhostMapPresence.TrackingStationSpawnSkipIntermediateChainSegment, reason);
        }

        [Fact]
        public void ShouldSpawnAtTrackingStationEnd_PidClaimedByGhostChain_ReturnsFalse()
        {
            var rec = MakeEligibleTrackingStationRecording(id: "rec-claimed", pid: 555);
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

            var (needsSpawn, reason) = GhostMapPresence.ShouldSpawnAtTrackingStationEnd(
                rec,
                rec.EndUT + 1,
                chains);

            Assert.False(needsSpawn);
            Assert.Equal(GhostMapPresence.TrackingStationSpawnSkipIntermediateGhostChainLink, reason);
        }

        [Fact]
        public void ShouldSpawnAtTrackingStationEnd_SupersededByRelation_ReturnsFalse()
        {
            var rec = MakeEligibleTrackingStationRecording(id: "rec-old", pid: 555);
            var relationSupersededIds = new HashSet<string> { rec.RecordingId };

            var (needsSpawn, reason) = GhostMapPresence.ShouldSpawnAtTrackingStationEnd(
                rec,
                rec.EndUT + 1,
                chains: null,
                relationSupersededIds: relationSupersededIds);

            Assert.False(needsSpawn);
            Assert.Equal(GhostMapPresence.TrackingStationSpawnSkipSupersededByRelation, reason);
        }

        [Fact]
        public void ShouldSpawnAtTrackingStationEnd_RewindRetired_ReturnsFalse()
        {
            var rec = MakeEligibleTrackingStationRecording(id: "rec-retired", pid: 555);
            RecordingStore.AddCommittedInternal(rec);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>
                {
                    new RecordingRewindRetirement
                    {
                        RetirementId = "rrt_retired",
                        RecordingId = rec.RecordingId,
                        RestoredRecordingId = "rec-restored",
                        Reason = RecordingRewindRetirement.DefaultReason
                    }
                }
            });

            var (needsSpawn, reason) = GhostMapPresence.ShouldSpawnAtTrackingStationEnd(
                rec,
                rec.EndUT + 1,
                chains: null);

            Assert.False(needsSpawn);
            Assert.Equal(GhostMapPresence.TrackingStationSpawnSkipRewindRetired, reason);
        }

        [Fact]
        public void ShouldSkipTrackingStationDuplicateSpawn_ExistingRealVessel_ReturnsTrue()
        {
            var rec = MakeEligibleTrackingStationRecording(pid: 777);

            bool alreadyMaterialized = GhostMapPresence.ShouldSkipTrackingStationDuplicateSpawn(
                rec,
                realVesselExists: true);

            Assert.True(alreadyMaterialized);
        }

        [Fact]
        public void ShouldSkipTrackingStationDuplicateSpawn_WithoutExistingRealVessel_ReturnsFalse()
        {
            var rec = MakeEligibleTrackingStationRecording(pid: 777);

            bool alreadyMaterialized = GhostMapPresence.ShouldSkipTrackingStationDuplicateSpawn(
                rec,
                realVesselExists: false);

            Assert.False(alreadyMaterialized);
        }

        // ── RealVesselExistsForRecording — F5b guid-aware TS materialization check ──

        [Fact]
        public void RealVesselExistsForRecording_GuidMatch_True()
        {
            var rec = MakeEligibleTrackingStationRecording(pid: 777);
            rec.RecordedVesselGuid = "2b6e6a60d2c947489753371317fa067e";
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 777);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => "2b6e6a60d2c947489753371317fa067e");

            Assert.True(GhostPlaybackLogic.RealVesselExistsForRecording(rec));
        }

        [Fact]
        public void RealVesselExistsForRecording_GuidMismatch_False_Relaunch()
        {
            // A relaunch of the same craft (same pid, different launch guid) must NOT make the
            // prior recording look materialized — otherwise its TS ghost is wrongly suppressed.
            var rec = MakeEligibleTrackingStationRecording(pid: 777);
            rec.RecordedVesselGuid = "2b6e6a60d2c947489753371317fa067e";
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 777);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => "a424011b746440baae6030e225c9de31");

            Assert.False(GhostPlaybackLogic.RealVesselExistsForRecording(rec));
        }

        [Fact]
        public void RealVesselExistsForRecording_GuidUnknown_FallsBackToPid()
        {
            var rec = MakeEligibleTrackingStationRecording(pid: 777);
            rec.RecordedVesselGuid = "2b6e6a60d2c947489753371317fa067e";
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 777);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => null); // live guid unknown

            Assert.True(GhostPlaybackLogic.RealVesselExistsForRecording(rec));
        }

        [Fact]
        public void RealVesselExistsForRecording_NoRealVessel_False()
        {
            var rec = MakeEligibleTrackingStationRecording(pid: 777);
            rec.RecordedVesselGuid = "2b6e6a60d2c947489753371317fa067e";
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(_ => false);

            Assert.False(GhostPlaybackLogic.RealVesselExistsForRecording(rec));
        }

        [Fact]
        public void TryRunTrackingStationSpawnHandoffs_MatchingSceneEntryPidMarksRecordingMaterialized()
        {
            var rec = MakeEligibleTrackingStationRecording(pid: 777);
            RecordingStore.SceneEntryActiveVesselPid = 777;
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 777);

            GhostMapPresence.TryRunTrackingStationSpawnHandoffs(
                new List<Recording> { rec },
                rec.EndUT + 1);

            Assert.True(rec.VesselSpawned);
            Assert.Equal(777u, rec.SpawnedVesselPersistentId);
            Assert.False(GhostMapPresence.HasGhostVesselForRecording(0));
        }

        [Fact]
        public void TryRunTrackingStationSpawnHandoffForIndex_OnlyTouchesSelectedRecording()
        {
            var selected = MakeEligibleTrackingStationRecording(id: "selected", pid: 777);
            var other = MakeEligibleTrackingStationRecording(id: "other", pid: 888);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 777);

            bool handled = GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                new List<Recording> { selected, other },
                0,
                selected.EndUT + 1);

            Assert.True(handled);
            Assert.True(selected.VesselSpawned);
            Assert.Equal(777u, selected.SpawnedVesselPersistentId);
            Assert.False(other.VesselSpawned);
            Assert.Equal(0u, other.SpawnedVesselPersistentId);
        }

        [Fact]
        public void TryRunTrackingStationSpawnHandoffForIndex_SupersededRelation_DoesNotMaterialize()
        {
            var oldRec = MakeEligibleTrackingStationRecording(id: "old", pid: 777);
            var newRec = MakeEligibleTrackingStationRecording(id: "new", pid: 888);
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_tracking",
                        OldRecordingId = oldRec.RecordingId,
                        NewRecordingId = newRec.RecordingId
                    }
                }
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(_ => false);

            bool handled = GhostMapPresence.TryRunTrackingStationSpawnHandoffForIndex(
                new List<Recording> { oldRec, newRec },
                0,
                oldRec.EndUT + 1);

            Assert.False(handled);
            Assert.False(oldRec.VesselSpawned);
            Assert.Equal(0u, oldRec.SpawnedVesselPersistentId);
        }

        [Fact]
        public void TryRunTrackingStationSpawnHandoffForRecordingId_WhenRawIndexIsStale_UsesCurrentIndex()
        {
            var staleIndex = MakeEligibleTrackingStationRecording(id: "stale-index", pid: 111);
            staleIndex.ExplicitEndUT = 5000;
            var selected = MakeEligibleTrackingStationRecording(id: "selected-id", pid: 777);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 777);

            bool handled = GhostMapPresence.TryRunTrackingStationSpawnHandoffForRecordingId(
                new List<Recording> { staleIndex, selected },
                selected.RecordingId,
                selected.EndUT + 1);

            Assert.True(handled);
            Assert.False(staleIndex.VesselSpawned);
            Assert.Equal(0u, staleIndex.SpawnedVesselPersistentId);
            Assert.True(selected.VesselSpawned);
            Assert.Equal(777u, selected.SpawnedVesselPersistentId);
        }

        [Fact]
        public void ShouldTransferTrackingStationNavigationTarget_MatchingGhostPid_ReturnsTrue()
        {
            bool shouldTransfer = GhostMapPresence.ShouldTransferTrackingStationNavigationTarget(
                ghostPid: 777u,
                currentTargetPid: 777u);

            Assert.True(shouldTransfer);
        }

        [Fact]
        public void ShouldTransferTrackingStationNavigationTarget_DifferentOrMissingTarget_ReturnsFalse()
        {
            Assert.False(GhostMapPresence.ShouldTransferTrackingStationNavigationTarget(
                ghostPid: 777u,
                currentTargetPid: 0u));
            Assert.False(GhostMapPresence.ShouldTransferTrackingStationNavigationTarget(
                ghostPid: 777u,
                currentTargetPid: 888u));
        }

        [Fact]
        public void ShouldTransferTrackingStationMapFocus_RequiresLiveGhostFocus()
        {
            Assert.True(GhostMapPresence.ShouldTransferTrackingStationMapFocus(
                mapViewEnabled: true,
                hasGhostMapObject: true,
                mapCameraAlreadyFocusedGhost: true));

            Assert.False(GhostMapPresence.ShouldTransferTrackingStationMapFocus(
                mapViewEnabled: false,
                hasGhostMapObject: true,
                mapCameraAlreadyFocusedGhost: true));
            Assert.False(GhostMapPresence.ShouldTransferTrackingStationMapFocus(
                mapViewEnabled: true,
                hasGhostMapObject: false,
                mapCameraAlreadyFocusedGhost: true));
            Assert.False(GhostMapPresence.ShouldTransferTrackingStationMapFocus(
                mapViewEnabled: true,
                hasGhostMapObject: true,
                mapCameraAlreadyFocusedGhost: false));
        }

        [Fact]
        public void IsTrackingStationMapFocusSceneActive_TrackingStationSceneCountsWithoutFlightMapView()
        {
            Assert.True(GhostMapPresence.IsTrackingStationMapFocusSceneActive(
                mapViewEnabled: false,
                isTrackingStationScene: true));
            Assert.True(GhostMapPresence.IsTrackingStationMapFocusSceneActive(
                mapViewEnabled: true,
                isTrackingStationScene: false));
            Assert.False(GhostMapPresence.IsTrackingStationMapFocusSceneActive(
                mapViewEnabled: false,
                isTrackingStationScene: false));
        }

        [Fact]
        public void TryProbeMapObjectName_ReturnsName()
        {
            bool ok = GhostMapPresence.TryProbeMapObjectName(
                () => "Learstar A1",
                out string name,
                out string error);

            Assert.True(ok);
            Assert.Equal("Learstar A1", name);
            Assert.Null(error);
        }

        [Fact]
        public void TryProbeMapObjectName_WhenGetterThrows_ReturnsFalse()
        {
            bool ok = GhostMapPresence.TryProbeMapObjectName(
                () => throw new InvalidOperationException("map object not ready"),
                out string name,
                out string error);

            Assert.False(ok);
            Assert.Null(name);
            Assert.Equal("GetName threw InvalidOperationException: map object not ready", error);
        }

        [Fact]
        public void TryProbeMapObjectName_NullGetter_ReturnsFalse()
        {
            bool ok = GhostMapPresence.TryProbeMapObjectName(
                null,
                out string name,
                out string error);

            Assert.False(ok);
            Assert.Null(name);
            Assert.Equal("getName-null", error);
        }

        [Fact]
        public void TrySetReadyMapObjectTarget_ReturnsNameAndRunsSetter()
        {
            bool setTargetCalled = false;

            bool ok = GhostMapPresence.TrySetReadyMapObjectTarget(
                () => "Learstar A1",
                () => setTargetCalled = true,
                out string name,
                out string error);

            Assert.True(ok);
            Assert.True(setTargetCalled);
            Assert.Equal("Learstar A1", name);
            Assert.Null(error);
        }

        [Fact]
        public void TrySetReadyMapObjectTarget_WhenGetterThrows_DoesNotRunSetter()
        {
            bool setTargetCalled = false;

            bool ok = GhostMapPresence.TrySetReadyMapObjectTarget(
                () => throw new InvalidOperationException("map object not ready"),
                () => setTargetCalled = true,
                out string name,
                out string error);

            Assert.False(ok);
            Assert.False(setTargetCalled);
            Assert.Null(name);
            Assert.Equal("GetName threw InvalidOperationException: map object not ready", error);
        }

        [Fact]
        public void TrySetReadyMapObjectTarget_WhenSetterThrows_ReturnsFalse()
        {
            bool ok = GhostMapPresence.TrySetReadyMapObjectTarget(
                () => "Learstar A1",
                () => throw new InvalidOperationException("camera target rejected"),
                out string name,
                out string error);

            Assert.False(ok);
            Assert.Equal("Learstar A1", name);
            Assert.Equal("SetTarget threw InvalidOperationException: camera target rejected", error);
        }

        [Fact]
        public void TrySetReadyMapObjectTarget_NullSetter_ReturnsFalse()
        {
            bool ok = GhostMapPresence.TrySetReadyMapObjectTarget(
                () => "Learstar A1",
                null,
                out string name,
                out string error);

            Assert.False(ok);
            Assert.Equal("Learstar A1", name);
            Assert.Equal("setTarget-null", error);
        }

        [Fact]
        public void ShouldAttemptMaterializedFocusRetry_NoPendingPid_ReturnsFalse()
        {
            bool attempt = ParsekTrackingStation.ShouldAttemptMaterializedFocusRetry(
                pendingPid: 0,
                now: 10f,
                nextAttemptTime: 5f,
                deadlineTime: 20f,
                out bool expired);

            Assert.False(attempt);
            Assert.False(expired);
        }

        [Fact]
        public void ShouldAttemptMaterializedFocusRetry_BeforeNextAttempt_ReturnsFalse()
        {
            bool attempt = ParsekTrackingStation.ShouldAttemptMaterializedFocusRetry(
                pendingPid: 123,
                now: 4.9f,
                nextAttemptTime: 5f,
                deadlineTime: 20f,
                out bool expired);

            Assert.False(attempt);
            Assert.False(expired);
        }

        [Fact]
        public void ShouldAttemptMaterializedFocusRetry_WhenDue_ReturnsTrue()
        {
            bool attempt = ParsekTrackingStation.ShouldAttemptMaterializedFocusRetry(
                pendingPid: 123,
                now: 5f,
                nextAttemptTime: 5f,
                deadlineTime: 20f,
                out bool expired);

            Assert.True(attempt);
            Assert.False(expired);
        }

        [Fact]
        public void ShouldAttemptMaterializedFocusRetry_AfterDeadline_Expires()
        {
            bool attempt = ParsekTrackingStation.ShouldAttemptMaterializedFocusRetry(
                pendingPid: 123,
                now: 20.1f,
                nextAttemptTime: 5f,
                deadlineTime: 20f,
                out bool expired);

            Assert.False(attempt);
            Assert.True(expired);
        }

        [Theory]
        [InlineData(123u, false, 0u, null, true, 456u, false, 0u, null, false, true, 456u, false, null)]
        [InlineData(123u, false, 0u, null, true, 456u, false, 0u, null, false, true, 123u, false, null)]
        [InlineData(123u, false, 0u, null, true, 456u, false, 0u, null, false, true, 789u, true, "stock-selection-changed selectedPid=789 baselinePid=456")]
        [InlineData(123u, false, 0u, null, false, 0u, false, 0u, null, false, true, 456u, false, null)]
        [InlineData(123u, false, 0u, null, true, 0u, false, 0u, null, false, true, 456u, true, "stock-selection-changed selectedPid=456 baselinePid=0")]
        [InlineData(123u, true, 900u, "rec-a", true, 456u, true, 900u, "rec-b", true, true, 456u, false, null)]
        [InlineData(123u, true, 900u, "rec-a", true, 456u, true, 901u, "rec-b", true, true, 456u, true, "ghost-selection-changed ghostPid=901 baselineGhostPid=900 recId=rec-b baselineRecId=rec-a")]
        [InlineData(123u, false, 0u, null, true, 456u, true, 901u, "rec-a", true, true, 456u, true, "ghost-selection-changed ghostPid=901 baselineGhostPid=0 recId=rec-a baselineRecId=(none)")]
        [InlineData(123u, true, 0u, "rec-a", true, 456u, true, 0u, "rec-a", false, true, 456u, false, null)]
        [InlineData(123u, true, 0u, "rec-a", true, 456u, true, 0u, "rec-b", false, true, 456u, true, "ghost-selection-changed ghostPid=0 baselineGhostPid=0 recId=rec-b baselineRecId=rec-a")]
        [InlineData(0u, false, 0u, null, true, 456u, true, 901u, "rec-a", true, true, 789u, false, null)]
        public void ShouldAbortMaterializedFocusRetryForUserSelection_OnlyCancelsAfterUserNavigatesAway(
            uint pendingPid,
            bool baselineHasSelectedGhost,
            uint baselineGhostPid,
            string baselineRecordingId,
            bool baselineSelectedPidAvailable,
            uint baselineSelectedPid,
            bool currentHasSelectedGhost,
            uint currentGhostPid,
            string currentRecordingId,
            bool currentGhostPidAvailable,
            bool currentSelectedPidAvailable,
            uint currentSelectedPid,
            bool expectedAbort,
            string expectedReason)
        {
            bool abort = ParsekTrackingStation.ShouldAbortMaterializedFocusRetryForUserSelection(
                pendingPid,
                baselineHasSelectedGhost,
                baselineGhostPid,
                baselineRecordingId,
                baselineSelectedPidAvailable,
                baselineSelectedPid,
                currentHasSelectedGhost,
                currentGhostPid,
                currentRecordingId,
                currentGhostPidAvailable,
                currentSelectedPidAvailable,
                currentSelectedPid,
                out string reason);

            Assert.Equal(expectedAbort, abort);
            Assert.Equal(expectedReason, reason);
        }

        [Fact]
        public void GetCommittedRecordingByRawIndex_ValidAndOutOfRangeIndices()
        {
            var rec = MakeEligibleTrackingStationRecording(id: "lookup");
            RecordingStore.AddCommittedInternal(rec);

            Assert.Same(rec, GhostMapPresence.GetCommittedRecordingByRawIndex(0));
            Assert.Null(GhostMapPresence.GetCommittedRecordingByRawIndex(-1));
            Assert.Null(GhostMapPresence.GetCommittedRecordingByRawIndex(1));
        }

        [Fact]
        public void RecordingGhostIdentityLookup_CapturesStableRecordingId()
        {
            GhostMapPresence.TrackRecordingGhostIdentityForTesting(
                ghostPid: 9001u,
                recordingIndex: 4,
                recordingId: "rec-stable");

            Assert.Equal(4, GhostMapPresence.FindRecordingIndexByVesselPid(9001u));
            Assert.Equal("rec-stable", GhostMapPresence.FindRecordingIdByVesselPid(9001u));

            GhostMapPresence.TrackRecordingGhostIdentityForTesting(
                ghostPid: 9001u,
                recordingIndex: 5,
                recordingId: null);

            Assert.Equal(5, GhostMapPresence.FindRecordingIndexByVesselPid(9001u));
            Assert.Null(GhostMapPresence.FindRecordingIdByVesselPid(9001u));
        }

        [Fact]
        public void ShouldPreserveIdentityForTrackingStationSpawn_ActiveTipWithoutRealVessel_ReturnsTrue()
        {
            var rec = MakeEligibleTrackingStationRecording(id: "tip", pid: 321);
            var chains = new Dictionary<uint, GhostChain>
            {
                [321] = new GhostChain
                {
                    OriginalVesselPid = 321,
                    TipRecordingId = "tip",
                    IsTerminated = false
                }
            };

            bool preserveIdentity = GhostMapPresence.ShouldPreserveIdentityForTrackingStationSpawn(
                chains,
                rec,
                realVesselExists: false);

            Assert.True(preserveIdentity);
        }

        [Fact]
        public void ShouldPreserveIdentityForTrackingStationSpawn_RealVesselExists_ReturnsFalse()
        {
            var rec = MakeEligibleTrackingStationRecording(id: "tip", pid: 321);
            var chains = new Dictionary<uint, GhostChain>
            {
                [321] = new GhostChain
                {
                    OriginalVesselPid = 321,
                    TipRecordingId = "tip",
                    IsTerminated = false
                }
            };

            bool preserveIdentity = GhostMapPresence.ShouldPreserveIdentityForTrackingStationSpawn(
                chains,
                rec,
                realVesselExists: true);

            Assert.False(preserveIdentity);
        }

        [Fact]
        public void ShouldPreserveIdentityForTrackingStationSpawn_TerminatedChain_ReturnsFalse()
        {
            var rec = MakeEligibleTrackingStationRecording(id: "tip", pid: 321);
            var chains = new Dictionary<uint, GhostChain>
            {
                [321] = new GhostChain
                {
                    OriginalVesselPid = 321,
                    TipRecordingId = "tip",
                    IsTerminated = true
                }
            };

            bool preserveIdentity = GhostMapPresence.ShouldPreserveIdentityForTrackingStationSpawn(
                chains,
                rec,
                realVesselExists: false);

            Assert.False(preserveIdentity);
        }

        [Fact]
        public void CreateGhostVesselsFromCommittedRecordings_RealVesselAlreadyMaterialized_SkipsGhostCreation()
        {
            var rec = MakeEligibleTrackingStationRecording(pid: 777);
            rec.OrbitSegments = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 1000, endUT = 2000, bodyName = "Mun", semiMajorAxis = 250000 }
            };
            RecordingStore.AddCommittedInternal(rec);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 777);
            GhostMapPresence.CurrentUTNow = () => 1500;

            int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();

            Assert.Equal(0, created);
            Assert.False(GhostMapPresence.HasGhostVesselForRecording(0));
        }

        [Fact]
        public void TrySelectTrackingStationFocusFrames_OrdinaryParentAnchoredDebris_UsesBodyFixedPrimary()
        {
            Recording rec = MakeTrackingStationDebrisRecording("ts-debris", "ts-parent");
            TrackSection section = rec.TrackSections[0];

            bool selected = ParsekTrackingStation.TrySelectTrackingStationFocusFrames(
                rec,
                105.0,
                out List<TrajectoryPoint> frames,
                out string reason);

            Assert.True(selected, reason);
            Assert.Same(section.bodyFixedFrames, frames);
        }

        [Fact]
        public void TrySelectTrackingStationFocusFrames_LoopAnchoredDebrisChain_UsesBodyFixedPrimary()
        {
            Recording child = MakeTrackingStationDebrisRecording("ts-loop-child", "ts-loop-parent");
            child.TreeId = "ts-loop-tree";
            Recording parent = MakeTrackingStationParentRecording(
                "ts-loop-parent",
                treeId: child.TreeId,
                loopAnchorPid: 77u);
            var tree = new RecordingTree
            {
                Id = child.TreeId,
                TreeName = "Tracking Station loop chain",
                RootRecordingId = parent.RecordingId,
                ActiveRecordingId = child.RecordingId,
            };
            tree.AddOrReplaceRecording(parent);
            tree.AddOrReplaceRecording(child);
            RecordingStore.AddCommittedTreeForTesting(tree);

            try
            {
                TrackSection section = child.TrackSections[0];

                bool selected = ParsekTrackingStation.TrySelectTrackingStationFocusFrames(
                    child,
                    105.0,
                    out List<TrajectoryPoint> frames,
                    out string reason);

                Assert.True(selected, reason);
                Assert.Same(section.bodyFixedFrames, frames);
            }
            finally
            {
                RecordingStore.RemoveCommittedTreeById(tree.Id, "tracking-station-test-cleanup");
            }
        }

        private static Recording MakeTrackingStationDebrisRecording(
            string recordingId,
            string parentRecordingId)
        {
            var relativeFrames = new List<TrajectoryPoint>
            {
                MakeTrackingStationPoint(100.0, 1.0),
                MakeTrackingStationPoint(110.0, 2.0),
            };
            var bodyFixedFrames = new List<TrajectoryPoint>
            {
                MakeTrackingStationPoint(100.0, 101.0),
                MakeTrackingStationPoint(110.0, 102.0),
            };
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 110.0,
                anchorRecordingId = parentRecordingId,
                frames = relativeFrames,
                bodyFixedFrames = bodyFixedFrames,
            };
            return new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                VesselName = recordingId,
                PlaybackEnabled = true,
                IsDebris = true,
                ParentAnchorRecordingId = parentRecordingId,
                Points = relativeFrames,
                TrackSections = new List<TrackSection> { section },
            };
        }

        private static Recording MakeTrackingStationParentRecording(
            string recordingId,
            string treeId,
            uint loopAnchorPid)
        {
            return new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                TreeId = treeId,
                VesselName = recordingId,
                LoopPlayback = true,
                LoopAnchorVesselId = loopAnchorPid,
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 100.0,
                        endUT = 110.0,
                        anchorVesselId = loopAnchorPid,
                        frames = new List<TrajectoryPoint>
                        {
                            MakeTrackingStationPoint(100.0, 201.0),
                            MakeTrackingStationPoint(110.0, 202.0),
                        },
                    },
                },
            };
        }

        private static TrajectoryPoint MakeTrackingStationPoint(double ut, double latitude)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                bodyName = "Kerbin",
                latitude = latitude,
                longitude = 0.0,
                altitude = 0.0,
            };
        }
    }
}
