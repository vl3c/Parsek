using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Pure static classification of vessel environment state.
    /// No state, no Vessel dependency — directly testable with primitive parameters.
    /// </summary>
    internal static class EnvironmentDetector
    {
        internal const double AtmosphericEvaNearSurfaceMeters = 5.0;
        internal const double AtmosphericEvaNearSeaLevelMeters = 1.0;

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
        /// <param name="approachAltitude">approach altitude threshold for airless bodies (0 = not applicable)</param>
        /// <param name="isEva">true for Kerbal EVA vessels only</param>
        /// <param name="heightFromTerrain">vessel.heightFromTerrain when available</param>
        /// <param name="heightFromTerrainValid">true when heightFromTerrain comes from a valid ground query</param>
        /// <param name="hasOcean">body.ocean</param>
        internal static SegmentEnvironment Classify(
            bool hasAtmosphere,
            double altitude,
            double atmosphereDepth,
            int situation,
            double srfSpeed,
            bool hasActiveThrust,
            double approachAltitude = 0,
            bool isEva = false,
            double heightFromTerrain = -1,
            bool heightFromTerrainValid = false,
            bool hasOcean = false)
        {
            // Landed/Splashed/Prelaunch -> surface states
            // situation == 1 (LANDED) or situation == 2 (SPLASHED) or situation == 4 (PRELAUNCH)
            if (situation == 1 || situation == 2 || situation == 4)
            {
                return srfSpeed > 0.1
                    ? SegmentEnvironment.SurfaceMobile
                    : SegmentEnvironment.SurfaceStationary;
            }

            // Kerbin/Laythe EVA vessels can briefly report FLYING/SUB_ORBITAL while the
            // kerbal is still effectively on the ground. Keep this narrow to avoid
            // turning actual jetpack flight into a surface segment.
            if (isEva && hasAtmosphere && heightFromTerrainValid &&
                heightFromTerrain <= AtmosphericEvaNearSurfaceMeters)
            {
                return srfSpeed > 0.1
                    ? SegmentEnvironment.SurfaceMobile
                    : SegmentEnvironment.SurfaceStationary;
            }

            // Swimming / bobbing EVA kerbals on atmospheric ocean worlds can jitter out of
            // SPLASHED while heightFromTerrain still measures the seafloor, not the water
            // surface. Treat sea-level-adjacent EVA as surface as well.
            if (isEva && hasAtmosphere && hasOcean &&
                altitude <= AtmosphericEvaNearSeaLevelMeters)
            {
                return srfSpeed > 0.1
                    ? SegmentEnvironment.SurfaceMobile
                    : SegmentEnvironment.SurfaceStationary;
            }

            // Airless body near-surface override: KSP's EVA physics jitter causes the vessel
            // situation to briefly flip from LANDED to FLYING/SUB_ORBITAL during walks and hops.
            // At very low altitude on an airless body, force Surface classification regardless
            // of the transient situation flag. 100m AGL covers EVA jetpack hops (~20m typical).
            if (!hasAtmosphere && approachAltitude > 0 && altitude < 100.0 && situation != 32)
            {
                return srfSpeed > 0.1
                    ? SegmentEnvironment.SurfaceMobile
                    : SegmentEnvironment.SurfaceStationary;
            }

            // Atmospheric is altitude-only — KSP's on-rails propagator ignores drag, so a
            // packed vessel with Pe inside atmosphere classifies as Atmospheric here even
            // though it does not decelerate. Callers that route this into TrackSection
            // emission must gate on "off rails" (see `BackgroundRecorder.OnBackgroundPhysicsFrame`'s
            // `bgVessel.packed` early-return and `FlightRecorder.OnPhysicsFrame`'s `isOnRails`
            // early-return) to avoid spurious atmo<->exo splits per orbit on grazing-Pe coasts.
            if (hasAtmosphere && altitude < atmosphereDepth)
                return SegmentEnvironment.Atmospheric;

            // Airless body: below approach altitude = Approach zone.
            // Intentionally above the thrust check — a powered descent on the Mun is still
            // "approach" for splitting purposes (we don't want engine on/off to fragment the
            // landing segment). Thrust distinction only matters in high orbit.
            // Exclude ORBITING (32) — a stable low orbit is Keplerian, not an approach.
            if (!hasAtmosphere && approachAltitude > 0 && altitude < approachAltitude
                && situation != 32)
                return SegmentEnvironment.Approach;

            if (hasActiveThrust)
                return SegmentEnvironment.ExoPropulsive;

            return SegmentEnvironment.ExoBallistic;
        }

        /// <summary>
        /// Returns true if the SegmentEnvironment value represents a surface state
        /// (SurfaceMobile or SurfaceStationary). Pure helper used by anchor / surface
        /// guards that previously relied on raw vessel.situation, which is jittery
        /// during EVA on airless bodies.
        /// </summary>
        internal static bool IsSurfaceEnvironment(SegmentEnvironment env)
            => env == SegmentEnvironment.SurfaceMobile
            || env == SegmentEnvironment.SurfaceStationary;

        /// <summary>
        /// Determines whether a vessel should be treated as "on the surface" for anchor
        /// detection / surface-only behaviors. Prefers the debounced environment
        /// classification (which has hysteresis to filter EVA jitter — see #246) when
        /// available; falls back to the raw KSP situation enum when no environment
        /// classifier is initialized (defensive — early in StartRecording, etc.).
        /// Pass <c>envHint = null</c> when the caller has no debounced classification.
        /// </summary>
        /// <param name="envHint">debounced environment classification, or null to use the situation fallback</param>
        /// <param name="situation">(int)vessel.situation (LANDED=1, SPLASHED=2, PRELAUNCH=4)</param>
        internal static bool IsSurfaceForAnchorDetection(SegmentEnvironment? envHint, int situation)
        {
            if (envHint.HasValue)
                return IsSurfaceEnvironment(envHint.Value);
            return situation == 1 || situation == 2 || situation == 4;
        }

        internal static bool IsHeightFromTerrainValid(double heightFromTerrain)
            => !double.IsNaN(heightFromTerrain) && heightFromTerrain >= 0.0;
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
        internal const double ApproachDebounceSeconds = 3.0;

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
                    hasPending = false;
                return false;
            }

            double requiredDebounce = GetDebounceFor(lastConfirmedEnvironment, rawClassification);

            if (!hasPending || pendingEnvironment != rawClassification)
            {
                // Start new pending transition
                pendingEnvironment = rawClassification;
                pendingStartUT = ut;
                hasPending = true;

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
            // Thrust toggle: 1s debounce (within same altitude zone)
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

            // Approach zone boundary on airless bodies
            if ((from == SegmentEnvironment.Approach && (to == SegmentEnvironment.ExoBallistic || to == SegmentEnvironment.ExoPropulsive)) ||
                ((from == SegmentEnvironment.ExoBallistic || from == SegmentEnvironment.ExoPropulsive) && to == SegmentEnvironment.Approach))
                return ApproachDebounceSeconds;

            // Approach/surface bounce on airless bodies (rough landings, EVA hopping).
            // 3.0s matches SurfaceSpeedDebounceSeconds — EVA physics jitter on the Mun
            // can produce situation flips lasting ~1-2s during walks and jetpack hops.
            if ((from == SegmentEnvironment.Approach && (to == SegmentEnvironment.SurfaceMobile || to == SegmentEnvironment.SurfaceStationary)) ||
                ((from == SegmentEnvironment.SurfaceMobile || from == SegmentEnvironment.SurfaceStationary) && to == SegmentEnvironment.Approach))
                return ApproachDebounceSeconds;

            // All other transitions: no debounce (immediate)
            return 0.0;
        }
    }
}
