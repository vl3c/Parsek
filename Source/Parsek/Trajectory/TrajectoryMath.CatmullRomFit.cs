using System;
using System.Collections.Generic;
using Parsek.Rendering;
using UnityEngine;

namespace Parsek
{
    public static partial class TrajectoryMath
    {
        /// <summary>
        /// Phase 1 smoothing-spline math (design doc §6.1, §17.3.1). Pure
        /// uniform Catmull-Rom in (latitude, longitude, altitude) space — the
        /// same coordinate system as <see cref="TrajectoryPoint"/> for
        /// ABSOLUTE-frame body-fixed segments. <see cref="Evaluate"/> returns
        /// a <c>Vector3d(latDeg, lonDeg, altMetres)</c> that the caller hands
        /// to <c>body.GetWorldSurfacePosition</c>.
        ///
        /// <para>
        /// Longitude wrap at +/-180 deg is unwrapped before fitting and
        /// re-wrapped on evaluation — fitting through an unwrapped sequence
        /// avoids the "long way around" interpolation that would otherwise
        /// occur when consecutive samples straddle the antimeridian.
        /// </para>
        /// </summary>
        internal static class CatmullRomFit
        {
            /// <summary>
            /// Fits a uniform Catmull-Rom spline through the supplied samples'
            /// (lat, lon, alt) tuples keyed by sample UT. Rejects samples
            /// fewer than 4, non-monotonic UTs, or non-finite components and
            /// returns <c>default(SmoothingSpline)</c> with
            /// <see cref="SmoothingSpline.IsValid"/> = false plus a populated
            /// <paramref name="failureReason"/>.
            /// </summary>
            internal static SmoothingSpline Fit(
                IList<TrajectoryPoint> samples, double tension, out string failureReason)
            {
                return Fit(samples, tension, out failureReason, rejected: null);
            }

            /// <summary>
            /// Phase 8 overload: when <paramref name="rejected"/> is non-null,
            /// samples whose <c>rejected.IsRejected(i)</c> is true are
            /// excluded from the fit (no knot, no control). After filtering,
            /// the effective sample count must still meet the 4-sample
            /// minimum or the fit returns invalid with a populated
            /// <paramref name="failureReason"/> so the orchestrator can fall
            /// through to the legacy bracket lerp (HR-9 visible failure).
            /// </summary>
            internal static SmoothingSpline Fit(
                IList<TrajectoryPoint> samples, double tension, out string failureReason,
                OutlierFlags rejected)
            {
                failureReason = null;

                if (samples == null)
                {
                    failureReason = "samples list is null";
                    return default(SmoothingSpline);
                }
                if (samples.Count < 4)
                {
                    failureReason = $"need at least 4 samples; got {samples.Count}";
                    return default(SmoothingSpline);
                }

                int rawCount = samples.Count;
                int rawRejectedCount = rejected != null ? rejected.RejectedCount : 0;
                // Pre-allocate to the worst case (no rejections) and trim later.
                double[] knots = new double[rawCount];
                float[] ctrlX = new float[rawCount];
                float[] ctrlY = new float[rawCount];
                float[] ctrlZ = new float[rawCount];

                double prevUT = double.NegativeInfinity;
                double prevLon = double.NaN;
                int kept = 0;
                for (int i = 0; i < rawCount; i++)
                {
                    if (rejected != null && rejected.IsRejected(i)) continue;

                    var p = samples[i];
                    if (!IsFinite(p.ut) || !IsFinite(p.latitude) || !IsFinite(p.longitude) || !IsFinite(p.altitude))
                    {
                        failureReason = $"sample {i} contains NaN or non-finite component (ut={p.ut} lat={p.latitude} lon={p.longitude} alt={p.altitude})";
                        return default(SmoothingSpline);
                    }
                    if (kept > 0 && p.ut <= prevUT)
                    {
                        failureReason = $"non-monotonic UT at sample {i}: {p.ut} <= {prevUT}";
                        return default(SmoothingSpline);
                    }

                    double lon = p.longitude;
                    if (kept > 0)
                    {
                        // Unwrap longitude: keep consecutive deltas within +/-180 deg so
                        // a sequence crossing the antimeridian fits as a continuous
                        // monotone progression instead of jumping by ~360 deg.
                        double delta = lon - prevLon;
                        if (delta > 180.0) lon -= 360.0;
                        else if (delta < -180.0) lon += 360.0;
                    }

                    knots[kept] = p.ut;
                    ctrlX[kept] = (float)p.latitude;
                    ctrlY[kept] = (float)lon;
                    ctrlZ[kept] = (float)p.altitude;

                    prevUT = p.ut;
                    prevLon = lon;
                    kept++;
                }

                if (kept < 4)
                {
                    failureReason = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "after-rejection sample count {0} < min 4 (rejected {1} of {2})",
                        kept, rawRejectedCount, rawCount);
                    return default(SmoothingSpline);
                }

                if (kept < rawCount)
                {
                    // Trim to actual kept count.
                    Array.Resize(ref knots, kept);
                    Array.Resize(ref ctrlX, kept);
                    Array.Resize(ref ctrlY, kept);
                    Array.Resize(ref ctrlZ, kept);
                }

                return new SmoothingSpline
                {
                    SplineType = 0, // Catmull-Rom
                    Tension = (float)tension,
                    KnotsUT = knots,
                    ControlsX = ctrlX,
                    ControlsY = ctrlY,
                    ControlsZ = ctrlZ,
                    FrameTag = 0, // body-fixed
                    IsValid = true,
                };
            }

