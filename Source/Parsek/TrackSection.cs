namespace Parsek
{
    /// <summary>
    /// Classifies the physical environment a vessel is in during a trajectory segment.
    /// Drives variable sample rates and trajectory segmentation.
    /// </summary>
    public enum SegmentEnvironment
    {
        Atmospheric = 0,       // Below atmosphere ceiling on a body with atmosphere
        ExoPropulsive = 1,     // Above atmosphere (or no atmosphere) with active thrust
        ExoBallistic = 2,      // Above atmosphere (or no atmosphere) coasting (no thrust)
        SurfaceMobile = 3,     // Landed/splashed/prelaunch with surface speed > threshold
        SurfaceStationary = 4  // Landed/splashed/prelaunch with surface speed <= threshold
    }
}
