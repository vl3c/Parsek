using System;

namespace Parsek.Reaim
{
    // Universal-variable (Bate-Mueller-White / Curtis "Orbital Mechanics for Engineering Students",
    // Algorithm 5.2) single-revolution Lambert solver: given two position vectors, a time of flight,
    // and the central gravitational parameter, find the velocity at each endpoint of the connecting
    // conic. Re-aim uses it to re-plan the heliocentric transfer to the target's CURRENT position each
    // window (docs/dev/plans/reaim-interplanetary-transfers.md, Phase 1).
    //
    // PURE: Vector3d (double) math only, no Unity scene state, no shared mutable state - fully
    // unit-testable against textbook solutions (Curtis Example 5.2). OUR code (not a port).
    // Single-revolution only (re-aim does not need multi-rev).
    //
    // FAIL-CLOSED on non-convergence: the y<0 z-bump is the Curtis recipe for a PROGRADE transfer
    // < 180 deg (A>0); for a transfer > 180 deg (A<0) Newton can diverge, so the solver verifies the
    // time residual at the final z and returns false if it is not ~0 (never returns a plausible-looking
    // but wrong conic). The caller (ReaimTransferSynthesizer) additionally validates the conic's
    // eccentricity and steps to the next window on any failure - so an off-phase departure simply
    // yields no transfer rather than a garbage one (plan review C-1 / M3).
    internal static class UvLambert
    {
        internal const int MaxNewtonIterations = 60;
        // Below this |sin(transfer angle)| the transfer plane is undefined (~0 or ~180 deg): bail so
        // the caller skips the window rather than returning a garbage conic (plan review M3).
        internal const double MinSinTransferAngle = 1e-6;

        /// <summary>Stumpff C(z) = (1-cos(sqrt z))/z for z&gt;0, (cosh(sqrt -z)-1)/(-z) for z&lt;0, 1/2 at 0.</summary>
        internal static double StumpffC(double z)
        {
            if (z > 1e-9)
            {
                double s = Math.Sqrt(z);
                return (1.0 - Math.Cos(s)) / z;
            }
            if (z < -1e-9)
            {
                double s = Math.Sqrt(-z);
                return (Math.Cosh(s) - 1.0) / (-z);
            }
            return 0.5; // series limit at z -> 0
        }

        /// <summary>Stumpff S(z) = (sqrt z - sin(sqrt z))/(sqrt z)^3 for z&gt;0, (sinh-sqrt)/... for z&lt;0, 1/6 at 0.</summary>
        internal static double StumpffS(double z)
        {
            if (z > 1e-9)
            {
                double s = Math.Sqrt(z);
                return (s - Math.Sin(s)) / (s * s * s);
            }
            if (z < -1e-9)
            {
                double s = Math.Sqrt(-z);
                return (Math.Sinh(s) - s) / (s * s * s);
            }
            return 1.0 / 6.0; // series limit at z -> 0
        }

        /// <summary>
        /// Solves Lambert's problem. Returns true with <paramref name="v1"/>/<paramref name="v2"/> (the
        /// velocity at <paramref name="r1"/> and <paramref name="r2"/>) on success; false on a degenerate
        /// geometry (collinear endpoints / ~0 or ~180 deg transfer) or non-convergence - the caller then
        /// skips the window. <paramref name="prograde"/> selects the prograde (vs retrograde) transfer
        /// branch via the sign of the transfer angle from r1 x r2. Units must be consistent
        /// (mu m^3/s^2, positions m, tof s -&gt; velocities m/s; or km throughout). Pure.
        /// </summary>
        /// <remarks>
        /// Thin forwarder to the plane-normal overload with <c>planeNormal = Vector3d.zero</c>, so the
        /// handedness selector falls back to the historical <c>cross.z</c> branch (byte-identical to the
        /// pre-handedness-fix behaviour). Every legacy caller and the Curtis 5.2 textbook case route
        /// through here unchanged.
        /// </remarks>
        internal static bool Solve(
            double mu, Vector3d r1, Vector3d r2, double tof, bool prograde,
            out Vector3d v1, out Vector3d v2)
            => Solve(mu, r1, r2, tof, prograde, Vector3d.zero, out v1, out v2);