            /// <summary>
            /// Evaluates the spline at <paramref name="ut"/>. Clamps to the
            /// endpoint sample (bit-exact at <c>ut == knots[0]</c> and
            /// <c>ut == knots[Last]</c>); never extrapolates. Returns
            /// <c>(latDeg, lonDeg, altMetres)</c> in body-fixed coordinates,
            /// re-wrapped to <c>[-180, 180]</c> for longitude.
            /// </summary>
            internal static Vector3d Evaluate(in SmoothingSpline spline, double ut)
            {
                if (!spline.IsValid || spline.KnotsUT == null || spline.KnotsUT.Length < 2)
                    return Vector3d.zero;

                int n = spline.KnotsUT.Length;

                // Endpoint clamps: return raw control values bit-exact so
                // anchor placement at section boundaries does not drift.
                if (ut <= spline.KnotsUT[0])
                {
                    return new Vector3d(
                        spline.ControlsX[0],
                        WrapLongitude(spline.ControlsY[0]),
                        spline.ControlsZ[0]);
                }
                if (ut >= spline.KnotsUT[n - 1])
                {
                    return new Vector3d(
                        spline.ControlsX[n - 1],
                        WrapLongitude(spline.ControlsY[n - 1]),
                        spline.ControlsZ[n - 1]);
                }

                // Locate the segment [i, i+1] containing ut via linear scan
                // (annotation tables are short — typical recording sections
                // hold tens to a few hundred samples).
                int i = 0;
                for (int k = 0; k < n - 1; k++)
                {
                    if (ut >= spline.KnotsUT[k] && ut < spline.KnotsUT[k + 1])
                    {
                        i = k;
                        break;
                    }
                }

                int i0 = i - 1; if (i0 < 0) i0 = 0;
                int i1 = i;
                int i2 = i + 1;
                int i3 = i + 2; if (i3 >= n) i3 = n - 1;

                double segDuration = spline.KnotsUT[i2] - spline.KnotsUT[i1];
                double t = segDuration > 0 ? (ut - spline.KnotsUT[i1]) / segDuration : 0.0;
                if (t < 0) t = 0; else if (t > 1) t = 1;

                float tension = spline.Tension;
                double x = CatmullRomScalar(
                    spline.ControlsX[i0], spline.ControlsX[i1], spline.ControlsX[i2], spline.ControlsX[i3],
                    spline.KnotsUT[i0], spline.KnotsUT[i1], spline.KnotsUT[i2], spline.KnotsUT[i3],
                    t, segDuration, tension);
                double y = CatmullRomScalar(
                    spline.ControlsY[i0], spline.ControlsY[i1], spline.ControlsY[i2], spline.ControlsY[i3],
                    spline.KnotsUT[i0], spline.KnotsUT[i1], spline.KnotsUT[i2], spline.KnotsUT[i3],
                    t, segDuration, tension);
                double z = CatmullRomScalar(
                    spline.ControlsZ[i0], spline.ControlsZ[i1], spline.ControlsZ[i2], spline.ControlsZ[i3],
                    spline.KnotsUT[i0], spline.KnotsUT[i1], spline.KnotsUT[i2], spline.KnotsUT[i3],
                    t, segDuration, tension);

                return new Vector3d(x, WrapLongitude(y), z);
            }

            // Time-aware Catmull-Rom on a single segment. Tangents are computed
            // as value-per-second slopes from the neighbouring knots and then
            // scaled by this segment's duration for the Hermite basis. This
            // keeps short sections from inheriting an unscaled tangent from a
            // much longer adjacent interval.
            // Hermite basis: h00=2t^3-3t^2+1, h10=t^3-2t^2+t, h01=-2t^3+3t^2,
            // h11=t^3-t^2.
            private static double CatmullRomScalar(
                double p0, double p1, double p2, double p3,
                double t0, double t1, double t2, double t3,
                double t, double segmentDuration, double tension)
            {
                double u2 = t * t;
                double u3 = u2 * t;
                double denom1 = t2 - t0;
                double denom2 = t3 - t1;
                double slope1 = denom1 > 0.0 ? (p2 - p0) / denom1 : 0.0;
                double slope2 = denom2 > 0.0 ? (p3 - p1) / denom2 : 0.0;
                double m1 = tension * slope1 * segmentDuration;
                double m2 = tension * slope2 * segmentDuration;
                double h00 = 2.0 * u3 - 3.0 * u2 + 1.0;
                double h10 = u3 - 2.0 * u2 + t;
                double h01 = -2.0 * u3 + 3.0 * u2;
                double h11 = u3 - u2;
                return h00 * p1 + h10 * m1 + h01 * p2 + h11 * m2;
            }

            private static double WrapLongitude(double lonDeg)
            {
                // Map any longitude back into (-180, 180]. Robust against
                // multiple wraps if the unwrap accumulated more than +/-360.
                double wrapped = lonDeg % 360.0;
                if (wrapped > 180.0) wrapped -= 360.0;
                else if (wrapped <= -180.0) wrapped += 360.0;
                return wrapped;
            }

            private static bool IsFinite(double value)
            {
                // Delegates to the enclosing TrajectoryMath.IsFinite (byte-identical
                // body); fully qualified so the call does not recurse into this
                // nested method.
                return TrajectoryMath.IsFinite(value);
            }
        }
    }
}
