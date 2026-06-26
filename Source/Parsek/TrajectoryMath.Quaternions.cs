using System;
using System.Collections.Generic;
using Parsek.Rendering;
using UnityEngine;

namespace Parsek
{
    public static partial class TrajectoryMath
    {
        /// <summary>
        /// Sanitize a quaternion by replacing NaN/Infinity with safe values
        /// and normalizing. Returns identity if magnitude is near-zero.
        /// </summary>
        internal static Quaternion SanitizeQuaternion(Quaternion q)
        {
            bool hadBadComponent = false;
            if (float.IsNaN(q.x) || float.IsInfinity(q.x)) { q.x = 0; hadBadComponent = true; }
            if (float.IsNaN(q.y) || float.IsInfinity(q.y)) { q.y = 0; hadBadComponent = true; }
            if (float.IsNaN(q.z) || float.IsInfinity(q.z)) { q.z = 0; hadBadComponent = true; }
            if (float.IsNaN(q.w) || float.IsInfinity(q.w)) { q.w = 1; hadBadComponent = true; }

            if (hadBadComponent)
                ParsekLog.VerboseRateLimited("TrajectoryMath", "sanitize-quat",
                    "SanitizeQuaternion replaced NaN/Infinity component(s)");

            float magnitude = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (float.IsNaN(magnitude) || float.IsInfinity(magnitude) || magnitude < 0.001f)
            {
                ParsekLog.VerboseRateLimited("TrajectoryMath", "sanitize-quat-identity",
                    "SanitizeQuaternion returned identity (near-zero magnitude)");
                return Quaternion.identity;
            }

            return new Quaternion(q.x / magnitude, q.y / magnitude, q.z / magnitude, q.w / magnitude);
        }

        /// <summary>
        /// Canonicalizes quaternions for angle comparisons so sign-equivalent values
        /// (q and -q) compare as the same physical rotation.
        /// </summary>
        internal static Quaternion NormalizeQuaternionForComparison(Quaternion q)
        {
            Quaternion normalized = PureNormalize(SanitizeQuaternion(q));
            return normalized.w < 0f
                ? new Quaternion(-normalized.x, -normalized.y, -normalized.z, -normalized.w)
                : normalized;
        }

        /// <summary>
        /// Returns the physical angle between two rotations in degrees after sanitizing
        /// and canonicalizing sign-equivalent quaternions.
        /// </summary>
        internal static float ComputeQuaternionAngleDegrees(Quaternion from, Quaternion to)
        {
            return Quaternion.Angle(
                NormalizeQuaternionForComparison(from),
                NormalizeQuaternionForComparison(to));
        }

        /// <summary>Spin threshold in rad/s (matches PersistentRotation's threshold).</summary>
        internal const float SpinThreshold = 0.05f;

        /// <summary>
        /// Returns true if the segment has recorded orbital-frame rotation data.
        /// Default struct value (0,0,0,0) = no data.
        /// </summary>
        internal static bool HasOrbitalFrameRotation(OrbitSegment seg)
            => seg.orbitalFrameRotation.x != 0f || seg.orbitalFrameRotation.y != 0f
            || seg.orbitalFrameRotation.z != 0f || seg.orbitalFrameRotation.w != 0f;

        /// <summary>
        /// Returns true if the segment has spin data (angular velocity above threshold).
        /// </summary>
        internal static bool IsSpinning(OrbitSegment seg)
            => seg.angularVelocity.sqrMagnitude > SpinThreshold * SpinThreshold;

        /// <summary>
        /// Computes vessel rotation relative to the orbital velocity frame.
        /// Returns Inverse(orbFrame) * worldRotation.
        /// Returns identity if velocity is near-zero (degenerate frame).
        /// Falls back to LookRotation(velocity) without up hint if velocity
        /// and radialOut are near-parallel (dot > 0.99).
        /// Uses pure-math quaternion operations (no Unity native calls) for testability.
        /// </summary>
        internal static Quaternion ComputeOrbitalFrameRotation(
            Quaternion worldRotation, Vector3d orbitalVelocity, Vector3d radialOut)
        {
            if (orbitalVelocity.sqrMagnitude < 0.001)
            {
                ParsekLog.VerboseRateLimited("TrajectoryMath", "ofr-degenerate-velocity",
                    $"Orbital-frame rotation: degenerate velocity (sqrMag={orbitalVelocity.sqrMagnitude:F6}), using identity");
                return Quaternion.identity;
            }

            Vector3 velNorm = ((Vector3)orbitalVelocity).normalized;
            Vector3 radNorm = ((Vector3)radialOut).normalized;
            float dot = Vector3.Dot(velNorm, radNorm);

            Quaternion orbFrame;
            if (Mathf.Abs(dot) > 0.99f)
            {
                ParsekLog.VerboseRateLimited("TrajectoryMath", "ofr-near-parallel",
                    $"Orbital-frame rotation: velocity/radialOut near-parallel (dot={dot:F4}), frame approximated");
                orbFrame = PureLookRotation(velNorm, Vector3.up);
            }
            else
            {
                orbFrame = PureLookRotation(velNorm, radNorm);
            }

            return PureMultiply(PureInverse(orbFrame), worldRotation);
        }

        // --- Pure-math quaternion helpers (no Unity native calls) ---

