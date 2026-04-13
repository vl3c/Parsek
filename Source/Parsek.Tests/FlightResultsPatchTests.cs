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

        [Theory]
        [InlineData(true, PendingTreeState.Finalized, true)]
        [InlineData(true, PendingTreeState.Limbo, false)]
        [InlineData(true, PendingTreeState.LimboVesselSwitch, false)]
        [InlineData(false, PendingTreeState.Finalized, false)]
        public void PendingTreeOwnsReplay_OnlyFinalizedPendingTreesOwn(
            bool hasPendingTree,
            PendingTreeState pendingTreeState,
            bool expected)
        {
            bool ownsReplay = FlightResultsPatch.PendingTreeOwnsReplay(
                hasPendingTree,
                pendingTreeState);

            Assert.Equal(expected, ownsReplay);
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

            bool pendingTreeOwnsReplay = FlightResultsPatch.PendingTreeOwnsReplay(
                hasPendingTree: true,
                pendingTreeState: PendingTreeState.Finalized);

            bool result = FlightResultsPatch.ShouldReplayOnFlightReady(
                pendingTreeOwnsReplay,
                mergeDialogPending: false);

            Assert.False(result);
        }

        [Fact]
        public void ShouldReplayOnFlightReady_TrueWhenPendingTreeIsOnlyLimboCarrier()
        {
            FlightResultsPatch.PendingOutcomeMsg = "Outcome: Catastrophic Failure!";

            bool pendingTreeOwnsReplay = FlightResultsPatch.PendingTreeOwnsReplay(
                hasPendingTree: true,
                pendingTreeState: PendingTreeState.Limbo);

            bool result = FlightResultsPatch.ShouldReplayOnFlightReady(
                pendingTreeOwnsReplay,
                mergeDialogPending: false);

            Assert.True(result);
        }

        [Fact]
        public void ShouldPreserveCapturedResultsOnSceneChange_TrueWhenSceneExitWillCreateMergeOwner()
        {
            FlightResultsPatch.PendingOutcomeMsg = "Outcome: Catastrophic Failure!";

            bool result = FlightResultsPatch.ShouldPreserveCapturedResultsOnSceneChange(
                pendingTreeOwnsReplay: false,
                sceneChangeWillCreateMergeOwner: true,
                mergeDialogPending: false);

            Assert.True(result);
        }

        [Fact]
        public void ShouldPreserveCapturedResultsOnSceneChange_FalseWhenOnlyLimboCarrierExists()
        {
            FlightResultsPatch.PendingOutcomeMsg = "Outcome: Catastrophic Failure!";

            bool result = FlightResultsPatch.ShouldPreserveCapturedResultsOnSceneChange(
                pendingTreeOwnsReplay: false,
                sceneChangeWillCreateMergeOwner: false,
                mergeDialogPending: false);

            Assert.False(result);
        }

        [Fact]
        public void ShouldPreserveCapturedResultsOnSceneChange_FalseWhenOnlyArmedButNothingCaptured()
        {
            FlightResultsPatch.DeferredMergeArmed = true;

            bool result = FlightResultsPatch.ShouldPreserveCapturedResultsOnSceneChange(
                pendingTreeOwnsReplay: true,
                sceneChangeWillCreateMergeOwner: true,
                mergeDialogPending: true);

            Assert.False(result);
        }

        [Fact]
        public void ResolveAwaitingSceneChangeMergeOwnerOnFlightReady_WithOwner_KeepsArmedUntilCaptureOrResolution()
        {
            FlightResultsPatch.AwaitingSceneChangeMergeOwner = true;
            FlightResultsPatch.DeferredMergeArmed = true;

            FlightResultsPatch.ResolveAwaitingSceneChangeMergeOwnerOnFlightReady(
                mergeOwnerExists: true,
                reason: "unit test owner exists");

            Assert.False(FlightResultsPatch.AwaitingSceneChangeMergeOwner);
            Assert.True(FlightResultsPatch.DeferredMergeArmed);
            Assert.False(FlightResultsPatch.HasPendingResults());
        }

        [Fact]
        public void ResolveAwaitingSceneChangeMergeOwnerOnFlightReady_WithoutOwner_ClearsCapturedState()
        {
            FlightResultsPatch.AwaitingSceneChangeMergeOwner = true;
            FlightResultsPatch.PendingOutcomeMsg = "Outcome: Catastrophic Failure!";
            FlightResultsPatch.DeferredMergeArmed = true;

            FlightResultsPatch.ResolveAwaitingSceneChangeMergeOwnerOnFlightReady(
                mergeOwnerExists: false,
                reason: "unit test no owner");

            Assert.False(FlightResultsPatch.AwaitingSceneChangeMergeOwner);
            Assert.False(FlightResultsPatch.DeferredMergeArmed);
            Assert.False(FlightResultsPatch.HasPendingResults());
        }

        [Fact]
        public void ShouldPreserveAwaitingSceneChangeOwnerOnSceneChange_TrueForFlight()
        {
            bool result = FlightResultsPatch.ShouldPreserveAwaitingSceneChangeOwnerOnSceneChange(
                awaitingSceneChangeMergeOwner: true,
                pendingDestinationScene: GameScenes.FLIGHT);

            Assert.True(result);
        }

        [Fact]
        public void ShouldPreserveAwaitingSceneChangeOwnerOnSceneChange_FalseForMainMenu()
        {
            bool result = FlightResultsPatch.ShouldPreserveAwaitingSceneChangeOwnerOnSceneChange(
                awaitingSceneChangeMergeOwner: true,
                pendingDestinationScene: GameScenes.MAINMENU);

            Assert.False(result);
        }

        [Fact]
        public void ClearPending_ClearsCapturedOutcomeAndArmedState()
        {
            FlightResultsPatch.DeferredMergeArmed = true;
            FlightResultsPatch.PendingOutcomeMsg = "Outcome: Catastrophic Failure!";
            FlightResultsPatch.AwaitingSceneChangeMergeOwner = true;
            FlightResultsPatch.Bypass = true;

            FlightResultsPatch.ClearPending("unit test clear");

            Assert.False(FlightResultsPatch.AwaitingSceneChangeMergeOwner);
            Assert.False(FlightResultsPatch.DeferredMergeArmed);
            Assert.False(FlightResultsPatch.HasPendingResults());
            Assert.True(FlightResultsPatch.Bypass);
        }

        private static void ResetPatchState()
        {
            FlightResultsPatch.Bypass = false;
            FlightResultsPatch.DeferredMergeArmed = false;
            FlightResultsPatch.PendingOutcomeMsg = null;
            FlightResultsPatch.AwaitingSceneChangeMergeOwner = false;
        }
    }
}
