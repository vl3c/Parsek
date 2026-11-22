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
        // Upward non-gravitational acceleration over local gravity (drag / weight while the
        // vessel falls under its canopies); 1 at terminal speed. Zero, negative or NaN when it
        // was not read (an unloaded or packed vessel computes none).
        public double DragToWeightRatio;
        // Summed stock chute drag (ModuleParachute.semiDeployedDrag / fullyDeployedDrag) of
        // the OPEN chutes in their current state, and of the same chutes fully open. Zero or
        // NaN when any open chute's drag was not read.
        // RealChute canopies are summed as open canopy area (m^2) instead, never mixed with
        // stock drag (OpenChuteTally.CombineDrag); only the ratio is read.
        public double OpenChuteDragNow;
        public double OpenChuteDragFull;
        // Which sums those are: stock, realchute-area, mixed (NaN) or "-"; null when unset.
        public string OpenChuteDragSource;
        // Air density (kg/m^3) at the vessel and at the ground under it; zero or NaN unread.
        public double DensityAtAltitude;
        public double DensityAtTouchdown;
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
        // estimated from measurements (false: DefaultFullCanopyDescentRate) and whether the
        // vessel survives it; then the estimate's source and factors
        // (ParachuteDescentPlanner.EstimateFullCanopyRate).
        public double LandingRate;
        public bool LandingRateMeasured;
        public bool SurvivesTouchdown;
        public string LandingRateSource;
        public double TerminalFactor;
        public string TerminalSpeedState;
        public double ChuteDragFactor;
        public double DensityFactor;
        public bool DensityRead;
        public List<TrajectoryPoint> Points;
    }

    /// <summary>
    /// Owner ruling Q4: a stage left with parachutes open descends at chute speed and ends
    /// Landed / Splashed where it comes down. Pure: the body-fixed lat / lon is kept (chutes
    /// kill horizontal speed, and the air turns with the planet), and the vessel descends at
    /// a constant speed to the ground. Owner ruling 2026-11-21: it survives the touchdown only
    /// when its rate under fully open canopies is at or below
    /// <see cref="MaxSafeLandingRate"/>; faster, it crashes (Destroyed at ground impact, like a
    /// fall). That rate is <see cref="EstimateFullCanopyRate"/>: the measured descent rate
    /// scaled to terminal speed, to fully open chutes and to the air at the ground.
    /// Fully deployed: the trajectory descends at the measured rate, not clamped (below
    /// <see cref="MinDescentRate"/> it is not descending yet and takes
    /// <see cref="DefaultFullCanopyDescentRate"/>).
    /// Semi-deployed: the current rate (clamped to
    /// <see cref="MaxSemiDeployedDescentRate"/>, <see cref="DefaultSemiDeployedDescentRate"/>
    /// when not descending) down to the chutes' full-deploy altitude, then the full-canopy
    /// estimate. The ground is the terrain height there; below sea level on a body with
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
        // Drag / weight bounds of the terminal-speed scaling (a factor of 3.16 either way):
        // below, the canopy barely holds the vessel and its speed says little; above, a
        // just-opened canopy's spike.
        internal const double MinDragToWeightRatio = 0.1;
        internal const double MaxDragToWeightRatio = 10.0;
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
            FullCanopyRateEstimate landing = EstimateFullCanopyRate(input);
            if (input.Canopy == ParachuteCanopyState.Deployed)
            {
                // The trajectory keeps the measured rate; the touchdown is judged on the
                // estimate at the ground.
                rate1 = ResolveDeployedRate(input.VerticalSpeed, out rateSource);
                rate2 = rate1;
            }
            else
            {
                double deployAgl = IsFinite(input.FullDeployAltitudeAgl) && input.FullDeployAltitudeAgl > 0.0
                    ? input.FullDeployAltitudeAgl
                    : DefaultFullDeployAltitudeAgl;
                double fullDeployAltitude = ground + deployAgl;
                rate2 = landing.Rate;
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

            bool survives = landing.Rate <= MaxSafeLandingRate;
            plan = new ParachuteDescentPlan
            {
                Terminal = !survives
                    ? TerminalState.Destroyed
                    : splashed ? TerminalState.Splashed : TerminalState.Landed,
                LandingRate = landing.Rate,
                LandingRateMeasured = landing.Measured,
                LandingRateSource = landing.Source,
                TerminalFactor = landing.TerminalFactor,
                TerminalSpeedState = landing.TerminalSpeedState,
                ChuteDragFactor = landing.ChuteDragFactor,
                DensityFactor = landing.DensityFactor,
                DensityRead = landing.DensityRead,
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

        /// <summary>The full-canopy descent rate a touchdown is judged on, and what it read.</summary>
        internal struct FullCanopyRateEstimate
        {
            public double Rate;
            // False: DefaultFullCanopyDescentRate, nothing to scale from.
            public bool Measured;
            public string Source;
            public double TerminalFactor;
            public string TerminalSpeedState;
            public double ChuteDragFactor;
            public double DensityFactor;
            public bool DensityRead;
        }

        /// <summary>
        /// Pure (owner ruling Q11): the descent rate under fully open canopies at the ground.
        /// Terminal speed is v = sqrt(2 m g / (rho Cd A)), so from the measured descent rate v:
        /// v_land = v * TerminalSpeedFactor(drag / weight) * sqrt(D_now / D_full)
        /// * sqrt(rho_here / rho_ground), where D is the open chutes' summed stock drag (now and
        /// fully open; armed chutes are not in it, Q10b). A fully deployed stage with no drag
        /// read takes D_now / D_full = 1; a semi-deployed one cannot be scaled and takes
        /// <see cref="DefaultFullCanopyDescentRate"/> (source <c>default-no-drag-data</c>), as
        /// does any stage not descending (<c>default-not-descending</c>). Unread densities take
        /// a ratio of 1. Known optimism: the vessel's own (non-chute) drag is left out of D,
        /// which reads a semi-deployed stage slightly slower than it will be.
        /// </summary>
        internal static FullCanopyRateEstimate EstimateFullCanopyRate(ParachuteDescentInput input)
        {
            var e = new FullCanopyRateEstimate
            {
                Rate = DefaultFullCanopyDescentRate,
                Measured = false,
                TerminalFactor = 1.0,
                TerminalSpeedState = "-",
                ChuteDragFactor = 1.0,
                DensityFactor = 1.0,
            };
            double v = -input.VerticalSpeed;
            if (!IsFinite(v) || v < MinDescentRate)
            {
                e.Source = "default-not-descending";
                return e;
            }
            bool semi = input.Canopy == ParachuteCanopyState.SemiDeployed;
            if (IsPositiveFinite(input.OpenChuteDragNow) && IsPositiveFinite(input.OpenChuteDragFull))
            {
                e.ChuteDragFactor = Math.Sqrt(Math.Min(1.0, input.OpenChuteDragNow / input.OpenChuteDragFull));
            }
            else if (semi)
            {
                e.Source = "default-no-drag-data";
                return e;
            }
            e.TerminalFactor = TerminalSpeedFactor(input.DragToWeightRatio, out e.TerminalSpeedState);
            if (IsPositiveFinite(input.DensityAtAltitude) && IsPositiveFinite(input.DensityAtTouchdown))
            {
                e.DensityFactor = Math.Sqrt(input.DensityAtAltitude / input.DensityAtTouchdown);
                e.DensityRead = true;
            }
            e.Rate = Math.Max(MinDescentRate, v * e.TerminalFactor * e.ChuteDragFactor * e.DensityFactor);
            e.Measured = true;
            e.Source = semi ? "semi-estimated" : "deployed-measured";
            return e;
        }

        /// <summary>
        /// Pure: the factor from the measured descent speed to the terminal one,
        /// 1 / sqrt(drag / weight) (a near-vertical fall has net upward acceleration
        /// a = k v^2 - g, so v_t = v * sqrt(g / (a + g))). The ratio is clamped to
        /// [<see cref="MinDragToWeightRatio"/>, <see cref="MaxDragToWeightRatio"/>]
        /// (<c>clamped</c>); zero, negative or NaN means not read, terminal speed assumed
        /// (<c>assumed</c>, factor 1).
        /// </summary>
        internal static double TerminalSpeedFactor(double dragToWeight, out string state)
        {
            if (!IsPositiveFinite(dragToWeight))
            {
                state = "assumed";
                return 1.0;
            }
            double ratio = dragToWeight;
            state = "read";
            if (ratio < MinDragToWeightRatio)
            {
                ratio = MinDragToWeightRatio;
                state = "clamped";
            }
            else if (ratio > MaxDragToWeightRatio)
            {
                ratio = MaxDragToWeightRatio;
                state = "clamped";
            }
            return 1.0 / Math.Sqrt(ratio);
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
                "landingRate={10:F2} landingRateMeasured={11} safeRate={12:F1} outcome={13} " +
                "landingSource={14} terminalFactor={15:F3} terminal={16} chuteDragFactor={17:F3} " +
                "densityFactor={18:F3} density={19}",
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
                plan.SurvivesTouchdown ? "touchdown" : "crash",
                plan.LandingRateSource ?? "(none)",
                plan.TerminalFactor,
                plan.TerminalSpeedState ?? "-",
                plan.ChuteDragFactor,
                plan.DensityFactor,
                plan.DensityRead ? "read" : "unread");
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

        private static bool IsPositiveFinite(double v)
        {
            return IsFinite(v) && v > 0.0;
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
