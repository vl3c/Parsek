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

        [Fact]
        public void TryRunTrackingStationSpawnHandoffs_ShowGhostsDisabled_MatchingSceneEntryPidStillMarksRecordingMaterialized()
        {
            var rec = MakeEligibleTrackingStationRecording(pid: 777);
            RecordingStore.SceneEntryActiveVesselPid = 777;
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(false);
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
        public void CreateGhostVesselsFromCommittedRecordings_ShowGhostsDisabled_SkipsGhostCreation()
        {
            var rec = MakeEligibleTrackingStationRecording();
            RecordingStore.AddCommittedInternal(rec);
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(false);

            int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();

            Assert.Equal(0, created);
            Assert.False(GhostMapPresence.HasGhostVesselForRecording(0));
            Assert.False(rec.VesselSpawned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
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
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(true);
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 777);
            GhostMapPresence.CurrentUTNow = () => 1500;

            int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();

            Assert.Equal(0, created);
            Assert.False(GhostMapPresence.HasGhostVesselForRecording(0));
        }
    }
}
