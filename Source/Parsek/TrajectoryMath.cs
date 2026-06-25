using System;
using System.Collections.Generic;
using Parsek.Rendering;
using UnityEngine;

namespace Parsek
{
    internal struct RecordingStats
    {
        public double maxAltitude;
        public double maxSpeed;
        public double distanceTravelled;
        public int pointCount;
        public int orbitSegmentCount;
        public int partEventCount;
        public string primaryBody;
        public double maxRange;
    }

    /// <summary>
    /// Pure static math functions for trajectory recording and playback.
    /// </summary>
    public static partial class TrajectoryMath
    {
        internal const double MinUsableOrbitSemiMajorAxisMeters = 1.0;

        /// <summary>
        /// Decides whether to record a trajectory point based on velocity changes,
        /// a min-interval floor, and a max-interval backstop. Pure function for testability.
        ///
        /// Gate order:
        ///   1. First point (lastRecordedUT &lt; 0) — always record
        ///   2. Max-interval backstop — always record after this long (overrides floor for
        ///      degenerate configs where minInterval &gt; maxInterval)
        ///   3. Min-interval floor — never record inside this window, regardless of velocity gates
        ///   4. Velocity direction / speed gates — opportunistic
        ///
        /// The min-interval floor caps worst-case sample rate during slow/jittery motion
        /// (EVA on surface, slow rovers, hovering aircraft) where the velocity gates can
        /// otherwise fire on every physics frame. Set minInterval = 0 to disable the floor.
        /// </summary>
        internal static bool ShouldRecordPoint(
            Vector3 currentVelocity, Vector3 lastVelocity,
            double currentUT, double lastRecordedUT,
            float minInterval, float maxInterval,
            float velDirThreshold, float speedThreshold)
        {
            // Always record the first point
            if (lastRecordedUT < 0)
                return true;

            double elapsed = currentUT - lastRecordedUT;

            // Max interval backstop — always record after this long.
            // Checked BEFORE the min floor so a degenerate config (minInterval > maxInterval)
            // still produces samples instead of starving the recording.
            if (elapsed >= maxInterval)
                return true;

            // Min interval floor — never record inside this window
            if (elapsed < minInterval)
                return false;

            float currentSpeed = currentVelocity.magnitude;
            float lastSpeed = lastVelocity.magnitude;

            // Velocity direction change (guard against zero vectors to avoid NaN)
            if (currentSpeed > 0.1f && lastSpeed > 0.1f)
            {
                float angle = Vector3.Angle(currentVelocity, lastVelocity);
                if (angle > velDirThreshold)
                    return true;
            }

            // Speed change (relative to last speed, with floor to avoid div-by-near-zero)
            float speedDelta = Mathf.Abs(currentSpeed - lastSpeed);
            float reference = Mathf.Max(lastSpeed, 0.1f);
            if (speedDelta / reference > speedThreshold)
                return true;

            return false;
        }

        /// <summary>
        /// Returns the index of the first point where the vessel has meaningfully moved
        /// from its initial position (altitude changed by >= altThreshold meters, or speed
        /// exceeds speedThreshold m/s). Returns 0 if the vessel is moving from the start
        /// or the list is too short.
        /// </summary>
        internal static int FindFirstMovingPoint(List<TrajectoryPoint> points,
            double altThreshold = 1.0, float speedThreshold = 5.0f)
        {
            if (points == null || points.Count < 2) return 0;
            double startAlt = points[0].altitude;
            for (int i = 0; i < points.Count; i++)
            {
                if (System.Math.Abs(points[i].altitude - startAlt) >= altThreshold)
                {
                    ParsekLog.Verbose("TrajectoryMath",
                        $"FindFirstMovingPoint: altitude trigger at index {i} " +
                        $"(alt={points[i].altitude:F1}, startAlt={startAlt:F1}, delta={System.Math.Abs(points[i].altitude - startAlt):F1})");
                    return i;
                }
                if (points[i].velocity.magnitude >= speedThreshold)
                {
                    ParsekLog.Verbose("TrajectoryMath",
                        $"FindFirstMovingPoint: speed trigger at index {i} " +
                        $"(speed={points[i].velocity.magnitude:F1}m/s, threshold={speedThreshold:F1})");
                    return i;
                }
            }
            // Vessel never moved significantly — keep all points
            ParsekLog.Verbose("TrajectoryMath",
                $"FindFirstMovingPoint: vessel never moved significantly across {points.Count} points, keeping all");
            return 0;
        }

        /// <summary>
        /// Find an orbit segment that covers the given UT. Returns null if none match.
        /// Linear scan — the list is tiny (typically 0-3 segments per recording).
        /// </summary>
        internal static OrbitSegment? FindOrbitSegment(List<OrbitSegment> segments, double ut)
        {
            if (segments == null) return null;
            for (int i = 0; i < segments.Count; i++)
            {
                bool inRange = (i == segments.Count - 1)
                    ? (ut >= segments[i].startUT && ut <= segments[i].endUT)
                    : (ut >= segments[i].startUT && ut < segments[i].endUT);
                if (inRange)
                    return segments[i];
            }
            return null;
        }

        /// <summary>
        /// Returns true when a stored Kepler segment has enough finite data to construct
        /// and propagate a KSP Orbit. Suborbital ellipses can have semi-major axes below
        /// the body's radius, so validity is intentionally independent of body radius.
        /// </summary>
        internal static bool HasUsableOrbitSegmentElements(OrbitSegment segment)
        {
            return IsFinite(segment.inclination)
                && IsFinite(segment.eccentricity)
                && IsFinite(segment.semiMajorAxis)
                && System.Math.Abs(segment.semiMajorAxis) >= MinUsableOrbitSemiMajorAxisMeters
                && IsFinite(segment.longitudeOfAscendingNode)
                && IsFinite(segment.argumentOfPeriapsis)
                && IsFinite(segment.meanAnomalyAtEpoch)
                && IsFinite(segment.epoch);
        }

