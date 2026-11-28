using System;

namespace Parsek
{
    /// <summary>
    /// One Kepler conic piece of a far-watch path (design-far-watch.md 8.1, 8.4): plain elements
    /// over a UT span. Only the radius is read, which no element-frame rotation changes, so the
    /// elements may come from any frame (a stored <see cref="OrbitSegment"/> or a state vector).
    /// </summary>
    internal struct FarWatchConic
    {
        internal string BodyName;
        internal double SemiMajorAxis;   // negative for a hyperbola
        internal double Eccentricity;
        internal double GravParameter;
        internal double MeanAnomalyAtEpoch;   // radians; the hyperbolic mean anomaly when e > 1
        internal double Epoch;
        internal double StartUT;
        internal double EndUT;
    }

    /// <summary>
    /// Pure two-body radius math for the far-watch safety check and the warp limits: radius at
    /// a UT, periapsis, time to periapsis, and the first time a conic falls below a radius.
    /// Every function is total: a degenerate conic (NaN, parabolic band, non-positive mu)
    /// answers as "unknown", which callers read as danger.
    /// </summary>
    internal static class FarWatchConicMath
    {
        internal const double ParabolicBand = 1e-9;
        private const double TwoPi = 2.0 * Math.PI;

        internal static bool IsUsable(FarWatchConic c)
        {
            return IsFinite(c.SemiMajorAxis) && IsFinite(c.Eccentricity) && IsFinite(c.GravParameter)
                && IsFinite(c.MeanAnomalyAtEpoch) && IsFinite(c.Epoch)
                && c.GravParameter > 0.0 && c.Eccentricity >= 0.0
                && Math.Abs(c.Eccentricity - 1.0) > ParabolicBand
                && c.SemiMajorAxis != 0.0
                && (c.Eccentricity < 1.0 ? c.SemiMajorAxis > 0.0 : c.SemiMajorAxis < 0.0);
        }

        internal static double PeriapsisRadius(FarWatchConic c)
        {
            return c.SemiMajorAxis * (1.0 - c.Eccentricity);
        }

        internal static double MeanMotion(FarWatchConic c)
        {
            double a = Math.Abs(c.SemiMajorAxis);
            return Math.Sqrt(c.GravParameter / (a * a * a));
        }

        internal static double MeanAnomalyAt(FarWatchConic c, double ut)
        {
            return c.MeanAnomalyAtEpoch + MeanMotion(c) * (ut - c.Epoch);
        }

        /// <summary>Radius at <paramref name="ut"/>, NaN for an unusable conic.</summary>
        internal static double RadiusAt(FarWatchConic c, double ut)
        {
            if (!IsUsable(c))
                return double.NaN;
            double m = MeanAnomalyAt(c, ut);
            if (c.Eccentricity < 1.0)
            {
                double e = BallisticExtrapolator.TwoBodyOrbit.SolveEllipticKepler(NormalizeAngle(m), c.Eccentricity);
                return c.SemiMajorAxis * (1.0 - c.Eccentricity * Math.Cos(e));
            }
            double h = SolveHyperbolicKepler(m, c.Eccentricity);
            return c.SemiMajorAxis * (1.0 - c.Eccentricity * Math.Cosh(h));
        }

        /// <summary>
        /// Stock's <c>Orbit.timeToPe</c> convention: in [0, period) on an ellipse, negative on a
        /// hyperbola after its periapsis. NaN for an unusable conic.
        /// </summary>
        internal static double TimeToPeriapsis(FarWatchConic c, double ut)
        {
            if (!IsUsable(c))
                return double.NaN;
            double n = MeanMotion(c);
            double m = MeanAnomalyAt(c, ut);
            if (c.Eccentricity < 1.0)
            {
                double mn = NormalizeAngle(m);
                return mn <= 0.0 ? 0.0 : (TwoPi - mn) / n;
            }
            return -m / n;
        }

