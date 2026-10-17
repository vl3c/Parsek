using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A Re-Fly keeps a chain tip's spawn stamp when the stamped vessel survives the strip as the
    /// same launch (RC-1 cycle 2, rewind to the Interceptor separation on bdock-recorded). The
    /// chain then ghosts that vessel, and the tip must be free to spawn again at its UT.
    /// </summary>
    [Collection("Sequential")]
    public class ChainTipSpawnClaimReleaseTests : IDisposable
    {
        private const uint StationPid = 3620499050;
        private const string StationGuid = "97813bb6fd9b462288d98258fcff0bbd";
        private readonly List<string> logLines = new List<string>();

        public ChainTipSpawnClaimReleaseTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        private static Recording StationTip(uint spawnedPid)
        {
            return new Recording
            {
                RecordingId = "37d0dc074351408ba0374230793abb1c",
                VesselName = "Kerbal X",
                VesselPersistentId = StationPid,
                SpawnedVesselPersistentId = spawnedPid,
                RecordedVesselGuid = StationGuid,
                VesselSpawned = spawnedPid != 0,
                SpawnAttempts = 2,
                TerminalStateValue = TerminalState.Orbiting,
            };
        }

        [Fact]
        public void ReFlyKeptTipStamp_ReleasedWhenChainGhostsThatVessel_TipSpawnGateOpens()
        {
            Recording tip = StationTip(StationPid);

            // The Re-Fly reconcile keeps the stamp: the Station survived the strip as the same launch.
            int reset = RewindInvoker.ReconcilePostStripSpawnState(
                new List<(uint pid, string guid)> { (StationPid, StationGuid) },
                new List<uint>(),
                new List<Recording> { tip });
            Assert.Equal(0, reset);
            Assert.Equal("already spawned (VesselSpawned=true)",
                GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(tip, false).reason);

            bool released = VesselGhoster.ReleaseTipSpawnClaimOnGhostedVessel(
                tip, StationPid, "chain=3620499050");

            Assert.True(released);
            Assert.False(tip.VesselSpawned);
            Assert.Equal(0u, tip.SpawnedVesselPersistentId);
            Assert.Equal(0, tip.SpawnAttempts);
            Assert.NotEqual("already spawned (VesselSpawned=true)",
                GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(tip, false).reason);
            Assert.Contains(logLines, l => l.Contains("[Ghoster]")
                && l.Contains("Chain tip spawn claim released")
                && l.Contains("rec=37d0dc074351408ba0374230793abb1c")
                && l.Contains("pid=3620499050")
                && l.Contains("chain=3620499050"));
        }

        [Fact]
        public void GhostedPidIsNotTheStamp_LeavesClaimAlone()
        {
            Recording tip = StationTip(2000000001);

            bool released = VesselGhoster.ReleaseTipSpawnClaimOnGhostedVessel(
                tip, StationPid, "chain=3620499050");

            Assert.False(released);
            Assert.True(tip.VesselSpawned);
            Assert.Equal(2000000001u, tip.SpawnedVesselPersistentId);
            Assert.Equal(2, tip.SpawnAttempts);
            Assert.DoesNotContain(logLines, l => l.Contains("Chain tip spawn claim released"));
            Assert.Contains(logLines, l => l.Contains("Chain tip spawn claim kept")
                && l.Contains("spawnedPid=2000000001") && l.Contains("ghostedPid=3620499050"));
        }

        [Fact]
        public void UnstampedTipOrNoTip_NoOp()
        {
            Recording tip = StationTip(0);

            Assert.False(VesselGhoster.ReleaseTipSpawnClaimOnGhostedVessel(tip, StationPid, "chain=1"));
            Assert.False(VesselGhoster.ReleaseTipSpawnClaimOnGhostedVessel(null, StationPid, "chain=1"));
            Assert.False(VesselGhoster.ReleaseTipSpawnClaimOnGhostedVessel(StationTip(StationPid), 0, "chain=1"));
            Assert.False(tip.VesselSpawned);
            Assert.DoesNotContain(logLines, l => l.Contains("Chain tip spawn claim released"));
        }
    }
}
