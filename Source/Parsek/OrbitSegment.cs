using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// A segment of on-rails (Keplerian) orbit captured during recording.
    /// Replaces thousands of sampled TrajectoryPoints with ~8 orbital parameters.
    /// </summary>
    public struct OrbitSegment
    {
        public double startUT, endUT;
        public double inclination, eccentricity, semiMajorAxis;
        public double longitudeOfAscendingNode, argumentOfPeriapsis;
        public double meanAnomalyAtEpoch, epoch;
        public string bodyName;
        public bool isPredicted;

        /// <summary>
        /// Vessel rotation relative to the orbital velocity frame; identity = prograde.
        /// Default (0,0,0,0) = no data (old recordings).
        /// </summary>
        public Quaternion orbitalFrameRotation;

        /// <summary>
        /// Vessel-local angular velocity at on-rails boundary (rad/s).
        /// Recorded as: Inverse(v.transform.rotation) * v.angularVelocity.
        /// Default (0,0,0) = not spinning / no PersistentRotation at recording time.
        /// </summary>
        public Vector3 angularVelocity;

        public override string ToString()
        {
            var s = $"UT={startUT:F1}-{endUT:F1} body={bodyName ?? "?"} inc={inclination:F2} " +
                    $"ecc={eccentricity:F4} sma={semiMajorAxis:F1}";

            if (isPredicted)
                s += " predicted=true";

            // Append orbital-frame rotation when any component is non-zero (not the default sentinel)
            if (orbitalFrameRotation.x != 0f || orbitalFrameRotation.y != 0f
                || orbitalFrameRotation.z != 0f || orbitalFrameRotation.w != 0f)
            {
                s += $" ofrRot={orbitalFrameRotation}";
            }

            // Append angular velocity when non-zero
            if (angularVelocity.sqrMagnitude > 0f)
            {
                s += $" angVel={angularVelocity}";
            }

            if (isPredicted)
            {
                s += " predicted=true";
            }

            return s;
        }
    }
}
