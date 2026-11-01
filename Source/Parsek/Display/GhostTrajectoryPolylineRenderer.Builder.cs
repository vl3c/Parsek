using System;
using System.Collections.Generic;
using UnityEngine;
using Vectrosity;

namespace Parsek.Display
{
    // The pure leg builder: content hash, frameless-gap and predicted-tail conic fills, per-section leg assembly, downsampling and the orbital-cover interval filters.
    internal static partial class GhostTrajectoryPolylineRenderer
    {
        /// <summary>
        /// Cheap content-hash key for cache invalidation (§1.4). XORs every
        /// sample UT and every TrackSection start/end UT so a
        /// supersede-time re-cut that preserves the four counts and the
        /// rec.EndUT still flips the hash.
        /// </summary>
        internal static long ComputeContentHash(Recording rec)
        {
            if (rec == null) return 0;
            long hash = 0;
            int pointCount = rec.Points != null ? rec.Points.Count : 0;
            int segCount = rec.OrbitSegments != null ? rec.OrbitSegments.Count : 0;
            int sectionCount = rec.TrackSections != null ? rec.TrackSections.Count : 0;
            hash ^= pointCount;
            hash ^= (long)segCount << 16;
            hash ^= (long)sectionCount << 32;
            hash ^= BitConverter.DoubleToInt64Bits(rec.EndUT);
            if (rec.Points != null)
            {
                for (int i = 0; i < rec.Points.Count; i++)
                    hash ^= BitConverter.DoubleToInt64Bits(rec.Points[i].ut);
            }
            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    var s = rec.TrackSections[i];
                    hash ^= BitConverter.DoubleToInt64Bits(s.startUT);
                    hash ^= BitConverter.DoubleToInt64Bits(s.endUT);
                }
            }
            return hash;
        }

        /// <summary>
        /// Seam (playtest-12 gap-fill): samples a recorded conic at one UT and returns the
        /// RECORDED-basis body-fixed (lat, lon, alt) triple (lon counter-rotated via
        /// <see cref="RecordedLongitudeAtUT"/> so it matches the recorded points around it). The
        /// Driver supplies a live Orbit-backed implementation; xUnit tests pass a synthetic one. The
        /// pure builder never touches KSP through this.
        /// </summary>
        internal delegate bool ConicGapSampler(
            OrbitSegment seg, double ut, out double lat, out double lon, out double alt);

        /// <summary>
        /// PURE gap-fill pass (playtest-12 straight-chord fix): the Duna landing leg merges Absolute
        /// frame clusters ACROSS frameless OrbitalCheckpoint spans whose covering conics are
        /// BELOW-SURFACE (excluded from the orbital cover by FIX #27, so no arc draws them and the
        /// leg chords straight across - the "straight segment right before the landing"). For every
        /// consecutive same-body sample pair separated by more than <see cref="GapFillMinSeconds"/>
        /// whose open span is covered by a below-surface recorded conic, inserts up to
        /// <see cref="GapFillMaxPointsPerGap"/> interior points sampled from THAT CONIC via
        /// <paramref name="sampler"/> - the recorded descent shape itself, so the chord gains the
        /// exact recorded curvature. RENDER-time only: the recording's own lists are never touched
        /// (points are appended to the build stream copy). Returns the number of points inserted
        /// (caller re-sorts the stream when &gt; 0). No-op without a sampler or surface provider
        /// (below-surface classification needs body radii). xUnit-testable with synthetic seams.
        /// </summary>
        internal static int FillFramelessGapsFromConics(
            List<TrajectoryPoint> pts, Recording rec, BodySurfaceProvider surface,
            ConicGapSampler sampler)
        {
            if (pts == null || pts.Count < 2 || rec == null || rec.OrbitSegments == null
                || sampler == null || surface == null)
                return 0;

            int inserted = 0;
            int originalCount = pts.Count;
            for (int i = 0; i < originalCount - 1; i++)
            {
                var p = pts[i];
                var q = pts[i + 1];
                if (!string.Equals(p.bodyName, q.bodyName, StringComparison.Ordinal)) continue;
                double dt = q.ut - p.ut;
                if (dt <= GapFillMinSeconds) continue;

                // The covering BELOW-SURFACE conic (the recorded descent shape). Above-surface conics
                // never reach here: their cover intervals split the leg at step (2)'s
                // OrbitalIntervalBetween, so a gap inside one cannot exist within a single run.
                int segIdx = -1;
                for (int s = 0; s < rec.OrbitSegments.Count; s++)
                {
                    var seg = rec.OrbitSegments[s];
                    if (seg.endUT <= seg.startUT) continue;
                    if (!string.Equals(seg.bodyName, p.bodyName, StringComparison.Ordinal)) continue;
                    if (seg.startUT >= q.ut || seg.endUT <= p.ut) continue; // no overlap with the gap
                    if (!IsSegmentExcludedFromOrbitOwnership(seg, surface)) continue;
                    segIdx = s;
                    break;
                }
                if (segIdx < 0) continue;

                var src = rec.OrbitSegments[segIdx];
                // Sample only where the conic actually covers the gap (the checkpoint conic may start
                // a few seconds after the cluster's last frame).
                double from = System.Math.Max(p.ut, src.startUT);
                double to = System.Math.Min(q.ut, src.endUT);
                if (to - from <= GapFillMinSeconds) continue;
                int n = (int)System.Math.Min(GapFillMaxPointsPerGap, System.Math.Floor((to - from) / 7.0));
                if (n < 1) continue;
                for (int k = 1; k <= n; k++)
                {
                    double ut = from + (to - from) * k / (n + 1);
                    if (!sampler(src, ut, out double lat, out double lon, out double alt)) break;
                    pts.Add(new TrajectoryPoint
                    {
                        ut = ut,
                        latitude = lat,
                        longitude = lon,
                        altitude = alt,
                        bodyName = p.bodyName,
                    });
                    inserted++;
                }
            }
            return inserted;
        }

        /// <summary>
        /// PURE predicted-TAIL fill (2026-09-08). <see cref="FillFramelessGapsFromConics"/> is
        /// strictly INTERIOR - it walks consecutive recorded pairs, so a conic that starts after
        /// the LAST recorded sample has no bracketing point and is never sampled. A scene-exit
        /// continuation tail is exactly that shape (predicted conics, zero points), so before this
        /// pass it was neither arc (dropped by the subsurface-periapsis predicate) nor leg (no gap
        /// to fill) and drew nothing at all.
        ///
        /// Samples every PREDICTED segment that (a) ends after the last recorded sample and
        /// (b) is NOT orbit-owned (<see cref="IsSegmentExcludedFromOrbitOwnership"/> true, i.e. the
        /// ballistic descent that reaches the ground; an orbit-owned coast is drawn as a forward
        /// arc and must not also be chorded here). Endpoints are INCLUSIVE so the run has at least
        /// two points and starts exactly at the conic's own start. RENDER-time only - the
        /// recording's lists are never touched; points are appended to the build stream copy.
        /// Returns the number of points inserted (the caller re-sorts when &gt; 0). No-op without a
        /// sampler or a surface provider, so the xUnit default and any unknown body are unchanged.
        /// </summary>
        internal static int FillPredictedTailFromConics(
            List<TrajectoryPoint> pts, Recording rec, BodySurfaceProvider surface,
            ConicGapSampler sampler, out int tailSegments)
        {
            tailSegments = 0;
            if (pts == null || rec == null || rec.OrbitSegments == null
                || sampler == null || surface == null)
                return 0;

            // The last recorded sample. An empty recorded stream is legal (a tail-only recording),
            // in which case every predicted segment is a tail.
            double lastRecordedUT = double.NegativeInfinity;
            for (int i = 0; i < pts.Count; i++)
                if (pts[i].ut > lastRecordedUT) lastRecordedUT = pts[i].ut;

            int inserted = 0;
            for (int s = 0; s < rec.OrbitSegments.Count; s++)
            {
                var seg = rec.OrbitSegments[s];
                if (!seg.isPredicted) continue;
                if (seg.endUT <= seg.startUT) continue;
                if (string.IsNullOrEmpty(seg.bodyName)) continue;
                if (seg.endUT <= lastRecordedUT) continue;
                // An orbit-owned predicted conic (the clipped coast) is drawn as an arc.
                if (!IsSegmentExcludedFromOrbitOwnership(seg, surface)) continue;

                double from = System.Math.Max(seg.startUT, lastRecordedUT);
                double to = seg.endUT;
                if (to - from < PredictedTailFillMinSeconds) continue;

                int interior = (int)System.Math.Min(
                    PredictedTailFillMaxPointsPerSegment - 2,
                    System.Math.Floor((to - from) / 7.0));
                if (interior < 0) interior = 0;
                int total = interior + 2; // inclusive endpoints
                int before = inserted;
                for (int k = 0; k < total; k++)
                {
                    double ut = total == 1 ? from : from + (to - from) * k / (total - 1);
                    if (!sampler(seg, ut, out double lat, out double lon, out double alt)) break;
                    pts.Add(new TrajectoryPoint
                    {
                        ut = ut,
                        latitude = lat,
                        longitude = lon,
                        altitude = alt,
                        bodyName = seg.bodyName,
                    });
                    inserted++;
                }
                if (inserted > before) tailSegments++;
            }
            return inserted;
        }

        internal static List<LegPolyline> BuildLegsForRecording(
            Recording rec, BodySurfaceProvider surface = null, ConicGapSampler gapSampler = null)
        {
            var legs = new List<LegPolyline>();
            if (rec == null) return legs;

            var orbitalIntervals = ComputeOrbitalCoverIntervals(rec.OrbitSegments, surface);

            // FIX #27: count + report the degenerate below-SURFACE segments
            // the cover now excludes (one-shot, build-time). When any are
            // excluded the descent samples they used to drop merge into the
            // descent leg, so log the resulting leg span window for diagnosis.
            int excludedBelowSurfaceSegments = 0;
            if (surface != null && rec.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                {
                    var seg = rec.OrbitSegments[i];
                    if (seg.endUT <= seg.startUT) continue;
                    if (IsSegmentExcludedFromOrbitOwnership(seg, surface))
                        excludedBelowSurfaceSegments++;
                }
            }

            int skippedRelativeWithoutBodyFixed = 0;
            int sectionPointCount = 0;
            int flatPointCount = 0;

            // (1) Collect every non-orbital sample into one stream. Per-section
            //     dispatch + the orbital-interval filter are unchanged; the merge
            //     into one leg per contiguous span happens in step (2).
            var pts = new List<TrajectoryPoint>();
            if (rec.TrackSections != null)
            {
                for (int s = 0; s < rec.TrackSections.Count; s++)
                {
                    var section = rec.TrackSections[s];
                    List<TrajectoryPoint> source = ResolveSourceListForSection(section);
                    if (source == null)
                    {
                        if (section.referenceFrame == ReferenceFrame.Relative)
                            skippedRelativeWithoutBodyFixed++;
                        continue;
                    }
                    var filtered = FilterPointsForLeg(source, section.startUT, section.endUT, orbitalIntervals);
                    pts.AddRange(filtered);
                    sectionPointCount += filtered.Count;
                }
            }

            // Flat Recording.Points OUTSIDE every section range (and outside any
            // orbital interval) fold into the same stream (pre/post-section
            // fallback coverage).
            if (rec.Points != null && rec.Points.Count > 0)
            {
                for (int i = 0; i < rec.Points.Count; i++)
                {
                    var p = rec.Points[i];
                    if (IsInsideAnySection(p.ut, rec.TrackSections)) continue;
                    if (IsInsideAnyOrbitalInterval(p.ut, orbitalIntervals)) continue;
                    pts.Add(p);
                    flatPointCount++;
                }
            }

            // (2) UT-sort and split into legs. A new leg starts on a body change
            //     or when an OrbitSegment coast lies between two consecutive
            //     non-orbital samples (the orbit arc owns that span). Otherwise
            //     contiguous same-body samples MERGE into one leg, so the head-UT
            //     draw gate shows a whole non-orbital span (the full burn arc)
            //     instead of a single fragmented section.
            pts.Sort((a, b) => a.ut.CompareTo(b.ut));

            // Playtest-12 straight-chord fix: fill frameless below-surface-conic gaps with the
            // recorded conic's own shape so the landing descent renders curved instead of a long
            // linear chord. Build-time only; the recording is never touched.
            int gapFilled = FillFramelessGapsFromConics(pts, rec, surface, gapSampler);

            // PREDICTED-TAIL fill: the scene-exit continuation conics that reach the ground carry
            // no recorded samples and sit AFTER the last one, so the interior filler above cannot
            // see them. Sampling them here is what makes the tail a leg the Driver's retained
            // forward pass can draw. Batch-counted, reported in the one build summary below.
            int tailFilled = FillPredictedTailFromConics(
                pts, rec, surface, gapSampler, out int tailSegments);
            if (gapFilled > 0 || tailFilled > 0)
                pts.Sort((a, b) => a.ut.CompareTo(b.ut));

            var run = new List<TrajectoryPoint>();
            string runBody = null;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (run.Count > 0)
                {
                    var prev = run[run.Count - 1];
                    bool sameBody = string.Equals(p.bodyName, runBody, StringComparison.Ordinal);
                    // Dedupe a sample shared at a section boundary ONLY within the same
                    // body, so an SOI crossing recorded at a single UT still starts a new
                    // leg below instead of being silently dropped.
                    if (sameBody && p.ut == prev.ut) continue;
                    bool breakRun =
                        !sameBody || OrbitalIntervalBetween(prev.ut, p.ut, orbitalIntervals);
                    if (breakRun)
                    {
                        FlushPolylineRun(run, runBody, legs);
                        run.Clear();
                    }
                }
                if (run.Count == 0) runBody = p.bodyName;
                run.Add(p);
            }
            FlushPolylineRun(run, runBody, legs);

            BuildInvocationCountForTesting++;
            // Per-recording rate limit: a descent rebuilds the polyline (cache
            // miss) every frame, so an unthrottled Verbose here bursts at frame
            // rate. verboseLogging ships ON, so this reaches a default install.
            ParsekLog.VerboseRateLimited(Tag,
                "polyline-build:" + rec.RecordingId,
                () => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Polyline build: rec={0} legs={1} (sectionPts={2} flatPts={3} skippedRelNoBodyFixed={4} excludedBelowSurfaceSegs={5} gapFilled={6} predictedTailPts={7} predictedTailSegs={8})",
                    rec.RecordingId,
                    legs.Count, sectionPointCount, flatPointCount,
                    skippedRelativeWithoutBodyFixed, excludedBelowSurfaceSegments, gapFilled,
                    tailFilled, tailSegments));

            // FIX #27 one-shot: when the cover excluded degenerate
            // below-surface segments, the descent samples they used to drop
            // now merge into a leg. Report that leg's span (the last leg, which
            // is the descent tail) so a coverage hole is diagnosable from the log.
            if (excludedBelowSurfaceSegments > 0 && legs.Count > 0)
            {
                var descentLeg = legs[legs.Count - 1];
                ParsekLog.VerboseRateLimited(Tag,
                    "polyline-excluded-belowsurf:" + rec.RecordingId,
                    () => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "excluded {0} below-surface orbit segments from cover rec={1} -> descent leg [{2:F1},{3:F1}]",
                        excludedBelowSurfaceSegments, rec.RecordingId,
                        descentLeg.startUT, descentLeg.endUT));
            }

            return legs;
        }

        /// <summary>
        /// Appends a merged non-orbital run to <paramref name="legs"/> as one leg
        /// (downsampled, endpoints preserved). Runs shorter than two points or
        /// with no resolvable body are dropped.
        /// </summary>
        private static void FlushPolylineRun(
            List<TrajectoryPoint> run, string body, List<LegPolyline> legs)
        {
            if (run == null || run.Count < 2) return;
            if (string.IsNullOrEmpty(body)) return;
            legs.Add(BuildLegFromBodyFixedPoints(run, body));
        }

        /// <summary>
        /// True when an OrbitSegment coast interval overlaps the OPEN span between
        /// two consecutive non-orbital samples, i.e. an orbit arc owns the gap so
        /// the polyline must break into a new leg there instead of drawing a chord
        /// across the orbit. Touching exactly at an interval endpoint (a burn leg
        /// meeting the orbit-arc boundary) does not count. Pure / xUnit-testable.
        /// </summary>
        internal static bool OrbitalIntervalBetween(
            double a, double b, List<(double startUT, double endUT)> intervals)
        {
            if (intervals == null) return false;
            double lo = a < b ? a : b;
            double hi = a < b ? b : a;
            for (int i = 0; i < intervals.Count; i++)
            {
                if (intervals[i].endUT > lo && intervals[i].startUT < hi)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the per-section source list per §1.3 policy. Null for
        /// Relative sections without bodyFixedFrames coverage and for
        /// OrbitalCheckpoint sections.
        /// </summary>
        internal static List<TrajectoryPoint> ResolveSourceListForSection(TrackSection section)
        {
            if (section.referenceFrame == ReferenceFrame.Absolute)
                return section.frames;
            if (section.referenceFrame == ReferenceFrame.Relative)
                return section.bodyFixedFrames; // null when no body-fixed coverage
            // OrbitalCheckpoint sections have no per-frame trajectory points; the
            // orbit-arc renderer covers those UTs.
            return null;
        }

        /// <summary>
        /// Builds a single leg from a body-coherent set of body-fixed
        /// TrajectoryPoints. Downsamples to <see cref="MaxPolylinePointsPerLeg"/>
        /// keeping the first + last sample and uniform-striding the
        /// remaining (cap - 2) interior samples (§1.3 endpoint preservation).
        ///
        /// Stores ONLY the recorded body-fixed (lat, lon, alt) triples; no
        /// geometry conversion happens here so the builder stays pure +
        /// xUnit-testable. The Driver converts each triple to a live world
        /// position per frame via <c>CelestialBody.GetWorldSurfacePosition</c>.
        /// </summary>
        internal static LegPolyline BuildLegFromBodyFixedPoints(
            List<TrajectoryPoint> points, string bodyName)
        {
            var sampled = DownsamplePreservingEndpoints(points, MaxPolylinePointsPerLeg);
            int m = sampled.Count;
            var lats = new double[m];
            var lons = new double[m];
            var alts = new double[m];
            var uts = new double[m];
            for (int i = 0; i < m; i++)
            {
                var p = sampled[i];
                lats[i] = p.latitude;
                lons[i] = p.longitude;
                alts[i] = p.altitude;
                uts[i] = p.ut;
            }
            return new LegPolyline
            {
                lats = lats,
                lons = lons,
                alts = alts,
                recordedUTs = uts,
                scratchScaledSpace = new Vector3[m],
                bodyName = bodyName,
                startUT = sampled[0].ut,
                endUT = sampled[m - 1].ut
            };
        }

        /// <summary>
        /// Downsamples a list of points keeping the first + last sample and
        /// uniform-striding the remaining (cap - 2) interior samples. Pure;
        /// reused by the unit tests directly.
        /// </summary>
        internal static List<TrajectoryPoint> DownsamplePreservingEndpoints(
            List<TrajectoryPoint> points, int cap)
        {
            if (points == null || points.Count == 0) return new List<TrajectoryPoint>();
            if (cap < 2) cap = 2;
            int n = points.Count;
            if (n <= cap)
            {
                return new List<TrajectoryPoint>(points);
            }

            var result = new List<TrajectoryPoint>(cap);
            result.Add(points[0]);
            int interiorCap = cap - 2;
            int interiorPool = n - 2;
            for (int i = 0; i < interiorCap; i++)
            {
                // Stride to pick a representative from the interior. Adds 1
                // to skip the first sample, +0.5 to centre the step bucket.
                int srcIdx = 1 + (int)((i + 0.5) * interiorPool / (double)interiorCap);
                if (srcIdx < 1) srcIdx = 1;
                if (srcIdx > n - 2) srcIdx = n - 2;
                result.Add(points[srcIdx]);
            }
            result.Add(points[n - 1]);
            return result;
        }

        /// <summary>
        /// Pure (FIX #27): is this OrbitSegment DEGENERATE, i.e. does its drawn
        /// Keplerian conic plunge below the body SURFACE so the orbit line cannot
        /// usably trace it? True when the segment's periapsis radius is below the
        /// body radius (periapsis altitude &lt; 0).
        ///
        /// Periapsis radius is <c>sma * (1 - ecc)</c>, which is valid for the
        /// hyperbolic case too (sma &lt; 0, ecc &gt; 1 give a positive periapsis
        /// radius). The boundary is the SURFACE, not the atmosphere top
        /// (CHANGE 2): the orbit line only becomes UNUSABLE when the conic is
        /// degenerate (dives under the ground). A valid conic that merely grazes
        /// the atmosphere at periapsis but stays above the surface (periapsis
        /// altitude in [0, atmosphereDepth]) is still drawn correctly by the orbit
        /// line, so the polyline must NOT claim it - excluding such an in-space
        /// eccentric orbit (e.g. a Kerbin parking orbit with periapsis a few km
        /// above the ground and a high apoapsis) would double-draw it. For the
        /// Duna arrival the final descent segments have periapsis well BELOW the
        /// surface (~-17 km), so they are still excluded and the descent hole is
        /// still fixed.
        ///
        /// Returns false (segment kept as orbit-owned) when the provider is null
        /// or the body is unknown, so a normal in-space orbit segment is
        /// UNCHANGED. Gated strictly on periapsis below the surface, so an
        /// ordinary parking / transfer / grazing orbit is never excluded.
        /// </summary>
        internal static bool IsOrbitSegmentBelowSurface(
            OrbitSegment segment, BodySurfaceProvider surface)
        {
            if (surface == null) return false;
            if (string.IsNullOrEmpty(segment.bodyName)) return false;
            if (!surface(segment.bodyName, out BodySurfaceInfo info)) return false;
            double periapsisRadius = segment.semiMajorAxis * (1.0 - segment.eccentricity);
            return periapsisRadius < info.radius;
        }

        /// <summary>
        /// Pure (PREDICTED-TAIL, 2026-09-08): is this a PREDICTED continuation conic that the
        /// orbit line should own DESPITE a subsurface periapsis? True only when the segment is
        /// <c>isPredicted</c>, <see cref="IsOrbitSegmentBelowSurface"/> would drop it, and its
        /// own <c>[startUT, endUT]</c> span never reaches the body surface
        /// (<see cref="TrajectoryMath.IsConicSpanAboveSurface"/>) - i.e. the scene-exit
        /// finalizer's COAST, clipped at atmosphere entry, whose conic only dives underground
        /// far past its own end.
        ///
        /// Why the <c>isPredicted</c> gate is the right axis and not a hidden flag dependency:
        /// FIX #27 excludes a below-surface conic so the RECORDED descent samples underneath it
        /// draw as a leg. A predicted tail carries ZERO points, so there is nothing to fall back
        /// on and the exclusion draws nothing at all. The distinguishing property is "no recorded
        /// coverage exists for this span", and on the producer side that is exactly the predicted
        /// suffix. A NON-predicted subsurface conic is therefore untouched, which keeps the Duna
        /// descent hole fixed.
        ///
        /// Fails CLOSED (returns false, so the segment stays leg-owned) when the surface provider
        /// is missing, the body is unknown, or its gravitational parameter is unset - a provider
        /// that fills only <c>radius</c> keeps the pre-fix behaviour byte-for-byte.
        /// </summary>
        internal static bool IsPredictedTailOrbitOwnedConic(
            OrbitSegment segment, BodySurfaceProvider surface)
        {
            if (!segment.isPredicted) return false;
            if (surface == null) return false;
            if (string.IsNullOrEmpty(segment.bodyName)) return false;
            if (!surface(segment.bodyName, out BodySurfaceInfo info)) return false;
            return TrajectoryMath.IsConicSpanAboveSurface(
                segment, info.gravParameter, info.radius);
        }

        /// <summary>
        /// Pure: the ONE predicate every orbit-ownership consumer asks - is this segment kept OUT
        /// of orbit-owned rendering (cover interval, forward arc, seam-bridge target)? That is
        /// <see cref="IsOrbitSegmentBelowSurface"/> minus the predicted-tail re-admission of
        /// <see cref="IsPredictedTailOrbitOwnedConic"/>. Callers must use this rather than
        /// calling <c>IsOrbitSegmentBelowSurface</c> directly, so the cover, the arc selector and
        /// the bridge search cannot drift apart (a segment that is arc-drawn but absent from the
        /// cover would be double-drawn as a chord).
        /// </summary>
        internal static bool IsSegmentExcludedFromOrbitOwnership(
            OrbitSegment segment, BodySurfaceProvider surface)
            => IsOrbitSegmentBelowSurface(segment, surface)
                && !IsPredictedTailOrbitOwnedConic(segment, surface);

        /// <summary>
        /// Computes the union of every OrbitSegment's [startUT, endUT]
        /// interval. Points whose UT falls inside the union are dropped
        /// from the polyline at filter time (the orbit-arc covers them).
        ///
        /// FIX #27: a degenerate below-SURFACE segment (see
        /// <see cref="IsOrbitSegmentBelowSurface"/>) is EXCLUDED from the
        /// cover so the polyline picks up the descent samples the unusable
        /// orbit line abandons there, tiling the two surfaces without a gap. When
        /// <paramref name="surface"/> is null (the xUnit builder default) no
        /// segment is excluded, so a recording with no degenerate segments is
        /// byte-identical to the pre-fix behaviour.
        /// </summary>
        internal static List<(double startUT, double endUT)> ComputeOrbitalCoverIntervals(
            List<OrbitSegment> segments, BodySurfaceProvider surface = null)
        {
            var intervals = new List<(double, double)>();
            if (segments == null || segments.Count == 0) return intervals;
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (s.endUT <= s.startUT) continue;
                if (IsSegmentExcludedFromOrbitOwnership(s, surface)) continue;
                intervals.Add((s.startUT, s.endUT));
            }
            return intervals;
        }

        internal static bool IsInsideAnyOrbitalInterval(
            double ut, List<(double startUT, double endUT)> intervals)
        {
            if (intervals == null) return false;
            for (int i = 0; i < intervals.Count; i++)
            {
                var iv = intervals[i];
                if (ut >= iv.startUT && ut <= iv.endUT) return true;
            }
            return false;
        }

        internal static bool IsInsideAnySection(double ut, List<TrackSection> sections)
        {
            if (sections == null) return false;
            for (int i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                if (ut >= s.startUT && ut <= s.endUT) return true;
            }
            return false;
        }

        /// <summary>
        /// Filters a per-section sample list to the section's UT range AND
        /// drops samples covered by an orbital interval (the orbit-arc
        /// already draws them).
        /// </summary>
        internal static List<TrajectoryPoint> FilterPointsForLeg(
            List<TrajectoryPoint> source, double sectionStartUT, double sectionEndUT,
            List<(double startUT, double endUT)> orbitalIntervals)
        {
            var result = new List<TrajectoryPoint>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                var p = source[i];
                if (p.ut < sectionStartUT || p.ut > sectionEndUT) continue;
                if (IsInsideAnyOrbitalInterval(p.ut, orbitalIntervals)) continue;
                result.Add(p);
            }
            return result;
        }
    }
}
