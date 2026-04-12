namespace Parsek
{
    /// <summary>
    /// Authoritative home for distance-based thresholds that affect ghost playback,
    /// watch mode, related scene policies, and nearby recorder behavior.
    /// Keep scene-specific exceptions here rather than scattering literals.
    /// </summary>
    internal static class DistanceThresholds
    {
        /// <summary>
        /// KSP's loaded-physics envelope around the active vessel. This is the core
        /// spatial boundary that several systems key off: rendering fidelity,
        /// relative-frame anchoring, spawn scoping, and background sampling.
        /// </summary>
        internal const double PhysicsBubbleMeters = 2300.0;

        /// <summary>
        /// Flight-scene ghost visual range boundary. Beyond this, the mesh is hidden
        /// and only logical playback / map presence remains.
        /// </summary>
        internal const double GhostVisualRangeMeters = 120000.0;

        internal static class RelativeFrame
        {
            internal const double EntryMeters = PhysicsBubbleMeters;
            internal const double ExitMeters = 2500.0;
            internal const double DockingApproachMeters = 200.0;
        }

        internal static class BackgroundSampling
        {
            internal const double DockingRangeMeters = RelativeFrame.DockingApproachMeters;
            internal const double MidRangeMeters = 1000.0;
            internal const double MaxDistanceMeters = PhysicsBubbleMeters;
        }

        internal static class GhostFlight
        {
            internal const double LoopFullFidelityMeters = PhysicsBubbleMeters;
            internal const double LoopSimplifiedMeters = 50000.0;

            internal const float DefaultWatchCameraCutoffKm = 300f;
            internal const double AirlessWatchHorizonLockAltitudeMeters = 50000.0;

            internal static float GetWatchCameraCutoffKm(ParsekSettings settings)
                => settings?.ghostCameraCutoffKm ?? DefaultWatchCameraCutoffKm;

            internal static double GetWatchCameraCutoffMeters(ParsekSettings settings)
                => GetWatchCameraCutoffKm(settings) * 1000.0;

            internal static bool ShouldAutoHorizonLock(
                bool hasAtmosphere, double atmosphereDepth, double altitudeMeters)
            {
                double threshold = hasAtmosphere
                    ? atmosphereDepth
                    : AirlessWatchHorizonLockAltitudeMeters;
                return altitudeMeters < threshold;
            }

            internal static double ComputeTerrainClearance(double distanceToVesselMeters)
            {
                if (distanceToVesselMeters <= PhysicsBubbleMeters)
                    return 0.5;

                double t = (distanceToVesselMeters - PhysicsBubbleMeters)
                    / (GhostVisualRangeMeters - PhysicsBubbleMeters);
                if (t > 1.0) t = 1.0;
                return 2.0 + t * 3.0;
            }
        }

        internal static class GhostAudio
        {
            internal const float RolloffMinDistanceMeters = 30f;
            internal const float RolloffMaxDistanceMeters = 5000f;
        }

        internal static class KscGhosts
        {
            internal const float CullDistanceMeters = 25000f;
            internal const float CullDistanceSq = CullDistanceMeters * CullDistanceMeters;
        }
    }
}
