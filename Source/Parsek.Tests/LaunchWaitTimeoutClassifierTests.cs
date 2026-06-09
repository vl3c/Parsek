using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Truth table for <see cref="RuntimeTests.ClassifyLaunchWaitTimeout"/>, the pure
    /// decision both launch waits (WaitForLaunchAutoRecordStart /
    /// WaitForRecordingToLeavePrelaunch) apply at their timeout: craft-specific
    /// liftoff problems Skip (environmental), while a vessel that DID leave
    /// PRELAUNCH without satisfying the recording-side conditions stays a Fail.
    /// </summary>
    public class LaunchWaitTimeoutClassifierTests
    {
        [Fact]
        public void StillPrelaunch_NoThrustEver_SkipsNoThrust()
        {
            Assert.Equal(RuntimeTests.LaunchWaitTimeoutOutcome.SkipNoThrust,
                RuntimeTests.ClassifyLaunchWaitTimeout(
                    vesselPresent: true, stillPrelaunch: true,
                    everProducedThrust: false, producingThrustNow: false));
        }

        [Fact]
        public void StillPrelaunch_BurnedButNeverLiftedOff_SkipsNeverLiftedOff()
        {
            // The 2026-06-09 career playtest shape: 4 engines burned the whole
            // window, clamps already released, vessel crept a few metres but the
            // 10s deadline expired before the PRELAUNCH transition.
            Assert.Equal(RuntimeTests.LaunchWaitTimeoutOutcome.SkipNeverLiftedOff,
                RuntimeTests.ClassifyLaunchWaitTimeout(
                    vesselPresent: true, stillPrelaunch: true,
                    everProducedThrust: true, producingThrustNow: true));
        }

        [Fact]
        public void StillPrelaunch_BurnedThenFlamedOut_SkipsNeverLiftedOff()
        {
            // SRB burnout on the pad: thrust was produced earlier but is gone at
            // the deadline. Still a craft-performance problem, not a regression.
            Assert.Equal(RuntimeTests.LaunchWaitTimeoutOutcome.SkipNeverLiftedOff,
                RuntimeTests.ClassifyLaunchWaitTimeout(
                    vesselPresent: true, stillPrelaunch: true,
                    everProducedThrust: true, producingThrustNow: false));
        }

        [Fact]
        public void StillPrelaunch_ThrustOnlyAtDeadline_SkipsNeverLiftedOff()
        {
            // Late ignition: nothing burned during the window but an engine is
            // producing thrust right now. The craft can never satisfy the wait,
            // so this is still environmental.
            Assert.Equal(RuntimeTests.LaunchWaitTimeoutOutcome.SkipNeverLiftedOff,
                RuntimeTests.ClassifyLaunchWaitTimeout(
                    vesselPresent: true, stillPrelaunch: true,
                    everProducedThrust: false, producingThrustNow: true));
        }

        [Fact]
        public void LeftPrelaunch_ButConditionsUnmet_Fails()
        {
            // The vessel launched, so a timeout means the recording-side
            // conditions (live recording / expected recording id) never became
            // true: a real product failure that must NOT be skipped.
            Assert.Equal(RuntimeTests.LaunchWaitTimeoutOutcome.FailRecordingContract,
                RuntimeTests.ClassifyLaunchWaitTimeout(
                    vesselPresent: true, stillPrelaunch: false,
                    everProducedThrust: true, producingThrustNow: true));
        }

        [Fact]
        public void VesselGone_Fails()
        {
            Assert.Equal(RuntimeTests.LaunchWaitTimeoutOutcome.FailRecordingContract,
                RuntimeTests.ClassifyLaunchWaitTimeout(
                    vesselPresent: false, stillPrelaunch: false,
                    everProducedThrust: true, producingThrustNow: false));
        }
    }
}
