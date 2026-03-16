namespace Parsek
{
    /// <summary>
    /// The environment type for a track section.
    /// Determines which physics model and ghost rendering to use.
    /// </summary>
    public enum TrackEnvironment
    {
        Surface     = 0,  // On or near a body surface (landed, splashed, low flight)
        Atmosphere  = 1,  // In atmosphere (flying, aerobraking)
        Space       = 2,  // In space (orbiting, escaping, sub-orbital above atmo)
    }

    /// <summary>
    /// The reference frame type for trajectory data in a track section.
    /// </summary>
    public enum TrackFrame
    {
        BodyFixed   = 0,  // Surface-relative (co-rotating with body)
        Inertial    = 1,  // Body-centered inertial (non-rotating)
    }

    /// <summary>
    /// A typed chunk of trajectory data within a recording segment.
    /// Each track section has a specific environment and reference frame,
    /// and covers a contiguous range of trajectory points.
    /// </summary>
    public struct TrackSection
    {
        public int startIndex;          // First point index in Recording.Points (inclusive)
        public int endIndex;            // Last point index in Recording.Points (inclusive)
        public TrackEnvironment environment;
        public TrackFrame frame;
        public string bodyName;         // Reference body name (e.g., "Kerbin")

        public override string ToString()
        {
            return $"Track [{startIndex}-{endIndex}] env={environment} frame={frame} body='{bodyName ?? ""}'";
        }
    }
}
