using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Parsek
{
    // Terminal-orbit spawn: the recorded orbit seed (stored segment or trajectory tail), mean anomaly at spawn, and the terminal-orbit spawn safety scan.
    public static partial class VesselSpawner
    {
        internal static bool HasRecordedTerminalOrbit(Recording rec)
        {
            return rec != null
                && !string.IsNullOrEmpty(rec.TerminalOrbitBody)
                && rec.TerminalOrbitSemiMajorAxis > 0.0;
        }

        /// <summary>
        /// Atmosphere depth the terminal-orbit spawn-safety deferral margin is built on. An
        /// airless body (or a body that could not be resolved) contributes no depth, so
        /// <see cref="TerminalOrbitSpawnSafety.ComputeSafeAltitude"/> also drops the margin
        /// and no deferral applies. The periapsis line on an airless body is its highest
        /// terrain (<see cref="OrbitClearance"/>), passed separately.
        /// </summary>
        internal static double ResolveSpawnSafetyAtmosphereDepth(
            bool bodyHasAtmosphere,
            double bodyAtmosphereDepth)
        {
            return bodyHasAtmosphere ? bodyAtmosphereDepth : 0.0;
        }

        /// <summary>
        /// Pure geometry half of <see cref="TryPassTerminalOrbitSpawnSafety"/>: normalize the
        /// body's atmosphere depth and run the shared safety predicate. Split out from the
        /// KSP-coupled wrapper so the decision can be driven headlessly with no
        /// <c>CelestialBody</c> / <c>Orbit</c> instance.
        /// </summary>
        internal static TerminalOrbitSpawnSafetyDecision EvaluateTerminalOrbitSpawnSafetyGeometry(
            bool bodyHasAtmosphere,
            double bodyAtmosphereDepth,
            double currentAltitude,
            double periapsisAltitude,
            double apoapsisAltitude,
            double maxTerrainAltitude = 0.0)
        {
            return TerminalOrbitSpawnSafety.Evaluate(
                currentAltitude,
                ResolveSpawnSafetyAtmosphereDepth(bodyHasAtmosphere, bodyAtmosphereDepth),
                TerminalOrbitSpawnSafety.DefaultSafetyMarginMeters,
                periapsisAltitude,
                apoapsisAltitude,
                maxTerrainAltitude);
        }

        /// <summary>
        /// Pure resolution applied when a <c>DeferUntilSafe</c> decision finds no future safe UT
        /// (the scan over one period missed the climb above the margin). A deferral is only
        /// reached once the periapsis already clears the periapsis floor, so the vessel is above
        /// the atmosphere all the way round: it spawns now, carrying
        /// <see cref="TerminalOrbitSpawnSafety.ReasonNoFutureSafeUT"/>, rather than being refused.
        /// </summary>
        internal static void ResolveTerminalOrbitDeferralWithNoFutureSafeUT(
            ref TerminalOrbitSpawnSafetyDecision decision,
            double currentAltitude)
        {
            decision.Action = TerminalOrbitSpawnSafetyAction.SpawnNow;
            decision.ReasonCode = TerminalOrbitSpawnSafety.ReasonNoFutureSafeUT;
            decision.Reason = string.Format(CultureInfo.InvariantCulture,
                "propagated altitude {0:F1}m is below safe altitude {1:F1}m and no future safe UT was found; " +
                "periapsis {2:F1}m clears the periapsis floor {3:F1}m, so the orbit spawns now",
                currentAltitude,
                decision.SafeAltitude,
                decision.PeriapsisAltitude,
                decision.PeriapsisFloorAltitude);
        }

        internal static bool TryPassTerminalOrbitSpawnSafety(
            Recording rec,
            int index,
            CelestialBody body,
            Orbit orbit,
            double currentUT,
            double currentAltitude,
            out TerminalOrbitSpawnSafetyDecision decision)
        {
            double periapsisAltitude = ComputeOrbitPeriapsisAltitude(orbit, body);
            double apoapsisAltitude = ComputeOrbitApoapsisAltitude(orbit, body);
            double pressure = TryGetAtmosphericPressure(body, currentAltitude);

            decision = EvaluateTerminalOrbitSpawnSafetyGeometry(
                body != null && body.atmosphere,
                body != null ? body.atmosphereDepth : 0.0,
                currentAltitude,
                periapsisAltitude,
                apoapsisAltitude,
                OrbitClearance.ResolveMaxTerrainAltitude(body));

            if (decision.Action == TerminalOrbitSpawnSafetyAction.DeferUntilSafe)
            {
                if (TryFindNextSafeTerminalOrbitSpawnUT(
                    orbit,
                    body,
                    currentUT,
                    decision.SafeAltitude,
                    out double nextSafeUT,
                    out double nextSafeAltitude))
                {
                    decision.NextSafeUT = nextSafeUT;
                    decision.NextSafeAltitude = nextSafeAltitude;
                    LogTerminalSpawnSafetyDecision(rec, index, body, currentUT, decision, pressure);
                    TerminalOrbitSpawnSafety.MarkDeferred(
                        rec,
                        decision,
                        currentUT,
                        nextSafeUT,
                        pressure);
                    ParsekLog.Info("Spawner", string.Format(CultureInfo.InvariantCulture,
                        "Terminal orbit spawn deferred: rec={0} idx={1} vessel=\"{2}\" " +
                        "reason={3} currentUT={4:F2} nextSafeUT={5:F2} propagatedAlt={6:F1} " +
                        "safeAlt={7:F1} atmosphereDepth={8:F1} periapsis={9:F1} pressure={10}",
                        rec?.RecordingId ?? "(null)",
                        index,
                        rec?.VesselName ?? "(null)",
                        decision.ReasonCode,
                        currentUT,
                        nextSafeUT,
                        decision.CurrentAltitude,
                        decision.SafeAltitude,
                        decision.AtmosphereDepth,
                        decision.PeriapsisAltitude,
                        FormatOptionalDouble(pressure)));
                    return false;
                }

                ResolveTerminalOrbitDeferralWithNoFutureSafeUT(ref decision, currentAltitude);
            }

            LogTerminalSpawnSafetyDecision(rec, index, body, currentUT, decision, pressure);

            if (decision.Action == TerminalOrbitSpawnSafetyAction.CannotSpawnSafely)
            {
                TerminalOrbitSpawnSafety.MarkCannotSpawnSafely(rec, decision, currentUT, pressure);
                ParsekLog.Warn("Spawner", string.Format(CultureInfo.InvariantCulture,
                    "Cannot spawn terminal orbit safely: rec={0} idx={1} vessel=\"{2}\" " +
                    "currentUT={3:F2} propagatedAlt={4:F1} safeAlt={5:F1} atmosphereDepth={6:F1} " +
                    "periapsis={7:F1} apoapsis={8:F1} decision={9} reason={10} pressure={11} " +
                    "periapsisFloor={12}",
                    rec?.RecordingId ?? "(null)",
                    index,
                    rec?.VesselName ?? "(null)",
                    currentUT,
                    decision.CurrentAltitude,
                    decision.SafeAltitude,
                    decision.AtmosphereDepth,
                    decision.PeriapsisAltitude,
                    decision.ApoapsisAltitude,
                    decision.Action,
                    decision.ReasonCode,
                    FormatOptionalDouble(pressure),
                    FormatOptionalDouble(decision.PeriapsisFloorAltitude)));
                return false;
            }

            TerminalOrbitSpawnSafety.Clear(rec);
            return true;
        }

        internal static void RejectUnresolvedTerminalOrbitSpawn(
            Recording rec,
            int index,
            CelestialBody body,
            double currentUT,
            double fallbackAltitude)
        {
            double atmosphereDepth = ResolveSpawnSafetyAtmosphereDepth(
                body != null && body.atmosphere,
                body != null ? body.atmosphereDepth : 0.0);
            double pressure = TryGetAtmosphericPressure(body, fallbackAltitude);
            var decision = new TerminalOrbitSpawnSafetyDecision
            {
                Action = TerminalOrbitSpawnSafetyAction.CannotSpawnSafely,
                ReasonCode = TerminalOrbitSpawnSafety.ReasonTerminalOrbitResolutionFailed,
                Reason = "recorded terminal orbit could not be resolved for current spawn UT",
                CurrentAltitude = fallbackAltitude,
                AtmosphereDepth = atmosphereDepth,
                SafetyMargin = TerminalOrbitSpawnSafety.DefaultSafetyMarginMeters,
                SafeAltitude = TerminalOrbitSpawnSafety.ComputeSafeAltitude(
                    atmosphereDepth,
                    TerminalOrbitSpawnSafety.DefaultSafetyMarginMeters),
                PeriapsisFloorAltitude = OrbitClearance.ResolvePeriapsisFloorAltitude(body),
                PeriapsisAltitude = double.NaN,
                ApoapsisAltitude = double.NaN,
                NextSafeUT = double.NaN,
                NextSafeAltitude = double.NaN,
            };

            LogTerminalSpawnSafetyDecision(rec, index, body, currentUT, decision, pressure);
            TerminalOrbitSpawnSafety.MarkCannotSpawnSafely(rec, decision, currentUT, pressure);
            ParsekLog.Warn("Spawner", string.Format(CultureInfo.InvariantCulture,
                "Cannot spawn terminal orbit safely: rec={0} idx={1} vessel=\"{2}\" " +
                "currentUT={3:F2} fallbackAlt={4:F1} safeAlt={5:F1} atmosphereDepth={6:F1} " +
                "decision={7} reason={8} pressure={9}",
                rec?.RecordingId ?? "(null)",
                index,
                rec?.VesselName ?? "(null)",
                currentUT,
                fallbackAltitude,
                decision.SafeAltitude,
                decision.AtmosphereDepth,
                decision.Action,
                decision.ReasonCode,
                FormatOptionalDouble(pressure)));
        }

        private static void LogTerminalSpawnSafetyDecision(
            Recording rec,
            int index,
            CelestialBody body,
            double currentUT,
            TerminalOrbitSpawnSafetyDecision decision,
            double pressure)
        {
            ParsekLog.Info("Spawner", string.Format(CultureInfo.InvariantCulture,
                "Terminal spawn safety decision: rec={0} idx={1} vessel=\"{2}\" body={3} " +
                "decision={4} reason={5} currentUT={6:F2} propagatedAlt={7:F1} safeAlt={8:F1} " +
                "atmosphereDepth={9:F1} margin={10:F1} periapsis={11:F1} apoapsis={12:F1} " +
                "nextSafeUT={13} nextSafeAlt={14} pressure={15} periapsisFloor={16}",
                rec?.RecordingId ?? "(null)",
                index,
                rec?.VesselName ?? "(null)",
                body?.name ?? "(null)",
                decision.Action,
                decision.ReasonCode,
                currentUT,
                decision.CurrentAltitude,
                decision.SafeAltitude,
                decision.AtmosphereDepth,
                decision.SafetyMargin,
                decision.PeriapsisAltitude,
                decision.ApoapsisAltitude,
                FormatOptionalDouble(decision.NextSafeUT),
                FormatOptionalDouble(decision.NextSafeAltitude),
                FormatOptionalDouble(pressure),
                FormatOptionalDouble(decision.PeriapsisFloorAltitude)));
        }

        private static bool TryFindNextSafeTerminalOrbitSpawnUT(
            Orbit orbit,
            CelestialBody body,
            double currentUT,
            double safeAltitude,
            out double nextSafeUT,
            out double nextSafeAltitude)
        {
            nextSafeUT = double.NaN;
            nextSafeAltitude = double.NaN;

            if (orbit == null
                || body == null
                || !TerminalOrbitSpawnSafety.IsFinite(currentUT)
                || !TerminalOrbitSpawnSafety.IsFinite(safeAltitude))
            {
                return false;
            }

            double period = TryGetOrbitPeriod(orbit, body);
            if (!TerminalOrbitSpawnSafety.IsFinite(period) || period <= 0.0)
                return false;

            double step = Math.Max(period / TerminalOrbitSpawnSafetyScanSteps, 1.0);
            double previousUT = currentUT;

            for (int i = 1; i <= TerminalOrbitSpawnSafetyScanSteps; i++)
            {
                double candidateUT = currentUT + step * i;
                if (!TryGetOrbitAltitudeAtUT(orbit, body, candidateUT, out double candidateAltitude))
                    continue;

                if (candidateAltitude >= safeAltitude)
                {
                    double lowUT = previousUT;
                    double highUT = candidateUT;
                    double highAlt = candidateAltitude;
                    for (int b = 0; b < 18; b++)
                    {
                        double midUT = (lowUT + highUT) * 0.5;
                        if (!TryGetOrbitAltitudeAtUT(orbit, body, midUT, out double midAlt))
                            break;

                        if (midAlt >= safeAltitude)
                        {
                            highUT = midUT;
                            highAlt = midAlt;
                        }
                        else
                        {
                            lowUT = midUT;
                        }
                    }

                    nextSafeUT = highUT;
                    nextSafeAltitude = highAlt;
                    return true;
                }

                previousUT = candidateUT;
            }

            return false;
        }

        private static bool TryGetOrbitAltitudeAtUT(
            Orbit orbit,
            CelestialBody body,
            double ut,
            out double altitude)
        {
            altitude = double.NaN;
            try
            {
                Vector3d worldPos = orbit.getPositionAtUT(ut);
                if (!VesselSnapshotOps.IsFinite(worldPos))
                    return false;
                altitude = body.GetAltitude(worldPos);
                return TerminalOrbitSpawnSafety.IsFinite(altitude);
            }
            catch
            {
                return false;
            }
        }

        private static double TryGetOrbitPeriod(Orbit orbit, CelestialBody body)
        {
            try
            {
                double period = orbit.period;
                if (TerminalOrbitSpawnSafety.IsFinite(period) && period > 0.0)
                    return period;
            }
            catch
            {
                // Fall through to Kepler period from elements.
            }

            try
            {
                double semiMajorAxis = orbit.semiMajorAxis;
                double grav = body?.gravParameter ?? 0.0;
                if (TerminalOrbitSpawnSafety.IsFinite(semiMajorAxis)
                    && semiMajorAxis > 0.0
                    && TerminalOrbitSpawnSafety.IsFinite(grav)
                    && grav > 0.0)
                {
                    return 2.0 * Math.PI * Math.Sqrt(
                        semiMajorAxis * semiMajorAxis * semiMajorAxis / grav);
                }
            }
            catch
            {
                return double.NaN;
            }

            return double.NaN;
        }

        private static double ComputeOrbitPeriapsisAltitude(Orbit orbit, CelestialBody body)
        {
            if (orbit == null || body == null)
                return double.NaN;

            try
            {
                return orbit.semiMajorAxis * (1.0 - orbit.eccentricity) - body.Radius;
            }
            catch
            {
                return double.NaN;
            }
        }

        private static double ComputeOrbitApoapsisAltitude(Orbit orbit, CelestialBody body)
        {
            if (orbit == null || body == null)
                return double.NaN;

            try
            {
                if (orbit.eccentricity >= 1.0)
                    return double.PositiveInfinity;

                return orbit.semiMajorAxis * (1.0 + orbit.eccentricity) - body.Radius;
            }
            catch
            {
                return double.NaN;
            }
        }

        private static double TryGetAtmosphericPressure(CelestialBody body, double altitude)
        {
            if (body == null || !body.atmosphere)
                return double.NaN;

            try
            {
                return body.GetPressure(altitude);
            }
            catch
            {
                return double.NaN;
            }
        }

        private static string FormatOptionalDouble(double value)
        {
            return TerminalOrbitSpawnSafety.IsFinite(value)
                ? value.ToString("R", CultureInfo.InvariantCulture)
                : "n/a";
        }

        internal static double ComputeRecordedTerminalOrbitMeanAnomalyAtUT(
            Recording rec, double bodyGravParam, double ut)
        {
            if (rec == null
                || rec.TerminalOrbitSemiMajorAxis <= 0.0
                || bodyGravParam <= 0.0
                || double.IsNaN(ut)
                || double.IsInfinity(ut))
            {
                return rec != null ? rec.TerminalOrbitMeanAnomalyAtEpoch : 0.0;
            }

            if (Math.Abs(ut - rec.TerminalOrbitEpoch) < 1e-9)
                return rec.TerminalOrbitMeanAnomalyAtEpoch;

            return TimeJumpManager.ComputeEpochShiftedMeanAnomaly(
                rec.TerminalOrbitMeanAnomalyAtEpoch,
                rec.TerminalOrbitEpoch,
                rec.TerminalOrbitSemiMajorAxis,
                bodyGravParam,
                ut);
        }

        internal static bool TryGetPreferredRecordedOrbitSeedForSpawn(
            Recording rec,
            out double inclination,
            out double eccentricity,
            out double semiMajorAxis,
            out double lan,
            out double argumentOfPeriapsis,
            out double meanAnomalyAtEpoch,
            out double epoch,
            out string bodyName)
        {
            inclination = 0.0;
            eccentricity = 0.0;
            semiMajorAxis = 0.0;
            lan = 0.0;
            argumentOfPeriapsis = 0.0;
            meanAnomalyAtEpoch = 0.0;
            epoch = 0.0;
            bodyName = null;

            if (rec == null)
                return false;

            if (rec.OrbitSegments != null)
            {
                for (int i = rec.OrbitSegments.Count - 1; i >= 0; i--)
                {
                    OrbitSegment seg = rec.OrbitSegments[i];
                    if (string.IsNullOrEmpty(seg.bodyName) || seg.semiMajorAxis <= 0.0)
                        continue;

                    inclination = seg.inclination;
                    eccentricity = seg.eccentricity;
                    semiMajorAxis = seg.semiMajorAxis;
                    lan = seg.longitudeOfAscendingNode;
                    argumentOfPeriapsis = seg.argumentOfPeriapsis;
                    meanAnomalyAtEpoch = seg.meanAnomalyAtEpoch;
                    epoch = seg.epoch;
                    bodyName = seg.bodyName;
                    return true;
                }
            }

            if (!HasRecordedTerminalOrbit(rec))
                return false;

            inclination = rec.TerminalOrbitInclination;
            eccentricity = rec.TerminalOrbitEccentricity;
            semiMajorAxis = rec.TerminalOrbitSemiMajorAxis;
            lan = rec.TerminalOrbitLAN;
            argumentOfPeriapsis = rec.TerminalOrbitArgumentOfPeriapsis;
            meanAnomalyAtEpoch = rec.TerminalOrbitMeanAnomalyAtEpoch;
            epoch = rec.TerminalOrbitEpoch;
            bodyName = rec.TerminalOrbitBody;
            return true;
        }

        internal static bool TryGetEndpointAlignedRecordedOrbitSeedForSpawn(
            Recording rec,
            out double inclination,
            out double eccentricity,
            out double semiMajorAxis,
            out double lan,
            out double argumentOfPeriapsis,
            out double meanAnomalyAtEpoch,
            out double epoch,
            out string bodyName)
        {
            inclination = 0.0;
            eccentricity = 0.0;
            semiMajorAxis = 0.0;
            lan = 0.0;
            argumentOfPeriapsis = 0.0;
            meanAnomalyAtEpoch = 0.0;
            epoch = 0.0;
            bodyName = null;

            if (RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed(
                rec,
                out inclination,
                out eccentricity,
                out semiMajorAxis,
                out lan,
                out argumentOfPeriapsis,
                out meanAnomalyAtEpoch,
                out epoch,
                out bodyName))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when the spawn must come from the recorded terminal orbit propagated to the
        /// spawn UT rather than from the last trajectory point. Applies to an EVA kerbal like
        /// any vessel: the last point is body-fixed lat/lon/alt, so re-seeding an orbit from it
        /// at a later UT rotates the orbit with the planet and leaves the kerbal's phase where
        /// it was at the end UT. Without orbit data the caller keeps the endpoint path.
        /// </summary>
        internal static bool ShouldUseRecordedTerminalOrbitSpawnState(Recording rec)
        {
            return rec != null
                && rec.TerminalStateValue == TerminalState.Orbiting
                && HasRecordedTerminalOrbit(rec);
        }

        internal static bool ShouldRouteThroughSpawnAtPosition(Recording rec)
        {
            if (rec == null)
                return false;

            bool isEva = !string.IsNullOrEmpty(rec.EvaCrewName);
            bool isBreakupContinuous = rec.ChildBranchPointId != null && rec.TerminalStateValue.HasValue;

            return rec.TerminalStateValue == TerminalState.Orbiting
                || rec.TerminalStateValue == TerminalState.Docked
                || isEva
                || isBreakupContinuous;
        }

        internal static bool TryBuildRecordedTerminalOrbitForSpawn(
            Recording rec, CelestialBody body, double ut, out Orbit orbit)
        {
            return TryBuildRecordedTerminalOrbitForSpawn(rec, body, ut, out orbit, out _);
        }

        /// <summary>
        /// Pure: the mean anomaly a terminal-orbit spawn at <paramref name="spawnUT"/>
        /// writes with <paramref name="spawnUT"/> as its epoch: the recorded orbit's phase
        /// propagated from its own epoch to the spawn UT.
        /// </summary>
        internal static double ComputeSpawnMeanAnomaly(
            double meanAnomalyAtEpoch, double epoch,
            double semiMajorAxis, double gravParameter,
            double spawnUT)
        {
            if (semiMajorAxis <= 0.0
                || gravParameter <= 0.0
                || double.IsNaN(spawnUT)
                || double.IsInfinity(spawnUT)
                || Math.Abs(spawnUT - epoch) < 1e-9)
            {
                return meanAnomalyAtEpoch;
            }

            return TimeJumpManager.ComputeEpochShiftedMeanAnomaly(
                meanAnomalyAtEpoch,
                epoch,
                semiMajorAxis,
                gravParameter,
                spawnUT);
        }

        /// <summary>
        /// <see cref="TryBuildRecordedTerminalOrbitForSpawn(Recording, CelestialBody, double, out Orbit)"/>
        /// that also names the seed it used (<see cref="TerminalOrbitSeedTrajectoryTail"/> or
        /// <see cref="TerminalOrbitSeedStoredSegment"/>; null when no seed was found). The Real Spawn
        /// Control Ready move reads it: only a tail seed turns with the body's live rotation.
        /// </summary>
        internal static bool TryBuildRecordedTerminalOrbitForSpawn(
            Recording rec, CelestialBody body, double ut, out Orbit orbit,
            out string seedSource)
        {
            orbit = null;
            seedSource = null;

            if (body == null)
                return false;

            double inclination;
            double eccentricity;
            double semiMajorAxis;
            double lan;
            double argumentOfPeriapsis;
            double meanAnomalyAtEpoch;
            double epoch;
            string orbitBodyName;

            // Trajectory tail takes precedence when its UT is later than every stored
            // OrbitSegment for the same body — the stored segment's elements have been
            // superseded by physics frames (typically a circ burn followed by a coast)
            // that the recorder did not promote to a fresh OrbitSegment.
            bool tailWon = TryDeriveTerminalOrbitSeedFromTrajectoryTail(
                rec,
                body,
                ut,
                out inclination,
                out eccentricity,
                out semiMajorAxis,
                out lan,
                out argumentOfPeriapsis,
                out meanAnomalyAtEpoch,
                out epoch,
                out orbitBodyName,
                out string tailDeclineReason);
            if (tailWon)
            {
                seedSource = TerminalOrbitSeedTrajectoryTail;
            }
            else if (TryGetEndpointAlignedRecordedOrbitSeedForSpawn(
                rec,
                out inclination,
                out eccentricity,
                out semiMajorAxis,
                out lan,
                out argumentOfPeriapsis,
                out meanAnomalyAtEpoch,
                out epoch,
                out orbitBodyName))
            {
                seedSource = TerminalOrbitSeedStoredSegment;
            }
            else
            {
                string resolvedEndpointBody;
                string endpointBody = RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out resolvedEndpointBody)
                    ? resolvedEndpointBody
                    : "(none)";
                ParsekLog.Warn("Spawner",
                    $"TryBuildRecordedTerminalOrbitForSpawn: no endpoint-aligned orbit seed " +
                    $"(endpointBody={endpointBody}, terminalBody={rec?.TerminalOrbitBody ?? "(null)"}, " +
                    $"orbitSegments={rec?.OrbitSegments?.Count ?? 0}, " +
                    $"tailDeclineReason={tailDeclineReason ?? "(unknown)"})");
                return false;
            }

            if (!string.Equals(body.name, orbitBodyName, StringComparison.Ordinal))
            {
                ParsekLog.Warn("Spawner",
                    $"TryBuildRecordedTerminalOrbitForSpawn: body mismatch " +
                    $"(body={body.name}, terminalBody={orbitBodyName}, source={seedSource})");
                return false;
            }

            try
            {
                double meanAnomalyAtSpawnUT = ComputeSpawnMeanAnomaly(
                    meanAnomalyAtEpoch,
                    epoch,
                    semiMajorAxis,
                    body.gravParameter,
                    ut);

                orbit = new Orbit(
                    inclination,
                    eccentricity,
                    semiMajorAxis,
                    lan,
                    argumentOfPeriapsis,
                    meanAnomalyAtSpawnUT,
                    ut,
                    body);
                ParsekLog.Verbose("Spawner",
                    string.Format(CultureInfo.InvariantCulture,
                        "TryBuildRecordedTerminalOrbitForSpawn: rec={0} source={1} body={2} epoch={3:F2} sma={4:F1} ecc={5:F4}",
                        rec?.RecordingId ?? "(null)",
                        seedSource,
                        orbitBodyName,
                        epoch,
                        semiMajorAxis,
                        eccentricity));
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Spawner",
                    $"TryBuildRecordedTerminalOrbitForSpawn failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Derive osculating orbital elements from the recording's most recent valid
        /// trajectory frame when that frame post-dates every stored
        /// <see cref="OrbitSegment"/>. Used to recover from recordings whose only
        /// stored segment is a pre-burn on-rails coast — physics frames captured
        /// after the segment (post-circ-burn coast) carry the correct final state
        /// in (lat, lon, alt, velocity), and the recorder never had a chance to
        /// promote them to a fresh segment before scene exit.
        ///
        /// Walks <see cref="Recording.TrackSections"/> in reverse, picking the
        /// latest <see cref="ReferenceFrame.Absolute"/> /
        /// <see cref="SegmentEnvironment.ExoBallistic"/> frame on the spawn body.
        /// Honors v7+ <see cref="TrackSection.bodyFixedFrames"/> shadow on Relative
        /// sections. Skips Surface / Atmospheric / ExoPropulsive / Approach
        /// environments — those produce sub-orbital or transitional osculating
        /// orbits that are not meaningful as the recording's "terminal" orbit.
        /// The tail's velocity is not trusted as recorded: its frame (surface-relative
        /// below the inverse-rotation altitude) and its world yaw are resolved from the
        /// tail and the coast frame before it
        /// (<see cref="OrbitReseed.TryFromRecordedTailPair"/>); a tail that cannot be
        /// resolved is declined so the stored segment / terminal orbit seeds answer.
        /// Also rejects when |spawnUT − tailUT| exceeds
        /// <see cref="TailDerivedOrbitMaxRotationDriftSeconds"/> because
        /// <c>body.GetWorldSurfacePosition</c> uses the body's current rotation,
        /// not the rotation at the recorded UT — large deltas would shift LAN/argPe
        /// by the body-rotation accumulated between candidate.ut and now.
        /// </summary>
        internal static bool TryDeriveTerminalOrbitSeedFromTrajectoryTail(
            Recording rec,
            CelestialBody body,
            double spawnUT,
            out double inclination,
            out double eccentricity,
            out double semiMajorAxis,
            out double lan,
            out double argumentOfPeriapsis,
            out double meanAnomalyAtEpoch,
            out double epoch,
            out string bodyName,
            out string declineReason)
        {
            inclination = 0.0;
            eccentricity = 0.0;
            semiMajorAxis = 0.0;
            lan = 0.0;
            argumentOfPeriapsis = 0.0;
            meanAnomalyAtEpoch = 0.0;
            epoch = 0.0;
            bodyName = null;
            declineReason = null;

            string spawnBodyName = OrbitSeedResolver.ResolveBodyName(body);
            string recId = rec?.RecordingId ?? "(null)";
            bool ok = OrbitSeedResolver.TryDeriveTailOrbitSeed(
                rec,
                body,
                spawnUT,
                TailSeedUse.Spawn,
                out TailDerivedOrbitSeed seed);
            declineReason = seed.DeclineReason;
            if (!ok)
            {
                if (declineReason == "no-absolute-coast-tail")
                {
                    ParsekLog.Verbose("Spawner",
                        string.Format(CultureInfo.InvariantCulture,
                            "Tail-derived terminal orbit skipped: rec={0} reason=no-absolute-coast-tail body={1}",
                            recId,
                            spawnBodyName ?? "(null)"));
                }
                else if (declineReason == "segment-newer-than-tail")
                {
                    ParsekLog.Verbose("Spawner",
                        string.Format(CultureInfo.InvariantCulture,
                            "Tail-derived terminal orbit skipped: rec={0} reason=segment-newer-than-tail tailUT={1:F2} segmentEndUT={2:F2}",
                            recId,
                            seed.TailUT,
                            seed.LatestStoredSegmentEndUT));
                }
                else if (declineReason == "terminal-orbit-newer-than-tail")
                {
                    ParsekLog.Verbose("Spawner",
                        string.Format(CultureInfo.InvariantCulture,
                            "Tail-derived terminal orbit skipped: rec={0} reason=terminal-orbit-newer-than-tail tailUT={1:F2} terminalOrbitEpoch={2:F2}",
                            recId,
                            seed.TailUT,
                            seed.TerminalOrbitEpoch));
                }
                else if (declineReason == "rotation-drift-out-of-bounds")
                {
                    ParsekLog.Verbose("Spawner",
                        string.Format(CultureInfo.InvariantCulture,
                            "Tail-derived terminal orbit skipped: rec={0} reason=rotation-drift-out-of-bounds tailUT={1:F2} spawnUT={2:F2} drift={3:F2}s limit={4:F2}s",
                            recId,
                            seed.TailUT,
                            spawnUT,
                            seed.RotationDriftSeconds,
                            TailDerivedOrbitMaxRotationDriftSeconds));
                }
                else if (declineReason == "non-finite-elements")
                {
                    ParsekLog.Warn("Spawner",
                        string.Format(CultureInfo.InvariantCulture,
                            "Tail-derived terminal orbit rejected: rec={0} reason=non-finite-elements sma={1} ecc={2}",
                            recId,
                            seed.Segment.semiMajorAxis,
                            seed.Segment.eccentricity));
                }
                else if (declineReason == "tail-velocity-single-frame"
                    || declineReason == RecordedTailVelocityResolver.DeclineNonFinite
                    || declineReason == RecordedTailVelocityResolver.DeclinePairGap
                    || declineReason == RecordedTailVelocityResolver.DeclineAzimuthUndefined
                    || declineReason == RecordedTailVelocityResolver.DeclineNoFit)
                {
                    ParsekLog.Verbose("Spawner",
                        string.Format(CultureInfo.InvariantCulture,
                            "Tail-derived terminal orbit skipped: rec={0} reason={1} tailUT={2:F2} " +
                            "velFrame={3} yaw={4:F3}deg residual={5:F3} recordedSpeed={6:F2}",
                            recId,
                            declineReason,
                            seed.TailUT,
                            seed.VelocityWasRotatingFrame ? "rotating" : "inertial",
                            seed.VelocityFrameYawDegrees,
                            seed.VelocityFrameResidual,
                            seed.RecordedSpeed));
                }
                else if (declineReason == "exception")
                {
                    ParsekLog.Warn("Spawner",
                        string.Format(CultureInfo.InvariantCulture,
                            "TryDeriveTerminalOrbitSeedFromTrajectoryTail failed: rec={0} reason=exception",
                            recId));
                }
                return false;
            }

            inclination = seed.Segment.inclination;
            eccentricity = seed.Segment.eccentricity;
            semiMajorAxis = seed.Segment.semiMajorAxis;
            lan = seed.Segment.longitudeOfAscendingNode;
            argumentOfPeriapsis = seed.Segment.argumentOfPeriapsis;
            meanAnomalyAtEpoch = seed.Segment.meanAnomalyAtEpoch;
            epoch = seed.Segment.epoch;
            bodyName = seed.BodyName;

            double periAlt = semiMajorAxis * (1.0 - eccentricity) - body.Radius;
            double apoAlt = semiMajorAxis * (1.0 + eccentricity) - body.Radius;
            ParsekLog.Verbose("Spawner",
                string.Format(CultureInfo.InvariantCulture,
                    "Tail-derived terminal orbit: rec={0} body={1} tailUT={2:F2} sma={3:F1} ecc={4:F4} periAlt={5:F1} apoAlt={6:F1} " +
                    "tailVelFrame={7} yaw={8:F3}deg residual={9:F3} recordedSpeed={10:F2} inertialSpeed={11:F2}",
                    recId,
                    seed.BodyName,
                    seed.TailUT,
                    semiMajorAxis,
                    eccentricity,
                    periAlt,
                    apoAlt,
                    seed.VelocityWasRotatingFrame ? "rotating" : "inertial",
                    seed.VelocityFrameYawDegrees,
                    seed.VelocityFrameResidual,
                    seed.RecordedSpeed,
                    seed.InertialSpeed));
            return true;
        }

        internal static bool TryFindLatestCoastTrajectoryFrame(
            Recording rec, string bodyName, out TrajectoryPoint frame)
        {
            return OrbitSeedResolver.TryFindLatestCoastTrajectoryFrame(rec, bodyName, out frame);
        }

        internal static double ResolveLatestStoredOrbitSegmentEndUT(Recording rec, string bodyName)
        {
            return OrbitSeedResolver.ResolveLatestStoredOrbitSegmentEndUT(rec, bodyName);
        }

        internal static bool TryResolveRecordedTerminalOrbitSpawnState(
            Recording rec, CelestialBody body, double ut,
            out double lat, out double lon, out double alt, out Vector3d velocity, out Orbit orbit)
        {
            lat = 0.0;
            lon = 0.0;
            alt = 0.0;
            velocity = Vector3d.zero;
            orbit = null;

            if (!TryBuildRecordedTerminalOrbitForSpawn(rec, body, ut, out orbit))
                return false;

            try
            {
                Vector3d worldPos = orbit.getPositionAtUT(ut);
                velocity = orbit.getOrbitalVelocityAtUT(ut);
                if (!VesselSnapshotOps.IsFinite(worldPos) || !VesselSnapshotOps.IsFinite(velocity))
                {
                    ParsekLog.Warn("Spawner",
                        "TryResolveRecordedTerminalOrbitSpawnState: propagated orbital state was non-finite");
                    return false;
                }

                lat = body.GetLatitude(worldPos);
                lon = body.GetLongitude(worldPos);
                alt = body.GetAltitude(worldPos);
                if (double.IsNaN(lat) || double.IsNaN(lon) || double.IsNaN(alt)
                    || double.IsInfinity(lat) || double.IsInfinity(lon) || double.IsInfinity(alt))
                {
                    ParsekLog.Warn("Spawner",
                        "TryResolveRecordedTerminalOrbitSpawnState: propagated orbital position was non-finite");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Spawner",
                    $"TryResolveRecordedTerminalOrbitSpawnState failed: {ex.Message}");
                return false;
            }
        }
    }
}
