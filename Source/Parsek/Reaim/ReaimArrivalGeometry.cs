using System;

namespace Parsek.Reaim
{
    // Pure arrival-v_inf geometry for the re-aim arrival-seam SOI-timing objective (docs/dev/plans/
    // reaim-arrival-seam-timing.md). All math is double precision with NO UnityEngine dependency, so it is
    // fully unit-testable off-Unity (ReaimArrivalGeometryTests). The live pieces (building a KSP Orbit from
    // the recorded ArrivalLeg, sampling the synthesized transfer's target-relative state) sit beside the
    // resolver because they need live KSP Orbits; this file owns only the frame-pure geometry.
    //
    // Salvaged from the closed PR #983 (ReaimRotation / ReaimElementRotation): the inbound-asymptote
    // direction + the double[3] vector ops. The ENTIRE rotation-and-apply path is DROPPED (v1 never rotates
    // / shifts / relocates the recorded destination leg). NEW here (not in the #983 salvage, which was
    // direction-only): the v_inf MAGNITUDE sqrt(mu/(-sma)), the candidate sma via vis-viva from (r_rel,
    // v_rel), the full v_inf vector (magnitude * direction), the arrival-v_inf-mismatch objective, and the
    // decline-to-faithful gate.
    //
    // FRAME DISCIPLINE (the #1 risk, same as #983): every vector fed here is in KSP's swizzled Zup
    // body-relative frame. The candidate v_inf and the recorded v_inf MUST both be computed in that same Zup
    // frame before differencing. Do NOT apply .xzy to positions/velocities before feeding them here; .xzy
    // converts to the Y-up world frame and corrupts the orientation. (getRelativePositionAtUT /
    // getOrbitalVelocityAtUT / GetEccVector / GetOrbitNormal all return Zup.)
    internal static class ReaimArrivalGeometry
    {
        private const double Epsilon = 1e-12;

        // double[3] vector helpers (deliberately tiny + allocation-light for the once-per-window path).

        internal static double[] Cross(double[] a, double[] b)
        {
            return new[]
            {
                a[1] * b[2] - a[2] * b[1],
                a[2] * b[0] - a[0] * b[2],
                a[0] * b[1] - a[1] * b[0]
            };
        }

        internal static double Dot(double[] a, double[] b)
        {
            return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        }

        internal static double Magnitude(double[] a)
        {
            return Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        }

        /// <summary>
        /// Returns <paramref name="a"/> scaled to unit length, or null when its magnitude is degenerate
        /// (zero / NaN / infinite). Pure. Callers must null-check (a degenerate input means the conic
        /// geometry was degenerate and the v_inf must not be built).
        /// </summary>
        internal static double[] Normalize(double[] a)
        {
            double m = Magnitude(a);
            if (m <= Epsilon || double.IsNaN(m) || double.IsInfinity(m))
                return null;
            return new[] { a[0] / m, a[1] / m, a[2] / m };
        }

        /// <summary>
        /// Analytic inbound hyperbolic asymptote direction from the conic's eccentricity vector
        /// <paramref name="eVec"/> and angular-momentum vector <paramref name="hVec"/> (frame-pure and
        /// UT-free). For an incoming hyperbola (ecc &gt; 1) the asymptote lies in the orbital plane; this
        /// returns the INBOUND branch (the v_inf direction the vessel arrives along).
        ///
        /// e_hat = e / |e|                (unit periapsis direction)
        /// h_hat = h / |h|                (unit plane normal)
        /// q_hat = h_hat cross e_hat      (in-plane, prograde at periapsis)
        /// s = normalize( sqrt(1 - 1/ecc^2) * e_hat + (ecc - 1/ecc) * q_hat )
        ///
        /// Returns null when ecc &lt;= 1 (no real asymptote) or either input vector is degenerate, so the
        /// caller falls back to the faithful path for that window. Salvaged from #983.
        /// </summary>
        internal static double[] InboundAsymptoteDir(double[] eVec, double[] hVec, double ecc)
        {
            if (eVec == null || hVec == null || double.IsNaN(ecc) || double.IsInfinity(ecc) || ecc <= 1.0)
                return null;
            double[] eHat = Normalize(eVec);
            double[] hHat = Normalize(hVec);
            if (eHat == null || hHat == null)
                return null;
            double[] qHat = Cross(hHat, eHat); // in-plane, prograde at periapsis
            double invE = 1.0 / ecc;
            double radial = Math.Sqrt(1.0 - invE * invE); // coefficient on e_hat
            double tangential = ecc - invE;                // coefficient on q_hat
            double[] s =
            {
                radial * eHat[0] + tangential * qHat[0],
                radial * eHat[1] + tangential * qHat[1],
                radial * eHat[2] + tangential * qHat[2]
            };
            return Normalize(s);
        }

