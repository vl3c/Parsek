using Parsek.Tests.Rendering;
using Xunit;

namespace Parsek.Tests
{
    // TEMPORARY Phase B leak-check scaffolding. Drives each leaking class's full
    // ctor -> test -> Dispose lifecycle in-process and asserts the shared static it installs is
    // back to the value it found. Deleted before the commit.
    [Collection("Sequential")]
    public class ZzPhaseBLeakProbeTests
    {
        [Fact]
        public void OutlierFlags_DoesNotLeakTheRotationPeriodSeam()
        {
            var before = TrajectoryMath.FrameTransform.RotationPeriodForTesting;
            using (var t = new OutlierFlagsSidecarRoundTripTests())
            {
                t.AlgorithmStampDrift_V6_To_V7_DiscardsOldFile();
            }
            Assert.Same(before, TrajectoryMath.FrameTransform.RotationPeriodForTesting);
        }

        [Fact]
        public void KerbalReservation_DoesNotLeakTheKerbalsModule()
        {
            var before = LedgerOrchestrator.Kerbals;
            using (var t = new KerbalReservationTests())
            {
                t.Recalculate_RepairedStandInRecording_StillRetiresHistoricalStandIn();
            }
            Assert.Same(before, LedgerOrchestrator.Kerbals);
        }
    }
}