        /// <summary>
        /// Phase 6 §7.5 / §7.7 shared helper. Evaluates a body-relative
        /// world position from an OrbitSegment list at the supplied UT.
        /// When <paramref name="ut"/> falls within a segment, propagates
        /// that segment's Kepler. When <paramref name="ut"/> is past the
        /// last segment's endUT (or before the first segment's startUT),
        /// falls back to the nearest endpoint segment so a partial last
        /// or first checkpoint doesn't silently produce a null result —
        /// this is the §7.7 BubbleEntry case where the candidate UT
        /// equals the Checkpoint section's endUT but the last sampled
        /// checkpoint's endUT is a hair below that, AND the §7.5 case
        /// where the boundary UT sits at the Checkpoint section's start
        /// or end UT but the first/last sampled checkpoint covers a
        /// slightly narrower range.
        ///
        /// <para>
        /// Returns <c>null</c> on any of: null/empty checkpoint list,
        /// null body resolver, body resolver returning null for the
        /// segment's bodyName, <c>Orbit.getPositionAtUT</c> throwing,
        /// or NaN/Inf result. Callers treat null as a fail-closed
        /// signal (HR-9 visible failure on the call site).
        /// </para>
        ///
        /// <para>
        /// Body resolution goes through the supplied delegate so
        /// xUnit can inject a fake <see cref="CelestialBody"/> via
        /// <see cref="Parsek.Rendering.AnchorPropagator.BodyResolverForTesting"/>
        /// or the equivalent test seam, while production passes a
        /// <c>FlightGlobals.Bodies</c> lookup.
        /// </para>
        /// </summary>
        internal static Vector3d? EvaluateOrbitSegmentAtUT(
            List<OrbitSegment> checkpoints,
            double ut,
            Func<string, CelestialBody> bodyResolver)
        {
            if (checkpoints == null || checkpoints.Count == 0) return null;
            if (bodyResolver == null) return null;

            OrbitSegment? maybeSeg = FindOrbitSegment(checkpoints, ut);
            if (!maybeSeg.HasValue)
            {
                // Endpoint fallback: pick the segment on the side of the
                // checkpoint range that the UT lies past. FindOrbitSegment
                // returns null only when ut < checkpoints[0].startUT OR
                // ut > checkpoints[Count-1].endUT, so the boundary check
                // disambiguates which endpoint to use.
                if (ut <= checkpoints[0].startUT)
                    maybeSeg = checkpoints[0];
                else
                    maybeSeg = checkpoints[checkpoints.Count - 1];
            }
            OrbitSegment seg = maybeSeg.Value;
            if (!HasUsableOrbitSegmentElements(seg))
                return null;

            CelestialBody body = bodyResolver(seg.bodyName);
            if (object.ReferenceEquals(body, null)) return null;

            try
            {
                Orbit orbit = new Orbit(
                    seg.inclination, seg.eccentricity, seg.semiMajorAxis,
                    seg.longitudeOfAscendingNode, seg.argumentOfPeriapsis,
                    seg.meanAnomalyAtEpoch, seg.epoch, body);
                Vector3d pos = orbit.getPositionAtUT(ut);
                if (double.IsNaN(pos.x) || double.IsNaN(pos.y) || double.IsNaN(pos.z))
                    return null;
                return pos;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Pure: returns true when two orbit segments represent the same underlying orbit
        /// for map-display continuity purposes. Epoch and mean anomaly are intentionally
        /// ignored because the same orbit can be serialized at different times.
        /// </summary>
        internal static bool AreOrbitSegmentsEquivalentForMapDisplay(OrbitSegment a, OrbitSegment b)
        {
            if (string.IsNullOrEmpty(a.bodyName)
                || !string.Equals(a.bodyName, b.bodyName, System.StringComparison.Ordinal))
                return false;

            double smaTolerance = System.Math.Max(10.0, System.Math.Abs(a.semiMajorAxis) * 1e-6);
            const double EccTolerance = 1e-5;
            const double AngleToleranceDeg = 0.01;

            return System.Math.Abs(a.semiMajorAxis - b.semiMajorAxis) <= smaTolerance
                && System.Math.Abs(a.eccentricity - b.eccentricity) <= EccTolerance
                && AngularDeltaDegrees(a.inclination, b.inclination) <= AngleToleranceDeg
                && AngularDeltaDegrees(a.longitudeOfAscendingNode, b.longitudeOfAscendingNode) <= AngleToleranceDeg
                && AngularDeltaDegrees(a.argumentOfPeriapsis, b.argumentOfPeriapsis) <= AngleToleranceDeg;
        }

        /// <summary>
        /// Coalesces consecutive same-orbit OrbitSegments (LOOP-ONLY, IN-MEMORY) into one continuous
        /// segment. The recorder splits a single coast into several fragments with the SAME Kepler
        /// elements (it closes + reopens a segment at every recording-mode transition -
        /// background/foreground vessel switch, scene change - with no coalescing). On the faithful first
        /// play those fragments replay at exact UT over real time and are invisible; but under a LOOP the
        /// compressed span clock can sweep the playback head across a short fragment (e.g. a ~40s parking
        /// tail right before an escape burn) and out of its bounds in under one render frame, opening a
        /// gap where the map icon flashes at the wrong phase (the s15 "Kerbal X" decouple seam). Merging
        /// the fragments into one segment means the head is always inside one continuous segment across
        /// the whole coast, so there is no mid-frame seam to flash on.
        ///
        /// <para>Both map renderers must call this on the list they consume - they do NOT share one
        /// upstream point: the legacy path coalesces inside <c>ReaimSegmentAssembler.ReplaceHeliocentricLeg</c>
        /// (its in-memory re-aim list), and the new MapRender pipeline coalesces inside
        /// <c>ChainAssembler.Build</c> (the raw recorded <c>traj.OrbitSegments</c>). Pure; assumes
        /// <paramref name="segs"/> is sorted by startUT (recorded segments are appended in time order;
        /// re-aim sorts before calling). Never mutates the recorded data - it returns a NEW list.</para>
        ///
        /// <para>Merges adjacent segments while <see cref="AreOrbitSegmentsEquivalentForMapDisplay"/>
        /// holds AND <c>isPredicted</c> matches, taking [first.startUT, last.endUT] and KEEPING the FIRST
        /// fragment's elements / epoch / frame (the predicate ignores epoch + mean anomaly; the first
        /// fragment's epoch correctly anchors the merged arc). A real maneuver (the escape burn changes
        /// sma by millions, ecc 0.0013 -> 1.19) is far outside the predicate tolerance, so boundaries that
        /// matter are preserved; the <c>isPredicted</c> guard keeps a predicted ballistic-tail segment
        /// from folding into a non-predicted coast. Element equivalence is the load-bearing test, NOT the
        /// gap duration - same-orbit fragments coalesce no matter how large the sampling gap.</para>
        /// <para>RETURN CONTRACT: the result is READ-ONLY - callers MUST NOT mutate it. It MAY be the input
        /// list returned BY REFERENCE (count &lt; 2, or the no-adjacent-pair-merges hot-path fast exit), and
        /// that input can itself alias recorded <c>Recording.OrbitSegments</c>; a fresh list is allocated
        /// only when a merge actually occurs. Mutating the result would corrupt recorded data. All current
        /// callers (ChainAssembler, the forward-render pass, ReaimSegmentAssembler) only read it.</para>
        /// </summary>
        internal static System.Collections.Generic.List<OrbitSegment> CoalesceSameOrbitFragments(
            System.Collections.Generic.List<OrbitSegment> segs)
        {
            if (segs == null || segs.Count < 2)
                return segs;

            // Hot-path allocation guard (forward-render review finding): this runs once per leg-bearing
            // committed recording per frame on the multi-ghost map path. A single cheap no-alloc scan first
            // checks whether ANY adjacent pair would actually merge; when none would (the common case - a
            // non-fragmented recording, e.g. a ghost on a full-loop parking orbit or a single recorded
            // coast), return the INPUT list by reference and skip the per-frame List<OrbitSegment>
            // allocation entirely. All callers only READ the returned list (ChainAssembler iterates it,
            // the forward pass passes it to ComputeForwardWindow / SelectForwardArcSegmentIndices, the
            // re-aim assembler returns it upward) and the recorded data is still never mutated, so the
            // by-reference return is behaviour-identical to the prior always-copy path.
            bool anyMerge = false;
            for (int i = 1; i < segs.Count; i++)
            {
                if (segs[i - 1].isPredicted == segs[i].isPredicted
                    && AreOrbitSegmentsEquivalentForMapDisplay(segs[i - 1], segs[i]))
                {
                    anyMerge = true;
                    break;
                }
            }
            if (!anyMerge)
                return segs;

            var merged = new System.Collections.Generic.List<OrbitSegment>(segs.Count);
            OrbitSegment current = segs[0];
            for (int i = 1; i < segs.Count; i++)
            {
                OrbitSegment next = segs[i];
                if (current.isPredicted == next.isPredicted
                    && AreOrbitSegmentsEquivalentForMapDisplay(current, next))
                {
                    // Same orbit, contiguous fragment: extend the running segment over the gap + the new
                    // data. Keep the first fragment's elements/epoch (anchors the merged arc).
                    if (next.endUT > current.endUT)
                        current.endUT = next.endUT;
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }
            merged.Add(current);
            return merged;
        }

        /// <summary>
        /// Shortest angular distance in degrees between two angles, accounting for
        /// the 0/360 wraparound. Inputs may be in any range (degrees); the result
        /// is always in [0, 180]. Use this instead of raw <c>Math.Abs(a - b)</c>
        /// for orbital LAN / argument-of-periapsis / true-anomaly comparisons,
        /// since stable orbits routinely cross the 0/360 boundary and a literal
        /// difference produces a false ~360 deg mismatch.
        /// Example: <c>AngularDeltaDegrees(359.997, 0.002) == 0.005</c>.
        /// Inclination (range [0, 180]) is also safe — within that range the
        /// wrap-correction branch is never taken, but the helper keeps math
        /// centralized for all angle deltas.
        /// </summary>
        internal static double AngularDeltaDegrees(double a, double b)
        {
            double delta = System.Math.Abs(a - b) % 360.0;
            return delta > 180.0 ? 360.0 - delta : delta;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsFinite(Vector3d value)
        {
            return !double.IsNaN(value.x) && !double.IsInfinity(value.x)
                && !double.IsNaN(value.y) && !double.IsInfinity(value.y)
                && !double.IsNaN(value.z) && !double.IsInfinity(value.z);
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        /// <summary>
        /// Velocity-aware world-position interpolation for sparse playback brackets.
        /// Uses cubic Hermite when endpoint velocities agree with the recorded
        /// chord and the curve stays near the legacy lerp. Returns false when
        /// the velocity data is missing, non-finite, frame-inconsistent, or
        /// would introduce a large bow/loop.
        /// </summary>
        internal static bool TryInterpolateWorldHermite(
            Vector3d posBefore,
            Vector3 velocityBefore,
            Vector3d posAfter,
            Vector3 velocityAfter,
            double deltaTimeSeconds,
            float t,
            double maxDeviationMeters,
            out Vector3d hermiteWorld,
            out double deviationMeters,
            out string reason)
        {
            hermiteWorld = Vector3d.zero;
            deviationMeters = double.NaN;
            reason = null;

            if (!IsFinite(posBefore) || !IsFinite(posAfter))
            {
                reason = "position-non-finite";
                return false;
            }
            if (!IsFinite(velocityBefore) || !IsFinite(velocityAfter))
            {
                reason = "velocity-non-finite";
                return false;
            }
            if (double.IsNaN(deltaTimeSeconds)
                || double.IsInfinity(deltaTimeSeconds)
                || deltaTimeSeconds <= 1e-6)
            {
                reason = "dt-invalid";
                return false;
            }
            if (float.IsNaN(t) || float.IsInfinity(t) || t < 0f || t > 1f)
            {
                reason = "t-invalid";
                return false;
            }
            if (double.IsNaN(maxDeviationMeters)
                || double.IsInfinity(maxDeviationMeters)
                || maxDeviationMeters <= 0.0)
            {
                reason = "max-deviation-invalid";
                return false;
            }

            double speedBeforeSq = velocityBefore.sqrMagnitude;
            double speedAfterSq = velocityAfter.sqrMagnitude;
            if (speedBeforeSq < 0.01 || speedAfterSq < 0.01)
            {
                reason = "velocity-missing";
                return false;
            }

            Vector3d v0 = new Vector3d(velocityBefore.x, velocityBefore.y, velocityBefore.z);
            Vector3d v1 = new Vector3d(velocityAfter.x, velocityAfter.y, velocityAfter.z);
            Vector3d chord = posAfter - posBefore;
            double chordMeters = chord.magnitude;
            Vector3d averageVelocityDisplacement = (v0 + v1) * (0.5 * deltaTimeSeconds);
            double velocityChordResidual = (averageVelocityDisplacement - chord).magnitude;
            double maxVelocityChordResidual = System.Math.Max(500.0, chordMeters * 0.75);
            if (velocityChordResidual > maxVelocityChordResidual)
            {
                reason = "velocity-chord-mismatch";
                return false;
            }

            double u = t;
            double u2 = u * u;
            double u3 = u2 * u;
            double h00 = 2.0 * u3 - 3.0 * u2 + 1.0;
            double h10 = u3 - 2.0 * u2 + u;
            double h01 = -2.0 * u3 + 3.0 * u2;
            double h11 = u3 - u2;
            Vector3d tangentBefore = v0 * deltaTimeSeconds;
            Vector3d tangentAfter = v1 * deltaTimeSeconds;

            hermiteWorld =
                posBefore * h00
                + tangentBefore * h10
                + posAfter * h01
                + tangentAfter * h11;
            if (!IsFinite(hermiteWorld))
            {
                reason = "hermite-non-finite";
                return false;
            }

            Vector3d lerpWorld = Vector3d.Lerp(posBefore, posAfter, t);
            deviationMeters = (hermiteWorld - lerpWorld).magnitude;
            if (double.IsNaN(deviationMeters) || double.IsInfinity(deviationMeters))
            {
                reason = "deviation-non-finite";
                return false;
            }
            if (deviationMeters > maxDeviationMeters)
            {
                reason = "deviation-too-large";
                return false;
            }

            reason = "applied";
            return true;
        }

        private static void ExpandEquivalentOrbitWindow(
            List<OrbitSegment> segments,
            int seedFirstIndex,
            int seedLastIndex,
            out double visibleStartUT,
            out double visibleEndUT,
            out int firstVisibleIndex,
            out int lastVisibleIndex)
        {
            firstVisibleIndex = seedFirstIndex;
            while (firstVisibleIndex > 0
                && AreOrbitSegmentsEquivalentForMapDisplay(
                    segments[firstVisibleIndex - 1], segments[firstVisibleIndex]))
            {
                firstVisibleIndex--;
            }

            lastVisibleIndex = seedLastIndex;
            while (lastVisibleIndex < segments.Count - 1
                && AreOrbitSegmentsEquivalentForMapDisplay(
                    segments[lastVisibleIndex], segments[lastVisibleIndex + 1]))
            {
                lastVisibleIndex++;
            }

            visibleStartUT = segments[firstVisibleIndex].startUT;
            visibleEndUT = segments[lastVisibleIndex].endUT;
        }

        /// <summary>
        /// Expand-on-read helper for the LOOP-SHIFTED / endpoint-tail map arc clip.
        ///
        /// <para>The loop-shifted ghost path stores a SINGLE currently-applied OrbitSegment's bounds
        /// (<c>ghostOrbitBounds[pid]</c>) and short-circuits the non-loop merge in
        /// <c>GhostMapPresence.TryGetVisibleOrbitBoundsForGhostVessel</c>, so the orbit-line arc gets
        /// clipped to one same-orbit fragment. When the recorder split a single coast into several
        /// adjacent fragments with IDENTICAL Kepler elements (it closes + reopens a segment at every
        /// recording-mode transition), the incoming SOI-approach hyperbola draws one piece at the
        /// crossing and the rest appears seconds later as the head advances into the next fragment.</para>
        ///
        /// <para>This pure helper locates the RAW recorded segment whose bounds match the stored RAW
        /// window (caller un-shifts the live-frame stored bounds first), then grows the window across
        /// element-equivalent adjacent neighbours using the SAME
        /// <see cref="AreOrbitSegmentsEquivalentForMapDisplay"/> predicate the non-loop merge uses, so a
        /// genuinely different conic (e.g. a captured ellipse after the hyperbola) stops the grow and is
        /// NOT folded in. Unlike <see cref="ExpandEquivalentOrbitWindow"/> the grow ALSO requires
        /// <c>isPredicted</c> to match (mirroring <see cref="CoalesceSameOrbitFragments"/>), so a
        /// predicted ballistic tail never folds into a non-predicted coast even when the elements match.</para>
        ///
        /// <para>READ-ONLY: it only reads <paramref name="segments"/> and returns time bounds; recorded
        /// data is never mutated. Returns <c>false</c> (caller keeps the stored single-segment bounds)
        /// when the list is null/empty or no segment matches the stored window. Returns <c>true</c> with
        /// <paramref name="fragmentCount"/> == 1 when the matched seed has no equivalent neighbour (a true
        /// single-segment ghost, no widening). The caller only widens when <paramref name="fragmentCount"/>
        /// &gt; 1.</para>
        /// </summary>
        internal static bool TryExpandStoredSingleSegmentWindow(
            List<OrbitSegment> segments,
            double storedStartUT,
            double storedEndUT,
            out double expandedStartUT,
            out double expandedEndUT,
            out int firstVisibleIndex,
            out int lastVisibleIndex,
            out int fragmentCount)
        {
            expandedStartUT = storedStartUT;
            expandedEndUT = storedEndUT;
            firstVisibleIndex = -1;
            lastVisibleIndex = -1;
            fragmentCount = 1;

            if (segments == null || segments.Count == 0)
                return false;

            // ghostOrbitBounds is written verbatim from one segment's startUT/endUT, so an exact
            // (within tolerance) startUT match identifies the seed; tie-break on the closer endUT
            // match to disambiguate a (recorder never emits) zero-length collision.
            const double MatchToleranceSeconds = 1e-3;
            int seedIndex = -1;
            double bestStartDelta = double.PositiveInfinity;
            double bestEndDelta = double.PositiveInfinity;
            for (int i = 0; i < segments.Count; i++)
            {
                double startDelta = System.Math.Abs(segments[i].startUT - storedStartUT);
                if (startDelta > MatchToleranceSeconds)
                    continue;

                double endDelta = System.Math.Abs(segments[i].endUT - storedEndUT);
                if (seedIndex < 0
                    || startDelta < bestStartDelta
                    || (startDelta <= bestStartDelta && endDelta < bestEndDelta))
                {
                    seedIndex = i;
                    bestStartDelta = startDelta;
                    bestEndDelta = endDelta;
                }
            }

            if (seedIndex < 0)
                return false;

            // Grow across element-equivalent AND isPredicted-matching neighbours. Inlined (rather than
            // calling ExpandEquivalentOrbitWindow, which ignores isPredicted) so the isPredicted guard is
            // explicit and self-contained per the no-predicted-straddle guardrail.
            firstVisibleIndex = seedIndex;
            while (firstVisibleIndex > 0
                && segments[firstVisibleIndex - 1].isPredicted == segments[firstVisibleIndex].isPredicted
                && AreOrbitSegmentsEquivalentForMapDisplay(
                    segments[firstVisibleIndex - 1], segments[firstVisibleIndex]))
            {
                firstVisibleIndex--;
            }

            lastVisibleIndex = seedIndex;
            while (lastVisibleIndex < segments.Count - 1
                && segments[lastVisibleIndex].isPredicted == segments[lastVisibleIndex + 1].isPredicted
                && AreOrbitSegmentsEquivalentForMapDisplay(
                    segments[lastVisibleIndex], segments[lastVisibleIndex + 1]))
            {
                lastVisibleIndex++;
            }

            expandedStartUT = segments[firstVisibleIndex].startUT;
            expandedEndUT = segments[lastVisibleIndex].endUT;
            fragmentCount = lastVisibleIndex - firstVisibleIndex + 1;
            return true;
        }

        /// <summary>
        /// Monotonic (never-shrink) combine of a single-segment arc-window EXPANSION with the window it
        /// expanded. <see cref="TryExpandStoredSingleSegmentWindow"/> is purely ADDITIVE by contract: it
        /// widens one applied fragment across its element-equivalent recorded neighbours so a same-orbit
        /// coast draws in one frame. But its seed match keys on <c>startUT</c> only, so when the stored
        /// window is a SYNTHESIZED (re-aimed) span whose start coincides with a recorded fragment but whose
        /// end lies BEYOND that fragment's element-equivalent run (the recorded coast was split mid-course
        /// by an element change &gt; the equivalence tolerance), the walk stops at the split and returns a
        /// window NARROWER than the stored span - truncating the drawn arc partway to the target. Union the
        /// expansion with the stored window so it can only ever WIDEN, never truncate.
        /// <paramref name="clampedToStored"/> reports that the stored window dominated on at least one side
        /// (the raw walk would have truncated) for diagnostics. Pure.
        /// </summary>
        internal static void UnionArcWindowWithStored(
            double storedStartUT, double storedEndUT,
            double expandedStartUT, double expandedEndUT,
            out double startUT, out double endUT, out bool clampedToStored)
        {
            startUT = System.Math.Min(storedStartUT, expandedStartUT);
            endUT = System.Math.Max(storedEndUT, expandedEndUT);
            clampedToStored = expandedStartUT > storedStartUT || expandedEndUT < storedEndUT;
        }

        /// <summary>
        /// Map-view policy helper: return the active orbit segment for the given UT plus
        /// the merged visible time bounds to use for map-line/icon continuity.
        /// Equivalent same-body segments are expanded into one continuous visible window,
        /// including brief gaps between them, so prerecorded same-SOI journeys render as
        /// one uninterrupted map line.
        /// </summary>
        internal static bool TryGetOrbitWindowForMapDisplay(
            List<OrbitSegment> segments,
            double ut,
            out OrbitSegment segment,
            out double visibleStartUT,
            out double visibleEndUT,
            out int firstVisibleIndex,
            out int lastVisibleIndex,
            out bool carriedAcrossGap)
        {
            segment = default(OrbitSegment);
            visibleStartUT = 0;
            visibleEndUT = 0;
            firstVisibleIndex = -1;
            lastVisibleIndex = -1;
            carriedAcrossGap = false;

            if (segments == null || segments.Count == 0)
                return false;

            for (int i = 0; i < segments.Count; i++)
            {
                bool inRange = (i == segments.Count - 1)
                    ? (ut >= segments[i].startUT && ut <= segments[i].endUT)
                    : (ut >= segments[i].startUT && ut < segments[i].endUT);
                if (!inRange)
                    continue;

                segment = segments[i];
                ExpandEquivalentOrbitWindow(
                    segments, i, i,
                    out visibleStartUT, out visibleEndUT,
                    out firstVisibleIndex, out lastVisibleIndex);
                return true;
            }

            if (segments.Count < 2)
                return false;

            int previousIndex = -1;
            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment candidate = segments[i];
                if (candidate.endUT <= ut)
                {
                    previousIndex = i;
                    continue;
                }

                if (previousIndex < 0 || candidate.startUT <= ut)
                    return false;

                if (!AreOrbitSegmentsEquivalentForMapDisplay(segments[previousIndex], candidate))
                    return false;

                segment = segments[previousIndex];
                carriedAcrossGap = true;
                ExpandEquivalentOrbitWindow(
                    segments, previousIndex, i,
                    out visibleStartUT, out visibleEndUT,
                    out firstVisibleIndex, out lastVisibleIndex);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Map-view policy helper: return the active orbit segment for the given UT plus
        /// the visible time bounds to use for map-line/icon continuity.
        /// </summary>
        internal static bool TryGetOrbitSegmentForMapDisplay(
            List<OrbitSegment> segments, double ut,
            out OrbitSegment segment, out double visibleStartUT, out double visibleEndUT)
        {
            return TryGetOrbitWindowForMapDisplay(
                segments, ut,
                out segment, out visibleStartUT, out visibleEndUT,
                out _, out _, out _);
        }

        /// <summary>
        /// Map-view policy helper: return the active orbit segment for the given UT, or
        /// carry the immediately preceding segment across a gap when the next segment stays
        /// in the same SOI/body. This avoids fragmenting ghost orbit lines across brief
        /// off-rails sections during a continuous same-body journey.
        /// </summary>
        internal static OrbitSegment? FindOrbitSegmentForMapDisplay(List<OrbitSegment> segments, double ut)
        {
            return TryGetOrbitSegmentForMapDisplay(segments, ut,
                out OrbitSegment segment, out _, out _)
                ? (OrbitSegment?)segment
                : null;
        }

        /// <summary>
        /// Map-view body-frame helper: like <see cref="FindOrbitSegmentForMapDisplay"/> but
        /// carries the previous segment across ANY same-body gap, not only orbit-equivalent
        /// gaps. Used by the per-tick orbit-update path to keep a ghost ProtoVessel alive
        /// while the playback head crosses brief inter-segment gaps inside one body frame
        /// (e.g., capture burn between two distinct Mun orbits). The orbit shape stays on
        /// the previous segment until UT enters the next segment; the SOI / body change is
        /// the only event that actually drops the ghost.
        /// </summary>
        internal static OrbitSegment? FindOrbitSegmentOrSameBodyCarry(List<OrbitSegment> segments, double ut)
        {
            if (segments == null || segments.Count == 0)
                return null;

            // Active segment: same inclusive-end-on-last rule as TryGetOrbitWindowForMapDisplay.
            for (int i = 0; i < segments.Count; i++)
            {
                bool inRange = (i == segments.Count - 1)
                    ? (ut >= segments[i].startUT && ut <= segments[i].endUT)
                    : (ut >= segments[i].startUT && ut < segments[i].endUT);
                if (inRange)
                    return segments[i];
            }

            // Gap-carry: previous segment + next segment exist and share a body name.
            // Orbit equivalence is intentionally NOT checked here — the goal is to keep
            // the ghost alive across an intra-body-frame burn / sparse-physics region.
            int previousIndex = -1;
            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment candidate = segments[i];
                if (candidate.endUT <= ut)
                {
                    previousIndex = i;
                    continue;
                }
                if (previousIndex < 0 || candidate.startUT <= ut)
                    return null;
                if (string.IsNullOrEmpty(segments[previousIndex].bodyName)
                    || !string.Equals(segments[previousIndex].bodyName, candidate.bodyName, System.StringComparison.Ordinal))
                    return null;
                return segments[previousIndex];
            }

            return null;
        }

        /// <summary>
        /// Map-view body-frame helper: return the time bounds covering the run of consecutive
        /// same-body OrbitSegments that contains <paramref name="ut"/> (or that brackets it
        /// across a same-body intra-block gap). Orbit equivalence is NOT required — the run
        /// stops only at a body change or recording boundary. Used by the orbit-line patch
        /// to decide line visibility independent of per-segment orbit-shape changes, so the
        /// rendered orbit line stays on continuously while the ghost crosses inter-segment
        /// gaps within one body frame and only blinks off at an SOI / body change.
        /// </summary>
        internal static bool TryGetBodyFrameBoundsForMapDisplay(
            List<OrbitSegment> segments,
            double ut,
            out double bodyFrameStartUT,
            out double bodyFrameEndUT,
            out string bodyName,
            out int firstIndex,
            out int lastIndex)
        {
            bodyFrameStartUT = 0;
            bodyFrameEndUT = 0;
            bodyName = null;
            firstIndex = -1;
            lastIndex = -1;

            if (segments == null || segments.Count == 0)
                return false;

            int seed = -1;
            for (int i = 0; i < segments.Count; i++)
            {
                bool inRange = (i == segments.Count - 1)
                    ? (ut >= segments[i].startUT && ut <= segments[i].endUT)
                    : (ut >= segments[i].startUT && ut < segments[i].endUT);
                if (inRange)
                {
                    seed = i;
                    break;
                }
            }

            if (seed < 0)
            {
                // Try same-body gap-carry seed.
                int previousIndex = -1;
                for (int i = 0; i < segments.Count; i++)
                {
                    OrbitSegment candidate = segments[i];
                    if (candidate.endUT <= ut)
                    {
                        previousIndex = i;
                        continue;
                    }
                    if (previousIndex < 0 || candidate.startUT <= ut)
                        return false;
                    if (string.IsNullOrEmpty(segments[previousIndex].bodyName)
                        || !string.Equals(segments[previousIndex].bodyName, candidate.bodyName, System.StringComparison.Ordinal))
                        return false;
                    seed = previousIndex;
                    break;
                }
                if (seed < 0)
                    return false;
            }

            string seedBody = segments[seed].bodyName;
            if (string.IsNullOrEmpty(seedBody))
                return false;

            firstIndex = seed;
            while (firstIndex > 0
                && string.Equals(segments[firstIndex - 1].bodyName, seedBody, System.StringComparison.Ordinal))
            {
                firstIndex--;
            }

            lastIndex = seed;
            while (lastIndex < segments.Count - 1
                && string.Equals(segments[lastIndex + 1].bodyName, seedBody, System.StringComparison.Ordinal))
            {
                lastIndex++;
            }

            bodyFrameStartUT = segments[firstIndex].startUT;
            bodyFrameEndUT = segments[lastIndex].endUT;
            bodyName = seedBody;
            return true;
        }

        /// <summary>
        /// Find the waypoint index for interpolation using cached lookup.
        /// Parameterized to work with any point list + cached index.
        /// </summary>
        internal static int FindWaypointIndex(List<TrajectoryPoint> points, ref int cachedIndex, double targetUT)
        {
            if (points.Count < 2)
            {
                ParsekLog.VerboseRateLimited("TrajectoryMath", "waypoint-too-few-points",
                    $"FindWaypointIndex skipped: points.Count={points.Count} targetUT={targetUT:F3}", 5.0);
                return -1;
            }

            if (targetUT < points[0].ut)
                return -1;

            if (targetUT >= points[points.Count - 1].ut)
                return points.Count - 2;

            // Try cached index first (common case: sequential playback)
            if (cachedIndex >= 0 && cachedIndex < points.Count - 1)
            {
                if (points[cachedIndex].ut <= targetUT &&
                    points[cachedIndex + 1].ut > targetUT)
                {
                    DiagnosticsState.health.waypointCacheHits++;
                    return cachedIndex;
                }

                int nextIndex = cachedIndex + 1;
                if (nextIndex < points.Count - 1 &&
                    points[nextIndex].ut <= targetUT &&
                    points[nextIndex + 1].ut > targetUT)
                {
                    cachedIndex = nextIndex;
                    DiagnosticsState.health.waypointCacheHits++;
                    return nextIndex;
                }
            }

            // Binary search fallback
            int low = 0;
            int high = points.Count - 2;

            while (low <= high)
            {
                int mid = (low + high) / 2;

                if (points[mid].ut <= targetUT && points[mid + 1].ut > targetUT)
                {
                    cachedIndex = mid;
                    DiagnosticsState.health.waypointCacheMisses++;
                    return mid;
                }
                else if (points[mid].ut > targetUT)
                {
                    high = mid - 1;
                }
                else
                {
                    low = mid + 1;
                }
            }

            // Linear fallback (shouldn't reach here)
            for (int i = 0; i < points.Count - 1; i++)
            {
                if (points[i].ut <= targetUT && points[i + 1].ut > targetUT)
                {
                    cachedIndex = i;
                    DiagnosticsState.health.waypointCacheMisses++;
                    ParsekLog.VerboseRateLimited("TrajectoryMath", "waypoint-linear-fallback-hit",
                        $"Linear fallback used for targetUT={targetUT:F3}, idx={i}", 5.0);
                    return i;
                }
            }

            ParsekLog.VerboseRateLimited("TrajectoryMath", "waypoint-index-not-found",
                $"No waypoint index found for targetUT={targetUT:F3}", 5.0);
            return -1;
        }

        /// <summary>
        /// Computes aggregate statistics for a recording.
        /// Pure function — uses bodyLookup callback for orbit segment calculations.
        /// bodyLookup("Kerbin") should return [radius, gravParameter] or null.
        /// </summary>
        internal static RecordingStats ComputeStats(
            Recording rec,
            System.Func<string, double[]> bodyLookup = null)
        {
            var stats = new RecordingStats();
            stats.pointCount = rec.Points.Count;
            stats.orbitSegmentCount = rec.OrbitSegments.Count;
            stats.partEventCount = rec.PartEvents.Count;

            if (rec.Points.Count == 0)
            {
                ParsekLog.VerboseRateLimited("TrajectoryMath", "compute-stats-empty",
                    "ComputeStats called with empty trajectory", 5.0);
                return stats;
            }

            var bodyCounts = new Dictionary<string, int>();
            double lat0 = rec.Points[0].latitude;
            double lon0 = rec.Points[0].longitude;
            string body0 = rec.Points[0].bodyName ?? "Kerbin";
            int firstPointSectionIdx = FindTrackSectionForUT(rec.TrackSections, rec.Points[0].ut);
            ReferenceFrame firstPointFrame = firstPointSectionIdx >= 0
                ? rec.TrackSections[firstPointSectionIdx].referenceFrame
                : ReferenceFrame.Absolute;

            ApplyTrackSectionAltitudeMetadata(rec.TrackSections, ref stats);

            for (int i = 0; i < rec.Points.Count; i++)
            {
                var pt = rec.Points[i];

                if (pt.altitude > stats.maxAltitude)
                    stats.maxAltitude = pt.altitude;

                float speed = pt.velocity.magnitude;
                if (speed > stats.maxSpeed)
                    stats.maxSpeed = speed;

                string body = pt.bodyName ?? "Kerbin";
                int count;
                bodyCounts.TryGetValue(body, out count);
                bodyCounts[body] = count + 1;

                if (bodyLookup != null)
                {
                    double[] bodyData = bodyLookup(body);
                    if (bodyData != null)
                    {
                        double bodyRadius = bodyData[0];

                        // Distance from previous point (same body only).
                        // Skip when both points fall inside an orbit segment
                        // to avoid double-counting distance already covered
                        // by the segment's mean-speed calculation.
                        if (i > 0 && (rec.Points[i - 1].bodyName ?? "Kerbin") == body)
                        {
                            var prev = rec.Points[i - 1];
                            double midUT = (prev.ut + pt.ut) * 0.5;
                            bool inOrbitSegment = FindOrbitSegment(rec.OrbitSegments, midUT) != null;
                            if (!inOrbitSegment)
                            {
                                int sectionIdx = FindTrackSectionForUT(rec.TrackSections, midUT);
                                ReferenceFrame frame = sectionIdx >= 0
                                    ? rec.TrackSections[sectionIdx].referenceFrame
                                    : ReferenceFrame.Absolute;

                                stats.distanceTravelled += ComputePairwiseTravelDistance(
                                    prev, pt, frame, bodyRadius);
                            }
                        }

                        // Max range from first point (same body only)
                        if (body == body0)
                        {
                            int pointSectionIdx = FindTrackSectionForUT(rec.TrackSections, pt.ut);
                            ReferenceFrame pointFrame = pointSectionIdx >= 0
                                ? rec.TrackSections[pointSectionIdx].referenceFrame
                                : ReferenceFrame.Absolute;
                            double range = ComputePointRangeFromStart(
                                rec.Points[0], pt, firstPointFrame, pointFrame, bodyRadius);
                            if (range > stats.maxRange)
                                stats.maxRange = range;
                        }
                    }
                }
            }

            AccumulateOrbitSegmentStats(rec.OrbitSegments, bodyLookup, ref stats);
            stats.primaryBody = DeterminePrimaryBody(bodyCounts);

            ParsekLog.Verbose("TrajectoryMath",
                $"ComputeStats complete: points={stats.pointCount} segments={stats.orbitSegmentCount} " +
                $"events={stats.partEventCount} maxAlt={stats.maxAltitude:F0} maxSpeed={stats.maxSpeed:F1} " +
                $"dist={stats.distanceTravelled:F0} range={stats.maxRange:F0} body={stats.primaryBody}");

            return stats;
        }

        /// <summary>
        /// Computes the distance contributed by a single consecutive point pair, dispatching
        /// on reference frame: Relative sections store anchor-local metre offsets in
        /// latitude/longitude/altitude (Euclidean dx/dy/dz delta), while non-Relative sections
        /// store body-fixed lat/lon/alt (haversine surface distance plus altitude delta).
        /// </summary>
        internal static double ComputePairwiseTravelDistance(
            in TrajectoryPoint prev,
            in TrajectoryPoint cur,
            ReferenceFrame frame,
            double bodyRadius)
        {
            if (frame == ReferenceFrame.Relative)
            {
                double dx = cur.latitude - prev.latitude;
                double dy = cur.longitude - prev.longitude;
                double dz = cur.altitude - prev.altitude;
                return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            double avgAlt = (prev.altitude + cur.altitude) * 0.5;
            double surfaceDist = HaversineDistance(
                prev.latitude, prev.longitude,
                cur.latitude, cur.longitude,
                bodyRadius + avgAlt);
            double altDiff = System.Math.Abs(cur.altitude - prev.altitude);
            return System.Math.Sqrt(surfaceDist * surfaceDist + altDiff * altDiff);
        }

        /// <summary>
        /// Computes the range of a point from the first recorded point, dispatching on the
        /// start-point and current-point reference frames: both Relative uses an anchor-local
        /// Euclidean dx/dy/dz delta; current-Relative-only returns 0.0 (cannot mix frames);
        /// otherwise uses a haversine surface range from the start point.
        /// </summary>
        internal static double ComputePointRangeFromStart(
            in TrajectoryPoint start,
            in TrajectoryPoint cur,
            ReferenceFrame startFrame,
            ReferenceFrame curFrame,
            double bodyRadius)
        {
            if (startFrame == ReferenceFrame.Relative
                && curFrame == ReferenceFrame.Relative)
            {
                double dx = cur.latitude - start.latitude;
                double dy = cur.longitude - start.longitude;
                double dz = cur.altitude - start.altitude;
                return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            if (curFrame == ReferenceFrame.Relative)
            {
                return 0.0;
            }

            double avgAlt = (start.altitude + cur.altitude) * 0.5;
            return HaversineDistance(
                start.latitude, start.longitude,
                cur.latitude, cur.longitude,
                bodyRadius + avgAlt);
        }

        private static void ApplyTrackSectionAltitudeMetadata(
            List<TrackSection> sections,
            ref RecordingStats stats)
        {
            if (sections == null)
                return;

            for (int i = 0; i < sections.Count; i++)
            {
                if (!float.IsNaN(sections[i].maxAltitude)
                    && sections[i].maxAltitude > stats.maxAltitude)
                {
                    stats.maxAltitude = sections[i].maxAltitude;
                }
            }
        }

        /// <summary>
        /// Accumulates orbit segment contributions into recording stats: apoapsis altitude,
        /// periapsis speed (vis-viva), and mean-speed distance for each segment.
        /// </summary>
        internal static void AccumulateOrbitSegmentStats(
            List<OrbitSegment> segments,
            System.Func<string, double[]> bodyLookup,
            ref RecordingStats stats)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (bodyLookup == null) continue;
                double[] bodyData = bodyLookup(seg.bodyName ?? "Kerbin");
                if (bodyData == null) continue;

                double bodyRadius = bodyData[0];
                double gm = bodyData[1];

                // Apoapsis altitude
                double apoRadius = seg.semiMajorAxis * (1.0 + seg.eccentricity);
                double apoAlt = apoRadius - bodyRadius;
                if (apoAlt > stats.maxAltitude)
                    stats.maxAltitude = apoAlt;

                // Periapsis speed (max orbital speed via vis-viva)
                double periRadius = seg.semiMajorAxis * (1.0 - seg.eccentricity);
                if (periRadius > 0 && seg.semiMajorAxis > 0)
                {
                    double periSpeed = System.Math.Sqrt(
                        gm * (2.0 / periRadius - 1.0 / seg.semiMajorAxis));
                    if (periSpeed > stats.maxSpeed)
                        stats.maxSpeed = periSpeed;
                }

                // Mean orbital speed * duration
                if (seg.semiMajorAxis > 0)
                {
                    double meanSpeed = System.Math.Sqrt(gm / seg.semiMajorAxis);
                    stats.distanceTravelled += meanSpeed * (seg.endUT - seg.startUT);
                }
            }
        }

        /// <summary>
        /// Returns the body name with the highest point count, or null if the dictionary is empty.
        /// </summary>
        internal static string DeterminePrimaryBody(Dictionary<string, int> bodyCounts)
        {
            string primaryBody = null;
            int maxCount = 0;
            foreach (var kvp in bodyCounts)
            {
                if (kvp.Value > maxCount)
                {
                    maxCount = kvp.Value;
                    primaryBody = kvp.Key;
                }
            }
            return primaryBody;
        }

        private static double HaversineDistance(
            double lat1Deg, double lon1Deg,
            double lat2Deg, double lon2Deg, double radius)
        {
            const double toRad = System.Math.PI / 180.0;
            double lat1 = lat1Deg * toRad;
            double lat2 = lat2Deg * toRad;
            double dlat = (lat2Deg - lat1Deg) * toRad;
            double dlon = (lon2Deg - lon1Deg) * toRad;
            double a = System.Math.Sin(dlat * 0.5) * System.Math.Sin(dlat * 0.5) +
                       System.Math.Cos(lat1) * System.Math.Cos(lat2) *
                       System.Math.Sin(dlon * 0.5) * System.Math.Sin(dlon * 0.5);
            double c = 2.0 * System.Math.Atan2(
                System.Math.Sqrt(a), System.Math.Sqrt(1.0 - a));
            return radius * c;
        }

        /// <summary>
        /// Performs point lookup and interpolation factor computation for trajectory playback.
        /// Shared core between flight and KSC interpolation paths.
        /// Returns true if a valid interpolation pair was found; false if targetUT is before
        /// the first point or the list is empty/null (caller should handle single-point fallback).
        /// When true, before/after/t are set for the caller to use with body-specific positioning.
        /// When false and the list is non-empty, before is set to points[0] for single-point fallback.
        /// </summary>
        internal static bool InterpolatePoints(
            List<TrajectoryPoint> points, ref int cachedIndex, double targetUT,
            out TrajectoryPoint before, out TrajectoryPoint after, out float t)
        {
            before = default;
            after = default;
            t = 0f;

            if (points == null || points.Count == 0)
                return false;

            int indexBefore = FindWaypointIndex(points, ref cachedIndex, targetUT);

            if (indexBefore < 0)
            {
                // Before recording start — caller should position at first point
                before = points[0];
                return false;
            }

            before = points[indexBefore];
            after = points[indexBefore + 1];

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                // Degenerate segment — treat as single point
                t = 0f;
                return true;
            }

            t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);
            return true;
        }

        /// <summary>
        /// Returns the nearest recorded TrajectoryPoint at the given UT, or null if the
        /// point list is empty/null or UT is before recording start. For UT past recording
        /// end, returns the last point. For mid-range UT, returns the lower bracket point.
        ///
        /// Uses the bracket point's recorded values directly (no interpolation) for
        /// orbit accuracy — same pattern as VesselSpawner. The bracket point represents
        /// the last sampled physics state, which produces a more physically correct orbit
        /// than interpolated values.
        /// </summary>
        internal static TrajectoryPoint? BracketPointAtUT(
            List<TrajectoryPoint> points, double ut, ref int cachedIndex)
        {
            if (points == null || points.Count == 0)
                return null;

            bool found = InterpolatePoints(points, ref cachedIndex, ut,
                out TrajectoryPoint before, out TrajectoryPoint after, out float t);

            if (!found)
            {
                // InterpolatePoints returns false for empty list or UT before start.
                // Empty already handled above, so this is UT before start → null.
                return null;
            }

            // Past end: t is clamped to 1 — return the upper bracket (last point).
            // Mid-range: return the lower bracket (most recent sampled state).
            return t >= 1f ? after : before;
        }

        internal static double InterpolateAltitude(double altBefore, double altAfter, float t)
        {
            return altBefore + (altAfter - altBefore) * t;
        }

        /// <summary>
        /// Computes the anchor-local offset used by RELATIVE sections.
        /// Pure static method for testability.
        /// </summary>
        internal static Vector3d ComputeRelativeLocalOffset(
            Vector3d focusedPosition,
            Vector3d anchorPosition,
            Quaternion anchorWorldRotation)
        {
            Vector3 worldOffset = (Vector3)(focusedPosition - anchorPosition);
            Quaternion inverseAnchor = PureInverse(PureNormalize(anchorWorldRotation));
            Vector3 localOffset = PureRotateVector(inverseAnchor, worldOffset);
            return new Vector3d(localOffset.x, localOffset.y, localOffset.z);
        }

        /// <summary>
        /// Computes world position from anchor position and a format-v6 anchor-local
        /// offset. Pure static for testability.
        /// </summary>
        internal static Vector3d ApplyRelativeLocalOffset(
            Vector3d anchorWorldPos,
            Quaternion anchorWorldRotation,
            double dx,
            double dy,
            double dz)
        {
            Vector3 localOffset = new Vector3((float)dx, (float)dy, (float)dz);
            Vector3 worldOffset = PureRotateVector(
                PureNormalize(anchorWorldRotation),
                localOffset);
            return anchorWorldPos + (Vector3d)worldOffset;
        }

        /// <summary>
        /// Computes the anchor-local rotation used by format-v6 RELATIVE sections.
        /// Pure static method for testability.
        /// </summary>
        internal static Quaternion ComputeRelativeLocalRotation(
            Quaternion focusWorldRotation,
            Quaternion anchorWorldRotation)
        {
            Quaternion anchorInverse = PureInverse(PureNormalize(anchorWorldRotation));
            return SanitizeQuaternion(PureMultiply(anchorInverse, focusWorldRotation));
        }

        /// <summary>
        /// Reconstructs world rotation from a format-v6 anchor-local RELATIVE rotation.
        /// Pure static for testability.
        /// </summary>
        internal static Quaternion ApplyRelativeLocalRotation(
            Quaternion anchorWorldRotation,
            Quaternion relativeLocalRotation)
        {
            return SanitizeQuaternion(
                PureMultiply(PureNormalize(anchorWorldRotation), relativeLocalRotation));
        }

        /// <summary>
        /// Resolves a RELATIVE-frame anchor-local position offset to world space.
        /// </summary>
        internal static Vector3d ResolveRelativePlaybackPosition(
            Vector3d anchorWorldPos,
            Quaternion anchorWorldRotation,
            double dx,
            double dy,
            double dz)
        {
            return ApplyRelativeLocalOffset(anchorWorldPos, anchorWorldRotation, dx, dy, dz);
        }

        /// <summary>
        /// Resolves a RELATIVE-frame rotation to world space. RELATIVE sections store the
        /// anchor-local rotation <c>Inverse(anchor) * focus</c>, and this resolver
        /// reconstitutes the focus world rotation with <c>anchor * stored</c>.
        /// </summary>
        internal static Quaternion ResolveRelativePlaybackRotation(
            Quaternion anchorWorldRotation,
            Quaternion storedRelativeRotation)
        {
            return ApplyRelativeLocalRotation(
                anchorWorldRotation,
                SanitizeQuaternion(storedRelativeRotation));
        }

        /// <summary>
        /// Finds the TrackSection covering the given UT.
        /// Returns the index into the sections list, or -1 if none found.
        /// Linear scan — the list is typically small (a handful of sections per recording).
        /// Pure static for testability.
        /// </summary>
        internal static int FindTrackSectionForUT(List<TrackSection> sections, double ut)
        {
            if (sections == null) return -1;
            for (int i = 0; i < sections.Count; i++)
            {
                // Last section uses inclusive end; others use exclusive end
                bool inRange = (i == sections.Count - 1)
                    ? (ut >= sections[i].startUT && ut <= sections[i].endUT)
                    : (ut >= sections[i].startUT && ut < sections[i].endUT);
                if (inRange)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Returns true if the TrackSection covering the given UT has a surface environment
        /// (SurfaceMobile or SurfaceStationary). Surface vessels should not use orbit segment
        /// interpolation — their Keplerian orbit is a sub-surface path through the planet.
        /// </summary>
        internal static bool IsSurfaceAtUT(List<TrackSection> sections, double ut)
        {
            int idx = FindTrackSectionForUT(sections, ut);
            if (idx < 0) return false;
            var env = sections[idx].environment;
            return env == SegmentEnvironment.SurfaceMobile || env == SegmentEnvironment.SurfaceStationary;
        }

        /// <summary>
        /// Speed floor for reentry FX build. A trajectory whose peak recorded velocity
        /// magnitude is below this threshold and that has no orbit segments cannot ever
        /// produce reentry visuals — Mach 2.5 thermal FX requires ~675 m/s even in thin
        /// atmosphere on Kerbin, and real reentry on Laythe/Duna/Eve is higher still.
        /// 400 m/s is well under Mach 1.5 anywhere, giving a safe cutoff for stationary
        /// part showcases, EVA walks, slow rovers, and low-speed suborbital hops.
        /// </summary>
        internal const float ReentryPotentialSpeedFloor = 400f;

        /// <summary>
        /// Returns true if the trajectory could plausibly produce reentry visuals during
        /// playback. Used to gate the expensive per-spawn reentry FX build
        /// (<see cref="GhostVisualBuilder.TryBuildReentryFx"/>), which combines all ghost
        /// meshes, allocates a ParticleSystem, and clones glow materials — costs that
        /// multiply across every loop-cycle rebuild of every active ghost.
        ///
        /// Returns true if:
        ///   - the trajectory has any orbit segments (orbital ghosts always de-orbit at
        ///     high speed), OR
        ///   - any recorded trajectory point has velocity magnitude at or above
        ///     <see cref="ReentryPotentialSpeedFloor"/>.
        ///
        /// <b>Velocity frame:</b> <see cref="TrajectoryPoint.velocity"/> is the
        /// Krakensbane-corrected `rb_velocityD + Krakensbane.GetFrameVelocity()` captured
        /// at sample time. In KSP's body-co-rotating world frame this is effectively the
        /// vessel's inertial speed, so a landed/stationary vessel reads ≈0 and the 400 m/s
        /// floor safely excludes showcases, walks, rovers, and low-speed suborbital hops
        /// while preserving every supersonic / orbital trajectory. NaN components in a
        /// point fail the ≥ comparison harmlessly and do not throw.
        ///
        /// Pure function — no Unity dependencies, no side effects. O(n) in trajectory
        /// point count; called once per ghost build, not per frame.
        /// </summary>
        internal static bool HasReentryPotential(IPlaybackTrajectory traj)
        {
            if (traj == null) return false;

            // Orbital ghosts always re-enter at high speed on de-orbit.
            if (traj.HasOrbitSegments) return true;

            var points = traj.Points;
            if (points == null) return false;

            float floorSq = ReentryPotentialSpeedFloor * ReentryPotentialSpeedFloor;
            for (int i = 0; i < points.Count; i++)
            {
                // sqrMagnitude with a NaN component yields NaN; `NaN >= floorSq` is false,
                // so malformed points cannot produce a false positive.
                if (points[i].velocity.sqrMagnitude >= floorSq)
                    return true;
            }
            return false;
        }
    }
}