        /// <summary>
        /// The hyperbolic-excess speed |v_inf| = sqrt(mu / (-sma)) for an arrival hyperbola of semi-major
        /// axis <paramref name="sma"/> (which is NEGATIVE for a hyperbola) about a body of gravitational
        /// parameter <paramref name="mu"/>. Returns NaN when the conic is not hyperbolic (sma &gt;= 0, an
        /// already-captured ellipse has no excess speed) or the inputs are degenerate (mu &lt;= 0 / NaN /
        /// infinite), so the caller falls back to the faithful path. NEW (not in the #983 salvage).
        /// </summary>
        internal static double HyperbolicExcessSpeed(double mu, double sma)
        {
            if (double.IsNaN(mu) || double.IsInfinity(mu) || mu <= 0.0
                || double.IsNaN(sma) || double.IsInfinity(sma) || sma >= 0.0)
                return double.NaN;
            return Math.Sqrt(mu / (-sma));
        }

        /// <summary>
        /// The semi-major axis of the conic implied by the target-relative state (<paramref name="rRel"/>,
        /// <paramref name="vRel"/>) about a body of gravitational parameter <paramref name="mu"/>, via
        /// vis-viva: 1/sma = 2/|r| - |v|^2/mu. For an arrival hyperbola this returns a NEGATIVE sma. Returns
        /// NaN on a degenerate input (zero-radius, mu &lt;= 0, NaN) OR when the energy is so close to
        /// parabolic that 1/sma underflows to (near) zero (|1/sma| &lt; eps), so the caller falls back to the
        /// faithful path rather than dividing by ~0. NEW (not in the #983 salvage).
        /// </summary>
        internal static double SemiMajorAxisFromState(double[] rRel, double[] vRel, double mu)
        {
            if (rRel == null || vRel == null
                || double.IsNaN(mu) || double.IsInfinity(mu) || mu <= 0.0)
                return double.NaN;
            double r = Magnitude(rRel);
            double v2 = Dot(vRel, vRel);
            if (r <= Epsilon || double.IsNaN(r) || double.IsInfinity(r)
                || double.IsNaN(v2) || double.IsInfinity(v2))
                return double.NaN;
            double invSma = 2.0 / r - v2 / mu;
            if (double.IsNaN(invSma) || double.IsInfinity(invSma) || Math.Abs(invSma) < 1e-30)
                return double.NaN;
            return 1.0 / invSma;
        }

        /// <summary>
        /// The eccentricity VECTOR of the conic implied by the target-relative state (<paramref name="rRel"/>,
        /// <paramref name="vRel"/>) about a body of gravitational parameter <paramref name="mu"/>:
        /// e = (v cross h)/mu - r_hat, where h = r cross v. Returns null on a degenerate input (so the
        /// caller falls back to faithful). Mirrors the #983 TryReaimedArrivalFrame e_re computation. NEW
        /// here as a standalone pure helper so the candidate (e, h) extraction is xUnit-tested.
        /// </summary>
        internal static double[] EccentricityVectorFromState(double[] rRel, double[] vRel, double mu)
        {
            if (rRel == null || vRel == null
                || double.IsNaN(mu) || double.IsInfinity(mu) || mu <= 0.0)
                return null;
            double[] rHat = Normalize(rRel);
            if (rHat == null)
                return null;
            double[] h = Cross(rRel, vRel);
            double[] vCrossH = Cross(vRel, h);
            return new[]
            {
                vCrossH[0] / mu - rHat[0],
                vCrossH[1] / mu - rHat[1],
                vCrossH[2] / mu - rHat[2]
            };
        }

