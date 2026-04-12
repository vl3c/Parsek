namespace Parsek
{
    /// <summary>
    /// Pure static helper that maps distance from the focused vessel
    /// to background recording sample interval. Closer = higher rate.
    /// </summary>
    internal static class ProximityRateSelector
    {
        // Distance thresholds (meters)
        internal const double DockingRange = DistanceThresholds.BackgroundSampling.DockingRangeMeters;
        internal const double MidRange = DistanceThresholds.BackgroundSampling.MidRangeMeters;
        internal const double PhysicsBubble = DistanceThresholds.BackgroundSampling.MaxDistanceMeters;

        // Sample intervals (seconds between samples)
        internal const double DockingInterval = 0.2;       // 5 Hz
        internal const double MidInterval = 0.5;            // 2 Hz
        internal const double FarInterval = 2.0;             // 0.5 Hz
        internal const double OutOfRangeInterval = double.MaxValue; // don't sample

        /// <summary>
        /// Returns the sample interval in seconds for a background vessel
        /// at the given distance from the focused vessel.
        /// </summary>
        internal static double GetSampleInterval(double distanceMeters)
        {
            if (distanceMeters < DockingRange) return DockingInterval;
            if (distanceMeters < MidRange) return MidInterval;
            if (distanceMeters < PhysicsBubble) return FarInterval;
            return OutOfRangeInterval;
        }

        /// <summary>
        /// Returns the approximate sample rate in Hz for diagnostics/logging.
        /// </summary>
        internal static float GetSampleRateHz(double distanceMeters)
        {
            double interval = GetSampleInterval(distanceMeters);
            if (interval <= 0 || interval >= OutOfRangeInterval || double.IsInfinity(interval)) return 0f;
            return (float)(1.0 / interval);
        }
    }
}
