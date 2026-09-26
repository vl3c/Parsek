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
        public void TryParseAction_AcceptsExactlyTheTwoTokens()
        {
            Assert.True(TestCommandEvaGroundScience.TryParseAction("place", out var a));
            Assert.Equal(EvaGroundScienceAction.Place, a);
            Assert.True(TestCommandEvaGroundScience.TryParseAction("pickup", out a));
            Assert.Equal(EvaGroundScienceAction.Pickup, a);
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
