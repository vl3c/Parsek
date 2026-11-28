using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The inputs of stock's on-rails warp limit, taken from the WATCHED GHOST (or the player
    /// ship when no ghost is watched) instead of the active drone (design-far-watch.md 8.4).
    /// Plain values; the live gatherer is in <see cref="FarWatchController"/>.
    /// </summary>
    internal struct FarWatchWarpInputs
    {
        internal int TargetRateIndex;
        internal bool LookAhead;
        internal TimeWarp.MaxRailsRateMode Mode;
        internal double PeModeSurfaceMargin;
        internal float[] WarpRates;
        /// <summary>The body's <c>timeWarpAltitudeLimits</c>.</summary>
        internal float[] AltitudeLimits;
        internal int MaxPhysicsRateIndex;
        internal double FixedDeltaTime;
        internal double GThreshold;
        internal bool AnyLoadedEvaOnLadder;

        internal bool LandedOrSplashed;
        internal double SrfSpeedSq;
        internal double Altitude;
        internal double GeeForce;

        internal bool HasAtmosphere;
        internal double AtmosphereDepth;
        internal bool HasSolidSurface;
        /// <summary>PQS radiusMax - radius.</summary>
        internal double PqsMaxHeight;
        /// <summary>PQS meshVertMax.</summary>
        internal double PqsMeshVertMax;
        internal double BodyRadius;

        internal bool HasConic;
        internal FarWatchConic Conic;
        internal double NowUT;
    }

    internal struct FarWatchWarpResult
    {
        internal int RateIndex;
        internal ClearToSaveStatus Reason;
        internal double MinPeAllowed;
    }

    /// <summary>
    /// Stock's <c>TimeWarp.getMaxOnRailsRateIdx</c> (decompiled 1.12.5, <c>TimeWarp.cs:1440</c> ff.)
    /// transcribed verbatim over plain inputs, plus Parsek's far-watch ceiling (8.4). The stock
    /// pressure test (<c>GetPressure(alt) &gt; 0</c>) is read as "below the atmosphere top", which
    /// is where stock's pressure curve is positive. The SOI clamp at the end of stock's method needs
    /// a patched-conic solver, which neither the drone (detached) nor a ghost has, so it never
    /// applies here, exactly as it never applies to the drone.
    /// </summary>
    internal static class FarWatchWarpLimits
    {
        internal const double CeilingRealSeconds = 10.0;
        internal const double CameraDroneFloorBaseMeters = 20.0;
        internal const double MaxFloorMeters = 1000.0;

        /// <summary>
        /// The ghost's felt (non-gravitational) acceleration in g between two body-relative states
        /// <paramref name="dt"/> seconds apart, for stock's <c>geeForce &gt; GThreshold</c> test (8.4).
        /// Read from the change of the two coast invariants, the specific orbital energy and the
        /// angular momentum's magnitude, which gravity alone never changes: a coast reads 0 g
        /// however long the window (high warp), where a finite difference of the velocity minus
        /// the end point's gravity reads the gravity vector's turn over the window as g. Only
        /// magnitudes are compared, so a frame that rotates between the samples (KSP's inverse
        /// rotation) changes nothing. A burn along or across the path reads its acceleration; a
        /// purely radial push on a circular coast or a pure plane change changes neither invariant
        /// to first order and reads near 0 (they stay under stock's other limits).
        /// </summary>
        internal static double NonGravitationalGee(Vector3d r0, Vector3d v0, Vector3d r1, Vector3d v1, double mu, double dt)
        {
            if (!(dt > 0.0) || !(mu > 0.0))
                return 0.0;
            double rm0 = r0.magnitude, rm1 = r1.magnitude;
            if (!(rm0 > 0.0) || !(rm1 > 0.0) || double.IsInfinity(rm0) || double.IsInfinity(rm1))
                return 0.0;
            double e0 = v0.sqrMagnitude / 2.0 - mu / rm0;
            double e1 = v1.sqrMagnitude / 2.0 - mu / rm1;
            double vMean = (v0.magnitude + v1.magnitude) / 2.0;
            double rMean = (rm0 + rm1) / 2.0;
            double along = vMean > 1e-6 ? Math.Abs(e1 - e0) / (vMean * dt) : 0.0;
            double h0 = Vector3d.Cross(r0, v0).magnitude;
            double h1 = Vector3d.Cross(r1, v1).magnitude;
            double across = Math.Abs(h1 - h0) / (rMean * dt);
            double a = Math.Max(along, across);
            return double.IsNaN(a) || double.IsInfinity(a) ? 0.0 : a / StandardGravity;
        }

        internal const double StandardGravity = 9.80665;

        internal static FarWatchWarpResult EvaluateStock(FarWatchWarpInputs i)
        {
            var res = new FarWatchWarpResult { RateIndex = i.TargetRateIndex, Reason = ClearToSaveStatus.CLEAR };
            int tgt = i.TargetRateIndex;
            if (tgt <= 0)
            {
                res.RateIndex = tgt;
                return res;
            }
            if (i.AnyLoadedEvaOnLadder)
            {
                res.RateIndex = 0;
                res.Reason = ClearToSaveStatus.NOT_WHILE_ON_A_LADDER;
                return res;
            }
            if (i.LandedOrSplashed)
            {
                if (i.SrfSpeedSq > SlideSpeedSqThreshold)
                {
                    res.RateIndex = 0;
                    res.Reason = ClearToSaveStatus.NOT_WHILE_MOVING_OVER_SURFACE;
                    return res;
                }
                res.RateIndex = tgt;
                return res;
            }

            double terrainMax = 0.0, meshMax = 0.0;
            if (i.HasSolidSurface)
            {
                terrainMax = i.PqsMaxHeight;
                meshMax = i.PqsMeshVertMax;
            }
            double atmo = i.HasAtmosphere ? i.AtmosphereDepth : 0.0;
            double minPe = Math.Max(Math.Max(terrainMax, atmo), meshMax);
            res.MinPeAllowed = minPe;

            if (PressurePositive(i, i.Altitude))
            {
                res.RateIndex = 0;
                res.Reason = ClearToSaveStatus.NOT_IN_ATMOSPHERE;
                return res;
            }
            if (i.GeeForce > i.GThreshold)
            {
                res.RateIndex = 0;
                res.Reason = ClearToSaveStatus.NOT_UNDER_ACCELERATION;
                return res;
            }
            if (i.Mode == TimeWarp.MaxRailsRateMode.VesselAltitude
                && i.Altitude < Limit(i, tgt))
            {
                res.Reason = ClearToSaveStatus.NOT_WHILE_ABOUT_TO_CRASH;
                tgt = MaxRateForAltitude(i, i.Altitude);
            }
            if (i.LookAhead && tgt > i.MaxPhysicsRateIndex && i.HasConic && i.WarpRates != null && i.WarpRates.Length > 0)
            {
                if (tgt > i.WarpRates.Length - 1)
                    tgt = i.WarpRates.Length - 1;
                double dtFix = i.FixedDeltaTime;
                double timeToPe = FarWatchConicMath.TimeToPeriapsis(i.Conic, i.NowUT);
                if (double.IsNaN(timeToPe) || timeToPe < 0.0)
                    timeToPe = double.MaxValue;
                double peA = FarWatchConicMath.PeriapsisRadius(i.Conic) - i.BodyRadius;
                double alt = LookAheadAltitude(i, dtFix * i.WarpRates[tgt], timeToPe, peA);
                while (tgt > i.MaxPhysicsRateIndex && (PressurePositive(i, alt) || alt < 0.0))
                {
                    tgt--;
                    alt = LookAheadAltitude(i, dtFix * i.WarpRates[tgt], timeToPe, peA);
                    res.Reason = ClearToSaveStatus.NOT_IN_ATMOSPHERE;
                }
                if (i.Mode == TimeWarp.MaxRailsRateMode.PeAltitude
                    && !(peA > minPe + i.PeModeSurfaceMargin))
                {
                    while (tgt > i.MaxPhysicsRateIndex && alt < Limit(i, tgt))
                    {
                        tgt--;
                        alt = LookAheadAltitude(i, dtFix * i.WarpRates[tgt], timeToPe, peA);
                        res.Reason = ClearToSaveStatus.NOT_WHILE_ABOUT_TO_CRASH;
                    }
                }
            }
            res.RateIndex = tgt;
            return res;
        }

        /// <summary>
        /// Parsek's ceiling (8.4): the highest rate index whose rate leaves at least
        /// <see cref="CeilingRealSeconds"/> of real time before the return point and never crosses
        /// half the remaining time in one tick, and whose terrain floor stays under
        /// <see cref="MaxFloorMeters"/> (or the ghost's own height above the ground, when larger).
        /// The whole ladder when no bound applies.
        /// </summary>
        internal static int CeilingRateIndex(float[] rates, double fixedDeltaTime, double nowUT, double returnUT,
            double verticalSpeed, double gravity, double heightAboveTerrain)
        {
            if (rates == null || rates.Length == 0)
                return 0;
            double remaining = double.IsNaN(returnUT) ? double.PositiveInfinity : returnUT - nowUT;
            double floorCap = Math.Max(MaxFloorMeters, double.IsNaN(heightAboveTerrain) ? 0.0 : heightAboveTerrain);
            int best = 0;
            for (int k = 0; k < rates.Length; k++)
            {
                double r = rates[k];
                if (!double.IsPositiveInfinity(remaining))
                {
                    if (r * CeilingRealSeconds > remaining) break;
                    if (r * fixedDeltaTime > remaining / 2.0) break;
                }
                if (TerrainFloorMeters(verticalSpeed, r * fixedDeltaTime, gravity) > floorCap) break;
                best = k;
            }
            return best;
        }

        /// <summary>The ceiling's binding cause for the overlay and the log.</summary>
        internal static string DescribeCeiling(float[] rates, double fixedDeltaTime, double nowUT, double returnUT,
            double verticalSpeed, double gravity, double heightAboveTerrain, int ceilingIndex)
        {
            if (rates == null || ceilingIndex >= rates.Length - 1)
                return "none";
            int next = ceilingIndex + 1;
            double r = rates[next];
            double remaining = double.IsNaN(returnUT) ? double.PositiveInfinity : returnUT - nowUT;
            if (!double.IsPositiveInfinity(remaining)
                && (r * CeilingRealSeconds > remaining || r * fixedDeltaTime > remaining / 2.0))
                return "return";
            return "floor";
        }

        /// <summary>
        /// Stock's Update-loop mode decision (<c>TimeWarp.Update</c>, the HIGH/LOW switch) read at the
        /// ghost: LOW (physics warp) while it is in air and not landed, or landed and sliding
        /// (surface speed squared above stock's 0.09 m2/s2).
        /// </summary>
        internal static bool WantsLowMode(bool landedOrSplashed, bool pressurePositive, double srfSpeedSq)
        {
            if (!landedOrSplashed)
                return pressurePositive;
            return srfSpeedSq > SlideSpeedSqThreshold;
        }

        internal const double SlideSpeedSqThreshold = 0.09000000357627869;

        internal static bool PressurePositive(FarWatchWarpInputs i, double altitude)
        {
            return i.HasAtmosphere && altitude < i.AtmosphereDepth;
        }

        /// <summary>
        /// The drone's terrain floor (design-far-watch.md 6.3): the base clearance plus the fall
        /// one physics tick of game time could cover. The drone's floor clamp forwards here, so the
        /// warp ceiling and the clamp read one formula.
        /// </summary>
        internal static double TerrainFloorMeters(double verticalSpeed, double gameDtSeconds, double gravity)
        {
            if (double.IsNaN(verticalSpeed) || double.IsInfinity(verticalSpeed)) verticalSpeed = 0.0;
            if (double.IsNaN(gameDtSeconds) || double.IsInfinity(gameDtSeconds) || gameDtSeconds < 0.0) gameDtSeconds = 0.0;
            if (double.IsNaN(gravity) || double.IsInfinity(gravity) || gravity < 0.0) gravity = 0.0;
            return CameraDroneFloorBaseMeters
                + Math.Max(0.0, -verticalSpeed * gameDtSeconds)
                + 0.5 * gravity * gameDtSeconds * gameDtSeconds;
        }

        internal static string FormatLimitLine(string source, string rule, float requested, float granted)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return string.Format(ic, "warp limit source={0} rule={1} requested={2} granted={3}",
                source, rule, requested.ToString("0.##", ic), granted.ToString("0.##", ic));
        }

        private static double LookAheadAltitude(FarWatchWarpInputs i, double dt, double timeToPe, double peA)
        {
            if (dt > timeToPe)
                return peA;
            double r = FarWatchConicMath.RadiusAt(i.Conic, i.NowUT + dt);
            return double.IsNaN(r) ? -1.0 : r - i.BodyRadius;
        }

        private static double Limit(FarWatchWarpInputs i, int idx)
        {
            if (i.AltitudeLimits == null || i.AltitudeLimits.Length == 0)
                return 0.0;
            if (idx < 0) idx = 0;
            if (idx >= i.AltitudeLimits.Length) idx = i.AltitudeLimits.Length - 1;
            return i.AltitudeLimits[idx];
        }

        private static int MaxRateForAltitude(FarWatchWarpInputs i, double altitude)
        {
            int n = i.AltitudeLimits != null ? i.AltitudeLimits.Length : 0;
            for (int k = 0; k < n; k++)
            {
                if (i.AltitudeLimits[k] < altitude)
                    continue;
                return Math.Max(0, k - 1);
            }
            return i.WarpRates != null ? i.WarpRates.Length - 1 : 0;
        }
    }
}
