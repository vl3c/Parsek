using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class TumblingParentInterpolationGateTests
    {
        [Fact]
        public void EvaluateParentRotationInterpolation_EntersOnlyWhenAngleRateAndOffsetAreHigh()
        {
            TrajectoryPoint before = Point(100.0, Quaternion.identity);
            TrajectoryPoint after = Point(100.1, TrajectoryMath.PureAngleAxis(24f, Vector3.up));

            AnchorRotationReliabilityDecision decision =
                TumblingParentInterpolationGate.EvaluateParentRotationInterpolation(
                    "parent",
                    before,
                    after,
                    debrisLocalOffsetSquaredMeters: 1500.0 * 1500.0);

            Assert.True(decision.Unreliable);
            Assert.Equal("parent", decision.AnchorRecordingId);
            Assert.Equal(24.0, decision.BracketDegrees, precision: 1);
            Assert.Equal(240.0, decision.RateDegreesPerSecond, precision: 1);
            Assert.Equal(1500.0, decision.OffsetMeters, precision: 1);

            Assert.False(TumblingParentInterpolationGate.EvaluateParentRotationInterpolation(
                "parent",
                before,
                after,
                debrisLocalOffsetSquaredMeters: 10.0 * 10.0).Unreliable);

            Assert.False(TumblingParentInterpolationGate.EvaluateParentRotationInterpolation(
                "parent",
                before,
                Point(100.1, TrajectoryMath.PureAngleAxis(3f, Vector3.up)),
                debrisLocalOffsetSquaredMeters: 1500.0 * 1500.0).Unreliable);
        }

        [Fact]
        public void UpdateHysteresis_HoldsUntilExitThresholdsAreMet()
        {
            var state = new AnchorRotationHysteresisState();
            var enter = new AnchorRotationReliabilityDecision(
                true,
                "parent",
                bracketDegrees: 12.0,
                rateDegreesPerSecond: 240.0,
                offsetMeters: 100.0);

            Assert.True(TumblingParentInterpolationGate.UpdateHysteresis(enter, ref state));
            Assert.True(state.Held);
            Assert.Equal(1, state.HeldFrames);

            var betweenEnterAndExit = new AnchorRotationReliabilityDecision(
                false,
                "parent",
                bracketDegrees: 6.0,
                rateDegreesPerSecond: 100.0,
                offsetMeters: 100.0);
            Assert.True(TumblingParentInterpolationGate.UpdateHysteresis(
                betweenEnterAndExit,
                ref state));
            Assert.Equal(2, state.HeldFrames);

            var exit = new AnchorRotationReliabilityDecision(
                false,
                "parent",
                bracketDegrees: 3.0,
                rateDegreesPerSecond: 60.0,
                offsetMeters: 100.0);
            Assert.False(TumblingParentInterpolationGate.UpdateHysteresis(exit, ref state));
            Assert.False(state.Held);
            Assert.Equal(0, state.HeldFrames);
        }

        [Fact]
        public void AnchorRotationHysteresisKey_UsesOrdinalValueEquality()
        {
            var left = new AnchorRotationHysteresisKey("child", "anchor");
            var same = new AnchorRotationHysteresisKey("child", "anchor");
            var differentCase = new AnchorRotationHysteresisKey("Child", "anchor");
            var differentScope = new AnchorRotationHysteresisKey("child", "anchor", "cycle=1");

            Assert.Equal(left, same);
            Assert.Equal(left.GetHashCode(), same.GetHashCode());
            Assert.NotEqual(left, differentCase);
            Assert.NotEqual(left, differentScope);
        }

        private static TrajectoryPoint Point(double ut, Quaternion rotation)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                rotation = rotation,
            };
        }
    }
}
