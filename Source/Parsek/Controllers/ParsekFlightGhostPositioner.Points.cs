using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Point interpolation core, the spline gates, and point and surface placement.
    internal sealed partial class ParsekFlightGhostPositioner
    {
        // CreateGhostSphere moved to GhostVisualBuilder.CreateGhostSphere (T25 Phase 4 prep)

        internal void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points, ref int cachedIndex,
            double targetUT, bool allowActivation, out InterpolationResult interpResult,
            string recordingId = null, int sectionIndex = -1,
            string pointFrameSource = "input",
            List<TrackSection> flatPointSections = null)
        {
            TrajectoryPoint before, after;
            float t;
            bool hasSegment = TrajectoryMath.InterpolatePoints(
                points, ref cachedIndex, targetUT, out before, out after, out t);

            // A flat Points read can land on a Relative section's anchor-local metre
            // offsets (a section gap, or a span no section covers). Those are not
            // lat/lon/alt: route them through the section's body-fixed shadow, or refuse.
            // Without a segment only `before` is meaningful (`after` is default).
            if (!hasSegment)
                after = before;
            if (flatPointSections != null
                && points != null
                && points.Count > 0
                && !TryDispatchFlatPointEndpoints(
                    flatPointSections, ref before, ref after, targetUT, recordingId, pointFrameSource))
            {
                interpResult = InterpolationResult.Zero;
                ghost.SetActive(false);
                return;
            }

            if (!hasSegment)
            {
                // Either empty list or before recording start
                if (points == null || points.Count == 0)
                {
                    interpResult = InterpolationResult.Zero;
                    ghost.SetActive(false);
                    return;
                }
                PositionGhostAt(ghost, before, recordingId);
                interpResult = new InterpolationResult(before.velocity, before.bodyName, before.altitude);
                return;
            }

            // Degenerate segment (t == 0 from zero-duration segment)
            if (t == 0f && before.ut == after.ut)
            {
                PositionGhostAt(ghost, before, recordingId);
                interpResult = new InterpolationResult(before.velocity, before.bodyName, before.altitude);
                return;
            }

            CelestialBody bodyBefore = FindBodyByName(FlightGlobals.Bodies, before.bodyName);
            CelestialBody bodyAfter = FindBodyByName(FlightGlobals.Bodies, after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
            {
                host.Log($"Could not find body: {(bodyBefore == null ? before.bodyName : after.bodyName)}");
                interpResult = InterpolationResult.Zero;
                ghost.SetActive(false);
                return;
            }
            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            // Phase 7 (design doc §13.1, §18 Phase 7): when both endpoints
            // carry a recorded ground clearance (surface section in a v9+
            // recording), substitute the interpolated altitude with
            // current_terrain + clearance so the rendered ghost stays at
            // constant clearance even when KSP regenerates terrain mesh
            // between sessions. NaN sentinel on either endpoint ⇒ silent
            // fall-through to the legacy altitude path (HR-9).
            //
            // referenceFrame=Absolute: this method's callers route RELATIVE
            // sections through IGhostPositioner.InterpolateAndPositionRelative,
            // and OrbitalCheckpoint sections through
            // TryInterpolateAndPositionCheckpointSection (orbit propagation),
            // so the points reaching here are always Absolute-frame body-
            // fixed lat/lon/alt. The explicit argument is the P2-1 safety
            // gate at the helper.
            double effectiveAltBefore = ParsekFlight.ResolveEffectiveAltitudeWithTailLift(
                bodyBefore, before.latitude, before.longitude, before.altitude,
                before.recordedGroundClearance, ReferenceFrame.Absolute,
                before.ut, recordingId);
            double effectiveAltAfter = ParsekFlight.ResolveEffectiveAltitudeWithTailLift(
                bodyAfter, after.latitude, after.longitude, after.altitude,
                after.recordedGroundClearance, ReferenceFrame.Absolute,
                after.ut, recordingId);

            // M4b body-fixed derotation (identity when activeBodyFixedShiftSeconds == 0, every
            // non-knob path): resolve at the shifted longitude so a vacuum point replayed off the
            // rotation-aligned grid lands at its recorded INERTIAL position instead of riding the
            // planet's extra rotation.
            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude,
                TrajectoryMath.FrameTransform.ShiftLongitudeDegrees(
                    before.longitude, activeBodyFixedShiftSeconds, bodyBefore),
                effectiveAltBefore);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude,
                TrajectoryMath.FrameTransform.ShiftLongitudeDegrees(
                    after.longitude, activeBodyFixedShiftSeconds, bodyAfter),
                effectiveAltAfter);

            Vector3d rawLerpPos = Vector3d.Lerp(posBefore, posAfter, t);
            Vector3d interpolatedPos = rawLerpPos;
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);

            interpolatedRot = TrajectoryMath.SanitizeQuaternion(interpolatedRot);

            if (double.IsNaN(interpolatedPos.x) || double.IsNaN(interpolatedPos.y) || double.IsNaN(interpolatedPos.z))
            {
                host.Log("Warning: NaN in interpolated position, using 'before' position");
                interpolatedPos = posBefore;
            }

            bool allowSplineForPlayback = allowSplinePositioningForPlayback(
                false,
                allowNormalPlaybackSplinePositioning: NormalPlaybackSplinePositioningEnabled,
                out string splineReason);

            // Phase 1 smoothing splines (design doc §6.1, §17.3.1, §18 Phase 1).
            // Replace the lerped lat/lon/alt-derived world position with a
            // Catmull-Rom spline evaluation only when the playback-mode gate
            // allows it, the section has a valid spline, and the rollout flag
            // is on. Normal Watch/loop playback stays on the historical
            // section-frame lerp unless a future product gate explicitly
            // enables spline-positioned playback. Rotation continues to come
            // from the existing slerp — Phase 1 is position-only. Any
            // precondition miss (no recordingId, no sectionIndex, flag off,
            // store miss, or !IsValid) silently falls through to the legacy
            // lerp result — that is acceptable per HR-9 because "no spline
            // yet" is a normal state, not a failure.
            bool splineApplied = false;
            if (allowSplineForPlayback
                && ParsekFlight.allowSplinePositioning(recordingId, sectionIndex, out Parsek.Rendering.SmoothingSpline spline))
            {
                ParsekFlight.RecordSplineEvalForLogging();
                Vector3d splineLatLonAlt = TrajectoryMath.CatmullRomFit.Evaluate(spline, targetUT);
                if (!double.IsNaN(splineLatLonAlt.x) && !double.IsNaN(splineLatLonAlt.y) && !double.IsNaN(splineLatLonAlt.z))
                {
                    // Phase 4 frame-aware dispatch (design doc §6.2 Stage 2,
                    // §18 Phase 4, §26.1 HR-9). FrameTag pins the coordinate
                    // contract of the spline's controls — body-fixed (0) hands
                    // straight to GetWorldSurfacePosition; inertial-longitude
                    // (1) re-lowers via FrameTransform.LowerFromInertialToWorld
                    // at the playback UT so the body's rotation between
                    // recording and playback is correctly cancelled out. An
                    // unrecognised tag emits Warn (per HR-9) gated by
                    // unknownFrameTagWarned so a degenerate recording can't
                    // flood the log — see DispatchSplineWorldByFrameTag.
                    Vector3d splineWorld = TrajectoryMath.FrameTransform.DispatchSplineWorldByFrameTag(
                        spline.FrameTag,
                        splineLatLonAlt.x, splineLatLonAlt.y, splineLatLonAlt.z,
                        bodyBefore, targetUT,
                        recordingId, sectionIndex,
                        host.unknownFrameTagWarned);
                    if (!double.IsNaN(splineWorld.x) && !double.IsNaN(splineWorld.y) && !double.IsNaN(splineWorld.z))
                    {
                        interpolatedPos = splineWorld;
                        splineApplied = true;
                        splineReason = "applied";
                    }
                }
            }

            // Velocity-aware PointInterp was introduced during Re-Fly work, but it
            // changes the visible path between recorded samples. Keep normal Watch
            // and loop playback on the historical section-frame lerp unless a
            // future product gate explicitly enables velocity-curved playback.
            // Active Re-Fly recorded-coordinate playback also stays linear.
            bool hermiteApplied = false;
            Vector3d rawHermitePos = Vector3d.zero;
            double hermiteDeviationMeters = double.NaN;
            double hermiteMaxDeviationMeters = ComputePointHermiteMaxDeviationMeters(posBefore, posAfter);
            bool allowHermite = ParsekFlight.allowPointHermiteInterpolation(
                false,
                splineApplied,
                allowNormalPlaybackHermite: ParsekFlight.NormalPlaybackPointHermiteEnabled,
                out string hermiteReason);
            if (allowHermite)
            {
                if (TrajectoryMath.TryInterpolateWorldHermite(
                        posBefore,
                        before.velocity,
                        posAfter,
                        after.velocity,
                        after.ut - before.ut,
                        t,
                        hermiteMaxDeviationMeters,
                        out rawHermitePos,
                        out hermiteDeviationMeters,
                        out hermiteReason))
                {
                    interpolatedPos = rawHermitePos;
                    hermiteApplied = true;
                }
            }

            // Phase 2 + Phase 3 anchor correction (design doc §6.3 Stage 3,
            // §6.4 multi-anchor lerp, §7.1, §8, §18 Phase 2-3 / HR-15).
            // Additive world-space ε translation pinned once at session entry;
            // the lookup reads the cached AnchorCorrection.Epsilon values and
            // never touches live vessel state. Phase 3 evaluates ε at the
            // current playback UT — a both-end interval lerps linearly per
            // §6.4, single-side intervals return constant ε. Any miss (no
            // recordingId, no section, flag off, no anchor in the store) is
            // a silent fall-through — re-fly sessions with no marker yet are
            // normal state, not failure.
            bool hasAnchorEps = ParsekFlight.allowRenderAnchorCorrectionInterval(
                recordingId, sectionIndex, targetUT, false,
                out Vector3d anchorEps, out string anchorCorrectionReason);
            if (hasAnchorEps)
                interpolatedPos += anchorEps;

            ghost.transform.position = interpolatedPos;
            ghost.transform.rotation = bodyBefore.bodyTransform.rotation
                * TrajectoryMath.FrameTransform.ShiftSurfaceRelativeRotation(
                    interpolatedRot, activeBodyFixedShiftSeconds, bodyBefore);

            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT;
            if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    recordingId,
                    -1,
                    traceCurrentUT,
                    targetUT,
                    "UpdatePath",
                    "mode=PointInterp"
                    + " pointFrameSource=" + (pointFrameSource ?? "input")
                    + " sectionIndex=" + sectionIndex.ToString(CultureInfo.InvariantCulture)
                    + " beforeUT=" + before.ut.ToString("F3", CultureInfo.InvariantCulture)
                    + " afterUT=" + after.ut.ToString("F3", CultureInfo.InvariantCulture)
                    + " t=" + t.ToString("F4", CultureInfo.InvariantCulture)
                    + " rawBefore=" + GhostRenderTrace.FormatVector3d(posBefore)
                    + " rawAfter=" + GhostRenderTrace.FormatVector3d(posAfter)
                    + " rawLerp=" + GhostRenderTrace.FormatVector3d(rawLerpPos)
                    + " rawHermite=" + GhostRenderTrace.FormatVector3d(hermiteApplied ? rawHermitePos : Vector3d.zero)
                    + " hermiteHit=" + (hermiteApplied ? "true" : "false")
                    + " hermiteReason=" + (hermiteReason ?? "<none>")
                    + " hermiteDeviationMeters=" + hermiteDeviationMeters.ToString("F2", CultureInfo.InvariantCulture)
                    + " hermiteMaxDeviationMeters=" + hermiteMaxDeviationMeters.ToString("F2", CultureInfo.InvariantCulture)
                    + " interpMode=" + (splineApplied ? "Spline" : hermiteApplied ? "Hermite" : "Lerp")
                    + " splineHit=" + (splineApplied ? "true" : "false")
                    + " splineReason=" + (splineReason ?? "<none>")
                    + " anchorCorrectionHit=" + (hasAnchorEps ? "true" : "false")
                    + " anchorCorrectionReason=" + anchorCorrectionReason
                    + " anchorEps=" + GhostRenderTrace.FormatVector3d(hasAnchorEps ? anchorEps : Vector3d.zero)
                    + " final=" + GhostRenderTrace.FormatVector3d(interpolatedPos)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
            }

            // Register for LateUpdate re-positioning after FloatingOrigin shift.
            // Phase 3 / Phase 4: store the lookup key (recordingId + sectionIndex)
            // so LateUpdate can re-evaluate BOTH the spline (Phase 1+4) and the
            // anchor interval (Phase 2+3) at the same playback UT. Storing the
            // key only when an anchor exists (the prior contract) discarded the
            // spline result on every LateUpdate — the raw lerp on lines below
            // would overwrite the spline-positioned transform. The key is
            // always-stored when (recordingId, sectionIndex) is valid; the gates
            // (settings flag, store presence) are re-checked at LateUpdate.
            bool hasLookupKey = !string.IsNullOrEmpty(recordingId) && sectionIndex >= 0;
            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = ghost,
                mode = ParsekFlight.GhostPosMode.PointInterp,
                recordingId = recordingId,
                bodyBefore = bodyBefore,
                bodyAfter = bodyAfter,
                latBefore = before.latitude, lonBefore = before.longitude, altBefore = effectiveAltBefore,
                latAfter = after.latitude, lonAfter = after.longitude, altAfter = effectiveAltAfter,
                t = t,
                pointUT = targetUT,
                pointDeltaTimeSeconds = after.ut - before.ut,
                interpolatedRot = interpolatedRot,
                velocityBefore = before.velocity,
                velocityAfter = after.velocity,
                anchorRecordingId = hasLookupKey ? recordingId : null,
                anchorSectionIndex = hasLookupKey ? sectionIndex : -1,
            });

            interpResult = new InterpolationResult(
                Vector3.Lerp(before.velocity, after.velocity, t),
                after.bodyName,
                TrajectoryMath.InterpolateAltitude(before.altitude, after.altitude, t));
            // Surface velocity from the same body-fixed bracket the position used (both
            // endpoints resolved now, so the delta is motion over the surface in today's
            // axes). A body change inside the bracket has no single surface to measure on.
            if (ReferenceEquals(bodyBefore, bodyAfter)
                && GhostSurfaceVelocity.TryFromBracketPositions(
                    posBefore, posAfter, after.ut - before.ut, out Vector3 surfaceVelocity))
            {
                interpResult = interpResult.WithSurfaceVelocity(
                    surfaceVelocity, GhostSurfaceVelocitySource.Positions);
                // The speed stock's vessel.speed would read here (the reentry scalar reads it), not
                // the bracket's mean.
                double stockFraction = GhostSurfaceVelocity.StockSpeedFraction(
                    t, after.ut - before.ut, TimeWarp.fixedDeltaTime);
                if (GhostSurfaceVelocity.TryRecordedSurfaceSpeed(surfaceVelocity.magnitude,
                        before.velocity.magnitude, after.velocity.magnitude, stockFraction, out float stockSpeed))
                    interpResult = interpResult.WithStockSurfaceSpeed(stockSpeed);
            }
        }

        internal static double ComputePointHermiteMaxDeviationMeters(Vector3d posBefore, Vector3d posAfter)
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
        /// Phase 1 + Phase 4 LateUpdate spline re-evaluation helper (design
        /// doc §6.1 / §6.2 / §18 Phase 1+4 / HR-9). Instance shim that
        /// supplies the per-session <see cref="ParsekFlight.unknownFrameTagWarned"/> dedup
        /// set; the gate-then-dispatch logic itself lives in
        /// <see cref="ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure"/> so xUnit
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
        internal bool TryComputeLateUpdateSplineWorldPosition(in ParsekFlight.GhostPosEntry e, out Vector3d worldPos)
        {
            if (!allowSplinePositioningForPlayback(
                    false,
                    allowNormalPlaybackSplinePositioning: NormalPlaybackSplinePositioningEnabled,
                    out _))
            {
                worldPos = default(Vector3d);
                return false;
            }

            return ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure(
                e.anchorRecordingId, e.anchorSectionIndex, e.pointUT, e.bodyBefore,
                host.unknownFrameTagWarned, out worldPos);
        }

        internal void PositionGhostAt(GameObject ghost, TrajectoryPoint point, string recordingId = null,
            bool altitudeAuthoritative = false)
        {
            CelestialBody body = FindBodyByName(FlightGlobals.Bodies, point.bodyName);
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
                : ParsekFlight.ResolveEffectiveAltitudeWithTailLift(
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
            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = ghost,
                mode = ParsekFlight.GhostPosMode.SinglePoint,
                recordingId = recordingId,
                bodyBefore = body,
                latBefore = point.latitude, lonBefore = point.longitude, altBefore = effectiveAltitude,
                pointUT = point.ut,
                interpolatedRot = sanitized,
            });
        }

        /// <summary>
        /// Positions a ghost at a fixed surface position (body, lat, lon, alt, rotation).
        /// Used for background vessels that are landed/splashed.
        /// </summary>
        internal void PositionGhostAtSurface(GameObject ghost, SurfacePosition surfPos, bool allowActivation,
            string recordingId = null, double pointUT = double.NaN)
        {
            CelestialBody body = FindBodyByName(FlightGlobals.Bodies, surfPos.body);
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
            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = ghost,
                mode = ParsekFlight.GhostPosMode.Surface,
                recordingId = recordingId,
                bodyBefore = body,
                latBefore = surfPos.latitude, lonBefore = surfPos.longitude, altBefore = surfPos.altitude,
                surfaceRot = surfPos.rotation,
            });

            ParsekLog.VerboseRateLimited("Flight", "surface-ghost-positioned",
                $"PositionGhostAtSurface: body={surfPos.body} lat={surfPos.latitude:F4} lon={surfPos.longitude:F4} alt={surfPos.altitude:F1}");
        }

        internal static bool IsRelativeSectionAtUT(IPlaybackTrajectory traj, double playbackUT)
        {
            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
                return false;
            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
            return sectionIdx >= 0
                && traj.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative;
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
