using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins which store a route endpoint's cargo is read from and written to
    /// (<see cref="RouteOrchestrator.EndpointStoreIsLiveParts(bool, bool)"/>).
    /// Every probe/writer pair in Logistics captures this ONE decision.
    /// </summary>
    public class EndpointStoreGateTests
    {
        // catches: a loaded-but-PACKED endpoint (inside physics range, on rails
        // under warp or a seam TimeJump) routed to the proto-snapshot branch.
        // Stock BackupVessel rebuilds protoVessel from the live parts on the
        // next save, so the debit and the delivery vanished: RVR-8 run
        // 2026-09-27_1147 re-read origin B at 200 on cycle 1 and delivered a
        // second time, and even the green 2026-09-10 run's produced save held
        // B=200 / A=200 after a completed cycle.
        [Fact]
        public void LoadedPackedVessel_UsesLiveParts()
        {
            Assert.True(RouteOrchestrator.EndpointStoreIsLiveParts(vesselLoaded: true, vesselPacked: true));
        }

        [Fact]
        public void LoadedUnpackedVessel_UsesLiveParts()
        {
            Assert.True(RouteOrchestrator.EndpointStoreIsLiveParts(vesselLoaded: true, vesselPacked: false));
        }

        // Mirror direction: an unloaded vessel has no live parts, so its proto
        // snapshots are the only store and the next load initializes from them.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void UnloadedVessel_UsesProtoSnapshots(bool packed)
        {
            Assert.False(RouteOrchestrator.EndpointStoreIsLiveParts(vesselLoaded: false, vesselPacked: packed));
        }

        [Fact]
        public void NullVessel_UsesProtoBranch()
        {
            Assert.False(RouteOrchestrator.EndpointStoreIsLiveParts((Vessel)null));
        }
    }
}
