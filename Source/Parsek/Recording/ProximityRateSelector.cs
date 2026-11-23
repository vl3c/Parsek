namespace Parsek
{
    /// <summary>
    /// Pure static helper that maps distance from the focused vessel
    /// to background recording sample interval. Closer = higher rate.
    ///
    /// <para>
    /// The table has no silent band. Every caller holds a LOADED, unpacked vessel
    /// (<c>BackgroundRecorder.OnBackgroundPhysicsFrame</c> returns early on a packed one), and
    /// stock keeps a flying vessel in physics out to 22.5 km (<c>VesselRanges</c>: flying
    /// unload 22500 m, sub-orbital pack 10000 m), so a vessel past <see cref="PhysicsBubble"/>
    /// is still falling for tens of seconds. Its samples are the only record of that fall: a
    /// silent band left the replay holding the last sample until the on-rails conic began,
    /// kilometres away.
    /// </para>
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
        // Past PhysicsBubble: parent-anchored debris drops from the in-range MidInterval cap to
        // this floor (an ordinary vessel keeps FarInterval), and nothing tightens it: no debris
        // ceiling cap and no attitude floor below it.
        internal const double BeyondRangeDebrisInterval = 1.0; // 1 Hz
        // "No interval yet": the value a loaded state holds before its first physics tick.
        internal const double UnsetInterval = double.MaxValue;

        /// <summary>
        /// Returns the sample interval in seconds for a background vessel
        /// at the given distance from the focused vessel.
        /// </summary>
        internal static double GetSampleInterval(double distanceMeters)
        {
            if (distanceMeters < DockingRange) return DockingInterval;
            if (distanceMeters < MidRange) return MidInterval;
            return FarInterval;
        }

        /// <summary>
        /// True when the vessel is past the proximity table's last band: at or past
        /// <see cref="PhysicsBubble"/>, or at an unknown distance (no focused vessel reads
        /// <c>double.MaxValue</c>, an unreadable pose NaN).
        /// </summary>
        internal static bool IsBeyondRange(double distanceMeters)
        {
            return !(distanceMeters < PhysicsBubble);
        }
    }
}
