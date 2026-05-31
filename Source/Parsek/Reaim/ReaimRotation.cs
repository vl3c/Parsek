using System;

namespace Parsek.Reaim
{
    // Pure rotation primitives for the re-aim arrival-seam restitch (docs/dev/plans/
    // reaim-arrival-seam-restitch.md, sections 4.2 / 4.3). All math is double precision with NO
    // UnityEngine dependency, so it is fully unit-testable off-Unity (ReaimRotationTests). The live
    // element read-back (sampling a KSP Orbit's state, rotating it, and reading back inc/LAN/argPe via
    // KSP's own UpdateFromStateVectors) lives beside the resolver because it needs live KSP Orbits; this
    // file owns only the frame-pure geometry.
    //
    // FRAME DISCIPLINE (the #1 risk, plan 4.1): every vector fed here is in KSP's swizzled Zup
    // body-relative frame (the SAME frame new Orbit(inc, ecc, sma, LAN, argPe, mEp, epoch, body)
    // interprets its angles in). R maps Zup -> Zup. Do NOT apply .xzy to velocities/positions before
    // feeding them here; .xzy converts to the Y-up world frame and corrupts the orientation.
    internal static class ReaimRotation
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
        /// (zero / NaN). Pure. Callers must null-check (a degenerate input means the conic geometry was
        /// degenerate and the rotation must not be built).
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
        /// <paramref name="eVec"/> and angular-momentum vector <paramref name="hVec"/> (plan 4.3),
        /// frame-pure and UT-free. For an incoming hyperbola (ecc &gt; 1) the asymptote lies in the
        /// orbital plane; this returns the INBOUND branch (the v_inf direction the vessel arrives along).
        ///
        /// e_hat = e / |e|                (unit periapsis direction)
        /// h_hat = h / |h|                (unit plane normal)
        /// q_hat = h_hat cross e_hat      (in-plane, prograde at periapsis)
        /// s = normalize( sqrt(1 - 1/ecc^2) * e_hat + (ecc - 1/ecc) * q_hat )
        ///
        /// Returns null when ecc &lt;= 1 (no real asymptote) or either input vector is degenerate, so the
        /// caller falls back to the faithful (no-rotation) path for that window.
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
        /// Builds the proper rotation matrix (plan 4.2) that maps the recorded incoming arrival frame onto
        /// the re-aimed approach frame, matching BOTH the inbound asymptote direction and the orbital-plane
        /// normal:
        ///
        /// x = sFrom,  z = normalize(hFrom),  y = z cross x       (recorded frame columns)
        /// x'= sTo,    z'= normalize(hTo),    y'= z' cross x'      (re-aimed frame columns)
        /// R = [x' y' z'] * [x y z]^T
        ///
        /// Returns a 3x3 double[,] proper rotation (det +1). Returns null when any input is degenerate
        /// (zero-length, NaN) so the caller skips the rotation. Callers MUST first apply the 4.2 handedness
        /// guard (dot(normalize(hFrom), normalize(hTo)) &gt; 0); this method does not enforce it, but a
        /// retrograde join would still produce a valid rotation, so the guard is the caller's responsibility.
        /// </summary>
        internal static double[,] RotationFrameToFrame(double[] sFrom, double[] hFrom, double[] sTo, double[] hTo)
        {
            double[,] from = OrthonormalFrame(sFrom, hFrom);
            double[,] to = OrthonormalFrame(sTo, hTo);
            if (from == null || to == null)
                return null;
            // R = to * from^T. from/to columns are the basis vectors; from^T maps a world vector into
            // recorded-frame coordinates, then to maps those coordinates back out in the re-aimed frame.
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    double sum = 0.0;
                    for (int k = 0; k < 3; k++)
                        sum += to[i, k] * from[j, k]; // to[:,k] dot from[:,k] == (to * from^T)[i,j]
                    r[i, j] = sum;
                }
            }
            return r;
        }

        /// <summary>
        /// Builds the orthonormal frame columns x = normalize(s), z = normalize(h), y = z cross x from an
        /// in-plane direction <paramref name="s"/> and the plane normal <paramref name="h"/>. For a
        /// hyperbola the asymptote s is exactly perpendicular to h, so x and z are orthonormal and
        /// y = z cross x completes a right-handed basis. Returns the 3x3 matrix whose COLUMNS are [x y z],
        /// or null on a degenerate input. The slight non-orthogonality if s is not exactly perpendicular to
        /// h is absorbed by re-deriving x = y cross z after computing y, keeping the frame orthonormal.
        /// </summary>
        private static double[,] OrthonormalFrame(double[] s, double[] h)
        {
            double[] x = Normalize(s);
            double[] z = Normalize(h);
            if (x == null || z == null)
                return null;
            double[] y = Normalize(Cross(z, x));
            if (y == null)
                return null;
            // Re-derive x = y cross z so the basis is exactly orthonormal even if s was not perfectly
            // perpendicular to h (keeps det == +1 and columns mutually orthogonal).
            double[] xOrtho = Normalize(Cross(y, z));
            if (xOrtho == null)
                return null;
            return new[,]
            {
                { xOrtho[0], y[0], z[0] },
                { xOrtho[1], y[1], z[1] },
                { xOrtho[2], y[2], z[2] }
            };
        }

        /// <summary>
        /// Applies the 3x3 rotation <paramref name="r"/> to vector <paramref name="v"/> (the only thing
        /// applied to the sampled state vector). Pure. Returns v unchanged when r is null (no-rotation
        /// fallback).
        /// </summary>
        internal static double[] RotateVector(double[,] r, double[] v)
        {
            if (r == null)
                return new[] { v[0], v[1], v[2] };
            return new[]
            {
                r[0, 0] * v[0] + r[0, 1] * v[1] + r[0, 2] * v[2],
                r[1, 0] * v[0] + r[1, 1] * v[1] + r[1, 2] * v[2],
                r[2, 0] * v[0] + r[2, 1] * v[1] + r[2, 2] * v[2]
            };
        }

        /// <summary>
        /// The rotation angle of a proper 3x3 rotation matrix in radians, acos((trace(R) - 1) / 2),
        /// clamped to a valid acos domain. Diagnostic only (logged at the seam). Returns NaN for a null
        /// matrix.
        /// </summary>
        internal static double RotationAngleRadians(double[,] r)
        {
            if (r == null)
                return double.NaN;
            double trace = r[0, 0] + r[1, 1] + r[2, 2];
            double c = (trace - 1.0) / 2.0;
            if (c > 1.0) c = 1.0;
            if (c < -1.0) c = -1.0;
            return Math.Acos(c);
        }

        /// <summary>The 3x3 identity rotation (used by callers as the no-op R for tests / fallbacks).</summary>
        internal static double[,] Identity()
        {
            return new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        }
    }
}
