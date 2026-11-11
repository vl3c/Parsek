using System;
using System.Collections.Generic;
using UnityEngine;
using Vectrosity;

namespace Parsek.Display
{
    // Seam bridges between a run leg and its conic: bridge conic samples, the angle classifier, merge-slice and chord point builds, and the run-leg hide decisions.
    internal static partial class GhostTrajectoryPolylineRenderer
    {
        // SEAM BRIDGE lines (playtest 6, generalized playtest 7): fillers connecting each drawn
        // body-fixed leg to its adjacent inertial conics. Keyed per (legRecordingId|legIndex|side) so
        // a leg can carry a start-side AND an end-side bridge simultaneously (the Duna descent legs).
        // One fixed-size (BridgeMergeSampleCount+1) VectorLine per key, rebuilt point-wise per frame
        // while bridging; hidden by the bridge deactivation sweep when not drawn. Destroyed with the
        // forward arcs in Clear() / ReleaseForRecording so no Vectrosity GameObject leaks.
        private sealed class BridgeLineEntry
        {
            public VectorLine line;
            public int lastDrawnFrame;
            // Map-line mode (0=unbuilt, 1=3D Draw3D, 2=2D Draw) the line was last built for. Bug 1:
            // Vectrosity's in-place VectorObject3D<->VectorObject2D swap leaves the 2D canvas Graphic
            // non-rendering, so the line is rebuilt when this differs from the live mode (stock MakeLine-
            // on-flip). See RebuildMapLineIfModeChanged.
            public int lineMode;
            // What the line was last drawn for, stamped with lastDrawnFrame (read only by the
            // map-geometry dump's VisitCachedBridgeLines): the leg it joins, the side, and the
            // body its scaled-space points are centred on.
            public string legRecordingId;
            public int legIndex;
            public bool atLegStart;
            public string bodyName;
        }

        // On-demand bridge conic samples (playtest 7): 61 inertial body-relative points along a
        // conic's lead-in or tail, for bridge sides whose conic is NOT a cached forward arc (the
        // CURRENT element - e.g. the final orbit the icon rides while the landing leg lies ahead).
        // Sampled per (recording|segment|side) via Orbit.getPositionAtUT and RESAMPLED IN PLACE when
        // the co-rotating world frame turns under the capture (playtest-9, see
        // ForwardArcSet.captureInverseRotAngle); capped and cleared with the forward caches.
        private sealed class BridgeConicSamples
        {
            public Vector3d[] pts;
            public double captureInverseRotAngle;
        }

        /// <summary>
        /// PURE on-demand bridge sample span (playtest-8 star fix): a third of the segment's duration
        /// OR of one orbital period, whichever is SHORTER (>= 1 s). Without the period clamp, a
        /// multi-revolution segment (the ~660-rev parking-ellipse loiter) yields 61 samples tens of
        /// thousands of seconds apart - arbitrary orbit phases that drew as a star polygon around
        /// Kerbin. A non-finite period (hyperbolic / unknown mu) falls back to duration/3.
        /// xUnit-testable.
        /// </summary>
        internal static double ComputeBridgeSampleSpanSeconds(
            double segStartUT, double segEndUT, double periodSeconds)
        {
            double duration = segEndUT - segStartUT;
            if (duration <= 0.0) return 0.0;
            double baseSpan = duration;
            if (!double.IsNaN(periodSeconds) && !double.IsInfinity(periodSeconds)
                && periodSeconds > 0.0 && periodSeconds < baseSpan)
                baseSpan = periodSeconds;
            return System.Math.Max(baseSpan / 3.0, 1.0);
        }

