using System;
using System.Collections.Generic;
using Parsek.Reaim;

namespace Parsek.Tests
{
    // The Kerbin->Eve CYCLE-0 re-aim window, modelled headlessly from stock constants so the E1/E2
    // cells of docs/dev/plans/reaim-inclined-target-tilt-retention.md (Phase 0) can drive the REAL
    // production helpers (ReaimTransferSynthesizer.* / UvLambert.Solve / ReaimTofSearch.*) over the
    // exact geometry the V8-eve-player-loop lane measured in game on 2026-08-11. Consumed by
    // ReaimTransferSynthesizerTests (E1 + E3) and UvLambertTests (E2); it lives in its own file so the
    // two cell groups share ONE ephemeris model rather than two copies that can drift apart.
    //
    // WHAT THIS MODELS. A two-body Kepler ephemeris over the stock element set - enough to reproduce the
    // three quantities the tilt gate and the Lambert solve consume (r1 at the departure UT, Eve's r2/v2
    // at each candidate arrival, and the launch-plane handedness axis). It is FAITHFUL to the live
    // population rather than an analogue of it: V8's Eve unit departs DIRECT (`parking=False`,
    // LaunchCenter anchor), so r1 IS the launch body's center at departureUT, and the model reproduces
    // the live per-candidate transfer-plane inclinations TERM BY TERM - see
    // EveCycleZero_PlaneInclinationSequence_MatchesTheLiveV8Run, which is the strongest cell in the set.
    // What it does NOT model is the pipeline downstream of the solve (Orbit.UpdateFromStateVectors /
    // PatchedConics), which is Unity-bound and stays the in-game canary's job.
    //
    // FRAME (load-bearing, and the reason this helper exists at all). The production helpers measure
    // inclination against world +Y (KSP is Y-up; ReaimTransferSynthesizer.cs:128-131 records the
    // shipped .z-vs-.y bug that a Z-up model would silently re-introduce here). A textbook orbital-
    // elements derivation produces a Z-up (ecliptic-normal = +z) frame, so <see cref="StateAt"/>
    // relabels its output (x, y, z) -> (x, z, y) before returning. That relabel is what makes
    // ecliptic-z land on KSP Y. The guard against getting it wrong is a cell, not a comment:
    // EveCycleZero_ModelFrameIsYUp_FlatOrbitReadsZeroInclination asserts that Kerbin's (i=0) plane
    // normal reads inclination ~0 and that Eve's reads its declared 2.1 deg - both of which come out
    // ~90 deg if the relabel is dropped.
    internal static class EveCycleZeroGeometry
    {
        // --- Stock constants (Kerbol system, KSP 1.12.5). ---
        internal const double SunMu = 1.1723328e18;          // m^3/s^2
        internal const double EveSoiMeters = 85109365.0;     // Eve's sphere of influence
        internal const double EveInclinationDegrees = 2.1;   // Eve's own orbital inclination
        internal const double EveEccentricity = 0.01;        // gates ReaimTofSearch's band width
        internal const double KerbinInclinationDegrees = 0.0;

        // --- The measured V8 window (2026-08-11, five bit-identical runs). ---
        // The cycle-0 departure UT and the two tofs the resolver logged for it.
        internal const double DepartureUT = 26616878.032152794;
        internal const double RecordedTofSeconds = 3743340.7887964416;
        internal const double GeomTofSeconds = 3679663.0363263134;

        /// <summary>Classical elements of a body's parent-relative orbit (angles in degrees; the mean
        /// anomaly at epoch in radians, matching KSP's own <c>mna</c> convention with epoch = UT 0).</summary>
        internal struct Elements
        {
            internal double SemiMajorAxis;
            internal double Eccentricity;
            internal double InclinationDegrees;
            internal double LanDegrees;
            internal double ArgPeDegrees;
            internal double MeanAnomalyAtEpochRadians;
        }

        internal static Elements Kerbin => new Elements
        {
            SemiMajorAxis = 13599840256.0,
            Eccentricity = 0.0,
            InclinationDegrees = 0.0,
            LanDegrees = 0.0,
            ArgPeDegrees = 0.0,
            MeanAnomalyAtEpochRadians = 3.14,
        };

        internal static Elements Eve => new Elements
        {
            SemiMajorAxis = 9832684544.0,
            Eccentricity = EveEccentricity,
            InclinationDegrees = EveInclinationDegrees,
            LanDegrees = 15.0,
            ArgPeDegrees = 0.0,
            MeanAnomalyAtEpochRadians = 3.14,
        };

