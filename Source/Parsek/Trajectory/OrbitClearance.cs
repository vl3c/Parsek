using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// The single periapsis line that separates a stable Orbiting terminal from a decaying
    /// flight (operator ruling 2026-09-27: "Any orbit Parsek calls Orbiting will spawn").
    /// Every producer of an Orbiting verdict (commit classification, the unloaded-vessel
    /// inference, the scene-exit finalizer, the finalization cache) and the terminal-orbit
    /// spawn-safety check read the SAME floor, so a periapsis cannot be Orbiting at commit and
    /// refused at spawn.
    ///
    /// The floor is the altitude above sea level the periapsis must clear:
    /// <list type="bullet">
    /// <item>the deletion altitude on a body with an atmosphere (owner ruling Q3): the
    /// altitude where the body's static pressure falls to
    /// <see cref="StockPackedDeletionPressureKPa"/>. Stock (decompiled <c>Vessel</c>, KSP
    /// 1.12.5) deletes a packed, unloaded, not landed / splashed vessel outside the active
    /// vessel's pack range when <c>mainBody.GetPressure(altitude) &gt; 1.0</c> kPa, and applies
    /// no drag on rails, so an unloaded orbit whose low point stays above that line keeps
    /// orbiting through the thin upper atmosphere;</item>
    /// <item>the body's maximum terrain height (the stock PQS <c>radiusMax - radius</c>, the
    /// value stock time warp uses on airless bodies), so a low periapsis cannot pass through
    /// a mountain;</item>
    /// <item>sea level when neither is known (the Sun, a body with no PQS, headless).</item>
    /// </list>
    /// The larger of the deletion altitude and the terrain height wins.
    /// </summary>
    internal static class OrbitClearance
    {
        /// <summary>Stock's packed-vessel deletion pressure (kPa), <c>Vessel.Update</c>.</summary>
        internal const double StockPackedDeletionPressureKPa = 1.0;

        private const int DeletionAltitudeBisectionIterations = 60;

        // Per-body deletion altitude, computed once per process from the body's pressure
        // curve (bodies do not change inside a KSP process).
        private static readonly Dictionary<string, double> deletionAltitudeCache =
            new Dictionary<string, double>(StringComparer.Ordinal);

        internal static double ComputePeriapsisFloorAltitude(
            double deletionAltitude,
            double maxTerrainAltitude)
        {
            double deletionTop = IsFinite(deletionAltitude) && deletionAltitude > 0.0
                ? deletionAltitude
                : 0.0;
            double terrainTop = IsFinite(maxTerrainAltitude) && maxTerrainAltitude > 0.0
                ? maxTerrainAltitude
                : 0.0;
            return Math.Max(deletionTop, terrainTop);
        }

        /// <summary>
        /// Pure: the altitude in [0, <paramref name="atmosphereDepth"/>] where
        /// <paramref name="pressureKPaAtAltitude"/> falls to
        /// <see cref="StockPackedDeletionPressureKPa"/>, by bisection (pressure falls with
        /// altitude). 0 when the body has no atmosphere, the inputs are not finite, or the
        /// surface pressure is already at or below the threshold; the atmosphere depth when
        /// even its top is above the threshold. A non-finite pressure sample counts as above
        /// the threshold, so a broken curve moves the line up (more crashes), never down.
        /// </summary>
        internal static double ComputeDeletionAltitude(
            Func<double, double> pressureKPaAtAltitude,
            double atmosphereDepth)
        {
            if (pressureKPaAtAltitude == null
                || !IsFinite(atmosphereDepth)
                || atmosphereDepth <= 0.0)
            {
                return 0.0;
            }

            double surfacePressure = pressureKPaAtAltitude(0.0);
            if (!IsFinite(surfacePressure) || surfacePressure <= StockPackedDeletionPressureKPa)
                return 0.0;

            double topPressure = pressureKPaAtAltitude(atmosphereDepth);
            if (!IsFinite(topPressure) || topPressure > StockPackedDeletionPressureKPa)
                return atmosphereDepth;

            double low = 0.0;
            double high = atmosphereDepth;
            for (int i = 0; i < DeletionAltitudeBisectionIterations; i++)
            {
                double mid = 0.5 * (low + high);
                double pressure = pressureKPaAtAltitude(mid);
                if (!IsFinite(pressure) || pressure > StockPackedDeletionPressureKPa)
                    low = mid;
                else
                    high = mid;
            }

            return 0.5 * (low + high);
        }

        /// <summary>
        /// Pure: a bound orbit (eccentricity below 1) whose periapsis altitude is strictly above
        /// <paramref name="periapsisFloorAltitude"/>. Strict so a periapsis exactly on the
        /// floor stays a decaying flight.
        /// </summary>
        internal static bool IsBoundOrbitClear(
            double eccentricity,
            double periapsisRadius,
            double bodyRadius,
            double periapsisFloorAltitude)
        {
            if (!IsFinite(eccentricity)
                || !IsFinite(periapsisRadius)
                || !IsFinite(bodyRadius)
                || bodyRadius <= 0.0)
            {
                return false;
            }

            if (eccentricity >= 1.0)
                return false;

            double floor = IsFinite(periapsisFloorAltitude) && periapsisFloorAltitude > 0.0
                ? periapsisFloorAltitude
                : 0.0;
            return periapsisRadius > bodyRadius + floor;
        }

        /// <summary>
        /// Live: the body's maximum terrain height above sea level from its PQS, or 0 when the
        /// body has no PQS or the value is not finite. Kept out of line so callers JIT headless.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static double ResolveMaxTerrainAltitude(CelestialBody body)
        {
            try
            {
                if (body == null)
                    return 0.0;
                PQS pqs = body.pqsController;
                if (pqs == null)
                    return 0.0;
                double height = pqs.radiusMax - pqs.radius;
                return IsFinite(height) && height > 0.0 ? height : 0.0;
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Live: the body's deletion altitude (<see cref="ComputeDeletionAltitude"/> over
        /// <c>body.GetPressure</c>), cached per body name. 0 for a body without an atmosphere.
        /// If the pressure curve cannot be read, the atmosphere top is returned (the floor
        /// before owner ruling Q3, so a failure keeps the verdict conservative) and nothing is
        /// cached. Kept out of line so callers JIT headless.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static double ResolveDeletionAltitude(CelestialBody body)
        {
            if (body == null)
                return 0.0;

            string name;
            bool hasAtmosphere;
            double atmosphereDepth;
            try
            {
                name = body.name;
                hasAtmosphere = body.atmosphere;
                atmosphereDepth = body.atmosphereDepth;
            }
            catch
            {
                return 0.0;
            }

            if (!hasAtmosphere || !IsFinite(atmosphereDepth) || atmosphereDepth <= 0.0)
                return 0.0;

            if (!string.IsNullOrEmpty(name)
                && deletionAltitudeCache.TryGetValue(name, out double cached))
            {
                return cached;
            }

            double deletionAltitude;
            try
            {
                deletionAltitude = ComputeDeletionAltitude(alt => body.GetPressure(alt), atmosphereDepth);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("OrbitClearance", string.Format(
                    CultureInfo.InvariantCulture,
                    "Deletion altitude unavailable for body={0}: GetPressure threw {1}; " +
                    "using the atmosphere top {2:F0}m",
                    name ?? "(null)",
                    ex.GetType().Name,
                    atmosphereDepth));
                return atmosphereDepth;
            }

            if (!string.IsNullOrEmpty(name))
                deletionAltitudeCache[name] = deletionAltitude;
            ParsekLog.Info("OrbitClearance", string.Format(
                CultureInfo.InvariantCulture,
                "Deletion altitude resolved: body={0} deletionAlt={1:F0}m atmoTop={2:F0}m " +
                "thresholdKPa={3:F1}",
                name ?? "(null)",
                deletionAltitude,
                atmosphereDepth,
                StockPackedDeletionPressureKPa));
            return deletionAltitude;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static double ResolvePeriapsisFloorAltitude(CelestialBody body)
        {
            if (body == null)
                return 0.0;
            return ComputePeriapsisFloorAltitude(
                ResolveDeletionAltitude(body),
                ResolveMaxTerrainAltitude(body));
        }

        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