        /// <summary>
        /// Returns 61 inertial body-relative samples along <paramref name="seg"/>'s lead-in
        /// (<paramref name="tail"/>=false: [startUT, startUT+span]) or tail (true:
        /// [endUT-span, endUT]), span per <see cref="ComputeBridgeSampleSpanSeconds"/> (duration- AND
        /// period-clamped), cached per (recording|segment|side). Null on a degenerate segment / orbit
        /// construction fault (the caller skips the bridge).
        /// </summary>
        private static Vector3d[] GetBridgeConicSamples(
            string recordingId, OrbitSegment seg, CelestialBody body, bool tail)
        {
            if (body == null || seg.endUT <= seg.startUT) return null;
            // Key includes the conic's ELEMENTS (review MINOR-3): a re-aim synodic-window rollover can
            // swap a segment's Kepler elements while keeping its UT span, and a UT-only key would then
            // serve stale wrong-aimed samples (the forward-arc cache keys on the re-aim window for the
            // same reason; sma+ecc discriminate the swapped geometry without plumbing the window index).
            string key = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}|{1:F1}|{2:F1}|{3:F0}|{4:F6}|{5}", recordingId, seg.startUT, seg.endUT,
                seg.semiMajorAxis, seg.eccentricity, tail ? "t" : "l");
            double liveRotAngle = Planetarium.InverseRotAngle;
            if (bridgeConicSampleCache.TryGetValue(key, out var cached))
            {
                // Rotating-frame staleness (playtest 9): when the co-rotating world frame turned
                // since capture, refill the SAME array (orbit evaluation is frame-correct per call).
                if (!HasFrameRotationDrift(cached.captureInverseRotAngle, liveRotAngle))
                    return cached.pts;
                if (FillBridgeConicSamples(seg, body, tail, cached.pts))
                {
                    cached.captureInverseRotAngle = liveRotAngle;
                    return cached.pts;
                }
                return cached.pts; // resample fault: keep the stale frame rather than dropping the bridge
            }

            var pts = new Vector3d[BridgeMergeSampleCount + 1];
            if (!FillBridgeConicSamples(seg, body, tail, pts))
                return null;

