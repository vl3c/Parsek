using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Pure static classification of vessel environment state.
    /// No state, no Vessel dependency — directly testable with primitive parameters.
    /// </summary>
    internal static class EnvironmentDetector
    {
        /// <summary>
        /// Pure classification — no hysteresis, no debounce.
        /// Parameters extracted from Vessel for testability (no Vessel dependency).
        /// </summary>
        /// <param name="hasAtmosphere">body.atmosphere</param>
        /// <param name="altitude">vessel.altitude</param>
        /// <param name="atmosphereDepth">body.atmosphereDepth</param>
        /// <param name="situation">(int)vessel.situation (LANDED=1, SPLASHED=2, PRELAUNCH=4, FLYING=8, etc.)</param>
        /// <param name="srfSpeed">vessel.srfSpeed</param>
        /// <param name="hasActiveThrust">any engine with currentThrust > 0</param>
        internal static SegmentEnvironment Classify(
            bool hasAtmosphere,
            double altitude,
            double atmosphereDepth,
            int situation,
            double srfSpeed,
            bool hasActiveThrust)
        {
            // Landed/Splashed/Prelaunch -> surface states
            // situation == 1 (LANDED) or situation == 2 (SPLASHED) or situation == 4 (PRELAUNCH)
            if (situation == 1 || situation == 2 || situation == 4)
            {
                var surfaceResult = srfSpeed > 0.1
                    ? SegmentEnvironment.SurfaceMobile
                    : SegmentEnvironment.SurfaceStationary;

                ParsekLog.Verbose("Environment",
                    $"Classify: situation={situation} srfSpeed={srfSpeed.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"-> {surfaceResult}");

                return surfaceResult;
            }

            // Atmospheric -> below atmosphere ceiling on a body with atmosphere
            if (hasAtmosphere && altitude < atmosphereDepth)
            {
                ParsekLog.Verbose("Environment",
                    $"Classify: hasAtmo=true alt={altitude.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"atmoDepth={atmosphereDepth.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"situation={situation} -> Atmospheric");

                return SegmentEnvironment.Atmospheric;
            }

            // Exo -> above atmosphere (or no atmosphere)
            if (hasActiveThrust)
            {
                ParsekLog.Verbose("Environment",
                    $"Classify: hasAtmo={hasAtmosphere} alt={altitude.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"atmoDepth={atmosphereDepth.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"thrust=active situation={situation} -> ExoPropulsive");

                return SegmentEnvironment.ExoPropulsive;
            }

            ParsekLog.Verbose("Environment",
                $"Classify: hasAtmo={hasAtmosphere} alt={altitude.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"atmoDepth={atmosphereDepth.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"thrust=none situation={situation} -> ExoBallistic");

            return SegmentEnvironment.ExoBallistic;
        }
    }

    /// <summary>
    /// Stateful debounce wrapper around EnvironmentDetector.Classify.
    /// Prevents rapid oscillation between environments (e.g., thrust toggle, surface speed jitter).
    /// </summary>
    internal class EnvironmentHysteresis
    {
        internal const double ThrustDebounceSeconds = 1.0;
        internal const double SurfaceSpeedDebounceSeconds = 3.0;
        internal const double SurfaceAtmosphericDebounceSeconds = 0.5;

        private SegmentEnvironment lastConfirmedEnvironment;
        private SegmentEnvironment pendingEnvironment;
        private double pendingStartUT;
        private bool hasPending;

        internal SegmentEnvironment CurrentEnvironment => lastConfirmedEnvironment;

        internal EnvironmentHysteresis(SegmentEnvironment initial)
        {
            lastConfirmedEnvironment = initial;
            ParsekLog.Verbose("Environment",
                $"Hysteresis initialized: initial={initial}");
        }

        /// <summary>
        /// Feed a raw classification and the current UT.
        /// Returns true if the confirmed environment changed (after debounce elapsed).
        /// </summary>
        internal bool Update(SegmentEnvironment rawClassification, double ut)
        {
            if (rawClassification == lastConfirmedEnvironment)
            {
                // Cancel any pending transition
                if (hasPending)
                {
                    ParsekLog.Verbose("Environment",
                        $"Hysteresis: pending {lastConfirmedEnvironment}->{pendingEnvironment} " +
                        $"cancelled (raw reverted to {rawClassification} at UT={ut.ToString("F2", CultureInfo.InvariantCulture)})");
                    hasPending = false;
                }
                return false;
            }

            double requiredDebounce = GetDebounceFor(lastConfirmedEnvironment, rawClassification);

            if (!hasPending || pendingEnvironment != rawClassification)
            {
                // Start new pending transition
                pendingEnvironment = rawClassification;
                pendingStartUT = ut;
                hasPending = true;

                ParsekLog.Verbose("Environment",
                    $"Hysteresis: pending {lastConfirmedEnvironment}->{rawClassification} " +
                    $"started at UT={ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"(debounce={requiredDebounce.ToString("F1", CultureInfo.InvariantCulture)}s)");

                // Immediate transition if debounce is zero
                if (requiredDebounce <= 0.0)
                {
                    var old = lastConfirmedEnvironment;
                    lastConfirmedEnvironment = rawClassification;
                    hasPending = false;
                    ParsekLog.Info("Environment",
                        $"Environment transition: {old} -> {rawClassification} " +
                        $"at UT={ut.ToString("F2", CultureInfo.InvariantCulture)} (immediate, debounce=0.0s)");
                    return true;
                }

                return false;
            }

            // Check if debounce period elapsed
            if (ut - pendingStartUT >= requiredDebounce)
            {
                var old = lastConfirmedEnvironment;
                lastConfirmedEnvironment = rawClassification;
                hasPending = false;
                ParsekLog.Info("Environment",
                    $"Environment transition: {old} -> {rawClassification} " +
                    $"at UT={ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"(debounce={requiredDebounce.ToString("F1", CultureInfo.InvariantCulture)}s)");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the debounce duration in seconds for a given transition.
        /// Pure function, no logging (called frequently).
        /// </summary>
        internal static double GetDebounceFor(SegmentEnvironment from, SegmentEnvironment to)
        {
            // Thrust toggle: 1s debounce
            if ((from == SegmentEnvironment.ExoPropulsive && to == SegmentEnvironment.ExoBallistic) ||
                (from == SegmentEnvironment.ExoBallistic && to == SegmentEnvironment.ExoPropulsive))
                return ThrustDebounceSeconds;

            // Surface speed oscillation: 3s debounce
            if ((from == SegmentEnvironment.SurfaceMobile && to == SegmentEnvironment.SurfaceStationary) ||
                (from == SegmentEnvironment.SurfaceStationary && to == SegmentEnvironment.SurfaceMobile))
                return SurfaceSpeedDebounceSeconds;

            // Surface/atmospheric boundary bounce (EVA Kerbals hopping): 0.5s debounce
            if ((from == SegmentEnvironment.SurfaceMobile && to == SegmentEnvironment.Atmospheric) ||
                (from == SegmentEnvironment.SurfaceStationary && to == SegmentEnvironment.Atmospheric) ||
                (from == SegmentEnvironment.Atmospheric && to == SegmentEnvironment.SurfaceMobile) ||
                (from == SegmentEnvironment.Atmospheric && to == SegmentEnvironment.SurfaceStationary))
                return SurfaceAtmosphericDebounceSeconds;

            // All other transitions: no debounce (immediate)
            return 0.0;
        }
    }
}
