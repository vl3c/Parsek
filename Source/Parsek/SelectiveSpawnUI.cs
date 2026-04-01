using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Lightweight data for a nearby ghost craft eligible for real spawn.
    /// Built by the proximity scan in ParsekFlight, consumed by the Real Spawn Control window.
    /// </summary>
    internal struct NearbySpawnCandidate
    {
        public int recordingIndex;
        public string vesselName;
        public double endUT;
        public double distance;
        public string recordingId;
        public bool willDepart;        // ghost will leave current orbit before EndUT
        public double departureUT;     // UT when ghost departs (0 if !willDepart)
        public string destination;     // "Mun", "maneuver", etc.
    }

    /// <summary>
    /// Result of departure analysis for a ghost craft.
    /// Indicates whether the ghost will leave its current orbit before recording ends.
    /// </summary>
    internal struct DepartureInfo
    {
        public bool willDepart;
        public double departureUT;     // endUT of the current orbit segment (0 if !willDepart)
        public string destination;     // body name or "maneuver"
    }

    /// <summary>
    /// Pure static methods for the Real Spawn Control UI: determining which nearby
    /// ghost craft can be warped to for real-vessel interaction, and formatting UI text.
    ///
    /// The player approaches a ghost vessel and opens the Real Spawn Control window
    /// to fast-forward to the moment the ghost becomes a real craft. The window lists
    /// all nearby ghost craft sorted by distance, with per-craft warp buttons.
    /// </summary>
    internal static class SelectiveSpawnUI
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private static readonly System.Text.StringBuilder SharedSB = new System.Text.StringBuilder(64);

        // Kerbin time: 6-hour days (21600s), 426-day years
        // Earth time: 24-hour days (86400s), 365-day years
        private const int KerbinDaySec = 21600;
        private const int KerbinYearDays = 426;
        private const int EarthDaySec = 86400;
        private const int EarthYearDays = 365;

        /// <summary>
        /// Test hook: when non-null, overrides GameSettings.KERBIN_TIME for unit tests.
        /// </summary>
        internal static bool? KerbinTimeOverrideForTesting;

        internal static bool UseKerbinTime =>
            KerbinTimeOverrideForTesting ?? GameSettings.KERBIN_TIME;

        internal static void GetDayAndYearConstants(out int daySec, out int yearDays)
        {
            if (UseKerbinTime)
            {
                daySec = KerbinDaySec;
                yearDays = KerbinYearDays;
            }
            else
            {
                daySec = EarthDaySec;
                yearDays = EarthYearDays;
            }
        }

        /// <summary>
        /// Pure: determine whether a ghost qualifies as a spawn candidate.
        /// True when endUT is in the future, spawn is needed, not suppressed, and within range.
        /// </summary>
        internal static bool IsSpawnCandidate(
            double endUT, double currentUT,
            bool needsSpawn, bool chainSuppressed,
            double distance, double proximityRadius)
        {
            return endUT > currentUT
                && needsSpawn
                && !chainSuppressed
                && distance <= proximityRadius;
        }

        /// <summary>
        /// Pure: find the candidate with the earliest effective UT in the future.
        /// For departing candidates, the effective UT is departureUT (when the ghost leaves).
        /// For non-departing candidates, the effective UT is endUT (when it spawns).
        /// Returns null if no candidates qualify.
        /// </summary>
        internal static NearbySpawnCandidate? FindNextSpawnCandidate(
            List<NearbySpawnCandidate> candidates, double currentUT)
        {
            if (candidates == null || candidates.Count == 0)
                return null;

            NearbySpawnCandidate? best = null;
            double bestUT = double.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                double effectiveUT = c.willDepart ? c.departureUT : c.endUT;
                if (effectiveUT > currentUT && effectiveUT < bestUT)
                {
                    best = c;
                    bestUT = effectiveUT;
                }
            }
            return best;
        }

        /// <summary>
        /// Pure: format a time delta as human-readable string.
        /// Under 60s: "{s}s". Under 3600s: "{m}m {s}s". Otherwise: "{h}h {m}m".
        /// Uses Kerbin or Earth time based on GameSettings.KERBIN_TIME for day/year boundaries.
        /// (Currently only shows up to hours, so day length doesn't affect output.)
        /// </summary>
        internal static string FormatTimeDelta(double seconds)
        {
            if (seconds < 0) seconds = 0;

            if (seconds < 60)
                return string.Format(IC, "{0}s", ((long)seconds).ToString(IC));

            if (seconds < 3600)
            {
                long m = (long)(seconds / 60);
                long s = (long)(seconds % 60);
                return string.Format(IC, "{0}m {1}s", m.ToString(IC), s.ToString(IC));
            }

            long h = (long)(seconds / 3600);
            long min = (long)((seconds % 3600) / 60);
            return string.Format(IC, "{0}h {1}m", h.ToString(IC), min.ToString(IC));
        }

        /// <summary>
        /// Pure: format a countdown string "T-Xd Xh Xm Xs" from a time delta.
        /// Hides zero leading components (no years if 0, no days if 0, etc.).
        /// Uses Kerbin time (6h days, 426-day years) or Earth time (24h days, 365-day years)
        /// based on GameSettings.KERBIN_TIME.
        /// Returns "T+..." for negative deltas (event in the past).
        /// </summary>
        internal static string FormatCountdown(double deltaSeconds)
        {
            string prefix = "T-";
            if (deltaSeconds < 0)
            {
                prefix = "T+";
                deltaSeconds = -deltaSeconds;
            }

            GetDayAndYearConstants(out int daySec, out int yearDays);
            long yearSec = (long)yearDays * daySec;

            long totalSec = (long)deltaSeconds;
            long years = totalSec / yearSec;
            totalSec %= yearSec;
            long days = totalSec / daySec;
            totalSec %= daySec;
            long hours = totalSec / 3600;
            totalSec %= 3600;
            long minutes = totalSec / 60;
            long seconds = totalSec % 60;

            var parts = SharedSB;
            parts.Clear();
            parts.Append(prefix);
            bool started = false;

            if (years > 0)
            {
                parts.Append(years.ToString(IC));
                parts.Append("y ");
                started = true;
            }
            if (started || days > 0)
            {
                parts.Append(days.ToString(IC));
                parts.Append("d ");
                started = true;
            }
            if (started || hours > 0)
            {
                parts.Append(hours.ToString(IC));
                parts.Append("h ");
                started = true;
            }
            if (started || minutes > 0)
            {
                parts.Append(minutes.ToString(IC));
                parts.Append("m ");
            }
            parts.Append(seconds.ToString(IC));
            parts.Append('s');

            return parts.ToString();
        }

        /// <summary>
        /// Pure: format the tooltip for the "Warp to Next Spawn" button.
        /// Adjusts text when the next candidate will depart before spawn.
        /// </summary>
        internal static string FormatNextSpawnTooltip(
            NearbySpawnCandidate? candidate, double currentUT)
        {
            if (candidate == null)
                return "No nearby craft to spawn";

            var c = candidate.Value;
            if (c.willDepart)
            {
                double depDelta = c.departureUT - currentUT;
                return string.Format(IC,
                    "Warp to {0} departure (departs in {1})",
                    c.vesselName, FormatTimeDelta(depDelta));
            }

            double delta = c.endUT - currentUT;
            return string.Format(IC,
                "Warp to {0} (spawns in {1})",
                c.vesselName, FormatTimeDelta(delta));
        }

        // ════════════════════════════════════════════════════════════════
        //  Departure Detection
        // ════════════════════════════════════════════════════════════════

        // Tolerances for OrbitsMatch — tight enough that any intentional maneuver
        // is detected, loose enough to handle float noise from re-captured orbits.
        internal const double SmaRelativeTolerance = 0.001;      // 0.1%
        internal const double EccAbsoluteTolerance = 0.0001;
        internal const double IncDegreeTolerance = 0.01;
        internal const double ArgPeDegreeTolerance = 1.0;
        internal const double EccThresholdForArgPe = 0.01;       // skip argPe below this

        /// <summary>
        /// Pure: compare two orbit segments for functional equivalence.
        /// Checks body, SMA, eccentricity, inclination, and argument of periapsis
        /// (argPe only for eccentric orbits where it is physically meaningful).
        /// LAN and mean anomaly are NOT compared — they are time-dependent.
        /// </summary>
        internal static bool OrbitsMatch(OrbitSegment a, OrbitSegment b)
        {
            // Body must match
            if (a.bodyName != b.bodyName) return false;

            // SMA: relative tolerance using absolute values (handles negative SMA for hyperbolic)
            double absA = System.Math.Abs(a.semiMajorAxis);
            double absB = System.Math.Abs(b.semiMajorAxis);
            double maxAbs = System.Math.Max(absA, absB);
            if (maxAbs > 0 && System.Math.Abs(a.semiMajorAxis - b.semiMajorAxis) / maxAbs > SmaRelativeTolerance)
                return false;

            // Eccentricity: absolute tolerance
            if (System.Math.Abs(a.eccentricity - b.eccentricity) > EccAbsoluteTolerance)
                return false;

            // Inclination: degree tolerance
            if (System.Math.Abs(a.inclination - b.inclination) > IncDegreeTolerance)
                return false;

            // Argument of periapsis: only for eccentric orbits (> 0.01) where it matters
            if (System.Math.Max(a.eccentricity, b.eccentricity) > EccThresholdForArgPe)
            {
                double argPeDiff = System.Math.Abs(a.argumentOfPeriapsis - b.argumentOfPeriapsis);
                // Wrap around 360 degrees
                if (argPeDiff > 180.0) argPeDiff = 360.0 - argPeDiff;
                if (argPeDiff > ArgPeDegreeTolerance)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Pure: determine whether a ghost will depart its current orbit before the recording ends.
        /// Takes minimal data (not a full Recording) for testability and interface independence.
        ///
        /// Resolution cascade for the "final orbit" to compare against:
        /// 1. Orbit segment covering endUT
        /// 2. Terminal orbit fields (if body is non-empty and SMA != 0)
        /// 3. Last orbit segment in the list (covers recordings ending in off-rails phase)
        /// </summary>
        internal static DepartureInfo ComputeDepartureInfo(
            System.Collections.Generic.List<OrbitSegment> orbitSegments, double endUT,
            string terminalOrbitBody, double terminalOrbitSMA,
            double terminalOrbitEcc, double terminalOrbitInc,
            double terminalOrbitArgPe,
            TerminalState? terminalState,
            double currentUT)
        {
            var noDeparture = new DepartureInfo { willDepart = false };

            if (orbitSegments == null || orbitSegments.Count == 0)
                return noDeparture;

            // Find current orbit segment
            OrbitSegment? currentSeg = TrajectoryMath.FindOrbitSegment(orbitSegments, currentUT);
            if (!currentSeg.HasValue)
                return noDeparture;  // off-rails / atmospheric — can't detect departure

            OrbitSegment current = currentSeg.Value;

            // Special case: terminal state is surface (Landed/Splashed/Destroyed) with no
            // orbit segment covering EndUT → ghost is orbiting now but will land/crash
            bool isSurfaceTerminal = terminalState.HasValue &&
                (terminalState.Value == TerminalState.Landed ||
                 terminalState.Value == TerminalState.Splashed ||
                 terminalState.Value == TerminalState.Destroyed);

            // Resolution cascade for the final orbit
            OrbitSegment? finalSeg = TrajectoryMath.FindOrbitSegment(orbitSegments, endUT);
            OrbitSegment finalOrbit;
            bool hasFinalOrbit;

            if (finalSeg.HasValue)
            {
                finalOrbit = finalSeg.Value;
                hasFinalOrbit = true;
            }
            else if (!string.IsNullOrEmpty(terminalOrbitBody) && terminalOrbitSMA != 0)
            {
                // Build from terminal orbit fields
                finalOrbit = new OrbitSegment
                {
                    bodyName = terminalOrbitBody,
                    semiMajorAxis = terminalOrbitSMA,
                    eccentricity = terminalOrbitEcc,
                    inclination = terminalOrbitInc,
                    argumentOfPeriapsis = terminalOrbitArgPe
                };
                hasFinalOrbit = true;
            }
            else if (isSurfaceTerminal)
            {
                // Ghost will land/crash — definite departure from current orbit
                return new DepartureInfo
                {
                    willDepart = true,
                    departureUT = current.endUT,
                    destination = current.bodyName ?? "surface"
                };
            }
            else
            {
                // Fallback: use last segment in list (recording ends in off-rails phase)
                finalOrbit = orbitSegments[orbitSegments.Count - 1];
                hasFinalOrbit = true;
            }

            if (!hasFinalOrbit)
                return noDeparture;

            if (OrbitsMatch(current, finalOrbit))
                return noDeparture;

            // Orbits differ — ghost will depart
            string destination;
            if (current.bodyName != finalOrbit.bodyName)
                destination = finalOrbit.bodyName ?? "unknown";
            else
                destination = "maneuver";

            return new DepartureInfo
            {
                willDepart = true,
                departureUT = current.endUT,
                destination = destination
            };
        }

        /// <summary>
        /// Convenience overload taking a Recording directly.
        /// Extracts the minimal fields needed for ComputeDepartureInfo.
        /// </summary>
        internal static DepartureInfo ComputeDepartureInfo(Recording rec, double currentUT)
        {
            if (rec == null)
                return new DepartureInfo { willDepart = false };

            return ComputeDepartureInfo(
                rec.OrbitSegments, rec.EndUT,
                rec.TerminalOrbitBody, rec.TerminalOrbitSemiMajorAxis,
                rec.TerminalOrbitEccentricity, rec.TerminalOrbitInclination,
                rec.TerminalOrbitArgumentOfPeriapsis,
                rec.TerminalStateValue,
                currentUT);
        }
    }
}
