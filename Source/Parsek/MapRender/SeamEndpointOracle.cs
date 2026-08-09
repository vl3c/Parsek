using System;

namespace Parsek.MapRender
{
    /// <summary>
    /// The ENCOUNTER-GEOMETRY instrument's pure core: at a recorded SOI handoff, did the arc the
    /// pipeline is ACTUALLY rendering reach the destination body's sphere of influence, or did it
    /// dead-end short of it in open space?
    ///
    /// <para><b>Why this lens exists (the 2026-06-15 looped re-aim defect, todo-and-known-bugs.md).</b>
    /// The reported symptom is that a looped, re-aimed interplanetary member's rendered TRANSFER ARC
    /// dead-ends roughly one SOI radius short of the destination while the ICON reaches the SOI. Every
    /// surface that already exists is blind to exactly that:
    /// <list type="bullet">
    ///   <item><see cref="RenderParityOracle"/> measures each segment against its OWN reference, so it
    ///     is blind to the gap BETWEEN two segments - and it EXPLICITLY skips re-aimed members
    ///     (<c>MapRenderProbe.ComputeFaithfulOrbitParity</c>'s <c>reaimed-or-foreign-seed</c> skip),
    ///     which is the whole population the defect lives in.</item>
    ///   <item><c>loop-seam-teleport</c> (GhostRenderTrace) measures the ghost TRANSFORM, not the drawn
    ///     line, and is suppressed at rebind frames by its clock-delta guard.</item>
    ///   <item><c>[ReaimSeam] dist[target]</c> measures the transform too - which the entry records as
    ///     ALREADY reaching the SOI.</item>
    ///   <item><c>xfer-vs-target@soi</c> measures the SOLVE - which the entry records as already
    ///     correct.</item>
    /// </list>
    /// So the missing measurement is the one taken here: the RENDERED conic's own position at the
    /// recorded handoff instant, against the destination body's SOI sphere AT THAT SAME INSTANT.</para>
    ///
    /// <para><b>The statement.</b> A recorded cross-body seam is, by construction, the instant the
    /// vessel crossed the destination's SOI sphere: the recorded leg that follows it is body-relative
    /// to that destination. So a rendered arc that is faithful to that encounter sits ON the sphere at
    /// the seam instant (ratio ~ 1.0), and an arc that never gets there sits OUTSIDE it (ratio &gt; 1).
    /// Reported as a dimensionless RATIO rather than metres so one threshold covers a 47.9 Mm Duna
    /// sphere and an 84.2 Mm Kerbin sphere alike.</para>
    ///
    /// <para><b>Headless contract</b> (mirrors <see cref="RenderParityOracle"/>): Unity-ECall-FREE and
    /// primitive-only. The Unity capture - resolving the covering recorded segment, walking forward to
    /// the next cross-body segment, propagating BOTH the rendered conic and the destination body to the
    /// seam UT through <c>getTruePositionAtUT</c> (never a current-anchored position; see the
    /// <c>TrajectoryMath.EvaluateOrbitSegmentTruePositionAtUT</c> contract), and reading
    /// <c>sphereOfInfluence</c> - lives in <c>MapRenderProbe.ComputeSeamEndpointGeometry</c>. NaN/Inf
    /// handling mirrors <see cref="RenderParityOracle"/> / <c>RewindReadbackGuard</c>: a non-finite or
    /// degenerate input yields NO measurement and therefore NO false anomaly.</para>
    ///
    /// <para><b>Report-only.</b> The raised reason <c>seam-endpoint-outside-soi</c> is registered in
    /// <c>hlib.ANOMALY_REASONS_RAISED_UNGATED</c>, NOT in the gated token set: it flies report-only
    /// first, exactly as the seven reasons promoted on 2026-08-04 did. Read-only observability - it
    /// changes no draw, mutates no state, and has no gameplay effect.</para>
    /// </summary>
    internal static class SeamEndpointOracle
    {
        /// <summary>
        /// The ratio above which the rendered arc is called OUTSIDE the destination's SOI at the
        /// handoff instant. <c>dist / soi</c>, so 1.0 is exactly ON the sphere.
        ///
        /// <para><b>Why this is 1.005 and not 1.0 exactly.</b> A correct encounter is <c>&lt;= 1.0</c>
        /// BY DEFINITION, so 1.0 is the honest ideal threshold - but the measurement is taken through a
        /// recorded Kepler round-trip at a recorded seam UT, and a healthy seam therefore reads a hair
        /// OVER 1.0 rather than exactly 1.0. The slack is sized from the two MEASURED populations, not
        /// from taste (the same two-population calibration shape
        /// <c>GhostRenderTrace.IsLoopSeamTeleport</c>'s 1,000 km floor uses):</para>
        /// <list type="bullet">
        ///   <item><b>Healthy ceiling, MEASURED</b> - the S1.8 <c>S1.8-soi-crossing-playback</c> daily
        ///     (live-proven on the committed duna-direct fixture) resolves the two flown seams
        ///     continuous to 10,146.3 m (Kerbin-&gt;Sun) and 7,284.0 m (Sun-&gt;Duna) against a 25 km
        ///     pin. As a fraction of the crossed sphere: 10,146.3 / 84,159,286 = 1.21e-4 (Kerbin),
        ///     7,284.0 / 47,921,949 = 1.52e-4 (Duna); the 25 km PIN itself is 5.22e-4 of Duna's sphere.
        ///     So measured healthy geometry lives below ~5e-4.</item>
        ///   <item><b>Defect floor, MEASURED</b> - the 2026-06-15 forensics read 1.027 (Sun-&gt;Duna,
        ///     49.19 Mm vs a 47,921,949 m sphere) and 1.043 (Kerbin-&gt;Sun, 87.76 Mm vs 84,159,286 m).
        ///     A faithful loop replay of an interplanetary transfer reads far higher still (the
        ///     destination has moved on in inertial space by the loop shift).</item>
        /// </list>
        /// <para>0.005 sits ~10x above the measured healthy ceiling and ~5x below the smallest measured
        /// defect, separating the populations with margin at BOTH ends. It is deliberately NOT wide
        /// enough to hide a one-SOI-radius dead-end, which is the defect this instrument exists to
        /// notice. <see cref="Evaluate"/> takes the tolerance as a parameter so a caller or a test can
        /// pin the ideal 1.0 boundary directly.</para>
        /// </summary>
        internal const double DefaultRatioTolerance = 1.005;

