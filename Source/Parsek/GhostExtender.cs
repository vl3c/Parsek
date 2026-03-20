using System;

namespace Parsek
{
    internal enum GhostExtensionStrategy
    {
        Orbital,
        Surface,
        LastRecordedPosition,
        None
    }

    /// <summary>
    /// Pure static methods for extending ghost position past the end of a recording.
    /// Used when a spawn is blocked (collision) and the ghost must continue moving,
    /// or for unloaded ghosts that need orbital propagation.
    /// </summary>
    internal static class GhostExtender
    {
        private const string Tag = "GhostExtend";

        /// <summary>
        /// Pure decision: which propagation strategy to use based on recording terminal state.
        /// </summary>
        internal static GhostExtensionStrategy ChooseStrategy(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Verbose(Tag, "ChooseStrategy: null recording, returning None");
                return GhostExtensionStrategy.None;
            }

            // 1. Orbital: terminal orbit body and SMA present
            if (!string.IsNullOrEmpty(rec.TerminalOrbitBody) &&
                rec.TerminalOrbitSemiMajorAxis > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ChooseStrategy: Orbital (body={rec.TerminalOrbitBody} " +
                    $"sma={rec.TerminalOrbitSemiMajorAxis:F0} " +
                    $"ecc={rec.TerminalOrbitEccentricity:F4})");
                return GhostExtensionStrategy.Orbital;
            }

            // 2. Surface: terminal position available
            if (rec.TerminalPosition.HasValue)
            {
                var tp = rec.TerminalPosition.Value;
                ParsekLog.Verbose(Tag,
                    $"ChooseStrategy: Surface (body={tp.body} " +
                    $"lat={tp.latitude:F4} lon={tp.longitude:F4} alt={tp.altitude:F1})");
                return GhostExtensionStrategy.Surface;
            }

            // 3. LastRecordedPosition: has trajectory points
            if (rec.Points != null && rec.Points.Count > 0)
            {
                var last = rec.Points[rec.Points.Count - 1];
                ParsekLog.Verbose(Tag,
                    $"ChooseStrategy: LastRecordedPosition (last point UT={last.ut:F1} " +
                    $"lat={last.latitude:F4} lon={last.longitude:F4} alt={last.altitude:F1})");
                return GhostExtensionStrategy.LastRecordedPosition;
            }

            // 4. None
            ParsekLog.Verbose(Tag,
                $"ChooseStrategy: None (no terminal orbit, no surface position, " +
                $"no trajectory points for rec={rec.RecordingId})");
            return GhostExtensionStrategy.None;
        }

        /// <summary>
        /// Pure Keplerian propagation: compute position at currentUT from orbital elements.
        /// Returns (lat, lon, alt) on the body surface.
        /// Requires bodyRadius and gravParameter as inputs (avoids CelestialBody dependency).
        /// </summary>
        internal static (double lat, double lon, double alt)
            PropagateOrbital(
                double inc, double ecc, double sma, double lan,
                double argPe, double meanAnomalyAtEpoch, double epoch,
                double bodyRadius, double bodyGravParam,
                double currentUT)
        {
            // Step 1: Mean motion n = sqrt(GM / a^3)
            double sma3 = sma * sma * sma;
            double n = Math.Sqrt(bodyGravParam / sma3);

            // Step 2: Mean anomaly at currentUT
            double dt = currentUT - epoch;
            double M = meanAnomalyAtEpoch + n * dt;

            // Step 3: Normalize M to [0, 2*pi]
            M = NormalizeAngle(M);

            // Step 4: Solve Kepler's equation M = E - ecc*sin(E)
            double E;
            if (ecc < 1e-6)
            {
                // Near-circular: E ~ M
                E = M;
            }
            else
            {
                E = SolveKepler(M, ecc);
            }

            // Step 5: True anomaly
            double sinHalfNu = Math.Sqrt(1.0 + ecc) * Math.Sin(E * 0.5);
            double cosHalfNu = Math.Sqrt(1.0 - ecc) * Math.Cos(E * 0.5);
            double nu = 2.0 * Math.Atan2(sinHalfNu, cosHalfNu);

            // Step 6: Radius
            double r = sma * (1.0 - ecc * Math.Cos(E));

            // Step 7: Position in perifocal frame (PQW)
            double cosNu = Math.Cos(nu);
            double sinNu = Math.Sin(nu);
            double xPeri = r * cosNu;
            double yPeri = r * sinNu;
            // zPeri = 0 (orbit lies in perifocal plane)

            // Step 8: Rotate from perifocal to body-fixed (inertial) frame
            // Rotation by argument of periapsis, inclination, and longitude of ascending node
            double cosArgPe = Math.Cos(argPe);
            double sinArgPe = Math.Sin(argPe);
            double cosInc = Math.Cos(inc);
            double sinInc = Math.Sin(inc);
            double cosLan = Math.Cos(lan);
            double sinLan = Math.Sin(lan);

            // Rotation matrix elements (perifocal -> ECI)
            // Column 1 (P direction):
            double r11 = cosLan * cosArgPe - sinLan * sinArgPe * cosInc;
            double r21 = sinLan * cosArgPe + cosLan * sinArgPe * cosInc;
            double r31 = sinArgPe * sinInc;

            // Column 2 (Q direction):
            double r12 = -cosLan * sinArgPe - sinLan * cosArgPe * cosInc;
            double r22 = -sinLan * sinArgPe + cosLan * cosArgPe * cosInc;
            double r32 = cosArgPe * sinInc;

            // Position in body-centered inertial frame
            double x = r11 * xPeri + r12 * yPeri;
            double y = r21 * xPeri + r22 * yPeri;
            double z = r31 * xPeri + r32 * yPeri;

            // Step 9: Convert to lat/lon/alt
            double dist = Math.Sqrt(x * x + y * y + z * z);
            double alt = dist - bodyRadius;
            double lat = Math.Asin(Clamp(z / dist, -1.0, 1.0)) * (180.0 / Math.PI);
            double lon = Math.Atan2(y, x) * (180.0 / Math.PI);

            ParsekLog.VerboseRateLimited(Tag, "propagate-orbital",
                $"PropagateOrbital: UT={currentUT:F1} dt={dt:F1} " +
                $"M={M:F4} E={E:F4} nu={nu:F4} r={r:F0} " +
                $"lat={lat:F4} lon={lon:F4} alt={alt:F0}");

            return (lat, lon, alt);
        }

