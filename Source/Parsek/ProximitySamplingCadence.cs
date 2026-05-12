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
        // public-API-shaped and a degenerate caller passing configuredMin == 0
        // would otherwise produce a literal zero interval. Downstream
        // ShouldRecordPoint compares `elapsed >= maxInterval`, so a literal
        // zero would record on every physics frame and break the inv that
        // every sampling decision flows through a positive interval. The
        // floor is at the physics-tick scale (KSP's default fixedDeltaTime is
        // 0.02 s = 50 Hz) so the clamped output is still the maximum cadence
        // physics can deliver, NOT a meaningless near-zero. Set below the
        // tightest production configuredMin (0.05f at High density) so this
        // floor is never reached in production.
        internal const float MinimumSampleIntervalSeconds = 0.02f;

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
