using System;
using System.Collections.Generic;
using Parsek.Rendering;
using UnityEngine;

namespace Parsek
{
    public static partial class TrajectoryMath
    {
        /// <summary>
        /// Phase 4 frame transformation (design doc §6.2 Stage 2 frame table,
        /// §18 Phase 4, §26.1 HR-9). Lifts body-fixed (lat, lon, alt) at the
        /// recording UT into "inertial-longitude" coordinates by adding the
        /// body's sidereal rotation phase, and lowers the inverse at the
        /// playback UT by subtracting the playback-time phase before handing
        /// to <c>body.GetWorldSurfacePosition</c>.
        ///
        /// <para>
        /// Formulation B: longitude unwrap by body rotation phase. Reuses
        /// the existing <c>IncompleteBallisticSceneExitFinalizer</c> formula
        /// (<c>longitude offset = (ut - referenceUT) * 360 / rotationPeriod</c>).
        /// <c>body.initialRotation</c> is intentionally omitted — both Lift
        /// and Lower add/subtract the same offset, so it cancels.
        /// </para>
        ///
        /// <para>
        /// Pure functions (HR-3): same inputs → same outputs, no hidden state.
        /// Null body / non-finite or zero <c>rotationPeriod</c> degrade
        /// gracefully (HR-9): Lift returns body-fixed unchanged and emits a
        /// <c>Pipeline-Frame</c> Warn so the failure is visible in KSP.log.
        /// EXO sections on tidally-locked / anomalous bodies render as
        /// body-fixed in that case.
        /// </para>
        /// </summary>
        internal static class FrameTransform
        {
            /// <summary>Test seam: when set, returned in place of
            /// <c>body.rotationPeriod</c>. xUnit can't realistically construct
            /// fully-initialised <see cref="CelestialBody"/> instances, so
            /// tests inject a synthetic period via this hook. Production
            /// callers leave it null and read the live field. Reset in test
            /// Dispose via <see cref="ResetForTesting"/>.</summary>
            internal static System.Func<CelestialBody, double> RotationPeriodForTesting;

            /// <summary>Test seam: when set, returned in place of
            /// <c>body.GetWorldSurfacePosition(lat, lon, alt)</c>. xUnit can't
            /// drive the live PQS lookup, so tests inject a deterministic
            /// surface-to-world mapping via this hook. Reset in Dispose via
            /// <see cref="ResetForTesting"/>.</summary>
            internal static System.Func<CelestialBody, double, double, double, Vector3d> WorldSurfacePositionForTesting;

            /// <summary>Test-only: clears any injected seams.</summary>
            internal static void ResetForTesting()
            {
                RotationPeriodForTesting = null;
                WorldSurfacePositionForTesting = null;
            }

            /// <summary>
            /// Sidereal phase advance in degrees from <c>UT == 0</c>:
            /// <c>(ut * 360 / rotationPeriod)</c>. Returns <c>0</c> when the
            /// body is null or its rotation period is non-finite / zero —
            /// callers treat that as "no inertial lift needed" (HR-9).
            /// </summary>
            internal static double RotationAngleAtUT(CelestialBody body, double ut)
            {
                if (object.ReferenceEquals(body, null))
                    return 0.0;
                double period = ResolveRotationPeriod(body);
                if (double.IsNaN(period) || double.IsInfinity(period) || System.Math.Abs(period) <= double.Epsilon)
                    return 0.0;
                if (double.IsNaN(ut) || double.IsInfinity(ut))
                    return 0.0;
                return (ut * 360.0) / period;
            }

            /// <summary>
            /// M4b phasing-knob body-fixed derotation: a recorded body-fixed point replayed
            /// <paramref name="shiftSeconds"/> LATER than its rotation-aligned time renders rotated
            /// eastward with the planet by <c>shift * 360 / T_rot</c>; resolving at
            /// <c>lon - thatAngle</c> restores the recorded INERTIAL position (correct physics for
            /// the vacuum burn/coast arcs the knob shifts; surface-coupled legs are pre-shift, so
            /// their shift is 0 and this is the identity). Same sign convention as
            /// <see cref="LiftToInertial"/> (inertialLon = lon + phase(t)). Identity for zero/NaN
            /// shift, null body, or a degenerate rotation period. Pure.
            /// </summary>
            internal static double ShiftLongitudeDegrees(
                double lonDeg, double shiftSeconds, CelestialBody body)
            {
                if (shiftSeconds == 0.0 || double.IsNaN(shiftSeconds) || double.IsInfinity(shiftSeconds))
                    return lonDeg;
                double angle = RotationAngleAtUT(body, shiftSeconds);
                if (angle == 0.0)
                    return lonDeg;
                return WrapLongitudeDegrees(lonDeg - angle);
            }

