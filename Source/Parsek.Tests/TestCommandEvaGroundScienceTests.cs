using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the <c>EvaGroundScience</c> seam verb's decision core (coverage
    /// wave 10) and for the inventory surface's honest apply outcome. No KSP types.
    /// The internal enums cannot appear in a public theory signature, so the cells
    /// enumerate inside facts (the TestCommandEvaChuteDeployTests constraint).
    /// </summary>
    public class TestCommandEvaGroundScienceTests
    {
        [Fact]
        public void TryParseAction_AcceptsExactlyTheThreeTokens()
        {
            Assert.True(TestCommandEvaGroundScience.TryParseAction("place", out var a));
            Assert.Equal(EvaGroundScienceAction.Place, a);
            Assert.True(TestCommandEvaGroundScience.TryParseAction("pickup", out a));
            Assert.Equal(EvaGroundScienceAction.Pickup, a);
            Assert.True(TestCommandEvaGroundScience.TryParseAction("take", out a));
            Assert.Equal(EvaGroundScienceAction.Take, a);
            Assert.False(TestCommandEvaGroundScience.TryParseAction("Take", out _));
            Assert.False(TestCommandEvaGroundScience.TryParseAction("Place", out _));
            Assert.False(TestCommandEvaGroundScience.TryParseAction("remove", out _));
            Assert.False(TestCommandEvaGroundScience.TryParseAction("", out _));
            Assert.False(TestCommandEvaGroundScience.TryParseAction(null, out _));
        }

        [Fact]
        public void NormalizePartName_UsesTheRuntimeDotForm()
        {
            Assert.Equal("solidBooster.v2", TestCommandEvaGroundScience.NormalizePartName("solidBooster_v2"));
            Assert.Equal("DeployedSeismicSensor", TestCommandEvaGroundScience.NormalizePartName("DeployedSeismicSensor"));
            Assert.Null(TestCommandEvaGroundScience.NormalizePartName(null));
        }

        [Fact]
        public void FindSlotHolding_PicksTheLowestMatchingSlot()
        {
            var slots = new List<KeyValuePair<int, string>>
            {
                new KeyValuePair<int, string>(2, "DeployedSeismicSensor"),
                new KeyValuePair<int, string>(0, "evaChute"),
                new KeyValuePair<int, string>(1, "DeployedSeismicSensor"),
            };
            Assert.Equal(1, TestCommandEvaGroundScience.FindSlotHolding(slots, "DeployedSeismicSensor"));
            Assert.Equal(0, TestCommandEvaGroundScience.FindSlotHolding(slots, "evaChute"));
            Assert.Equal(-1, TestCommandEvaGroundScience.FindSlotHolding(slots, "evaJetpack"));
            // Ordinal: a case variant is a different part.
            Assert.Equal(-1, TestCommandEvaGroundScience.FindSlotHolding(slots, "deployedseismicsensor"));
            Assert.Equal(-1, TestCommandEvaGroundScience.FindSlotHolding(null, "evaChute"));
            Assert.Equal(-1, TestCommandEvaGroundScience.FindSlotHolding(slots, null));
        }

        [Fact]
        public void IsInjectedPressFrame_OnlyTheArmedFrame()
        {
            Assert.True(TestCommandEvaGroundScience.IsInjectedPressFrame(100, 100));
            Assert.False(TestCommandEvaGroundScience.IsInjectedPressFrame(100, 99));
            Assert.False(TestCommandEvaGroundScience.IsInjectedPressFrame(100, 101));
            // Disarmed never presses, even on frame -1.
            Assert.False(TestCommandEvaGroundScience.IsInjectedPressFrame(-1, -1));
        }

        [Fact]
        public void ShouldReTurn_OnlyAnUnplaceableBuiltPreview_Bounded()
        {
            int wait = TestCommandEvaGroundScience.ReTurnFrames;
            int never = int.MaxValue;
            Assert.True(TestCommandEvaGroundScience.ShouldReTurn(true, true, false, 0, wait, never, 0));
            Assert.False(TestCommandEvaGroundScience.ShouldReTurn(true, true, false, 0, wait - 1, never, 0));
            Assert.False(TestCommandEvaGroundScience.ShouldReTurn(false, true, false, 0, wait, never, 0));
            Assert.False(TestCommandEvaGroundScience.ShouldReTurn(true, false, false, 0, wait, never, 0));
            Assert.False(TestCommandEvaGroundScience.ShouldReTurn(true, true, true, 0, wait, never, 0));
            Assert.False(TestCommandEvaGroundScience.ShouldReTurn(
                true, true, false, 0, wait, never, TestCommandEvaGroundScience.MaxReTurns));
            Assert.True(TestCommandEvaGroundScience.ShouldReTurn(
                true, true, false, 0, wait, never, TestCommandEvaGroundScience.MaxReTurns - 1));
        }

        [Fact]
        public void ShouldReTurn_AfterAPressThatDidNotLand_OncePastItsSettleWindow()
        {
            // Run 2026-09-27_1344: presses=1, the preview still up and the spot blocked
            // (hit=COL/layer0) for the rest of the 120 s budget. The old gate refused
            // every re-turn once a press went out; a missed press must not end the retries.
            int wait = TestCommandEvaGroundScience.ReTurnFrames;
            Assert.True(TestCommandEvaGroundScience.ShouldReTurn(true, true, false, 1, wait, wait, 1));
            Assert.True(TestCommandEvaGroundScience.ShouldReTurn(true, true, false, 3, wait * 5, wait, 4));
            // The press is still in its settle window: its effect is not read yet.
            Assert.False(TestCommandEvaGroundScience.ShouldReTurn(true, true, false, 1, wait, wait - 1, 1));
            // The re-turn spacing still holds after a press.
            Assert.False(TestCommandEvaGroundScience.ShouldReTurn(true, true, false, 1, wait - 1, wait, 1));
            // A placeable spot after a press is the confirm phase's business, never a re-turn.
            Assert.False(TestCommandEvaGroundScience.ShouldReTurn(true, true, true, 1, wait, wait, 1));
            // The overall bound still applies.
            Assert.False(TestCommandEvaGroundScience.ShouldReTurn(
                true, true, false, 1, wait, wait, TestCommandEvaGroundScience.MaxReTurns));
        }

        [Fact]
        public void ReTurnBudget_LeavesAPressPerReTurnAndFitsTheStepBudget()
        {
            Assert.True(TestCommandEvaGroundScience.MaxConfirmPresses >= TestCommandEvaGroundScience.MaxReTurns);
            // Every re-turn waits ReTurnFrames; at 30 fps the whole ladder stays well
            // inside the seam's 120 s EvaGroundScience budget.
            double worstSeconds = (TestCommandEvaGroundScience.MaxReTurns + 1)
                * 2.0 * TestCommandEvaGroundScience.ReTurnFrames / 30.0;
            Assert.True(worstSeconds < 120.0 / 2, "re-turn ladder worst case " + worstSeconds + " s");
        }

        [Fact]
        public void TurnOffsetDegrees_StartsStraightAwayThenFansOutAndRepeats()
        {
            Assert.Equal(0.0, TestCommandEvaGroundScience.TurnOffsetDegrees(0));
            Assert.Equal(0.0, TestCommandEvaGroundScience.TurnOffsetDegrees(-3));
            Assert.Equal(30.0, TestCommandEvaGroundScience.TurnOffsetDegrees(1));
            Assert.Equal(-30.0, TestCommandEvaGroundScience.TurnOffsetDegrees(2));
            Assert.Equal(60.0, TestCommandEvaGroundScience.TurnOffsetDegrees(3));
            Assert.Equal(-60.0, TestCommandEvaGroundScience.TurnOffsetDegrees(4));
            Assert.Equal(0.0, TestCommandEvaGroundScience.TurnOffsetDegrees(5));
            Assert.Equal(30.0, TestCommandEvaGroundScience.TurnOffsetDegrees(6));
            // Every offset keeps the kerbal facing away from the hull (under 90 degrees).
            for (int i = 0; i <= TestCommandEvaGroundScience.MaxReTurns; i++)
                Assert.True(System.Math.Abs(TestCommandEvaGroundScience.TurnOffsetDegrees(i)) < 90.0);
        }

        [Fact]
        public void ShouldHoldHeading_OnlyPastTheToleranceUntilConfirmed_Bounded()
        {
            double tol = TestCommandEvaGroundScience.HeadingHoldToleranceDegrees;
            int cap = TestCommandEvaGroundScience.MaxHeadingHolds;
            // The drift the EVA-5/6/7 logs show one second after a turn.
            Assert.True(TestCommandEvaGroundScience.ShouldHoldHeading(true, true, false, 153.6, 0));
            Assert.True(TestCommandEvaGroundScience.ShouldHoldHeading(true, true, false, tol + 0.1, cap - 1));
            Assert.False(TestCommandEvaGroundScience.ShouldHoldHeading(true, true, false, tol, 0));
            Assert.False(TestCommandEvaGroundScience.ShouldHoldHeading(true, true, false, 153.6, cap));
            // No faceAway, no heading chosen yet (drift reads -1), or already placed: never.
            Assert.False(TestCommandEvaGroundScience.ShouldHoldHeading(false, true, false, 153.6, 0));
            Assert.False(TestCommandEvaGroundScience.ShouldHoldHeading(true, false, false, 153.6, 0));
            Assert.False(TestCommandEvaGroundScience.ShouldHoldHeading(true, true, true, 153.6, 0));
            Assert.False(TestCommandEvaGroundScience.ShouldHoldHeading(true, true, false, -1.0, 0));
        }

        [Fact]
        public void DecideConfirm_PressesOnlyOnABuiltPlaceablePreview()
        {
            Assert.Equal(GroundPlaceConfirmDecision.Press,
                TestCommandEvaGroundScience.DecideConfirm(true, true, true, 0, int.MaxValue));
            // No preview yet, preview still building, spot not placeable: wait.
            Assert.Equal(GroundPlaceConfirmDecision.Wait,
                TestCommandEvaGroundScience.DecideConfirm(false, true, true, 0, int.MaxValue));
            Assert.Equal(GroundPlaceConfirmDecision.Wait,
                TestCommandEvaGroundScience.DecideConfirm(true, false, true, 0, int.MaxValue));
            Assert.Equal(GroundPlaceConfirmDecision.Wait,
                TestCommandEvaGroundScience.DecideConfirm(true, true, false, 0, int.MaxValue));
        }

        [Fact]
        public void DecideConfirm_SpacesPressesAndGivesUpAfterTheCap()
        {
            int gap = TestCommandEvaGroundScience.FramesBetweenPresses;
            // A press is in flight: wait out the gap even on a placeable spot.
            Assert.Equal(GroundPlaceConfirmDecision.Wait,
                TestCommandEvaGroundScience.DecideConfirm(true, true, true, 1, gap - 1));
            Assert.Equal(GroundPlaceConfirmDecision.Press,
                TestCommandEvaGroundScience.DecideConfirm(true, true, true, 1, gap));
            int cap = TestCommandEvaGroundScience.MaxConfirmPresses;
            Assert.Equal(GroundPlaceConfirmDecision.GiveUp,
                TestCommandEvaGroundScience.DecideConfirm(true, true, true, cap, gap));
            // Giving up does not wait for the spot to become placeable again.
            Assert.Equal(GroundPlaceConfirmDecision.GiveUp,
                TestCommandEvaGroundScience.DecideConfirm(true, true, false, cap, gap));
        }

        [Fact]
        public void DecidePlaceCompletion_NeedsEveryConjunctHeldForTheSettleWindow()
        {
            int settle = TestCommandEvaGroundScience.SettleFrames;
            Assert.Equal(GroundScienceCompletionDecision.CompleteOk,
                TestCommandEvaGroundScience.DecidePlaceCompletion(5, 120, true, true, true, settle));
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaGroundScience.DecidePlaceCompletion(5, 120, true, true, true, settle - 1));
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaGroundScience.DecidePlaceCompletion(5, 120, false, true, true, settle));
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaGroundScience.DecidePlaceCompletion(5, 120, true, false, true, settle));
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaGroundScience.DecidePlaceCompletion(5, 120, true, true, false, settle));
            Assert.Equal(GroundScienceCompletionDecision.Timeout,
                TestCommandEvaGroundScience.DecidePlaceCompletion(120, 120, true, true, false, settle));
            // A completed state wins over the budget on the same poll.
            Assert.Equal(GroundScienceCompletionDecision.CompleteOk,
                TestCommandEvaGroundScience.DecidePlaceCompletion(130, 120, true, true, true, settle));
        }

        [Fact]
        public void DecidePickupCompletion_NeedsTheVesselGoneAndThePartBack()
        {
            int settle = TestCommandEvaGroundScience.SettleFrames;
            Assert.Equal(GroundScienceCompletionDecision.CompleteOk,
                TestCommandEvaGroundScience.DecidePickupCompletion(5, 120, true, true, settle));
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaGroundScience.DecidePickupCompletion(5, 120, false, true, settle));
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaGroundScience.DecidePickupCompletion(5, 120, true, false, settle));
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaGroundScience.DecidePickupCompletion(5, 120, true, true, settle - 1));
            Assert.Equal(GroundScienceCompletionDecision.Timeout,
                TestCommandEvaGroundScience.DecidePickupCompletion(121, 120, false, true, 0));
        }

        [Fact]
        public void ActionToken_RoundTripsThroughTryParseAction()
        {
            foreach (EvaGroundScienceAction action in new[]
                { EvaGroundScienceAction.Place, EvaGroundScienceAction.Pickup, EvaGroundScienceAction.Take })
            {
                string token = TestCommandEvaGroundScience.ActionToken(action);
                Assert.True(TestCommandEvaGroundScience.TryParseAction(token, out var parsed));
                Assert.Equal(action, parsed);
            }
        }

        private static GroundTakeCandidate Cand(int container, int slot, string part, double dist)
            => new GroundTakeCandidate { ContainerIndex = container, Slot = slot, PartName = part, DistanceMeters = dist };

        [Fact]
        public void ChooseTakeSource_NearestContainerInReachThenLowestSlot()
        {
            var cands = new List<GroundTakeCandidate>
            {
                Cand(0, 2, "DeployedRTG", 3.0),
                Cand(0, 1, "DeployedRTG", 3.0),
                Cand(1, 0, "DeployedRTG", 1.5),
                Cand(1, 1, "DeployedSeismicSensor", 1.5),
                Cand(2, 0, "DeployedRTG", 9.0),
            };
            GroundTakeSourceChoice c = TestCommandEvaGroundScience.ChooseTakeSource(cands, "DeployedRTG", 5.0);
            Assert.Equal(GroundTakeSourceDecision.Found, c.Decision);
            Assert.Equal(1, c.Candidate.ContainerIndex);
            Assert.Equal(0, c.Candidate.Slot);
            Assert.Equal(1.5, c.NearestDistanceMeters);

            // Same distance: lower container, then lower slot, independent of input order.
            cands.RemoveAt(2);
            c = TestCommandEvaGroundScience.ChooseTakeSource(cands, "DeployedRTG", 5.0);
            Assert.Equal(0, c.Candidate.ContainerIndex);
            Assert.Equal(1, c.Candidate.Slot);
            cands.Reverse();
            c = TestCommandEvaGroundScience.ChooseTakeSource(cands, "DeployedRTG", 5.0);
            Assert.Equal(0, c.Candidate.ContainerIndex);
            Assert.Equal(1, c.Candidate.Slot);
        }

        [Fact]
        public void ChooseTakeSource_TellsNotStoredFromOutOfReach()
        {
            var cands = new List<GroundTakeCandidate>
            {
                Cand(0, 0, "DeployedRTG", 6.25),
                Cand(1, 0, "evaChute", 0.5),
            };
            GroundTakeSourceChoice far = TestCommandEvaGroundScience.ChooseTakeSource(cands, "DeployedRTG", 5.0);
            Assert.Equal(GroundTakeSourceDecision.OutOfRange, far.Decision);
            Assert.Equal(6.25, far.NearestDistanceMeters);

            // Reach is inclusive: exactly at the range is still a take.
            Assert.Equal(GroundTakeSourceDecision.Found,
                TestCommandEvaGroundScience.ChooseTakeSource(cands, "DeployedRTG", 6.25).Decision);

            GroundTakeSourceChoice none = TestCommandEvaGroundScience.ChooseTakeSource(cands, "DeployedGoExOb", 5.0);
            Assert.Equal(GroundTakeSourceDecision.NotStored, none.Decision);
            Assert.Equal(double.MaxValue, none.NearestDistanceMeters);

            // Ordinal part-name match, and the degenerate inputs.
            Assert.Equal(GroundTakeSourceDecision.NotStored,
                TestCommandEvaGroundScience.ChooseTakeSource(cands, "deployedrtg", 5.0).Decision);
            Assert.Equal(GroundTakeSourceDecision.NotStored,
                TestCommandEvaGroundScience.ChooseTakeSource(null, "DeployedRTG", 5.0).Decision);
            Assert.Equal(GroundTakeSourceDecision.NotStored,
                TestCommandEvaGroundScience.ChooseTakeSource(cands, null, 5.0).Decision);
        }

        [Fact]
        public void DecideTakeCompletion_NeedsThePartMovedBothWaysForTheSettleWindow()
        {
            int settle = TestCommandEvaGroundScience.SettleFrames;
            Assert.Equal(GroundScienceCompletionDecision.CompleteOk,
                TestCommandEvaGroundScience.DecideTakeCompletion(1, 120, true, true, settle));
            // A copy (kerbal holds it, the container still does) is not a take.
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaGroundScience.DecideTakeCompletion(1, 120, true, false, settle));
            // A loss (container emptied, kerbal does not hold it) is not a take either.
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaGroundScience.DecideTakeCompletion(1, 120, false, true, settle));
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaGroundScience.DecideTakeCompletion(1, 120, true, true, settle - 1));
            Assert.Equal(GroundScienceCompletionDecision.Timeout,
                TestCommandEvaGroundScience.DecideTakeCompletion(120, 120, true, false, 0));
        }

        [Fact]
        public void BuildCompletePayload_IsInvariantAndOrdered()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var p = TestCommandEvaGroundScience.BuildCompletePayload(
                    EvaGroundScienceAction.Place, "DeployedSeismicSensor", 4000000001u, 12345u, 1, 2, 1.256);
                Assert.Equal(new[] { "action", "part", "partPid", "vesselPid", "slot", "presses", "distance" },
                    p.ConvertAll(kv => kv.Key).ToArray());
                Assert.Equal("place", p[0].Value);
                Assert.Equal("4000000001", p[2].Value);
                Assert.Equal("1.26", p[6].Value);
                var q = TestCommandEvaGroundScience.BuildCompletePayload(
                    EvaGroundScienceAction.Pickup, null, 0, 0, -1, 0, 0);
                Assert.Equal("pickup", q[0].Value);
                Assert.Equal(string.Empty, q[1].Value);
                var t = TestCommandEvaGroundScience.BuildCompletePayload(
                    EvaGroundScienceAction.Take, "DeployedRTG", 7u, 2708531065u, 0, 0, 2.5);
                Assert.Equal("take", t[0].Value);
                Assert.Equal("2708531065", t[3].Value);
                Assert.Equal("2.50", t[6].Value);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void InventoryApplyOutcome_ReportsAppliedOnlyWhenTheGhostCarriesThePart()
        {
            Assert.Equal(GhostPartEventOutcome.Applied, GhostPlaybackLogic.InventoryApplyOutcome(true));
            Assert.Equal(GhostPartEventOutcome.NoInfoForPart, GhostPlaybackLogic.InventoryApplyOutcome(false));
            Assert.Equal("no-info-for-part",
                GhostPartEventApplyLog.OutcomeToken(GhostPlaybackLogic.InventoryApplyOutcome(false)));
        }
    }
}