        /// <summary>
        /// Pure-math LookRotation: builds a rotation from forward and up vectors.
        /// Equivalent to Quaternion.LookRotation but uses only managed code.
        /// </summary>
        internal static Quaternion PureLookRotation(Vector3 forward, Vector3 up)
        {
            forward = forward.normalized;
            if (forward.sqrMagnitude < 1e-6f) return Quaternion.identity;

            Vector3 right = Vector3.Cross(up, forward).normalized;
            if (right.sqrMagnitude < 1e-6f)
            {
                up = Mathf.Abs(forward.y) < 0.9f ? Vector3.up : Vector3.right;
                right = Vector3.Cross(up, forward).normalized;
            }
            up = Vector3.Cross(forward, right);

            float m00 = right.x, m01 = up.x, m02 = forward.x;
            float m10 = right.y, m11 = up.y, m12 = forward.y;
            float m20 = right.z, m21 = up.z, m22 = forward.z;

            float trace = m00 + m11 + m22;
            Quaternion q;
            if (trace > 0)
            {
                float s = Mathf.Sqrt(trace + 1f) * 2f;
                q = new Quaternion(
                    (m21 - m12) / s,
                    (m02 - m20) / s,
                    (m10 - m01) / s,
                    s / 4f);
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
                q = new Quaternion(
                    s / 4f,
                    (m01 + m10) / s,
                    (m02 + m20) / s,
                    (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
                q = new Quaternion(
                    (m01 + m10) / s,
                    s / 4f,
                    (m12 + m21) / s,
                    (m02 - m20) / s);
            }
            else
            {
                float s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
                q = new Quaternion(
                    (m02 + m20) / s,
                    (m12 + m21) / s,
                    s / 4f,
                    (m10 - m01) / s);
            }
            return PureNormalize(q);
        }

        /// <summary>Pure-math quaternion inverse (conjugate / sqrMagnitude).</summary>
        internal static Quaternion PureInverse(Quaternion q)
        {
            float sqrMag = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (sqrMag < 1e-12f) return Quaternion.identity;
            float inv = 1f / sqrMag;
            return new Quaternion(-q.x * inv, -q.y * inv, -q.z * inv, q.w * inv);
        }

        /// <summary>Pure-math quaternion multiplication.</summary>
        internal static Quaternion PureMultiply(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        }

        /// <summary>Pure-math quaternion normalization.</summary>
        internal static Quaternion PureNormalize(Quaternion q)
        {
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (mag < 1e-6f) return Quaternion.identity;
            return new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
        }

        /// <summary>
        /// Pure-math quaternion slerp equivalent to Quaternion.Slerp without Unity native calls.
        /// </summary>
        internal static Quaternion PureSlerp(Quaternion from, Quaternion to, float t)
        {
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;

            from = PureNormalize(SanitizeQuaternion(from));
            to = PureNormalize(SanitizeQuaternion(to));

            float dot =
                from.x * to.x +
                from.y * to.y +
                from.z * to.z +
                from.w * to.w;
            if (dot < 0f)
            {
                to = new Quaternion(-to.x, -to.y, -to.z, -to.w);
                dot = -dot;
            }

            if (dot > 0.9995f)
            {
                return PureNormalize(new Quaternion(
                    from.x + (to.x - from.x) * t,
                    from.y + (to.y - from.y) * t,
                    from.z + (to.z - from.z) * t,
                    from.w + (to.w - from.w) * t));
            }

            if (dot > 1f) dot = 1f;
            double theta0 = System.Math.Acos(dot);
            double theta = theta0 * t;
            double sinTheta = System.Math.Sin(theta);
            double sinTheta0 = System.Math.Sin(theta0);

            double s0 = System.Math.Cos(theta) - dot * sinTheta / sinTheta0;
            double s1 = sinTheta / sinTheta0;
            return PureNormalize(new Quaternion(
                (float)(from.x * s0 + to.x * s1),
                (float)(from.y * s0 + to.y * s1),
                (float)(from.z * s0 + to.z * s1),
                (float)(from.w * s0 + to.w * s1)));
        }

        /// <summary>Pure-math AngleAxis rotation.</summary>
        internal static Quaternion PureAngleAxis(float angleDeg, Vector3 axis)
        {
            float mag = axis.magnitude;
            if (mag < 1e-6f) return Quaternion.identity;
            axis = axis / mag;
            float halfRad = angleDeg * Mathf.Deg2Rad * 0.5f;
            float s = Mathf.Sin(halfRad);
            float c = Mathf.Cos(halfRad);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, c);
        }

        /// <summary>Pure-math: rotate a vector by a quaternion (q * v * q^-1).</summary>
        internal static Vector3 PureRotateVector(Quaternion q, Vector3 v)
        {
            float qx = q.x, qy = q.y, qz = q.z, qw = q.w;
            float tx = 2f * (qy * v.z - qz * v.y);
            float ty = 2f * (qz * v.x - qx * v.z);
            float tz = 2f * (qx * v.y - qy * v.x);
            return new Vector3(
                v.x + qw * tx + (qy * tz - qz * ty),
                v.y + qw * ty + (qz * tx - qx * tz),
                v.z + qw * tz + (qx * ty - qy * tx));
        }
    }
}
