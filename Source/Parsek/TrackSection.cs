using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Physical environment classification for a trajectory segment.
    /// Determines sampling strategy and playback behavior.
    /// </summary>
    public enum SegmentEnvironment
    {
        Atmospheric = 0,        // Below atmosphere ceiling
        ExoPropulsive = 1,      // Above atmosphere, engine producing thrust
        ExoBallistic = 2,       // Above atmosphere, no thrust (Keplerian)
        SurfaceMobile = 3,      // Landed/splashed, moving (>0.1 m/s)
        SurfaceStationary = 4   // Landed/splashed, stationary (<0.1 m/s for >3s)
    }

    /// <summary>
    /// Coordinate reference frame used for recording/playback of a trajectory segment.
    /// </summary>
    public enum ReferenceFrame
    {
        Absolute = 0,           // Body-fixed coordinates (lat, lon, alt, attitude)
        Relative = 1,           // Offset from anchor vessel
        OrbitalCheckpoint = 2   // Keplerian elements at discrete timestamps
    }

    /// <summary>
    /// A typed chunk of trajectory data within a recording.
    /// Replaces the flat list of trajectory points with environment-tagged sections
    /// that carry their own reference frame and sampling metadata.
    ///
    /// IMPORTANT: This is a struct. The 'frames' and 'checkpoints' lists default to null.
    /// Always initialize them via StartNewTrackSection (recording) or DeserializeTrackSections
    /// (loading). Do not create TrackSection manually without initializing the lists.
    /// </summary>
    public struct TrackSection
    {
        public SegmentEnvironment environment;
        public ReferenceFrame referenceFrame;
        public double startUT;
        public double endUT;
        public uint anchorVesselId;             // For Relative frame only (0 = not set)
        public List<TrajectoryPoint> frames;    // For Absolute/Relative (null until initialized)
        public List<OrbitSegment> checkpoints;  // For OrbitalCheckpoint (null until initialized)
        public float sampleRateHz;              // Actual recording sample rate
        public bool isFromBackground;           // True if from background recording

        public override string ToString()
        {
            var ic = CultureInfo.InvariantCulture;
            int frameCount = frames?.Count ?? 0;
            int checkpointCount = checkpoints?.Count ?? 0;
            return $"TrackSection env={environment} ref={referenceFrame} " +
                   $"ut=[{startUT.ToString("F2", ic)},{endUT.ToString("F2", ic)}] frames={frameCount} " +
                   $"checkpoints={checkpointCount} bg={isFromBackground}";
        }
    }
}