        /// <summary>
        /// Builds the full inbound v_inf VECTOR (magnitude * direction) from a conic's eccentricity vector
        /// <paramref name="eVec"/>, angular-momentum vector <paramref name="hVec"/>, and semi-major axis
        /// <paramref name="sma"/> (hyperbolic => sma &lt; 0), about a body of gravitational parameter
        /// <paramref name="mu"/>. The direction is the analytic inbound asymptote; the magnitude is
        /// sqrt(mu / (-sma)). Returns null when the conic is not hyperbolic / the geometry is degenerate, so
        /// the caller declines to faithful. Pure.
        /// </summary>
        internal static double[] InboundVInfVector(double[] eVec, double[] hVec, double sma, double mu)
        {
            double ecc = eVec == null ? double.NaN : Magnitude(eVec);
            double[] dir = InboundAsymptoteDir(eVec, hVec, ecc);
            double mag = HyperbolicExcessSpeed(mu, sma);
            if (dir == null || double.IsNaN(mag) || double.IsInfinity(mag))
                return null;
            return new[] { mag * dir[0], mag * dir[1], mag * dir[2] };
        }

        /// <summary>
        /// The arrival-v_inf-mismatch objective: the magnitude of the vector difference
        /// |v_inf_cand - v_inf_rec| (both full vectors, magnitude * direction, in the SAME Zup frame). This
        /// is the per-window selection score (lower is better): the candidate transfer whose arrival v_inf
        /// best matches the recorded arrival v_inf wins. Returns NaN when either input is null (a degenerate
        /// / non-hyperbolic conic), so the caller skips that candidate. Pure.
        /// </summary>
        internal static double VInfMismatch(double[] vInfCand, double[] vInfRec)
        {
            if (vInfCand == null || vInfRec == null)
                return double.NaN;
            double[] d =
            {
                vInfCand[0] - vInfRec[0],
                vInfCand[1] - vInfRec[1],
                vInfCand[2] - vInfRec[2]
            };
            return Magnitude(d);
        }

        /// <summary>
        /// The angle (DEGREES) between two v_inf vectors, for the diagnostic log (the direction residual
        /// that timing cannot fully remove for an eccentric target). Returns NaN when either input is null /
        /// degenerate. Pure.
        /// </summary>
        internal static double AngleBetweenDegrees(double[] a, double[] b)
        {
            double[] aHat = Normalize(a);
            double[] bHat = Normalize(b);
            if (aHat == null || bHat == null)
                return double.NaN;
            double c = Dot(aHat, bHat);
            if (c > 1.0) c = 1.0;
            if (c < -1.0) c = -1.0;
            return Math.Acos(c) * 180.0 / Math.PI;
        }

        /// <summary>
        /// The decline-to-faithful gate (plan section 3). Accepts the v_inf-chosen transfer for a window
        /// ONLY when its seam magnitude is BOTH (i) strictly smaller than the faithful (position-targeted)
        /// seam AND (ii) below <paramref name="soiFraction"/> * <paramref name="targetSoiRadius"/> (start at
        /// 0.25 * SOI). Otherwise the window declines to the faithful transfer (the current cosmetic seam),
        /// so v1 can never regress. Returns true to ACCEPT the chosen transfer, false to decline. NaN /
        /// non-finite inputs decline (fail closed). Pure.
        /// </summary>
        internal static bool AcceptChosenOverFaithful(
            double chosenSeamMeters, double faithfulSeamMeters,
            double targetSoiRadius, double soiFraction)
        {
            if (double.IsNaN(chosenSeamMeters) || double.IsInfinity(chosenSeamMeters)
                || double.IsNaN(faithfulSeamMeters) || double.IsInfinity(faithfulSeamMeters)
                || double.IsNaN(targetSoiRadius) || double.IsInfinity(targetSoiRadius) || targetSoiRadius <= 0.0
                || double.IsNaN(soiFraction) || double.IsInfinity(soiFraction) || soiFraction <= 0.0)
                return false;
            bool strictlyBetter = chosenSeamMeters < faithfulSeamMeters;
            bool underTolerance = chosenSeamMeters < soiFraction * targetSoiRadius;
            return strictlyBetter && underTolerance;
        }
    }
}