            /// <summary>
            /// The attitude companion of <see cref="ShiftLongitudeDegrees"/>: rigidly rotating the
            /// recorded state about the body's spin axis by <c>-angle</c> maps the recorded
            /// surface-relative rotation to <c>AngleAxis(-angle, localUp) * srfRel</c> (the spin
            /// axis is the body's local +Y, the same axis lat/lon are defined about; composing
            /// <c>bodyRot * AngleAxis(-angle, up) * srfRel</c> equals rotating the applied world
            /// rotation about the body's world spin axis). Identity for zero/NaN shift or a
            /// degenerate period. Pure apart from Unity quaternion math.
            /// </summary>
            internal static Quaternion ShiftSurfaceRelativeRotation(
                Quaternion srfRel, double shiftSeconds, CelestialBody body)
            {
                if (shiftSeconds == 0.0 || double.IsNaN(shiftSeconds) || double.IsInfinity(shiftSeconds))
                    return srfRel;
                double angle = RotationAngleAtUT(body, shiftSeconds);
                if (angle == 0.0)
                    return srfRel;
                return Quaternion.AngleAxis((float)(-angle), Vector3.up) * srfRel;
            }

            /// <summary>
            /// Lifts body-fixed <c>(lat, lon, alt)</c> at <paramref name="recordedUT"/>
            /// to inertial-longitude <c>(lat, inertialLon, alt)</c>. Inertial
            /// longitude is wrapped to <c>(-180, 180]</c>. Null body or
            /// non-finite / zero rotation period is a no-op (returns the
            /// body-fixed input) and emits a <c>Pipeline-Frame</c> Warn (HR-9).
            /// </summary>
            internal static Vector3d LiftToInertial(double latDeg, double lonDeg, double altMeters,
                CelestialBody body, double recordedUT)
            {
                if (object.ReferenceEquals(body, null))
                {
                    ParsekLog.Warn("Pipeline-Frame",
                        $"LiftToInertial degraded to body-fixed: body=null recordedUT={recordedUT}");
                    return new Vector3d(latDeg, WrapLongitudeDegrees(lonDeg), altMeters);
                }
                double period = ResolveRotationPeriod(body);
                if (double.IsNaN(period) || double.IsInfinity(period) || System.Math.Abs(period) <= double.Epsilon)
                {
                    ParsekLog.Warn("Pipeline-Frame",
                        $"LiftToInertial degraded to body-fixed: body={body.bodyName} rotationPeriod={period} recordedUT={recordedUT}");
                    return new Vector3d(latDeg, WrapLongitudeDegrees(lonDeg), altMeters);
                }

                double phase = (recordedUT * 360.0) / period;
                double inertialLon = WrapLongitudeDegrees(lonDeg + phase);
                return new Vector3d(latDeg, inertialLon, altMeters);
            }

            /// <summary>
            /// Lowers <c>(lat, inertialLon, alt)</c> at <paramref name="playbackUT"/>
            /// back to a world position via <c>body.GetWorldSurfacePosition</c>.
            /// The inverse of <see cref="LiftToInertial"/>: subtracts the
            /// playback-time rotation phase from the inertial longitude (with
            /// wrap to <c>(-180, 180]</c>) before the surface lookup. Null
            /// body returns <c>Vector3d.zero</c> and emits a
            /// <c>Pipeline-Frame</c> Warn (HR-9).
            /// </summary>
            internal static Vector3d LowerFromInertialToWorld(double latDeg, double inertialLonDeg, double altMeters,
                CelestialBody body, double playbackUT)
            {
                if (object.ReferenceEquals(body, null))
                {
                    ParsekLog.Warn("Pipeline-Frame",
                        $"LowerFromInertialToWorld degraded to zero: body=null playbackUT={playbackUT}");
                    return Vector3d.zero;
                }

                double period = ResolveRotationPeriod(body);
                double phase = 0.0;
                if (!double.IsNaN(period) && !double.IsInfinity(period) && System.Math.Abs(period) > double.Epsilon
                    && !double.IsNaN(playbackUT) && !double.IsInfinity(playbackUT))
                {
                    phase = (playbackUT * 360.0) / period;
                }

                double bodyFixedLon = WrapLongitudeDegrees(inertialLonDeg - phase);
                return ResolveWorldSurfacePosition(body, latDeg, bodyFixedLon, altMeters);
            }

