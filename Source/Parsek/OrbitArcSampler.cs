using System;
using Parsek.Patches; // ArcAnomalyMath lives in Parsek.Patches (GhostOrbitLinePatch.cs)
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Shared Kepler-arc sampler: clips a stock <see cref="Orbit"/> to the eccentric-
    /// anomaly arc between two recorded-frame UTs and writes WORLD-frame sample points
    /// (getPositionFromEccAnomalyWithSemiMinorAxis returns referenceBody.position +
    /// relative, so callers subtract body.position for body-relative offsets or feed
    /// ScaledSpace.LocalToScaledSpace directly)
    /// into a caller-owned buffer. This is the single copy of the arc-sampling math
    /// extracted from <c>GhostOrbitArcPatch.UpdateSpline</c>
    /// (Source/Parsek/Patches/GhostOrbitLinePatch.cs); both the existing current-arc
    /// patch (Step 2 of docs/dev/plans/forward-trajectory-render.md, path A) and the
    /// forward-arc renderer (path C) call it, so there is exactly one implementation.
    ///
    /// The math is byte-identical to the pre-extraction patch: 180 samples (the buffer
    /// length the patch passes from <c>OrbitRendererBase.OrbitPoints</c>),
    /// <c>EccentricAnomalyAtUT(start/end)</c>, the elliptical periapsis-wraparound
    /// correction (<see cref="ArcAnomalyMath"/>, elliptical only), and the open-arc
    /// sweep via <c>getPositionFromEccAnomalyWithSemiMinorAxis</c>. The caller owns
    /// the diagnostic logging and the scaled-space conversion
    /// (<c>ScaledSpace.LocalToScaledSpace</c>), since draw-target plumbing differs
    /// between the stock patch (existing <c>OrbitLine.points3</c>) and the forward-arc
    /// <c>VectorLine</c>.
    ///
    /// NOTE: this is a thin Unity-coupled wrapper around the stock <c>Orbit</c> API
    /// (it calls <c>EccentricAnomalyAtUT</c> / <c>GetTrueAnomaly</c> /
    /// <c>getPositionFromEccAnomalyWithSemiMinorAxis</c>), so it is exercised by the
    /// in-game tests rather than xUnit; the pure anomaly branching it delegates to
    /// lives in <see cref="ArcAnomalyMath"/> (unit-tested separately).
    /// </summary>
    internal static class OrbitArcSampler
    {
        /// <summary>
        /// Outcome of <see cref="SampleSegmentArc"/>: whether world-frame points were
        /// written, the sample count actually filled, and the anomaly bounds +
        /// orbit-type flag the caller needs for its diagnostic log and open-arc
        /// draw-range. <c>Sampled == false</c> means the caller must fall through to
        /// the stock full-ellipse path (degenerate / parabolic / out-of-validity UT).
        /// </summary>
        internal struct ArcSampleResult
        {
            public bool Sampled;
            public int Count;       // number of points written into the buffer (== buffer length on success)
            public bool Hyperbolic; // ecc > 1
            public double FromE;    // start (hyperbolic) eccentric anomaly, post-wraparound
            public double ToE;      // end (hyperbolic) eccentric anomaly
        }

        /// <summary>
        /// Sample the open arc of <paramref name="orbit"/> between
        /// <paramref name="startUTRaw"/> and <paramref name="endUTRaw"/> (both in the
        /// RECORDED clock the orbit's epoch is expressed in) into
        /// <paramref name="outPoints"/> as WORLD-frame positions. Returns an
        /// <see cref="ArcSampleResult"/> describing the sample; on
        /// <c>Sampled == false</c> the buffer is left untouched and the caller should
        /// route to stock (exactly-parabolic ecc==1, or a NaN eccentric anomaly from a
        /// degenerate orbit / out-of-validity UT — same fall-through conditions as the
        /// pre-extraction patch).
        ///
        /// Behaviour is byte-identical to the extracted patch block: the caller is
        /// responsible for the full-period early-return (a full revolution renders the
        /// stock complete ellipse, NOT a clipped arc) BEFORE calling this — this
        /// sampler always produces the open <c>fromE..toE</c> arc.
        /// </summary>
        /// <param name="orbit">The (raw-epoch) orbit to clip.</param>
        /// <param name="startUTRaw">Arc start UT in the orbit's recorded clock.</param>
        /// <param name="endUTRaw">Arc end UT in the orbit's recorded clock.</param>
        /// <param name="outPoints">Caller buffer; its length is the sample count
        /// (180 for the stock <c>OrbitPoints</c>). Must be non-null and length &gt;= 2.</param>
        internal static ArcSampleResult SampleSegmentArc(
            Orbit orbit, double startUTRaw, double endUTRaw, Vector3d[] outPoints)
        {
            var result = new ArcSampleResult { Sampled = false };

            if (orbit == null || outPoints == null || outPoints.Length < 2)
                return result;

            // Orbit-type dispatch mirrors the patch: ecc>1 is hyperbolic (open arc, no
            // wraparound), exactly ecc==1 (parabolic) falls through to stock.
            bool hyperbolic = orbit.eccentricity > 1.0;
            if (orbit.eccentricity >= 1.0 && !hyperbolic)
                return result; // exactly-parabolic — caller routes to stock

            // Convert UT bounds to eccentric anomaly (same as PatchRendering/Trajectory).
            double fromE = orbit.EccentricAnomalyAtUT(startUTRaw);
            double toE = orbit.EccentricAnomalyAtUT(endUTRaw);

            // NaN guard — degenerate orbits or UT outside validity → caller routes to stock.
            if (double.IsNaN(fromE) || double.IsNaN(toE))
                return result;

            // Handle wraparound (periapsis crossing), ELLIPTICAL ONLY, same logic as
            // Trajectory.UpdateFromOrbit. GetTrueAnomaly returns [0, 2pi] for E in [0, 2pi).
            // When fromV > toV, the arc wraps through periapsis (V=0); making fromE negative
            // creates a monotonically increasing range that crosses E=0. A hyperbola is
            // monotonic in (eccentric) anomaly H and never wraps, so it MUST NOT get this
            // correction; applying it would fabricate a bogus reversed range.
            double fromV = orbit.GetTrueAnomaly(fromE);
            double toV = orbit.GetTrueAnomaly(toE);
            if (!hyperbolic && ArcAnomalyMath.NeedsPeriapsisWraparound(fromV, toV))
                fromE = ArcAnomalyMath.ApplyPeriapsisWraparound(fromE);

            // Sample the partial arc across all available points. The stock sampler
            // getPositionFromEccAnomalyWithSemiMinorAxis and orbit.semiMinorAxis both dispatch
            // on eccentricity internally (cos/sin for ecc<1, cosh/sinh for ecc>1), so the
            // identical loop produces the correct elliptical OR hyperbolic arc fromE..toE.
            double semiMinorAxis = orbit.semiMinorAxis;
            int count = outPoints.Length; // 180 at stock sampleResolution=2.0
            double interval = (toE - fromE) / (count - 1);

            for (int i = 0; i < count; i++)
                outPoints[i] = orbit.getPositionFromEccAnomalyWithSemiMinorAxis(
                    fromE + interval * (double)i, semiMinorAxis);

            result.Sampled = true;
            result.Count = count;
            result.Hyperbolic = hyperbolic;
            result.FromE = fromE;
            result.ToE = toE;
            return result;
        }
    }
}
