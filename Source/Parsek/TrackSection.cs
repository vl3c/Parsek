using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Data provenance for a track section — indicates which subsystem produced it.
    /// Used for diagnostics and to distinguish active recording from background/checkpoint data.
    /// </summary>
    public enum TrackSectionSource
    {
        Active = 0,          // From focused vessel's FlightRecorder
        Background = 1,      // From BackgroundRecorder (physics bubble)
        Checkpoint = 2       // From orbital checkpoint propagation
    }

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
        SurfaceStationary = 4,  // Landed/splashed, stationary (<0.1 m/s for >3s)
        Approach = 5            // Below approach altitude on airless body (not on surface)
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
    /// IMPORTANT: This is a struct. The 'frames', 'absoluteFrames', and 'checkpoints' lists default to null.
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
        public List<TrajectoryPoint> absoluteFrames; // For Relative only: planet-relative shadow payload
        public List<OrbitSegment> checkpoints;  // For OrbitalCheckpoint (null until initialized)
        public float sampleRateHz;              // Actual recording sample rate
        public TrackSectionSource source;       // Data provenance (for diagnostics)
        public float boundaryDiscontinuityMeters; // Position gap at section start vs previous section end (0 = no gap)
        public float minAltitude;              // Minimum altitude during this section (NaN = not set)
        public float maxAltitude;              // Maximum altitude during this section (NaN = not set)

        public override string ToString()
        {
            var ic = CultureInfo.InvariantCulture;
            int frameCount = frames?.Count ?? 0;
            int absoluteFrameCount = absoluteFrames?.Count ?? 0;
            int checkpointCount = checkpoints?.Count ?? 0;
            string altRange = !float.IsNaN(minAltitude) && !float.IsNaN(maxAltitude)
                ? $" alt=[{minAltitude.ToString("F0", ic)},{maxAltitude.ToString("F0", ic)}]" : "";
            return $"TrackSection env={environment} ref={referenceFrame} " +
                   $"ut=[{startUT.ToString("F2", ic)},{endUT.ToString("F2", ic)}] frames={frameCount} absFrames={absoluteFrameCount} " +
                   $"checkpoints={checkpointCount} " +
                   $"src={source} bdisc={boundaryDiscontinuityMeters.ToString("F2", ic)}{altRange}";
        }
    }
}
