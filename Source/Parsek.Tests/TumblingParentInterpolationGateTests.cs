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
        public void EvaluateParentRotationInterpolation_MarksDecisionEvaluated()
        {
            TrajectoryPoint before = Point(100.0, Quaternion.identity);
            TrajectoryPoint after = Point(100.1, TrajectoryMath.PureAngleAxis(24f, Vector3.up));

            AnchorRotationReliabilityDecision evaluatedReliable =
                TumblingParentInterpolationGate.EvaluateParentRotationInterpolation(
                    "parent",
                    before,
                    after,
                    debrisLocalOffsetSquaredMeters: 10.0 * 10.0);
            Assert.True(evaluatedReliable.Evaluated);
            Assert.False(evaluatedReliable.Unreliable);

            AnchorRotationReliabilityDecision evaluatedUnreliable =
                TumblingParentInterpolationGate.EvaluateParentRotationInterpolation(
                    "parent",
                    before,
                    after,
                    debrisLocalOffsetSquaredMeters: 1500.0 * 1500.0);
            Assert.True(evaluatedUnreliable.Evaluated);
            Assert.True(evaluatedUnreliable.Unreliable);

            AnchorRotationReliabilityDecision defaultDecision = default;
            Assert.False(defaultDecision.Evaluated);
            Assert.False(defaultDecision.Unreliable);
        }

        [Fact]
        public void UpdateHysteresis_UnevaluatedDecision_PreservesHeldState()
        {
            // Reproduces the run-2 false-release pattern: gate engaged on a
            // chaotic bracket, then a sample-boundary frame where the resolver
            // skips the gate (t at 0 or 1) and propagates default(decision).
            // The hysteresis must NOT exit on default's offset=0 / rate=0.
            var state = new AnchorRotationHysteresisState();
            var enter = new AnchorRotationReliabilityDecision(
                unreliable: true,
                anchorRecordingId: "parent",
                bracketDegrees: 44.72,
                rateDegreesPerSecond: 203.3,
                offsetMeters: 1506.4);

            Assert.True(TumblingParentInterpolationGate.UpdateHysteresis(enter, ref state));
            Assert.True(state.Held);
            Assert.Equal(1, state.HeldFrames);

            // Sample-boundary frame: resolver skipped gate, decision is default.
            AnchorRotationReliabilityDecision unevaluated = default;
            Assert.False(unevaluated.Evaluated);

            for (int i = 0; i < 10; i++)
            {
                Assert.True(
                    TumblingParentInterpolationGate.UpdateHysteresis(unevaluated, ref state),
                    $"unevaluated frame #{i} unexpectedly released the hold");
                Assert.True(state.Held);
            }

            // HeldFrames continues to advance through unevaluated frames so
            // post-release diagnostics report the full hold duration.
            Assert.Equal(11, state.HeldFrames);
        }

        [Fact]
        public void UpdateHysteresis_UnevaluatedDecision_DoesNotEnterFromIdle()
        {
            var state = new AnchorRotationHysteresisState();
            AnchorRotationReliabilityDecision unevaluated = default;

            Assert.False(TumblingParentInterpolationGate.UpdateHysteresis(unevaluated, ref state));
            Assert.False(state.Held);
            Assert.Equal(0, state.HeldFrames);
        }

        [Fact]
        public void UpdateHysteresis_EvaluatedExitDecision_StillReleasesAfterHold()
        {
            // The unevaluated-decision guard must not block a legitimate exit.
            var state = new AnchorRotationHysteresisState();
            var enter = new AnchorRotationReliabilityDecision(
                unreliable: true,
                anchorRecordingId: "parent",
                bracketDegrees: 44.72,
                rateDegreesPerSecond: 203.3,
                offsetMeters: 1506.4);
            Assert.True(TumblingParentInterpolationGate.UpdateHysteresis(enter, ref state));

            var exit = new AnchorRotationReliabilityDecision(
                unreliable: false,
                anchorRecordingId: "parent",
                bracketDegrees: 3.87,
                rateDegreesPerSecond: 17.6,
                offsetMeters: 1500.0);
            Assert.True(exit.Evaluated);
            Assert.False(TumblingParentInterpolationGate.UpdateHysteresis(exit, ref state));
            Assert.False(state.Held);
            Assert.Equal(0, state.HeldFrames);
        }

        [Fact]
        public void Decision_Combine_PrefersUnreliableThenEvaluated()
        {
            var earlierEvaluatedReliable = new AnchorRotationReliabilityDecision(
                unreliable: false, anchorRecordingId: "p", bracketDegrees: 2.0,
                rateDegreesPerSecond: 30.0, offsetMeters: 1500.0);
            var laterUnevaluated = default(AnchorRotationReliabilityDecision);
            var laterEvaluatedReliable = new AnchorRotationReliabilityDecision(
                unreliable: false, anchorRecordingId: "g", bracketDegrees: 1.0,
                rateDegreesPerSecond: 10.0, offsetMeters: 1500.0);
            var laterUnreliable = new AnchorRotationReliabilityDecision(
                unreliable: true, anchorRecordingId: "g", bracketDegrees: 50.0,
                rateDegreesPerSecond: 250.0, offsetMeters: 1500.0);

            // Earlier evaluated reliable + later unevaluated -> earlier wins.
            var combinedSkippedParent = AnchorRotationReliabilityDecision.Combine(
                earlierEvaluatedReliable, laterUnevaluated);
            Assert.True(combinedSkippedParent.Evaluated);
            Assert.False(combinedSkippedParent.Unreliable);
            Assert.Equal("p", combinedSkippedParent.AnchorRecordingId);

            // Later evaluated reliable beats earlier evaluated reliable.
            var combinedBothEvaluated = AnchorRotationReliabilityDecision.Combine(
                earlierEvaluatedReliable, laterEvaluatedReliable);
            Assert.Equal("g", combinedBothEvaluated.AnchorRecordingId);

            // Either side unreliable wins.
            var combinedLaterUnreliable = AnchorRotationReliabilityDecision.Combine(
                earlierEvaluatedReliable, laterUnreliable);
            Assert.True(combinedLaterUnreliable.Unreliable);
            var combinedEarlierUnreliable = AnchorRotationReliabilityDecision.Combine(
                laterUnreliable, earlierEvaluatedReliable);
            Assert.True(combinedEarlierUnreliable.Unreliable);
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
