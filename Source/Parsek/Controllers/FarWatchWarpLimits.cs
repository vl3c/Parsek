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
