using System;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 0 / design §14: the PURE, Unity-ECall-free part of the recorded-vs-rendered parity
    /// SAMPLER that feeds <see cref="RenderParityOracle"/>. The oracle takes ALREADY-FRAMED flat
    /// <c>double[]</c> XYZ triples in ONE consistent metres frame; this helper does the small amount of
    /// genuinely-new pure logic between a raw <c>Vector3d</c> sample and that flat array:
    ///
    /// <list type="bullet">
    ///   <item>BODY-RELATIVE REFRAMING: turning an ABSOLUTE world position into the orbit's own
    ///     reference-body frame (<c>worldPos - bodyWorldPos</c>) - the SAME frame
    ///     <see cref="MapRenderProbe"/> already uses for the icon-jump / icon-off-orbit checks
    ///     (<c>GetWorldPos3D - referenceBody.position</c>). Both the rendered and the recorded sample
    ///     MUST be reframed against the SAME body world position so the diff is body-motion-free
    ///     (KSP builds an on-rails vessel's world position as <c>referenceBody.position + orbitRelative</c>,
    ///     so the body-relative delta IS the orbital arc, with the floating-origin / body-world-motion
    ///     contamination cancelled).</item>
    ///   <item>FLATTENING a sequence of <c>Vector3d</c> samples into the flat <c>[x0,y0,z0, x1,...]</c>
    ///     layout the oracle expects.</item>
    ///   <item>BUILDING THE PARITY SAMPLE UTs: a small, even spread of UTs across the orbit's visible
    ///     span at which BOTH sides are sampled (the rendered orbit at a live-clock UT, the recorded
    ///     reference at the matching recorded-clock UT = renderedUT - loopShift), so a loop-shifted
    ///     FAITHFUL ghost reads ~0 drift (the shift only sets WHERE on the orbit, not the orbit's shape)
    ///     while a real draw regression (wrong elements / wrong body / icon off its own recorded orbit)
    ///     shows drift.</item>
    /// </list>
    ///
    /// <para>Unity reads (the actual <c>Orbit.getPositionAtUT</c> / <c>OrbitDriver.orbit</c> /
    /// <c>Vessel.GetWorldPos3D</c> sampling) stay in the probe; this helper only manipulates primitives
    /// and <c>Vector3d</c>, so it is unit-testable headless. NaN/Inf-safe: a non-finite sample is written
    /// through unchanged (the oracle skips non-finite points and never raises a false anomaly), never
    /// silently zeroed.</para>
    /// </summary>
    internal static class RenderGeometrySampler
    {
        /// <summary>
        /// Default number of parity sample points spread across the orbit's visible span. Enough to
        /// distinguish two same-shaped orbits at different phases / elements from one continuous
        /// nearest-point projection (the oracle's diff) while keeping the per-frame
        /// <c>Orbit.getPositionAtUT</c> cost trivial (a handful of evaluations per tracked ghost, gated by
        /// <see cref="MapRenderTrace.IsEnabled"/>). Odd so one sample lands on the centre clock.
        /// </summary>
        internal const int DefaultOrbitSampleCount = 9;

        /// <summary>
        /// Pure: reframe an ABSOLUTE world position into the orbit's reference-body frame
        /// (<c>worldPos - bodyWorldPos</c>). A non-finite component on EITHER operand yields a NaN-filled
        /// result so the oracle skips the point (no false anomaly) rather than projecting onto a
        /// body-motion-contaminated delta.
        /// </summary>
        internal static Vector3d ToBodyRelative(Vector3d worldPos, Vector3d bodyWorldPos)
        {
            if (!IsFinite(worldPos) || !IsFinite(bodyWorldPos))
                return new Vector3d(double.NaN, double.NaN, double.NaN);
            return worldPos - bodyWorldPos;
        }

        /// <summary>
        /// Pure: flatten a sequence of <c>Vector3d</c> samples into the flat <c>[x0,y0,z0, x1,...]</c>
        /// layout <see cref="RenderParityOracle.ComputeDrift"/> expects. <paramref name="count"/> caps how
        /// many entries of <paramref name="points"/> are read (so a caller can reuse an oversized scratch
        /// buffer). Non-finite components are written through unchanged - the oracle filters them. A null /
        /// empty source (or count &lt;= 0) returns an empty array (the oracle's "no measurement" path).
        /// </summary>
        internal static double[] Flatten(Vector3d[] points, int count)
        {
            if (points == null || count <= 0)
                return Array.Empty<double>();
            int n = Math.Min(count, points.Length);
            if (n <= 0)
                return Array.Empty<double>();
            var flat = new double[n * 3];
            for (int i = 0; i < n; i++)
            {
                flat[i * 3] = points[i].x;
                flat[i * 3 + 1] = points[i].y;
                flat[i * 3 + 2] = points[i].z;
            }
            return flat;
        }

        /// <summary>
        /// Pure convenience: flatten a single sample into a one-point flat array (the icon point case,
        /// where the oracle's count==1 reference / rendered path applies).
        /// </summary>
        internal static double[] FlattenSingle(Vector3d point)
        {
            return new[] { point.x, point.y, point.z };
        }

        /// <summary>
        /// Pure: build the EVEN spread of <paramref name="count"/> sample UTs across
        /// <c>[centerUT - halfSpanSeconds, centerUT + halfSpanSeconds]</c> the rendered orbit is sampled
        /// at. With <c>count == 1</c> the single sample is the centre clock. The recorded reference is
        /// sampled at <c>renderedUT - loopShiftSeconds</c> for each of these (see
        /// <see cref="ShiftSampleUTs"/>); the rendered orbit at these UTs directly. A non-finite
        /// <paramref name="centerUT"/> / <paramref name="halfSpanSeconds"/>, or a non-positive
        /// <paramref name="count"/>, returns an empty array (caller treats it as "cannot sample").
        /// </summary>
        internal static double[] BuildSampleUTs(double centerUT, double halfSpanSeconds, int count)
        {
            if (count <= 0 || !IsFiniteScalar(centerUT) || !IsFiniteScalar(halfSpanSeconds))
                return Array.Empty<double>();
            double half = Math.Abs(halfSpanSeconds);
            var uts = new double[count];
            if (count == 1)
            {
                uts[0] = centerUT;
                return uts;
            }
            double step = (2.0 * half) / (count - 1);
            for (int i = 0; i < count; i++)
                uts[i] = centerUT - half + i * step;
            return uts;
        }

        /// <summary>
        /// Pure: produce a NEW array of <c>ut - shiftSeconds</c> (the recorded-clock UTs matching a set of
        /// rendered live-clock sample UTs). The loop-epoch shift is <c>liveUT - effUT</c>, so the recorded
        /// reference is sampled at <c>renderedUT - shift</c>. A null source returns an empty array; a
        /// non-finite shift returns the source UTs unchanged (the caller's downstream finite-guard handles
        /// the resulting samples).
        /// </summary>
        internal static double[] ShiftSampleUTs(double[] renderedUTs, double shiftSeconds)
        {
            if (renderedUTs == null)
                return Array.Empty<double>();
            if (!IsFiniteScalar(shiftSeconds))
                return (double[])renderedUTs.Clone();
            var shifted = new double[renderedUTs.Length];
            for (int i = 0; i < renderedUTs.Length; i++)
                shifted[i] = renderedUTs[i] - shiftSeconds;
            return shifted;
        }

        private static bool IsFinite(Vector3d v)
        {
            return IsFiniteScalar(v.x) && IsFiniteScalar(v.y) && IsFiniteScalar(v.z);
        }

        private static bool IsFiniteScalar(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