        /// <summary>
        /// Plane-normal overload of <see cref="Solve(double, Vector3d, Vector3d, double, bool, out Vector3d, out Vector3d)"/>.
        /// Identical contract, plus an optional <paramref name="planeNormal"/> that supplies a STABLE
        /// handedness axis for the prograde/retrograde branch. When a non-degenerate normal is supplied
        /// the branch rides <c>dot(r1 x r2, planeNormal)</c> (the projection of the transfer plane normal
        /// onto a fixed reference axis) instead of the noise-dominated <c>cross.z</c> component; near a
        /// ~180-degree transfer angle <c>cross.z</c> flips sign on rounding noise and selects the wrong
        /// (retrograde inc=180) branch, while the projection onto a well-defined normal stays stable.
        /// <paramref name="planeNormal"/> = <see cref="Vector3d.zero"/> (or NaN) => legacy <c>cross.z</c>
        /// behaviour, so this is a strict superset of the 7-arg solve. Pure.
        /// </summary>
        internal static bool Solve(
            double mu, Vector3d r1, Vector3d r2, double tof, bool prograde, Vector3d planeNormal,
            out Vector3d v1, out Vector3d v2)
        {
            v1 = Vector3d.zero;
            v2 = Vector3d.zero;
            if (double.IsNaN(mu) || double.IsInfinity(mu) || mu <= 0.0
                || double.IsNaN(tof) || double.IsInfinity(tof) || tof <= 0.0)
                return false;

            double r1m = r1.magnitude, r2m = r2.magnitude;
            if (r1m <= 0.0 || r2m <= 0.0)
                return false;

            double cosdnu = Vector3d.Dot(r1, r2) / (r1m * r2m);
            if (cosdnu > 1.0) cosdnu = 1.0;
            if (cosdnu < -1.0) cosdnu = -1.0;

            // Transfer angle (0..2pi). The prograde/retrograde branch handedness comes from
            // dot(r1 x r2, planeNormal) when a stable plane normal is supplied (the projection of the
            // transfer-plane normal onto a fixed reference axis), falling back to cross.z otherwise. Near
            // a ~180-degree transfer angle cross.z is noise-dominated and flips sign on rounding, picking
            // the wrong (retrograde inc=180) branch; projecting onto a well-defined normal keeps the
            // branch stable. With planeNormal = Vector3d.zero/NaN this is identical to the historical
            // cross.z path (the 7-arg forwarder and Curtis 5.2 are byte-identical).
            Vector3d cross = Vector3d.Cross(r1, r2);
            double handed = (planeNormal.sqrMagnitude > 0.0 && !double.IsNaN(planeNormal.sqrMagnitude))
                ? Vector3d.Dot(cross, planeNormal)
                : cross.z;
            double baseAngle = Math.Acos(cosdnu);
            double dnu = prograde
                ? (handed >= 0.0 ? baseAngle : (2.0 * Math.PI - baseAngle))
                : (handed < 0.0 ? baseAngle : (2.0 * Math.PI - baseAngle));

            double sindnu = Math.Sin(dnu);
            if (Math.Abs(sindnu) < MinSinTransferAngle)
                return false; // ~0/~180 deg: transfer plane undefined (caller steps to next window)

            // A = sign(sin dnu) * sqrt(r1*r2*(1+cos dnu)) (equivalent to Curtis A = sin*sqrt(r1 r2/(1-cos))).
            double A = (sindnu > 0.0 ? 1.0 : -1.0) * Math.Sqrt(r1m * r2m * (1.0 + cosdnu));
            if (A == 0.0)
                return false;

            double sqrtMu = Math.Sqrt(mu);
            double z = 0.0;

            // Newton on F(z) = (y/C)^1.5 * S + A*sqrt(y) - sqrt(mu)*tof, with a y<0 z-bump (Curtis 5.2).
            for (int iter = 0; iter < MaxNewtonIterations; iter++)
            {
                double C = StumpffC(z);
                double S = StumpffS(z);
                double y = r1m + r2m + A * (z * S - 1.0) / Math.Sqrt(C);

                // y must stay positive; for A>0 a too-low z drives it negative -> bump z up.
                int bump = 0;
                while (y < 0.0 && bump < MaxNewtonIterations)
                {
                    z += 0.1;
                    C = StumpffC(z);
                    S = StumpffS(z);
                    y = r1m + r2m + A * (z * S - 1.0) / Math.Sqrt(C);
                    bump++;
                }
                if (y < 0.0 || double.IsNaN(y))
                    return false;

                double sqrtY = Math.Sqrt(y);
                double x = Math.Sqrt(y / C);
                double F = x * x * x * S + A * sqrtY - sqrtMu * tof;

                double dFdz;
                if (Math.Abs(z) > 1e-9)
                {
                    dFdz = Math.Pow(y / C, 1.5)
                            * ((1.0 / (2.0 * z)) * (C - 3.0 * S / (2.0 * C)) + 3.0 * S * S / (4.0 * C))
                         + (A / 8.0) * (3.0 * (S / C) * sqrtY + A * Math.Sqrt(C / y));
                }
                else
                {
                    double y0 = r1m + r2m - A * Math.Sqrt(2.0);
                    if (y0 <= 0.0)
                        return false;
                    dFdz = (Math.Sqrt(2.0) / 40.0) * Math.Pow(y0, 1.5)
                         + (A / 8.0) * (Math.Sqrt(y0) + A * Math.Sqrt(1.0 / (2.0 * y0)));
                }

                if (dFdz == 0.0 || double.IsNaN(dFdz))
                    return false;

                double zNext = z - F / dFdz;
                if (double.IsNaN(zNext) || double.IsInfinity(zNext))
                    return false;
                if (Math.Abs(zNext - z) <= 1e-8 * Math.Max(1.0, Math.Abs(z)))
                {
                    z = zNext;
                    break;
                }
                z = zNext;
            }

            // Final state from the converged z (Lagrange coefficients).
            double Cf = StumpffC(z);
            double Sf = StumpffS(z);
            if (Cf <= 1e-12)
                return false; // z ran into the C(z)->0 singularity (~ (2pi)^2): diverged
            double yf = r1m + r2m + A * (z * Sf - 1.0) / Math.Sqrt(Cf);
            if (yf <= 0.0 || double.IsNaN(yf))
                return false;

            // FAIL CLOSED on non-convergence (review C-1). The y<0 bump above only ever RAISES z,
            // which is the Curtis recipe for a prograde transfer < 180 deg (A>0); for a transfer
            // > 180 deg (A<0) Newton can diverge (z runs away) yet still land on a plausible-looking
            // bound ellipse that IsSaneTransferConic would accept and that misses the target by
            // hundreds of percent. The iteration count alone does not prove convergence, so verify the
            // time residual: F(z) = (y/C)^1.5 * S + A*sqrt(y) - sqrt(mu)*tof must be ~0 relative to the
            // sqrt(mu)*tof scale. Converged solutions sit at ~1e-13 relative; diverged ones at >> 1.
            double xf = Math.Sqrt(yf / Cf);
            double residual = xf * xf * xf * Sf + A * Math.Sqrt(yf) - sqrtMu * tof;
            double residualScale = Math.Abs(sqrtMu * tof);
            if (double.IsNaN(residual) || Math.Abs(residual) > 1e-6 * Math.Max(1.0, residualScale))
                return false;

            double f = 1.0 - yf / r1m;
            double g = A * Math.Sqrt(yf / mu);
            double gdot = 1.0 - yf / r2m;
            if (Math.Abs(g) < 1e-12 || double.IsNaN(g))
                return false;

            v1 = (r2 - f * r1) / g;
            v2 = (gdot * r2 - r1) / g;
            return !(double.IsNaN(v1.x) || double.IsNaN(v2.x));
        }
    }
}