        /// <summary>
        /// Parent-relative position + velocity of <paramref name="el"/> at <paramref name="ut"/>, in the
        /// KSP world (Y-up) frame the production helpers work in. Two-body Kepler; see the frame note on
        /// the class.
        /// </summary>
        internal static void StateAt(Elements el, double ut, out Vector3d position, out Vector3d velocity)
        {
            double a = el.SemiMajorAxis;
            double e = el.Eccentricity;
            double n = Math.Sqrt(SunMu / (a * a * a));
            double eccAnomaly = SolveEccentricAnomaly(el.MeanAnomalyAtEpochRadians + n * ut, e);
            double cosE = Math.Cos(eccAnomaly), sinE = Math.Sin(eccAnomaly);
            double beta = Math.Sqrt(1.0 - e * e);

            // Perifocal state (the orbit's own plane, periapsis on +x).
            double xPf = a * (cosE - e);
            double yPf = a * beta * sinE;
            double eDot = n / (1.0 - e * cosE);              // dE/dt
            double vxPf = -a * sinE * eDot;
            double vyPf = a * beta * cosE * eDot;

            // Perifocal -> Z-up reference frame: Rz(LAN) * Rx(inc) * Rz(argPe).
            double inc = el.InclinationDegrees * Math.PI / 180.0;
            double lan = el.LanDegrees * Math.PI / 180.0;
            double argPe = el.ArgPeDegrees * Math.PI / 180.0;
            position = ToWorld(RotateToReferenceFrame(xPf, yPf, argPe, inc, lan));
            velocity = ToWorld(RotateToReferenceFrame(vxPf, vyPf, argPe, inc, lan));
        }

        /// <summary>
        /// The ORDERED candidate time-of-flight list for this window, built by the PRODUCT'S OWN band
        /// law rather than a re-derivation, and by the builder the DIRECT departure path actually
        /// selects: <c>ReaimPlaybackResolver.cs:456-458</c> dispatches on <c>hasDepartureOverride</c>,
        /// and V8's Eve unit departs direct (`parking=False`, LaunchCenter anchor), so the band is
        /// <see cref="ReaimTofSearch.BuildCandidateTofs"/> - centered on the RECORDED tof, base +-6% in
        /// +k,-k order, then the eccentricity expansion step (k=13, since HalfWidthFraction(0.01) =
        /// 0.065) probing the geomTof side first. 27 candidates, the count the V8 runs logged
        /// (`synth failed across 27 tof candidates`), in the order those runs logged them.
        /// </summary>
        internal static IReadOnlyList<double> CandidateTofs()
            => ReaimTofSearch.BuildCandidateTofs(RecordedTofSeconds, GeomTofSeconds, EveEccentricity);

        /// <summary>
        /// The band the PARKING departure path would use (<see cref="ReaimTofSearch.BuildParkingCandidateTofs"/>,
        /// centered on geomTof). NOT this window's band - kept only so a cell can show that the gate's
        /// candidate-invariance is a property of the gate's inputs, not of which builder ran.
        /// </summary>
        internal static IReadOnlyList<double> ParkingCandidateTofs()
            => ReaimTofSearch.BuildParkingCandidateTofs(GeomTofSeconds, EveEccentricity);

        /// <summary>
        /// The inclination (degrees) of the plane whose normal is <paramref name="planeNormal"/>: the
        /// angle from the KSP world reference-plane normal +Y, <c>acos(|n.y|/|n|)</c>. Mirrors what
        /// <see cref="ReaimTransferSynthesizer.AchievablePlaneInclinationDegrees"/> measures after its
        /// r-hat projection, and what stock <c>Orbit.inclination</c> reports for a conic.
        /// </summary>
        internal static double InclinationOfNormalDegrees(Vector3d planeNormal)
        {
            double m = planeNormal.magnitude;
            if (m <= 0.0 || double.IsNaN(m))
                return double.NaN;
            double c = Math.Abs(planeNormal.y) / m;
            if (c > 1.0) c = 1.0;
            if (c < -1.0) c = -1.0;
            return Math.Acos(c) * 180.0 / Math.PI;
        }

