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
        }

        public void Dispose()
        {
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            GhostMapPresence.ResetForTesting();
            ParsekSettingsPersistence.ResetForTesting();
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
        public void CreateGhostVesselsFromCommittedRecordings_ShowGhostsDisabled_DoesNotMaterializeRecording()
        {
            var rec = MakeEligibleTrackingStationRecording();
            RecordingStore.AddCommittedInternal(rec);
            ParsekSettingsPersistence.SetStoredShowGhostsInTrackingStationForTesting(false);

            int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();

            Assert.Equal(0, created);
            Assert.False(rec.VesselSpawned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
        }
    }
}
