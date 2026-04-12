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
                deferredMergeArmed: true,
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
                deferredMergeArmed: true,
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
                deferredMergeArmed: true,
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
                deferredMergeArmed: false,
                hasPendingOutcome: true);

            Assert.Equal(
                FlightResultsPatch.DisplayInterceptDecision.SuppressDuplicate,
                decision);
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
        public void ShouldReplayOnFlightReady_FalseWhenPendingTreeOwnsReplay()
        {
            FlightResultsPatch.PendingOutcomeMsg = "Outcome: Catastrophic Failure!";

            bool result = FlightResultsPatch.ShouldReplayOnFlightReady(
                hasPendingTree: true,
                mergeDialogPending: false);

            Assert.False(result);
        }

        [Fact]
        public void ShouldReplayOnFlightReady_TrueWhenNoOwnerExists()
        {
            FlightResultsPatch.PendingOutcomeMsg = "Outcome: Catastrophic Failure!";

            bool result = FlightResultsPatch.ShouldReplayOnFlightReady(
                hasPendingTree: false,
                mergeDialogPending: false);

            Assert.True(result);
        }

        [Fact]
        public void ShouldPreserveCapturedResultsOnSceneChange_TrueWhenTreeDestructionPending()
        {
            FlightResultsPatch.PendingOutcomeMsg = "Outcome: Catastrophic Failure!";

            bool result = FlightResultsPatch.ShouldPreserveCapturedResultsOnSceneChange(
                hasPendingTree: false,
                treeDestructionDialogPending: true,
                mergeDialogPending: false);

            Assert.True(result);
        }

        [Fact]
        public void ShouldPreserveCapturedResultsOnSceneChange_FalseWhenNoMergeOwnerExists()
        {
            FlightResultsPatch.PendingOutcomeMsg = "Outcome: Catastrophic Failure!";

            bool result = FlightResultsPatch.ShouldPreserveCapturedResultsOnSceneChange(
                hasPendingTree: false,
                treeDestructionDialogPending: false,
                mergeDialogPending: false);

            Assert.False(result);
        }

        [Fact]
        public void ShouldPreserveCapturedResultsOnSceneChange_FalseWhenOnlyArmedButNothingCaptured()
        {
            FlightResultsPatch.DeferredMergeArmed = true;

            bool result = FlightResultsPatch.ShouldPreserveCapturedResultsOnSceneChange(
                hasPendingTree: true,
                treeDestructionDialogPending: true,
                mergeDialogPending: true);

            Assert.False(result);
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