        /// <summary>
        /// The outcome of one seam-endpoint measurement.
        /// </summary>
        internal readonly struct SeamEndpointResult
        {
            /// <summary>
            /// True when the inputs produced a usable measurement: a finite non-negative endpoint
            /// distance, a finite POSITIVE SOI radius, and a finite ratio. When false,
            /// <see cref="OutsideSoi"/> is forced false - the caller MUST treat this as "could not
            /// measure", never as "passed" (the <see cref="RenderParityOracle.ParityResult"/> contract).
            /// </summary>
            internal bool HasMeasurement { get; }

            /// <summary>
            /// <c>endpointDistance / soiRadius</c>. 1.0 = exactly on the sphere, &lt; 1.0 = inside it
            /// (a real encounter), &gt; 1.0 = the rendered arc dead-ends outside it. 0 when there is no
            /// measurement.
            /// </summary>
            internal double Ratio { get; }

            /// <summary>Distance (metres) from the rendered arc at the seam instant to the destination body.</summary>
            internal double EndpointDistanceMeters { get; }

            /// <summary>The destination body's SOI radius (metres) the distance was measured against.</summary>
            internal double SoiRadiusMeters { get; }

            /// <summary>The ratio threshold <see cref="Ratio"/> was compared against.</summary>
            internal double RatioTolerance { get; }

            /// <summary>
            /// True iff there is a measurement AND <see cref="Ratio"/> exceeds
            /// <see cref="RatioTolerance"/> - the <c>seam-endpoint-outside-soi</c> decision. Always
            /// false without a measurement (non-finite / degenerate inputs never raise).
            /// </summary>
            internal bool OutsideSoi { get; }

            internal SeamEndpointResult(
                bool hasMeasurement, double ratio, double endpointDistanceMeters,
                double soiRadiusMeters, double ratioTolerance, bool outsideSoi)
            {
                HasMeasurement = hasMeasurement;
                Ratio = ratio;
                EndpointDistanceMeters = endpointDistanceMeters;
                SoiRadiusMeters = soiRadiusMeters;
                RatioTolerance = ratioTolerance;
                OutsideSoi = outsideSoi;
            }

            internal static SeamEndpointResult NoMeasurement(double ratioTolerance)
            {
                return new SeamEndpointResult(
                    hasMeasurement: false, ratio: 0.0, endpointDistanceMeters: 0.0,
                    soiRadiusMeters: 0.0, ratioTolerance: ratioTolerance, outsideSoi: false);
            }
        }

        /// <summary>
        /// PURE encounter-geometry predicate: is the rendered arc's position at the recorded SOI-handoff
        /// instant INSIDE the destination body's sphere of influence?
        ///
        /// <para><paramref name="endpointDistanceMeters"/> is
        /// <c>|renderedPos(seamUT) - destinationBodyPos(seamUT)|</c> with BOTH terms propagated to the
        /// SAME seam UT (the epoch-consistent <c>getTruePositionAtUT</c> frame - a current-anchored body
        /// position would disagree by the body's own displacement since that instant, millions of km,
        /// drowning the signal). <paramref name="soiRadiusMeters"/> is that body's
        /// <c>sphereOfInfluence</c>.</para>
        ///
        /// <para>NO MEASUREMENT (and therefore no anomaly) when: the distance is non-finite or negative;
        /// the SOI radius is non-finite or non-positive (a missing / unresolved body, or the Sun, whose
        /// sphere is not a finite encounter target); or the ratio itself comes out non-finite. A
        /// non-finite <paramref name="ratioTolerance"/>, or one below 1.0 (which would call a real
        /// encounter INSIDE the sphere a defect), falls back to
        /// <see cref="DefaultRatioTolerance"/>.</para>
        /// </summary>
        internal static SeamEndpointResult Evaluate(
            double endpointDistanceMeters,
            double soiRadiusMeters,
            double ratioTolerance = DefaultRatioTolerance)
        {
            double tol = (!IsFinite(ratioTolerance) || ratioTolerance < 1.0)
                ? DefaultRatioTolerance
                : ratioTolerance;

            if (!IsFinite(endpointDistanceMeters) || endpointDistanceMeters < 0.0)
                return SeamEndpointResult.NoMeasurement(tol);
            if (!IsFinite(soiRadiusMeters) || soiRadiusMeters <= 0.0)
                return SeamEndpointResult.NoMeasurement(tol);

            double ratio = endpointDistanceMeters / soiRadiusMeters;
            if (!IsFinite(ratio))
                return SeamEndpointResult.NoMeasurement(tol);

            return new SeamEndpointResult(
                hasMeasurement: true, ratio: ratio,
                endpointDistanceMeters: endpointDistanceMeters,
                soiRadiusMeters: soiRadiusMeters,
                ratioTolerance: tol,
                outsideSoi: ratio > tol);
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
