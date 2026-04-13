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
    }
}
