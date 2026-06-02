using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Display;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 1 (design §6.2): builds a <see cref="GhostRenderChain"/> for one committed member +
    /// cycle instance from its (recorded or re-aimed) <see cref="IPlaybackTrajectory"/>. Pure given
    /// the injected <paramref name="surface"/> provider (the only KSP-coupled input — body radii —
    /// which the caller wires to FlightGlobals; null in unit tests, matching the polyline renderer).
    ///
    /// The treatment-assignment rule REUSES the existing conic/cover predicate (relocating the
    /// orbit-vs-polyline DECISION here, the one place the design wants it):
    ///   - an above-surface OrbitSegment overlapping the window → StockConic (carrying the conic);
    ///   - recorded Points not inside the orbital cover → TracedPath, split at body changes so each
    ///     segment has exactly one body frame (intra-arc SOI split, §6.2); below-surface orbit
    ///     ranges are excluded from the cover (FIX #27) so descent samples fall to TracedPath.
    /// A body-frame change between adjacent segments is an SOI crossing → a tolerated FlexibleSoi
    /// seam (§3.3); same body → a Rigid seam.
    ///
    /// CANNOT be built/tested in the dev container; written against verified contracts. Two parts are
    /// marked TODO because they want validation against real re-aimed recordings (not KSP disassembly,
    /// but "important decisions" per the maintainer's steer): the generated-transfer marking and the
    /// fine-grained SegmentKind classification. Both are non-load-bearing for routing (treatment +
    /// frame + UT are) and have defensible v1 defaults.
    /// </summary>
    internal static class ChainAssembler
    {
        internal static GhostRenderChain Build(
            IPlaybackTrajectory traj,
            int committedIndex,
            int instanceKey,
            double windowStartUT,
            double windowEndUT,
            bool faithfulFallback = false,
            GhostTrajectoryPolylineRenderer.BodySurfaceProvider surface = null)
        {
            var ordered = new List<RenderSegment>();
            if (traj == null)
            {
                return new GhostRenderChain(null, committedIndex, instanceKey, ordered,
                    windowStartUT, windowEndUT, faithfulFallback);
            }

            List<OrbitSegment> orbitSegs = traj.OrbitSegments ?? new List<OrbitSegment>();
            List<(double startUT, double endUT)> cover =
                GhostTrajectoryPolylineRenderer.ComputeOrbitalCoverIntervals(orbitSegs, surface);

            // (1) Conic segments: above-surface orbit segments clamped to the window → StockConic.
            for (int i = 0; i < orbitSegs.Count; i++)
            {
                OrbitSegment seg = orbitSegs[i];
                if (seg.endUT <= seg.startUT) continue;
                if (GhostTrajectoryPolylineRenderer.IsOrbitSegmentBelowSurface(seg, surface)) continue; // → traced (FIX #27)
                double s = Math.Max(seg.startUT, windowStartUT);
                double e = Math.Min(seg.endUT, windowEndUT);
                if (e <= s) continue; // not in window

                // TODO(§6.9): validate generated-transfer marking against ReaimedTrajectory. isPredicted
                // is the v1 heuristic (the synthesized transfer is predicted), but ballistic-extrapolated
                // recorded tails are also predicted, so this may over-mark; refine when re-aim wiring lands.
                bool generated = seg.isPredicted;
                ordered.Add(new RenderSegment(
                    generated ? SegmentKind.Transfer : SegmentKind.Loiter,
                    Treatment.StockConic, s, e, seg.bodyName,
                    SegmentPayload.ForConic(seg), isGenerated: generated));
            }

            // (2) Traced segments: recorded points not covered by an orbit interval, split at body changes.
            AppendTracedRuns(traj.Points, cover, windowStartUT, windowEndUT, ordered);

            // (3) Order by StartUT, then classify seams by body-frame change.
            ordered.Sort((a, b) => a.StartUT.CompareTo(b.StartUT));
            List<RenderSegment> withSeams = AssignSeams(ordered);

            // §13 diagnostic: one assembly summary per build (every branch logs).
            ParsekLog.Verbose("MapRender", string.Format(CultureInfo.InvariantCulture,
                "assembled chain rec={0} idx={1} inst={2} segs={3} conic={4} traced={5} window=[{6:F1},{7:F1}] faithfulFallback={8}",
                traj.RecordingId ?? "?", committedIndex, instanceKey, withSeams.Count,
                CountTreatment(withSeams, Treatment.StockConic), CountTreatment(withSeams, Treatment.TracedPath),
                windowStartUT, windowEndUT, faithfulFallback));

            return new GhostRenderChain(
                traj.RecordingId, committedIndex, instanceKey, withSeams, windowStartUT, windowEndUT, faithfulFallback);
        }

        private static void AppendTracedRuns(
            List<TrajectoryPoint> points,
            List<(double startUT, double endUT)> cover,
            double windowStartUT, double windowEndUT,
            List<RenderSegment> outSegments)
        {
            if (points == null || points.Count == 0) return;

            bool haveRun = false;
            double runStart = 0, runEnd = 0;
            string runBody = null;

            for (int i = 0; i < points.Count; i++)
            {
                TrajectoryPoint p = points[i];
                bool inWindow = p.ut >= windowStartUT && p.ut <= windowEndUT;
                bool covered = GhostTrajectoryPolylineRenderer.IsInsideAnyOrbitalInterval(p.ut, cover);
                bool usable = inWindow && !covered;

                // Extend the current run only if usable AND same body (a body change is an intra-arc
                // SOI split → flush and start a fresh run, so each segment carries one body frame).
                if (usable && haveRun && string.Equals(p.bodyName, runBody))
                {
                    runEnd = p.ut;
                    continue;
                }

                if (haveRun)
                {
                    FlushRun(runBody, runStart, runEnd, outSegments);
                    haveRun = false;
                }

                if (usable)
                {
                    haveRun = true;
                    runStart = p.ut;
                    runEnd = p.ut;
                    runBody = p.bodyName;
                }
            }

            if (haveRun) FlushRun(runBody, runStart, runEnd, outSegments);
        }

        private static void FlushRun(string body, double startUT, double endUT, List<RenderSegment> outSegments)
        {
            // Need ≥2 distinct-UT samples to form a drawable polyline run; a lone point is dropped.
            if (endUT <= startUT) return;
            // TODO(kind): coarse classification only (ascent vs approach vs landing vs surface). Kind is
            // cosmetic; treatment + frame + UT are load-bearing. Refine with real recordings if useful.
            outSegments.Add(new RenderSegment(
                SegmentKind.Surface, Treatment.TracedPath, startUT, endUT, body, SegmentPayload.Traced));
        }

        private static List<RenderSegment> AssignSeams(List<RenderSegment> ordered)
        {
            int n = ordered.Count;
            var result = new List<RenderSegment>(n);
            for (int i = 0; i < n; i++)
            {
                RenderSegment cur = ordered[i];
                SeamKind leading = i > 0 ? SeamBetween(ordered[i - 1], cur) : SeamKind.None;
                SeamKind trailing = i < n - 1 ? SeamBetween(cur, ordered[i + 1]) : SeamKind.None;
                result.Add(new RenderSegment(
                    cur.Kind, cur.Treatment, cur.StartUT, cur.EndUT, cur.FrameBodyName, cur.Payload,
                    cur.IsGenerated, leading, trailing));
            }
            return result;
        }

        // Body-frame change between adjacent segments == SOI crossing → tolerated flexible seam
        // (design §3.3). Same body → rigid (ascent↔orbit, orbit↔landing must connect cleanly).
        private static SeamKind SeamBetween(RenderSegment a, RenderSegment b)
            => string.Equals(a.FrameBodyName, b.FrameBodyName) ? SeamKind.Rigid : SeamKind.FlexibleSoi;

        private static int CountTreatment(List<RenderSegment> segs, Treatment t)
        {
            int c = 0;
            for (int i = 0; i < segs.Count; i++) if (segs[i].Treatment == t) c++;
            return c;
        }
    }
}