        /// <summary>
        /// Surface hold: returns the terminal surface position unchanged.
        /// Falls back to LastRecordedPosition if terminal position is null.
        /// </summary>
        internal static (double lat, double lon, double alt)
            PropagateSurface(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Warn(Tag, "PropagateSurface: null recording, returning (0,0,0)");
                return (0, 0, 0);
            }

            if (rec.TerminalPosition.HasValue)
            {
                var tp = rec.TerminalPosition.Value;
                ParsekLog.Verbose(Tag,
                    $"PropagateSurface: returning terminal position " +
                    $"lat={tp.latitude:F4} lon={tp.longitude:F4} alt={tp.altitude:F1}");
                return (tp.latitude, tp.longitude, tp.altitude);
            }

            // Fallback to last recorded position
            ParsekLog.Verbose(Tag,
                "PropagateSurface: no terminal position, falling back to LastRecordedPosition");
            return LastRecordedPosition(rec);
        }

        /// <summary>
        /// Last recorded position fallback: returns the last TrajectoryPoint's lat/lon/alt.
        /// </summary>
        internal static (double lat, double lon, double alt)
            LastRecordedPosition(Recording rec)
        {
            if (rec == null || rec.Points == null || rec.Points.Count == 0)
            {
                ParsekLog.Warn(Tag,
                    "LastRecordedPosition: no points available, returning (0,0,0)");
                return (0, 0, 0);
            }

            var last = rec.Points[rec.Points.Count - 1];
            ParsekLog.Verbose(Tag,
                $"LastRecordedPosition: UT={last.ut:F1} " +
                $"lat={last.latitude:F4} lon={last.longitude:F4} alt={last.altitude:F1}");
            return (last.latitude, last.longitude, last.altitude);
        }

        // --- Private helpers ---

        /// <summary>
        /// Solve Kepler's equation M = E - ecc*sin(E) using Newton-Raphson.
        /// Max 10 iterations. Initial guess E0 = M.
        /// </summary>
        private static double SolveKepler(double M, double ecc)
        {
            double E = M; // initial guess
            for (int i = 0; i < 10; i++)
            {
                double dE = (E - ecc * Math.Sin(E) - M) / (1.0 - ecc * Math.Cos(E));
                E -= dE;
                if (Math.Abs(dE) < 1e-12)
                    break;
            }
            return E;
        }

        /// <summary>
        /// Normalize angle to [0, 2*pi].
        /// </summary>
        private static double NormalizeAngle(double angle)
        {
            const double twoPi = 2.0 * Math.PI;
            angle %= twoPi;
            if (angle < 0) angle += twoPi;
            return angle;
        }

        /// <summary>
        /// Clamp value to [min, max].
        /// </summary>
        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