            if (bridgeConicSampleCache.Count >= BridgeConicSampleCacheCap)
                bridgeConicSampleCache.Clear();
            bridgeConicSampleCache[key] = new BridgeConicSamples
            {
                pts = pts,
                captureInverseRotAngle = liveRotAngle,
            };
            return pts;
        }

        /// <summary>
        /// Samples <paramref name="seg"/>'s lead-in/tail span into <paramref name="pts"/> (in place;
        /// the rotating-frame resample reuses the cached array). False on a degenerate segment /
        /// orbit fault / non-finite sample - the array is then left untouched past the failure point,
        /// so callers must not publish a half-filled FRESH array (the in-place resample tolerates it:
        /// a stale-but-coherent frame beats a torn one only when the prior content was coherent,
        /// which a cache hit guarantees).
        /// </summary>
        private static bool FillBridgeConicSamples(
            OrbitSegment seg, CelestialBody body, bool tail, Vector3d[] pts)
        {
            double span = ComputeBridgeSampleSpanSeconds(
                seg.startUT, seg.endUT,
                ForwardRenderWindow.ComputePeriod(seg.semiMajorAxis, body.gravParameter));
            if (span <= 0.0) return false;
            double t0 = tail ? seg.endUT - span : seg.startUT;
            try
            {
                var orbit = new Orbit(
                    seg.inclination, seg.eccentricity, seg.semiMajorAxis,
                    seg.longitudeOfAscendingNode, seg.argumentOfPeriapsis,
                    seg.meanAnomalyAtEpoch, seg.epoch, body);
                Vector3d bodyPos = body.position;
                for (int i = 0; i <= BridgeMergeSampleCount; i++)
                {
                    double ut = t0 + span * i / BridgeMergeSampleCount;
                    Vector3d world = orbit.getPositionAtUT(ut);
                    if (!IsFiniteVec(world)) return false;
                    pts[i] = world - bodyPos;
                }
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// PURE: angle (radians) between two body-relative rays, used by the decide-side bridge gate.
        /// Returns PositiveInfinity for a degenerate (near-zero) input so the gate always rejects it.
        /// xUnit-testable (System.Math only, no Unity ECalls).
        /// </summary>
        internal static double SeamBridgeAngleRad(Vector3d a, Vector3d b)
        {
            double am = a.magnitude, bm = b.magnitude;
            if (am < 1e-9 || bm < 1e-9) return double.PositiveInfinity;
            Vector3d an = a / am, bn = b / bm;
            double cross = Vector3d.Cross(an, bn).magnitude;
            double dot = Vector3d.Dot(an, bn);
            return System.Math.Atan2(cross, dot);
        }

        /// <summary>How the decide-side gate treats a seam given its leg-to-conic angle.</summary>
        internal enum SeamBridgeKind { SkipAngleTooLarge, SkipMeetsConic, Chord, MergeSlice }

        /// <summary>
        /// PURE four-band seam-bridge classifier by leg-to-conic angle (launch-&gt;escape seam render):
        /// (a) infinite / &gt; <see cref="BridgeMaxAngleRadians"/> (45 deg) -&gt; SkipAngleTooLarge (honest
        /// gap; the merge slice would draw a wild spiral); (b) (5 deg, 45 deg] -&gt; MergeSlice (the
        /// moderate-misalignment smoother); (c) (<see cref="BridgeChordMinAngleRadians"/> 0.5 deg, 5 deg]
        /// -&gt; Chord (a small but visible gap the merge slice mutes itself on - a zero-bulge straight
        /// chord closes it); (d) &lt;= 0.5 deg -&gt; SkipMeetsConic (the leg already meets the conic within
        /// a few km; a chord would be degenerate). xUnit-testable.
        /// </summary>
        internal static SeamBridgeKind ClassifySeamBridgeByAngle(double angleRad)
        {
            if (double.IsInfinity(angleRad) || angleRad > BridgeMaxAngleRadians)
                return SeamBridgeKind.SkipAngleTooLarge;
            if (angleRad <= BridgeChordMinAngleRadians)
                return SeamBridgeKind.SkipMeetsConic;
            if (angleRad <= BridgeMinAngleRadians)
                return SeamBridgeKind.Chord;
            return SeamBridgeKind.MergeSlice;
        }

        /// <summary>
        /// PURE seam-bridge geometry (playtest 6): fills <paramref name="outPoints"/> with
        /// <paramref name="mergeCount"/>+1 body-relative points connecting <paramref name="endARel"/>
        /// (the body-fixed head leg's drawn end) to <c>arcRelPoints[mergeCount]*arcScale</c> (a point
        /// ON the inertial arc B), following B'S OWN SHAPE with the seam rotation unwound along it:
        /// point i is <c>arcRelPoints[i]*arcScale</c> rotated about the seam axis (the minimal-rotation
        /// axis from B's first sample ray to A's end ray - for same-latitude seam points that IS the
        /// body spin axis, the conic-anchor precedent) by <c>seamAngle*(1 - i/mergeCount)</c>, with a
        /// radial blend so point 0 lands EXACTLY on <paramref name="endARel"/>. Returns false (no
        /// bridge) when inputs are degenerate, the rays are antiparallel (no unique axis), or the seam
        /// angle exceeds <paramref name="maxAngleRad"/>. Rodrigues rotation in doubles - no Unity
        /// ECalls, so it is directly xUnit-testable.
        /// </summary>
        /// <param name="endARel">Bridge start: head leg's last drawn point, body-relative.</param>
        /// <param name="arcRelPoints">B's body-relative sample offsets (forward-arc cache).</param>
        /// <param name="arcScale">Scale applied to every arc sample (1 for metre-space tests; the
        /// Driver passes ScaledSpace.InverseScaleFactor so the output is scaled-space-relative).</param>
        /// <param name="mergeCount">B sample index where the bridge merges into B (uses 0..mergeCount).</param>
        /// <param name="maxAngleRad">Seam-angle gate; larger gaps draw no bridge.</param>
        /// <param name="outPoints">Filled with mergeCount+1 body-relative points.</param>
        /// <param name="seamAngleRad">The seam angle actually measured (diagnostic).</param>
        internal static bool TryBuildSeamBridgeLocalPoints(
            Vector3d endARel, Vector3d[] arcRelPoints, double arcScale, int mergeCount,
            double maxAngleRad, Vector3d[] outPoints, out double seamAngleRad)
        {
            seamAngleRad = double.NaN;
            if (arcRelPoints == null || outPoints == null) return false;
            if (mergeCount < 1 || arcRelPoints.Length <= mergeCount) return false;
            if (outPoints.Length < mergeCount + 1) return false;

            Vector3d b0 = arcRelPoints[0] * arcScale;
            double aMag = endARel.magnitude, bMag = b0.magnitude;
            if (aMag < 1e-9 || bMag < 1e-9) return false;

            Vector3d axisRaw = Vector3d.Cross(b0 / bMag, endARel / aMag);
            double sinA = axisRaw.magnitude;
            double cosA = Vector3d.Dot(b0 / bMag, endARel / aMag);
            double angle = System.Math.Atan2(sinA, cosA);
            seamAngleRad = angle;
            if (angle > maxAngleRad) return false;
            // Antiparallel rays have no unique rotation axis; also covered by any sane maxAngleRad,
            // but guard explicitly so a caller passing a huge gate cannot divide by ~0 below.
            if (sinA < 1e-12)
            {
                if (cosA < 0.0) return false;
                // Aligned rays (seam closed): the bridge IS B's lead-in, with only the radial blend.
                for (int i = 0; i <= mergeCount; i++)
                {
                    double w = (double)i / mergeCount;
                    Vector3d p = arcRelPoints[i] * arcScale;
                    double radial = 1.0 + (aMag / bMag - 1.0) * (1.0 - w);
                    outPoints[i] = p * radial;
                }
                return true;
            }

            Vector3d axis = axisRaw / sinA;
            double radial0 = aMag / bMag;
            for (int i = 0; i <= mergeCount; i++)
            {
                double w = (double)i / mergeCount;
                Vector3d p = arcRelPoints[i] * arcScale;
                double theta = angle * (1.0 - w);
                // Rodrigues: v cos(t) + (axis x v) sin(t) + axis (axis . v)(1 - cos(t)).
                double ct = System.Math.Cos(theta), st = System.Math.Sin(theta);
                Vector3d rotated = p * ct
                    + Vector3d.Cross(axis, p) * st
                    + axis * (Vector3d.Dot(axis, p) * (1.0 - ct));
                // Radial blend so bridge[0] lands EXACTLY on endARel (the rotation preserves |p|, and
                // A's end can sit at a slightly different radius than B's first sample).
                double radial = 1.0 + (radial0 - 1.0) * (1.0 - w);
                outPoints[i] = rotated * radial;
            }
            return true;
        }

        /// <summary>
        /// PURE small-gap CHORD builder (launch-&gt;escape seam render): a STRAIGHT line from the leg's
        /// drawn end (<paramref name="endARel"/>, scaled body-relative) to the conic's near endpoint
        /// (<paramref name="conicNearRel"/>, the conic's first sample in body-LOCAL metres, scaled by
        /// <paramref name="arcScale"/>). Used instead of <see cref="TryBuildSeamBridgeLocalPoints"/>'s
        /// conic-merge slice when the seam gap is small (a few degrees): the merge slice's fixed
        /// ~200-370 km off-chord bulge would dwarf the gap, but a chord just closes it with zero bulge.
        /// outPoints[0] == endARel (leg end), outPoints[mergeCount] == the conic near endpoint, linearly
        /// interpolated between. No rotation, no radial blend, no conic clip (the chord ends exactly where
        /// the conic begins). xUnit-testable (no Unity ECalls).
        /// </summary>
        internal static bool TryBuildSeamBridgeChordLocalPoints(
            Vector3d endARel, Vector3d conicNearRel, double arcScale, int mergeCount, Vector3d[] outPoints)
        {
            if (outPoints == null || mergeCount < 1 || outPoints.Length < mergeCount + 1) return false;
            Vector3d b0 = conicNearRel * arcScale;
            for (int i = 0; i <= mergeCount; i++)
            {
                double w = (double)i / mergeCount;
                outPoints[i] = endARel * (1.0 - w) + b0 * w;
            }
            return true;
        }

        /// <summary>
        /// PURE bridge-target selector: among the cached forward arcs of one recording, the index of
        /// the arc that CONTINUES the body-fixed head leg - same body, starting at/just after the
        /// leg's endUT (1 s shared-boundary tolerance) and within
        /// <paramref name="maxSeamGapSeconds"/> - choosing the earliest-starting candidate. -1 when
        /// none. xUnit-testable (operates on the cached arc metadata only).
        /// </summary>
        /// <summary>
        /// PURE diagnostic: max perpendicular deviation of the interior points from the straight chord
        /// (points[0] -> points[count-1]), in the points' own units. Quantifies a bridge's CURVATURE
        /// in the draw log (playtest-11 "the bridge looks straight" report): a genuinely straight
        /// bridge reads ~0, a healthy conic-tail bridge reads a substantial fraction of its length.
        /// xUnit-testable.
        /// </summary>
        internal static double MaxChordDeviation(Vector3d[] points, int count)
        {
            if (points == null || count < 3 || points.Length < count) return 0.0;
            Vector3d a = points[0];
            Vector3d ab = points[count - 1] - a;
            double abLen = ab.magnitude;
            if (abLen < 1e-9) return 0.0;
            Vector3d dir = ab / abLen;
            double max = 0.0;
            for (int i = 1; i < count - 1; i++)
            {
                Vector3d ap = points[i] - a;
                Vector3d perp = ap - dir * Vector3d.Dot(ap, dir);
                double d = perp.magnitude;
                if (d > max) max = d;
            }
            return max;
        }

        /// <summary>
        /// PURE adjacency rule (playtest-7 generalized bridges): is this conic the inertial NEIGHBOUR of
        /// a body-fixed leg at the given seam? <paramref name="atLegStart"/>=true tests the PREV side
        /// (the conic ENDS at/just before the leg's startUT, 1 s shared-boundary tolerance forward);
        /// false tests the NEXT side (the conic STARTS at/just after the leg's endUT). Same-body only;
        /// the seam UT gap is bounded by <paramref name="maxSeamGapSeconds"/>. The single load-bearing
        /// predicate behind the Driver's adjacent-conic search, so the tested rule IS the production
        /// rule. xUnit-testable.
        /// </summary>
        internal static bool IsBridgeAdjacentConic(
            string segBodyName, double segStartUT, double segEndUT,
            string legBodyName, double legSeamUT, bool atLegStart, double maxSeamGapSeconds)
        {
            if (string.IsNullOrEmpty(legBodyName)) return false;
            if (!string.Equals(segBodyName, legBodyName, StringComparison.Ordinal)) return false;
            return atLegStart
                ? segEndUT >= legSeamUT - maxSeamGapSeconds && segEndUT <= legSeamUT + 1.0
                : segStartUT >= legSeamUT - 1.0 && segStartUT <= legSeamUT + maxSeamGapSeconds;
        }

        /// <summary>
        /// One bridge-eligible leg's UT span + body, for the pure intervening-leg predicate. A flat
        /// projection of <c>BridgeLegCandidate</c> so the rule is xUnit-testable without the private
        /// Driver scratch type.
        /// </summary>
        internal struct BridgeLegSpan
        {
            public double startUT;
            public double endUT;
            public string bodyName;
        }

        /// <summary>
        /// PURE intervening-ascent-leg rule (launch-escape-seam render fix): once an adjacent conic has
        /// been chosen for a candidate leg's seam, is there ANOTHER body-fixed leg in the same chain run
        /// (same body) that CONTINUES from this leg's seam BEFORE the conic - i.e. the conic is NOT this
        /// leg's immediate next/prev segment? A launch records across two consecutive body-fixed legs
        /// (pad ascent -> continuation ascent) feeding one escape conic; the pad-ascent leg's end is
        /// adjacent to the conic only in UT, but the CONTINUATION leg sits between them, so the pad
        /// leg's bridge would shortcut over it. Only the leg IMMEDIATELY adjacent to the conic may bridge.
        ///
        /// <para>END side (<paramref name="atLegStart"/>=false): the conic STARTS at <paramref name="conicSeamUT"/>
        /// (its startUT); skip this bridge when another body-fixed leg STARTS at/after this leg's
        /// <paramref name="legSeamUT"/> (the leg-end, shared-boundary slack) and STRICTLY before the conic
        /// start. A launch's continuation leg starts exactly at the pad leg's end and ends before the
        /// conic, so its start lands in <c>[legEnd - tol, conicStart - tol)</c> - it intervenes. START side
        /// (true): the conic ENDS at <paramref name="conicSeamUT"/> (its endUT); skip when another body-fixed
        /// leg ENDS at/before this leg's start (shared slack) and strictly after the conic end - i.e. its end
        /// lands in <c>(conicEnd + tol, thisLegStart + tol]</c>.</para>
        ///
        /// <para>The candidate itself (<paramref name="selfIndex"/>) is excluded; only same-body legs
        /// count (a different body's leg cannot be a continuation across an SOI seam here). The boundary
        /// tolerance <paramref name="seamTolSeconds"/> mirrors the 1 s shared-seam slack so a leg whose
        /// start coincides with this leg's end (a true continuation handoff) still counts as intervening.
        /// xUnit-testable (no Unity ECalls).</para>
        /// </summary>
        internal static bool HasInterveningContinuationLeg(
            BridgeLegSpan[] legs, int selfIndex, string candBodyName,
            double legSeamUT, double conicSeamUT, bool atLegStart, double seamTolSeconds,
            out int interveningIndex, out double interveningSeamUT)
        {
            interveningIndex = -1;
            interveningSeamUT = double.NaN;
            if (legs == null || string.IsNullOrEmpty(candBodyName)) return false;
            for (int i = 0; i < legs.Length; i++)
            {
                if (i == selfIndex) continue;
                var other = legs[i];
                if (!string.Equals(other.bodyName, candBodyName, StringComparison.Ordinal)) continue;
                // The other leg's seam that attaches it to the chain at the conic-facing end: its START on
                // the end side (it continues forward from this leg's end toward the conic), its END on the
                // start side (it feeds backward from this leg's start toward the conic behind it).
                double otherSeam = atLegStart ? other.endUT : other.startUT;
                bool intervenes = atLegStart
                    // start side: another leg ENDS at/before this leg's start (shared slack) yet strictly
                    // after the conic that ends behind it - it sits between the conic and this leg.
                    ? otherSeam <= legSeamUT + seamTolSeconds && otherSeam > conicSeamUT + seamTolSeconds
                    // end side: another leg STARTS at/after this leg's end (shared slack) yet strictly
                    // before the conic that starts ahead of it - it sits between this leg and the conic.
                    : otherSeam >= legSeamUT - seamTolSeconds && otherSeam < conicSeamUT - seamTolSeconds;
                if (intervenes)
                {
                    interveningIndex = i;
                    interveningSeamUT = otherSeam;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// PURE signed-gap rule (playtest-7, maintainer rule): a bridge fills a REAL trajectory gap -
        /// the previous element's drawn end sits BEHIND the next element's drawn start along the
        /// direction of travel. When the previous end has rotated PAST the next start (overshoot, the
        /// two lines already overlap), drawing a bridge would double back - skip it. The displacement
        /// from the previous end to the next start projected on the travel direction decides:
        /// positive = real gap (bridge), non-positive = overshoot/contact (no bridge). All three
        /// vectors are body-relative in the same frame; scale-invariant. xUnit-testable.
        /// </summary>
        internal static bool IsSeamGapAhead(
            Vector3d prevEndRel, Vector3d nextStartRel, Vector3d travelDir)
            => Vector3d.Dot(nextStartRel - prevEndRel, travelDir) > 0.0;

        /// <summary>
        /// PURE run-leg visibility rule (playtest-7 revision of the playtest-5 hide): a body-fixed
        /// (non-anchorable) run leg is hidden only when it is entirely in the PAST (the icon has moved
        /// beyond its end) - the trailing piece would rotate-sweep into the inertial line behind the
        /// icon. A FUTURE body-fixed leg (the Duna landing descent) DRAWS, body-fixed where the planet
        /// rotation currently places it, connected to the inertial run by the seam bridges; previously
        /// it was hidden until the icon reached it, which read as the landing line "appearing at the
        /// end". xUnit-testable.
        /// </summary>
        internal static bool ShouldHideBodyFixedRunLeg(
            bool anchorCandidate, double legEndUT, double headUT)
            => !anchorCandidate && legEndUT <= headUT;

        /// <summary>
        /// PURE terminal-leg exception input (playtest 11): does any ABOVE-SURFACE conic in this
        /// segment list start at/after <paramref name="ut"/> (1 s shared-boundary tolerance) and
        /// before <paramref name="windowStopUT"/>? The past-hide rule exists because a trailing
        /// body-fixed leg SWEEPS INTO inertial geometry that FOLLOWS it (the launch ascent under the
        /// suborbital conic). A TERMINAL past leg - the Duna landing trail, with nothing after it in
        /// the run - cannot overlap anything and stays visible (glued to its landing site, bridged to
        /// the orbit behind it); hiding it at touchdown read as "the landing line disappeared while
        /// the rest of the trajectory stayed". Below-surface conics do not count: they are never
        /// drawn. xUnit-testable (surface seam injected).
        /// </summary>
        internal static bool AnyAboveSurfaceConicStartsAtOrAfter(
            List<OrbitSegment> segs, double ut, double windowStopUT, BodySurfaceProvider surface)
        {
            if (segs == null) return false;
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                if (s.endUT <= s.startUT) continue;
                if (s.startUT < ut - 1.0) continue;
                if (s.startUT >= windowStopUT) continue;
                if (IsSegmentExcludedFromOrbitOwnership(s, surface)) continue;
                return true;
            }
            return false;
        }
    }
}
