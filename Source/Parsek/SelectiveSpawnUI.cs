using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

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
        public double relativeSpeed;   // m/s; double.PositiveInfinity when no valid second sample yet
        public string recordingId;
        public bool willDepart;        // ghost will leave current orbit before EndUT
        public double departureUT;     // UT when ghost departs (0 if !willDepart)
        public DepartureKind departureKind; // where it goes when it leaves (None if !willDepart)
        public string destination;     // the body named by departureKind (null if !willDepart)
    }

    /// <summary>
    /// Where a departing ghost goes when it leaves its current orbit. Player wording comes
    /// from <see cref="SelectiveSpawnUI.FormatDepartureDestination"/>, never from the enum
    /// name.
    /// </summary>
    internal enum DepartureKind
    {
        /// <summary>Not departing.</summary>
        None,
        /// <summary>Into another body's sphere of influence; destination names that body.</summary>
        OtherBody,
        /// <summary>A different orbit around the same body; destination names that body.</summary>
        NewOrbit,
        /// <summary>Down to land or splash down on the body it orbits now.</summary>
        Landing,
        /// <summary>Down to be destroyed on the body it orbits now.</summary>
        Crash
    }

    /// <summary>
    /// Result of departure analysis for a ghost craft.
    /// Indicates whether the ghost will leave its current orbit before recording ends.
    /// </summary>
    internal struct DepartureInfo
    {
        public bool willDepart;
        public double departureUT;     // endUT of the current orbit segment (0 if !willDepart)
        public DepartureKind kind;     // None when !willDepart
        public string destination;     // the body the kind refers to; null when unknown
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

        /// <summary>
        /// Test hook: delegates to ParsekTimeFormat.KerbinTimeOverrideForTesting.
        /// Kept for backward compatibility with existing tests.
        /// </summary>
        internal static bool? KerbinTimeOverrideForTesting
        {
            get => ParsekTimeFormat.KerbinTimeOverrideForTesting;
            set => ParsekTimeFormat.KerbinTimeOverrideForTesting = value;
        }

        internal static bool UseKerbinTime => ParsekTimeFormat.UseKerbinTime;

        internal static void GetDayAndYearConstants(out int daySec, out int yearDays)
            => ParsekTimeFormat.GetDayAndYearConstants(out daySec, out yearDays);

        /// <summary>
        /// Pure: determine whether a ghost qualifies as a spawn candidate.
        /// True when endUT is in the future, spawn is needed, not suppressed,
        /// within range, and the surface-frame relative speed is within tolerance.
        /// </summary>
        internal static bool IsSpawnCandidate(
            double endUT, double currentUT,
            bool needsSpawn, bool chainSuppressed,
            double distance, double proximityRadius,
            double relativeSpeed, double maxRelativeSpeed)
        {
            return endUT > currentUT
                && needsSpawn
                && !chainSuppressed
                && distance <= proximityRadius
                && relativeSpeed <= maxRelativeSpeed;
        }

        /// <summary>
        /// Pure: derive the relative speed (m/s) between active vessel and ghost from two
        /// position samples taken `dt` seconds apart. Frame-agnostic: any uniform shift
        /// (floating-origin, krakensbane) cancels in the per-sample relative vector.
        /// Returns +infinity when dt is outside [minDt, maxDt] - the sample is either
        /// jitter-dominated (too short) or stale (time warp / scene change).
        /// </summary>
        internal static double ComputeRelativeSpeed(
            Vector3d activePos, Vector3d ghostPos,
            Vector3d prevActivePos, Vector3d prevGhostPos,
            float dt, float minDt, float maxDt)
        {
            if (dt < minDt || dt > maxDt)
                return double.PositiveInfinity;
            Vector3d nowRel = activePos - ghostPos;
            Vector3d prevRel = prevActivePos - prevGhostPos;
            return (nowRel - prevRel).magnitude / dt;
        }

        /// <summary>
        /// Pure: the moment a candidate's Warp acts on. A craft that leaves its orbit before
        /// it would spawn is warped to its departure (<c>departureUT</c>; it does not spawn
        /// here); any other craft to its spawn (<c>endUT</c>). The window's time cells and
        /// its Spawns sort read this same value, so a row sorts where its button goes.
        /// </summary>
        internal static double EffectiveWarpUT(NearbySpawnCandidate c)
        {
            return c.willDepart ? c.departureUT : c.endUT;
        }

        /// <summary>
        /// Pure: find the candidate with the earliest effective UT in the future.
        /// For departing candidates, the effective UT is departureUT (when the ghost leaves).
        /// For non-departing candidates, the effective UT is endUT (when it spawns).
        /// Skips candidates whose distance exceeds proximityRadius or whose relative speed
        /// exceeds maxRelativeSpeed - the list keeps too-fast ghosts (greyed "Too fast"
        /// rows), but only candidates inside both spawn gates are warp-eligible.
        /// Returns null if no candidates qualify.
        /// </summary>
        internal static NearbySpawnCandidate? FindNextSpawnCandidate(
            List<NearbySpawnCandidate> candidates, double currentUT,
            double proximityRadius, double maxRelativeSpeed)
        {
            if (candidates == null || candidates.Count == 0)
                return null;

            NearbySpawnCandidate? best = null;
            double bestUT = double.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.distance > proximityRadius)
                    continue;
                if (c.relativeSpeed > maxRelativeSpeed)
                    continue;
                // The > currentUT filter also skips departing candidates whose departure is
                // already in the past.
                double effectiveUT = EffectiveWarpUT(c);
                if (effectiveUT > currentUT && effectiveUT < bestUT)
                {
                    best = c;
                    bestUT = effectiveUT;
                }
            }
            return best;
        }

        /// <summary>
        /// Pure: whether a ghost the proximity scan sampled belongs in the Real Spawn
        /// Control list. Distance is the spawn gate itself: a ghost farther than
        /// <paramref name="spawnRadius"/> cannot be spawned from here, so it is not listed
        /// at all. A ghost inside the radius but passing faster than the spawn gate is
        /// listed (its row reads "Too fast", which closing the speed fixes), up to
        /// <paramref name="maxListRelativeSpeed"/>.
        /// </summary>
        internal static bool IsListedCandidate(
            double distance, double relativeSpeed,
            double spawnRadius, double maxListRelativeSpeed)
        {
            return distance <= spawnRadius && relativeSpeed <= maxListRelativeSpeed;
        }

        /// <summary>
        /// Pure: where a departing ghost goes, as the words that follow "leaves orbit"
        /// ("for Mun", "for a new orbit", "to land on Kerbin", "to come down on Kerbin").
        /// Empty for <see cref="DepartureKind.None"/>.
        /// </summary>
        internal static string FormatDepartureDestination(DepartureKind kind, string body)
        {
            bool hasBody = !string.IsNullOrEmpty(body);
            switch (kind)
            {
                case DepartureKind.OtherBody:
                    return hasBody ? "for " + body : "for another body";
                case DepartureKind.NewOrbit:
                    return "for a new orbit";
                case DepartureKind.Landing:
                    return hasBody ? "to land on " + body : "to land";
                case DepartureKind.Crash:
                    return hasBody ? "to come down on " + body : "to come down";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Pure: format the one-shot proximity screen message for a newly discovered nearby
        /// ghost craft.
        /// <para><paramref name="spawnControlReachable"/> decides only whether the message
        /// ends with the "open Real Spawn Control" instruction. Basic UI mode hides that
        /// window's launcher, and a 10-second on-screen instruction to open a window with no
        /// button is worse than saying nothing - so Basic gets the observation without the
        /// dead-end call to action. The caller supplies the flag as a plain bool (the UI
        /// coordinator owns the reachability question) so this stays pure and mode-blind - which
        /// the complexity-mode grep gate enforces: this file may not name that vocabulary.</para>
        /// </summary>
        internal static string FormatProximityNotification(
            NearbySpawnCandidate candidate, double currentUT, bool spawnControlReachable)
        {
            if (candidate.willDepart)
            {
                string departure = string.Format(IC,
                    "Nearby craft: {0} (leaves orbit {1} in {2}).",
                    candidate.vesselName,
                    FormatDepartureDestination(candidate.departureKind, candidate.destination),
                    ParsekTimeFormat.FormatDuration(candidate.departureUT - currentUT));
                return spawnControlReachable
                    ? departure + " Open Real Spawn Control."
                    : departure;
            }

            return spawnControlReachable
                ? string.Format(IC,
                    "Nearby craft: {0}. Open the Real Spawn Control window to fast forward and interact.",
                    candidate.vesselName)
                : string.Format(IC, "Nearby craft: {0}.", candidate.vesselName);
        }

        /// <summary>
        /// Pure: format the tooltip for the "Warp to Next Spawn" button. A craft that
        /// leaves its orbit before it would spawn is warped to just before it leaves, and
        /// the hover says it does not spawn here.
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
                    "Warps to just before {0} leaves orbit in {1}; it does not spawn here.",
                    c.vesselName, ParsekTimeFormat.FormatDuration(depDelta));
            }

            double delta = c.endUT - currentUT;
            return string.Format(IC,
                "Warps to when {0} spawns here, in {1}.",
                c.vesselName, ParsekTimeFormat.FormatDuration(delta));
        }

        // ================================================================
        //  Departure Detection
        // ================================================================

        // Tolerances for OrbitsMatch - tight enough that any intentional maneuver
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
        /// LAN and mean anomaly are NOT compared - they are time-dependent.
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
            var noDeparture = new DepartureInfo { willDepart = false, kind = DepartureKind.None };

            if (orbitSegments == null || orbitSegments.Count == 0)
                return noDeparture;

            // Find current orbit segment
            OrbitSegment? currentSeg = TrajectoryMath.FindOrbitSegment(orbitSegments, currentUT);
            if (!currentSeg.HasValue)
                return noDeparture;  // off-rails / atmospheric - can't detect departure

            OrbitSegment current = currentSeg.Value;

            // Special case: terminal state is surface (Landed/Splashed/Destroyed) with no
            // orbit segment covering EndUT -> ghost is orbiting now but will land/crash
            bool isSurfaceTerminal = terminalState.HasValue &&
                (terminalState.Value == TerminalState.Landed ||
                 terminalState.Value == TerminalState.Splashed ||
                 terminalState.Value == TerminalState.Destroyed);

            // Resolution cascade for the final orbit
            OrbitSegment? finalSeg = TrajectoryMath.FindOrbitSegment(orbitSegments, endUT);
            OrbitSegment finalOrbit;

            if (finalSeg.HasValue)
            {
                finalOrbit = finalSeg.Value;
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
            }
            else if (isSurfaceTerminal)
            {
                // Ghost will land/crash - definite departure from current orbit
                // It comes down on the body it orbits now, so that body is where it
                // lands, not a body it departs to.
                return new DepartureInfo
                {
                    willDepart = true,
                    departureUT = current.endUT,
                    kind = terminalState.Value == TerminalState.Destroyed
                        ? DepartureKind.Crash
                        : DepartureKind.Landing,
                    destination = current.bodyName
                };
            }
            else
            {
                // Fallback: use last segment in list (recording ends in off-rails phase)
                finalOrbit = orbitSegments[orbitSegments.Count - 1];
            }

            if (OrbitsMatch(current, finalOrbit))
                return noDeparture;

            // Orbits differ - ghost will depart, either into another body's SOI or onto
            // a different orbit around the body it orbits now.
            bool otherBody = current.bodyName != finalOrbit.bodyName;
            return new DepartureInfo
            {
                willDepart = true,
                departureUT = current.endUT,
                kind = otherBody ? DepartureKind.OtherBody : DepartureKind.NewOrbit,
                destination = otherBody ? finalOrbit.bodyName : current.bodyName
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
