using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // The positioning statics that code outside ParsekFlight names (the playback engine, map
    // presence, xUnit). The instance positioning they serve is ParsekFlightGhostPositioner.
    public partial class ParsekFlight
    {
        /// <summary>
        /// Phase 1 spline-positioning gate (design doc §6.1, §17.3.1).
        /// Returns <c>true</c> with a populated <paramref name="spline"/>
        /// when (a) the caller supplied both a recording id and a non-negative
        /// section index and (b) <see cref="SectionAnnotationStore"/> holds a valid
        /// spline for the (recordingId, sectionIndex) pair. Any miss is a
        /// silent fall-through to legacy lerp positioning (HR-9 — "no spline
        /// yet" is a normal state, not a failure).
        /// </summary>
        internal static bool allowSplinePositioning(string recordingId, int sectionIndex,
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
        /// <see cref="ParsekFlightGhostPositioner.allowAnchorCorrectionInterval"/> (which returns the
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

            if (ParsekFlightGhostPositioner.allowAnchorCorrectionInterval(recordingId, sectionIndex, targetUT, out epsilon))
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

        /// <summary>
        /// Pure-static core of <see cref="ParsekFlightGhostPositioner.TryComputeLateUpdateSplineWorldPosition"/>.
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

        // Delegates to TrajectoryMath — kept for backward compatibility
        internal static OrbitSegment? FindOrbitSegment(List<OrbitSegment> segments, double ut)
        {
            return TrajectoryMath.FindOrbitSegment(segments, ut);
        }

        internal struct PredictedOrbitTailContinuityCacheEntry
        {
            public Vector3d rawOffset;
            public string anchorSource;
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

                Quaternion orbFrameAtStart = ParsekFlightGhostPositioner.SafeOrbitalLookRotation(
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

                Quaternion orbFrame = ParsekFlightGhostPositioner.SafeOrbitalLookRotation(
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

        internal static bool IsFiniteVector3d(Vector3d value)
        {
            return !double.IsNaN(value.x) && !double.IsInfinity(value.x)
                && !double.IsNaN(value.y) && !double.IsInfinity(value.y)
                && !double.IsNaN(value.z) && !double.IsInfinity(value.z);
        }

        internal static double? ResolveUnresolvedRelativeSectionDistanceFallback(
            IPlaybackTrajectory traj,
            double playbackUT)
        {
            return ParsekFlightGhostPositioner.IsRelativeSectionAtUT(traj, playbackUT)
                ? (double?)double.MaxValue
                : null;
        }

        internal static bool HasRenderableCheckpointTrackSection(TrackSection section)
        {
            if (section.referenceFrame != ReferenceFrame.OrbitalCheckpoint)
                return false;

            bool hasFrames = section.frames != null && section.frames.Count > 0;
            bool hasCheckpoints = section.checkpoints != null && section.checkpoints.Count > 0;
            return hasFrames || hasCheckpoints;
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
                && ParsekFlightGhostPositioner.TryFindAdjacentAbsolutePlaybackFrame(
                    trackSections,
                    sectionIdx - 1,
                    searchForward: false,
                    requireBeforeUT: first.ut,
                    requireAfterUT: double.NaN,
                    bodyName: first.bodyName,
                    out previousFrame);
            bool append = last.ut + epsilonSeconds < section.endUT
                && ParsekFlightGhostPositioner.TryFindAdjacentAbsolutePlaybackFrame(
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

            if (!ParsekFlightGhostPositioner.PointBelongsToSection(trackSections, sectionIdx, section, playbackUT))
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

            denseFrameCount = ParsekFlightGhostPositioner.CountDensebodyFixedFramesForSection(
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

            if (!ParsekFlightGhostPositioner.TryGetBracketWidthSeconds(
                    sectionFrames,
                    playbackUT,
                    out sectionBracketSeconds))
            {
                reason = "no-section-bracket";
                return false;
            }

            if (!ParsekFlightGhostPositioner.TryGetDenseSectionBracketWidthSeconds(
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
                    ParsekFlightGhostPositioner.ShortRecordingId(trajectory?.RecordingId),
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

            Vector3d previousOffset = ParsekFlightGhostPositioner.RecordedRelativeOffset(previous);
            Vector3d currentOffset = ParsekFlightGhostPositioner.RecordedRelativeOffset(current);
            Vector3d nextOffset = ParsekFlightGhostPositioner.RecordedRelativeOffset(next);
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
    }
}
