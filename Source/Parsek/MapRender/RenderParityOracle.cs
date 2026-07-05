using System;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 0 / design §14: the recorded-vs-rendered geometry-diff PARITY ORACLE.
    ///
    /// <para>This is a NEW, DISTINCT acceptance axis added BESIDE the existing
    /// <see cref="GhostRenderReconciler"/> (intent-vs-old-truth). The two comparators COEXIST through
    /// Phases 0-8 (Phase 8 unwires the old one); this oracle is NOT a rename or promotion of the
    /// reconciler. Where the reconciler asks "did the new pipeline's INTENT match what the OLD path
    /// actually drew?", this oracle asks "did the geometry the pipeline actually RENDERED match the
    /// REFERENCE geometry it was supposed to render?" - a recorded-vs-rendered (or
    /// intended-vs-rendered) drift, the objective "did I break rendering?" gate every later phase runs
    /// over the regression set.</para>
    ///
    /// <para><b>Two modes (design §14 faithful-vs-synthesized):</b>
    /// <list type="bullet">
    ///   <item><see cref="ParityMode.Faithful"/>: rendered geometry vs the RECORDED source geometry.
    ///     A faithful (non-re-aimed) member must draw exactly what it recorded - any drift is a draw
    ///     regression. Phases 5a/5b/7 gate in this mode.</item>
    ///   <item><see cref="ParityMode.Synthesized"/>: rendered geometry vs the PRODUCER'S INTENDED arc
    ///     (the re-aim / re-time / re-rotate output). Here the oracle validates DRAW-FIDELITY (rendered
    ///     == intended arc), NOT SOLVE-CORRECTNESS (intended arc == physically-correct transfer). The
    ///     latter (plane-tilt / near-180 handedness, etc.) stays the re-aim solver's own test surface.
    ///     Phase 6 (descent re-stitch, which intentionally CHANGES the deorbit geometry) gates in this
    ///     mode.</list>
    /// The diff MATH is identical in both modes; only the REFERENCE point set differs. The mode is
    /// carried on the result purely so a caller / log line records which reference was diffed.</para>
    ///
    /// <para><b>Headless contract:</b> this type is Unity-ECall-FREE and primitive-only. It takes
    /// ALREADY-FRAMED geometry (flat <c>double[]</c> XYZ triples). The Unity sampling + the
    /// body-relative / scaled-to-world framing (reading <c>OrbitDriver</c> world pos, <c>ScaledSpace</c>,
    /// Vectrosity <c>VectorLine.points3</c>) is the NEXT slice in <c>MapRenderProbe</c>; that probe must
    /// hand this oracle points already in a single consistent metres frame (the design's
    /// <c>bodyRelPos</c> for icon/orbit, scaled-&gt;world for the polyline). NaN/Inf handling mirrors
    /// <see cref="GhostRenderReconciler"/> / <c>RewindReadbackGuard</c>: a non-finite input yields NO
    /// false anomaly.</para>
    /// </summary>
    internal static class RenderParityOracle
    {
        /// <summary>Which reference geometry the rendered geometry was diffed against (design §14).</summary>
        internal enum ParityMode : byte
        {
            /// <summary>Default / unset.</summary>
            Unknown = 0,

            /// <summary>Rendered vs the RECORDED source geometry (a faithful member must draw what it recorded).</summary>
            Faithful = 1,

            /// <summary>
            /// Rendered vs the producer's INTENDED arc (re-aim / re-stitch). Validates draw-fidelity, NOT
            /// solve-correctness.
            /// </summary>
            Synthesized = 2,
        }

        internal static string ParityModeToken(ParityMode mode)
        {
            switch (mode)
            {
                case ParityMode.Faithful: return "faithful";
                case ParityMode.Synthesized: return "synthesized";
                default: return "unknown";
            }
        }

        /// <summary>
        /// The outcome of one recorded-vs-rendered (or intended-vs-rendered) geometry diff.
        /// </summary>
        internal readonly struct ParityResult
        {
            /// <summary>The reference the diff was taken against (faithful = recorded, synthesized = intended).</summary>
            internal ParityMode Mode { get; }

            /// <summary>
            /// True when the diff produced a usable measurement: both point sets had at least one finite
            /// point and the rendered polyline had at least one finite segment endpoint to project onto.
            /// When false (<see cref="OverTolerance"/> is forced false) the inputs were empty / all
            /// non-finite and the oracle DELIBERATELY reports no drift (no false anomaly) - the caller
            /// should treat this as "could not measure", not "passed".
            /// </summary>
            internal bool HasMeasurement { get; }

            /// <summary>
            /// The maximum deviation (metres) of any finite REFERENCE point from the rendered polyline
            /// (the nearest-distance from each reference point to the rendered point sequence). 0 when
            /// there is no measurement.
            /// </summary>
            internal double MaxDeviationMeters { get; }

            /// <summary>The tolerance (metres) the deviation was compared against.</summary>
            internal double ToleranceMeters { get; }

            /// <summary>
            /// True when the reference and rendered point COUNTS differed. Not itself an anomaly - the
            /// diff is count-independent (nearest-point projection) - but recorded so a caller can log
            /// it (a large count delta is a useful secondary signal).
            /// </summary>
            internal bool CountMismatch { get; }

            /// <summary>Number of reference points supplied (finite + non-finite).</summary>
            internal int ReferenceCount { get; }

            /// <summary>Number of rendered points supplied (finite + non-finite).</summary>
            internal int RenderedCount { get; }

            /// <summary>
            /// True iff there is a measurement AND the max deviation exceeds the tolerance. This is the
            /// <c>parity-drift</c> decision. Always false when there is no measurement (NaN/Inf/empty
            /// inputs never raise a false anomaly).
            /// </summary>
            internal bool OverTolerance { get; }

            internal ParityResult(
                ParityMode mode, bool hasMeasurement, double maxDeviationMeters, double toleranceMeters,
                bool countMismatch, int referenceCount, int renderedCount, bool overTolerance)
            {
                Mode = mode;
                HasMeasurement = hasMeasurement;
                MaxDeviationMeters = maxDeviationMeters;
                ToleranceMeters = toleranceMeters;
                CountMismatch = countMismatch;
                ReferenceCount = referenceCount;
                RenderedCount = renderedCount;
                OverTolerance = overTolerance;
            }

            internal static ParityResult NoMeasurement(
                ParityMode mode, double toleranceMeters, int referenceCount, int renderedCount)
            {
                return new ParityResult(
                    mode, hasMeasurement: false, maxDeviationMeters: 0.0, toleranceMeters: toleranceMeters,
                    countMismatch: referenceCount != renderedCount,
                    referenceCount: referenceCount, renderedCount: renderedCount, overTolerance: false);
            }
        }

        // ---- Tolerance from geometry scale (design §14: per-scenario, derived from scale, NOT a blanket constant) ----

        /// <summary>
        /// Floor under the scale-derived tolerance (metres). Even a tiny-scale near-surface scene
        /// carries float / interpolation / sampling jitter; below this floor a "drift" is noise.
        /// </summary>
        internal const double MinToleranceMeters = 1.0;

        /// <summary>
        /// Minimum REFERENCE arc extent (metres, the bounding-box diagonal from
        /// <see cref="EstimateScaleFromPoints"/>) below which a SCALE-DERIVED parity diff is NOT a
        /// shape-parity measurement at all (<see cref="ComputeDriftScaleDerived"/> returns
        /// <see cref="ParityResult.NoMeasurement"/>, NOT a pass). The oracle's contract is "is the rendered
        /// ARC the same CURVE as the reference ARC?". A reference whose own points span less than this -
        /// e.g. a landed / near-stationary surface-dwell leg whose <c>GetWorldSurfacePosition</c> samples
        /// are all (near-)coincident, so the bounding box collapses to a POINT - has no curve to compare:
        /// a point-to-polyline distance is "how far is the line from this one point", not a parity of
        /// shape, and a degenerate reference makes the scale-derived tolerance floor to
        /// <see cref="MinToleranceMeters"/> so any sub-meter rendered jitter false-fires (the live Duna One
        /// surface-dwell legs that emitted 20 tol=1m/scale~0 parity-drift anomalies with wildly
        /// frame-unstable maxDev - the signature of a non-measurement, not a fixed mis-draw).
        ///
        /// <para>This is DELIBERATELY a floor on the REFERENCE EXTENT (a no-measurement gate), NOT a
        /// blanket tolerance raise: a genuinely WRONG draw on a SMALL-but-real arc (a few-km descent leg
        /// rendered kilometres off) still has real reference extent &gt;= this floor, derives its own
        /// scale-proportional tolerance, and STILL fires <see cref="ParityResult.OverTolerance"/>. The
        /// floor is two orders of magnitude below the smallest REAL leg extent seen live (the Kerbin
        /// ascent leg at scale~16.9 km and the Duna descent legs at scale~223 km), so it never blinds a
        /// real small leg; it only refuses to measure a point-degenerate reference. 100 m is comfortably
        /// above the metre-scale <c>GetWorldSurfacePosition</c> / float jitter of a "stationary" leg
        /// (~16 m live) yet far below any leg that actually traces an arc.</para>
        /// </summary>
        internal const double MinMeasurableScaleMeters = 100.0;

        /// <summary>
        /// Default fraction of the geometry scale used as the parity tolerance. A rendered arc that is
        /// faithful to within this fraction of the arc's own extent is drawing the same geometry; a
        /// looped-rotation / off-orbit / mis-stitched arc deviates by a far larger fraction. 0.1% of the
        /// scale separates the two with margin (cf. the reconciler's 1-deg icon-off-orbit threshold,
        /// which is ~1.7% of a full circle).
        /// </summary>
        internal const double DefaultScaleToleranceFraction = 0.001;

        /// <summary>
        /// Pure per-scenario tolerance helper: derive the parity tolerance (metres) from the geometry's
        /// own SCALE (its characteristic extent in metres at the relevant map zoom), NOT a blanket
        /// constant. A low-Kerbin-orbit arc (~700 km radius) and a heliocentric transfer (~1e10 m) want
        /// wildly different absolute tolerances; a fixed metre tolerance would either mask real drift on
        /// the big arc or false-fire on float jitter on the small one.
        ///
        /// <para>Returns <c>max(MinToleranceMeters, fraction * |scaleMeters|)</c>. A non-finite or
        /// non-positive scale (degenerate / unknown) falls back to the floor so the caller still gets a
        /// usable, conservative tolerance rather than 0 or NaN. <paramref name="fraction"/> defaults to
        /// <see cref="DefaultScaleToleranceFraction"/>; a non-finite / negative fraction is treated as
        /// the default.</para>
        /// </summary>
        internal static double ToleranceForScale(
            double scaleMeters, double fraction = DefaultScaleToleranceFraction)
        {
            double f = (double.IsNaN(fraction) || double.IsInfinity(fraction) || fraction < 0.0)
                ? DefaultScaleToleranceFraction
                : fraction;
            double scale = (double.IsNaN(scaleMeters) || double.IsInfinity(scaleMeters))
                ? 0.0
                : Math.Abs(scaleMeters);
            double derived = scale * f;
            if (double.IsNaN(derived) || double.IsInfinity(derived))
                return MinToleranceMeters;
            return Math.Max(MinToleranceMeters, derived);
        }

        /// <summary>
        /// Pure: estimate a geometry's characteristic SCALE (metres) from a flat XYZ-triple point set, as
        /// the diagonal of the set's finite-point bounding box. This is the natural "extent of the arc at
        /// the relevant zoom" the tolerance scales off (the same arc the oracle diffs). Non-finite points
        /// are skipped; an empty / all-non-finite / single-point set has zero extent and returns 0 (the
        /// caller's <see cref="ToleranceForScale"/> then floors it).
        /// </summary>
        internal static double EstimateScaleFromPoints(double[] xyzFlat)
        {
            if (xyzFlat == null)
                return 0.0;

            bool any = false;
            double minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
            int n = xyzFlat.Length / 3;
            for (int i = 0; i < n; i++)
            {
                double x = xyzFlat[i * 3];
                double y = xyzFlat[i * 3 + 1];
                double z = xyzFlat[i * 3 + 2];
                if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
                    continue;
                if (!any)
                {
                    minX = maxX = x; minY = maxY = y; minZ = maxZ = z;
                    any = true;
                }
                else
                {
                    if (x < minX) minX = x; else if (x > maxX) maxX = x;
                    if (y < minY) minY = y; else if (y > maxY) maxY = y;
                    if (z < minZ) minZ = z; else if (z > maxZ) maxZ = z;
                }
            }
            if (!any)
                return 0.0;
            double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
            double diag = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return (double.IsNaN(diag) || double.IsInfinity(diag)) ? 0.0 : diag;
        }

        // ---- The geometry diff ----

        /// <summary>
        /// PURE recorded-vs-rendered (or intended-vs-rendered) geometry diff. Both point sets are flat
        /// XYZ triples (<c>[x0,y0,z0, x1,y1,z1, ...]</c>) ALREADY in one consistent metres frame (the
        /// Unity sampler in the next slice supplies body-relative for icon/orbit and scaled-&gt;world for
        /// the polyline). <paramref name="referenceXyz"/> is the geometry the pipeline was SUPPOSED to
        /// draw (the RECORDED source in <see cref="ParityMode.Faithful"/>, the INTENDED arc in
        /// <see cref="ParityMode.Synthesized"/>); <paramref name="renderedXyz"/> is what it ACTUALLY
        /// drew this frame.
        ///
        /// <para>The deviation is count-independent: for each finite REFERENCE point, the nearest
        /// distance to the rendered POLYLINE (every consecutive finite rendered segment, plus the
        /// rendered vertices themselves) is taken, and the max over all reference points is the drift.
        /// This is the right diff for "is the rendered arc the same curve as the reference arc?" even
        /// when the two are sampled at different densities (the common case: a recorded points list vs a
        /// Vectrosity line). Mismatched counts are recorded on the result but are NOT themselves an
        /// anomaly.</para>
        ///
        /// <para>NaN/Inf-safe (mirrors <see cref="GhostRenderReconciler"/>): non-finite points are
        /// skipped on BOTH sides; if no finite reference point or no finite rendered point survives,
        /// the result is <see cref="ParityResult.HasMeasurement"/> = false with
        /// <see cref="ParityResult.OverTolerance"/> = false (no false anomaly). A non-finite / negative
        /// <paramref name="toleranceMeters"/> is treated as <see cref="MinToleranceMeters"/>.</para>
        /// </summary>
        internal static ParityResult ComputeDrift(
            ParityMode mode, double[] referenceXyz, double[] renderedXyz, double toleranceMeters)
        {
            double tol = (double.IsNaN(toleranceMeters) || double.IsInfinity(toleranceMeters)
                          || toleranceMeters < MinToleranceMeters)
                ? MinToleranceMeters
                : toleranceMeters;

            int refCount = referenceXyz == null ? 0 : referenceXyz.Length / 3;
            int rendCount = renderedXyz == null ? 0 : renderedXyz.Length / 3;

            if (refCount == 0 || rendCount == 0)
                return ParityResult.NoMeasurement(mode, tol, refCount, rendCount);

            // Collect the finite rendered points (the polyline vertices we project onto).
            // Done once so the inner per-reference-point loop is a flat scan.
            int finiteRendered = 0;
            double[] rxs = new double[rendCount];
            double[] rys = new double[rendCount];
            double[] rzs = new double[rendCount];
            for (int j = 0; j < rendCount; j++)
            {
                double x = renderedXyz[j * 3];
                double y = renderedXyz[j * 3 + 1];
                double z = renderedXyz[j * 3 + 2];
                if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
                    continue;
                rxs[finiteRendered] = x;
                rys[finiteRendered] = y;
                rzs[finiteRendered] = z;
                finiteRendered++;
            }
            if (finiteRendered == 0)
                return ParityResult.NoMeasurement(mode, tol, refCount, rendCount);

            double maxDev = 0.0;
            int finiteReference = 0;
            for (int i = 0; i < refCount; i++)
            {
                double px = referenceXyz[i * 3];
                double py = referenceXyz[i * 3 + 1];
                double pz = referenceXyz[i * 3 + 2];
                if (!IsFinite(px) || !IsFinite(py) || !IsFinite(pz))
                    continue;
                finiteReference++;

                double nearest = NearestDistanceToPolyline(
                    px, py, pz, rxs, rys, rzs, finiteRendered);
                if (nearest > maxDev)
                    maxDev = nearest;
            }
            if (finiteReference == 0)
                return ParityResult.NoMeasurement(mode, tol, refCount, rendCount);

            bool over = maxDev > tol;
            return new ParityResult(
                mode, hasMeasurement: true, maxDeviationMeters: maxDev, toleranceMeters: tol,
                countMismatch: refCount != rendCount, referenceCount: refCount, renderedCount: rendCount,
                overTolerance: over);
        }

        /// <summary>
        /// Convenience overload: derive the tolerance from the REFERENCE geometry's own scale (its
        /// bounding-box diagonal via <see cref="EstimateScaleFromPoints"/>) so the caller does not have
        /// to pass an explicit metre tolerance. Equivalent to
        /// <c>ComputeDrift(mode, reference, rendered, ToleranceForScale(EstimateScaleFromPoints(reference), fraction))</c>.
        /// The reference (not the rendered) drives the scale: it is the geometry of record, and a drifted
        /// rendered arc could have a misleadingly large or small extent.
        ///
        /// <para>DEGENERATE-REFERENCE GUARD: when the reference's own arc extent is below
        /// <see cref="MinMeasurableScaleMeters"/> (a landed / near-stationary surface-dwell leg whose
        /// points collapse to a POINT), this returns <see cref="ParityResult.NoMeasurement"/> instead of a
        /// floored-tolerance diff. A zero-extent reference has no curve to compare, so a point-to-polyline
        /// distance is not a shape-parity measurement; the floor refuses to MEASURE it rather than
        /// false-firing on sub-meter rendered jitter. This is the scale-derived path ONLY - the
        /// explicit-tolerance <see cref="ComputeDrift"/> overload (the faithful/synthesized conic lenses)
        /// is unaffected.</para>
        ///
        /// <para>MEASUREMENT NOISE FLOOR: <paramref name="minToleranceMeters"/> (default 0 = no floor)
        /// clamps the derived tolerance UP to the caller's known measurement resolution - e.g. the float
        /// quantization of drawn <c>Vector3</c> vertices
        /// (<see cref="RenderGeometrySampler.DrawnVertexQuantizationFloorMeters"/>), which on a body far
        /// from the scaled origin can exceed a small leg's 0.1% scale-derived tolerance. This is honest
        /// metrology, not a tolerance widen: a deviation below the instrument's own resolution is float
        /// noise, unmeasurable by construction, while a real mis-draw larger than the floor still fires.
        /// A non-finite floor is ignored (treated as 0).</para>
        /// </summary>
        internal static ParityResult ComputeDriftScaleDerived(
            ParityMode mode, double[] referenceXyz, double[] renderedXyz,
            double fraction = DefaultScaleToleranceFraction,
            double minToleranceMeters = 0.0)
        {
            double scale = EstimateScaleFromPoints(referenceXyz);

            // Degenerate-reference guard: a reference whose own points span less than the minimum
            // measurable arc extent (a landed / near-stationary surface-dwell leg that collapses to a
            // POINT) is NOT a shape-parity measurement. Return NoMeasurement ("could not measure", caller
            // does NOT emit) rather than letting ToleranceForScale floor to MinToleranceMeters and
            // false-fire on sub-meter rendered jitter. This is a floor on the REFERENCE EXTENT, not a
            // tolerance widen: a SMALL-but-real arc (scale >= the floor) still derives its own
            // scale-proportional tolerance and a real mis-draw still fires OverTolerance. See
            // MinMeasurableScaleMeters.
            if (scale < MinMeasurableScaleMeters)
            {
                int refCount = referenceXyz == null ? 0 : referenceXyz.Length / 3;
                int rendCount = renderedXyz == null ? 0 : renderedXyz.Length / 3;
                return ParityResult.NoMeasurement(
                    mode, ToleranceForScale(scale, fraction), refCount, rendCount);
            }

            double noiseFloor =
                (double.IsNaN(minToleranceMeters) || double.IsInfinity(minToleranceMeters))
                    ? 0.0 : minToleranceMeters;
            double tol = System.Math.Max(ToleranceForScale(scale, fraction), noiseFloor);
            return ComputeDrift(mode, referenceXyz, renderedXyz, tol);
        }

        // ---- Internals (pure, primitive-only) ----

        /// <summary>
        /// Nearest distance from point (px,py,pz) to the polyline through the first <paramref name="count"/>
        /// rendered vertices. With one vertex it is the point-to-point distance; with two or more it is
        /// the min over each segment of the point-to-segment distance. All finite by construction (the
        /// caller pre-filtered non-finite vertices).
        /// </summary>
        private static double NearestDistanceToPolyline(
            double px, double py, double pz,
            double[] xs, double[] ys, double[] zs, int count)
        {
            if (count == 1)
                return Distance(px, py, pz, xs[0], ys[0], zs[0]);

            double best = double.PositiveInfinity;
            for (int s = 0; s < count - 1; s++)
            {
                double d = PointToSegmentDistance(
                    px, py, pz,
                    xs[s], ys[s], zs[s],
                    xs[s + 1], ys[s + 1], zs[s + 1]);
                if (d < best)
                    best = d;
            }
            return best;
        }

        /// <summary>
        /// Pure point-to-segment distance in 3D (metres). The segment is [A,B]; returns the distance from
        /// P to the closest point on the segment (the projection clamped to [A,B]). A degenerate segment
        /// (A==B) falls back to the point-to-A distance.
        /// </summary>
        private static double PointToSegmentDistance(
            double px, double py, double pz,
            double ax, double ay, double az,
            double bx, double by, double bz)
        {
            double abx = bx - ax, aby = by - ay, abz = bz - az;
            double apx = px - ax, apy = py - ay, apz = pz - az;
            double abLenSq = abx * abx + aby * aby + abz * abz;
            if (abLenSq <= 0.0)
                return Distance(px, py, pz, ax, ay, az);

            double t = (apx * abx + apy * aby + apz * abz) / abLenSq;
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;

            double cx = ax + t * abx;
            double cy = ay + t * aby;
            double cz = az + t * abz;
            return Distance(px, py, pz, cx, cy, cz);
        }

        private static double Distance(
            double ax, double ay, double az, double bx, double by, double bz)
        {
            double dx = ax - bx, dy = ay - by, dz = az - bz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
