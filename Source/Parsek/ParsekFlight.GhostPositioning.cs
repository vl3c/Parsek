using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // Ghost positioning shared by manual and timeline playback: point, orbit, relative-anchor,
    // body-fixed and surface placement, spline evaluation and the positioning caches.
    public partial class ParsekFlight
    {
        /// <summary>
        /// Surface velocity from a RELATIVE section's body-fixed shadow frames at
        /// <paramref name="ut"/>. RELATIVE <c>frames</c> hold anchor-local metre offsets, never
        /// body-fixed coordinates, so they are not read here; a section without two
        /// <c>bodyFixedFrames</c> covering <paramref name="ut"/> leaves the result unchanged
        /// (surface velocity unknown).
        /// </summary>
        private static InterpolationResult WithRelativeSectionSurfaceVelocity(
            InterpolationResult result, TrackSection section, double ut)
        {
            List<TrajectoryPoint> bodyFixed =
                GhostSurfaceVelocity.SelectRelativeSectionBodyFixedFrames(section);
            int i = GhostSurfaceVelocity.FindBracketIndex(bodyFixed, ut);
            if (i < 0)
                return result;
            TrajectoryPoint before = bodyFixed[i];
            TrajectoryPoint after = bodyFixed[i + 1];
            if (string.IsNullOrEmpty(before.bodyName)
                || !string.Equals(before.bodyName, after.bodyName, StringComparison.Ordinal))
                return result;
            CelestialBody body = FindBodyByNameNoAlloc(before.bodyName);
            if (body == null)
                return result;
            Vector3d posBefore = body.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = body.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);
            if (!GhostSurfaceVelocity.TryFromBracketPositions(
                    posBefore, posAfter, after.ut - before.ut, out Vector3 surfaceVelocity))
                return result;
            return result.WithSurfaceVelocity(surfaceVelocity, GhostSurfaceVelocitySource.Positions);
        }

        // Runs per frame for every RELATIVE-section ghost: a plain loop, no predicate closure.
        private static CelestialBody FindBodyByNameNoAlloc(string bodyName)
        {
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            if (bodies == null || string.IsNullOrEmpty(bodyName))
                return null;
            for (int i = 0; i < bodies.Count; i++)
            {
                CelestialBody candidate = bodies[i];
                if (candidate != null && candidate.name == bodyName)
                    return candidate;
            }
            return null;
        }

        private static double ComputePointHermiteMaxDeviationMeters(Vector3d posBefore, Vector3d posAfter)
        {
            double chordMeters = Vector3d.Distance(posBefore, posAfter);
            if (double.IsNaN(chordMeters) || double.IsInfinity(chordMeters))
                return PointHermiteMinDeviationMeters;

            double scaled = chordMeters * PointHermiteDeviationFraction;
            if (scaled < PointHermiteMinDeviationMeters)
                return PointHermiteMinDeviationMeters;
            if (scaled > PointHermiteMaxDeviationCapMeters)
                return PointHermiteMaxDeviationCapMeters;
            return scaled;
        }

        /// <summary>
        /// Phase 1 spline-positioning gate (design doc §6.1, §17.3.1).
        /// Returns <c>true</c> with a populated <paramref name="spline"/>
        /// when (a) the caller supplied both a recording id and a non-negative
        /// section index and (b) <see cref="SectionAnnotationStore"/> holds a valid
        /// spline for the (recordingId, sectionIndex) pair. Any miss is a
        /// silent fall-through to legacy lerp positioning (HR-9 — "no spline
        /// yet" is a normal state, not a failure).
        /// </summary>
        private static bool allowSplinePositioning(string recordingId, int sectionIndex,
            out Parsek.Rendering.SmoothingSpline spline)
        {
            spline = default(Parsek.Rendering.SmoothingSpline);
            if (string.IsNullOrEmpty(recordingId) || sectionIndex < 0)
                return false;

            if (!Parsek.Rendering.SectionAnnotationStore.TryGetSmoothingSpline(
                    recordingId, sectionIndex, out spline))
                return false;

            return spline.IsValid;
        }

        /// <summary>
        /// Phase 2 start-side anchor-correction gate (design doc §6.3 /
        /// §6.4 single-anchor case / §7.1 / §18 Phase 2 / §26.1 HR-15).
        /// Returns <c>true</c> with a populated <paramref name="ac"/> when
        /// (a) the caller supplied both a recording id and a non-negative
        /// section index and (b) <see cref="Parsek.Rendering.RenderSessionState"/>
        /// holds a start-side anchor for the (recordingId, sectionIndex)
        /// pair.
        ///
        /// <para>
        /// HR-15 — the lookup reads the cached <see cref="Parsek.Rendering.AnchorCorrection.Epsilon"/>
        /// frozen at session entry; no live vessel state is read. Any miss is
        /// a silent fall-through (HR-9: a missing anchor is a normal state for
        /// non-re-fly sessions and for siblings that have not yet had their
        /// ε computed, not a failure).
        /// </para>
        ///
        /// <para>
        /// Phase 3 superseded the in-renderer call site with
        /// <see cref="allowAnchorCorrectionInterval"/> (which returns the
        /// time-varying lerp result per §6.4). This start-side helper is
        /// retained for the unit-test surface
        /// (<c>AnchorCorrectionConsumerHookTests</c>) — production rendering
        /// no longer calls it directly.
        /// </para>
        /// </summary>
        internal static bool allowAnchorCorrection(string recordingId, int sectionIndex,
            out Parsek.Rendering.AnchorCorrection ac)
        {
            ac = default(Parsek.Rendering.AnchorCorrection);
            if (string.IsNullOrEmpty(recordingId) || sectionIndex < 0)
                return false;

            return Parsek.Rendering.RenderSessionState.TryLookup(
                recordingId, sectionIndex, Parsek.Rendering.AnchorSide.Start, out ac);
        }

        /// <summary>
        /// Phase 3 multi-anchor-lerp gate (design doc §6.4 / §8 / §18 Phase 3
        /// / §19.2 Stage 4 log table). Returns <c>true</c> with a populated
        /// <paramref name="epsilon"/> when (a) the caller supplied both a
        /// recording id and a non-negative section index and (b)
        /// <see cref="Parsek.Rendering.RenderSessionState"/> holds at least
        /// one anchor (start or end side) for the (recordingId, sectionIndex)
        /// pair. The returned ε is evaluated at <paramref name="targetUT"/>
        /// per §6.4 — constant for one-sided intervals, linear lerp for
        /// both-end intervals.
        ///
        /// <para>
        /// Side effects on hit: emits one of two <c>[Pipeline-Lerp]</c> log
        /// lines (single-anchor Verbose, both-end divergence Warn) ONCE per
        /// session per (recordingId, sectionIndex) — the dedup is owned by
        /// <see cref="Parsek.Rendering.RenderSessionState"/> so per-frame
        /// queries here cost only a HashSet lookup. The
        /// <c>degenerate-span</c> Warn fires from the math evaluator
        /// (<see cref="Parsek.Rendering.AnchorCorrectionInterval.EvaluateAt"/>)
        /// when the interval is malformed — keeping per-key dedup in one
        /// place. HR-7 is naturally enforced because the consumer always
        /// queries with the section that owns <paramref name="targetUT"/>; a
        /// lerp cannot cross a hard discontinuity by construction.
        /// </para>
        /// </summary>
        internal static bool allowAnchorCorrectionInterval(string recordingId, int sectionIndex,
            double targetUT, out Vector3d epsilon)
        {
            epsilon = default(Vector3d);
            if (string.IsNullOrEmpty(recordingId) || sectionIndex < 0)
                return false;

            Parsek.Rendering.AnchorCorrectionInterval? maybeInterval =
                Parsek.Rendering.RenderSessionState.LookupForSegmentInterval(
                    recordingId, sectionIndex);
            if (!maybeInterval.HasValue)
                return false;

            Parsek.Rendering.AnchorCorrectionInterval interval = maybeInterval.Value;
            epsilon = interval.EvaluateAt(targetUT);

            // Per-session-per-key dedup'd diagnostics (§19.2 Stage 4):
            //   Both-end + divergence > threshold  → Warn  "epsilon-divergence"
            //   StartOnly / EndOnly                 → Verb  "Single-anchor case"
            // The Warn for degenerate spans (End.UT <= Start.UT) is emitted
            // from the evaluator itself so any caller of EvaluateAt picks it
            // up uniformly.
            if (interval.Kind == Parsek.Rendering.AnchorIntervalKind.Both)
            {
                Parsek.Rendering.RenderSessionState.NotifyLerpDivergenceCheck(in interval);
            }
            else
            {
                Parsek.Rendering.RenderSessionState.NotifySingleAnchorLerpCase(in interval);
            }

            return true;
        }

        /// <summary>
        /// Render-path anchor-correction gate.
        /// </summary>
        internal static bool allowRenderAnchorCorrectionInterval(
            string recordingId,
            int sectionIndex,
            double targetUT,
            bool suppressAnchorCorrection,
            out Vector3d epsilon,
            out string reason)
        {
            epsilon = default(Vector3d);
            if (suppressAnchorCorrection)
            {
                reason = "suppressed";
                return false;
            }

            if (allowAnchorCorrectionInterval(recordingId, sectionIndex, targetUT, out epsilon))
            {
                reason = "applied";
                return true;
            }

            reason = "miss";
            return false;
        }

        internal static bool allowPointHermiteInterpolation(
            bool suppressHermite,
            bool splineApplied,
            bool allowNormalPlaybackHermite,
            out string reason)
        {
            if (splineApplied)
            {
                reason = "spline-applied";
                return false;
            }

            if (suppressHermite)
            {
                reason = "suppressed";
                return false;
            }

            if (!allowNormalPlaybackHermite)
            {
                reason = "normal-playback-linearized";
                return false;
            }

            reason = "eligible";
            return true;
        }

        internal static bool allowSplinePositioningForPlayback(
            bool suppressSpline,
            bool allowNormalPlaybackSplinePositioning,
            out string reason)
        {
            if (suppressSpline)
            {
                reason = "suppressed";
                return false;
            }

            if (!allowNormalPlaybackSplinePositioning)
            {
                reason = "normal-playback-linearized";
                return false;
            }

            reason = "eligible";
            return true;
        }

        /// <summary>
        private static string FormatStandaloneCoverageRange(double startUT, double endUT)
        {
            if (double.IsNaN(startUT) || double.IsNaN(endUT))
                return "(none)";
            return "[" + startUT.ToString("R", CultureInfo.InvariantCulture)
                + "," + endUT.ToString("R", CultureInfo.InvariantCulture) + "]";
        }

        private static void LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed(
            IPlaybackTrajectory traj,
            int recordingIndex,
            double playbackUT,
            string callsite,
            string routeReason,
            DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic)
        {
            string recordingId = traj?.RecordingId ?? "(none)";
            string key = "playback-world-parent-debris-body-fixed-failed-closed|"
                + recordingId + "|" + recordingIndex.ToString(CultureInfo.InvariantCulture)
                + "|" + (callsite ?? "(unknown)");
            ParsekLog.WarnRateLimited(
                "Playback",
                key,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: body-fixed primary failed closed for parent-anchored debris; recorded-relative fallback suppressed recording=#{1} recordingId={2} vessel=\"{3}\" playbackUT={4} routeReason={5} coverageReason={6} sectionIndex={7} sectionUT={8} relativeFrames={9} bodyFixedFrames={10} anchorRec={11}",
                    callsite ?? "(unknown)",
                    recordingIndex.ToString(CultureInfo.InvariantCulture),
                    recordingId,
                    traj?.VesselName ?? "(unknown)",
                    playbackUT.ToString("R", CultureInfo.InvariantCulture),
                    routeReason ?? "(unknown)",
                    diagnostic.Reason ?? "(unknown)",
                    diagnostic.SectionIndex.ToString(CultureInfo.InvariantCulture),
                    FormatStandaloneCoverageRange(diagnostic.SectionStartUT, diagnostic.SectionEndUT),
                    FormatStandaloneCoverageRange(diagnostic.FirstRelativeFrameUT, diagnostic.LastRelativeFrameUT),
                    FormatStandaloneCoverageRange(diagnostic.FirstBodyFixedFrameUT, diagnostic.LastBodyFixedFrameUT),
                    diagnostic.AnchorRecordingId ?? "(none)"),
                5.0);
        }

        /// <summary>
        /// Resolves a recording by id without driving the active scene's
        /// playback context. Walks <see cref="RecordingStore.CommittedRecordings"/>
        /// directly so the preview-source tail-lift resolver can locate a
        /// recording outside the active playback scope.
        /// </summary>
        private static Recording ResolveRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            try
            {
                IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
                if (committed != null)
                {
                    for (int i = 0; i < committed.Count; i++)
                    {
                        Recording r = committed[i];
                        if (r == null) continue;
                        if (string.Equals(r.RecordingId, recordingId, StringComparison.Ordinal))
                            return r;
                    }
                }
            }
            catch
            {
                // Mid-load mutation; treat as missing.
            }
            return null;
        }

        /// <summary>
        /// Phase 1 + Phase 4 LateUpdate spline re-evaluation helper (design
        /// doc §6.1 / §6.2 / §18 Phase 1+4 / HR-9). Instance shim that
        /// supplies the per-session <see cref="unknownFrameTagWarned"/> dedup
        /// set; the gate-then-dispatch logic itself lives in
        /// <see cref="TryComputeLateUpdateSplineWorldPositionPure"/> so xUnit
        /// can exercise every branch without driving Unity's LateUpdate.
        ///
        /// <para>
        /// Without re-evaluating the spline here, the LateUpdate PointInterp
        /// branch would overwrite the spline-positioned <c>transform.position</c>
        /// with the raw <c>(latBefore..latAfter)</c> bracket lerp on every
        /// late frame, discarding Phase 1's smoothing and Phase 4's frame
        /// transformation in the steady state.
        /// </para>
        /// </summary>
        private bool TryComputeLateUpdateSplineWorldPosition(in GhostPosEntry e, out Vector3d worldPos)
        {
            if (!allowSplinePositioningForPlayback(
                    false,
                    allowNormalPlaybackSplinePositioning: NormalPlaybackSplinePositioningEnabled,
                    out _))
            {
                worldPos = default(Vector3d);
                return false;
            }

            return TryComputeLateUpdateSplineWorldPositionPure(
                e.anchorRecordingId, e.anchorSectionIndex, e.pointUT, e.bodyBefore,
                unknownFrameTagWarned, out worldPos);
        }

        /// <summary>
        /// Pure-static core of <see cref="TryComputeLateUpdateSplineWorldPosition"/>.
        /// Returns <c>true</c> with a populated <paramref name="worldPos"/>
        /// when (a) the lookup key is valid (non-empty recordingId + non-
        /// negative sectionIndex), (b) <see cref="Parsek.Rendering.SectionAnnotationStore"/>
        /// holds a valid spline for the pair, (c) the body reference is non-
        /// null, and (d) the spline's FrameTag dispatches to a finite world
        /// position. Any miss falls through silently — the caller uses the
        /// legacy raw-lerp bracket. Unknown FrameTag values also return false;
        /// the Warn fires through <paramref name="unknownFrameTagWarnedKeys"/>
        /// so the Update path's prior Add suppresses a LateUpdate double-emit.
        ///
        /// <para>
        /// Tested via <c>LateUpdateSplineDispatchTests</c> — every gate's
        /// failure branch and both successful FrameTag paths are exercised
        /// without standing up a real <see cref="GhostPosEntry"/> or Unity's
        /// LateUpdate (which xUnit cannot drive).
        /// </para>
        /// </summary>
        internal static bool TryComputeLateUpdateSplineWorldPositionPure(
            string recordingId, int sectionIndex, double pointUT, CelestialBody body,
            HashSet<string> unknownFrameTagWarnedKeys, out Vector3d worldPos)
        {
            worldPos = default(Vector3d);
            if (string.IsNullOrEmpty(recordingId) || sectionIndex < 0)
                return false;

            if (!Parsek.Rendering.SectionAnnotationStore.TryGetSmoothingSpline(
                    recordingId, sectionIndex, out Parsek.Rendering.SmoothingSpline spline)
                || !spline.IsValid)
                return false;

            // ReferenceEquals to bypass Unity's overloaded `==`, which would
            // treat a TestBodyRegistry-built uninitialised CelestialBody as
            // null. Production callers route through FlightGlobals.Bodies; the
            // test suite delivers genuine CLR objects whose Unity-cached pointer
            // is zero. Same pattern as SmoothingPipeline.FitAndStorePerSection.
            if (object.ReferenceEquals(body, null))
                return false;

            Vector3d splineLatLonAlt = TrajectoryMath.CatmullRomFit.Evaluate(spline, pointUT);
            if (double.IsNaN(splineLatLonAlt.x) || double.IsNaN(splineLatLonAlt.y) || double.IsNaN(splineLatLonAlt.z))
                return false;

            Vector3d splineWorld = TrajectoryMath.FrameTransform.DispatchSplineWorldByFrameTag(
                spline.FrameTag,
                splineLatLonAlt.x, splineLatLonAlt.y, splineLatLonAlt.z,
                body, pointUT,
                recordingId, sectionIndex,
                unknownFrameTagWarnedKeys);
            if (double.IsNaN(splineWorld.x) || double.IsNaN(splineWorld.y) || double.IsNaN(splineWorld.z))
                return false;

            worldPos = splineWorld;
            RecordSplineEvalForLogging();
            return true;
        }

        /// <summary>
        /// Increment the per-second spline-eval counter; flush a Verbose
        /// summary line once per second when the counter is non-zero.
        /// Test seam <see cref="NowProviderForTesting"/> overrides the
        /// wall-clock for deterministic xUnit assertions.
        /// </summary>
        internal static void RecordSplineEvalForLogging()
        {
            int n = ++s_splineEvalCount;
            long now = NowProviderForTesting != null
                ? NowProviderForTesting().Ticks
                : DateTime.UtcNow.Ticks;
            if (s_splineEvalLogLastTicks == 0)
            {
                s_splineEvalLogLastTicks = now;
                return;
            }
            if (now - s_splineEvalLogLastTicks >= SplineEvalLogIntervalTicks)
            {
                double intervalSec = (now - s_splineEvalLogLastTicks) / (double)TimeSpan.TicksPerSecond;
                ParsekLog.Verbose("Pipeline-Smoothing",
                    string.Format(CultureInfo.InvariantCulture,
                        "frame summary: splineEvals={0} intervalSec={1:F2}", n, intervalSec));
                s_splineEvalCount = 0;
                s_splineEvalLogLastTicks = now;
            }
        }

        /// <summary>Test-only reset of the L4 summary state (counter + last-emit timestamp).</summary>
        internal static void ResetSplineEvalLoggingForTesting()
        {
            s_splineEvalCount = 0;
            s_splineEvalLogLastTicks = 0;
            NowProviderForTesting = null;
        }

        // Keep the old signature for backward compat with tests
        internal int FindWaypointIndex(double targetUT)
        {
            return TrajectoryMath.FindWaypointIndex(recording, ref lastPlaybackIndex, targetUT);
        }


        void PositionGhostAt(GameObject ghost, TrajectoryPoint point, string recordingId = null,
            bool altitudeAuthoritative = false)
        {
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == point.bodyName);
            if (body == null)
            {
                ParsekLog.VerboseRateLimited("Flight", "position-ghost-no-body",
                    $"PositionGhostAt: body '{point.bodyName}' not found — ghost position not updated");
                return;
            }

            // Phase 7 (design doc §13.1, §18 Phase 7): apply continuous terrain
            // correction when the recorded surface section captured a
            // ground clearance. Effective altitude = current_terrain + clearance.
            // NaN sentinel ⇒ legacy / non-surface point ⇒ fall through to
            // the recorded altitude (HR-9 silent fall-through). See
            // ResolvePhase7EffectiveAltitude for the per-frame cache lookup
            // and Pipeline-Terrain logging.
            //
            // PositionGhostAt only services Absolute-frame TrajectoryPoints
            // (RELATIVE-frame playback uses InterpolateAndPositionRelative,
            // OrbitalCheckpoint uses orbit-driven positioning) — so the
            // helper's referenceFrame argument is hard-coded Absolute. The
            // helper still NaN-fall-throughs on legacy/non-surface
            // points; the explicit Absolute argument is the P2-1 safety
            // gate against any future caller routing a Relative point here.
            //
            // altitudeAuthoritative=true short-circuits both Phase 7 and
            // tail-lift: the caller (e.g. PositionAtPoint after
            // ApplyLandedGhostClearance) has already produced the final
            // rendered altitude, and re-correcting it would double-apply Δ.
            double effectiveAltitude = altitudeAuthoritative
                ? point.altitude
                : ResolveEffectiveAltitudeWithTailLift(
                    body, point.latitude, point.longitude, point.altitude,
                    point.recordedGroundClearance, ReferenceFrame.Absolute,
                    point.ut, recordingId);

            // M4b body-fixed derotation (identity at shift 0): see InterpolateAndPosition.
            Vector3d worldPos = body.GetWorldSurfacePosition(
                point.latitude,
                TrajectoryMath.FrameTransform.ShiftLongitudeDegrees(
                    point.longitude, activeBodyFixedShiftSeconds, body),
                effectiveAltitude);

            Quaternion sanitized = TrajectoryMath.SanitizeQuaternion(point.rotation);

            ghost.transform.position = worldPos;
            ghost.transform.rotation = body.bodyTransform.rotation
                * TrajectoryMath.FrameTransform.ShiftSurfaceRelativeRotation(
                    sanitized, activeBodyFixedShiftSeconds, body);
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut;
            if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    recordingId,
                    -1,
                    traceCurrentUT,
                    point.ut,
                    "UpdatePath",
                    "mode=SinglePoint body=" + (point.bodyName ?? "<none>")
                    + " lat=" + point.latitude.ToString("F6", CultureInfo.InvariantCulture)
                    + " lon=" + point.longitude.ToString("F6", CultureInfo.InvariantCulture)
                    + " alt=" + effectiveAltitude.ToString("F2", CultureInfo.InvariantCulture)
                    + " altitudeAuthoritative=" + (altitudeAuthoritative ? "true" : "false")
                    + " rawWorld=" + GhostRenderTrace.FormatVector3d(worldPos)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghost.transform.position)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
            }

            // Register for LateUpdate re-positioning after FloatingOrigin shift
            AddOrReplaceGhostPosEntry(new GhostPosEntry
            {
                ghost = ghost,
                mode = GhostPosMode.SinglePoint,
                recordingId = recordingId,
                bodyBefore = body,
                latBefore = point.latitude, lonBefore = point.longitude, altBefore = effectiveAltitude,
                pointUT = point.ut,
                interpolatedRot = sanitized,
            });
        }

        // Delegates to TrajectoryMath — kept for backward compatibility
        internal static OrbitSegment? FindOrbitSegment(List<OrbitSegment> segments, double ut)
        {
            return TrajectoryMath.FindOrbitSegment(segments, ut);
        }

        private struct PredictedOrbitTailContinuityCacheEntry
        {
            public Vector3d rawOffset;
            public string anchorSource;
        }

        private void ClearOrbitPlaybackCaches()
        {
            orbitCache?.Clear();
            predictedOrbitTailContinuityCache?.Clear();
        }

        void PositionGhostFromOrbit(GameObject ghost, OrbitSegment segment, double ut, long cacheKey,
            string recordingId = null, IPlaybackTrajectory traj = null)
        {
            if (!TrajectoryMath.HasUsableOrbitSegmentElements(segment))
            {
                Log($"Orbit segment rejected: unusable elements, sma={segment.semiMajorAxis:F0}, " +
                    $"UT {segment.startUT:F0}-{segment.endUT:F0}");
                return;
            }

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == segment.bodyName);
            if (body == null)
            {
                Log($"Could not find body '{segment.bodyName}' for orbit playback");
                return;
            }

            Orbit orbit;
            if (!orbitCache.TryGetValue(cacheKey, out orbit))
            {
                try
                {
                    orbit = new Orbit(
                        segment.inclination,
                        segment.eccentricity,
                        segment.semiMajorAxis,
                        segment.longitudeOfAscendingNode,
                        segment.argumentOfPeriapsis,
                        segment.meanAnomalyAtEpoch,
                        segment.epoch,
                        body);
                }
                catch (Exception ex)
                {
                    Log($"Orbit segment construction failed: body={segment.bodyName}, " +
                        $"sma={segment.semiMajorAxis:F0}, error={ex.GetType().Name}");
                    return;
                }
                orbitCache[cacheKey] = orbit;
            }

            Vector3d rawOrbitWorldPos;
            try
            {
                rawOrbitWorldPos = orbit.getPositionAtUT(ut);
            }
            catch (Exception ex)
            {
                Log($"Orbit segment propagation failed: body={segment.bodyName}, " +
                    $"sma={segment.semiMajorAxis:F0}, error={ex.GetType().Name}");
                return;
            }
            if (!IsFiniteVector3d(rawOrbitWorldPos))
            {
                Log($"Orbit segment propagation produced non-finite position: body={segment.bodyName}, " +
                    $"sma={segment.semiMajorAxis:F0}");
                return;
            }

            Vector3d worldPos = rawOrbitWorldPos;
            Vector3d orbitContinuityOffset = Vector3d.zero;
            Vector3d rawOrbitContinuityOffset = Vector3d.zero;
            bool hasOrbitContinuityOffset = TryResolvePredictedOrbitTailContinuityOffset(
                traj,
                segment,
                orbit,
                ut,
                out orbitContinuityOffset,
                out rawOrbitContinuityOffset,
                out double orbitContinuityWeight,
                out double orbitContinuityAnchorUT,
                out double orbitContinuityBlendSeconds,
                out string orbitContinuityAnchorSource);
            if (hasOrbitContinuityOffset)
                worldPos += orbitContinuityOffset;
            if (!IsFiniteVector3d(worldPos))
            {
                Log($"Orbit segment continuity produced non-finite position: body={segment.bodyName}, " +
                    $"sma={segment.semiMajorAxis:F0}");
                return;
            }

            // Surface clamp: if the Keplerian orbit goes underground (e.g., deorbit
            // orbit with periapsis below surface, or impact trajectory on airless body),
            // clamp to surface altitude. Prevents the ghost mesh from tunneling through
            // the planet during orbit-only recording sections where no trajectory points exist.
            double orbitAlt;
            bool orbitTerrainClamped = false;
            try
            {
                orbitAlt = body.GetAltitude(worldPos);
                if (!IsFinite(orbitAlt))
                {
                    Log($"Orbit segment altitude was non-finite: body={segment.bodyName}, " +
                        $"sma={segment.semiMajorAxis:F0}");
                    return;
                }

                if (orbitAlt < 0)
                {
                    double lat = body.GetLatitude(worldPos);
                    double lon = body.GetLongitude(worldPos);
                    worldPos = body.GetWorldSurfacePosition(lat, lon, 0);
                    if (!IsFiniteVector3d(worldPos))
                    {
                        Log($"Orbit segment surface clamp produced non-finite position: body={segment.bodyName}, " +
                            $"sma={segment.semiMajorAxis:F0}");
                        return;
                    }
                    orbitTerrainClamped = true;
                }
            }
            catch (Exception ex)
            {
                Log($"Orbit segment body-position API failed: body={segment.bodyName}, " +
                    $"sma={segment.semiMajorAxis:F0}, error={ex.GetType().Name}");
                return;
            }

            ghost.transform.position = worldPos;

            Vector3d velocity;
            try
            {
                velocity = orbit.getOrbitalVelocityAtUT(ut);
            }
            catch
            {
                velocity = Vector3d.zero;
            }
            if (!IsFiniteVector3d(velocity))
                velocity = Vector3d.zero;

            // Orbital rotation: 3-way branch
            bool hasOfr = TrajectoryMath.HasOrbitalFrameRotation(segment);
            bool spinning = TrajectoryMath.IsSpinning(segment);

            var (ghostRot, boundaryWorldRot) = ComputeOrbitalRotation(
                segment, orbit, ut, velocity, worldPos, body.position,
                ghost.transform.rotation, cacheKey, hasOfr, spinning);

            ghost.transform.rotation = ghostRot;
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : ut;
            if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    recordingId,
                    -1,
                    traceCurrentUT,
                    ut,
                    "UpdatePath",
                    "mode=Orbit cacheKey=" + cacheKey.ToString(CultureInfo.InvariantCulture)
                    + " body=" + (segment.bodyName ?? "<none>")
                    + " segmentUT=[" + segment.startUT.ToString("F3", CultureInfo.InvariantCulture)
                    + "," + segment.endUT.ToString("F3", CultureInfo.InvariantCulture) + "]"
                    + " rawWorld=" + GhostRenderTrace.FormatVector3d(rawOrbitWorldPos)
                    + " continuityHit=" + (hasOrbitContinuityOffset ? "true" : "false")
                    + " continuityAnchorUT=" + FormatFiniteOrNa(orbitContinuityAnchorUT, "F3")
                    + " continuityBlendSeconds=" + FormatFiniteOrNa(orbitContinuityBlendSeconds, "F3")
                    + " continuityWeight=" + FormatFiniteOrNa(orbitContinuityWeight, "F3")
                    + " continuityAnchorSource=" + (orbitContinuityAnchorSource ?? "<none>")
                    + " continuityRawOffset=" + GhostRenderTrace.FormatVector3d(rawOrbitContinuityOffset)
                    + " continuityOffset=" + GhostRenderTrace.FormatVector3d(orbitContinuityOffset)
                    + " terrainClamp=" + (orbitTerrainClamped ? "true" : "false")
                    + " orbitAlt=" + orbitAlt.ToString("F2", CultureInfo.InvariantCulture)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghost.transform.position)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation)
                    + " rotationMode=" + (spinning ? "spin-forward" : hasOfr ? "orbital-frame" : "prograde"));
            }

            // First-activation logging (once per segment)
            if (!loggedOrbitRotationSegments.Contains(cacheKey))
            {
                loggedOrbitRotationSegments.Add(cacheKey);
                if (spinning)
                    ParsekLog.Verbose("Playback", $"Orbit segment {cacheKey}: spin-forward (|angVel|={segment.angularVelocity.magnitude:F4})");
                else if (hasOfr)
                    ParsekLog.Verbose("Playback", $"Orbit segment {cacheKey}: orbital-frame rotation");
                else
                    ParsekLog.Verbose("Playback", $"Orbit segment {cacheKey}: velocity-derived prograde (no ofr data)");
            }

            // Register for LateUpdate re-positioning after FloatingOrigin shift
            AddOrReplaceGhostPosEntry(new GhostPosEntry
            {
                ghost = ghost,
                mode = GhostPosMode.Orbit,
                recordingId = recordingId,
                orbitCacheKey = cacheKey,
                orbitUT = ut,
                orbitFrameRot = segment.orbitalFrameRotation,
                hasOrbitFrameRot = hasOfr,
                orbitBody = body,
                orbitAngularVelocity = segment.angularVelocity,
                isSpinning = spinning,
                orbitSegmentStartUT = segment.startUT,
                boundaryWorldRot = boundaryWorldRot,
                orbitContinuityOffset = rawOrbitContinuityOffset,
                hasOrbitContinuityOffset = hasOrbitContinuityOffset,
                orbitContinuityAnchorUT = orbitContinuityAnchorUT,
                orbitContinuityBlendSeconds = orbitContinuityBlendSeconds,
            });
        }

        private static Vector3d ResolveOrbitContinuityOffset(GhostPosEntry entry, double playbackUT)
        {
            if (!entry.hasOrbitContinuityOffset)
                return Vector3d.zero;

            double weight = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityWeight(
                entry.orbitContinuityAnchorUT,
                playbackUT,
                entry.orbitContinuityBlendSeconds);
            if (weight <= 1e-6)
                return Vector3d.zero;

            Vector3d offset = entry.orbitContinuityOffset * weight;
            return IsFiniteVector3d(offset) ? offset : Vector3d.zero;
        }

        private bool TryResolvePredictedOrbitTailContinuityOffset(
            IPlaybackTrajectory traj,
            OrbitSegment segment,
            Orbit orbit,
            double playbackUT,
            out Vector3d weightedOffset,
            out Vector3d rawOffset,
            out double weight,
            out double anchorUT,
            out double blendSeconds,
            out string anchorSource)
        {
            weightedOffset = Vector3d.zero;
            rawOffset = Vector3d.zero;
            weight = 0.0;
            anchorUT = double.NaN;
            blendSeconds = double.NaN;
            anchorSource = null;

            if (!ShouldApplyPredictedOrbitTailContinuity(traj, segment, playbackUT))
                return false;

            anchorUT = traj.Points[traj.Points.Count - 1].ut;
            blendSeconds = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityBlendSeconds(
                anchorUT, segment.startUT);
            weight = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityWeight(
                anchorUT, playbackUT, blendSeconds);
            if (weight <= 1e-6)
                return false;

            string cacheKey = BuildPredictedOrbitTailContinuityCacheKey(traj, segment, anchorUT);
            bool cacheHit = false;
            if (predictedOrbitTailContinuityCache.TryGetValue(
                    cacheKey, out PredictedOrbitTailContinuityCacheEntry cacheEntry)
                && IsFiniteVector3d(cacheEntry.rawOffset))
            {
                rawOffset = cacheEntry.rawOffset;
                anchorSource = cacheEntry.anchorSource;
                cacheHit = true;
            }
            else
            {
                if (!TryResolvePredictedOrbitTailContinuityAnchorWorldPosition(
                        traj, out Vector3d anchorWorld, out double resolvedAnchorUT, out anchorSource))
                {
                    return false;
                }

                if (Math.Abs(resolvedAnchorUT - anchorUT) > 1e-6)
                {
                    anchorUT = resolvedAnchorUT;
                    blendSeconds = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityBlendSeconds(
                        anchorUT, segment.startUT);
                    weight = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityWeight(
                        anchorUT, playbackUT, blendSeconds);
                    if (weight <= 1e-6)
                        return false;
                    cacheKey = BuildPredictedOrbitTailContinuityCacheKey(traj, segment, anchorUT);
                }

                Vector3d orbitAtAnchor = orbit.getPositionAtUT(anchorUT);
                rawOffset = anchorWorld - orbitAtAnchor;
                if (!IsFiniteVector3d(rawOffset))
                    return false;

                double candidateRawMeters = rawOffset.magnitude;
                if (candidateRawMeters <= 0.01)
                    return false;

                predictedOrbitTailContinuityCache[cacheKey] =
                    new PredictedOrbitTailContinuityCacheEntry
                    {
                        rawOffset = rawOffset,
                        anchorSource = anchorSource
                    };
            }

            weightedOffset = rawOffset * weight;
            if (!IsFiniteVector3d(weightedOffset))
                return false;

            double rawMeters = rawOffset.magnitude;
            if (rawMeters <= 0.01)
                return false;

            ParsekLog.VerboseRateLimited(
                "Playback",
                "orbit-tail-continuity|" + (traj.RecordingId ?? "<no-rec>"),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Predicted orbit-tail continuity: rec={0} vessel=\"{1}\" playbackUT={2:F3} anchorUT={3:F3} segmentUT=[{4:F3},{5:F3}] source={6} rawOffset={7} rawMeters={8:F2} weight={9:F3} blendSeconds={10:F3} appliedOffset={11}",
                    ShortRecordingId(traj.RecordingId),
                    traj.VesselName ?? "?",
                    playbackUT,
                    anchorUT,
                    segment.startUT,
                    segment.endUT,
                    anchorSource ?? "<none>",
                    GhostRenderTrace.FormatVector3d(rawOffset),
                    rawMeters,
                    weight,
                    blendSeconds,
                    GhostRenderTrace.FormatVector3d(weightedOffset)
                    + " cache=" + (cacheHit ? "hit" : "miss")),
                5.0);
            return true;
        }

        private static string BuildPredictedOrbitTailContinuityCacheKey(
            IPlaybackTrajectory traj,
            OrbitSegment segment,
            double anchorUT)
        {
            string recordingId = traj?.RecordingId;
            if (string.IsNullOrEmpty(recordingId))
                recordingId = traj?.VesselName ?? "<no-recording-id>";

            return string.Join("|",
                recordingId,
                segment.bodyName ?? "<no-body>",
                anchorUT.ToString("R", CultureInfo.InvariantCulture),
                segment.startUT.ToString("R", CultureInfo.InvariantCulture),
                segment.endUT.ToString("R", CultureInfo.InvariantCulture),
                segment.epoch.ToString("R", CultureInfo.InvariantCulture),
                segment.semiMajorAxis.ToString("R", CultureInfo.InvariantCulture),
                segment.eccentricity.ToString("R", CultureInfo.InvariantCulture),
                segment.inclination.ToString("R", CultureInfo.InvariantCulture));
        }

        private static bool ShouldApplyPredictedOrbitTailContinuity(
            IPlaybackTrajectory traj, OrbitSegment segment, double playbackUT)
        {
            if (traj?.Points == null || traj.Points.Count == 0)
                return false;
            if (!segment.isPredicted)
                return false;

            double lastPointUT = traj.Points[traj.Points.Count - 1].ut;
            if (playbackUT <= lastPointUT + 1e-6)
                return false;

            double blendSeconds = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityBlendSeconds(
                lastPointUT, segment.startUT);
            return playbackUT <= lastPointUT + blendSeconds + 1e-6;
        }

        private bool TryResolvePredictedOrbitTailContinuityAnchorWorldPosition(
            IPlaybackTrajectory traj,
            out Vector3d anchorWorld,
            out double anchorUT,
            out string source)
        {
            anchorWorld = Vector3d.zero;
            anchorUT = double.NaN;
            source = null;
            if (traj?.Points == null || traj.Points.Count == 0)
                return false;

            TrajectoryPoint lastPoint = traj.Points[traj.Points.Count - 1];
            anchorUT = lastPoint.ut;

            if (traj.TrackSections != null && traj.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, anchorUT);
                bool exactSection = sectionIdx >= 0;
                if (sectionIdx < 0)
                    sectionIdx = FindNearestTrackSectionIndexForContinuityAnchor(
                        traj.TrackSections, anchorUT);

                if (sectionIdx >= 0 && sectionIdx < traj.TrackSections.Count)
                {
                    TrackSection section = traj.TrackSections[sectionIdx];
                    if (section.referenceFrame == ReferenceFrame.Relative)
                    {
                        if (BodyFixedPrimaryCoversPlaybackUT(
                                section, anchorUT, out _, out _))
                        {
                            int absoluteCachedIndex = 0;
                            if (TryResolvePointWorldPosition(
                                    section.bodyFixedFrames,
                                    ref absoluteCachedIndex,
                                    anchorUT,
                                    out anchorWorld))
                            {
                                source = "relative-body-fixed-primary";
                                return true;
                            }
                        }

                        if (exactSection)
                            return false;
                    }

                    if (section.referenceFrame == ReferenceFrame.Absolute
                        && TryGetAbsoluteSectionPlaybackFramesForPlayback(
                            traj.TrackSections,
                            sectionIdx,
                            out List<TrajectoryPoint> bodyFixedFrames,
                            out _))
                    {
                        int absoluteCachedIndex = 0;
                        if (TryResolvePointWorldPosition(
                                bodyFixedFrames,
                                ref absoluteCachedIndex,
                                anchorUT,
                                out anchorWorld))
                        {
                            source = "absolute-section";
                            return true;
                        }
                    }

                    if (exactSection && section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                        return false;
                }
            }

            if (TryResolveTrajectoryPointWorldPosition(lastPoint, out anchorWorld))
            {
                source = "flat-point";
                return true;
            }

            return false;
        }

        private static int FindNearestTrackSectionIndexForContinuityAnchor(
            List<TrackSection> trackSections, double anchorUT)
        {
            if (trackSections == null || trackSections.Count == 0)
                return -1;

            if (anchorUT < trackSections[0].startUT)
                return 0;
            for (int i = trackSections.Count - 1; i >= 0; i--)
            {
                TrackSection section = trackSections[i];
                if (anchorUT >= section.startUT - 1e-6)
                    return i;
            }
            return trackSections.Count - 1;
        }

        private static string FormatFiniteOrNa(double value, string format)
        {
            return IsFinite(value)
                ? value.ToString(format, CultureInfo.InvariantCulture)
                : "n/a";
        }

        /// <summary>
        /// Decodes an <see cref="OrbitSegment"/>'s stored <c>orbitalFrameRotation</c> into a world
        /// attitude for playback (and, on the spinning path, spins it forward from the boundary).
        /// <para>
        /// FRAME CONTRACT — WORLD forward axis + WORLD radial up axis, on every branch. The
        /// orbital frame is <c>LookRotation(orbitalVelocity_WORLD, radialOut_WORLD)</c>, which is
        /// exactly the frame every producer encodes in: the recorder
        /// (<c>FlightRecorder.CreateOrbitSegmentWithRotation</c> and the SOI-change capture, both
        /// <c>v.obt_velocity</c> + <c>v.CoMD - v.mainBody.position</c>), the scene-exit finalizer
        /// (<c>IncompleteBallisticSceneExitFinalizer.SeedPredictedSegmentOrbitalFrameRotations</c>)
        /// and the extrapolator's SOI reframe
        /// (<c>BallisticExtrapolator.ReframeOrbitalFrameRotation</c>). See the contract banner on
        /// <see cref="TrajectoryMath.SwizzleZupBodyRelativeToWorld"/>.
        /// </para>
        /// <para>
        /// PARAMETER CONTRACT, deliberately NOT the same thing: <paramref name="velocity"/> stays
        /// ZUP-SWIZZLED BODY-RELATIVE — i.e. straight off <c>Orbit.getOrbitalVelocityAtUT</c>,
        /// which is what all SEVEN production call sites and both H9 frame probes already hold —
        /// and is converted to world HERE, once, per branch. <paramref name="worldPos"/> and
        /// <paramref name="bodyPosition"/> are Y-up WORLD (<c>Orbit.getPositionAtUT</c> already
        /// unswizzles; <c>CelestialBody.position</c> is world), so the radial needs no conversion.
        /// Converting inside is one conversion point instead of nine, and it keeps the awkward
        /// half of KSP's API — a velocity getter whose frame does not match its position getter —
        /// behind one seam.
        /// </para>
        /// <para>
        /// Until branch <c>orbital-rotation-frame</c> this built <c>LookRotation(zupVelocity,
        /// worldRadial)</c>: one frame out of two frames, the FIFTH frame mismatch of
        /// docs/dev/todo-and-known-bugs.md. Every recorder-authored segment — the bulk of the
        /// recorded population — was decoded against a forward axis in the wrong frame.
        /// DO NOT remove these swizzles without moving every producer in lockstep.
        /// </para>
        /// </summary>
        internal static (Quaternion ghostRot, Quaternion boundaryWorldRot) ComputeOrbitalRotation(
            OrbitSegment segment, Orbit orbit, double ut, Vector3d velocity, Vector3d worldPos,
            Vector3d bodyPosition, Quaternion currentRotation, long cacheKey, bool hasOfr, bool spinning)
        {
            Quaternion ghostRot = currentRotation; // preserve current if no update
            Quaternion boundaryWorldRot = Quaternion.identity;

            if (spinning)
            {
                // Spin-forward path: reconstruct boundary world rotation from orbit at startUT.
                // getOrbitalVelocityAtUT is Zup body-relative; getPositionAtUT is already world.
                Vector3d velAtStart = TrajectoryMath.SwizzleZupBodyRelativeToWorld(
                    orbit.getOrbitalVelocityAtUT(segment.startUT));
                Vector3d posAtStart = orbit.getPositionAtUT(segment.startUT);
                Vector3d radialAtStart = (posAtStart - bodyPosition).normalized;

                Quaternion orbFrameAtStart = SafeOrbitalLookRotation(
                    velAtStart, radialAtStart, cacheKey, "start");

                boundaryWorldRot = orbFrameAtStart * segment.orbitalFrameRotation;

                double dt = ut - segment.startUT;
                Vector3 worldAxis = boundaryWorldRot * segment.angularVelocity;
                float angle = (float)((double)segment.angularVelocity.magnitude * dt * Mathf.Rad2Deg);
                ghostRot = Quaternion.AngleAxis(angle, worldAxis) * boundaryWorldRot;
            }
            else if (hasOfr && velocity.sqrMagnitude > 0.001)
            {
                // Orbital-frame-relative path. The swizzle preserves magnitude, so the
                // degenerate-velocity gate above reads the same either side of it.
                Vector3d worldVelocity = TrajectoryMath.SwizzleZupBodyRelativeToWorld(velocity);
                Vector3d radialOut = (worldPos - bodyPosition).normalized;

                Quaternion orbFrame = SafeOrbitalLookRotation(
                    worldVelocity, radialOut, cacheKey, null);

                ghostRot = orbFrame * segment.orbitalFrameRotation;
            }
            else if (velocity.sqrMagnitude > 0.001)
            {
                // Prograde fallback (old recordings). No stored rotation to decode, but the
                // ghost is still being oriented IN WORLD SPACE, so the Zup velocity has to be
                // lifted just the same — pointing a world transform along a Zup axis is the
                // same mismatch with one of the two halves missing.
                ghostRot = Quaternion.LookRotation(
                    (Vector3)TrajectoryMath.SwizzleZupBodyRelativeToWorld(velocity));
            }

            return (ghostRot, boundaryWorldRot);
        }

        /// <summary>
        /// Computes Quaternion.LookRotation(velocity, radialOut) with a fallback to Vector3.up
        /// when velocity and radialOut are nearly parallel (dot > 0.99). Logs rate-limited
        /// diagnostic when the fallback is used. Pass suffix "start" for startUT context or
        /// null for current-time context.
        /// <para>
        /// BOTH arguments are Y-up WORLD (see <see cref="ComputeOrbitalRotation"/>'s frame
        /// contract). That also makes the near-parallel test mean what it reads as: the dot of a
        /// world velocity with a world radial is the sine of the flight-path angle, so the
        /// fallback now fires on genuinely radial motion rather than on a cross-frame accident.
        /// </para>
        /// </summary>
        private static Quaternion SafeOrbitalLookRotation(
            Vector3 velocity, Vector3 radialOut, long cacheKey, string suffix)
        {
            if (Mathf.Abs(Vector3.Dot(velocity.normalized, radialOut.normalized)) > 0.99f)
            {
                string tagSuffix = suffix != null ? $"-{suffix}" : "";
                string msgSuffix = suffix != null ? $" at {suffix}UT" : "";
                ParsekLog.VerboseRateLimited("Playback", $"orbit-near-parallel{tagSuffix}-{cacheKey}",
                    $"Orbit segment {cacheKey}: velocity/radialOut near-parallel{msgSuffix}, LookRotation fallback");
                return Quaternion.LookRotation(velocity, Vector3.up);
            }
            return Quaternion.LookRotation(velocity, radialOut);
        }

        /// <summary>
        /// Positions a ghost using only orbit segments (no trajectory points).
        /// Used for background-only recordings (vessels that stayed on rails).
        /// </summary>
        void PositionGhostFromOrbitOnly(GameObject ghost, Recording rec, double ut, int orbitCacheBase,
            bool allowActivation)
        {
            for (int s = 0; s < rec.OrbitSegments.Count; s++)
            {
                var seg = rec.OrbitSegments[s];
                if (ut >= seg.startUT && ut <= seg.endUT)
                {
                    if (!TrajectoryMath.HasUsableOrbitSegmentElements(seg))
                        continue;

                    int cacheKey = orbitCacheBase + s;
                    if (loggedOrbitSegments.Add(cacheKey))
                        Log($"Orbit-only segment activated: cache={cacheKey}, body={seg.bodyName}, " +
                            $"sma={seg.semiMajorAxis:F0}, UT {seg.startUT:F0}-{seg.endUT:F0}");
                    PositionGhostFromOrbit(ghost, seg, ut, cacheKey, rec?.RecordingId, rec);
                    if (allowActivation)
                        ghost.SetActive(true);
                    return;
                }
            }
            // UT falls in a gap between orbit segments — hide ghost
            ghost.SetActive(false);
        }

        /// <summary>
        /// Positions a ghost at a fixed surface position (body, lat, lon, alt, rotation).
        /// Used for background vessels that are landed/splashed.
        /// </summary>
        void PositionGhostAtSurface(GameObject ghost, SurfacePosition surfPos, bool allowActivation,
            string recordingId = null, double pointUT = double.NaN)
        {
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == surfPos.body);
            if (body == null)
            {
                ParsekLog.Warn("Flight", $"PositionGhostAtSurface: body '{surfPos.body}' not found");
                ghost.SetActive(false);
                return;
            }
            ghost.transform.position = body.GetWorldSurfacePosition(surfPos.latitude, surfPos.longitude, surfPos.altitude);
            ghost.transform.rotation = body.bodyTransform.rotation * surfPos.rotation;
            double tracePlaybackUT = double.IsNaN(pointUT) && Planetarium.fetch != null
                ? Planetarium.GetUniversalTime()
                : pointUT;
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : tracePlaybackUT;
            if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    recordingId,
                    -1,
                    traceCurrentUT,
                    tracePlaybackUT,
                    "UpdatePath",
                    "mode=Surface body=" + (surfPos.body ?? "<none>")
                    + " lat=" + surfPos.latitude.ToString("F6", CultureInfo.InvariantCulture)
                    + " lon=" + surfPos.longitude.ToString("F6", CultureInfo.InvariantCulture)
                    + " alt=" + surfPos.altitude.ToString("F2", CultureInfo.InvariantCulture)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghost.transform.position)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
            }
            if (allowActivation)
                ghost.SetActive(true);

            // Register for LateUpdate re-positioning after FloatingOrigin shift
            AddOrReplaceGhostPosEntry(new GhostPosEntry
            {
                ghost = ghost,
                mode = GhostPosMode.Surface,
                recordingId = recordingId,
                bodyBefore = body,
                latBefore = surfPos.latitude, lonBefore = surfPos.longitude, altBefore = surfPos.altitude,
                surfaceRot = surfPos.rotation,
            });

            ParsekLog.VerboseRateLimited("Flight", "surface-ghost-positioned",
                $"PositionGhostAtSurface: body={surfPos.body} lat={surfPos.latitude:F4} lon={surfPos.longitude:F4} alt={surfPos.altitude:F1}");
        }

        internal static bool TryResolvePlaybackDistanceReferencePosition(
            Vector3d? cameraAnchorWorldPosition,
            Vector3d? cameraWorldPosition,
            out Vector3d referencePosition)
        {
            referencePosition = Vector3d.zero;

            // Center the ghost render-distance LOD radius on the camera ANCHOR — the
            // real vessel in normal flight, the watched ghost in watch mode — not on the
            // free-floating camera transform. Zooming the camera out moves the camera far
            // from the anchored object; using camera-to-ghost distance then culls ghost FX
            // (plume/smoke) even though the ghost is still close to the anchor, and zooming
            // back in does not reliably restore them. Anchor-to-ghost distance is
            // zoom-invariant, so all ghosts within the LOD radius of the followed object
            // render regardless of camera zoom.
            if (cameraAnchorWorldPosition.HasValue
                && IsFiniteVector3d(cameraAnchorWorldPosition.Value))
            {
                referencePosition = cameraAnchorWorldPosition.Value;
                return true;
            }

            // Fallback only: the anchor is unavailable (no active vessel and not watching
            // a resolvable ghost). The camera transform is the last resort so the distance
            // is still finite rather than NaN.
            if (cameraWorldPosition.HasValue
                && IsFiniteVector3d(cameraWorldPosition.Value))
            {
                referencePosition = cameraWorldPosition.Value;
                return true;
            }

            return false;
        }

        private static bool IsFiniteVector3d(Vector3d value)
        {
            return !double.IsNaN(value.x) && !double.IsInfinity(value.x)
                && !double.IsNaN(value.y) && !double.IsInfinity(value.y)
                && !double.IsNaN(value.z) && !double.IsInfinity(value.z);
        }

        double ResolvePlaybackDistanceForEngine(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            Camera sceneCamera = FlightCamera.fetch?.mainCamera;
            Vector3d? cameraWorldPosition = sceneCamera != null
                ? (Vector3d?)sceneCamera.transform.position
                : null;
            Vector3d referencePosition;
            if (!TryResolvePlaybackDistanceReferencePosition(
                    ResolveCameraAnchorWorldPosition(),
                    cameraWorldPosition,
                    out referencePosition))
            {
                return double.NaN;
            }

            return ResolvePlaybackDistanceFromReferencePosition(
                index, traj, state, playbackUT, referencePosition);
        }

        /// <summary>
        /// World position the flight camera is anchored on: the watched ghost while in watch
        /// mode, otherwise the active (real) vessel. The ghost render-distance LOD radius is
        /// centered here so zooming the camera in/out does not toggle ghost FX (see
        /// <see cref="TryResolvePlaybackDistanceReferencePosition"/>). A non-finite watched-ghost
        /// anchor falls through to the active vessel; an unavailable active vessel returns null.
        /// The caller treats null or a non-finite result as "no anchor" and falls back to the
        /// camera, so the active-vessel branch is intentionally not finiteness-checked here.
        /// </summary>
        Vector3d? ResolveCameraAnchorWorldPosition()
        {
            if (watchMode != null
                && watchMode.IsWatchingGhost
                && watchMode.TryGetWatchedGhostAnchorWorldPosition(out Vector3d watchedAnchor)
                && IsFiniteVector3d(watchedAnchor))
            {
                return watchedAnchor;
            }

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            Transform activeTransform = activeVessel != null ? activeVessel.transform : null;
            if (activeTransform != null)
                return (Vector3d)activeTransform.position;

            return null;
        }

        double ResolvePlaybackActiveVesselDistanceForEngine(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            Vector3d? activeVesselWorldPosition = FlightGlobals.ActiveVessel != null
                ? (Vector3d?)FlightGlobals.ActiveVessel.transform.position
                : null;
            if (!activeVesselWorldPosition.HasValue
                || !IsFiniteVector3d(activeVesselWorldPosition.Value))
            {
                return ResolvePlaybackDistanceForEngine(index, traj, state, playbackUT);
            }

            return ResolvePlaybackDistanceFromReferencePosition(
                index, traj, state, playbackUT, activeVesselWorldPosition.Value);
        }

        double ResolvePlaybackDistanceFromReferencePosition(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, Vector3d referencePosition)
        {
            Vector3d worldPos;
            if (TryResolvePlaybackWorldPosition(index, traj, state, playbackUT, out worldPos))
            {
                return Vector3d.Distance(worldPos, referencePosition);
            }

            return ResolveUnresolvedPlaybackDistanceFallback(
                traj, state, playbackUT, referencePosition);
        }

        internal static double ResolveUnresolvedPlaybackDistanceFallback(
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            Vector3d referencePosition)
        {
            double? relativeDistance = ResolveUnresolvedRelativeSectionDistanceFallback(
                traj, playbackUT);
            if (relativeDistance.HasValue)
                return relativeDistance.Value;

            if (state != null && state.ghost != null)
            {
                return Vector3d.Distance(
                    (Vector3d)state.ghost.transform.position,
                    referencePosition);
            }

            return double.NaN;
        }

        internal static double? ResolveUnresolvedRelativeSectionDistanceFallback(
            IPlaybackTrajectory traj,
            double playbackUT)
        {
            return IsRelativeSectionAtUT(traj, playbackUT)
                ? (double?)double.MaxValue
                : null;
        }

        private static bool IsRelativeSectionAtUT(IPlaybackTrajectory traj, double playbackUT)
        {
            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
                return false;
            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
            return sectionIdx >= 0
                && traj.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative;
        }

        bool TryInterpolateAndPositionCheckpointSection(
            int recordingIndex,
            IPlaybackTrajectory traj,
            GameObject ghost,
            ref int playbackIdx,
            double playbackUT,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            interpResult = InterpolationResult.Zero;
            if (!TryGetCheckpointTrackSection(traj, playbackUT, out int sectionIdx, out TrackSection section))
                return false;

            if (!TryInterpolateAndPositionCheckpointSectionWithOrbitRotation(
                    recordingIndex,
                    sectionIdx,
                    section,
                    ghost,
                    ref playbackIdx,
                    playbackUT,
                    allowActivation,
                    recordingId: traj?.RecordingId,
                    out interpResult))
            {
                // Phase 1: pass section context. Checkpoint sections are
                // OrbitalCheckpoint reference frame, which ShouldFitSection
                // filters out — the spline gate will naturally fall through
                // to legacy lerp here. Threaded so a future spline-on-
                // checkpoint extension wouldn't need to revisit the call site.
                InterpolateAndPosition(
                    ghost,
                    section.frames,
                    ref playbackIdx,
                    playbackUT,
                    allowActivation,
                    out interpResult,
                    recordingId: traj?.RecordingId,
                    sectionIndex: sectionIdx);
            }

            int logIndex = playbackIdx;
            if (TryResolvePointWorldPosition(section.frames, ref logIndex, playbackUT, out Vector3d worldPos))
                LogCheckpointPointPlayback(traj, sectionIdx, section, playbackUT, worldPos);

            return true;
        }

        bool TryInterpolateAndPositionCheckpointSectionWithOrbitRotation(
            int recordingIndex,
            int sectionIdx,
            TrackSection section,
            GameObject ghost,
            ref int playbackIdx,
            double playbackUT,
            bool allowActivation,
            string recordingId,
            out InterpolationResult interpResult)
        {
            interpResult = InterpolationResult.Zero;
            if (!TryFindCheckpointOrbitSegment(section, playbackUT, out OrbitSegment segment, out int checkpointIdx))
                return false;

            long cacheKey = BuildCheckpointOrbitCacheKey(recordingIndex, sectionIdx, checkpointIdx);
            if (!TryGetOrbitForSegment(segment, cacheKey, out Orbit orbit, out CelestialBody orbitBody))
                return false;

            bool hasCheckpointFrames = section.frames != null && section.frames.Count > 0;
            if (!hasCheckpointFrames)
            {
                if (allowActivation && !ghost.activeSelf)
                    ghost.SetActive(true);

                PositionGhostFromOrbit(ghost, segment, playbackUT, cacheKey, recordingId);

                Vector3d orbitWorldPos = orbit.getPositionAtUT(playbackUT);
                Vector3d orbitVelocity = orbit.getOrbitalVelocityAtUT(playbackUT);
                double orbitAltitude = orbitBody.GetAltitude(orbitWorldPos);
                interpResult = new InterpolationResult(
                    (Vector3)orbitVelocity,
                    segment.bodyName,
                    orbitAltitude).WithSurfaceVelocity(
                        GhostSurfaceVelocity.FromOrbitVelocity(
                            orbitVelocity, orbitBody.getRFrmVel(orbitWorldPos)),
                        GhostSurfaceVelocitySource.Orbit);

                string recId = string.IsNullOrEmpty(recordingId) ? "(null)" : recordingId;
                ParsekLog.VerboseRateLimited(
                    "Playback",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "orbital-checkpoint-orbit-only-{0}-{1}-{2}",
                        recId,
                        sectionIdx,
                        checkpointIdx),
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "OrbitalCheckpoint orbit-only playback: rec={0} currentUT={1:F1} section[{2}] sectionUT={3:F1}-{4:F1} checkpointIdx={5} segmentUT={6:F1}-{7:F1} world={8} alt={9:F1}",
                        recId,
                        playbackUT,
                        sectionIdx,
                        section.startUT,
                        section.endUT,
                        checkpointIdx,
                        segment.startUT,
                        segment.endUT,
                        GhostRenderTrace.FormatVector3d(orbitWorldPos),
                        orbitAltitude),
                    5.0);
                return true;
            }

            bool hasPointInterpolation = TryResolveCheckpointPointInterpolation(
                section.frames,
                ref playbackIdx,
                playbackUT,
                out TrajectoryPoint before,
                out TrajectoryPoint after,
                out float t);
            if (!hasPointInterpolation)
            {
                if (section.frames == null || section.frames.Count == 0)
                {
                    ghost.SetActive(false);
                    return true;
                }

                before = section.frames[0];
                after = before;
                t = 0f;
            }

            CelestialBody bodyBefore = FlightGlobals.Bodies?.Find(b => b.name == before.bodyName);
            CelestialBody bodyAfter = FlightGlobals.Bodies?.Find(b => b.name == after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
                return false;

            if (allowActivation && !ghost.activeSelf)
                ghost.SetActive(true);

            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);
            Vector3d interpolatedPos = Vector3d.Lerp(posBefore, posAfter, t);
            if (double.IsNaN(interpolatedPos.x) || double.IsNaN(interpolatedPos.y) || double.IsNaN(interpolatedPos.z))
                interpolatedPos = posBefore;

            // §7.7 BubbleEntry/Exit (and any other §7.x candidate landing on
            // a Checkpoint section): apply the rigid-translation ε on top of
            // the Kepler-bracketed lerp before assigning the transform.
            // Without this, the ε computed by AnchorPropagator and stored in
            // RenderSessionState would be invisible — the spline / lerp paths
            // already query allowAnchorCorrectionInterval, but the checkpoint
            // path used to skip it because the original §7.5 OrbitalCheckpoint
            // anchor lands on the ABSOLUTE neighbour rather than the
            // Checkpoint section itself; §7.7 changed that, and so did the
            // Phase 6 anchor-bound resolver work that produces ε at the
            // Checkpoint side. Same lookup as the PointInterp path uses
            // (Phase 2/3 ε, additive in world-space, HR-15 frozen-once).
            bool hasAnchorEps = allowRenderAnchorCorrectionInterval(
                recordingId, sectionIdx, playbackUT, false,
                out Vector3d anchorEps, out string anchorCorrectionReason);
            if (hasAnchorEps)
                interpolatedPos += anchorEps;

            Vector3d velocity = orbit.getOrbitalVelocityAtUT(playbackUT);
            bool hasOfr = TrajectoryMath.HasOrbitalFrameRotation(segment);
            bool spinning = TrajectoryMath.IsSpinning(segment);
            var (ghostRot, _) = ComputeOrbitalRotation(
                segment,
                orbit,
                playbackUT,
                velocity,
                interpolatedPos,
                orbitBody.position,
                ghost.transform.rotation,
                cacheKey,
                hasOfr,
                spinning);

            ghost.transform.position = interpolatedPos;
            ghost.transform.rotation = ghostRot;
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : playbackUT;
            if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    recordingId,
                    recordingIndex,
                    traceCurrentUT,
                    playbackUT,
                    "UpdatePath",
                    "mode=CheckpointPoint sectionIndex=" + sectionIdx.ToString(CultureInfo.InvariantCulture)
                    + " cacheKey=" + cacheKey.ToString(CultureInfo.InvariantCulture)
                    + " beforeUT=" + before.ut.ToString("F3", CultureInfo.InvariantCulture)
                    + " afterUT=" + after.ut.ToString("F3", CultureInfo.InvariantCulture)
                    + " t=" + t.ToString("F4", CultureInfo.InvariantCulture)
                    + " rawBefore=" + GhostRenderTrace.FormatVector3d(posBefore)
                    + " rawAfter=" + GhostRenderTrace.FormatVector3d(posAfter)
                    + " anchorCorrectionHit=" + (hasAnchorEps ? "true" : "false")
                    + " anchorCorrectionReason=" + anchorCorrectionReason
                    + " anchorEps=" + GhostRenderTrace.FormatVector3d(hasAnchorEps ? anchorEps : Vector3d.zero)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghost.transform.position)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
            }

            Quaternion pointRot = Quaternion.Slerp(before.rotation, after.rotation, t);
            pointRot = TrajectoryMath.SanitizeQuaternion(pointRot);
            AddOrReplaceGhostPosEntry(new GhostPosEntry
            {
                ghost = ghost,
                mode = GhostPosMode.CheckpointPoint,
                recordingId = recordingId,
                recordingIndex = recordingIndex,
                hasRecordingIndex = true,
                bodyBefore = bodyBefore,
                bodyAfter = bodyAfter,
                latBefore = before.latitude,
                lonBefore = before.longitude,
                altBefore = before.altitude,
                latAfter = after.latitude,
                lonAfter = after.longitude,
                altAfter = after.altitude,
                t = t,
                pointUT = playbackUT,
                interpolatedRot = pointRot,
                orbitCacheKey = cacheKey,
                orbitUT = playbackUT,
                orbitFrameRot = segment.orbitalFrameRotation,
                hasOrbitFrameRot = hasOfr,
                orbitBody = orbitBody,
                orbitAngularVelocity = segment.angularVelocity,
                isSpinning = spinning,
                orbitSegmentStartUT = segment.startUT,
                // §7.7 anchor lookup keys for LateUpdate re-application.
                // Without these, the LateUpdate FloatingOrigin shift
                // would replay the bare lerp position and the rendered
                // ghost would drop the anchor correction every late frame.
                anchorRecordingId = recordingId,
                anchorSectionIndex = sectionIdx,
            });

            interpResult = new InterpolationResult(
                (Vector3)velocity,
                segment.bodyName,
                TrajectoryMath.InterpolateAltitude(before.altitude, after.altitude, t));
            if (hasPointInterpolation
                && ReferenceEquals(bodyBefore, bodyAfter)
                && GhostSurfaceVelocity.TryFromBracketPositions(
                    posBefore, posAfter, after.ut - before.ut, out Vector3 checkpointSurfaceVelocity))
            {
                interpResult = interpResult.WithSurfaceVelocity(
                    checkpointSurfaceVelocity, GhostSurfaceVelocitySource.Positions);
            }
            return true;
        }

        private bool TryGetOrbitForSegment(
            OrbitSegment segment,
            long cacheKey,
            out Orbit orbit,
            out CelestialBody body)
        {
            orbit = null;
            body = null;
            if (!TrajectoryMath.HasUsableOrbitSegmentElements(segment))
                return false;

            body = FlightGlobals.Bodies?.Find(b => b.name == segment.bodyName);
            if (body == null)
                return false;

            if (!orbitCache.TryGetValue(cacheKey, out orbit))
            {
                try
                {
                    orbit = new Orbit(
                        segment.inclination,
                        segment.eccentricity,
                        segment.semiMajorAxis,
                        segment.longitudeOfAscendingNode,
                        segment.argumentOfPeriapsis,
                        segment.meanAnomalyAtEpoch,
                        segment.epoch,
                        body);
                }
                catch
                {
                    orbit = null;
                    return false;
                }
                orbitCache[cacheKey] = orbit;
            }

            return true;
        }

        private static bool TryFindCheckpointOrbitSegment(
            TrackSection section,
            double playbackUT,
            out OrbitSegment segment,
            out int checkpointIdx)
        {
            segment = default(OrbitSegment);
            checkpointIdx = -1;
            if (section.checkpoints == null || section.checkpoints.Count == 0)
                return false;

            for (int i = 0; i < section.checkpoints.Count; i++)
            {
                OrbitSegment candidate = section.checkpoints[i];
                if (playbackUT >= candidate.startUT && playbackUT <= candidate.endUT)
                {
                    segment = candidate;
                    checkpointIdx = i;
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveCheckpointPointInterpolation(
            List<TrajectoryPoint> frames,
            ref int playbackIdx,
            double playbackUT,
            out TrajectoryPoint before,
            out TrajectoryPoint after,
            out float t)
        {
            return TrajectoryMath.InterpolatePoints(
                frames,
                ref playbackIdx,
                playbackUT,
                out before,
                out after,
                out t);
        }

        private static long BuildCheckpointOrbitCacheKey(
            int recordingIndex,
            int sectionIdx,
            int checkpointIdx)
        {
            // Regular orbit playback cache keys are non-negative int-derived values.
            // Checkpoint keys live in a negative long namespace with explicit strides
            // so section/checkpoint growth cannot alias the ordinary segment cache.
            long rec = Math.Min(CheckpointOrbitCacheMaxRecordingIndex, Math.Max(0, (long)recordingIndex));
            long section = Math.Min(CheckpointOrbitCacheSectionStride - 1, Math.Max(0, (long)sectionIdx));
            long checkpoint = Math.Min(CheckpointOrbitCacheSectionStride - 1, Math.Max(0, (long)checkpointIdx));
            return unchecked(
                CheckpointOrbitCacheBase
                + rec * CheckpointOrbitCacheRecordingStride
                + section * CheckpointOrbitCacheSectionStride
                + checkpoint);
        }

        bool TryGetCheckpointTrackSection(
            IPlaybackTrajectory traj,
            double playbackUT,
            out int sectionIdx,
            out TrackSection section)
        {
            sectionIdx = -1;
            section = default(TrackSection);
            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
                return false;

            sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
            if (sectionIdx < 0)
                return false;

            section = traj.TrackSections[sectionIdx];
            return HasRenderableCheckpointTrackSection(section);
        }

        internal static bool HasRenderableCheckpointTrackSection(TrackSection section)
        {
            if (section.referenceFrame != ReferenceFrame.OrbitalCheckpoint)
                return false;

            bool hasFrames = section.frames != null && section.frames.Count > 0;
            bool hasCheckpoints = section.checkpoints != null && section.checkpoints.Count > 0;
            return hasFrames || hasCheckpoints;
        }

        void LogCheckpointPointPlayback(
            IPlaybackTrajectory traj,
            int sectionIdx,
            TrackSection section,
            double playbackUT,
            Vector3d worldPos)
        {
            int pointIdx = 0;
            TrajectoryPoint? point = TrajectoryMath.BracketPointAtUT(
                section.frames,
                playbackUT,
                ref pointIdx);
            if (!point.HasValue && section.frames != null && section.frames.Count > 0)
                point = section.frames[0];

            string recId = string.IsNullOrEmpty(traj?.RecordingId) ? "(null)" : traj.RecordingId;
            string pointDetail = point.HasValue
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "pointUT={0:F1} body={1} alt={2:F0} speed={3:F1}",
                    point.Value.ut,
                    point.Value.bodyName ?? "(null)",
                    point.Value.altitude,
                    point.Value.velocity.magnitude)
                : "pointUT=(none) body=(none) alt=0 speed=0";

            // Bug #595: the previous 1.0s rate-limit window was tight enough that
            // a long-playing OrbitalCheckpoint section still emitted ~14
            // lines/min per (rec, section) pair (413 lines in a single 30-min
            // session). Use the default 5s window to keep one line per section
            // per (typical) rate-limit window without losing the section-change
            // signal — the key is per-(recId, sectionIdx) so a new section
            // still logs immediately on first frame.
            ParsekLog.VerboseRateLimited(
                "Playback",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "orbital-checkpoint-point-{0}-{1}",
                    recId,
                    sectionIdx),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "OrbitalCheckpoint point playback: rec={0} currentUT={1:F1} section[{2}] sectionUT={3:F1}-{4:F1} {5} world={6} frames={7}",
                    recId,
                    playbackUT,
                    sectionIdx,
                    section.startUT,
                    section.endUT,
                    pointDetail,
                    FormatVector3d(worldPos),
                    section.frames?.Count ?? 0));
        }

        bool TryResolvePlaybackWorldPosition(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (traj == null)
                return false;

            int cachedIndex = state != null ? state.playbackIndex : 0;
            if (GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                    traj,
                    playbackUT,
                    out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic retireDiagnostic))
            {
                LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed(
                    traj,
                    index,
                    playbackUT,
                    "TryResolvePlaybackWorldPosition",
                    "body-fixed-primary-unavailable",
                    retireDiagnostic);
                return false;
            }

            bool skipRecordedRelativeResolverForAuthoredFrameGap =
                GhostPlaybackEngine.ShouldSkipRecordedRelativeResolverForAuthoredFrameGap(
                    traj,
                    playbackUT,
                    out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic);
            bool authoredGapHasShadow =
                skipRecordedRelativeResolverForAuthoredFrameGap
                && diagnostic.BodyFixedFramesCoverUT;

            if (!authoredGapHasShadow
                && TryResolveOrbitTailWorldPosition(
                    traj, playbackUT, index * 10000, out worldPos))
            {
                return true;
            }

            if (traj.TrackSections != null && traj.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
                if (sectionIdx >= 0)
                {
                    var section = traj.TrackSections[sectionIdx];
                    if (section.referenceFrame == ReferenceFrame.Relative)
                    {
                        var target = new RelativeSectionPlaybackTarget(
                            traj.RecordingId,
                            sectionIdx,
                            section);
                        if (!skipRecordedRelativeResolverForAuthoredFrameGap
                            && TryResolveRelativeWorldPosition(
                                section.frames ?? traj.Points,
                                playbackUT,
                                target,
                                traj.RecordingFormatVersion,
                                out worldPos))
                        {
                            return true;
                        }
                        if ((!skipRecordedRelativeResolverForAuthoredFrameGap
                                || diagnostic.BodyFixedFramesCoverUT)
                            && BodyFixedPrimaryCoversPlaybackUT(
                                section, playbackUT, out _, out _))
                        {
                            int absoluteCachedIndex = 0;
                            bool bodyFixedResolved = TryResolvePointWorldPosition(
                                section.bodyFixedFrames,
                                ref absoluteCachedIndex,
                                playbackUT,
                                out worldPos);
                            if (!bodyFixedResolved && skipRecordedRelativeResolverForAuthoredFrameGap)
                            {
                                LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed(
                                    traj,
                                    index,
                                    playbackUT,
                                    "TryResolvePlaybackWorldPosition",
                                    "body-fixed-primary-position-failed",
                                    diagnostic);
                            }
                            return bodyFixedResolved;
                        }
                        if (skipRecordedRelativeResolverForAuthoredFrameGap)
                        {
                            LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed(
                                traj,
                                index,
                                playbackUT,
                                "TryResolvePlaybackWorldPosition",
                                diagnostic.BodyFixedFramesCoverUT
                                    ? "body-fixed-primary-position-failed"
                                    : "body-fixed-primary-unavailable",
                                diagnostic);
                        }
                        // Relative sections store metre offsets in the
                        // lat/lon/alt fields. If their anchor cannot resolve,
                        // do not fall through to absolute/interpolated
                        // surface playback or checkpoint recovery.
                        return false;
                    }
                    else if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint
                        && section.frames != null
                        && section.frames.Count > 0
                        && TryResolvePointWorldPosition(
                            section.frames,
                            ref cachedIndex,
                            playbackUT,
                            out worldPos))
                    {
                        LogCheckpointPointPlayback(traj, sectionIdx, section, playbackUT, worldPos);
                        return true;
                    }
                    else if (TryGetAbsoluteSectionPlaybackFramesForPlayback(
                        traj.TrackSections,
                        sectionIdx,
                        out List<TrajectoryPoint> bodyFixedFrames,
                        out _))
                    {
                        // Section-local absolute frames are authoritative at
                        // frame boundaries. The flat Points list can contain
                        // adjacent Relative metre-offset samples; interpreting
                        // those as lat/lon/alt causes planet-scale spikes.
                        int sectionCachedIndex = 0;
                        return TryResolvePointWorldPosition(
                            bodyFixedFrames,
                            ref sectionCachedIndex,
                            playbackUT,
                            out worldPos);
                    }
                }
            }

            bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(traj.TrackSections, playbackUT);
            if (TryResolveInterpolatedWorldPosition(
                    traj.Points, traj.OrbitSegments, ref cachedIndex,
                    playbackUT, index * 10000, surfaceSkip, out worldPos,
                    traj.TrackSections))
            {
                return true;
            }

            if (traj.SurfacePos.HasValue)
                return TryResolveSurfaceWorldPosition(traj.SurfacePos.Value, out worldPos);

            return false;
        }

        bool TryResolveInterpolatedWorldPosition(
            List<TrajectoryPoint> points,
            List<OrbitSegment> segments,
            ref int cachedIndex,
            double targetUT,
            int orbitCacheBase,
            bool skipOrbitSegments,
            out Vector3d worldPos,
            List<TrackSection> flatPointSections = null)
        {
            if (!skipOrbitSegments
                && TryResolveOrbitWorldPosition(segments, targetUT, orbitCacheBase, out worldPos))
            {
                return true;
            }

            return TryResolvePointWorldPosition(
                points, ref cachedIndex, targetUT, out worldPos, flatPointSections);
        }

        bool TryResolveOrbitTailWorldPosition(
            IPlaybackTrajectory traj,
            double targetUT,
            int orbitCacheBase,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (!GhostPlaybackEngine.TryFindOrbitTailPlaybackSegment(
                    traj, targetUT, out OrbitSegment segment, out int segmentIndex))
            {
                return false;
            }

            long cacheKey = orbitCacheBase + Math.Max(0, segmentIndex);
            if (!TryGetOrbitForSegment(segment, cacheKey, out Orbit orbit, out CelestialBody body))
                return false;

            worldPos = orbit.getPositionAtUT(targetUT);
            if (TryResolvePredictedOrbitTailContinuityOffset(
                    traj,
                    segment,
                    orbit,
                    targetUT,
                    out Vector3d continuityOffset,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _))
            {
                worldPos += continuityOffset;
            }

            if (!IsFiniteVector3d(worldPos))
                return false;

            double orbitAlt = body.GetAltitude(worldPos);
            if (orbitAlt < 0)
            {
                double lat = body.GetLatitude(worldPos);
                double lon = body.GetLongitude(worldPos);
                worldPos = body.GetWorldSurfacePosition(lat, lon, 0);
            }

            return IsFiniteVector3d(worldPos);
        }

        internal static bool TryGetAbsoluteSectionPlaybackFrames(
            TrackSection section,
            out List<TrajectoryPoint> frames)
        {
            frames = null;
            if (section.referenceFrame != ReferenceFrame.Absolute
                || section.frames == null
                || section.frames.Count == 0)
            {
                return false;
            }

            frames = section.frames;
            return true;
        }

        internal static bool TryGetAbsoluteSectionPlaybackFramesForPlayback(
            List<TrackSection> trackSections,
            int sectionIdx,
            out List<TrajectoryPoint> frames,
            out string frameSource)
        {
            frames = null;
            frameSource = "section-frames";
            if (trackSections == null
                || sectionIdx < 0
                || sectionIdx >= trackSections.Count)
            {
                return false;
            }

            TrackSection section = trackSections[sectionIdx];
            if (!TryGetAbsoluteSectionPlaybackFrames(section, out List<TrajectoryPoint> sectionFrames))
                return false;

            frames = sectionFrames;
            if (sectionFrames.Count == 0)
                return true;

            const double epsilonSeconds = 1e-6;
            TrajectoryPoint first = sectionFrames[0];
            TrajectoryPoint last = sectionFrames[sectionFrames.Count - 1];
            TrajectoryPoint previousFrame = default(TrajectoryPoint);
            TrajectoryPoint nextFrame = default(TrajectoryPoint);
            bool prepend = section.startUT + epsilonSeconds < first.ut
                && TryFindAdjacentAbsolutePlaybackFrame(
                    trackSections,
                    sectionIdx - 1,
                    searchForward: false,
                    requireBeforeUT: first.ut,
                    requireAfterUT: double.NaN,
                    bodyName: first.bodyName,
                    out previousFrame);
            bool append = last.ut + epsilonSeconds < section.endUT
                && TryFindAdjacentAbsolutePlaybackFrame(
                    trackSections,
                    sectionIdx + 1,
                    searchForward: true,
                    requireBeforeUT: double.NaN,
                    requireAfterUT: last.ut,
                    bodyName: last.bodyName,
                    out nextFrame);

            if (!prepend && !append)
                return true;

            var bridged = new List<TrajectoryPoint>(
                sectionFrames.Count + (prepend ? 1 : 0) + (append ? 1 : 0));
            if (prepend)
                bridged.Add(previousFrame);
            bridged.AddRange(sectionFrames);
            if (append)
                bridged.Add(nextFrame);

            frames = bridged;
            frameSource = "section-frames-bridged";
            return true;
        }

        private static bool TryFindAdjacentAbsolutePlaybackFrame(
            List<TrackSection> trackSections,
            int adjacentIdx,
            bool searchForward,
            double requireBeforeUT,
            double requireAfterUT,
            string bodyName,
            out TrajectoryPoint frame)
        {
            frame = default(TrajectoryPoint);
            if (trackSections == null
                || adjacentIdx < 0
                || adjacentIdx >= trackSections.Count)
            {
                return false;
            }

            List<TrajectoryPoint> adjacentFrames =
                GetAbsolutePlaybackFrameList(trackSections[adjacentIdx]);
            if (adjacentFrames == null || adjacentFrames.Count == 0)
                return false;

            if (searchForward)
            {
                for (int i = 0; i < adjacentFrames.Count; i++)
                {
                    TrajectoryPoint candidate = adjacentFrames[i];
                    if (IsEligibleBoundaryBridgeFrame(
                            candidate,
                            requireBeforeUT,
                            requireAfterUT,
                            bodyName))
                    {
                        frame = candidate;
                        return true;
                    }
                }
            }
            else
            {
                for (int i = adjacentFrames.Count - 1; i >= 0; i--)
                {
                    TrajectoryPoint candidate = adjacentFrames[i];
                    if (IsEligibleBoundaryBridgeFrame(
                            candidate,
                            requireBeforeUT,
                            requireAfterUT,
                            bodyName))
                    {
                        frame = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        private static List<TrajectoryPoint> GetAbsolutePlaybackFrameList(TrackSection section)
        {
            if (section.referenceFrame == ReferenceFrame.Absolute)
                return section.frames;
            if (section.referenceFrame == ReferenceFrame.Relative)
                return section.bodyFixedFrames;
            return null;
        }

        private static bool IsEligibleBoundaryBridgeFrame(
            TrajectoryPoint candidate,
            double requireBeforeUT,
            double requireAfterUT,
            string bodyName)
        {
            const double epsilonSeconds = 1e-6;
            if (!double.IsNaN(requireBeforeUT)
                && !(candidate.ut + epsilonSeconds < requireBeforeUT))
            {
                return false;
            }

            if (!double.IsNaN(requireAfterUT)
                && !(candidate.ut > requireAfterUT + epsilonSeconds))
            {
                return false;
            }

            return string.IsNullOrEmpty(bodyName)
                || string.IsNullOrEmpty(candidate.bodyName)
                || string.Equals(candidate.bodyName, bodyName, StringComparison.Ordinal);
        }

        private sealed class ReFlyDenseAbsolutePlaybackFrameCacheEntry
        {
            internal readonly List<TrajectoryPoint> Frames;
            internal readonly int DenseFrameCount;
            internal readonly string BuildReason;

            internal ReFlyDenseAbsolutePlaybackFrameCacheEntry(
                List<TrajectoryPoint> frames,
                int denseFrameCount,
                string buildReason)
            {
                Frames = frames;
                DenseFrameCount = denseFrameCount;
                BuildReason = buildReason;
            }

            internal bool Eligible => Frames != null && Frames.Count >= 2;
        }

        private bool TryGetDenseReFlyAbsolutePlaybackFrames(
            IPlaybackTrajectory traj,
            int sectionIdx,
            TrackSection section,
            List<TrajectoryPoint> sectionFrames,
            double playbackUT,
            out List<TrajectoryPoint> denseFrames,
            out string reason)
        {
            denseFrames = null;
            if (section.referenceFrame != ReferenceFrame.Absolute)
            {
                reason = "not-absolute";
                return false;
            }

            if (!PointBelongsToSection(traj?.TrackSections, sectionIdx, section, playbackUT))
            {
                reason = "ut-outside-section";
                return false;
            }

            if (sectionFrames == null || sectionFrames.Count == 0)
            {
                reason = "no-section-frames";
                return false;
            }

            if (traj?.Points == null || traj.Points.Count == 0)
            {
                reason = "no-flat-points";
                return false;
            }

            string recId = string.IsNullOrEmpty(traj?.RecordingId) ? "(null)" : traj.RecordingId;
            string cacheKey = string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2:R}|{3:R}|{4}|{5}|{6}",
                recId,
                sectionIdx,
                section.startUT,
                section.endUT,
                traj.Points.Count,
                traj.TrackSections?.Count ?? 0,
                RecordingStore.StateVersion);

            if (!reFlyDenseAbsolutePlaybackFrameCache.TryGetValue(
                    cacheKey,
                    out ReFlyDenseAbsolutePlaybackFrameCacheEntry entry))
            {
                denseFrames = BuildDensebodyFixedFramesForSection(
                    traj.TrackSections,
                    sectionIdx,
                    section,
                    traj.Points);

                string buildReason = "dense-built";
                if (denseFrames.Count < 2)
                    buildReason = "dense-too-small";
                else if (denseFrames.Count <= sectionFrames.Count)
                    buildReason = "dense-not-more-populated";
                else
                    buildReason = "dense-built-eligible";

                entry = new ReFlyDenseAbsolutePlaybackFrameCacheEntry(
                    buildReason == "dense-built-eligible" ? denseFrames : null,
                    denseFrames.Count,
                    buildReason);
                reFlyDenseAbsolutePlaybackFrameCache[cacheKey] = entry;
            }

            if (!entry.Eligible)
            {
                reason = entry.BuildReason;
                return false;
            }

            denseFrames = entry.Frames;
            if (!TryGetBracketWidthSeconds(
                    sectionFrames,
                    playbackUT,
                    out double sectionBracketSeconds))
            {
                reason = "no-section-bracket";
                return false;
            }

            if (!TryGetBracketWidthSeconds(
                    denseFrames,
                    playbackUT,
                    out double denseBracketSeconds))
            {
                reason = "no-dense-bracket";
                return false;
            }

            if (!(denseBracketSeconds + 1e-6 < sectionBracketSeconds))
            {
                reason = "dense-bracket-not-tighter";
                return false;
            }

            reason = "dense-bracket-tighter";
            if (loggedReFlyDenseAbsolutePlaybackFrameSelections.Add(cacheKey))
            {
                ParsekLog.Info("Playback",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Re-Fly PointInterp using dense flat samples: rec={0} section={1} sectionUT=[{2:F3},{3:F3}] sectionFrames={4} denseFrames={5} sectionBracketSeconds={6:F3} denseBracketSeconds={7:F3} reason={8} buildReason={9}",
                        recId,
                        sectionIdx,
                        section.startUT,
                        section.endUT,
                        sectionFrames?.Count ?? 0,
                        entry.DenseFrameCount,
                        sectionBracketSeconds,
                        denseBracketSeconds,
                        reason,
                        entry.BuildReason));
            }

            return true;
        }

        internal static bool ShouldUseDenseReFlyAbsolutePlaybackFrames(
            List<TrackSection> trackSections,
            int sectionIdx,
            TrackSection section,
            List<TrajectoryPoint> sectionFrames,
            List<TrajectoryPoint> flatPoints,
            double playbackUT,
            out int denseFrameCount,
            out double sectionBracketSeconds,
            out double denseBracketSeconds,
            out string reason)
        {
            denseFrameCount = 0;
            sectionBracketSeconds = double.NaN;
            denseBracketSeconds = double.NaN;
            reason = "not-evaluated";

            if (section.referenceFrame != ReferenceFrame.Absolute)
            {
                reason = "not-absolute";
                return false;
            }

            if (!PointBelongsToSection(trackSections, sectionIdx, section, playbackUT))
            {
                reason = "ut-outside-section";
                return false;
            }

            if (sectionFrames == null || sectionFrames.Count == 0)
            {
                reason = "no-section-frames";
                return false;
            }

            if (flatPoints == null || flatPoints.Count == 0)
            {
                reason = "no-flat-points";
                return false;
            }

            denseFrameCount = CountDensebodyFixedFramesForSection(
                trackSections,
                sectionIdx,
                section,
                flatPoints);
            if (denseFrameCount < 2)
            {
                reason = "dense-too-small";
                return false;
            }

            if (denseFrameCount <= sectionFrames.Count)
            {
                reason = "dense-not-more-populated";
                return false;
            }

            if (!TryGetBracketWidthSeconds(
                    sectionFrames,
                    playbackUT,
                    out sectionBracketSeconds))
            {
                reason = "no-section-bracket";
                return false;
            }

            if (!TryGetDenseSectionBracketWidthSeconds(
                    trackSections,
                    sectionIdx,
                    section,
                    flatPoints,
                    playbackUT,
                    out denseBracketSeconds))
            {
                reason = "no-dense-bracket";
                return false;
            }

            if (!(denseBracketSeconds + 1e-6 < sectionBracketSeconds))
            {
                reason = "dense-bracket-not-tighter";
                return false;
            }

            reason = "dense-bracket-tighter";
            return true;
        }

        private static List<TrajectoryPoint> BuildDensebodyFixedFramesForSection(
            List<TrackSection> trackSections,
            int sectionIdx,
            TrackSection section,
            List<TrajectoryPoint> flatPoints)
        {
            var frames = new List<TrajectoryPoint>();
            if (flatPoints == null)
                return frames;

            for (int i = 0; i < flatPoints.Count; i++)
            {
                TrajectoryPoint point = flatPoints[i];
                if (PointBelongsToSection(trackSections, sectionIdx, section, point.ut))
                    frames.Add(point);
            }

            return frames;
        }

        private static int CountDensebodyFixedFramesForSection(
            List<TrackSection> trackSections,
            int sectionIdx,
            TrackSection section,
            List<TrajectoryPoint> flatPoints)
        {
            if (flatPoints == null)
                return 0;

            int count = 0;
            for (int i = 0; i < flatPoints.Count; i++)
            {
                if (PointBelongsToSection(trackSections, sectionIdx, section, flatPoints[i].ut))
                    count++;
            }

            return count;
        }

        private static bool TryGetDenseSectionBracketWidthSeconds(
            List<TrackSection> trackSections,
            int sectionIdx,
            TrackSection section,
            List<TrajectoryPoint> points,
            double playbackUT,
            out double bracketSeconds)
        {
            bracketSeconds = double.NaN;
            if (points == null || points.Count == 0)
                return false;

            bool hasBefore = false;
            bool hasAfter = false;
            double beforeUT = 0.0;
            double afterUT = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                TrajectoryPoint point = points[i];
                if (!PointBelongsToSection(trackSections, sectionIdx, section, point.ut))
                    continue;

                if (point.ut <= playbackUT && (!hasBefore || point.ut > beforeUT))
                {
                    beforeUT = point.ut;
                    hasBefore = true;
                }

                if (point.ut >= playbackUT && (!hasAfter || point.ut < afterUT))
                {
                    afterUT = point.ut;
                    hasAfter = true;
                }
            }

            if (!hasBefore || !hasAfter)
                return false;

            bracketSeconds = System.Math.Max(0.0, afterUT - beforeUT);
            return true;
        }

        private static bool TryGetBracketWidthSeconds(
            List<TrajectoryPoint> points,
            double playbackUT,
            out double bracketSeconds)
        {
            bracketSeconds = double.NaN;
            if (points == null || points.Count < 2)
                return false;

            int cachedIndex = 0;
            if (!TrajectoryMath.InterpolatePoints(
                    points,
                    ref cachedIndex,
                    playbackUT,
                    out TrajectoryPoint before,
                    out TrajectoryPoint after,
                    out float ignoredT))
            {
                return false;
            }

            bracketSeconds = System.Math.Max(0.0, after.ut - before.ut);
            return true;
        }

        private static bool PointBelongsToSection(
            List<TrackSection> trackSections,
            int sectionIdx,
            TrackSection section,
            double ut)
        {
            if (trackSections != null
                && sectionIdx >= 0
                && sectionIdx < trackSections.Count)
            {
                return TrajectoryMath.FindTrackSectionForUT(trackSections, ut) == sectionIdx;
            }

            return ut >= section.startUT && ut <= section.endUT;
        }

        bool TryResolvePointWorldPosition(
            List<TrajectoryPoint> points,
            ref int cachedIndex,
            double targetUT,
            out Vector3d worldPos,
            List<TrackSection> flatPointSections = null)
        {
            worldPos = Vector3d.zero;
            if (points == null || points.Count == 0)
                return false;

            TrajectoryPoint before;
            TrajectoryPoint after;
            float t;
            if (points.Count == 1)
            {
                before = points[0];
                after = points[0];
                if (flatPointSections != null
                    && !TryDispatchFlatPointEndpoints(
                        flatPointSections, ref before, ref after, targetUT, null, "world-resolve-flat-points"))
                {
                    return false;
                }
                return TryResolveTrajectoryPointWorldPosition(before, out worldPos);
            }

            bool hasSegment = TrajectoryMath.InterpolatePoints(
                points, ref cachedIndex, targetUT, out before, out after, out t);
            if (!hasSegment)
                after = before;
            if (flatPointSections != null
                && !TryDispatchFlatPointEndpoints(
                    flatPointSections, ref before, ref after, targetUT, null, "world-resolve-flat-points"))
            {
                return false;
            }
            if (!hasSegment)
            {
                return points != null
                    && points.Count > 0
                    && TryResolveTrajectoryPointWorldPosition(before, out worldPos);
            }

            if (t == 0f && before.ut == after.ut)
                return TryResolveTrajectoryPointWorldPosition(before, out worldPos);

            CelestialBody bodyBefore = FlightGlobals.Bodies?.Find(b => b.name == before.bodyName);
            CelestialBody bodyAfter = FlightGlobals.Bodies?.Find(b => b.name == after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
                return false;

            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);

            worldPos = Vector3d.Lerp(posBefore, posAfter, t);
            if (double.IsNaN(worldPos.x) || double.IsNaN(worldPos.y) || double.IsNaN(worldPos.z))
                worldPos = posBefore;
            return true;
        }

        bool TryResolveTrajectoryPointWorldPosition(TrajectoryPoint point, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == point.bodyName);
            if (body == null)
                return false;

            worldPos = body.GetWorldSurfacePosition(
                point.latitude, point.longitude, point.altitude);
            return true;
        }

        bool TryResolveOrbitWorldPosition(
            List<OrbitSegment> segments, double targetUT, int orbitCacheBase, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (segments == null || segments.Count == 0)
                return false;

            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment seg = segments[i];
                if (targetUT < seg.startUT || targetUT > seg.endUT)
                    continue;

                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == seg.bodyName);
                if (body == null)
                    return false;

                if (!TrajectoryMath.HasUsableOrbitSegmentElements(seg))
                    return false;

                Orbit orbit;
                int cacheKey = orbitCacheBase + i;
                if (!orbitCache.TryGetValue(cacheKey, out orbit))
                {
                    try
                    {
                        orbit = new Orbit(
                            seg.inclination,
                            seg.eccentricity,
                            seg.semiMajorAxis,
                            seg.longitudeOfAscendingNode,
                            seg.argumentOfPeriapsis,
                            seg.meanAnomalyAtEpoch,
                            seg.epoch,
                            body);
                    }
                    catch
                    {
                        return false;
                    }
                    orbitCache[cacheKey] = orbit;
                }

                try
                {
                    worldPos = orbit.getPositionAtUT(targetUT);
                    if (!IsFiniteVector3d(worldPos))
                        return false;

                    if (body.GetAltitude(worldPos) < 0)
                    {
                        double lat = body.GetLatitude(worldPos);
                        double lon = body.GetLongitude(worldPos);
                        worldPos = body.GetWorldSurfacePosition(lat, lon, 0);
                        if (!IsFiniteVector3d(worldPos))
                            return false;
                    }
                }
                catch
                {
                    return false;
                }

                return true;
            }

            return false;
        }

        bool TryResolveRelativeWorldPosition(
            List<TrajectoryPoint> frames,
            double targetUT,
            RelativeSectionPlaybackTarget target,
            int recordingFormatVersion,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (frames == null || frames.Count == 0)
                return false;
            if (frames.Count == 1)
            {
                return TryResolveRelativeOffsetWorldPosition(
                    frames[0].latitude, frames[0].longitude, frames[0].altitude,
                    target, targetUT,
                    recordingFormatVersion, out worldPos);
            }

            int cachedIndex = 0;
            int indexBefore = TrajectoryMath.FindWaypointIndex(frames, ref cachedIndex, targetUT);
            if (indexBefore < 0)
                return TryResolveRelativeOffsetWorldPosition(
                    frames[0].latitude, frames[0].longitude, frames[0].altitude,
                    target, frames[0].ut,
                    recordingFormatVersion, out worldPos);
            if (indexBefore >= frames.Count - 1)
            {
                return TryResolveRelativeOffsetWorldPosition(
                    frames[frames.Count - 1].latitude,
                    frames[frames.Count - 1].longitude,
                    frames[frames.Count - 1].altitude,
                    target, frames[frames.Count - 1].ut,
                    recordingFormatVersion, out worldPos);
            }

            TrajectoryPoint before = frames[indexBefore];
            TrajectoryPoint after = frames[indexBefore + 1];
            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                return TryResolveRelativeOffsetWorldPosition(
                    before.latitude, before.longitude, before.altitude,
                    target, before.ut,
                    recordingFormatVersion, out worldPos);
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            double dx = before.latitude + (after.latitude - before.latitude) * t;
            double dy = before.longitude + (after.longitude - before.longitude) * t;
            double dz = before.altitude + (after.altitude - before.altitude) * t;
            return TryResolveRelativeOffsetWorldPosition(
                dx,
                dy,
                dz,
                target,
                targetUT,
                recordingFormatVersion,
                out worldPos);
        }

        bool TryResolveRelativeOffsetWorldPosition(
            double dx,
            double dy,
            double dz,
            RelativeSectionPlaybackTarget target,
            double targetUT,
            int recordingFormatVersion,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            RelativeAnchorPose anchorPose;
            if (!TryResolveRecordedRelativeAnchorPose(
                    target,
                    targetUT,
                    out anchorPose,
                    out RelativeAnchorResolveFailure failure))
            {
                string reason = RelativeAnchorResolveFailure.ReasonOrFallback(
                    failure,
                    "anchor-pose-unresolved");
                GhostRenderTrace.EmitRelativeResolver(
                    target.RecordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "recorded-relative-offset",
                    reason,
                    0u,
                    target.AnchorRecordingId,
                    success: false,
                    fromRecordedTrajectory: true,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: Vector3d.zero);
                return false;
            }

            worldPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                anchorPose.worldPos,
                anchorPose.worldRotation,
                dx,
                dy,
                dz);
            if (double.IsNaN(worldPos.x) || double.IsNaN(worldPos.y) || double.IsNaN(worldPos.z))
                worldPos = anchorPose.worldPos;
            GhostRenderTrace.EmitRelativeResolver(
                target.RecordingId,
                -1,
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                targetUT,
                "recorded-relative-offset",
                "resolved",
                0u,
                target.AnchorRecordingId,
                success: true,
                fromRecordedTrajectory: anchorPose.fromRecordedTrajectory,
                anchorPosition: anchorPose.worldPos,
                anchorRotation: anchorPose.worldRotation,
                localOffset: new Vector3d(dx, dy, dz),
                outputPosition: worldPos);
            return true;
        }

        bool TryResolveLoopLiveAnchorPose(
            uint anchorVesselId,
            string victimRecordingId,
            double targetUT,
            out RelativeAnchorPose pose)
        {
            pose = default(RelativeAnchorPose);
            if (anchorVesselId == 0u)
            {
                GhostRenderTrace.EmitRelativeResolver(
                    victimRecordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-live-anchor",
                    "missing-anchor-pid",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                return false;
            }

            Vessel anchor = FlightRecorder.FindVesselByPid(anchorVesselId);
            if (anchor == null)
            {
                GhostRenderTrace.EmitRelativeResolver(
                    victimRecordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-live-anchor",
                    "anchor-not-loaded",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                return false;
            }

            // Match the recorder's live-anchor frame (FlightRecorder /
            // BackgroundRecorder TryResolveAnchorPoseForCandidate). The recorder
            // writes loop-relative offsets against vesselTransform.position; the
            // matching rotation is vesselTransform.rotation. GetWorldPos3D (CoMD)
            // here would re-introduce the same CoM-vs-vesselTransform mismatch
            // the recorder fix removed, but inverted at playback time. Use
            // anchor.transform explicitly (not GetTransform(), which follows
            // ReferenceTransform / "Control From Here").
            Transform anchorTransform = anchor.transform;
            Quaternion anchorRotation = anchorTransform != null
                ? anchorTransform.rotation
                : Quaternion.identity;
            Vector3d anchorWorld = anchorTransform != null
                ? (Vector3d)anchorTransform.position
                : anchor.GetWorldPos3D();
            if (!IsFiniteVector3d(anchorWorld))
            {
                GhostRenderTrace.EmitRelativeResolver(
                    victimRecordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-live-anchor",
                    "non-finite-live-anchor",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: anchorWorld,
                    anchorRotation: anchorRotation,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                return false;
            }

            pose.worldPos = anchorWorld;
            pose.worldRotation = anchorRotation;
            pose.fromRecordedTrajectory = false;
            GhostRenderTrace.EmitRelativeResolver(
                victimRecordingId,
                -1,
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                targetUT,
                "loop-live-anchor",
                "live-selected",
                anchorVesselId,
                null,
                success: true,
                fromRecordedTrajectory: false,
                anchorPosition: pose.worldPos,
                anchorRotation: pose.worldRotation,
                localOffset: Vector3d.zero,
                outputPosition: pose.worldPos);
            return true;
        }

        internal static List<TrajectoryPoint> ResolveBodyFixedPrimaryPlaybackFrames(
            IPlaybackTrajectory trajectory,
            TrackSection section,
            double targetUT)
        {
            List<TrajectoryPoint> bodyFixedFrames = section.bodyFixedFrames;
            if (bodyFixedFrames == null || bodyFixedFrames.Count == 0)
                return bodyFixedFrames;
            if (RecordedAnchorPointListCoversUT(bodyFixedFrames, targetUT))
                return bodyFixedFrames;
            if (targetUT >= bodyFixedFrames[0].ut)
                return bodyFixedFrames;

            TrajectoryPoint bridge;
            if (!TryFindBodyFixedPrimaryBridgeFrame(trajectory, section, targetUT, out bridge))
                return bodyFixedFrames;
            if (bridge.ut >= bodyFixedFrames[0].ut - 0.0001)
                return bodyFixedFrames;

            var bridged = new List<TrajectoryPoint>(bodyFixedFrames.Count + 1);
            bridged.Add(bridge);
            bridged.AddRange(bodyFixedFrames);
            ParsekLog.WarnRateLimited(
                "Playback",
                string.Concat(
                    "body-fixed-primary-bridge|",
                    trajectory?.RecordingId ?? "(none)",
                    "|",
                    section.startUT.ToString("R", CultureInfo.InvariantCulture)),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "RELATIVE body-fixed primary bridge inserted: recording={0} targetUT={1:F2} " +
                    "bridgeUT={2:F2} firstShadowUT={3:F2} sectionUT=[{4:F2},{5:F2}]",
                    ShortRecordingId(trajectory?.RecordingId),
                    targetUT,
                    bridge.ut,
                    bodyFixedFrames[0].ut,
                    section.startUT,
                    section.endUT),
                30.0);
            return bridged;
        }

        internal static bool TryFindBodyFixedPrimaryBridgeFrame(
            IPlaybackTrajectory trajectory,
            TrackSection relativeSection,
            double targetUT,
            out TrajectoryPoint bridge)
        {
            bridge = default(TrajectoryPoint);
            bool found = false;
            double bestUT = double.NegativeInfinity;
            double cutoffUT = Math.Max(targetUT, relativeSection.startUT) + 0.0001;

            if (trajectory?.TrackSections != null)
            {
                for (int i = 0; i < trajectory.TrackSections.Count; i++)
                {
                    TrackSection section = trajectory.TrackSections[i];
                    if (section.referenceFrame != ReferenceFrame.Absolute || section.frames == null)
                        continue;
                    for (int j = 0; j < section.frames.Count; j++)
                    {
                        TrajectoryPoint candidate = section.frames[j];
                        if (candidate.ut > cutoffUT || candidate.ut <= bestUT)
                            continue;
                        bridge = candidate;
                        bestUT = candidate.ut;
                        found = true;
                    }
                }
            }

            return found;
        }

        private static string ShortRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return "-";
            return recordingId.Length <= 8 ? recordingId : recordingId.Substring(0, 8);
        }

        bool TryResolveActiveReFlyPid(
            ReFlySessionMarker marker,
            IReadOnlyList<RecordingTree> searchTrees,
            out uint activeReFlyPid)
        {
            activeReFlyPid = 0u;
            if (marker == null || string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                return false;

            if (searchTrees != null)
            {
                for (int t = 0; t < searchTrees.Count; t++)
                {
                    RecordingTree tree = searchTrees[t];
                    if (tree?.Recordings == null)
                        continue;
                    Recording rec;
                    if (tree.Recordings.TryGetValue(marker.ActiveReFlyRecordingId, out rec)
                        && rec != null
                        && rec.VesselPersistentId != 0u)
                    {
                        activeReFlyPid = rec.VesselPersistentId;
                        return true;
                    }
                }
            }

            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    Recording rec = committed[i];
                    if (rec == null
                        || !string.Equals(rec.RecordingId, marker.ActiveReFlyRecordingId, StringComparison.Ordinal)
                        || rec.VesselPersistentId == 0u)
                    {
                        continue;
                    }

                    activeReFlyPid = rec.VesselPersistentId;
                    return true;
                }
            }

            return false;
        }

        bool TryResolveRecordedAnchorPoseWithCoverage(
            uint anchorVesselId,
            string victimRecordingId,
            double targetUT,
            IReadOnlyList<RecordingTree> searchTrees,
            ReFlySessionMarker marker,
            bool bypassLiveAnchorForActiveReFly,
            bool requireCoverage,
            out RelativeAnchorPose pose)
        {
            pose = default(RelativeAnchorPose);
            if (anchorVesselId == 0u)
                return false;

            HashSet<string> seenRecordingIds = recordedAnchorSeenRecordingIds;
            seenRecordingIds.Clear();
            if (TryResolvePreReFlyFrozenAnchorPose(
                    anchorVesselId,
                    victimRecordingId,
                    targetUT,
                    searchTrees,
                    marker,
                    bypassLiveAnchorForActiveReFly,
                    seenRecordingIds,
                    requireCoverage,
                    out pose))
            {
                seenRecordingIds.Clear();
                return true;
            }

            if (searchTrees != null)
            {
                for (int t = 0; t < searchTrees.Count; t++)
                {
                    RecordingTree tree = searchTrees[t];
                    if (tree?.Recordings == null)
                        continue;
                    foreach (Recording rec in tree.Recordings.Values)
                    {
                        if (ShouldSkipMutableActiveReFlyAnchorCandidate(
                                rec, marker, bypassLiveAnchorForActiveReFly))
                        {
                            continue;
                        }
                        if (TryResolveRecordedAnchorPoseFromCandidate(
                                rec, anchorVesselId, victimRecordingId,
                                targetUT, seenRecordingIds, requireCoverage, out pose))
                        {
                            seenRecordingIds.Clear();
                            return true;
                        }
                    }
                }
            }

            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    if (ShouldSkipMutableActiveReFlyAnchorCandidate(
                            committed[i], marker, bypassLiveAnchorForActiveReFly))
                    {
                        continue;
                    }
                    if (TryResolveRecordedAnchorPoseFromCandidate(
                            committed[i], anchorVesselId, victimRecordingId,
                            targetUT, seenRecordingIds, requireCoverage, out pose))
                    {
                        seenRecordingIds.Clear();
                        return true;
                    }
                }
            }

            seenRecordingIds.Clear();
            return false;
        }

        internal static bool ShouldSkipMutableActiveReFlyAnchorCandidate(
            Recording candidate,
            ReFlySessionMarker marker,
            bool bypassLiveAnchorForActiveReFly)
        {
            if (!bypassLiveAnchorForActiveReFly)
                return false;
            if (candidate == null || marker == null)
                return false;
            if (string.IsNullOrEmpty(candidate.RecordingId)
                || string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                return false;
            return string.Equals(
                candidate.RecordingId,
                marker.ActiveReFlyRecordingId,
                StringComparison.Ordinal);
        }

        bool TryResolvePreReFlyFrozenAnchorPose(
            uint anchorVesselId,
            string victimRecordingId,
            double targetUT,
            IReadOnlyList<RecordingTree> searchTrees,
            ReFlySessionMarker marker,
            bool bypassLiveAnchorForActiveReFly,
            HashSet<string> seenRecordingIds,
            bool requireCoverage,
            out RelativeAnchorPose pose)
        {
            pose = default(RelativeAnchorPose);
            if (!bypassLiveAnchorForActiveReFly)
                return false;
            if (marker == null || string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                return false;

            Recording liveActive = FindActiveReFlyRecording(marker, searchTrees);
            if (!ShouldUsePreReFlyAnchorTrajectory(
                    liveActive,
                    anchorVesselId,
                    victimRecordingId,
                    marker))
            {
                return false;
            }

            Recording frozen = liveActive.BuildPreReFlyAnchorTrajectoryRecording(marker.SessionId);
            if (frozen == null)
                return false;

            return TryResolveRecordedAnchorPoseFromCandidate(
                frozen,
                anchorVesselId,
                victimRecordingId,
                targetUT,
                seenRecordingIds,
                requireCoverage,
                out pose);
        }

        Recording FindActiveReFlyRecording(
            ReFlySessionMarker marker,
            IReadOnlyList<RecordingTree> searchTrees)
        {
            if (marker == null || string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                return null;

            if (searchTrees != null)
            {
                for (int t = 0; t < searchTrees.Count; t++)
                {
                    RecordingTree tree = searchTrees[t];
                    if (tree?.Recordings == null)
                        continue;
                    Recording rec;
                    if (tree.Recordings.TryGetValue(marker.ActiveReFlyRecordingId, out rec)
                        && rec != null)
                    {
                        return rec;
                    }
                }
            }

            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    Recording rec = committed[i];
                    if (rec != null
                        && string.Equals(rec.RecordingId, marker.ActiveReFlyRecordingId, StringComparison.Ordinal))
                    {
                        return rec;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Issue #734 race fix: ensures the in-place Re-Fly fork is present
        /// in <paramref name="tree"/>'s <c>Recordings</c> dictionary before
        /// the restore coroutine runs <see cref="ReFlySessionMarker.ResolveInPlaceContinuationTarget"/>.
        /// AtomicMarkerWrite eagerly attaches the fork to the pending tree,
        /// but on async FLIGHT loads the restore coroutine and
        /// AtomicMarkerWrite both fire on <c>onFlightReady</c> — if the
        /// coroutine wins the race, AtomicMarkerWrite finds no pending tree
        /// and the fork only lives in <see cref="RecordingStore.CommittedRecordings"/>.
        /// This helper looks the fork up by id from the committed list and
        /// attaches it to the tree so the swap can fire and the recorder
        /// has a destination dict entry to flush into. Internal static so
        /// xUnit can pin every branch without a live coroutine.
        /// </summary>
        internal static bool ReconcileInPlaceForkIntoTreeIfNeeded(
            RecordingTree tree, ReFlySessionMarker marker)
        {
            if (tree == null || tree.Recordings == null || marker == null)
                return false;
            // Only the post-#734 fork shape needs reconciliation.
            // AtomicMarkerWrite is the only writer of the
            // InPlaceContinuation flag and always pairs it with a fresh
            // provisional recording.
            if (!marker.InPlaceContinuation)
                return false;
            if (string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                || string.IsNullOrEmpty(marker.OriginChildRecordingId))
                return false;
            if (!string.IsNullOrEmpty(marker.TreeId)
                && !string.Equals(marker.TreeId, tree.Id, StringComparison.Ordinal))
            {
                return false;
            }

            // Locate the fork in the committed list. Allowlisted raw read
            // per [ERS-exempt - Phase 6] file-level note in RewindInvoker.
            // The committed-list object is the canonical one: AtomicMarkerWrite
            // wrote it via AddProvisional, the recorder flushes through
            // tree.ActiveRecordingId -> tree.Recordings[id], and
            // TryCommitReFlySupersede resolves the provisional from the
            // committed list. The same instance must live in BOTH places or
            // commit/discard can pull a stale shadow object.
            Recording committedFork = null;
            var committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec == null) continue;
                    if (string.Equals(rec.RecordingId,
                            marker.ActiveReFlyRecordingId, StringComparison.Ordinal))
                    {
                        committedFork = rec;
                        break;
                    }
                }
            }
            if (committedFork == null)
            {
                // Normal F5/F9 mid-Re-Fly: LoadRecordingTrees skips
                // isActive nodes and TryRestoreActiveTreeNode populates
                // PendingTree.Recordings only (no AddCommittedInternal),
                // so the fork is intentionally absent from the committed
                // list while present in the tree's Recordings dict. The
                // recorder will flush correctly via the tree path; only
                // the genuine "missing from BOTH" state is data-loss.
                bool inTree = tree.Recordings.ContainsKey(marker.ActiveReFlyRecordingId);
                if (inTree)
                {
                    ParsekLog.Verbose("Flight",
                        $"ReconcileInPlaceForkIntoTreeIfNeeded: fork rec={marker.ActiveReFlyRecordingId} " +
                        $"absent from committed list but present in tree.Recordings " +
                        $"(normal F5/F9 mid-Re-Fly hydration; recorder flush will work). " +
                        $"sess={marker.SessionId ?? "<no-id>"} tree={tree.Id ?? "<none>"}");
                }
                else
                {
                    ParsekLog.Warn("Flight",
                        $"ReconcileInPlaceForkIntoTreeIfNeeded: fork rec={marker.ActiveReFlyRecordingId} " +
                        $"missing from BOTH committed list AND tree.Recordings — " +
                        $"recorder flush will warn and stop. " +
                        $"sess={marker.SessionId ?? "<no-id>"} tree={tree.Id ?? "<none>"}");
                }
                return false;
            }

            // Critical: when an F5/F9 mid-session deserialised tree has
            // an in-place fork entry, that entry is a *new* Recording
            // instance, but the committed list still holds the pre-load
            // fork object. Recorder flush appends to the tree's instance,
            // TryCommitReFlySupersede resolves the provisional from the
            // committed list -- so without convergence the merge sees a
            // stale shadow with no recorded data and CommitTree can even
            // re-add a duplicate same-id row by reference. Compare the two
            // and overwrite the tree slot with the committed instance when
            // they diverge. EnsureForkAttachedToTree's own no-op short-
            // circuit fires when they already match.
            if (tree.Recordings.TryGetValue(marker.ActiveReFlyRecordingId,
                    out Recording treeFork)
                && ReferenceEquals(treeFork, committedFork))
            {
                return false;
            }

            // Always rebuild the background map after attach. The earlier
            // optimisation (skipBackgroundMapRebuild: true) assumed
            // RestoreActiveTreeFromPending's markerSwap branch would
            // rebuild immediately afterwards, but that branch only fires
            // when `ShouldSwap=true`. On the cold-start mid-Re-Fly path
            // the saved tree's ActiveRecordingId is already the fork id
            // (`already-pointing-at-marker`), so the swap is a no-op,
            // the rebuild never runs, and the freshly-attached fork's
            // pid lingers as a stale background entry. The double-rebuild
            // when ShouldSwap IS true is cheap.
            return RewindInvoker.EnsureForkAttachedToTree(
                tree, committedFork, "RestoreActiveTreeFromPending:reconcile");
        }

        internal static bool ShouldUsePreReFlyAnchorTrajectory(
            Recording candidate,
            uint anchorVesselId,
            string victimRecordingId,
            ReFlySessionMarker marker)
        {
            if (candidate == null || marker == null)
                return false;
            if (anchorVesselId == 0u || candidate.VesselPersistentId != anchorVesselId)
                return false;
            if (string.IsNullOrEmpty(candidate.RecordingId)
                || !string.Equals(candidate.RecordingId, marker.ActiveReFlyRecordingId, StringComparison.Ordinal))
                return false;
            // Accept both the post-#734 fork shape (InPlaceContinuation flag,
            // snapshot lives on the fork) and the pre-fork legacy in-place
            // pattern (active == origin, snapshot lives on origin).
            if (!ReFlySessionMarker.IsInPlaceContinuation(marker))
                return false;
            if (!candidate.HasPreReFlyAnchorTrajectory(marker.SessionId))
                return false;
            if (!string.IsNullOrEmpty(victimRecordingId)
                && string.Equals(victimRecordingId, candidate.RecordingId, StringComparison.Ordinal))
                return false;
            return true;
        }

        bool TryResolveRecordedAnchorPoseFromCandidate(
            Recording rec,
            uint anchorVesselId,
            string victimRecordingId,
            double targetUT,
            HashSet<string> seenRecordingIds,
            bool requireCoverage,
            out RelativeAnchorPose pose)
        {
            pose = default(RelativeAnchorPose);
            if (rec == null || rec.VesselPersistentId != anchorVesselId)
                return false;
            if (!string.IsNullOrEmpty(rec.RecordingId))
            {
                if (!string.IsNullOrEmpty(victimRecordingId)
                    && string.Equals(rec.RecordingId, victimRecordingId, StringComparison.Ordinal))
                    return false;
                if (seenRecordingIds != null && !seenRecordingIds.Add(rec.RecordingId))
                    return false;
            }

            if (rec.TrackSections != null && rec.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, targetUT);
                if (sectionIdx < 0)
                {
                    if (requireCoverage)
                        return false;

                    if (!TryResolveNearestRecordedAnchorAbsoluteSectionPose(
                            rec, targetUT, out pose, out double fallbackGapSeconds))
                    {
                        return false;
                    }

                    MaybeWarnLargeRecordedAnchorFallbackGap(
                        rec, anchorVesselId, victimRecordingId, targetUT, fallbackGapSeconds);
                    return true;
                }

                TrackSection section = rec.TrackSections[sectionIdx];
                if (section.referenceFrame != ReferenceFrame.Absolute)
                {
                    if (requireCoverage)
                        return false;

                    if (!TryResolveNearestRecordedAnchorAbsoluteSectionPose(
                            rec, targetUT, out pose, out double fallbackGapSeconds))
                    {
                        return false;
                    }

                    MaybeWarnLargeRecordedAnchorFallbackGap(
                        rec, anchorVesselId, victimRecordingId, targetUT, fallbackGapSeconds);
                    return true;
                }

                List<TrajectoryPoint> anchorPoints =
                    section.frames != null && section.frames.Count > 0
                        ? section.frames
                        : rec.Points;
                bool resolved = TryResolveRecordedAnchorPointPose(
                    anchorPoints,
                    targetUT,
                    requireCoverage: requireCoverage,
                    out pose);
                if (resolved && !requireCoverage)
                {
                    MaybeWarnLargeRecordedAnchorFallbackGap(
                        rec,
                        anchorVesselId,
                        victimRecordingId,
                        targetUT,
                        DistanceOutsideRecordedAnchorCoverage(anchorPoints, targetUT));
                }

                return resolved;
            }

            bool resolvedFromPoints = TryResolveRecordedAnchorPointPose(
                rec.Points,
                targetUT,
                requireCoverage: requireCoverage,
                out pose);
            if (resolvedFromPoints && !requireCoverage)
            {
                MaybeWarnLargeRecordedAnchorFallbackGap(
                    rec,
                    anchorVesselId,
                    victimRecordingId,
                    targetUT,
                    DistanceOutsideRecordedAnchorCoverage(rec.Points, targetUT));
            }

            return resolvedFromPoints;
        }

        bool TryResolveNearestRecordedAnchorAbsoluteSectionPose(
            Recording rec,
            double targetUT,
            out RelativeAnchorPose pose,
            out double fallbackGapSeconds)
        {
            pose = default(RelativeAnchorPose);
            fallbackGapSeconds = double.MaxValue;
            if (rec?.TrackSections == null || rec.TrackSections.Count == 0)
                return false;

            List<TrajectoryPoint> bestPoints = null;
            double bestGap = double.MaxValue;
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection section = rec.TrackSections[i];
                if (section.referenceFrame != ReferenceFrame.Absolute)
                    continue;

                List<TrajectoryPoint> points =
                    section.frames != null && section.frames.Count > 0
                        ? section.frames
                        : rec.Points;
                if (points == null || points.Count == 0)
                    continue;

                double gap = DistanceOutsideRecordedAnchorCoverage(points, targetUT);
                if (gap < bestGap)
                {
                    bestGap = gap;
                    bestPoints = points;
                }
            }

            bool resolved = TryResolveRecordedAnchorPointPose(
                bestPoints,
                targetUT,
                requireCoverage: false,
                out pose);
            if (resolved)
                fallbackGapSeconds = bestGap;
            return resolved;
        }

        void MaybeWarnLargeRecordedAnchorFallbackGap(
            Recording rec,
            uint anchorVesselId,
            string victimRecordingId,
            double targetUT,
            double fallbackGapSeconds)
        {
            if (!ShouldWarnRecordedAnchorFallbackGap(fallbackGapSeconds))
                return;

            string anchorRecordingId = rec?.RecordingId;
            string key = string.Format(CultureInfo.InvariantCulture,
                "recorded-anchor-fallback-gap|anchorRec={0}|victimRec={1}|anchorPid={2}",
                string.IsNullOrEmpty(anchorRecordingId) ? "(none)" : anchorRecordingId,
                string.IsNullOrEmpty(victimRecordingId) ? "(none)" : victimRecordingId,
                anchorVesselId);
            ParsekLog.WarnRateLimited(
                "Anchor",
                key,
                BuildRecordedAnchorFallbackGapLog(
                    anchorRecordingId,
                    victimRecordingId,
                    anchorVesselId,
                    targetUT,
                    fallbackGapSeconds),
                minIntervalSeconds: 30.0);
        }

        internal static bool ShouldWarnRecordedAnchorFallbackGap(double fallbackGapSeconds)
        {
            return !double.IsNaN(fallbackGapSeconds)
                && !double.IsInfinity(fallbackGapSeconds)
                && fallbackGapSeconds > RecordedAnchorFallbackGapWarnThresholdSeconds;
        }

        internal static string BuildRecordedAnchorFallbackGapLog(
            string anchorRecordingId,
            string victimRecordingId,
            uint anchorVesselId,
            double targetUT,
            double fallbackGapSeconds)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "recorded-anchor-fallback-gap: anchorRec={0} victimRec={1} anchorPid={2} " +
                "targetUT={3:F2} gap={4:F2}s reason=active-refly-live-anchor-bypass",
                string.IsNullOrEmpty(anchorRecordingId) ? "(none)" : anchorRecordingId,
                string.IsNullOrEmpty(victimRecordingId) ? "(none)" : victimRecordingId,
                anchorVesselId,
                targetUT,
                fallbackGapSeconds);
        }

        internal static double DistanceOutsideRecordedAnchorCoverage(
            List<TrajectoryPoint> points,
            double targetUT)
        {
            if (points == null || points.Count == 0)
                return double.MaxValue;

            double firstUT = points[0].ut;
            double lastUT = points[points.Count - 1].ut;
            if (targetUT < firstUT)
                return firstUT - targetUT;
            if (targetUT > lastUT)
                return targetUT - lastUT;
            return 0.0;
        }

        internal static bool RecordedAnchorPointListCoversUT(
            List<TrajectoryPoint> points,
            double targetUT)
        {
            if (points == null || points.Count == 0)
                return false;

            const double eps = 0.0001;
            double firstUT = points[0].ut;
            double lastUT = points[points.Count - 1].ut;
            return targetUT >= firstUT - eps && targetUT <= lastUT + eps;
        }

        internal static string BuildRelativeOffsetAppliedLog(
            int recordingFormatVersion,
            double dx,
            double dy,
            double dz,
            uint anchorVesselId,
            bool anchorFromRecordedTrajectory)
        {
            return $"RELATIVE playback: contract=anchor-local " +
                $"version={recordingFormatVersion} dx={dx:F2} dy={dy:F2} dz={dz:F2} " +
                $"|offset|={System.Math.Sqrt(dx * dx + dy * dy + dz * dz):F2}m anchor={anchorVesselId} " +
                $"source={(anchorFromRecordedTrajectory ? "recorded" : "live")}";
        }

        internal static string BuildRecordedRelativeOffsetAppliedLog(
            int recordingFormatVersion,
            double dx,
            double dy,
            double dz,
            string anchorRecordingId)
        {
            return $"RELATIVE playback: contract=anchor-local " +
                $"version={recordingFormatVersion} dx={dx:F2} dy={dy:F2} dz={dz:F2} " +
                $"|offset|={System.Math.Sqrt(dx * dx + dy * dy + dz * dz):F2}m " +
                $"anchorRec={(string.IsNullOrEmpty(anchorRecordingId) ? "(missing)" : anchorRecordingId)} source=recorded";
        }

        bool TryResolveRecordedAnchorPointPose(
            List<TrajectoryPoint> points,
            double targetUT,
            bool requireCoverage,
            out RelativeAnchorPose pose)
        {
            pose = default(RelativeAnchorPose);
            if (points == null || points.Count == 0)
                return false;
            if (requireCoverage && !RecordedAnchorPointListCoversUT(points, targetUT))
                return false;
            if (points.Count == 1)
                return TryBuildRecordedAnchorPoseFromPoint(points[0], out pose);

            int cachedIndex = 0;
            TrajectoryPoint before;
            TrajectoryPoint after;
            float t;
            bool hasSegment = TrajectoryMath.InterpolatePoints(
                points, ref cachedIndex, targetUT, out before, out after, out t);
            if (!hasSegment)
                return requireCoverage
                    ? false
                    : TryBuildRecordedAnchorPoseFromPoint(before, out pose);

            CelestialBody bodyBefore = FlightGlobals.Bodies?.Find(b => b.name == before.bodyName);
            CelestialBody bodyAfter = FlightGlobals.Bodies?.Find(b => b.name == after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
                return false;

            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);
            Quaternion surfaceRot = TrajectoryMath.SanitizeQuaternion(
                Quaternion.Slerp(before.rotation, after.rotation, t));

            pose.worldPos = Vector3d.Lerp(posBefore, posAfter, t);
            pose.worldRotation = bodyBefore.bodyTransform.rotation * surfaceRot;
            pose.fromRecordedTrajectory = true;
            pose.hasSurfacePose = true;
            pose.bodyBefore = bodyBefore;
            pose.bodyAfter = bodyAfter;
            pose.latBefore = before.latitude;
            pose.lonBefore = before.longitude;
            pose.altBefore = before.altitude;
            pose.latAfter = after.latitude;
            pose.lonAfter = after.longitude;
            pose.altAfter = after.altitude;
            pose.t = t;
            pose.surfaceRot = surfaceRot;
            return IsFiniteVector3d(pose.worldPos);
        }

        bool TryBuildRecordedAnchorPoseFromPoint(
            TrajectoryPoint point, out RelativeAnchorPose pose)
        {
            pose = default(RelativeAnchorPose);
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == point.bodyName);
            if (body == null || body.bodyTransform == null)
                return false;

            Quaternion surfaceRot = TrajectoryMath.SanitizeQuaternion(point.rotation);
            pose.worldPos = body.GetWorldSurfacePosition(
                point.latitude, point.longitude, point.altitude);
            pose.worldRotation = body.bodyTransform.rotation * surfaceRot;
            pose.fromRecordedTrajectory = true;
            pose.hasSurfacePose = true;
            pose.bodyBefore = body;
            pose.bodyAfter = body;
            pose.latBefore = point.latitude;
            pose.lonBefore = point.longitude;
            pose.altBefore = point.altitude;
            pose.latAfter = point.latitude;
            pose.lonAfter = point.longitude;
            pose.altAfter = point.altitude;
            pose.t = 0f;
            pose.surfaceRot = surfaceRot;
            return IsFiniteVector3d(pose.worldPos);
        }

        bool TryResolveSurfaceWorldPosition(SurfacePosition surfacePos, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == surfacePos.body);
            if (body == null)
                return false;

            worldPos = body.GetWorldSurfacePosition(
                surfacePos.latitude, surfacePos.longitude, surfacePos.altitude);
            return true;
        }

        /// <summary>
        /// Unified positioning method for loop ghosts. Selects between anchor-relative
        /// (InterpolateAndPositionRelative) and absolute (InterpolateAndPosition) based
        /// on the useAnchor flag and the TrackSection at the given loopUT.
        /// </summary>
        void PositionLoopGhost(
            GameObject ghost,
            IPlaybackTrajectory rec,
            ref int playbackIdx,
            double loopUT,
            int recIdx,
            int ghostIdSalt,
            bool useAnchor,
            GhostPlaybackState retireSignalState,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            if (TryInterpolateAndPositionCheckpointSection(
                recIdx,
                rec,
                ghost,
                ref playbackIdx,
                loopUT,
                allowActivation,
                out interpResult))
            {
                return;
            }

            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, loopUT);
            if (sectionIdx >= 0 && rec.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative)
            {
                var section = rec.TrackSections[sectionIdx];
                var sectionFrames = section.frames ?? rec.Points;
                uint anchorVesselId = section.anchorVesselId != 0
                    ? section.anchorVesselId
                    : rec.LoopAnchorVesselId;

                if (useAnchor)
                {
                    long relKey = ((long)recIdx << 32) | anchorVesselId;
                    if (loggedRelativeStart.Add(relKey))
                        ParsekLog.Info("Loop",
                            $"Anchor-relative loop playback started: recording #{recIdx} " +
                            $"\"{rec.VesselName}\" anchorPid={anchorVesselId} " +
                            $"contract=anchor-local " +
                            $"version={rec.RecordingFormatVersion} " +
                            $"sectionUT=[{section.startUT:F1},{section.endUT:F1}]");

                    InterpolateAndPositionLoopRelative(
                        ghost, sectionFrames, ref playbackIdx, loopUT,
                        anchorVesselId, rec.RecordingFormatVersion,
                        recIdx, rec.RecordingId, rec.VesselName, retireSignalState,
                        allowActivation, out interpResult);
                    if (GhostPlaybackEngine.IsInterpolationResultValid(interpResult))
                        interpResult = WithRelativeSectionSurfaceVelocity(interpResult, section, loopUT);
                    return;
                }

                if (ghost.activeSelf) ghost.SetActive(false);
                if (retireSignalState != null)
                    retireSignalState.anchorRetiredThisFrame = true;
                long missingAnchorKey = RelativeAnchorResolution.DedupeKey(recIdx, anchorVesselId);
                if (loggedAnchorNotFound.Add(missingAnchorKey))
                    ParsekLog.Warn("Anchor",
                        RelativeAnchorResolution.FormatRetiredMessage(
                            recIdx,
                            rec.VesselName,
                            anchorVesselId,
                            "PositionLoopGhost"));
                interpResult = InterpolationResult.Zero;
                return;
            }

            if (sectionIdx >= 0
                && TryGetAbsoluteSectionPlaybackFramesForPlayback(
                    rec.TrackSections,
                    sectionIdx,
                    out List<TrajectoryPoint> absoluteSectionFrames,
                    out _))
            {
                InterpolateAndPosition(
                    ghost,
                    absoluteSectionFrames,
                    null,
                    ref playbackIdx,
                    loopUT,
                    ghostIdSalt,
                    out interpResult,
                    allowActivation: allowActivation,
                    skipOrbitSegments: true,
                    recordingId: rec.RecordingId,
                    sectionIndex: sectionIdx);
                return;
            }

            // Absolute positioning fallback
            // Phase 1: pass section context for spline lookup. The fallback is
            // the body-fixed loop path, which is exactly where ABSOLUTE
            // ExoPropulsive / ExoBallistic splines are meant to apply.
            int splineSectionIdx = sectionIdx;
            InterpolateAndPosition(ghost, rec.Points, rec.OrbitSegments,
                ref playbackIdx, loopUT, ghostIdSalt, out interpResult,
                allowActivation: allowActivation,
                recordingId: rec.RecordingId, sectionIndex: splineSectionIdx,
                pointFrameSource: "loop-flat-points",
                flatPointSections: rec.TrackSections);
        }

        /// <summary>
        /// Positions a ghost using RELATIVE reference frame data.
        /// Ghost position/rotation resolve through the recording's versioned RELATIVE
        /// contract (legacy world-offset v5-and-older, anchor-local v6+).
        /// </summary>
        void InterpolateAndPositionRecordedRelative(
            GameObject ghost,
            IPlaybackTrajectory traj,
            List<TrajectoryPoint> frames,
            ref int cachedIndex,
            double targetUT,
            RelativeSectionPlaybackTarget target,
            int recordingFormatVersion,
            int recordingIndex,
            string recordingVesselName,
            GhostPlaybackState retireSignalState,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            if (GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                    traj,
                    targetUT,
                    out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic))
            {
                RetireUnresolvedRecordedRelative(
                    ghost,
                    recordingIndex,
                    recordingVesselName,
                    target,
                    retireSignalState,
                    "InterpolateAndPositionRecordedRelative",
                    resolverReason:
                        "parent-anchored-debris-outside-relative-coverage coverageReason="
                        + (diagnostic.Reason ?? "relative-and-body-fixed-frames-out-of-range"));
                interpResult = InterpolationResult.Zero;
                return;
            }

            if (GhostPlaybackEngine.ShouldSkipRecordedRelativeResolverForAuthoredFrameGap(
                    traj,
                    targetUT,
                    out diagnostic))
            {
                RetireUnresolvedRecordedRelative(
                    ghost,
                    recordingIndex,
                    recordingVesselName,
                    target,
                    retireSignalState,
                    "InterpolateAndPositionRecordedRelative",
                    resolverReason:
                        "body-fixed-primary-required; recorded-relative fallback suppressed coverageReason="
                        + (diagnostic.Reason ?? "covered-by-body-fixed-primary"));
                interpResult = InterpolationResult.Zero;
                return;
            }

            if (frames == null || frames.Count == 0)
            {
                interpResult = InterpolationResult.Zero;
                ghost.SetActive(false);
                return;
            }

            int indexBefore = TrajectoryMath.FindWaypointIndex(frames, ref cachedIndex, targetUT);

            if (indexBefore < 0)
            {
                if (TryPositionGhostRecordedRelativeAt(
                        ghost,
                        frames[0],
                        target,
                        recordingFormatVersion,
                        allowActivation,
                        out double firstAltitude,
                        out RelativeAnchorResolveFailure firstFailure))
                {
                    interpResult = new InterpolationResult(
                        frames[0].velocity,
                        frames[0].bodyName,
                        firstAltitude);
                    return;
                }

                if (TryUseRelativeBodyFixedPrimaryFallback(
                        recordingIndex,
                        traj,
                        retireSignalState,
                        target,
                        targetUT,
                        ref cachedIndex,
                        out interpResult,
                        resolverFailure: firstFailure))
                    return;

                RetireUnresolvedRecordedRelative(
                    ghost,
                    recordingIndex,
                    recordingVesselName,
                    target,
                    retireSignalState,
                    "InterpolateAndPositionRecordedRelative",
                    firstFailure.Outcome.ToString(),
                    RelativeAnchorResolveFailure.ReasonOrFallback(firstFailure, null));
                interpResult = InterpolationResult.Zero;
                return;
            }

            TrajectoryPoint before = frames[indexBefore];
            TrajectoryPoint after = frames[indexBefore + 1];

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                if (TryPositionGhostRecordedRelativeAt(
                        ghost,
                        before,
                        target,
                        recordingFormatVersion,
                        allowActivation,
                        out double pointAltitude,
                        out RelativeAnchorResolveFailure pointFailure))
                {
                    interpResult = new InterpolationResult(
                        before.velocity,
                        before.bodyName,
                        pointAltitude);
                    return;
                }

                if (TryUseRelativeBodyFixedPrimaryFallback(
                        recordingIndex,
                        traj,
                        retireSignalState,
                        target,
                        targetUT,
                        ref cachedIndex,
                        out interpResult,
                        resolverFailure: pointFailure))
                    return;

                RetireUnresolvedRecordedRelative(
                    ghost,
                    recordingIndex,
                    recordingVesselName,
                    target,
                    retireSignalState,
                    "InterpolateAndPositionRecordedRelative",
                    pointFailure.Outcome.ToString(),
                    RelativeAnchorResolveFailure.ReasonOrFallback(pointFailure, null));
                interpResult = InterpolationResult.Zero;
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            Vector3d beforeOffset = new Vector3d(before.latitude, before.longitude, before.altitude);
            Vector3d afterOffset = new Vector3d(after.latitude, after.longitude, after.altitude);
            bool correctedBefore = TryCorrectIsolatedRecordedRelativeOffsetSpike(
                frames,
                indexBefore,
                out Vector3d correctedBeforeOffset,
                out double beforeSpikeDeviationMeters);
            bool correctedAfter = TryCorrectIsolatedRecordedRelativeOffsetSpike(
                frames,
                indexBefore + 1,
                out Vector3d correctedAfterOffset,
                out double afterSpikeDeviationMeters);
            if (correctedBefore)
                beforeOffset = correctedBeforeOffset;
            if (correctedAfter)
                afterOffset = correctedAfterOffset;
            if (correctedBefore || correctedAfter)
                LogRecordedRelativeOffsetSpikeCorrection(
                    target,
                    recordingIndex,
                    indexBefore,
                    correctedBefore,
                    beforeSpikeDeviationMeters,
                    correctedAfter,
                    afterSpikeDeviationMeters);

            double dx = beforeOffset.x + (afterOffset.x - beforeOffset.x) * t;
            double dy = beforeOffset.y + (afterOffset.y - beforeOffset.y) * t;
            double dz = beforeOffset.z + (afterOffset.z - beforeOffset.z) * t;
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);
            interpolatedRot = TrajectoryMath.SanitizeQuaternion(interpolatedRot);

            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            if (!TryResolveRecordedRelativeAnchorPose(
                    target,
                    targetUT,
                    out RelativeAnchorPose anchorPose,
                    out RelativeAnchorResolveFailure anchorFailure))
            {
                if (TryUseRelativeBodyFixedPrimaryFallback(
                        recordingIndex,
                        traj,
                        retireSignalState,
                        target,
                        targetUT,
                        ref cachedIndex,
                        out interpResult,
                        resolverFailure: anchorFailure))
                    return;

                RetireUnresolvedRecordedRelative(
                    ghost,
                    recordingIndex,
                    recordingVesselName,
                    target,
                    retireSignalState,
                    "InterpolateAndPositionRecordedRelative",
                    anchorFailure.Outcome.ToString(),
                    RelativeAnchorResolveFailure.ReasonOrFallback(anchorFailure, null));
                interpResult = InterpolationResult.Zero;
                return;
            }

            Vector3d ghostPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                anchorPose.worldPos,
                anchorPose.worldRotation,
                dx,
                dy,
                dz);

            if (double.IsNaN(ghostPos.x) || double.IsNaN(ghostPos.y) || double.IsNaN(ghostPos.z))
            {
                ParsekLog.Warn("Flight", "InterpolateAndPositionRecordedRelative: NaN in ghost position, using anchor position");
                ghostPos = anchorPose.worldPos;
            }

            ghost.transform.position = ghostPos;
            ghost.transform.rotation = TrajectoryMath.ResolveRelativePlaybackRotation(
                anchorPose.worldRotation,
                interpolatedRot);
            GhostRenderTrace.EmitRelativeResolver(
                target.RecordingId,
                recordingIndex,
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                targetUT,
                "recorded-relative-position",
                "resolved",
                0u,
                target.AnchorRecordingId,
                success: true,
                fromRecordedTrajectory: anchorPose.fromRecordedTrajectory,
                anchorPosition: anchorPose.worldPos,
                anchorRotation: anchorPose.worldRotation,
                localOffset: new Vector3d(dx, dy, dz),
                outputPosition: ghostPos);
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT;
            if (GhostRenderTrace.ShouldEmitPhase(target.RecordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    target.RecordingId,
                    recordingIndex,
                    traceCurrentUT,
                    targetUT,
                    "UpdatePath",
                    "mode=RecordedRelative sectionIndex=" + target.SectionIndex.ToString(CultureInfo.InvariantCulture)
                    + " beforeUT=" + before.ut.ToString("F3", CultureInfo.InvariantCulture)
                    + " afterUT=" + after.ut.ToString("F3", CultureInfo.InvariantCulture)
                    + " t=" + t.ToString("F4", CultureInfo.InvariantCulture)
                    + " anchorRec=" + (target.AnchorRecordingId ?? "<none>")
                    + " localOffset=" + GhostRenderTrace.FormatVector3d(new Vector3d(dx, dy, dz))
                    + " anchorPos=" + GhostRenderTrace.FormatVector3d(anchorPose.worldPos)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghostPos)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
            }

            ParsekLog.VerboseRateLimited("Flight", "recorded-relative-offset-applied",
                BuildRecordedRelativeOffsetAppliedLog(
                    recordingFormatVersion,
                    dx,
                    dy,
                    dz,
                    target.AnchorRecordingId),
                2.0);

            string bodyName = before.bodyName ?? "Kerbin";
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            double alt = body != null ? body.GetAltitude(ghostPos) : 0;

            AddOrReplaceGhostPosEntry(new GhostPosEntry
            {
                ghost = ghost,
                mode = GhostPosMode.Relative,
                recordingId = target.RecordingId,
                recordingIndex = recordingIndex,
                hasRecordingIndex = true,
                anchorVesselId = 0u,
                relDx = dx, relDy = dy, relDz = dz,
                relativeRot = interpolatedRot,
                relativeBodyName = bodyName,
                bodyBefore = body,
                latBefore = before.latitude, lonBefore = before.longitude, altBefore = before.altitude,
                relativeRecordedAnchorPose = RelativeAnchorPoseSnapshot.FromRecordedPose(anchorPose),
                anchorRecordingId = target.RecordingId,
                anchorSectionIndex = target.SectionIndex,
                relativeAnchorRecordingId = target.AnchorRecordingId,
                relativeLoopLiveAnchor = false,
                pointUT = targetUT,
            });

            interpResult = WithRelativeSectionSurfaceVelocity(
                new InterpolationResult(
                    Vector3.Lerp(before.velocity, after.velocity, t),
                    bodyName,
                    alt),
                target.Section,
                targetUT);
        }

        internal static bool TryCorrectIsolatedRecordedRelativeOffsetSpike(
            IList<TrajectoryPoint> frames,
            int sampleIndex,
            out Vector3d correctedOffset,
            out double deviationMeters)
        {
            correctedOffset = Vector3d.zero;
            deviationMeters = 0.0;
            if (frames == null || sampleIndex <= 0 || sampleIndex >= frames.Count - 1)
                return false;

            TrajectoryPoint previous = frames[sampleIndex - 1];
            TrajectoryPoint current = frames[sampleIndex];
            TrajectoryPoint next = frames[sampleIndex + 1];
            double span = next.ut - previous.ut;
            if (span <= 0.0001 || current.ut < previous.ut || current.ut > next.ut)
                return false;

            Vector3d previousOffset = RecordedRelativeOffset(previous);
            Vector3d currentOffset = RecordedRelativeOffset(current);
            Vector3d nextOffset = RecordedRelativeOffset(next);
            if (!IsFinite(previousOffset) || !IsFinite(currentOffset) || !IsFinite(nextOffset))
                return false;

            double neighborDistance = Vector3d.Distance(previousOffset, nextOffset);
            if (neighborDistance > RecordedRelativeOffsetSpikeNeighborMaxMeters)
                return false;

            double t = (current.ut - previous.ut) / span;
            t = Math.Max(0.0, Math.Min(1.0, t));
            correctedOffset = previousOffset + (nextOffset - previousOffset) * t;
            deviationMeters = Vector3d.Distance(currentOffset, correctedOffset);
            if (deviationMeters < RecordedRelativeOffsetSpikeMinDeviationMeters)
                return false;

            double prevDistance = Vector3d.Distance(previousOffset, currentOffset);
            double nextDistance = Vector3d.Distance(currentOffset, nextOffset);
            return prevDistance >= RecordedRelativeOffsetSpikeMinDeviationMeters
                && nextDistance >= RecordedRelativeOffsetSpikeMinDeviationMeters;
        }

        private static Vector3d RecordedRelativeOffset(TrajectoryPoint point)
        {
            return new Vector3d(point.latitude, point.longitude, point.altitude);
        }

        private static void LogRecordedRelativeOffsetSpikeCorrection(
            RelativeSectionPlaybackTarget target,
            int recordingIndex,
            int indexBefore,
            bool correctedBefore,
            double beforeSpikeDeviationMeters,
            bool correctedAfter,
            double afterSpikeDeviationMeters)
        {
            string key = string.Concat(
                "recorded-relative-offset-spike-corrected|",
                target.RecordingId ?? "(none)",
                "|",
                target.SectionIndex.ToString(CultureInfo.InvariantCulture),
                "|",
                indexBefore.ToString(CultureInfo.InvariantCulture),
                "|",
                correctedBefore ? "B" : "-",
                correctedAfter ? "A" : "-");
            ParsekLog.WarnRateLimited(
                "Playback",
                key,
                $"Recorded Relative local-offset spike corrected: recording=#{recordingIndex} " +
                $"recordingId={ShortRecordingId(target.RecordingId)} " +
                $"sectionIndex={target.SectionIndex} bracketIndex={indexBefore} " +
                $"anchorRec={target.AnchorRecordingId ?? "(missing)"} " +
                $"correctedBefore={correctedBefore} beforeDeviation={beforeSpikeDeviationMeters.ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"correctedAfter={correctedAfter} afterDeviation={afterSpikeDeviationMeters.ToString("F2", CultureInfo.InvariantCulture)}m",
                5.0);
        }

        private bool TryPositionGhostRecordedRelativeAt(
            GameObject ghost,
            TrajectoryPoint point,
            RelativeSectionPlaybackTarget target,
            int recordingFormatVersion,
            bool allowActivation,
            out double altitude,
            out RelativeAnchorResolveFailure failure)
        {
            altitude = 0.0;
            failure = default;
            if (!TryResolveRecordedRelativeAnchorPose(
                    target,
                    point.ut,
                    out RelativeAnchorPose anchorPose,
                    out failure))
            {
                return false;
            }

            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            Quaternion sanitized = TrajectoryMath.SanitizeQuaternion(point.rotation);
            double dx = point.latitude;
            double dy = point.longitude;
            double dz = point.altitude;
            string bodyName = point.bodyName ?? "Kerbin";

            Vector3d ghostPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                anchorPose.worldPos,
                anchorPose.worldRotation,
                dx,
                dy,
                dz);

            ghost.transform.position = ghostPos;
            ghost.transform.rotation = TrajectoryMath.ResolveRelativePlaybackRotation(
                anchorPose.worldRotation,
                sanitized);
            GhostRenderTrace.EmitRelativeResolver(
                target.RecordingId,
                -1,
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut,
                point.ut,
                "recorded-relative-single",
                "resolved",
                0u,
                target.AnchorRecordingId,
                success: true,
                fromRecordedTrajectory: anchorPose.fromRecordedTrajectory,
                anchorPosition: anchorPose.worldPos,
                anchorRotation: anchorPose.worldRotation,
                localOffset: new Vector3d(dx, dy, dz),
                outputPosition: ghostPos);
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut;
            if (GhostRenderTrace.ShouldEmitPhase(target.RecordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    target.RecordingId,
                    -1,
                    traceCurrentUT,
                    point.ut,
                    "UpdatePath",
                    "mode=RecordedRelativeSingle sectionIndex=" + target.SectionIndex.ToString(CultureInfo.InvariantCulture)
                    + " anchorRec=" + (target.AnchorRecordingId ?? "<none>")
                    + " localOffset=" + GhostRenderTrace.FormatVector3d(new Vector3d(dx, dy, dz))
                    + " anchorPos=" + GhostRenderTrace.FormatVector3d(anchorPose.worldPos)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghostPos)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
            }

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            altitude = body != null ? body.GetAltitude(ghostPos) : 0.0;
            AddOrReplaceGhostPosEntry(new GhostPosEntry
            {
                ghost = ghost,
                mode = GhostPosMode.Relative,
                recordingId = target.RecordingId,
                anchorVesselId = 0u,
                relDx = dx, relDy = dy, relDz = dz,
                relativeRot = sanitized,
                relativeBodyName = bodyName,
                bodyBefore = body,
                latBefore = dx, lonBefore = dy, altBefore = dz,
                relativeRecordedAnchorPose = RelativeAnchorPoseSnapshot.FromRecordedPose(anchorPose),
                anchorRecordingId = target.RecordingId,
                anchorSectionIndex = target.SectionIndex,
                relativeAnchorRecordingId = target.AnchorRecordingId,
                relativeLoopLiveAnchor = false,
                pointUT = point.ut,
            });
            return true;
        }

        private bool TryResolveRecordedRelativeAnchorPose(
            RelativeSectionPlaybackTarget target,
            double targetUT,
            out RelativeAnchorPose pose)
        {
            return TryResolveRecordedRelativeAnchorPose(
                target,
                targetUT,
                out pose,
                out _);
        }

        private bool TryResolveRecordedRelativeAnchorPose(
            RelativeSectionPlaybackTarget target,
            double targetUT,
            out RelativeAnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            return TryResolveRecordedRelativeAnchorPose(
                target.RecordingId,
                target.AnchorRecordingId,
                target.SectionIndex,
                targetUT,
                out pose,
                out failure);
        }

        private bool TryResolveRecordedRelativeAnchorPose(
            string recordingId,
            string anchorRecordingId,
            int sectionIndex,
            double targetUT,
            out RelativeAnchorPose pose)
        {
            return TryResolveRecordedRelativeAnchorPose(
                recordingId,
                anchorRecordingId,
                sectionIndex,
                targetUT,
                out pose,
                out _);
        }

        private bool TryResolveRecordedRelativeAnchorPose(
            string recordingId,
            string anchorRecordingId,
            int sectionIndex,
            double targetUT,
            out RelativeAnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default(RelativeAnchorPose);
            failure = default;
            if (string.IsNullOrWhiteSpace(anchorRecordingId))
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.PreconditionFailed,
                    "anchor-recording-id-missing",
                    recordingId,
                    anchorRecordingId,
                    targetUT,
                    sectionIndex);
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "recorded-anchor-pose",
                    "anchor-recording-id-missing",
                    0u,
                    anchorRecordingId,
                    success: false,
                    fromRecordedTrajectory: true,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                ParsekLog.WarnRateLimited(
                    "Anchor",
                    "recorded-relative-missing-anchor-recording-id|" +
                    (recordingId ?? "(none)") + "|" +
                    sectionIndex.ToString(CultureInfo.InvariantCulture),
                    $"recorded-relative-unresolved: reason=anchor-recording-id-missing " +
                    $"recordingId={recordingId ?? "(none)"} " +
                    $"sectionIndex={sectionIndex} ut={targetUT.ToString("R", CultureInfo.InvariantCulture)}",
                    5.0);
                return false;
            }

            anchorRecordingId = anchorRecordingId.Trim();
            if (!TryFindRelativeAnchorFocusTree(recordingId, out RecordingTree focusTree))
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.Other,
                    "focus-tree-missing",
                    recordingId,
                    anchorRecordingId,
                    targetUT,
                    sectionIndex);
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "recorded-anchor-pose",
                    "focus-tree-missing",
                    0u,
                    anchorRecordingId,
                    success: false,
                    fromRecordedTrajectory: true,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                ParsekLog.WarnRateLimited(
                    "Anchor",
                    "recorded-relative-focus-tree-missing|" + (recordingId ?? "(none)"),
                    $"recorded-relative-unresolved: reason=focus-tree-missing " +
                    $"recordingId={recordingId ?? "(none)"} " +
                    $"anchorRecordingId={anchorRecordingId} " +
                    $"ut={targetUT.ToString("R", CultureInfo.InvariantCulture)}",
                    5.0);
                return false;
            }

            var context = BuildFlightRelativeAnchorResolverContext(
                focusTree,
                recordingId,
                ParsekScenario.Instance?.ActiveReFlySessionMarker);
            HashSet<string> visited = recordedRelativeAnchorVisited;
            visited.Clear();
            bool resolved;
            AnchorPose anchorPose;
            try
            {
                resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                    context,
                    anchorRecordingId,
                    targetUT,
                    visited,
                    out anchorPose,
                    out failure);
            }
            finally
            {
                visited.Clear();
            }

            if (!resolved)
            {
                if (!failure.HasFailure)
                {
                    failure = RelativeAnchorResolveFailure.Create(
                        RelativeAnchorResolveOutcome.Other,
                        "resolver-unresolved",
                        recordingId,
                        anchorRecordingId,
                        targetUT,
                        sectionIndex);
                }
                string reason = RelativeAnchorResolveFailure.ReasonOrFallback(
                    failure,
                    "resolver-unresolved");
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "recorded-anchor-pose",
                    reason,
                    0u,
                    anchorRecordingId,
                    success: false,
                    fromRecordedTrajectory: true,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                return false;
            }

            pose.worldPos = anchorPose.WorldPos;
            pose.worldRotation = anchorPose.WorldRotation;
            pose.fromRecordedTrajectory = true;
            GhostRenderTrace.EmitRelativeResolver(
                recordingId,
                -1,
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                targetUT,
                "recorded-anchor-pose",
                "resolved",
                0u,
                anchorRecordingId,
                success: true,
                fromRecordedTrajectory: true,
                anchorPosition: pose.worldPos,
                anchorRotation: pose.worldRotation,
                localOffset: Vector3d.zero,
                outputPosition: pose.worldPos);
            return true;
        }

        private static bool TryBuildRelativeAnchorResolverContext(
            string recordingId,
            out RelativeAnchorResolverContext context)
        {
            context = default;
            if (!TryFindRelativeAnchorFocusTree(recordingId, out RecordingTree focusTree))
                return false;

            context = BuildFlightRelativeAnchorResolverContext(
                focusTree,
                recordingId,
                ParsekScenario.Instance?.ActiveReFlySessionMarker);
            return true;
        }

        private static bool TryFindRelativeAnchorFocusTree(
            string recordingId,
            out RecordingTree focusTree)
        {
            focusTree = null;
            if (string.IsNullOrEmpty(recordingId))
                return false;

            List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    RecordingTree tree = committedTrees[i];
                    if (tree?.Recordings != null
                        && tree.Recordings.ContainsKey(recordingId))
                    {
                        focusTree = tree;
                        return true;
                    }
                }
            }

            RecordingTree pending = PendingTreeSlot.HasPendingTree
                ? PendingTreeSlot.PendingTree
                : null;
            if (pending?.Recordings != null && pending.Recordings.ContainsKey(recordingId))
            {
                focusTree = pending;
                return true;
            }

            return false;
        }

        private bool TryUseRelativeBodyFixedPrimaryFallback(
            int recordingIndex,
            IPlaybackTrajectory trajectory,
            GhostPlaybackState state,
            RelativeSectionPlaybackTarget target,
            double targetUT,
            ref int playbackIdx,
            out InterpolationResult interpResult,
            bool suppressFallbackWarn = false,
            RelativeAnchorResolveFailure resolverFailure = default(RelativeAnchorResolveFailure))
        {
            interpResult = InterpolationResult.Zero;
            double firstBodyFixedUT;
            double lastBodyFixedUT;
            if (state?.ghost == null
                || !BodyFixedPrimaryCoversPlaybackUT(
                    target.Section,
                    targetUT,
                    out firstBodyFixedUT,
                    out lastBodyFixedUT))
            {
                return false;
            }

            int absolutePlaybackIdx = playbackIdx;
            bool ghostWasActive = state.ghost.activeSelf;
            InterpolateAndPosition(
                state.ghost,
                target.Section.bodyFixedFrames,
                null,
                ref absolutePlaybackIdx,
                targetUT,
                recordingIndex * 10000,
                out interpResult,
                allowActivation: ShouldAutoActivateGhost(state),
                skipOrbitSegments: true,
                recordingId: target.RecordingId,
                sectionIndex: target.SectionIndex);

            if (!GhostPlaybackEngine.IsInterpolationResultValid(interpResult))
            {
                if (state.ghost != null && state.ghost.activeSelf != ghostWasActive)
                    state.ghost.SetActive(ghostWasActive);
                return false;
            }

            playbackIdx = absolutePlaybackIdx;

            // PR 3c review follow-up: skip the [WARN] entirely (don't even
            // populate the dedupe set) when called by the legacy-debris gate —
            // the wrapper emits its own distinct verbose log so legacy-path
            // dispatches stay filterable. This keeps the post-failure-recovery
            // and intended-legacy-path log surfaces cleanly separate, and stops
            // the gate from inflating the WARN count the plan's success
            // criteria require to reach zero on a clean replay.
            if (!suppressFallbackWarn)
            {
                string key = string.Concat(
                    "recorded-relative-shadow-fallback|",
                    target.RecordingId ?? "(none)",
                    "|",
                    target.AnchorRecordingId ?? "(missing)",
                    "|",
                    target.SectionIndex.ToString(CultureInfo.InvariantCulture));
                if (loggedRelativeBodyFixedPrimaryStart.Add(key))
                {
                    ParsekLog.Warn("Playback",
                        $"RELATIVE recorded-anchor fallback to body-fixed primary: " +
                        $"recording #{recordingIndex} \"{trajectory?.VesselName}\" " +
                        $"recordingId={ShortRecordingId(target.RecordingId)} " +
                        $"anchorRec={target.AnchorRecordingId ?? "(missing)"} " +
                        $"frames={target.Section.bodyFixedFrames.Count} " +
                        $"sectionUT=[{target.Section.startUT:F1},{target.Section.endUT:F1}]");
                }
            }
            return true;
        }

        private void RetireUnresolvedRecordedRelative(
            GameObject ghost,
            int recordingIndex,
            string recordingVesselName,
            RelativeSectionPlaybackTarget target,
            GhostPlaybackState retireSignalState,
            string callsite,
            string resolverOutcome = null,
            string resolverReason = null)
        {
            if (!ReferenceEquals(ghost, null))
                GhostPlaybackLogic.HideGhostForRetire(ghost);
            if (retireSignalState != null)
                retireSignalState.anchorRetiredThisFrame = true;

            ParsekLog.WarnRateLimited(
                "Anchor",
                "recorded-relative-retired|" +
                recordingIndex.ToString(CultureInfo.InvariantCulture) + "|" +
                (target.AnchorRecordingId ?? "(missing)") + "|" +
                target.SectionIndex.ToString(CultureInfo.InvariantCulture),
                $"recorded-relative-retired: recording=#{recordingIndex} " +
                $"vessel=\"{recordingVesselName ?? "(unknown)"}\" " +
                $"anchorRec={target.AnchorRecordingId ?? "(missing)"} " +
                $"sectionIndex={target.SectionIndex} callsite={callsite}" +
                (!string.IsNullOrEmpty(resolverOutcome) ? $" outcome={resolverOutcome}" : string.Empty) +
                (!string.IsNullOrEmpty(resolverReason) ? $" reason={resolverReason}" : string.Empty),
                5.0);
        }

        void InterpolateAndPositionLoopRelative(
            GameObject ghost,
            List<TrajectoryPoint> frames,
            ref int cachedIndex,
            double targetUT,
            uint anchorVesselId,
            int recordingFormatVersion,
            int recordingIndex,
            string recordingId,
            string recordingVesselName,
            GhostPlaybackState retireSignalState,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            if (frames == null || frames.Count == 0)
            {
                interpResult = InterpolationResult.Zero;
                ghost.SetActive(false);
                return;
            }

            int indexBefore = TrajectoryMath.FindWaypointIndex(frames, ref cachedIndex, targetUT);

            if (indexBefore < 0)
            {
                // Before first frame — position at first frame's offset
                PositionLoopGhostRelativeAt(
                    ghost,
                    frames[0],
                    anchorVesselId,
                    recordingFormatVersion,
                    recordingIndex,
                    recordingId,
                    recordingVesselName,
                    retireSignalState,
                    allowActivation);
                interpResult = new InterpolationResult(frames[0].velocity, frames[0].bodyName, 0);
                return;
            }

            TrajectoryPoint before = frames[indexBefore];
            TrajectoryPoint after = frames[indexBefore + 1];

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                PositionLoopGhostRelativeAt(
                    ghost,
                    before,
                    anchorVesselId,
                    recordingFormatVersion,
                    recordingIndex,
                    recordingId,
                    recordingVesselName,
                    retireSignalState,
                    allowActivation);
                interpResult = new InterpolationResult(before.velocity, before.bodyName, 0);
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            // Interpolate offset (dx, dy, dz stored in lat/lon/alt)
            double dx = before.latitude + (after.latitude - before.latitude) * t;
            double dy = before.longitude + (after.longitude - before.longitude) * t;
            double dz = before.altitude + (after.altitude - before.altitude) * t;

            // Interpolate rotation in the recording's stored RELATIVE frame.
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);
            interpolatedRot = TrajectoryMath.SanitizeQuaternion(interpolatedRot);

            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            string bodyName = before.bodyName ?? "Kerbin";
            RelativeAnchorPose anchorPose;

            if (TryResolveLoopLiveAnchorPose(anchorVesselId, recordingId, targetUT, out anchorPose))
            {
                Vector3d ghostPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                    anchorPose.worldPos,
                    anchorPose.worldRotation,
                    dx,
                    dy,
                    dz);

                if (double.IsNaN(ghostPos.x) || double.IsNaN(ghostPos.y) || double.IsNaN(ghostPos.z))
                {
                    ParsekLog.Warn("Flight", "InterpolateAndPositionLoopRelative: NaN in ghost position, using anchor position");
                    ghostPos = anchorPose.worldPos;
                }

                ghost.transform.position = ghostPos;
                ghost.transform.rotation = TrajectoryMath.ResolveRelativePlaybackRotation(
                    anchorPose.worldRotation,
                    interpolatedRot);
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    recordingIndex,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-relative-position",
                    "resolved",
                    anchorVesselId,
                    null,
                    success: true,
                    fromRecordedTrajectory: anchorPose.fromRecordedTrajectory,
                    anchorPosition: anchorPose.worldPos,
                    anchorRotation: anchorPose.worldRotation,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: ghostPos);
                double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT;
                if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
                {
                    GhostRenderTrace.EmitPhase(
                        recordingId,
                        recordingIndex,
                        traceCurrentUT,
                        targetUT,
                        "UpdatePath",
                        "mode=LoopRelative anchorPid=" + anchorVesselId.ToString(CultureInfo.InvariantCulture)
                        + " beforeUT=" + before.ut.ToString("F3", CultureInfo.InvariantCulture)
                        + " afterUT=" + after.ut.ToString("F3", CultureInfo.InvariantCulture)
                        + " t=" + t.ToString("F4", CultureInfo.InvariantCulture)
                        + " anchorSource=" + (anchorPose.fromRecordedTrajectory ? "recorded" : "live")
                        + " localOffset=" + GhostRenderTrace.FormatVector3d(new Vector3d(dx, dy, dz))
                        + " anchorPos=" + GhostRenderTrace.FormatVector3d(anchorPose.worldPos)
                        + " final=" + GhostRenderTrace.FormatVector3d(ghostPos)
                        + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
                }

                ParsekLog.VerboseRateLimited("Flight", "relative-offset-applied",
                    BuildRelativeOffsetAppliedLog(
                        recordingFormatVersion,
                        dx,
                        dy,
                        dz,
                        anchorVesselId,
                        anchorPose.fromRecordedTrajectory),
                    2.0);

                // Compute altitude for InterpolationResult
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                double alt = body != null ? body.GetAltitude(ghostPos) : 0;

                AddOrReplaceGhostPosEntry(new GhostPosEntry
                {
                    ghost = ghost,
                    mode = GhostPosMode.Relative,
                    recordingId = recordingId,
                    recordingIndex = recordingIndex,
                    hasRecordingIndex = true,
                    anchorVesselId = anchorVesselId,
                    relDx = dx, relDy = dy, relDz = dz,
                    relativeRot = interpolatedRot,
                    relativeBodyName = bodyName,
                    bodyBefore = body, // for fallback in LateUpdate
                    latBefore = before.latitude, lonBefore = before.longitude, altBefore = before.altitude,
                    relativeRecordedAnchorPose = RelativeAnchorPoseSnapshot.FromRecordedPose(anchorPose),
                    relativeLoopLiveAnchor = true,
                });

                interpResult = new InterpolationResult(
                    Vector3.Lerp(before.velocity, after.velocity, t),
                    bodyName, alt);
            }
            else
            {
                // No live or recorded anchor pose is available. Retire for this
                // frame so v6 metre offsets are never interpreted as surface
                // lat/lon/alt or left frozen at a stale transform.
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    recordingIndex,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-relative-position",
                    "anchor-unresolved-retired",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: Vector3d.zero);
                if (ghost.activeSelf) ghost.SetActive(false);
                if (retireSignalState != null)
                    retireSignalState.anchorRetiredThisFrame = true;
                long key = RelativeAnchorResolution.DedupeKey(recordingIndex, anchorVesselId);
                if (loggedAnchorNotFound.Add(key))
                    ParsekLog.Warn("Anchor",
                        RelativeAnchorResolution.FormatRetiredMessage(
                            recordingIndex,
                            recordingVesselName,
                            anchorVesselId,
                            "InterpolateAndPositionLoopRelative"));

                // Return zero result -- ghost stays hidden until the next
                // absolute section or until the anchor resolves.
                interpResult = InterpolationResult.Zero;
            }
        }

        /// <summary>
        /// Positions a ghost at a single RELATIVE frame point (no interpolation).
        /// Used for edge cases (before first frame, zero-duration segments).
        /// </summary>
        void PositionLoopGhostRelativeAt(
            GameObject ghost,
            TrajectoryPoint point,
            uint anchorVesselId,
            int recordingFormatVersion,
            int recordingIndex,
            string recordingId,
            string recordingVesselName,
            GhostPlaybackState retireSignalState,
            bool allowActivation)
        {
            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            Quaternion sanitized = TrajectoryMath.SanitizeQuaternion(point.rotation);
            double dx = point.latitude;
            double dy = point.longitude;
            double dz = point.altitude;
            string bodyName = point.bodyName ?? "Kerbin";
            RelativeAnchorPose anchorPose;

            if (TryResolveLoopLiveAnchorPose(anchorVesselId, recordingId, point.ut, out anchorPose))
            {
                Vector3d ghostPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                    anchorPose.worldPos,
                    anchorPose.worldRotation,
                    dx,
                    dy,
                    dz);

                ghost.transform.position = ghostPos;
                ghost.transform.rotation = TrajectoryMath.ResolveRelativePlaybackRotation(
                    anchorPose.worldRotation,
                    sanitized);
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    recordingIndex,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut,
                    point.ut,
                    "loop-relative-single",
                    "resolved",
                    anchorVesselId,
                    null,
                    success: true,
                    fromRecordedTrajectory: anchorPose.fromRecordedTrajectory,
                    anchorPosition: anchorPose.worldPos,
                    anchorRotation: anchorPose.worldRotation,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: ghostPos);
                double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut;
                if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
                {
                    GhostRenderTrace.EmitPhase(
                        recordingId,
                        recordingIndex,
                        traceCurrentUT,
                        point.ut,
                        "UpdatePath",
                        "mode=LoopRelativeSingle anchorPid=" + anchorVesselId.ToString(CultureInfo.InvariantCulture)
                        + " anchorSource=" + (anchorPose.fromRecordedTrajectory ? "recorded" : "live")
                        + " localOffset=" + GhostRenderTrace.FormatVector3d(new Vector3d(dx, dy, dz))
                        + " anchorPos=" + GhostRenderTrace.FormatVector3d(anchorPose.worldPos)
                        + " final=" + GhostRenderTrace.FormatVector3d(ghostPos)
                        + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
                }

                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                AddOrReplaceGhostPosEntry(new GhostPosEntry
                {
                    ghost = ghost,
                    mode = GhostPosMode.Relative,
                    recordingId = recordingId,
                    recordingIndex = recordingIndex,
                    hasRecordingIndex = true,
                    anchorVesselId = anchorVesselId,
                    relDx = dx, relDy = dy, relDz = dz,
                    relativeRot = sanitized,
                    relativeBodyName = bodyName,
                    bodyBefore = body,
                    latBefore = dx, lonBefore = dy, altBefore = dz,
                    relativeRecordedAnchorPose = RelativeAnchorPoseSnapshot.FromRecordedPose(anchorPose),
                    relativeLoopLiveAnchor = true,
                });
            }
            else
            {
                // No live or recorded anchor pose is available. Retire for this
                // frame; see InterpolateAndPositionLoopRelative for the same gate.
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    recordingIndex,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut,
                    point.ut,
                    "loop-relative-single",
                    "anchor-unresolved-retired",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: Vector3d.zero);
                if (ghost.activeSelf) ghost.SetActive(false);
                if (retireSignalState != null)
                    retireSignalState.anchorRetiredThisFrame = true;
                long key = RelativeAnchorResolution.DedupeKey(recordingIndex, anchorVesselId);
                if (loggedAnchorNotFound.Add(key))
                    ParsekLog.Warn("Anchor",
                        RelativeAnchorResolution.FormatRetiredMessage(
                            recordingIndex,
                            recordingVesselName,
                            anchorVesselId,
                            "PositionGhostRelativeAt"));
            }
        }

        /// <summary>
        /// Flat-points dispatch for <see cref="FlatPointFrameDispatch"/>: substitutes the
        /// body-fixed shadow for a Relative-offset endpoint, or returns false when an
        /// endpoint has no body-fixed reading. Both outcomes are logged rate-limited.
        /// </summary>
        private static bool TryDispatchFlatPointEndpoints(
            List<TrackSection> sections,
            ref TrajectoryPoint before,
            ref TrajectoryPoint after,
            double targetUT,
            string recordingId,
            string pointFrameSource)
        {
            if (FlatPointFrameDispatch.TryDispatchEndpoints(
                    sections, ref before, ref after, out int substituted))
            {
                if (substituted > 0)
                {
                    ParsekLog.VerboseRateLimited("Playback",
                        "flat-point-relative-shadow|" + (recordingId ?? "(none)"),
                        $"Flat-point fallback read a Relative offset; used the body-fixed shadow: " +
                        $"rec={recordingId ?? "(none)"} source={pointFrameSource ?? "(none)"} " +
                        $"endpoints={substituted.ToString(CultureInfo.InvariantCulture)} " +
                        $"ut={targetUT.ToString("F2", CultureInfo.InvariantCulture)}",
                        5.0);
                }
                return true;
            }

            ParsekLog.InfoRateLimited("Playback",
                "flat-point-relative-refused|" + (recordingId ?? "(none)"),
                $"Flat-point fallback refused: a Relative-offset endpoint has no body-fixed shadow, ghost hidden " +
                $"rec={recordingId ?? "(none)"} source={pointFrameSource ?? "(none)"} " +
                $"ut={targetUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"beforeUT={before.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"afterUT={after.ut.ToString("F2", CultureInfo.InvariantCulture)}",
                10.0);
            return false;
        }
    }
}
