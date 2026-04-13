using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// A single point in the recorded trajectory.
    /// Uses geographic coordinates (lat/lon/alt) instead of Unity world coords
    /// because world coords drift over time as celestial bodies move.
    /// </summary>
    public struct TrajectoryPoint
    {
        public double ut;           // Universal Time when recorded
        public double latitude;
        public double longitude;
        public double altitude;
        public Quaternion rotation;
        public Vector3 velocity;    // Playback velocity captured from KSP; not guaranteed surface-relative
        public string bodyName;     // Reference celestial body

        // Career mode resources (absolute values at this tick)
        public double funds;
        public float science;
        public float reputation;

        public override string ToString()
        {
            return $"UT={ut:F1} lat={latitude:F4} lon={longitude:F4} alt={altitude:F1}";
        }
    }
}
