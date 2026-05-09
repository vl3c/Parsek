using System;
using UnityEngine;

namespace Parsek
{
    internal readonly struct AnchorRotationReliabilityDecision
    {
        public readonly bool Unreliable;
        public readonly string AnchorRecordingId;
        public readonly double BracketDegrees;
        public readonly double RateDegreesPerSecond;
        public readonly double OffsetMeters;

        public AnchorRotationReliabilityDecision(
            bool unreliable,
            string anchorRecordingId,
            double bracketDegrees,
            double rateDegreesPerSecond,
            double offsetMeters)
        {
            Unreliable = unreliable;
            AnchorRecordingId = anchorRecordingId;
            BracketDegrees = bracketDegrees;
            RateDegreesPerSecond = rateDegreesPerSecond;
            OffsetMeters = offsetMeters;
        }

        public AnchorRotationReliabilityDecision WithUnreliable(bool unreliable)
        {
            return new AnchorRotationReliabilityDecision(
                unreliable,
                AnchorRecordingId,
                BracketDegrees,
                RateDegreesPerSecond,
                OffsetMeters);
        }
    }

    internal readonly struct AnchorRotationHysteresisKey : IEquatable<AnchorRotationHysteresisKey>
    {
        public readonly string ChildRecordingId;
        public readonly string AnchorRecordingId;
        public readonly string PlaybackScope;

        public AnchorRotationHysteresisKey(
            string childRecordingId,
            string anchorRecordingId,
            string playbackScope = null)
        {
            ChildRecordingId = childRecordingId ?? string.Empty;
            AnchorRecordingId = anchorRecordingId ?? string.Empty;
            PlaybackScope = playbackScope ?? string.Empty;
        }

        public bool Equals(AnchorRotationHysteresisKey other)
        {
            return string.Equals(ChildRecordingId, other.ChildRecordingId, StringComparison.Ordinal)
                && string.Equals(AnchorRecordingId, other.AnchorRecordingId, StringComparison.Ordinal)
                && string.Equals(PlaybackScope, other.PlaybackScope, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is AnchorRotationHysteresisKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(ChildRecordingId);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(AnchorRecordingId);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(PlaybackScope);
                return hash;
            }
        }
    }

    internal struct AnchorRotationHysteresisState
    {
        public bool Held;
        public int HeldFrames;
    }

    /// <summary>
    /// Decides when parent attitude interpolation is too sparse to safely
    /// amplify through a recorded-relative debris offset. The three enter
    /// conditions are intentionally conjunctive: bracket angle and angular
    /// rate catch under-sampled tumbling, while the offset floor prevents
    /// close-in debris from hiding for visually insignificant parent motion.
    /// </summary>
    internal static class TumblingParentInterpolationGate
    {
        internal const double EnterAngleDegrees = 8.0;
        internal const double EnterRateDegreesPerSecond = 150.0;
        internal const double ExitAngleDegrees = 4.0;
        internal const double ExitRateDegreesPerSecond = 75.0;
        internal const double MinOffsetMagnitudeMeters = 50.0;
        internal const double MinOffsetMagnitudeSquaredMeters =
            MinOffsetMagnitudeMeters * MinOffsetMagnitudeMeters;

        internal static AnchorRotationReliabilityDecision EvaluateParentRotationInterpolation(
            string anchorRecordingId,
            TrajectoryPoint before,
            TrajectoryPoint after,
            double debrisLocalOffsetSquaredMeters)
        {
            double offsetMeters = ResolveOffsetMeters(debrisLocalOffsetSquaredMeters);
            double bracketSeconds = Math.Abs(after.ut - before.ut);
            double bracketDegrees = 0.0;
            double rateDegreesPerSecond = 0.0;

            if (IsFinite(bracketSeconds) && bracketSeconds > 1e-9)
            {
                bracketDegrees = TrajectoryMath.ComputeQuaternionAngleDegrees(
                    before.rotation,
                    after.rotation);
                if (!IsFinite(bracketDegrees))
                    bracketDegrees = 0.0;
                rateDegreesPerSecond = bracketDegrees / bracketSeconds;
                if (!IsFinite(rateDegreesPerSecond))
                    rateDegreesPerSecond = 0.0;
            }

            bool unreliable = ShouldEnter(bracketDegrees, rateDegreesPerSecond, offsetMeters);
            return new AnchorRotationReliabilityDecision(
                unreliable,
                anchorRecordingId,
                bracketDegrees,
                rateDegreesPerSecond,
                offsetMeters);
        }

        internal static bool UpdateHysteresis(
            AnchorRotationReliabilityDecision decision,
            ref AnchorRotationHysteresisState state)
        {
            bool held = state.Held
                ? !ShouldExit(decision.BracketDegrees, decision.RateDegreesPerSecond, decision.OffsetMeters)
                : ShouldEnter(decision.BracketDegrees, decision.RateDegreesPerSecond, decision.OffsetMeters);

            if (held)
                state.HeldFrames = state.Held ? state.HeldFrames + 1 : 1;
            else
                state.HeldFrames = 0;

            state.Held = held;
            return held;
        }

        internal static bool ShouldEnter(
            double bracketDegrees,
            double rateDegreesPerSecond,
            double offsetMeters)
        {
            return offsetMeters >= MinOffsetMagnitudeMeters
                && bracketDegrees >= EnterAngleDegrees
                && rateDegreesPerSecond >= EnterRateDegreesPerSecond;
        }

        internal static bool ShouldExit(
            double bracketDegrees,
            double rateDegreesPerSecond,
            double offsetMeters)
        {
            return offsetMeters < MinOffsetMagnitudeMeters
                || (bracketDegrees <= ExitAngleDegrees
                    && rateDegreesPerSecond <= ExitRateDegreesPerSecond);
        }

        private static double ResolveOffsetMeters(double debrisLocalOffsetSquaredMeters)
        {
            if (!IsFinite(debrisLocalOffsetSquaredMeters) || debrisLocalOffsetSquaredMeters <= 0.0)
                return 0.0;

            return Math.Sqrt(debrisLocalOffsetSquaredMeters);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
