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
        // Defensive floor for tier-derived sample intervals. Production
        // ParsekSettings.GetMinSampleInterval enforces a positive value
        // (0.05f / 0.2f / 0.5f at High / Medium / Low), but the helper is
        // public-API-shaped and the floor guards against a degenerate caller
        // passing configuredMin == 0 (which would otherwise collapse every
        // tier to 0 and record every physics frame).
        internal const float MinimumSampleIntervalSeconds = 0.001f;

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

            if (distanceMeters < 0.0)
            {
                reason = "distance-invalid";
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

            float interval = Math.Max(MinimumSampleIntervalSeconds, configuredMin);
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