        /// <summary>The unsigned angle (degrees) between two position vectors - the transfer angle.</summary>
        internal static double TransferAngleDegrees(Vector3d r1, Vector3d r2)
        {
            double c = Vector3d.Dot(r1, r2) / (r1.magnitude * r2.magnitude);
            if (c > 1.0) c = 1.0;
            if (c < -1.0) c = -1.0;
            return Math.Acos(c) * 180.0 / Math.PI;
        }

        /// <summary>
        /// The plane normal a conic through <paramref name="r1"/> and <paramref name="r2"/> must carry:
        /// <c>r1 x r2</c>. UvLambert returns v1 in span(r1, r2), so the solved conic's plane IS this one
        /// (the H1 mechanism), which is why the E1 cells can measure it without solving.
        /// </summary>
        internal static Vector3d PlaneNormalOfEndpoints(Vector3d r1, Vector3d r2)
            => Vector3d.Cross(r1, r2);

        /// <summary>
        /// The launch-plane handedness axis exactly as
        /// <c>ReaimTransferSynthesizer.TrySynthesizeTransfer</c> builds it for the direct path
        /// (ReaimTransferSynthesizer.cs:359-361): <c>r1 x v_launch</c> at the departure anchor.
        /// </summary>
        internal static Vector3d LaunchPlaneNormal(Vector3d r1, Vector3d launchVelocity)
            => Vector3d.Cross(r1, launchVelocity);

        /// <summary>
        /// The out-of-plane arrival miss the tilt CORRECTION would produce if it fired:
        /// <c>|dot(r2, n_ach)|</c>, the distance from Eve's actual arrival position to the best plane
        /// reachable while holding r1 fixed. Compared against Eve's SOI this is the physically
        /// load-bearing quantity the gate is standing in for (plan section 1, Design B).
        /// </summary>
        internal static double FlattenMissMeters(Vector3d r1, Vector3d r2, Vector3d intendedNormal)
        {
            Vector3d rHat = r1 / r1.magnitude;
            Vector3d nAch = intendedNormal - Vector3d.Dot(rHat, intendedNormal) * rHat;
            double m = nAch.magnitude;
            if (m <= 0.0 || double.IsNaN(m))
                return double.NaN;
            return Math.Abs(Vector3d.Dot(r2, nAch / m));
        }

        // --- internals ---

        // Newton on M = E - e sin E. e here is 0 (Kerbin) / 0.01 (Eve), so this converges in a handful
        // of iterations from M; the pi seed for e >= 0.8 mirrors stock's own extreme-eccentricity
        // dispatch and is unused by this fixture.
        private static double SolveEccentricAnomaly(double meanAnomalyRadians, double e)
        {
            double m = meanAnomalyRadians % (2.0 * Math.PI);
            if (m < 0.0) m += 2.0 * Math.PI;
            double eccAnomaly = e < 0.8 ? m : Math.PI;
            for (int i = 0; i < 200; i++)
            {
                double f = eccAnomaly - e * Math.Sin(eccAnomaly) - m;
                double fPrime = 1.0 - e * Math.Cos(eccAnomaly);
                double delta = -f / fPrime;
                eccAnomaly += delta;
                if (Math.Abs(delta) < 1e-14)
                    break;
            }
            return eccAnomaly;
        }

        // Rz(lan) * Rx(inc) * Rz(argPe) applied to an in-plane (perifocal) vector; returns Z-up components.
        private static Vector3d RotateToReferenceFrame(double xPf, double yPf, double argPe, double inc, double lan)
        {
            double x1 = xPf * Math.Cos(argPe) - yPf * Math.Sin(argPe);
            double y1 = xPf * Math.Sin(argPe) + yPf * Math.Cos(argPe);
            double z1 = 0.0;

            double x2 = x1;
            double y2 = y1 * Math.Cos(inc) - z1 * Math.Sin(inc);
            double z2 = y1 * Math.Sin(inc) + z1 * Math.Cos(inc);

            double x3 = x2 * Math.Cos(lan) - y2 * Math.Sin(lan);
            double y3 = x2 * Math.Sin(lan) + y2 * Math.Cos(lan);
            double z3 = z2;

            return new Vector3d(x3, y3, z3);
        }

        // Z-up (ecliptic normal = +z) -> KSP world (Y-up): relabel (x, y, z) -> (x, z, y). See the
        // class-level FRAME note; the flat-orbit cell is the guard.
        private static Vector3d ToWorld(Vector3d zUp) => new Vector3d(zUp.x, zUp.z, zUp.y);
    }
}
