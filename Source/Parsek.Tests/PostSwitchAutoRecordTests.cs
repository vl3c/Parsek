using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    public class PostSwitchAutoRecordTests
    {
        [Theory]
        [InlineData(true, false, true, false, false, true)]
        [InlineData(false, false, true, false, false, false)]
        [InlineData(true, true, true, false, false, false)]
        [InlineData(true, false, false, false, false, false)]
        [InlineData(true, false, true, true, false, false)]
        [InlineData(true, false, true, false, true, false)]
        public void ShouldArmPostSwitchAutoRecord_RequiresIdleEnabledRealVessel(
            bool enabled,
            bool isRecording,
            bool hasNewVessel,
            bool newVesselIsGhost,
            bool newVesselIsEva,
            bool expected)
        {
            bool result = ParsekFlight.ShouldArmPostSwitchAutoRecord(
                enabled,
                isRecording,
                hasNewVessel,
                newVesselIsGhost,
                newVesselIsEva);

            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(false, false, (int)ParsekFlight.PostSwitchAutoRecordArmDecision.None)]
        [InlineData(true, true, (int)ParsekFlight.PostSwitchAutoRecordArmDecision.ArmTrackedBackgroundMember)]
        [InlineData(true, false, (int)ParsekFlight.PostSwitchAutoRecordArmDecision.ArmOutsider)]
        public void EvaluatePostSwitchAutoRecordArmDecision_ReturnsExpectedAction(
            bool shouldArm,
            bool trackedInActiveTree,
            int expected)
        {
            var result = ParsekFlight.EvaluatePostSwitchAutoRecordArmDecision(
                shouldArm,
                trackedInActiveTree);

            Assert.Equal((ParsekFlight.PostSwitchAutoRecordArmDecision)expected, result);
        }

        [Fact]
        public void EvaluatePostSwitchAutoRecordSuppression_StableFrame_ReturnsNone()
        {
            var result = ParsekFlight.EvaluatePostSwitchAutoRecordSuppression(
                autoRecordOnFirstModificationAfterSwitchEnabled: true,
                isRecording: false,
                hasActiveVessel: true,
                activeVesselPid: 42,
                armedVesselPid: 42,
                activeVesselIsGhost: false,
                isRestoringActiveTree: false,
                hasPendingTransition: false,
                isWarpActive: false,
                activeVesselPacked: false);

            Assert.Equal(ParsekFlight.PostSwitchAutoRecordSuppressionReason.None, result);
        }

        [Theory]
        [InlineData(false, false, true, 42u, 42u, false, false, false, false, false, (int)ParsekFlight.PostSwitchAutoRecordSuppressionReason.Disabled)]
        [InlineData(true, true, true, 42u, 42u, false, false, false, false, false, (int)ParsekFlight.PostSwitchAutoRecordSuppressionReason.AlreadyRecording)]
        [InlineData(true, false, false, 0u, 42u, false, false, false, false, false, (int)ParsekFlight.PostSwitchAutoRecordSuppressionReason.NoActiveVessel)]
        [InlineData(true, false, true, 7u, 42u, false, false, false, false, false, (int)ParsekFlight.PostSwitchAutoRecordSuppressionReason.ActiveVesselMismatch)]
        [InlineData(true, false, true, 42u, 42u, true, false, false, false, false, (int)ParsekFlight.PostSwitchAutoRecordSuppressionReason.GhostMapVessel)]
        [InlineData(true, false, true, 42u, 42u, false, true, false, false, false, (int)ParsekFlight.PostSwitchAutoRecordSuppressionReason.RestoreInProgress)]
        [InlineData(true, false, true, 42u, 42u, false, false, true, false, false, (int)ParsekFlight.PostSwitchAutoRecordSuppressionReason.PendingTransition)]
        [InlineData(true, false, true, 42u, 42u, false, false, false, true, false, (int)ParsekFlight.PostSwitchAutoRecordSuppressionReason.WarpActive)]
        [InlineData(true, false, true, 42u, 42u, false, false, false, false, true, (int)ParsekFlight.PostSwitchAutoRecordSuppressionReason.PackedOrOnRails)]
        public void EvaluatePostSwitchAutoRecordSuppression_ReturnsExpectedReason(
            bool enabled,
            bool isRecording,
            bool hasActiveVessel,
            uint activeVesselPid,
            uint armedVesselPid,
            bool activeVesselIsGhost,
            bool isRestoring,
            bool hasPendingTransition,
            bool isWarpActive,
            bool activeVesselPacked,
            int expected)
        {
            var result = ParsekFlight.EvaluatePostSwitchAutoRecordSuppression(
                enabled,
                isRecording,
                hasActiveVessel,
                activeVesselPid,
                armedVesselPid,
                activeVesselIsGhost,
                isRestoring,
                hasPendingTransition,
                isWarpActive,
                activeVesselPacked);

            Assert.Equal((ParsekFlight.PostSwitchAutoRecordSuppressionReason)expected, result);
        }

        [Theory]
        [InlineData(10.0, 11.0, false, 5, 5, false)]
        [InlineData(11.0, 11.0, false, 5, 5, true)]
        [InlineData(10.0, 11.0, true, 5, 5, true)]
        [InlineData(10.0, 11.0, false, 5, 6, true)]
        public void ShouldEvaluatePostSwitchManifestDiff_UsesThrottleAndInvalidationSignals(
            double currentUT,
            double nextManifestEvaluationUt,
            bool moduleCachesDirty,
            int cachedPartCount,
            int currentPartCount,
            bool expected)
        {
            bool needsCacheRefresh = moduleCachesDirty || cachedPartCount != currentPartCount;
            bool result = ParsekFlight.ShouldEvaluatePostSwitchManifestDiff(
                currentUT,
                nextManifestEvaluationUt,
                needsCacheRefresh);

            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(0.6, 0.0, true)]
        [InlineData(0.0, 0.6, true)]
        [InlineData(0.2, 0.2, false)]
        public void HasMeaningfulLandedMotionChange_UsesDistanceAndSpeedThresholds(
            double distanceDeltaMeters,
            double speedMetersPerSecond,
            bool expected)
        {
            bool result = ParsekFlight.HasMeaningfulLandedMotionChange(
                distanceDeltaMeters,
                speedMetersPerSecond);

            Assert.Equal(expected, result);
        }

        [Fact]
        public void HasMeaningfulOrbitChange_DetectsElementChangesWithoutSituationChange()
        {
            var baseline = new ParsekFlight.PostSwitchOrbitSnapshot
            {
                IsValid = true,
                BodyName = "Kerbin",
                SemiMajorAxis = 1000000.0,
                Eccentricity = 0.01,
                Inclination = 2.0,
                LongitudeOfAscendingNode = 15.0,
                ArgumentOfPeriapsis = 45.0
            };

            var current = baseline;
            current.SemiMajorAxis += 20.0;

            Assert.True(ParsekFlight.HasMeaningfulOrbitChange(baseline, current));
        }

        [Fact]
        public void HasMeaningfulOrbitChange_IgnoresTinyNoise()
        {
            var baseline = new ParsekFlight.PostSwitchOrbitSnapshot
            {
                IsValid = true,
                BodyName = "Kerbin",
                SemiMajorAxis = 1000000.0,
                Eccentricity = 0.01,
                Inclination = 2.0,
                LongitudeOfAscendingNode = 15.0,
                ArgumentOfPeriapsis = 45.0
            };

            var current = baseline;
            current.SemiMajorAxis += 0.5;
            current.Eccentricity += 1e-8;

            Assert.False(ParsekFlight.HasMeaningfulOrbitChange(baseline, current));
        }

        [Fact]
        public void ManifestDeltaHelpers_ClassifyCrewResourceAndInventoryChanges()
        {
            var crewDelta = new Dictionary<string, int> { ["Pilot"] = 1 };
            var resourceDelta = new Dictionary<string, double> { ["LiquidFuel"] = -5.0 };
            var inventoryDelta = new Dictionary<string, InventoryItem>
            {
                ["evaJetpack"] = new InventoryItem { count = -1, slotsTaken = -1 }
            };

            Assert.True(ParsekFlight.HasMeaningfulCrewDelta(crewDelta));
            Assert.True(ParsekFlight.HasMeaningfulResourceDelta(resourceDelta));
            Assert.True(ParsekFlight.HasMeaningfulInventoryDelta(inventoryDelta));
        }

        [Fact]
        public void HasMeaningfulResourceDelta_IgnoresNearZeroNoise()
        {
            var resourceDelta = new Dictionary<string, double> { ["LiquidFuel"] = 0.001 };

            Assert.False(ParsekFlight.HasMeaningfulResourceDelta(resourceDelta));
        }

        [Fact]
        public void HasMeaningfulPartStateTokenChange_DetectsDigestDifferences()
        {
            var baseline = new HashSet<string> { "gear:1:0" };
            var current = new HashSet<string>();

            Assert.True(ParsekFlight.HasMeaningfulPartStateTokenChange(baseline, current));
        }

        [Fact]
        public void HasMeaningfulPartStateChange_NonCosmeticTrigger_ReturnsTrue()
        {
            bool result = ParsekFlight.HasMeaningfulPartStateChange(
                new[] { PartEventType.GearDeployed });

            Assert.True(result);
        }

        [Fact]
        public void HasMeaningfulPartStateChange_CosmeticOnly_ReturnsFalse()
        {
            bool result = ParsekFlight.HasMeaningfulPartStateChange(
                new[] { PartEventType.LightOn, PartEventType.LightBlinkRate });

            Assert.False(result);
        }

        [Theory]
        [InlineData(false, false, false, false, false, false, false, (int)ParsekFlight.PostSwitchAutoRecordTrigger.None)]
        [InlineData(true, true, true, true, true, true, true, (int)ParsekFlight.PostSwitchAutoRecordTrigger.EngineActivity)]
        [InlineData(false, true, true, true, true, true, true, (int)ParsekFlight.PostSwitchAutoRecordTrigger.SustainedRcsActivity)]
        [InlineData(false, false, true, true, true, true, true, (int)ParsekFlight.PostSwitchAutoRecordTrigger.CrewChange)]
        [InlineData(false, false, false, true, true, true, true, (int)ParsekFlight.PostSwitchAutoRecordTrigger.ResourceChange)]
        [InlineData(false, false, false, false, true, true, true, (int)ParsekFlight.PostSwitchAutoRecordTrigger.PartStateChange)]
        [InlineData(false, false, false, false, false, true, true, (int)ParsekFlight.PostSwitchAutoRecordTrigger.LandedMotion)]
        [InlineData(false, false, false, false, false, false, true, (int)ParsekFlight.PostSwitchAutoRecordTrigger.OrbitChange)]
        public void EvaluatePostSwitchAutoRecordTrigger_UsesExpectedPriority(
            bool engineTriggered,
            bool rcsTriggered,
            bool crewChanged,
            bool resourceChanged,
            bool partStateChanged,
            bool landedMotionChanged,
            bool orbitChanged,
            int expected)
        {
            var result = ParsekFlight.EvaluatePostSwitchAutoRecordTrigger(
                engineTriggered,
                rcsTriggered,
                crewChanged,
                resourceChanged,
                partStateChanged,
                landedMotionChanged,
                orbitChanged);

            Assert.Equal((ParsekFlight.PostSwitchAutoRecordTrigger)expected, result);
        }

        [Fact]
        public void EvaluatePostSwitchAutoRecordStartDecision_TrackedBackgroundMember_Promotes()
        {
            var result = ParsekFlight.EvaluatePostSwitchAutoRecordStartDecision(
                armedVesselPid: 42,
                activeVesselPid: 42,
                hasActiveTree: true,
                activeVesselTrackedInBackground: true,
                canRestorePendingTrackedTree: false,
                suppressStart: false);

            Assert.Equal(
                ParsekFlight.PostSwitchAutoRecordStartDecision.PromoteTrackedRecording,
                result);
        }

        [Fact]
        public void EvaluatePostSwitchAutoRecordStartDecision_Outsider_StartsFresh()
        {
            var result = ParsekFlight.EvaluatePostSwitchAutoRecordStartDecision(
                armedVesselPid: 42,
                activeVesselPid: 42,
                hasActiveTree: true,
                activeVesselTrackedInBackground: false,
                canRestorePendingTrackedTree: false,
                suppressStart: false);

            Assert.Equal(
                ParsekFlight.PostSwitchAutoRecordStartDecision.StartFreshRecording,
                result);
        }

        [Fact]
        public void EvaluatePostSwitchAutoRecordStartDecision_SuppressedFrame_ReturnsNone()
        {
            var result = ParsekFlight.EvaluatePostSwitchAutoRecordStartDecision(
                armedVesselPid: 42,
                activeVesselPid: 42,
                hasActiveTree: true,
                activeVesselTrackedInBackground: true,
                canRestorePendingTrackedTree: false,
                suppressStart: true);

            Assert.Equal(ParsekFlight.PostSwitchAutoRecordStartDecision.None, result);
        }
    }
}
