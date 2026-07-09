using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="InGameFixtureMath"/>, the pure fixture math the FLIGHT-scene
    /// in-game tests use to size themselves against whatever vessel the batch is flying.
    /// No Unity or KSP types, so it runs headlessly.
    /// </summary>
    public class InGameFixtureMathTests
    {
        // ─────────────────────────────────────────────────────────────
        //  SceneFloatGridToleranceMeters
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void SceneFloatGridTolerance_AtOrigin_ReturnsFloor()
        {
            Assert.Equal(
                InGameFixtureMath.MinSceneFloatToleranceMeters,
                InGameFixtureMath.SceneFloatGridToleranceMeters(0.0));
        }

        [Fact]
        public void SceneFloatGridTolerance_NearOrigin_StaysAtFloor()
        {
            // 1 km from the floating origin: grid step ~0.12 mm, eight of them is still
            // under the floor.
            Assert.Equal(
                InGameFixtureMath.MinSceneFloatToleranceMeters,
                InGameFixtureMath.SceneFloatGridToleranceMeters(1_000.0));
        }

        [Fact]
        public void SceneFloatGridTolerance_ScalesWithMagnitude()
        {
            // The regression that motivated this helper: a station orbiting at 214 km puts
            // its own ground track that far from the floating origin, where one float grid
            // step is ~2.5 cm — twenty-five times the 1 mm epsilon the test used to assert.
            double tolerance = InGameFixtureMath.SceneFloatGridToleranceMeters(214_000.0);

            Assert.True(tolerance > 0.001, $"expected the orbital-context tolerance to exceed the old 1 mm epsilon, got {tolerance}");
            Assert.Equal(
                214_000.0 * InGameFixtureMath.FloatMantissaStep * InGameFixtureMath.SceneFloatGridSteps,
                tolerance,
                precision: 9);
        }

        [Fact]
        public void SceneFloatGridTolerance_IsSignAgnostic()
        {
            Assert.Equal(
                InGameFixtureMath.SceneFloatGridToleranceMeters(214_000.0),
                InGameFixtureMath.SceneFloatGridToleranceMeters(-214_000.0));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void SceneFloatGridTolerance_NonFinite_DegradesToFloor(double magnitude)
        {
            Assert.Equal(
                InGameFixtureMath.MinSceneFloatToleranceMeters,
                InGameFixtureMath.SceneFloatGridToleranceMeters(magnitude));
        }

        // ─────────────────────────────────────────────────────────────
        //  ToleranceResolvesSignal
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void ToleranceResolvesSignal_LandedContext_Resolves()
        {
            // Ghost a few metres from the floating origin: 2 cm floor against a 1.5 m clamp
            // (1 m below terrain + the 0.5 m minimum clearance).
            double tolerance = InGameFixtureMath.SceneFloatGridToleranceMeters(20.0);

            Assert.True(InGameFixtureMath.ToleranceResolvesSignal(tolerance, 1.5));
        }

        [Fact]
        public void ToleranceResolvesSignal_OrbitalStationContext_Resolves()
        {
            // The 2026-07-08 failure's geometry: a station 214 km up, so its ground track sits
            // 214 km from the origin. Clearance saturates at 5 m there, giving a 6 m clamp
            // against a ~0.2 m grid tolerance.
            double tolerance = InGameFixtureMath.SceneFloatGridToleranceMeters(214_000.0);

            Assert.True(tolerance > 0.001, "the old 1 mm epsilon was below the float grid here — that is the bug");
            Assert.True(InGameFixtureMath.ToleranceResolvesSignal(tolerance, 6.0));
        }

        [Fact]
        public void ToleranceResolvesSignal_FarFromOrigin_DoesNotResolve()
        {
            // 5000 km out (an active vessel mid Mun transfer) the grid step is metres: the band
            // would straddle the buried altitude the clamp is meant to have left behind.
            double tolerance = InGameFixtureMath.SceneFloatGridToleranceMeters(5_000_000.0);

            Assert.True(tolerance > 6.0 * InGameFixtureMath.MaxToleranceFractionOfSignal);
            Assert.False(InGameFixtureMath.ToleranceResolvesSignal(tolerance, 6.0));
        }

        [Fact]
        public void ToleranceResolvesSignal_BoundaryIsInclusive()
        {
            Assert.True(InGameFixtureMath.ToleranceResolvesSignal(1.5, 6.0));   // exactly a quarter
            Assert.False(InGameFixtureMath.ToleranceResolvesSignal(1.5001, 6.0));
        }

        [Theory]
        [InlineData(0.02, 0.0)]
        [InlineData(0.02, -1.0)]
        [InlineData(0.02, double.NaN)]
        [InlineData(double.NaN, 6.0)]
        public void ToleranceResolvesSignal_DegenerateInputs_DoNotResolve(double tolerance, double signal)
        {
            Assert.False(InGameFixtureMath.ToleranceResolvesSignal(tolerance, signal));
        }

        // ─────────────────────────────────────────────────────────────
        //  Walkback clearance budget
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void ClearanceBudget_SumsReachExtentAndPadding()
        {
            Assert.Equal(46.25, InGameFixtureMath.ResolveWalkbackClearanceBudgetMeters(40.0, 1.25, 5.0), precision: 6);
        }

        [Theory]
        [InlineData(-5.0, 1.25, 5.0, 6.25)]
        [InlineData(40.0, -1.0, 5.0, 45.0)]
        [InlineData(double.NaN, 1.25, 5.0, 6.25)]
        public void ClearanceBudget_ClampsNegativeAndNaNInputsToZero(
            double reach, double extent, double padding, double expected)
        {
            Assert.Equal(expected, InGameFixtureMath.ResolveWalkbackClearanceBudgetMeters(reach, extent, padding), precision: 6);
        }

        // ─────────────────────────────────────────────────────────────
        //  Max walkback travel (measured from the recorded endpoint)
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void MaxWalkbackTravel_ExceedsBudgetByHalfTheRunway()
        {
            // A correct walkback stops at ~budget; the threshold sits half a runway above it.
            double budget = InGameFixtureMath.ResolveWalkbackClearanceBudgetMeters(30.0, 1.25, 5.0);
            double bound = InGameFixtureMath.ResolveMaxWalkbackTravelMeters(30.0, 1.25, 5.0);

            Assert.Equal(
                budget + InGameFixtureMath.WalkbackFixtureRunwayMeters * InGameFixtureMath.MaxWalkbackTravelRunwayFraction,
                bound,
                precision: 6);
            Assert.True(bound > budget, $"bound {bound} must exceed budget {budget}");
        }

        [Fact]
        public void MaxWalkbackTravel_Station_ScalesWithReach()
        {
            double rocket = InGameFixtureMath.ResolveMaxWalkbackTravelMeters(10.0, 1.25, 5.0);
            double station = InGameFixtureMath.ResolveMaxWalkbackTravelMeters(60.0, 1.25, 5.0);

            Assert.True(station > rocket, "a larger parent must widen the bound, not keep the pad rocket's");
        }

        [Theory]
        [InlineData(10.0)]    // pad rocket / lander
        [InlineData(50.0)]    // small base
        [InlineData(150.0)]   // large landed base
        [InlineData(400.0)]   // absurd but still inside the cap
        public void MaxWalkbackTravel_DiscriminatesOvershoot_ForEveryCoveredParent(double parentReach)
        {
            // The regression the reviewers caught: the old CoM-relative bound went vacuous for
            // parents past ~60 m of reach. The endpoint-relative bound must stay strictly below
            // the trajectory length for EVERY parent the fixture accepts, so an overshoot that
            // runs to the trajectory start is always caught.
            double budget = InGameFixtureMath.ResolveWalkbackClearanceBudgetMeters(parentReach, 1.25, 5.0);
            double length = InGameFixtureMath.ResolveWalkbackFixtureLengthMeters(parentReach, 1.25, 5.0);
            double bound = InGameFixtureMath.ResolveMaxWalkbackTravelMeters(parentReach, 1.25, 5.0);

            Assert.True(InGameFixtureMath.WalkbackFixtureCoversParent(length, budget),
                "sanity: these reaches must all be covered");
            // A correct walkback (~budget) passes; an overshoot to the start (~length) fails.
            Assert.True(bound > budget, $"correct walkback (~{budget}) must pass the bound {bound}");
            Assert.True(bound < length, $"overshoot to start (~{length}) must fail the bound {bound}");
            // With headroom on both sides so settle/snap/float noise cannot flip either verdict.
            Assert.True(bound - budget >= 20.0, "at least 20 m headroom above the correct-walkback distance");
            Assert.True(length - bound >= 20.0, "at least 20 m headroom below the overshoot-to-start distance");
        }

        // ─────────────────────────────────────────────────────────────
        //  Fixture length
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void FixtureLength_SmallParent_UsesTheMinimum()
        {
            // budget + runway = 16.25 + 80 = 96.25 < 120, so the minimum binds.
            Assert.Equal(
                InGameFixtureMath.WalkbackFixtureMinLengthMeters,
                InGameFixtureMath.ResolveWalkbackFixtureLengthMeters(10.0, 1.25, 5.0));
        }

        [Fact]
        public void FixtureLength_LargeParent_GrowsPastTheMinimum()
        {
            // budget + runway = 206.25 + 80 = 286.25.
            double length = InGameFixtureMath.ResolveWalkbackFixtureLengthMeters(200.0, 1.25, 5.0);

            Assert.Equal(286.25, length, precision: 6);
            Assert.True(length > InGameFixtureMath.WalkbackFixtureMinLengthMeters);
        }

        [Fact]
        public void FixtureLength_HugeParent_ClampsToTheMaximum()
        {
            Assert.Equal(
                InGameFixtureMath.WalkbackFixtureMaxLengthMeters,
                InGameFixtureMath.ResolveWalkbackFixtureLengthMeters(5_000.0, 1.25, 5.0));
        }

        // ─────────────────────────────────────────────────────────────
        //  Coverage predicate
        // ─────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(10.0)]    // pad rocket
        [InlineData(60.0)]    // assembled station
        [InlineData(400.0)]   // absurd but still inside the cap
        [InlineData(900.0)]   // near the cap, full runway still fits
        public void FixtureCoversParent_HoldsForEveryParentInsideTheCap(double parentReach)
        {
            double budget = InGameFixtureMath.ResolveWalkbackClearanceBudgetMeters(parentReach, 1.25, 5.0);
            double length = InGameFixtureMath.ResolveWalkbackFixtureLengthMeters(parentReach, 1.25, 5.0);

            Assert.True(InGameFixtureMath.WalkbackFixtureCoversParent(length, budget));
        }

        [Fact]
        public void FixtureCoversParent_FailsWhenTheRunwayDoesNotFit()
        {
            // Beyond the trajectory cap the full runway no longer fits, so the max-travel
            // threshold could not sit strictly inside the trajectory. The test must skip
            // rather than report a bug it caused or assert an unsupportable bound.
            const double parentReach = 5_000.0;
            double budget = InGameFixtureMath.ResolveWalkbackClearanceBudgetMeters(parentReach, 1.25, 5.0);
            double length = InGameFixtureMath.ResolveWalkbackFixtureLengthMeters(parentReach, 1.25, 5.0);

            Assert.False(InGameFixtureMath.WalkbackFixtureCoversParent(length, budget));
        }

        [Fact]
        public void FixtureCoversParent_BoundaryIsExactlyBudgetPlusRunway()
        {
            double budget = 100.0;
            Assert.True(InGameFixtureMath.WalkbackFixtureCoversParent(
                budget + InGameFixtureMath.WalkbackFixtureRunwayMeters, budget));
            Assert.False(InGameFixtureMath.WalkbackFixtureCoversParent(
                budget + InGameFixtureMath.WalkbackFixtureRunwayMeters - 0.01, budget));
        }

        // ─────────────────────────────────────────────────────────────
        //  Point count
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void PointCount_MinimumLength_MatchesTheStepSpacing()
        {
            // 120 m at 5 m spacing = 24 steps = 25 points.
            Assert.Equal(25, InGameFixtureMath.ResolveWalkbackFixturePointCount(
                InGameFixtureMath.WalkbackFixtureMinLengthMeters, InGameFixtureMath.WalkbackFixtureStepMeters));
        }

        [Fact]
        public void PointCount_RoundsPartialStepsUp()
        {
            // 26 m at 5 m spacing needs 6 steps (the last one partial), so 7 points.
            Assert.Equal(7, InGameFixtureMath.ResolveWalkbackFixturePointCount(26.0, 5.0));
            Assert.Equal(6, InGameFixtureMath.ResolveWalkbackFixturePointCount(25.0, 5.0));
        }

        [Theory]
        [InlineData(0.0, 5.0)]
        [InlineData(-100.0, 5.0)]
        [InlineData(120.0, 0.0)]
        [InlineData(120.0, -1.0)]
        [InlineData(double.NaN, 5.0)]
        public void PointCount_DegenerateInputs_StillYieldASegment(double length, double step)
        {
            // Two points is the floor: the walkback needs one segment to subdivide.
            Assert.Equal(2, InGameFixtureMath.ResolveWalkbackFixturePointCount(length, step));
        }

        [Fact]
        public void PointCount_StepIsCoarserThanTheWalkbackSubStep()
        {
            // 5 m spacing against SpawnCollisionDetector's 1.5 m walkback sub-step means each
            // segment subdivides several times, so the metric-subdivision path is exercised
            // instead of degenerating to point granularity.
            Assert.True(InGameFixtureMath.WalkbackFixtureStepMeters
                > SpawnCollisionDetector.DefaultWalkbackStepMeters * 2.0);
        }
    }
}
