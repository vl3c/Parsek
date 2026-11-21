using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>The strongest canopy state among a vessel's parachutes.</summary>
    internal enum ParachuteCanopyState
    {
        None = 0,
        SemiDeployed = 1,
        Deployed = 2,
    }

    /// <summary>
    /// What the chute descent prediction reads off the vessel at the moment Parsek stops
    /// watching it. Filled by <see cref="ParachuteDescentLiveReader"/> from a live vessel and
    /// directly by tests.
    /// </summary>
    internal struct ParachuteDescentInput
    {
        public string BodyName;
        public double StartUT;
        public double Latitude;
        public double Longitude;
        // Altitude above sea level of the vessel's root, metres.
        public double Altitude;
        // Signed vertical speed, metres per second; negative is descending.
        public double VerticalSpeed;
        public ParachuteCanopyState Canopy;
        // The largest full-deploy altitude (stock ModuleParachute.deployAltitude, metres above
        // the ground) among the semi-deployed chutes; NaN when none was read.
        public double FullDeployAltitudeAgl;
        // Ground height above sea level under the vessel (negative below the sea).
        public double TerrainAltitude;
        public bool BodyHasOcean;
        public bool InAtmosphere;
        // Height of the root above the vessel's lowest point; NaN when unknown.
        public double RootHeightAboveLowestPoint;
        public Quaternion SurfaceRelativeRotation;
        // World "up" at the vessel, for the predicted points' velocity; zero = no velocity.
        public Vector3 WorldUp;
        public uint ChutePartPersistentId;
        public int OpenChuteCount;
    }

    /// <summary>The predicted chute descent: a few body-fixed points and its ending.</summary>
    internal struct ParachuteDescentPlan
    {
        public TerminalState Terminal;
        public double TouchdownUT;
        public double TouchdownAltitude;
        public double GroundAltitude;
        public double Phase1Rate;
        public double Phase2Rate;
        public double SwitchAltitude;
        public double SwitchUT;
        public string RateSource;
        // The descent rate under fully open canopies the ground is met at, whether it was
        // measured (false: DefaultFullCanopyDescentRate) and whether the vessel survives it.
        public double LandingRate;
        public bool LandingRateMeasured;
        public bool SurvivesTouchdown;
        public List<TrajectoryPoint> Points;
    }

    /// <summary>
    /// Owner ruling Q4: a stage left with parachutes open descends at chute speed and ends
    /// Landed / Splashed where it comes down. Pure: the body-fixed lat / lon is kept (chutes
    /// kill horizontal speed, and the air turns with the planet), and the vessel descends at
    /// a constant speed to the ground. Owner ruling 2026-11-21: it survives the touchdown only
    /// when its rate under fully open canopies is at or below
    /// <see cref="MaxSafeLandingRate"/>; faster, it crashes (Destroyed at ground impact, like a
    /// fall). Fully deployed: the measured descent rate, not clamped (below
    /// <see cref="MinDescentRate"/> it is not descending yet and takes
    /// <see cref="DefaultFullCanopyDescentRate"/>).
    /// Semi-deployed: the current rate (clamped to
    /// <see cref="MaxSemiDeployedDescentRate"/>, <see cref="DefaultSemiDeployedDescentRate"/>
    /// when not descending) down to the chutes' full-deploy altitude, then
    /// <see cref="DefaultFullCanopyDescentRate"/> (no full-canopy speed is measured before the
    /// canopy opens). The ground is the terrain height there; below sea level on a body with
    /// an ocean the vessel ends Splashed at sea level, else Landed on the terrain.
    /// </summary>
    internal static class ParachuteDescentPlanner
    {
        // Stock pods and boosters under full canopies come down at about 6-8 m/s.
        internal const double DefaultFullCanopyDescentRate = 7.0;
        internal const double DefaultSemiDeployedDescentRate = 40.0;
        internal const double MinDescentRate = 1.0;
        // Owner ruling 2026-11-21: the fastest full-canopy descent a stage survives.
        internal const double MaxSafeLandingRate = 12.0;
        internal const double MaxSemiDeployedDescentRate = 300.0;
        // Stock ModuleParachute.deployAltitude default.
        internal const double DefaultFullDeployAltitudeAgl = 500.0;
        // A root offset above this is not a vessel height (a bad raycast).
        internal const double MaxRootHeightAboveLowestPoint = 50.0;

        internal static bool TryPlan(ParachuteDescentInput input, out ParachuteDescentPlan plan, out string declineReason)
        {
            plan = default(ParachuteDescentPlan);
            declineReason = null;
            if (input.Canopy == ParachuteCanopyState.None)
            {
                declineReason = "no-open-chute";
                return false;
            }
            if (!input.InAtmosphere)
            {
                declineReason = "not-in-atmosphere";
                return false;
            }
            if (string.IsNullOrEmpty(input.BodyName))
            {
                declineReason = "body-missing";
                return false;
            }
            if (!IsFinite(input.StartUT) || !IsFinite(input.Latitude) || !IsFinite(input.Longitude)
                || !IsFinite(input.Altitude) || !IsFinite(input.TerrainAltitude))
            {
                declineReason = "state-non-finite";
                return false;
            }

            bool splashed = input.BodyHasOcean && input.TerrainAltitude < 0.0;
            double ground = splashed ? 0.0 : input.TerrainAltitude;
            double rootOffset = ResolveRootOffset(input.RootHeightAboveLowestPoint);
            double touchdownAltitude = splashed ? 0.0 : ground + rootOffset;
            if (input.Altitude <= touchdownAltitude)
            {
                declineReason = "at-or-below-ground";
                return false;
            }

            var points = new List<TrajectoryPoint>(3);
            double rate1;
            double rate2;
            double switchAltitude = double.NaN;
            double switchUT = double.NaN;
            string rateSource;
            double ut = input.StartUT;
            double altitude = input.Altitude;
            if (input.Canopy == ParachuteCanopyState.Deployed)
            {
                rate1 = ResolveDeployedRate(input.VerticalSpeed, out rateSource);
                rate2 = rate1;
            }
            else
            {
                double deployAgl = IsFinite(input.FullDeployAltitudeAgl) && input.FullDeployAltitudeAgl > 0.0
                    ? input.FullDeployAltitudeAgl
                    : DefaultFullDeployAltitudeAgl;
                double fullDeployAltitude = ground + deployAgl;
                rate2 = DefaultFullCanopyDescentRate;
                if (altitude > fullDeployAltitude)
                {
                    rate1 = ResolveSemiDeployedRate(input.VerticalSpeed, out rateSource);
                    switchAltitude = fullDeployAltitude;
                    switchUT = ut + (altitude - fullDeployAltitude) / rate1;
                }
                else
                {
                    // Already under the full-deploy altitude: the canopy opens now.
                    rate1 = rate2;
                    rateSource = "semi-below-deploy-altitude";
                }
            }

            points.Add(MakePoint(input, ut, altitude, rate1));
            if (IsFinite(switchUT))
            {
                points.Add(MakePoint(input, switchUT, switchAltitude, rate2));
                ut = switchUT;
                altitude = switchAltitude;
            }
            double touchdownUT = ut + (altitude - touchdownAltitude) / rate2;
            points.Add(MakePoint(input, touchdownUT, touchdownAltitude, rate2));

            bool landingMeasured = input.Canopy == ParachuteCanopyState.Deployed
                && string.Equals(rateSource, "measured", StringComparison.Ordinal);
            bool survives = rate2 <= MaxSafeLandingRate;
            plan = new ParachuteDescentPlan
            {
                Terminal = !survives
                    ? TerminalState.Destroyed
                    : splashed ? TerminalState.Splashed : TerminalState.Landed,
                LandingRate = rate2,
                LandingRateMeasured = landingMeasured,
                SurvivesTouchdown = survives,
                TouchdownUT = touchdownUT,
                TouchdownAltitude = touchdownAltitude,
                GroundAltitude = ground,
                Phase1Rate = rate1,
                Phase2Rate = rate2,
                SwitchAltitude = switchAltitude,
                SwitchUT = switchUT,
                RateSource = rateSource,
                Points = points,
            };
            return true;
        }

        internal static double ResolveDeployedRate(double verticalSpeed, out string source)
        {
            double rate = -verticalSpeed;
            if (!IsFinite(rate) || rate < MinDescentRate)
            {
                source = "default-not-descending";
                return DefaultFullCanopyDescentRate;
            }
            source = "measured";
            return rate;
        }

        internal static double ResolveSemiDeployedRate(double verticalSpeed, out string source)
        {
            double rate = -verticalSpeed;
            if (!IsFinite(rate) || rate < MinDescentRate)
            {
                source = "semi-default-not-descending";
                return DefaultSemiDeployedDescentRate;
            }
            if (rate > MaxSemiDeployedDescentRate)
            {
                source = "semi-measured-clamped";
                return MaxSemiDeployedDescentRate;
            }
            source = "semi-measured";
            return rate;
        }

        /// <summary>
        /// Pure: the descent's state at <paramref name="ut"/>, linear between its points
        /// (clamped to the first / last point outside them). Null for an empty list.
        /// </summary>
        internal static TrajectoryPoint? EvaluateAt(IList<TrajectoryPoint> points, double ut)
        {
            if (points == null || points.Count == 0)
                return null;
            if (ut <= points[0].ut)
                return WithUT(points[0], ut);
            for (int i = 1; i < points.Count; i++)
            {
                TrajectoryPoint b = points[i];
                if (ut > b.ut)
                    continue;
                TrajectoryPoint a = points[i - 1];
                double span = b.ut - a.ut;
                double t = span > 0.0 ? (ut - a.ut) / span : 1.0;
                TrajectoryPoint p = a;
                p.ut = ut;
                p.latitude = a.latitude + (b.latitude - a.latitude) * t;
                p.longitude = a.longitude + (b.longitude - a.longitude) * t;
                p.altitude = a.altitude + (b.altitude - a.altitude) * t;
                p.velocity = b.velocity;
                return p;
            }
            return WithUT(points[points.Count - 1], ut);
        }

        internal static string Describe(ParachuteDescentPlan plan)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "terminal={0} touchdownUT={1:F3} touchdownAlt={2:F1} ground={3:F1} rate1={4:F2} " +
                "rate2={5:F2} switchAlt={6} switchUT={7} rateSource={8} points={9} " +
                "landingRate={10:F2} landingRateMeasured={11} safeRate={12:F1} outcome={13}",
                plan.Terminal,
                plan.TouchdownUT,
                plan.TouchdownAltitude,
                plan.GroundAltitude,
                plan.Phase1Rate,
                plan.Phase2Rate,
                IsFinite(plan.SwitchAltitude) ? plan.SwitchAltitude.ToString("F1", CultureInfo.InvariantCulture) : "-",
                IsFinite(plan.SwitchUT) ? plan.SwitchUT.ToString("F3", CultureInfo.InvariantCulture) : "-",
                plan.RateSource ?? "(none)",
                plan.Points != null ? plan.Points.Count : 0,
                plan.LandingRate,
                plan.LandingRateMeasured ? "True" : "False",
                MaxSafeLandingRate,
                plan.SurvivesTouchdown ? "touchdown" : "crash");
        }

        private static double ResolveRootOffset(double rootHeight)
        {
            if (!IsFinite(rootHeight) || rootHeight < 0.0 || rootHeight > MaxRootHeightAboveLowestPoint)
                return 0.0;
            return rootHeight;
        }

        private static TrajectoryPoint MakePoint(ParachuteDescentInput input, double ut, double altitude, double rate)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = input.Latitude,
                longitude = input.Longitude,
                altitude = altitude,
                rotation = input.SurfaceRelativeRotation,
                velocity = input.WorldUp * (float)(-rate),
                bodyName = input.BodyName,
                recordedGroundClearance = double.NaN,
            };
        }

        private static TrajectoryPoint WithUT(TrajectoryPoint p, double ut)
        {
            p.ut = ut;
            return p;
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
