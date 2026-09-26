using System;
using System.Globalization;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure half of the automation-only <c>SpinVessel</c> verb (D17 <c>persistent-rotation</c>):
    /// set the ACTIVE, unpacked vessel spinning about its control reference's roll axis
    /// (<c>ReferenceTransform.up</c>, the nose) at <c>rate=</c> rad/s, so a lane can put a
    /// genuinely rotating vessel on rails. The applier turns SAS off first (SAS would damp the
    /// spin back out) and gives every part rigidbody the same world angular velocity plus the
    /// tangential linear velocity a rigid body turning about the vessel CoM carries, so the
    /// joints see a consistent rigid spin rather than a per-part twist.
    /// <para>Grammar: <c>cmd=SpinVessel rate=&lt;rad/s&gt;</c>, rate in
    /// (0, <see cref="MaxRateRadPerSec"/>]. Single-phase on the default budget.</para>
    /// </summary>
    internal static class TestCommandSpinVessel
    {
        internal const string Verb = "SpinVessel";
        internal const string RateKey = "rate";

        /// <summary>Upper bound on <c>rate=</c>: well under Unity's default
        /// <c>Rigidbody.maxAngularVelocity</c> (7 rad/s), so the engine never clamps it.</summary>
        internal const float MaxRateRadPerSec = 2f;

        internal static readonly string[] Reasons =
        {
            "spinvessel-rate-arg-missing",
            "spinvessel-rate-arg-invalid",
            "spinvessel-no-active-vessel",
            "spinvessel-vessel-packed",
            "spinvessel-no-rigidbodies",
        };

        /// <summary>Parses <c>rate=</c> (invariant culture); returns null on success or the
        /// refusal reason. NaN, infinities, zero, negatives and anything above
        /// <see cref="MaxRateRadPerSec"/> are invalid.</summary>
        internal static string ValidateRate(string raw, out float rate)
        {
            rate = 0f;
            if (string.IsNullOrEmpty(raw))
                return "spinvessel-rate-arg-missing";
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                || float.IsNaN(parsed) || float.IsInfinity(parsed)
                || parsed <= 0f || parsed > MaxRateRadPerSec)
                return "spinvessel-rate-arg-invalid";
            rate = parsed;
            return null;
        }

        /// <summary>The world angular velocity for a roll spin: <paramref name="rate"/> about
        /// the reference transform's local up (the vessel nose in KSP's convention).</summary>
        internal static Vector3 ComputeRollAngularVelocityWorld(Quaternion referenceRotation, float rate)
            => TrajectoryMath.PureRotateVector(referenceRotation, Vector3.up) * rate;

        /// <summary>The linear velocity a point at <paramref name="partCenterOfMass"/> gains
        /// when the rigid vessel turns at <paramref name="angularVelocityWorld"/> about
        /// <paramref name="vesselCenterOfMass"/>: omega x r.</summary>
        internal static Vector3 ComputeTangentialVelocity(
            Vector3 angularVelocityWorld, Vector3 partCenterOfMass, Vector3 vesselCenterOfMass)
            => Vector3.Cross(angularVelocityWorld, partCenterOfMass - vesselCenterOfMass);

        /// <summary>The grep-stable success line.</summary>
        internal static string FormatAppliedLine(
            string vesselName, uint pid, float rate, int parts, bool sasWasOn)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "spinvessel applied: vessel=" + (vesselName ?? string.Empty)
                + " pid=" + pid.ToString(ic)
                + " rate=" + rate.ToString("F4", ic)
                + " axis=roll parts=" + parts.ToString(ic)
                + " sasWasOn=" + (sasWasOn ? "true" : "false");
        }
    }
}