        /// <summary>
        /// The first UT in [<paramref name="fromUT"/>, <paramref name="toUT"/>] at which the conic's
        /// radius is below <paramref name="radius"/>, solved analytically at the descending
        /// crossing. False when it stays at or above it. An unusable conic answers
        /// <paramref name="fromUT"/> (unknown counts as danger).
        /// </summary>
        internal static bool TryFirstTimeBelowRadius(FarWatchConic c, double radius, double fromUT, double toUT, out double ut)
        {
            ut = double.NaN;
            if (double.IsNaN(fromUT) || double.IsNaN(toUT) || toUT < fromUT)
                return false;
            if (!IsUsable(c))
            {
                ut = fromUT;
                return true;
            }
            if (PeriapsisRadius(c) >= radius)
                return false;
            double r0 = RadiusAt(c, fromUT);
            if (!(r0 >= radius))
            {
                ut = fromUT;
                return true;
            }
            double n = MeanMotion(c);
            double m0 = MeanAnomalyAt(c, fromUT);
            double e = c.Eccentricity;
            double crossing;
            if (e < 1.0)
            {
                double cosE = Clamp((1.0 - radius / c.SemiMajorAxis) / e, -1.0, 1.0);
                double ec = Math.Acos(cosE);
                double mAscending = ec - e * Math.Sin(ec);
                double mDescending = TwoPi - mAscending;
                double dm = mDescending - NormalizeAngle(m0);
                if (dm < 0.0) dm += TwoPi;
                crossing = fromUT + dm / n;
            }
            else
            {
                double coshH = (radius / -c.SemiMajorAxis + 1.0) / e;
                double hc = Acosh(Math.Max(1.0, coshH));
                double mDescending = -(e * Math.Sinh(hc) - hc);
                if (m0 >= mDescending)
                    return false;
                crossing = fromUT + (mDescending - m0) / n;
            }
            if (crossing > toUT)
                return false;
            ut = crossing;
            return true;
        }

        /// <summary>
        /// A conic from a body-relative state vector (any inertial frame; only the radius is used
        /// later). False for a degenerate state (zero radius, parabolic band, NaN).
        /// </summary>
        internal static bool TryFromStateVector(Vector3d r, Vector3d v, double mu, double ut, string bodyName, out FarWatchConic conic)
        {
            conic = default(FarWatchConic);
            double rm = r.magnitude;
            if (!IsFinite(rm) || rm <= 0.0 || !IsFinite(v.magnitude) || !(mu > 0.0))
                return false;
            double v2 = v.sqrMagnitude;
            double energy = v2 / 2.0 - mu / rm;
            if (energy == 0.0)
                return false;
            double a = -mu / (2.0 * energy);
            Vector3d ev = ((v2 - mu / rm) * r - Vector3d.Dot(r, v) * v) / mu;
            double e = ev.magnitude;
            if (!IsFinite(e) || Math.Abs(e - 1.0) <= ParabolicBand)
                return false;
            double rv = Vector3d.Dot(r, v);
            double m;
            if (e < 1e-12)
            {
                m = 0.0;
            }
            else if (e < 1.0)
            {
                double cosE = Clamp((1.0 - rm / a) / e, -1.0, 1.0);
                double sinE = rv / (e * Math.Sqrt(mu * a));
                double ea = Math.Atan2(sinE, cosE);
                m = ea - e * Math.Sin(ea);
            }
            else
            {
                double sinhH = rv / (e * Math.Sqrt(-mu * a));
                double h = Asinh(sinhH);
                m = e * Math.Sinh(h) - h;
            }
            conic = new FarWatchConic
            {
                BodyName = bodyName,
                SemiMajorAxis = a,
                Eccentricity = e,
                GravParameter = mu,
                MeanAnomalyAtEpoch = m,
                Epoch = ut,
                StartUT = ut,
                EndUT = double.PositiveInfinity,
            };
            return IsUsable(conic);
        }

        internal static FarWatchConic FromSegment(OrbitSegment s, double mu)
        {
            return new FarWatchConic
            {
                BodyName = s.bodyName,
                SemiMajorAxis = s.semiMajorAxis,
                Eccentricity = s.eccentricity,
                GravParameter = mu,
                MeanAnomalyAtEpoch = s.meanAnomalyAtEpoch,
                Epoch = s.epoch,
                StartUT = s.startUT,
                EndUT = s.endUT,
            };
        }

        internal static double SolveHyperbolicKepler(double m, double e)
        {
            double h = Asinh(m / e);
            for (int i = 0; i < 60; i++)
            {
                double f = e * Math.Sinh(h) - h - m;
                double fp = e * Math.Cosh(h) - 1.0;
                if (fp == 0.0) break;
                double step = f / fp;
                h -= step;
                if (Math.Abs(step) < 1e-12) break;
            }
            return h;
        }

        internal static double NormalizeAngle(double a)
        {
            double r = a % TwoPi;
            if (r < 0.0) r += TwoPi;
            return r;
        }

        private static double Clamp(double x, double lo, double hi)
        {
            return x < lo ? lo : (x > hi ? hi : x);
        }

        private static double Acosh(double x)
        {
            return Math.Log(x + Math.Sqrt(x * x - 1.0));
        }

        private static double Asinh(double x)
        {
            double ax = Math.Abs(x);
            double r = Math.Log(ax + Math.Sqrt(ax * ax + 1.0));
            return x < 0.0 ? -r : r;
        }

        private static bool IsFinite(double x)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x);
        }
    }
}
