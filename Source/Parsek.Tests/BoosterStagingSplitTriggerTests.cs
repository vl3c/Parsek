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

        [Fact]
        public void ShouldPreferLiveBreakupChildSeed_ControlledLiveChildBeyondTolerance_ReturnsTrue()
        {
            bool preferLive = ParsekFlight.ShouldPreferLiveBreakupChildSeed(
                childHasController: true,
                liveVesselAvailable: true,
                capturedSeedAvailable: true,
                seedLiveRootDistanceMeters: 865.88,
                toleranceMeters: 250.0);

            Assert.True(preferLive);
        }

        [Fact]
        public void ShouldPreferLiveBreakupChildSeed_WithinTolerance_KeepsCapturedSeed()
        {
            bool preferLive = ParsekFlight.ShouldPreferLiveBreakupChildSeed(
                childHasController: true,
                liveVesselAvailable: true,
                capturedSeedAvailable: true,
                seedLiveRootDistanceMeters: 42.0,
                toleranceMeters: 250.0);

            Assert.False(preferLive);
        }

        [Fact]
        public void ShouldPreferLiveBreakupChildSeed_DebrisOrMissingLiveVessel_KeepsCapturedSeed()
        {
            Assert.False(ParsekFlight.ShouldPreferLiveBreakupChildSeed(
                childHasController: false,
                liveVesselAvailable: true,
                capturedSeedAvailable: true,
                seedLiveRootDistanceMeters: 865.88,
                toleranceMeters: 250.0));

            Assert.False(ParsekFlight.ShouldPreferLiveBreakupChildSeed(
                childHasController: true,
                liveVesselAvailable: false,
                capturedSeedAvailable: true,
                seedLiveRootDistanceMeters: 865.88,
                toleranceMeters: 250.0));
        }

        [Fact]
        public void PrepareRecorderStartCollections_NullCaches_CreateFreshCachesAndRequestSubscription()
        {
            List<int> created = null;
            Dictionary<uint, bool> controllerStatus = null;
            Dictionary<uint, string> trajectorySeeds = null;

            bool shouldSubscribe = ParsekFlight.PrepareRecorderStartCollections(
                ref created,
                ref controllerStatus,
                ref trajectorySeeds);

            Assert.True(shouldSubscribe);
            Assert.NotNull(created);
            Assert.NotNull(controllerStatus);
            Assert.NotNull(trajectorySeeds);
            Assert.Empty(created);
            Assert.Empty(controllerStatus);
            Assert.Empty(trajectorySeeds);
        }

        [Fact]
        public void PrepareRecorderStartCollections_ExistingCaches_AreClearedAndReused()
        {
            var created = new List<int> { 1, 2, 3 };
            var controllerStatus = new Dictionary<uint, bool>
            {
                [7u] = true
            };
            var trajectorySeeds = new Dictionary<uint, string>
            {
                [8u] = "seed"
            };

            bool shouldSubscribe = ParsekFlight.PrepareRecorderStartCollections(
                ref created,
                ref controllerStatus,
                ref trajectorySeeds);

            Assert.False(shouldSubscribe);
            Assert.NotNull(created);
            Assert.NotNull(controllerStatus);
            Assert.NotNull(trajectorySeeds);
            Assert.Empty(created);
            Assert.Empty(controllerStatus);
            Assert.Empty(trajectorySeeds);
        }
    }
}