            /// <summary>
            /// Phase 4 frame-aware dispatch (design doc §6.2 Stage 2, §18 Phase
            /// 4, §26.1 HR-9). Resolves a smoothed <c>(lat, lon, alt)</c>
            /// spline sample to a world position based on the spline's
            /// <c>FrameTag</c> contract:
            /// <list type="bullet">
            ///   <item>Tag 0 (body-fixed) — straight <c>GetWorldSurfacePosition</c>.</item>
            ///   <item>Tag 1 (inertial-longitude) — re-lower via
            ///     <see cref="LowerFromInertialToWorld"/> at the playback UT.</item>
            ///   <item>Anything else — HR-9 visible failure: emits a
            ///     <c>Pipeline-Smoothing</c> Warn (gated by <paramref name="warnedKeys"/>
            ///     when supplied so a degenerate recording can't flood the log)
            ///     and returns NaN so the caller's outer guard falls back to
            ///     the legacy lerp.</item>
            /// </list>
            /// Extracted from <c>ParsekFlight.InterpolateAndPosition</c> so
            /// the unknown-tag branch can be exercised in xUnit without Unity.
            /// </summary>
            internal static Vector3d DispatchSplineWorldByFrameTag(byte frameTag,
                double latDeg, double lonDeg, double altMeters,
                CelestialBody body, double playbackUT,
                string recordingId, int sectionIndex,
                System.Collections.Generic.HashSet<string> warnedKeys = null)
            {
                switch (frameTag)
                {
                    case 0:
                        return ResolveWorldSurfacePosition(body, latDeg, lonDeg, altMeters);
                    case 1:
                        return LowerFromInertialToWorld(latDeg, lonDeg, altMeters, body, playbackUT);
                    default:
                    {
                        // HR-9: visible failure for an unrecognised tag.
                        // Emits Warn (not VerboseRateLimited) so a programmer
                        // error or a v1 .pann slipping past the gates surfaces
                        // in stock logs. The optional warnedKeys dedup gates
                        // a single (recordingId, sectionIndex) pair to one Warn
                        // per session — a degenerate recording with an unknown
                        // tag at every frame can't flood the log, but each
                        // distinct unknown-tag occurrence is still visible.
                        bool emit = true;
                        if (warnedKeys != null)
                        {
                            string key = recordingId + ":" + sectionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            emit = warnedKeys.Add(key);
                        }
                        if (emit)
                        {
                            ParsekLog.Warn("Pipeline-Smoothing",
                                $"unknown frameTag={frameTag} recordingId={recordingId} sectionIndex={sectionIndex} -- falling back to legacy bracket interpolation");
                        }
                        return new Vector3d(double.NaN, double.NaN, double.NaN);
                    }
                }
            }

            private static double ResolveRotationPeriod(CelestialBody body)
            {
                var seam = RotationPeriodForTesting;
                if (seam != null)
                    return seam(body);
                return body.rotationPeriod;
            }

            private static Vector3d ResolveWorldSurfacePosition(CelestialBody body,
                double latDeg, double lonDeg, double altMeters)
            {
                var seam = WorldSurfacePositionForTesting;
                if (seam != null)
                    return seam(body, latDeg, lonDeg, altMeters);
                return body.GetWorldSurfacePosition(latDeg, lonDeg, altMeters);
            }

            private static double WrapLongitudeDegrees(double lonDeg)
            {
                // Match the existing CatmullRomFit.WrapLongitude contract so
                // body-fixed and inertial longitudes share a single canonical
                // (-180, 180] range.
                if (double.IsNaN(lonDeg) || double.IsInfinity(lonDeg))
                    return lonDeg;
                double wrapped = lonDeg % 360.0;
                if (wrapped > 180.0) wrapped -= 360.0;
                else if (wrapped <= -180.0) wrapped += 360.0;
                return wrapped;
            }
        }
    }
}
