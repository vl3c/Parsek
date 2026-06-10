using System;
using System.Globalization;

namespace Parsek.Reaim
{
    // Transfer-window math for re-aim interplanetary looping (docs/dev/plans/
    // reaim-interplanetary-transfers.md, Phase 1). PURE: no Unity, no shared state - every method
    // takes plain doubles so the whole thing is unit-testable against textbook solutions. The live
    // side (reading a body's orbital elements / current phase) is a thin wrapper tested in-game.
    //
    // The formulas are the standard circular-coplanar Hohmann / synodic approximation, adapted from
    // KerbalAlarmClock (MIT, https://github.com/TriggerAu/KerbalAlarmClock):
    //   - Hohmann phase-angle target: KACXFerTarget.CalcPhaseAngleTarget (TimeObjects.cs).
    //   - Synodic alignment time:     KACXFerTarget.AlignmentTime (TimeObjects.cs).
    //   - Angle clamps:               KACUtils.clampDegrees* (Utilities.cs).
    // This is the "visually plausible ghost" window: one closed-form departure phase per window, fed
    // to a single Lambert solve. A porkchop dV-grid refinement is a deferred option (plan section 12).

    internal static class TransferWindowMath
    {
        internal const double TwoPi = 2.0 * Math.PI;
        internal const double Rad2Deg = 180.0 / Math.PI;
        internal const double Deg2Rad = Math.PI / 180.0;

        /// <summary>Wraps an angle into [0, 360). NaN/Infinity pass through unchanged.</summary>
        internal static double ClampDegrees360(double angle)
        {
            if (double.IsNaN(angle) || double.IsInfinity(angle))
                return angle;
            double m = angle % 360.0;
            if (m < 0.0)
                m += 360.0;
            return m;
        }

        /// <summary>Wraps an angle into (-180, 180]. NaN/Infinity pass through unchanged.</summary>
        internal static double ClampDegrees180(double angle)
        {
            double m = ClampDegrees360(angle);
            if (m > 180.0)
                m -= 360.0;
            return m;
        }

        /// <summary>
        /// Longitude of periapsis (degrees, wrapped to [0,360)): LAN + argumentOfPeriapsis. The
        /// ROBUST orientation metric for near-equatorial orbits, where LAN alone is degenerate:
        /// at inclination ~0 the ascending node is undefined and KSP's element extraction
        /// (Orbit.UpdateFromFixedVectors, KSP 1.12.5) substitutes the +X axis for the degenerate
        /// node, pinning LAN to exactly 0 while argumentOfPeriapsis becomes the in-plane angle
        /// from +X to the eccentricity vector - i.e. AoP IS the periapsis longitude there. With a
        /// tiny noise inclination LAN and AoP are measured from the SAME (noise-chosen) node, so
        /// their SUM remains the well-defined periapsis longitude in both regimes. NaN/Infinity
        /// inputs propagate. Pure.
        /// </summary>
        internal static double LongitudeOfPeriapsisDegrees(double lanDegrees, double aopDegrees)
        {
            return ClampDegrees360(lanDegrees + aopDegrees);
        }

        /// <summary>
        /// Synodic period (seconds) of two bodies orbiting the same parent: the time between identical
        /// relative geometries, <c>1 / |1/P_target - 1/P_origin|</c>. Returns +Infinity for equal or
        /// degenerate periods (no relative drift -> never realigns), so callers can guard it. Pure.
        /// </summary>
        internal static double SynodicPeriodSeconds(double originPeriodSeconds, double targetPeriodSeconds)
        {
            if (double.IsNaN(originPeriodSeconds) || double.IsNaN(targetPeriodSeconds)
                || originPeriodSeconds <= 0.0 || targetPeriodSeconds <= 0.0)
                return double.PositiveInfinity;
            double diff = Math.Abs(1.0 / targetPeriodSeconds - 1.0 / originPeriodSeconds);
            if (diff <= 0.0 || double.IsNaN(diff))
                return double.PositiveInfinity;
            return 1.0 / diff;
        }

        /// <summary>
        /// The ideal Hohmann departure phase angle (degrees, in [0,360)): how far the target must LEAD
        /// the origin at burn time so a Hohmann transfer arrives where the target will be.
        /// <c>180 * (1 - ((a_origin + a_target) / (2*a_target))^1.5)</c>. For an outbound transfer
        /// (a_target &gt; a_origin) this is a positive lead; for inbound it is negative (target trails),
        /// wrapped into [0,360). Pure. NaN/non-positive SMAs -&gt; NaN.
        /// </summary>
        internal static double HohmannPhaseAngleTargetDegrees(double aOriginMeters, double aTargetMeters)
        {
            if (double.IsNaN(aOriginMeters) || double.IsNaN(aTargetMeters)
                || aOriginMeters <= 0.0 || aTargetMeters <= 0.0)
                return double.NaN;
            double ratio = (aOriginMeters + aTargetMeters) / (2.0 * aTargetMeters);
            return ClampDegrees360(180.0 * (1.0 - Math.Pow(ratio, 1.5)));
        }

