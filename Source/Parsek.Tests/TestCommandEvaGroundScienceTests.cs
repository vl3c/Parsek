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
        public void TryParseAction_AcceptsExactlyTheFourTokens()
        {
            Assert.True(TestCommandEvaGroundScience.TryParseAction("place", out var a));
            Assert.Equal(EvaGroundScienceAction.Place, a);
            Assert.True(TestCommandEvaGroundScience.TryParseAction("pickup", out a));
            Assert.Equal(EvaGroundScienceAction.Pickup, a);
            Assert.True(TestCommandEvaGroundScience.TryParseAction("take", out a));
            Assert.Equal(EvaGroundScienceAction.Take, a);
            Assert.False(TestCommandEvaGroundScience.TryParseAction("Take", out _));
            Assert.True(TestCommandEvaGroundScience.TryParseAction("step", out a));
            Assert.Equal(EvaGroundScienceAction.Step, a);
            Assert.False(TestCommandEvaGroundScience.TryParseAction("walk", out _));
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
                { EvaGroundScienceAction.Place, EvaGroundScienceAction.Pickup, EvaGroundScienceAction.Take,
                  EvaGroundScienceAction.Step })
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
        public void TryParseStepArgs_AcceptsAPidAndABoundedInvariantDistance()
        {
            Assert.True(TestCommandEvaGroundScience.TryParseStepArgs("2708531065", "4.5",
                out uint pid, out double dist, out string err));
            Assert.Equal(2708531065u, pid);
            Assert.Equal(4.5, dist);
            Assert.Null(err);
            Assert.True(TestCommandEvaGroundScience.TryParseStepArgs("1", "30", out _, out dist, out _));
            Assert.Equal(TestCommandEvaGroundScience.MaxStepDistanceMeters, dist);

            foreach (string badAnchor in new[] { null, "", "0", "-5", "abc", "2708531065.0", "99999999999" })
            {
                Assert.False(TestCommandEvaGroundScience.TryParseStepArgs(badAnchor, "4", out _, out _, out err));
                Assert.Equal("step-anchor-invalid", err);
            }
            foreach (string badDistance in new[] { null, "", "0", "-1", "30.01", "4,5", "NaN", "1e2" })
            {
                Assert.False(TestCommandEvaGroundScience.TryParseStepArgs("7", badDistance, out _, out dist, out err));
                Assert.Equal("step-distance-invalid", err);
                Assert.Equal(0.0, dist);
            }
        }

        [Fact]
        public void TryParseStepBearing_OptionalUnsignedInvariantDegreesBelow360()
        {
            foreach (string absent in new[] { null, "" })
            {
                Assert.True(TestCommandEvaGroundScience.TryParseStepBearing(absent,
                    out bool has, out double b, out string err));
                Assert.False(has);
                Assert.Equal(0.0, b);
                Assert.Null(err);
            }
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                // A comma-decimal host culture must not change what a spec's "22.5" means.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.True(TestCommandEvaGroundScience.TryParseStepBearing("22.5",
                    out bool has, out double b, out string err));
                Assert.True(has);
                Assert.Equal(22.5, b);
                Assert.Null(err);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
            Assert.True(TestCommandEvaGroundScience.TryParseStepBearing("0", out bool zeroHas, out double zero, out _));
            Assert.True(zeroHas);
            Assert.Equal(0.0, zero);
            Assert.True(TestCommandEvaGroundScience.TryParseStepBearing("359.9", out _, out double top, out _));
            Assert.Equal(359.9, top);

            foreach (string bad in new[] { "360", "-10", "abc", "22,5", "NaN", "1e2", "400" })
            {
                Assert.False(TestCommandEvaGroundScience.TryParseStepBearing(bad,
                    out bool has, out double b, out string err));
                Assert.False(has);
                Assert.Equal(0.0, b);
                Assert.Equal("step-bearing-invalid", err);
            }
        }

        [Fact]
        public void OffsetLatLonAlongBearing_MovesTheCompassDirectionAtTheRequestedDistance()
        {
            const double r = 600000.0;
            double metresPerDegree = r * System.Math.PI / 180.0;

            // Due north / south: latitude only.
            TestCommandEvaGroundScience.OffsetLatLonAlongBearing(0.0, 10.0, 0.0, 13.0, r,
                out double lat, out double lon);
            Assert.Equal(13.0 / metresPerDegree, lat, 12);
            Assert.Equal(10.0, lon, 12);
            TestCommandEvaGroundScience.OffsetLatLonAlongBearing(0.0, 10.0, 180.0, 13.0, r,
                out lat, out lon);
            Assert.Equal(-13.0 / metresPerDegree, lat, 12);
            Assert.Equal(10.0, lon, 9);

            // Due east at latitude 60: the longitude step is 1 / cos(60) = 2x wider.
            TestCommandEvaGroundScience.OffsetLatLonAlongBearing(60.0, 10.0, 90.0, 13.0, r,
                out lat, out lon);
            Assert.Equal(60.0, lat, 9);
            Assert.Equal(10.0 + 2.0 * 13.0 / metresPerDegree, lon, 9);

            // Any bearing: the tangent-plane distance back from the offset is the request.
            foreach (double bearing in new[] { 60.0, 80.0, 100.0, 120.0, 270.0 })
            {
                TestCommandEvaGroundScience.OffsetLatLonAlongBearing(-0.12, 86.7, bearing, 13.0, r,
                    out lat, out lon);
                double dn = (lat + 0.12) * metresPerDegree;
                double de = (lon - 86.7) * metresPerDegree * System.Math.Cos(-0.12 * System.Math.PI / 180.0);
                Assert.Equal(13.0, System.Math.Sqrt(dn * dn + de * de), 6);
                double back = (System.Math.Atan2(de, dn) * 180.0 / System.Math.PI + 360.0) % 360.0;
                Assert.Equal(bearing, back, 6);
            }

            // Two spots 20 degrees apart on a 13 m ring stand 2 * 13 * sin(10 deg) = 4.51 m apart
            // (the EVA-9 layout, which must stay inside the 6.25 m spawn-overlap half extent).
            TestCommandEvaGroundScience.OffsetLatLonAlongBearing(0.0, 0.0, 60.0, 13.0, r,
                out double lat1, out double lon1);
            TestCommandEvaGroundScience.OffsetLatLonAlongBearing(0.0, 0.0, 80.0, 13.0, r,
                out double lat2, out double lon2);
            double gap = System.Math.Sqrt(
                System.Math.Pow((lat2 - lat1) * metresPerDegree, 2)
                + System.Math.Pow((lon2 - lon1) * metresPerDegree, 2));
            Assert.Equal(2 * 13.0 * System.Math.Sin(10.0 * System.Math.PI / 180.0), gap, 6);

            // Longitude wraps across the antimeridian; a polar anchor does not divide by zero.
            TestCommandEvaGroundScience.OffsetLatLonAlongBearing(0.0, 179.99999, 90.0, 13.0, r,
                out _, out lon);
            Assert.True(lon < -179.0 && lon >= -180.0, "lon=" + lon);
            TestCommandEvaGroundScience.OffsetLatLonAlongBearing(90.0, 0.0, 90.0, 13.0, r,
                out lat, out lon);
            Assert.False(double.IsNaN(lon) || double.IsInfinity(lon));
            Assert.Equal(90.0, lat, 9);
        }

        [Fact]
        public void StepHorizontalOffset_KeepsTheKerbalsBearingAndRescalesIt()
        {
            TestCommandEvaGroundScience.StepHorizontalOffset(0.6, 0.8, 5.0, 1, 0, out double ox, out double oz);
            Assert.Equal(3.0, ox, 9);
            Assert.Equal(4.0, oz, 9);
            // A kerbal already farther out comes IN to the requested distance.
            TestCommandEvaGroundScience.StepHorizontalOffset(-12.0, 0.0, 4.0, 1, 0, out ox, out oz);
            Assert.Equal(-4.0, ox, 9);
            Assert.Equal(0.0, oz, 9);
            // Straight above the anchor: the fallback bearing, normalized.
            TestCommandEvaGroundScience.StepHorizontalOffset(0.0, 0.0, 2.0, 0.0, -3.0, out ox, out oz);
            Assert.Equal(0.0, ox, 9);
            Assert.Equal(-2.0, oz, 9);
            // Both degenerate: +x.
            TestCommandEvaGroundScience.StepHorizontalOffset(0.0, 0.0, 2.0, 0.0, 0.0, out ox, out oz);
            Assert.Equal(2.0, ox, 9);
            Assert.Equal(0.0, oz, 9);
        }

        [Fact]
        public void DecideStepCompletion_NeedsLandedOnTheTargetSpotForTheSettleWindow()
        {
            int settle = TestCommandEvaGroundScience.SettleFrames;
            double tol = TestCommandEvaGroundScience.StepToleranceMeters;
            Assert.Equal(GroundScienceCompletionDecision.CompleteOk,
                TestCommandEvaGroundScience.DecideStepCompletion(2, 120, true, tol, settle));
            Assert.Equal(GroundScienceCompletionDecision.CompleteOk,
                TestCommandEvaGroundScience.DecideStepCompletion(2, 120, true, 0.0, settle));
            // Off the spot by more than the tolerance: EVA-8 `2026-09-29_1618`, where the
            // collision guard put Jeb back 3.2 m short of a 13 m step.
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaGroundScience.DecideStepCompletion(2, 120, true, tol + 0.01, settle));
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaGroundScience.DecideStepCompletion(2, 120, true, double.NaN, settle));
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaGroundScience.DecideStepCompletion(2, 120, false, 0.0, settle));
            Assert.Equal(GroundScienceCompletionDecision.StillWaiting,
                TestCommandEvaGroundScience.DecideStepCompletion(2, 120, true, 0.0, settle - 1));
            Assert.Equal(GroundScienceCompletionDecision.Timeout,
                TestCommandEvaGroundScience.DecideStepCompletion(120, 120, true, 3.2, settle));
        }

        [Fact]
        public void FormatStepMoveLine_KeepsThePinnedPrefixAndNamesTheMethod()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = TestCommandEvaGroundScience.FormatStepMoveLine(
                    "Jebediah Kerman", 2, 1, true, true, true, 12);
                Assert.Equal(
                    "evagroundscience step move kerbal=Jebediah Kerman move=2 collisionEnhancersSkipped=1 "
                    + "skipFrames=" + TestCommandEvaGroundScience.StepCollisionSkipFrames
                    + " method=anchor-release+rb-pose evaModule=true wasAnchored=true anchorReleased=true "
                    + "rigidbodiesPosed=12 terrain=? setDownAlt=? groundAlt=? feetDepth=? groundSource=? "
                    + "crashGuard=false",
                    line);
                Assert.EndsWith(" terrain=150.42 setDownAlt=151.52 groundAlt=150.50 feetDepth=0.97 "
                    + "groundSource=raycast crashGuard=true",
                    TestCommandEvaGroundScience.FormatStepMoveLine(
                        "Jebediah Kerman", 1, 1, true, false, true, 1, 150.42, 151.52, 150.50, 0.97,
                        "raycast", true));
                // The lanes' pinned token still matches (EVA-8 / EVA-9 / EVA-10 require it on move 1).
                string first = TestCommandEvaGroundScience.FormatStepMoveLine(
                    "Jebediah Kerman", 1, 1, true, false, false, 1);
                Assert.Matches(
                    "evagroundscience step move kerbal=Jebediah Kerman move=1 collisionEnhancersSkipped=[1-9][0-9]* skipFrames=",
                    first);
                Assert.Matches(" method=anchor-release\\+rb-pose ", first);
                Assert.Contains("wasAnchored=false anchorReleased=false", first);
                Assert.Contains("evaModule=false",
                    TestCommandEvaGroundScience.FormatStepMoveLine(null, 1, 0, false, false, false, 0));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void ShouldRecoverFromRagdoll_OnlyALyingSettledKerbalBounded()
        {
            double wait = TestCommandEvaGroundScience.RagdollRecoverAfterSeconds;
            double slow = TestCommandEvaGroundScience.RagdollRecoverMaxSpeed - 0.01;
            int max = TestCommandEvaGroundScience.MaxRagdollRecovers;
            // EVA-8 `2026-09-29_1759`: landed, still, ragdolled for good.
            Assert.True(TestCommandEvaGroundScience.ShouldRecoverFromRagdoll(true, true, true, 0.02, 120.0, 0));
            Assert.True(TestCommandEvaGroundScience.ShouldRecoverFromRagdoll(true, true, true, slow, wait, max - 1));
            // Not ragdolled, or already out of the ragdoll state (recovering).
            Assert.False(TestCommandEvaGroundScience.ShouldRecoverFromRagdoll(false, false, true, 0.0, 10.0, 0));
            Assert.False(TestCommandEvaGroundScience.ShouldRecoverFromRagdoll(true, false, true, 0.0, 10.0, 0));
            // Still tumbling or airborne, or not lying there long enough yet.
            Assert.False(TestCommandEvaGroundScience.ShouldRecoverFromRagdoll(true, true, false, 0.0, 10.0, 0));
            Assert.False(TestCommandEvaGroundScience.ShouldRecoverFromRagdoll(
                true, true, true, TestCommandEvaGroundScience.RagdollRecoverMaxSpeed, 10.0, 0));
            Assert.False(TestCommandEvaGroundScience.ShouldRecoverFromRagdoll(true, true, true, double.NaN, 10.0, 0));
            Assert.False(TestCommandEvaGroundScience.ShouldRecoverFromRagdoll(true, true, true, 0.0, wait - 0.01, 0));
            // Bounded.
            Assert.False(TestCommandEvaGroundScience.ShouldRecoverFromRagdoll(true, true, true, 0.0, 10.0, max));
        }

        [Fact]
        public void StepSetDownAltitude_PutsTheFeetOnTheGroundColliderAboveTheCrashGuard()
        {
            double margin = TestCommandEvaGroundScience.StepSetDownMarginMeters;
            double guard = TestCommandEvaGroundScience.StepCrashGuardMarginMeters;
            double lift = TestCommandEvaGroundScience.StepLiftMeters;

            // The ground collider, not the PQS height: EVA-9 `_1931`'s last step (PQS 151.36)
            // with the collider higher than PQS lands the feet ON it, not 0.5 m inside it.
            double alt = TestCommandEvaGroundScience.StepSetDownAltitude(
                151.90, 151.36, 0.95, 151.30, out string src, out bool guarded);
            Assert.Equal(151.90 + 0.95 + margin, alt, 9);
            Assert.Equal(TestCommandEvaGroundScience.GroundSourceRaycast, src);
            Assert.False(guarded);

            // Downhill (EVA-8 `_1729`: PQS 150.42, the spot left cached 151.02): no drop from the
            // old spot's height any more, only the crash guard's floor when the set-down would
            // read below the cached terrain.
            alt = TestCommandEvaGroundScience.StepSetDownAltitude(
                150.45, 150.42, 0.40, 151.02, out src, out guarded);
            Assert.Equal(151.02 + guard, alt, 9);
            Assert.True(guarded);
            alt = TestCommandEvaGroundScience.StepSetDownAltitude(
                150.45, 150.42, 0.95, 151.02, out src, out guarded);
            Assert.Equal(150.45 + 0.95 + margin, alt, 9);
            Assert.False(guarded);

            // The PQS height itself is a crash-guard floor.
            alt = TestCommandEvaGroundScience.StepSetDownAltitude(
                149.00, 150.42, 0.30, -1.0, out src, out guarded);
            Assert.Equal(150.42 + guard, alt, 9);
            Assert.True(guarded);

            // No ground hit: the PQS height plus the depth.
            alt = TestCommandEvaGroundScience.StepSetDownAltitude(
                double.NaN, 150.42, 0.95, double.NaN, out src, out guarded);
            Assert.Equal(150.42 + 0.95 + margin, alt, 9);
            Assert.Equal(TestCommandEvaGroundScience.GroundSourcePqs, src);

            // An unbelievable feet depth falls back to the plain lift.
            foreach (double badDepth in new[] { double.NaN, -0.2, TestCommandEvaGroundScience.MaxFeetDepthMeters + 0.1 })
            {
                alt = TestCommandEvaGroundScience.StepSetDownAltitude(
                    151.90, 151.36, badDepth, -1.0, out src, out guarded);
                Assert.Equal(151.90 + lift, alt, 9);
            }

            // Unknown cached terrain values never act as a guard.
            foreach (double cached in new[] { -1.0, double.NaN, double.PositiveInfinity })
            {
                alt = TestCommandEvaGroundScience.StepSetDownAltitude(
                    151.90, 151.36, 0.95, cached, out src, out guarded);
                Assert.Equal(151.90 + 0.95 + margin, alt, 9);
                Assert.False(guarded);
            }
        }

        [Fact]
        public void GroundClearance_OkOnlyWithinTheToleranceAndLoggedInvariant()
        {
            double tol = TestCommandEvaGroundScience.StepGroundClearanceToleranceMeters;
            Assert.True(TestCommandEvaGroundScience.IsGroundClearanceOk(0.02));
            Assert.True(TestCommandEvaGroundScience.IsGroundClearanceOk(-tol));
            Assert.True(TestCommandEvaGroundScience.IsGroundClearanceOk(tol));
            // Half in the ground (the operator's EVA-9 observation) or still falling.
            Assert.False(TestCommandEvaGroundScience.IsGroundClearanceOk(-0.5));
            Assert.False(TestCommandEvaGroundScience.IsGroundClearanceOk(0.8));
            Assert.False(TestCommandEvaGroundScience.IsGroundClearanceOk(double.NaN));

            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal(
                    "evagroundscience step ground kerbal=Jebediah Kerman feetClearance=0.03 originClearance=0.98 "
                    + "tolerance=0.25 groundSource=raycast ok=true",
                    TestCommandEvaGroundScience.FormatStepGroundLine("Jebediah Kerman", 0.03, 0.98, "raycast", true));
                Assert.Equal(
                    "evagroundscience step ground kerbal=Jebediah Kerman feetClearance=nan originClearance=nan "
                    + "tolerance=0.25 groundSource=? ok=false",
                    TestCommandEvaGroundScience.FormatStepGroundLine("Jebediah Kerman", double.NaN, double.NaN, null, false));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }

            // A landed kerbal with his feet off the ground collider is moved again.
            int wait = TestCommandEvaGroundScience.StepReapplyFrames;
            Assert.True(TestCommandEvaGroundScience.ShouldReapplyStep(0.1, true, wait, 1));
            Assert.False(TestCommandEvaGroundScience.ShouldReapplyStep(0.1, false, wait, 1));
            Assert.False(TestCommandEvaGroundScience.ShouldReapplyStep(0.1, true, wait - 1, 1));
            Assert.False(TestCommandEvaGroundScience.ShouldReapplyStep(0.1, true, wait,
                TestCommandEvaGroundScience.MaxStepMoves));
        }

        [Fact]
        public void ShouldReapplyStep_OnlyOffTargetAfterTheMoveSettledAndBounded()
        {
            double tol = TestCommandEvaGroundScience.StepToleranceMeters;
            int wait = TestCommandEvaGroundScience.StepReapplyFrames;
            int max = TestCommandEvaGroundScience.MaxStepMoves;
            // On target: never again.
            Assert.False(TestCommandEvaGroundScience.ShouldReapplyStep(tol, wait, 1));
            Assert.False(TestCommandEvaGroundScience.ShouldReapplyStep(0.2, 1000, 1));
            // Off target, but the move has not had its frames yet.
            Assert.False(TestCommandEvaGroundScience.ShouldReapplyStep(3.2, wait - 1, 1));
            // Off target (or unmeasurable) after the wait: move again.
            Assert.True(TestCommandEvaGroundScience.ShouldReapplyStep(3.2, wait, 1));
            Assert.True(TestCommandEvaGroundScience.ShouldReapplyStep(double.NaN, wait, 1));
            Assert.True(TestCommandEvaGroundScience.ShouldReapplyStep(3.2, wait, max - 1));
            // The move budget is spent: wait out the step's own budget instead.
            Assert.False(TestCommandEvaGroundScience.ShouldReapplyStep(3.2, wait, max));
            Assert.False(TestCommandEvaGroundScience.ShouldReapplyStep(3.2, int.MaxValue, max + 1));
            Assert.True(max >= 2, "one retry at least");
            Assert.True(TestCommandEvaGroundScience.StepCollisionSkipFrames >= 1);
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
