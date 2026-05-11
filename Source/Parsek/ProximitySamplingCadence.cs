using System;

namespace Parsek
{
    internal enum ProximitySamplingTier
    {
        None = 0,
        Half = 1,
        Full = 2,
    }

    internal static class ProximitySamplingCadence
    {
        internal static ProximitySamplingTier Resolve(
            double distanceMeters,
            double fullFidelityMaxMeters,
            double halfFidelityMaxMeters,
            out string reason)
        {
            if (!IsFinite(distanceMeters))
            {
                reason = "distance-missing";
                return ProximitySamplingTier.None;
            }

            if (distanceMeters <= fullFidelityMaxMeters)
            {
                reason = "full";
                return ProximitySamplingTier.Full;
            }

            if (distanceMeters <= halfFidelityMaxMeters)
            {
                reason = "half";
                return ProximitySamplingTier.Half;
            }

            reason = "out-of-range";
            return ProximitySamplingTier.None;
        }

        internal static float ResolveSampleInterval(
            ProximitySamplingTier tier,
            float configuredMin,
            float configuredMax)
        {
            if (tier == ProximitySamplingTier.None)
                return configuredMax;

            float interval = Math.Max(0f, configuredMin);
            if (tier == ProximitySamplingTier.Half)
                interval *= 2f;

            return Math.Min(configuredMax, interval);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
