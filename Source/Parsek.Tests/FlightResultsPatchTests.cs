using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class FlightResultsPatchTests
    {
        public FlightResultsPatchTests()
        {
            ResetPatchState();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        [Fact]
        public void ClassifyDisplayIntercept_BypassWins()
        {
            var decision = FlightResultsPatch.ClassifyDisplayIntercept(
                bypass: true,
                isAutoMerge: false,
                shouldIntercept: true,
                hasPendingOutcome: true);

            Assert.Equal(
                FlightResultsPatch.DisplayInterceptDecision.AllowBypassReplay,
                decision);
        }

        [Fact]
        public void ClassifyDisplayIntercept_AutoMergePassesThrough()
        {
            var decision = FlightResultsPatch.ClassifyDisplayIntercept(
                bypass: false,
                isAutoMerge: true,
                shouldIntercept: true,
                hasPendingOutcome: false);

            Assert.Equal(
                FlightResultsPatch.DisplayInterceptDecision.Allow,
                decision);
        }

        [Fact]
        public void ClassifyDisplayIntercept_ArmedWithoutCapturedOutcome_SuppressesAndCaptures()
        {
            var decision = FlightResultsPatch.ClassifyDisplayIntercept(
                bypass: false,
                isAutoMerge: false,
                shouldIntercept: true,
                hasPendingOutcome: false);

            Assert.Equal(
                FlightResultsPatch.DisplayInterceptDecision.SuppressAndCapture,
                decision);
        }

        [Fact]
        public void ClassifyDisplayIntercept_CapturedOutcome_SuppressesDuplicate()
        {
            var decision = FlightResultsPatch.ClassifyDisplayIntercept(
                bypass: false,
                isAutoMerge: false,
                shouldIntercept: false,
                hasPendingOutcome: true);

            Assert.Equal(
                FlightResultsPatch.DisplayInterceptDecision.SuppressDuplicate,
                decision);
        }

        [Fact]
        public void Prefix_WhenArmed_CapturesOutcomeAndSuppresses()
        {
            FlightResultsPatch.DeferredMergeArmed = true;

            bool allowed = FlightResultsPatch.Prefix("Outcome: Catastrophic Failure!");

            Assert.False(allowed);
            Assert.False(FlightResultsPatch.DeferredMergeArmed);
            Assert.Equal("Outcome: Catastrophic Failure!", FlightResultsPatch.PendingOutcomeMsg);
        }

        [Fact]
        public void Prefix_WhenPendingOutcomeExists_SuppressesDuplicateWithoutOverwriting()
        {
            FlightResultsPatch.PendingOutcomeMsg = "Outcome: First Failure";

            bool allowed = FlightResultsPatch.Prefix("Outcome: Second Failure");

            Assert.False(allowed);
            Assert.Equal("Outcome: First Failure", FlightResultsPatch.PendingOutcomeMsg);
        }

        [Fact]
        public void Prefix_WhenBypassActive_AllowsReplayAndClearsBypass()
        {
            FlightResultsPatch.Bypass = true;

            bool allowed = FlightResultsPatch.Prefix("Outcome: Catastrophic Failure!");

            Assert.True(allowed);
            Assert.False(FlightResultsPatch.Bypass);
        }

        [Fact]
        public void CancelDeferredMerge_WithoutCapturedOutcome_DisarmsOnly()
        {
            FlightResultsPatch.DeferredMergeArmed = true;

            FlightResultsPatch.CancelDeferredMerge("unit test abort without capture");

            Assert.False(FlightResultsPatch.DeferredMergeArmed);
            Assert.False(FlightResultsPatch.HasPendingResults());
            Assert.False(FlightResultsPatch.Bypass);
        }

        [Fact]
        public void ReplayFlightResults_WithoutCapturedOutcome_ClearsArmedState()
        {
            FlightResultsPatch.DeferredMergeArmed = true;

            string msg = FlightResultsPatch.PrepareReplayFlightResults(
                "unit test resolve without capture");

            Assert.Null(msg);
            Assert.False(FlightResultsPatch.DeferredMergeArmed);
            Assert.False(FlightResultsPatch.HasPendingResults());
            Assert.False(FlightResultsPatch.Bypass);
        }

        [Fact]
        public void PrepareReplayFlightResults_WithCapturedOutcome_ClearsStateAndSetsBypass()
        {
            FlightResultsPatch.DeferredMergeArmed = true;
            FlightResultsPatch.PendingOutcomeMsg = "Outcome: Catastrophic Failure!";

            string msg = FlightResultsPatch.PrepareReplayFlightResults(
                "unit test resolve with capture");

            Assert.Equal("Outcome: Catastrophic Failure!", msg);
            Assert.False(FlightResultsPatch.DeferredMergeArmed);
            Assert.False(FlightResultsPatch.HasPendingResults());
            Assert.True(FlightResultsPatch.Bypass);
        }

        [Fact]
        public void ClearPending_ClearsCapturedOutcomeAndArmedState()
        {
            FlightResultsPatch.DeferredMergeArmed = true;
            FlightResultsPatch.PendingOutcomeMsg = "Outcome: Catastrophic Failure!";
            FlightResultsPatch.Bypass = true;

            FlightResultsPatch.ClearPending("unit test clear");

            Assert.False(FlightResultsPatch.DeferredMergeArmed);
            Assert.False(FlightResultsPatch.HasPendingResults());
            Assert.True(FlightResultsPatch.Bypass);
        }

        private static void ResetPatchState()
        {
            FlightResultsPatch.Bypass = false;
            FlightResultsPatch.DeferredMergeArmed = false;
            FlightResultsPatch.PendingOutcomeMsg = null;
        }
    }
}