        /// <summary>
        /// Hohmann transfer time (seconds): half the period of the transfer ellipse whose semi-major
        /// axis is <c>(a_origin + a_target) / 2</c>, i.e. <c>pi * sqrt(a_t^3 / mu)</c> where mu is the
        /// COMMON-PARENT (Sun) gravitational parameter. Pure. NaN/non-positive inputs -&gt; NaN.
        /// </summary>
        internal static double HohmannTransferTimeSeconds(
            double aOriginMeters, double aTargetMeters, double muParent)
        {
            if (double.IsNaN(aOriginMeters) || double.IsNaN(aTargetMeters) || double.IsNaN(muParent)
                || aOriginMeters <= 0.0 || aTargetMeters <= 0.0 || muParent <= 0.0)
                return double.NaN;
            double aTransfer = 0.5 * (aOriginMeters + aTargetMeters);
            return Math.PI * Math.Sqrt(aTransfer * aTransfer * aTransfer / muParent);
        }

        /// <summary>
        /// Seconds until the next departure window: the time for the relative phase to drift from
        /// <paramref name="currentPhaseDegrees"/> to <paramref name="targetPhaseDegrees"/>, given each
        /// body's orbital period. The relative angular rate is <c>360/P_target - 360/P_origin</c>
        /// (deg/s); the angle to make up is wrapped to the correct sign for that rate so the result is
        /// always the NEXT future alignment (>= 0). Returns +Infinity when the bodies never realign
        /// (equal/degenerate periods). Pure. (KAC AlignmentTime.)
        /// </summary>
        internal static double TimeToNextWindowSeconds(
            double currentPhaseDegrees, double targetPhaseDegrees,
            double originPeriodSeconds, double targetPeriodSeconds)
        {
            if (double.IsNaN(currentPhaseDegrees) || double.IsNaN(targetPhaseDegrees)
                || originPeriodSeconds <= 0.0 || targetPeriodSeconds <= 0.0
                || double.IsNaN(originPeriodSeconds) || double.IsNaN(targetPeriodSeconds))
                return double.PositiveInfinity;

            double anglePerSec = 360.0 / targetPeriodSeconds - 360.0 / originPeriodSeconds;
            if (anglePerSec == 0.0 || double.IsNaN(anglePerSec))
                return double.PositiveInfinity; // no relative drift -> never realigns

            double angleToMakeUp = ClampDegrees360(currentPhaseDegrees) - ClampDegrees360(targetPhaseDegrees);
            // Wrap so the angle and the drift rate have consistent sign -> always a future window.
            if (angleToMakeUp > 0.0 && anglePerSec > 0.0)
                angleToMakeUp -= 360.0;
            if (angleToMakeUp < 0.0 && anglePerSec < 0.0)
                angleToMakeUp += 360.0;

            return Math.Abs(angleToMakeUp / anglePerSec);
        }

        /// <summary>
        /// The next departure UT at or after <paramref name="afterUT"/>: the live phase + the time to
        /// the next window. Convenience composition of <see cref="TimeToNextWindowSeconds"/> for the
        /// scheduler. Returns NaN when no window exists (degenerate periods). Pure.
        /// </summary>
        internal static double NextDepartureUT(
            double afterUT, double currentPhaseDegrees, double targetPhaseDegrees,
            double originPeriodSeconds, double targetPeriodSeconds)
        {
            double dt = TimeToNextWindowSeconds(
                currentPhaseDegrees, targetPhaseDegrees, originPeriodSeconds, targetPeriodSeconds);
            if (double.IsNaN(afterUT) || double.IsInfinity(dt) || double.IsNaN(dt))
                return double.NaN;
            return afterUT + dt;
        }

        internal static string Describe(double aOrigin, double aTarget, double pOrigin, double pTarget, double muParent)
        {
            var ic = CultureInfo.InvariantCulture;
            double syn = SynodicPeriodSeconds(pOrigin, pTarget);
            double phase = HohmannPhaseAngleTargetDegrees(aOrigin, aTarget);
            double tof = HohmannTransferTimeSeconds(aOrigin, aTarget, muParent);
            return $"synodic={syn.ToString("R", ic)}s phaseTarget={phase.ToString("R", ic)}deg " +
                   $"hohmannTof={tof.ToString("R", ic)}s";
        }
    }
}
