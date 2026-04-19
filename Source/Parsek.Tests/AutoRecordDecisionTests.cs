using Xunit;

namespace Parsek.Tests
{
    public class AutoRecordDecisionTests
    {
        [Fact]
        public void EvaluateAutoRecordLaunchDecision_RecordingInProgress_SkipsAlreadyRecording()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: true,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.PRELAUNCH,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: 0,
                currentUt: 10,
                landedSettleThreshold: 5.0);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipAlreadyRecording, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_NonActiveVessel_SkipsInactiveVessel()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: false,
                fromSituation: Vessel.Situations.PRELAUNCH,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: 0,
                currentUt: 10,
                landedSettleThreshold: 5.0);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipInactiveVessel, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_PrelaunchAndEnabled_StartsFromPrelaunch()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.PRELAUNCH,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: -1,
                currentUt: 10,
                landedSettleThreshold: 5.0);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.StartFromPrelaunch, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_PrelaunchButDisabled_SkipsDisabled()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.PRELAUNCH,
                autoRecordOnLaunchEnabled: false,
                lastLandedUt: -1,
                currentUt: 10,
                landedSettleThreshold: 5.0);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipDisabled, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_LandedBelowSettleThreshold_SkipsBounce()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.LANDED,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: 96.0,
                currentUt: 100.0,
                landedSettleThreshold: 5.0);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipBounce, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_LandedWithoutSeededSettleTime_SkipsBounce()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.LANDED,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: -1.0,
                currentUt: 100.0,
                landedSettleThreshold: 5.0);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipBounce, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_SettledLandedAndEnabled_StartsFromSettledLanded()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.LANDED,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: 95.0,
                currentUt: 100.0,
                landedSettleThreshold: 5.0);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.StartFromSettledLanded, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_SettledLandedButDisabled_SkipsDisabled()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.LANDED,
                autoRecordOnLaunchEnabled: false,
                lastLandedUt: 95.0,
                currentUt: 100.0,
                landedSettleThreshold: 5.0);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipDisabled, decision);
        }

        [Fact]
        public void EvaluateAutoRecordLaunchDecision_OtherTransition_SkipsNotLaunchTransition()
        {
            var decision = ParsekFlight.EvaluateAutoRecordLaunchDecision(
                isRecording: false,
                isActiveVessel: true,
                fromSituation: Vessel.Situations.FLYING,
                autoRecordOnLaunchEnabled: true,
                lastLandedUt: 95.0,
                currentUt: 100.0,
                landedSettleThreshold: 5.0);

            Assert.Equal(ParsekFlight.AutoRecordLaunchDecision.SkipNotLaunchTransition, decision);
        }

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(false, false, false)]
        public void ShouldQueueAutoRecordOnEva_RequiresSourceVesselAndEnabledSetting(
            bool hasSourceVessel,
            bool autoRecordOnEvaEnabled,
            bool expected)
        {
            bool result = ParsekFlight.ShouldQueueAutoRecordOnEva(
                hasSourceVessel,
                autoRecordOnEvaEnabled);

            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(true, false, true, true, true)]
        [InlineData(false, false, true, true, false)]
        [InlineData(true, true, true, true, false)]
        [InlineData(true, false, false, false, false)]
        [InlineData(true, false, true, false, false)]
        public void ShouldStartDeferredAutoRecordEva_RequiresPendingIdleActiveEva(
            bool pendingAutoRecord,
            bool isRecording,
            bool hasActiveVessel,
            bool activeVesselIsEva,
            bool expected)
        {
            bool result = ParsekFlight.ShouldStartDeferredAutoRecordEva(
                pendingAutoRecord,
                isRecording,
                hasActiveVessel,
                activeVesselIsEva);

            Assert.Equal(expected, result);
        }
    }
}
