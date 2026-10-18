using System;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Outcome of <see cref="RecordedTailVelocityResolver.TryResolve"/>.
    /// </summary>
    internal struct RecordedTailVelocityResolution
    {
        internal bool Resolved;
        internal string DeclineReason;
        // True when the recorded vectors fit as surface-relative (co-rotating world frame).
        internal bool SampleWasRotatingFrame;
        // Yaw about the spin axis (world Y) mapping the recorded world axes onto the
        // rebuild frame, degrees, wrapped to (-180, 180].
        internal double YawDegrees;
        // |R(yaw) * recordedMid - reference| for the chosen and the rejected hypothesis, m/s.
        internal double Residual;
        internal double OtherResidual;
        internal double RecordedSpeed;
        // Inertial (body-centred, non-rotating) velocity at the tail sample, in the frame
        // the positions were given in.
        internal Vector3d InertialVelocityYup;
    }

    /// <summary>
    /// Recovers the inertial velocity of a recorded tail sample in the frame its position
    /// is rebuilt in, without trusting the recorded vector's frame (see
    /// <see cref="OrbitReseed"/>'s class summary: the vector may be surface-relative, and
    /// its world axes sit at an unrecorded yaw about the spin axis).
    ///
    /// Inputs are the tail sample and the coast sample before it, with body-relative Y-up
    /// positions rebuilt from their body-fixed lat/lon/alt against ONE body orientation
    /// (the caller's <c>GetWorldSurfacePosition</c>), so the chord between them is the
    /// surface-relative velocity at the midpoint in the rebuild frame. Two hypotheses are
    /// tested against the mean of the two recorded vectors (also the midpoint velocity, to
    /// the same order): rotating (recorded vectors are surface-relative, so they map onto
    /// the chord) and inertial (they map onto the chord plus the frame velocity
    /// <see cref="OrbitReseed.ComputeRotatingFrameVelocityYup"/>). Each hypothesis is fitted
    /// with the one yaw about Y that aligns the horizontal (XZ) azimuths, and scored by the
    /// full vector residual, so a magnitude, a spin-axis component or a handedness that does
    /// not fit shows up. The better fit wins when it is within
    /// <see cref="MaxResidualMetersPerSecond"/> + <see cref="MaxResidualFraction"/>, except
    /// that two fits within that tolerance of each other defer to the caller's altitude rule
    /// (<see cref="OrbitReseed.IsRecordedVelocityInRotatingFrame"/>); no fit declines. The
    /// tail's recorded vector is then rotated by that yaw (magnitude and spin-axis component
    /// are kept exactly) and, for a rotating sample, the frame velocity at the tail added.
    ///
    /// Bounds: the chord approximates the midpoint velocity to O(dt^2) (0.1 m/s at 3 s in
    /// low Kerbin orbit); for an inertial sample the yaw drifts by the body rotation over
    /// half the pair gap (0.025 deg at 3 s). Measured on LAB-7's recorded tail (run
    /// 2026-10-18_0026): rotating residual 0.014 m/s against 202 m/s for the wrong
    /// hypothesis, sma 699737.9 / ecc 0.0103 against stock's 699745.8 / 0.0104.
    /// </summary>
    internal static class RecordedTailVelocityResolver
    {
        internal const double MaxPairGapSeconds = 30.0;
        internal const double MinPairGapSeconds = 1e-3;
        internal const double MaxResidualMetersPerSecond = 2.0;
        internal const double MaxResidualFraction = 0.005;
        internal const double MinHorizontalSpeedMetersPerSecond = 1.0;

        internal const string DeclineNonFinite = "tail-pair-non-finite";
        internal const string DeclinePairGap = "tail-pair-gap-out-of-range";
        internal const string DeclineAzimuthUndefined = "tail-velocity-azimuth-undefined";
        internal const string DeclineNoFit = "tail-velocity-frame-no-fit";

        internal static bool TryResolve(
            Vector3d previousPosYup,
            double previousUT,
            Vector3d previousRecordedVelYup,
            Vector3d tailPosYup,
            double tailUT,
            Vector3d tailRecordedVelYup,
            double rotationPeriod,
            bool preferRotatingOnTie,
            out RecordedTailVelocityResolution result)
        {
            result = new RecordedTailVelocityResolution
            {
                Resolved = false,
                DeclineReason = null,
                SampleWasRotatingFrame = false,
                YawDegrees = double.NaN,
                Residual = double.NaN,
                OtherResidual = double.NaN,
                RecordedSpeed = IsFinite(tailRecordedVelYup) ? tailRecordedVelYup.magnitude : double.NaN,
                InertialVelocityYup = Vector3d.zero
            };

            if (!IsFinite(previousPosYup) || !IsFinite(tailPosYup)
                || !IsFinite(previousRecordedVelYup) || !IsFinite(tailRecordedVelYup)
                || !IsFinite(previousUT) || !IsFinite(tailUT))
            {
                result.DeclineReason = DeclineNonFinite;
                return false;
            }

            double dt = tailUT - previousUT;
            if (dt < MinPairGapSeconds || dt > MaxPairGapSeconds)
            {
                result.DeclineReason = DeclinePairGap;
                return false;
            }

            Vector3d chord = (tailPosYup - previousPosYup) / dt;
            Vector3d midPos = (tailPosYup + previousPosYup) * 0.5;
            Vector3d recordedMid = (tailRecordedVelYup + previousRecordedVelYup) * 0.5;
            Vector3d frameVelMid = OrbitReseed.ComputeRotatingFrameVelocityYup(midPos, rotationPeriod);

            bool rotatingOk = TryFitYaw(recordedMid, chord, out double rotatingYaw, out double rotatingResidual);
            bool inertialOk = TryFitYaw(recordedMid, chord + frameVelMid, out double inertialYaw, out double inertialResidual);
            if (!rotatingOk && !inertialOk)
            {
                result.DeclineReason = DeclineAzimuthUndefined;
                return false;
            }

            double tolerance = MaxResidualMetersPerSecond + MaxResidualFraction * recordedMid.magnitude;
            bool chooseRotating;
            if (rotatingOk && inertialOk)
            {
                // Both readings fitting within the tolerance of each other is a real
                // geometry (the frame velocity can leave |surface| == |inertial|, e.g. a
                // near-polar orbit), and there the data cannot tell them apart: the
                // altitude rule decides. Otherwise the better fit does.
                bool ambiguous = rotatingResidual <= tolerance
                    && inertialResidual <= tolerance
                    && Math.Abs(rotatingResidual - inertialResidual) <= tolerance;
                chooseRotating = ambiguous
                    ? preferRotatingOnTie
                    : rotatingResidual < inertialResidual;
            }
            else
            {
                chooseRotating = rotatingOk;
            }

            double yaw = chooseRotating ? rotatingYaw : inertialYaw;
            result.SampleWasRotatingFrame = chooseRotating;
            result.YawDegrees = WrapDegrees(yaw * (180.0 / Math.PI));
            result.Residual = chooseRotating ? rotatingResidual : inertialResidual;
            result.OtherResidual = chooseRotating
                ? (inertialOk ? inertialResidual : double.NaN)
                : (rotatingOk ? rotatingResidual : double.NaN);

            if (!(result.Residual <= tolerance))
            {
                result.DeclineReason = DeclineNoFit;
                return false;
            }

            Vector3d tailInRebuildFrame = RotateAboutY(tailRecordedVelYup, yaw);
            result.InertialVelocityYup = chooseRotating
                ? tailInRebuildFrame + OrbitReseed.ComputeRotatingFrameVelocityYup(tailPosYup, rotationPeriod)
                : tailInRebuildFrame;
            result.Resolved = true;
            return true;
        }

        /// <summary>
        /// Pure: the yaw about Y taking <paramref name="from"/>'s XZ azimuth onto
        /// <paramref name="to"/>'s, and the vector residual left after applying it.
        /// False when either XZ projection is too short to carry an azimuth.
        /// </summary>
        internal static bool TryFitYaw(Vector3d from, Vector3d to, out double yawRadians, out double residual)
        {
            yawRadians = 0.0;
            residual = double.NaN;
            double fromXZ = Math.Sqrt(from.x * from.x + from.z * from.z);
            double toXZ = Math.Sqrt(to.x * to.x + to.z * to.z);
            if (fromXZ < MinHorizontalSpeedMetersPerSecond || toXZ < MinHorizontalSpeedMetersPerSecond)
                return false;

            yawRadians = Math.Atan2(to.z, to.x) - Math.Atan2(from.z, from.x);
            residual = (RotateAboutY(from, yawRadians) - to).magnitude;
            return true;
        }

        /// <summary>
        /// Pure: rotates <paramref name="v"/> about Y so that its XZ azimuth
        /// <c>atan2(z, x)</c> grows by <paramref name="yawRadians"/>. Keeps the magnitude
        /// and the Y component.
        /// </summary>
        internal static Vector3d RotateAboutY(Vector3d v, double yawRadians)
        {
            double c = Math.Cos(yawRadians);
            double s = Math.Sin(yawRadians);
            return new Vector3d(v.x * c - v.z * s, v.y, v.x * s + v.z * c);
        }

        private static double WrapDegrees(double degrees)
        {
            degrees %= 360.0;
            if (degrees <= -180.0)
                degrees += 360.0;
            if (degrees > 180.0)
                degrees -= 360.0;
            return degrees;
        }

        private static bool IsFinite(double value)
        {
            return !(double.IsNaN(value) || double.IsInfinity(value));
        }

        private static bool IsFinite(Vector3d value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }
    }
}
