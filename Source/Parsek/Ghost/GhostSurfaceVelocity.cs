using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Pure derivation of a ghost's velocity relative to the body surface.
    ///
    /// <c>TrajectoryPoint.velocity</c> is the Unity world vector as recorded: surface-relative
    /// below the body's <c>inverseRotThresholdAltitude</c> (the world co-rotates there, which
    /// covers every landed or low vessel), inertial above it, and in world axes whose yaw about
    /// the spin axis depends on the recording session. No single conversion turns it into a
    /// ground speed: subtracting stock <c>getRFrmVel</c> invents ~175 m/s on a parked Kerbin
    /// ghost, and the direction still carries the session yaw.
    ///
    /// Recorded POSITIONS (latitude / longitude / altitude) are body-fixed, so two of them
    /// resolved through <c>GetWorldSurfacePosition</c> at the same moment differ only by the
    /// vessel's motion over the surface, already in today's world axes. The bracket the
    /// interpolation uses supplies both, so the derivation costs one subtraction and one
    /// divide per ghost per frame and allocates nothing.
    ///
    /// RELATIVE-frame sections store anchor-local metre offsets in those same fields; they
    /// are never body-fixed and must not be fed here. Use the section's
    /// <c>bodyFixedFrames</c> when present (<see cref="SelectRelativeSectionBodyFixedFrames"/>),
    /// otherwise the surface velocity is unknown.
    /// </summary>
    internal static class GhostSurfaceVelocity
    {
        /// <summary>A bracket shorter than this is a duplicate sample, not a time step.</summary>
        internal const double MinBracketSeconds = 1e-4;

        /// <summary>
        /// A bracket longer than this spans a warp, pause or sampling hole; the chord across it
        /// is an average over minutes, not the ghost's current ground speed.
        /// </summary>
        internal const double MaxBracketSeconds = 60.0;

        /// <summary>
        /// Surface velocity from two body-fixed positions resolved to world space in the same
        /// frame, and the UT gap between their samples. False (and zero) when the gap is not a
        /// usable time step or either input is non-finite.
        /// </summary>
        internal static bool TryFromBracketPositions(
            Vector3d worldPosBefore, Vector3d worldPosAfter, double dtSeconds,
            out Vector3 surfaceVelocity)
        {
            surfaceVelocity = Vector3.zero;
            if (double.IsNaN(dtSeconds) || double.IsInfinity(dtSeconds)
                || dtSeconds < MinBracketSeconds || dtSeconds > MaxBracketSeconds)
                return false;

            Vector3d delta = worldPosAfter - worldPosBefore;
            if (!IsFinite(delta.x) || !IsFinite(delta.y) || !IsFinite(delta.z))
                return false;

            surfaceVelocity = (Vector3)(delta / dtSeconds);
            return true;
        }

        /// <summary>
        /// Index of the sample that starts the bracket containing <paramref name="ut"/>, or -1
        /// when the list has fewer than two samples or <paramref name="ut"/> lies outside
        /// [first.ut, last.ut]. Binary search; no allocation and no diagnostics counters.
        /// </summary>
        internal static int FindBracketIndex(List<TrajectoryPoint> frames, double ut)
        {
            if (frames == null || frames.Count < 2 || double.IsNaN(ut))
                return -1;
            int last = frames.Count - 1;
            if (ut < frames[0].ut || ut > frames[last].ut)
                return -1;
            if (ut >= frames[last - 1].ut)
                return last - 1;

            int low = 0;
            int high = last - 1;
            while (low < high)
            {
                int mid = low + ((high - low + 1) >> 1);
                if (frames[mid].ut <= ut)
                    low = mid;
                else
                    high = mid - 1;
            }
            return low;
        }

        /// <summary>
        /// The body-fixed samples a RELATIVE section offers for a surface velocity: its
        /// <c>bodyFixedFrames</c> when there are at least two, otherwise null. Any other
        /// reference frame returns null too; its flat frames are the caller's own bracket.
        /// </summary>
        internal static List<TrajectoryPoint> SelectRelativeSectionBodyFixedFrames(TrackSection section)
        {
            if (section.referenceFrame != ReferenceFrame.Relative)
                return null;
            List<TrajectoryPoint> frames = section.bodyFixedFrames;
            return frames != null && frames.Count >= 2 ? frames : null;
        }

        /// <summary>
        /// Surface velocity of a ghost moving on an orbit: the orbit's inertial velocity
        /// (stock Zup, body-relative) lifted to world axes, minus the body's rotating-frame
        /// velocity at the ghost (stock <c>getRFrmVel</c>, world). This is the one place the
        /// rotating-frame term is legitimately subtracted: an <c>Orbit</c> velocity is truly
        /// inertial, unlike a recorded <c>TrajectoryPoint.velocity</c>.
        /// </summary>
        internal static Vector3 FromOrbitVelocity(Vector3d zupOrbitalVelocity, Vector3d rotatingFrameVelocityWorld)
        {
            return (Vector3)(TrajectoryMath.SwizzleZupBodyRelativeToWorld(zupOrbitalVelocity)
                - rotatingFrameVelocityWorld);
        }

        /// <summary>
        /// How far the mean of the two recorded speed magnitudes may sit from the bracket's
        /// position chord speed for the recorded magnitudes to count as surface speeds: a
        /// fraction of the chord speed, with a floor in m/s.
        /// </summary>
        internal const double RecordedSpeedAgreementFraction = 0.015;
        internal const double RecordedSpeedAgreementFloorMps = 2.0;

        /// <summary>
        /// <c>FlightIntegrator.VelSmoothLen</c>: stock's <c>vessel.speed</c> (and so its Mach) is
        /// the magnitude of the mean velocity of the last this-many physics frames
        /// (<c>FlightIntegrator.SmoothVelocity</c>), the value <c>AerodynamicsFX</c> reads.
        /// </summary>
        internal const int StockVelocitySmoothFrames = 10;

        /// <summary>
        /// Where in the bracket (0 = before sample, 1 = after) the speed stock would read at
        /// fraction <paramref name="t"/> sits: the mean of a speed that changes linearly over the
        /// last <see cref="StockVelocitySmoothFrames"/> physics frames is its value
        /// <c>(frames - 1) / 2</c> frames back. Can fall below 0 (the bracket's own trend read
        /// back, at most one bracket). An unusable input returns <paramref name="t"/>.
        /// </summary>
        internal static double StockSpeedFraction(double t, double bracketSeconds, double fixedDeltaTime)
        {
            if (!IsFinite(t) || !IsFinite(bracketSeconds) || !IsFinite(fixedDeltaTime)
                || bracketSeconds < MinBracketSeconds || fixedDeltaTime < 0.0)
                return t;
            double lagSeconds = 0.5 * (StockVelocitySmoothFrames - 1) * fixedDeltaTime;
            double f = t - lagSeconds / bracketSeconds;
            return f < -1.0 ? -1.0 : (f > 1.0 ? 1.0 : f);
        }

        /// <summary>
        /// The ghost's surface speed at bracket fraction <paramref name="t"/> from the bracket's
        /// recorded speed magnitudes, when they are surface speeds. The position chord is the
        /// mean speed over the whole bracket, the speed at its middle: under a reentry's
        /// deceleration (about 100 m/s per s) and the recorder's 0.5 s brackets it read 2 % slow
        /// near a bracket's start, and stock's <c>FxScalar</c> (speed to the 3.5th power, Mach
        /// through the thermal blend) 0.1 low (flight 3 of 2026-11-28, Mach 3.75).
        /// <para>
        /// A recorded <c>TrajectoryPoint.velocity</c> is the vessel's own velocity at the sample
        /// (<c>rb_velocityD</c> plus Krakensbane's frame velocity, the vector stock's
        /// <c>vessel.velocityD</c> is), surface-relative only while the world co-rotated with
        /// the body; its magnitude does not depend on the session's yaw. The pair is used only
        /// when the mean of its magnitudes agrees with the chord (a co-rotating pair agrees to
        /// well under 1 % over a reentry bracket; an inertial pair differs by the body's
        /// rotation along the track), and the result is the magnitudes interpolated linearly at
        /// <paramref name="t"/> (clamped to -1..1, so read back at most one bracket), never
        /// below 0. Otherwise false, and the chord stays the speed.
        /// </para>
        /// </summary>
        internal static bool TryRecordedSurfaceSpeed(double chordSpeed, double recordedSpeedBefore,
            double recordedSpeedAfter, double t, out float speed)
        {
            speed = 0f;
            if (!IsFinite(chordSpeed) || !IsFinite(recordedSpeedBefore) || !IsFinite(recordedSpeedAfter)
                || !IsFinite(t) || chordSpeed < 0.0 || recordedSpeedBefore < 0.0 || recordedSpeedAfter < 0.0)
                return false;
            double mean = 0.5 * (recordedSpeedBefore + recordedSpeedAfter);
            double tolerance = Math.Max(RecordedSpeedAgreementFloorMps, RecordedSpeedAgreementFraction * chordSpeed);
            if (Math.Abs(mean - chordSpeed) > tolerance)
                return false;
            if (t < -1.0) t = -1.0;
            else if (t > 1.0) t = 1.0;
            double value = recordedSpeedBefore + (recordedSpeedAfter - recordedSpeedBefore) * t;
            speed = (float)(value < 0.0 ? 0.0 : value);
            return true;
        }

        /// <summary>
        /// The speed the ghost's reentry scalar uses: the stock-read speed when one was derived
        /// for exactly this stored chord (<see cref="TryRecordedSurfaceSpeed"/> at
        /// <see cref="StockSpeedFraction"/>; NaN means none), else the chord's magnitude. A
        /// surface velocity written by any other path (a test, the parity driver) no longer
        /// equals the chord the stock speed came with, so it is read as is.
        /// </summary>
        internal static float SpeedForAeroFx(Vector3 surfaceVelocity, GhostSurfaceVelocitySource source,
            float stockSpeed, Vector3 stockSpeedChord, out bool usedStockSpeed)
        {
            usedStockSpeed = source == GhostSurfaceVelocitySource.Positions
                && !float.IsNaN(stockSpeed) && !float.IsInfinity(stockSpeed) && stockSpeed >= 0f
                && surfaceVelocity == stockSpeedChord;
            if (usedStockSpeed) return stockSpeed;
            return Usable(surfaceVelocity, source).magnitude;
        }

        /// <summary>Grep-stable log token for a source.</summary>
        internal static string SourceToken(GhostSurfaceVelocitySource source)
        {
            switch (source)
            {
                case GhostSurfaceVelocitySource.Positions: return "positions";
                case GhostSurfaceVelocitySource.SurfaceHold: return "surfaceHold";
                case GhostSurfaceVelocitySource.Orbit: return "orbit";
                default: return "none";
            }
        }

        /// <summary>
        /// The surface velocity a reader may use: the stored vector when its source is known,
        /// otherwise zero (no ground speed is ever invented).
        /// </summary>
        internal static Vector3 Usable(Vector3 surfaceVelocity, GhostSurfaceVelocitySource source)
        {
            return source == GhostSurfaceVelocitySource.None ? Vector3.zero : surfaceVelocity;
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
