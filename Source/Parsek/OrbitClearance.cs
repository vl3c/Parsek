using System;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// The single periapsis line that separates a stable Orbiting terminal from a decaying
    /// flight (operator ruling 2026-09-27: "Any orbit Parsek calls Orbiting will spawn").
    /// Every producer of an Orbiting verdict (commit classification, the unloaded-vessel
    /// inference, the scene-exit finalizer) and the terminal-orbit spawn-safety check read
    /// the SAME floor, so a periapsis cannot be Orbiting at commit and refused at spawn.
    ///
    /// The floor is the altitude above sea level the periapsis must clear:
    /// <list type="bullet">
    /// <item>the atmosphere top on a body with an atmosphere (stock applies no drag above it);</item>
    /// <item>the body's maximum terrain height (the stock PQS <c>radiusMax - radius</c>, the
    /// value stock time warp uses on airless bodies), so a low periapsis cannot pass through
    /// a mountain;</item>
    /// <item>sea level when neither is known (the Sun, a body with no PQS, headless).</item>
    /// </list>
    /// The larger of the atmosphere top and the terrain height wins; on every stock
    /// atmospheric body the atmosphere top is the higher of the two.
    /// </summary>
    internal static class OrbitClearance
    {
        internal static double ComputePeriapsisFloorAltitude(
            bool bodyHasAtmosphere,
            double atmosphereDepth,
            double maxTerrainAltitude)
        {
            double atmosphereTop = bodyHasAtmosphere && IsFinite(atmosphereDepth) && atmosphereDepth > 0.0
                ? atmosphereDepth
                : 0.0;
            double terrainTop = IsFinite(maxTerrainAltitude) && maxTerrainAltitude > 0.0
                ? maxTerrainAltitude
                : 0.0;
            return Math.Max(atmosphereTop, terrainTop);
        }

        /// <summary>
        /// Pure: a bound orbit (eccentricity below 1) whose periapsis altitude is strictly above
        /// <paramref name="periapsisFloorAltitude"/>. Strict so a periapsis exactly at the
        /// atmosphere top stays a decaying flight.
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static double ResolvePeriapsisFloorAltitude(CelestialBody body)
        {
            if (body == null)
                return 0.0;
            return ComputePeriapsisFloorAltitude(
                body.atmosphere,
                body.atmosphereDepth,
                ResolveMaxTerrainAltitude(body));
        }

        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
