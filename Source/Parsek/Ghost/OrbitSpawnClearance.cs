using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>What the orbit-route spawn clearance did with a spawn point.</summary>
    internal enum OrbitSpawnClearanceOutcome
    {
        /// <summary>The spawn point was clear; nothing changed.</summary>
        Clear = 0,
        /// <summary>Moved a little ahead or behind along the vessel's own orbit.</summary>
        PhaseShifted = 1,
        /// <summary>Pushed straight out from an asteroid or comet and given its velocity.</summary>
        RockPushedOut = 2,
        /// <summary>No clear spot within the bound: the caller's blocked outcome applies.</summary>
        Blocked = 3,
    }

    /// <summary>
    /// Result of an orbit-route spawn clearance. <see cref="Orbit"/>, <see cref="WorldPos"/> and
    /// <see cref="VelocityZup"/> describe the place the vessel spawns at (the input orbit when the
    /// outcome is <see cref="OrbitSpawnClearanceOutcome.Clear"/>); the velocity is the
    /// body-relative Zup orbital velocity, the frame <c>Orbit.getOrbitalVelocityAtUT</c> returns.
    /// </summary>
    internal struct OrbitSpawnClearanceResult
    {
        public OrbitSpawnClearanceOutcome Outcome;
        public Orbit Orbit;
        public Vector3d WorldPos;
        public Vector3d VelocityZup;
        public string BlockerName;
        public double ShiftMeters;
        public double ShiftSeconds;
        public double PushOutMeters;
        public int Attempts;

        /// <summary>True when the spawn may go ahead at <see cref="WorldPos"/>.</summary>
        public bool CanSpawn
        {
            get { return Outcome != OrbitSpawnClearanceOutcome.Blocked; }
        }

        /// <summary>True when the spawn state differs from the propagated terminal orbit.</summary>
        public bool StateChanged
        {
            get
            {
                return Outcome == OrbitSpawnClearanceOutcome.PhaseShifted
                    || Outcome == OrbitSpawnClearanceOutcome.RockPushedOut;
            }
        }
    }

    /// <summary>Result of one bounded clearance search (<see cref="OrbitSpawnClearance"/>).</summary>
    internal struct OrbitSpawnClearanceSearch
    {
        public bool Found;
        public double Value;
        public int Attempts;
    }

    /// <summary>
    /// Pure decisions and orbit builds behind the orbit-route spawn clearance (owner ruling
    /// 2026-11-21, todo SPAWN-ORBITAL-WALKBACK-NOT-WHERE-IT-SPAWNS / SPAWN-RESTING-ON-ROCK).
    /// A vessel whose spawn goes through the recorded terminal orbit and would appear
    /// overlapping another vessel moves a little ahead or behind along its OWN orbit (same
    /// elements, a small mean-anomaly offset at the spawn epoch) until its bounds are clear. A
    /// spawn overlapping an asteroid or comet is pushed straight out from the rock's centre
    /// until clear by a small margin and takes the rock's velocity, so it co-moves with the
    /// rock. Everything here is either pure or builds a stock <c>Orbit</c> in one synchronous
    /// pass at one UT (the <c>Planetarium.Zup</c> contract); the live overlap tests are passed
    /// in as predicates.
    /// </summary>
    internal static class OrbitSpawnClearance
    {
        private const string Tag = "SpawnCollision";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>First along-track offset tried, in metres (each direction).</summary>
        internal const double PhaseShiftFirstStepMeters = 4.0;

        /// <summary>Growth of the along-track offset between candidate pairs.</summary>
        internal const double PhaseShiftGrowthFactor = 1.5;

        /// <summary>
        /// Largest along-track offset tried, in metres (about 0.45 s of low Kerbin orbit).
        /// Beyond it the blocker is not a neighbour the spawn can step past, and the caller's
        /// blocked outcome applies.
        /// </summary>
        internal const double PhaseShiftMaxMeters = 1000.0;

        /// <summary>Radial step of the push-out from a rock, in metres.</summary>
        internal const double RockPushOutStepMeters = 0.5;

        /// <summary>Clearance left between the spawn bounds and the rock after a push-out.</summary>
        internal const float RockPushOutMarginMeters = 1.0f;

        /// <summary>
        /// Pure: the signed along-track offsets (metres) the phase search tries, nearest first,
        /// ahead before behind: +first, -first, +first*g, -first*g, ... up to
        /// <paramref name="maxMeters"/> (the last magnitude is clamped to it). Empty for a
        /// non-positive first step or bound; a growth at or below 1 is read as 2.
        /// </summary>
        internal static List<double> BuildPhaseShiftScheduleMeters(
            double firstStepMeters, double growthFactor, double maxMeters)
        {
            var schedule = new List<double>();
            if (!(firstStepMeters > 0.0) || !(maxMeters > 0.0)
                || double.IsInfinity(firstStepMeters) || double.IsInfinity(maxMeters))
                return schedule;
            double growth = growthFactor > 1.0 && !double.IsInfinity(growthFactor) ? growthFactor : 2.0;
            double magnitude = Math.Min(firstStepMeters, maxMeters);
            while (true)
            {
                schedule.Add(magnitude);
                schedule.Add(-magnitude);
                if (magnitude >= maxMeters)
                    break;
                magnitude = Math.Min(magnitude * growth, maxMeters);
            }
            return schedule;
        }

        /// <summary>
        /// Pure: the mean anomaly at the spawn epoch that puts the vessel
        /// <paramref name="shiftMeters"/> along its own orbit (positive ahead), with the time
        /// that offset stands for. The along-track distance over the orbital speed is the time
        /// offset; the mean motion turns it into mean anomaly
        /// (<see cref="TrajectoryMath.TryComputeCoOrbitalMeanAnomalyShift"/>). A closed orbit wraps
        /// into [0, 2 pi); an open conic grows without wrapping. False for a degenerate orbit.
        /// </summary>
        internal static bool TryComputeShiftedMeanAnomaly(
            double meanAnomalyAtEpoch, double eccentricity, double semiMajorAxis,
            double gravParameter, double orbitalSpeed, double shiftMeters,
            out double shiftedMeanAnomaly, out double shiftSeconds)
        {
            shiftedMeanAnomaly = meanAnomalyAtEpoch;
            shiftSeconds = 0.0;
            if (double.IsNaN(meanAnomalyAtEpoch) || double.IsInfinity(meanAnomalyAtEpoch))
                return false;
            if (!TrajectoryMath.TryComputeCoOrbitalMeanAnomalyShift(
                    shiftMeters, orbitalSpeed, semiMajorAxis, gravParameter, out double shift))
                return false;
            shiftSeconds = shiftMeters / orbitalSpeed;
            double shifted = meanAnomalyAtEpoch + shift;
            bool closed = eccentricity < 1.0 && semiMajorAxis > 0.0;
            shiftedMeanAnomaly = closed ? WrapClosedOrbitMeanAnomaly(shifted) : shifted;
            return true;
        }

        /// <summary>Pure: a closed orbit's mean anomaly in [0, 2 pi).</summary>
        internal static double WrapClosedOrbitMeanAnomaly(double meanAnomaly)
        {
            const double twoPi = 2.0 * Math.PI;
            double wrapped = meanAnomaly % twoPi;
            if (wrapped < 0.0)
                wrapped += twoPi;
            return wrapped;
        }

        /// <summary>
        /// Pure: walks <paramref name="scheduleMeters"/> in order and returns the first offset
        /// whose candidate <paramref name="isClear"/> accepts, with the number of candidates
        /// tried. Not found when every candidate is blocked or the schedule is empty.
        /// </summary>
        internal static OrbitSpawnClearanceSearch SearchPhaseShift(
            IList<double> scheduleMeters, Func<double, bool> isClear)
        {
            var result = new OrbitSpawnClearanceSearch();
            if (scheduleMeters == null || isClear == null)
                return result;
            for (int i = 0; i < scheduleMeters.Count; i++)
            {
                result.Attempts++;
                if (isClear(scheduleMeters[i]))
                {
                    result.Found = true;
                    result.Value = scheduleMeters[i];
                    return result;
                }
            }
            return result;
        }

        /// <summary>
        /// Pure: steps outward from a rock's centre, from <paramref name="startDistance"/> in
        /// <paramref name="stepMeters"/> increments, and returns the first distance whose
        /// <paramref name="isClear"/> accepts. The last candidate is exactly
        /// <paramref name="maxDistance"/> (at least one step out from the start); not found when
        /// it is still blocked.
        /// </summary>
        internal static OrbitSpawnClearanceSearch SearchPushOutDistance(
            double startDistance, double stepMeters, double maxDistance, Func<double, bool> isClear)
        {
            var result = new OrbitSpawnClearanceSearch();
            if (isClear == null || !(stepMeters > 0.0) || double.IsNaN(startDistance)
                || double.IsNaN(maxDistance) || double.IsInfinity(maxDistance))
                return result;
            double distance = Math.Max(0.0, startDistance);
            double bound = Math.Max(maxDistance, distance + stepMeters);
            while (true)
            {
                distance = Math.Min(distance + stepMeters, bound);
                result.Attempts++;
                if (isClear(distance))
                {
                    result.Found = true;
                    result.Value = distance;
                    return result;
                }
                if (distance >= bound)
                    return result;
            }
        }

        /// <summary>
        /// Pure: radius (metres) of a sphere around the vessel origin that holds the spawn
        /// bounds: the distance to the bounds centre plus the half diagonal.
        /// </summary>
        internal static double ResolveBoundsRadius(Bounds spawnBounds)
        {
            return ((Vector3d)spawnBounds.center).magnitude + ((Vector3d)spawnBounds.extents).magnitude;
        }

        /// <summary>
        /// Pure: the distance from a rock's centre at which the spawn bounds clear a rock of
        /// <paramref name="rockRadius"/> by <paramref name="marginMeters"/>.
        /// </summary>
        internal static double ResolveRockClearanceDistance(
            double rockRadius, double boundsRadius, double marginMeters)
        {
            return Math.Max(0.0, rockRadius) + Math.Max(0.0, boundsRadius) + Math.Max(0.0, marginMeters);
        }

        /// <summary>
        /// Pure: the unit direction a spawn is pushed along, straight out from the rock's centre
        /// through the spawn point. A spawn at the centre (closer than 1 cm) takes
        /// <paramref name="fallbackDirection"/> (the caller passes radial out from the body);
        /// zero when that is degenerate too.
        /// </summary>
        internal static Vector3d ResolvePushOutDirection(
            Vector3d rockCentre, Vector3d spawnPos, Vector3d fallbackDirection)
        {
            Vector3d offset = spawnPos - rockCentre;
            double length = offset.magnitude;
            if (length >= 0.01 && !double.IsNaN(length) && !double.IsInfinity(length))
                return offset / length;
            double fallbackLength = fallbackDirection.magnitude;
            if (fallbackLength >= 1e-9 && !double.IsNaN(fallbackLength) && !double.IsInfinity(fallbackLength))
                return fallbackDirection / fallbackLength;
            return Vector3d.zero;
        }

        /// <summary>
        /// Builds the vessel's own orbit with the spawn epoch's mean anomaly moved
        /// <paramref name="shiftMeters"/> along it (<see cref="TryComputeShiftedMeanAnomaly"/>),
        /// and the position and Zup orbital velocity it gives at <paramref name="ut"/>. Every
        /// other element and the epoch stay the base orbit's. False for a degenerate orbit or a
        /// non-finite state.
        /// </summary>
        internal static bool TryBuildPhaseShiftedOrbit(
            Orbit baseOrbit, double ut, double shiftMeters,
            out Orbit shifted, out Vector3d worldPos, out Vector3d velocityZup, out double shiftSeconds)
        {
            shifted = null;
            worldPos = Vector3d.zero;
            velocityZup = Vector3d.zero;
            shiftSeconds = 0.0;
            if (baseOrbit == null || baseOrbit.referenceBody == null)
                return false;

            // The mean anomaly of the base orbit at ut, so the shift is measured from the spawn
            // point whatever epoch the base orbit carries.
            double speed = baseOrbit.getOrbitalVelocityAtUT(ut).magnitude;
            double baseMeanAnomaly = baseOrbit.meanAnomalyAtEpoch;
            if (Math.Abs(baseOrbit.epoch - ut) > 1e-9)
            {
                double absSma = Math.Abs(baseOrbit.semiMajorAxis);
                if (!(absSma > 0.0) || double.IsInfinity(absSma))
                    return false;
                baseMeanAnomaly += Math.Sqrt(
                    baseOrbit.referenceBody.gravParameter / (absSma * absSma * absSma))
                    * (ut - baseOrbit.epoch);
            }

            if (!TryComputeShiftedMeanAnomaly(
                    baseMeanAnomaly, baseOrbit.eccentricity, baseOrbit.semiMajorAxis,
                    baseOrbit.referenceBody.gravParameter, speed, shiftMeters,
                    out double shiftedMeanAnomaly, out shiftSeconds))
                return false;

            shifted = new Orbit(
                baseOrbit.inclination,
                baseOrbit.eccentricity,
                baseOrbit.semiMajorAxis,
                baseOrbit.LAN,
                baseOrbit.argumentOfPeriapsis,
                shiftedMeanAnomaly,
                ut,
                baseOrbit.referenceBody);
            worldPos = shifted.getPositionAtUT(ut);
            velocityZup = shifted.getOrbitalVelocityAtUT(ut);
            if (!IsFinite(worldPos) || !IsFinite(velocityZup))
            {
                shifted = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Builds the orbit of a vessel at <paramref name="worldPos"/> moving with
        /// <paramref name="velocityZup"/> (a body-relative Zup orbital velocity, the rock's) at
        /// <paramref name="ut"/>: the world offset from the body is swizzled into the Zup frame
        /// <c>Orbit.UpdateFromStateVectors</c> reads. False for a non-finite result.
        /// </summary>
        internal static bool TryBuildCoMovingOrbit(
            CelestialBody body, Vector3d worldPos, Vector3d velocityZup, double ut, out Orbit orbit)
        {
            orbit = null;
            if (body == null || !IsFinite(worldPos) || !IsFinite(velocityZup))
                return false;
            Vector3d relativeZup = TrajectoryMath.SwizzleZupBodyRelativeToWorld(worldPos - body.position);
            var built = new Orbit();
            built.UpdateFromStateVectors(relativeZup, velocityZup, body, ut);
            if (double.IsNaN(built.semiMajorAxis) || double.IsInfinity(built.semiMajorAxis)
                || double.IsNaN(built.eccentricity) || double.IsInfinity(built.eccentricity)
                || double.IsNaN(built.inclination) || double.IsNaN(built.LAN)
                || double.IsNaN(built.argumentOfPeriapsis) || double.IsNaN(built.meanAnomalyAtEpoch))
                return false;
            orbit = built;
            return true;
        }

        /// <summary>
        /// Logs the outcome of one clearance. A blocked spawn retries every frame or second,
        /// so the line is rate-limited per site, spawn and outcome: a changed outcome prints at
        /// once.
        /// </summary>
        internal static void LogOutcome(string site, string logContext, OrbitSpawnClearanceResult result)
        {
            ParsekLog.InfoRateLimited(Tag,
                "orbit-clearance|" + (site ?? "") + "|" + (logContext ?? "") + "|" + result.Outcome,
                string.Format(IC,
                "Orbit spawn clearance: site={0} {1} outcome={2} attempts={3} shift={4:F1}m " +
                "shiftSeconds={5:F3} pushOut={6:F1}m blocker='{7}'",
                site ?? "(none)",
                logContext ?? "(none)",
                result.Outcome,
                result.Attempts,
                result.ShiftMeters,
                result.ShiftSeconds,
                result.PushOutMeters,
                result.BlockerName ?? "(none)"));
        }

        private static bool IsFinite(Vector3d v)
        {
            return !double.IsNaN(v.x) && !double.IsNaN(v.y) && !double.IsNaN(v.z)
                && !double.IsInfinity(v.x) && !double.IsInfinity(v.y) && !double.IsInfinity(v.z);
        }
    }
}
