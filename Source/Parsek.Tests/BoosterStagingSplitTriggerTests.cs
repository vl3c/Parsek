using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class BoosterStagingSplitTriggerTests
    {
        [Fact]
        public void ResolveDeferredSplitCheckTrigger_JointBreakWinsWhenBothSignalsArePresent()
        {
            var trigger = ParsekFlight.ResolveDeferredSplitCheckTrigger(
                pendingSplitInProgress: false,
                recorderIsRecording: true,
                jointBreakPending: true,
                decoupleCreatedVesselsPending: true);

            Assert.Equal(ParsekFlight.DeferredSplitCheckTrigger.JointBreak, trigger);
        }

        [Fact]
        public void ResolveDeferredSplitCheckTrigger_DecoupleCreatedVesselsStartFallbackCheck()
        {
            var trigger = ParsekFlight.ResolveDeferredSplitCheckTrigger(
                pendingSplitInProgress: false,
                recorderIsRecording: true,
                jointBreakPending: false,
                decoupleCreatedVesselsPending: true);

            Assert.Equal(ParsekFlight.DeferredSplitCheckTrigger.DecoupleCreatedVessel, trigger);
        }

        [Fact]
        public void ResolveDeferredSplitCheckTrigger_SuppressesNewCheckWhileSplitAlreadyPending()
        {
            var trigger = ParsekFlight.ResolveDeferredSplitCheckTrigger(
                pendingSplitInProgress: true,
                recorderIsRecording: true,
                jointBreakPending: true,
                decoupleCreatedVesselsPending: true);

            Assert.Equal(ParsekFlight.DeferredSplitCheckTrigger.None, trigger);
        }

        [Fact]
        public void ResolveDeferredSplitCheckTrigger_DoesNotStartWhenRecorderIsInactive()
        {
            var trigger = ParsekFlight.ResolveDeferredSplitCheckTrigger(
                pendingSplitInProgress: false,
                recorderIsRecording: false,
                jointBreakPending: false,
                decoupleCreatedVesselsPending: true);

            Assert.Equal(ParsekFlight.DeferredSplitCheckTrigger.None, trigger);
        }

        [Fact]
        public void ShouldCaptureDecoupleCreatedVessel_MatchesRecordedVessel()
        {
            bool capture = ParsekFlight.ShouldCaptureDecoupleCreatedVessel(
                recordedVesselPid: 2708531065u,
                originalVesselPid: 2708531065u);

            Assert.True(capture);
        }

        [Fact]
        public void ShouldCaptureDecoupleCreatedVessel_IgnoresUnrelatedVessel()
        {
            bool capture = ParsekFlight.ShouldCaptureDecoupleCreatedVessel(
                recordedVesselPid: 2708531065u,
                originalVesselPid: 3819315892u);

            Assert.False(capture);
        }

        [Fact]
        public void ShouldCaptureDecoupleCreatedVessel_IgnoresMissingRecordedPid()
        {
            bool capture = ParsekFlight.ShouldCaptureDecoupleCreatedVessel(
                recordedVesselPid: 0u,
                originalVesselPid: 2708531065u);

            Assert.False(capture);
        }

        [Fact]
        public void ResolveDeferredSplitBranchUT_PrefersCapturedDecoupleSeedUT()
        {
            var captured = new Dictionary<uint, TrajectoryPoint>
            {
                [1079957709u] = new TrajectoryPoint { ut = 25.38 },
                [1789849836u] = new TrajectoryPoint { ut = 25.39 },
                [999u] = new TrajectoryPoint { ut = 12.00 }
            };

            double branchUT = ParsekFlight.ResolveDeferredSplitBranchUT(
                fallbackUT: 25.40,
                exactTriggerUT: double.NaN,
                newVesselPids: new[] { 1079957709u, 1789849836u },
                capturedTrajectoryPoints: captured);

            Assert.Equal(25.38, branchUT, 3);
        }

        [Fact]
        public void ResolveDeferredSplitBranchUT_UsesExactTriggerUTWhenNoCapturedSeedExists()
        {
            double branchUT = ParsekFlight.ResolveDeferredSplitBranchUT(
                fallbackUT: 41.33,
                exactTriggerUT: 41.25,
                newVesselPids: new[] { 3027027466u, 2130796824u },
                capturedTrajectoryPoints: new Dictionary<uint, TrajectoryPoint>());

            Assert.Equal(41.25, branchUT, 3);
        }

        [Fact]
        public void ResolveDeferredSplitBranchUT_FallsBackToDeferredCheckUTWithoutExactSignals()
        {
            double branchUT = ParsekFlight.ResolveDeferredSplitBranchUT(
                fallbackUT: 61.39,
                exactTriggerUT: double.NaN,
                newVesselPids: new[] { 3271565278u, 633147235u },
                capturedTrajectoryPoints: null);

            Assert.Equal(61.39, branchUT, 3);
        }
    }
}
