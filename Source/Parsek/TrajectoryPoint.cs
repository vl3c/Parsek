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
        // Absolute / surface playback stores body-surface-relative rotation (v.srfRelRotation).
        // Format-v6 RELATIVE TrackSections instead store anchor-local world rotation.
        // Legacy v5-and-older RELATIVE sections keep their original playback contract.
        public Quaternion rotation;
        public Vector3 velocity;    // Playback velocity captured from KSP; not guaranteed surface-relative
        public string bodyName;     // Reference celestial body

        // Career mode resources (absolute values at this tick)
        public double funds;
        public float science;
        public float reputation;

        // Phase 7: continuous terrain correction (design doc §13.1, §17.3.2,
        // §18 Phase 7). Recorded ground clearance in metres = altitude (root
        // origin) - terrainHeight at this point's lat/lon at recording time.
        // Populated only for SurfaceMobile section samples (post-v9 recordings).
        // NaN sentinel for legacy points and non-SurfaceMobile environments —
        // playback falls through to the legacy altitude-only path.
        // Binary codec gates this field on
        // RecordingStore.TerrainGroundClearanceFormatVersion (v9+).
        public double recordedGroundClearance;

        // Phase 9: per-sample flag bitset (design doc §12, §17.3.2, §18 Phase 9).
        // Bit 0 (<see cref="TrajectoryPointFlags.StructuralEventSnapshot"/>) marks
        // a synthetic snapshot the recorder appended at the exact UT of a structural
        // event (dock / undock / EVA / joint-break) so anchor ε at re-fly merge
        // points lands at physics-precision instead of a one-tick interpolation.
        // Bits 1-7 are reserved. Default 0 for every legacy point and every regular
        // per-tick sample. Binary codec gates the byte on
        // RecordingStore.StructuralEventFlagFormatVersion (v10+); legacy readers
        // default to 0.
        public byte flags;

        public override string ToString()
        {
            return $"UT={ut:F1} lat={latitude:F4} lon={longitude:F4} alt={altitude:F1}";
        }
    }
}
