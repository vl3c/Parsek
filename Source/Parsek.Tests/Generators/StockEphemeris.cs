using System;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// A three-component double vector for fixture geometry, independent of Unity.
    /// </summary>
    internal struct FixtureVec3
    {
        public readonly double X, Y, Z;

        public FixtureVec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static FixtureVec3 operator +(FixtureVec3 a, FixtureVec3 b)
            => new FixtureVec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static FixtureVec3 operator -(FixtureVec3 a, FixtureVec3 b)
            => new FixtureVec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static FixtureVec3 operator *(FixtureVec3 a, double s)
            => new FixtureVec3(a.X * s, a.Y * s, a.Z * s);

        public double Dot(FixtureVec3 b) => X * b.X + Y * b.Y + Z * b.Z;

        public double Magnitude => Math.Sqrt(Dot(this));

        /// <summary>
        /// Closest distance from <paramref name="center"/> to the segment a-b: what stock's
        /// horizon occluder test reduces to for a spherical body.
        /// </summary>
        public static double SegmentDistanceTo(FixtureVec3 a, FixtureVec3 b, FixtureVec3 center)
        {
            FixtureVec3 ab = b - a;
            double len2 = ab.Dot(ab);
            double s = len2 > 0.0 ? -(a - center).Dot(ab) / len2 : 0.0;
            if (s < 0.0) s = 0.0;
            if (s > 1.0) s = 1.0;
            return (a + ab * s - center).Magnitude;
        }
    }

    /// <summary>KSP-native Keplerian elements (degrees for the angles, mean anomaly in radians).</summary>
    internal struct FixtureOrbitElements
    {
        public double Sma, Ecc, IncDeg, LanDeg, ArgPeDeg, MnaRad, Epoch;

        public FixtureOrbitElements WithMeanAnomalyOffset(double deltaRad)
        {
            FixtureOrbitElements e = this;
            e.MnaRad += deltaRad;
            return e;
        }
    }

    /// <summary>
    /// Two-body positions of the stock KSP 1.12.5 bodies a fixture lane needs, in the one
    /// inertial element frame every KSP orbit shares (LAN measured from the same reference
    /// direction at every level of the hierarchy). Constants are the stock system's; they
    /// are pinned against recorded SOI crossings in <c>GhostCommNetLaneGeometryTests</c>
    /// (the parent-frame and child-frame positions of one vessel at one UT agree to a few km
    /// at the SOI radius), so a wrong constant reds there rather than in a flight.
    /// </summary>
    internal static class StockEphemeris
    {
        internal const double SunGravParameter = 1.1723328e18;
        internal const double KerbinGravParameter = 3.5316e12;
        internal const double DunaGravParameter = 3.0136321e11;
        internal const double IkeGravParameter = 1.8568369e10;
        internal const double KerbinRadius = 600000.0;
        internal const double DunaRadius = 320000.0;
        internal const double IkeRadius = 130000.0;

        internal static readonly FixtureOrbitElements Kerbin = new FixtureOrbitElements
        {
            Sma = 13599840256.0, Ecc = 0.0, IncDeg = 0.0, LanDeg = 0.0, ArgPeDeg = 0.0, MnaRad = 3.14, Epoch = 0.0,
        };

        internal static readonly FixtureOrbitElements Duna = new FixtureOrbitElements
        {
            Sma = 20726155264.0, Ecc = 0.051, IncDeg = 0.06, LanDeg = 135.5, ArgPeDeg = 0.0, MnaRad = 3.14, Epoch = 0.0,
        };

        /// <summary>Ike around Duna.</summary>
        internal static readonly FixtureOrbitElements Ike = new FixtureOrbitElements
        {
            Sma = 3200000.0, Ecc = 0.03, IncDeg = 0.2, LanDeg = 0.0, ArgPeDeg = 0.0, MnaRad = 1.7, Epoch = 0.0,
        };

        /// <summary>
        /// Parent-relative position of an orbit at <paramref name="ut"/>: the mean anomaly
        /// propagated from its epoch, Kepler's equation solved (elliptic or hyperbolic), then
        /// rotated by argPe, inclination and LAN.
        /// </summary>
        internal static FixtureVec3 Position(FixtureOrbitElements el, double mu, double ut)
        {
            double a = el.Sma, e = el.Ecc;
            double n = Math.Sqrt(mu / Math.Abs(a * a * a));
            double m = el.MnaRad + n * (ut - el.Epoch);
            double nu, r;
            if (e < 1.0)
            {
                m %= 2.0 * Math.PI;
                double ecc = e < 0.8 ? m : Math.PI;
                for (int i = 0; i < 100; i++)
                    ecc -= (ecc - e * Math.Sin(ecc) - m) / (1.0 - e * Math.Cos(ecc));
                nu = 2.0 * Math.Atan2(Math.Sqrt(1.0 + e) * Math.Sin(ecc / 2.0),
                    Math.Sqrt(1.0 - e) * Math.Cos(ecc / 2.0));
                r = a * (1.0 - e * Math.Cos(ecc));
            }
            else
            {
                double h = Asinh(m / e);
                for (int i = 0; i < 200; i++)
                    h -= (e * Math.Sinh(h) - h - m) / (e * Math.Cosh(h) - 1.0);
                nu = 2.0 * Math.Atan2(Math.Sqrt(e + 1.0) * Math.Sinh(h / 2.0),
                    Math.Sqrt(e - 1.0) * Math.Cosh(h / 2.0));
                r = a * (1.0 - e * Math.Cosh(h));
            }
            double inc = el.IncDeg * Math.PI / 180.0;
            double lan = el.LanDeg * Math.PI / 180.0;
            double u = el.ArgPeDeg * Math.PI / 180.0 + nu;
            return new FixtureVec3(
                r * (Math.Cos(lan) * Math.Cos(u) - Math.Sin(lan) * Math.Sin(u) * Math.Cos(inc)),
                r * (Math.Sin(lan) * Math.Cos(u) + Math.Cos(lan) * Math.Sin(u) * Math.Cos(inc)),
                r * Math.Sin(u) * Math.Sin(inc));
        }

        private static double Asinh(double x) => Math.Log(x + Math.Sqrt(x * x + 1.0));

        internal static FixtureVec3 KerbinHeliocentric(double ut) => Position(Kerbin, SunGravParameter, ut);

        internal static FixtureVec3 DunaHeliocentric(double ut) => Position(Duna, SunGravParameter, ut);

        internal static FixtureVec3 IkeHeliocentric(double ut)
            => DunaHeliocentric(ut) + Position(Ike, DunaGravParameter, ut);

        /// <summary>Element-frame longitude (degrees, atan2 of the in-frame x/y) of a vector.</summary>
        internal static double ElementAngleDeg(FixtureVec3 v) => Math.Atan2(v.Y, v.X) * 180.0 / Math.PI;

        /// <summary>Latitude (degrees) of a body-relative vector for a body whose axis is the frame z.</summary>
        internal static double LatitudeDeg(FixtureVec3 v) => Math.Asin(v.Z / v.Magnitude) * 180.0 / Math.PI;

        internal static double Wrap180(double deg)
        {
            double w = deg % 360.0;
            if (w < 0) w += 360.0;
            return w > 180.0 ? w - 360.0 : w;
        }
    }
}
