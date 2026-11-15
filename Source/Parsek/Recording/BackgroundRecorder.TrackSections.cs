using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Environment classification and TrackSection management: environment transitions, pending initial overrides, relative-frame contract, section open / append / close and flush.
    internal partial class BackgroundRecorder
    {
        /// <summary>
        /// Handles a background vessel's environment transition: classifies the raw environment,
        /// feeds the hysteresis, and on a confirmed transition activates high-fidelity sampling,
        /// captures the boundary point, closes the current BG track section, opens a new Absolute
        /// section, and seeds the boundary point. Gated on a non-null hysteresis. Runs strictly
        /// below the packed/on-rails early-return in <see cref="OnBackgroundPhysicsFrame"/> (the
        /// eccentric-orbit invariant, §S16).
        /// </summary>
        private void HandleBackgroundEnvironmentTransition(
            BackgroundVesselState state, Vessel bgVessel, uint pid, double ut)
        {
            if (state.environmentHysteresis != null)
            {
                bool hasAtmo = bgVessel.mainBody != null && bgVessel.mainBody.atmosphere;
                double atmoDepth = hasAtmo ? bgVessel.mainBody.atmosphereDepth : 0;
                double approachAlt = (!hasAtmo && bgVessel.mainBody != null)
                    ? FlightRecorder.ComputeApproachAltitude(bgVessel.mainBody) : 0;
                var rawEnv = ClassifyBackgroundEnvironment(
                    hasAtmo, bgVessel.altitude, atmoDepth,
                    (int)bgVessel.situation, bgVessel.srfSpeed, state.cachedEngines, approachAlt,
                    bgVessel.isEVA, bgVessel.heightFromTerrain,
                    EnvironmentDetector.IsHeightFromTerrainValid(bgVessel.heightFromTerrain),
                    bgVessel.mainBody != null && bgVessel.mainBody.ocean);
                if (state.environmentHysteresis.Update(rawEnv, ut))
                {
                    var newEnv = state.environmentHysteresis.CurrentEnvironment;
                    ActivateBackgroundHighFidelitySampling(state, ut, "environment-transition");
                    ParsekLog.Info("BgRecorder",
                        $"Environment transition: pid={pid} -> {newEnv} " +
                        $"at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");

                    // Capture boundary point before closing (#283)
                    TrajectoryPoint? boundaryPoint = GetLastBackgroundFrame(state);
                    CloseBackgroundTrackSection(state, ut);
                    StartBackgroundTrackSection(state, newEnv, ReferenceFrame.Absolute, ut);
                    SeedBackgroundBoundaryPoint(state, boundaryPoint);
                }
            }
        }

        #region Background Environment Classification & TrackSection Management

        /// <summary>
        /// Classifies the environment for a background vessel using its cached engine list.
        /// Similar to FlightRecorder.ClassifyCurrentEnvironment but works with
        /// BackgroundVesselState's cached engines instead of FlightRecorder instance fields.
        /// Pure enough to be internal static for testability.
        /// </summary>
        internal static SegmentEnvironment ClassifyBackgroundEnvironment(
            bool hasAtmosphere, double altitude, double atmosphereDepth,
            int situation, double srfSpeed,
            List<(Part part, ModuleEngines engine, int moduleIndex)> cachedEngines,
            double approachAltitude = 0,
            bool isEva = false,
            double heightFromTerrain = -1,
            bool heightFromTerrainValid = false,
            bool hasOcean = false)
        {
            bool hasActiveThrust = false;
            if (cachedEngines != null)
            {
                for (int i = 0; i < cachedEngines.Count; i++)
                {
                    if (cachedEngines[i].engine != null &&
                        cachedEngines[i].engine.EngineIgnited &&
                        cachedEngines[i].engine.finalThrust > 0)
                    {
                        hasActiveThrust = true;
                        break;
                    }
                }
            }

            return EnvironmentDetector.Classify(
                hasAtmosphere, altitude, atmosphereDepth,
                situation, srfSpeed, hasActiveThrust, approachAltitude,
                isEva, heightFromTerrain, heightFromTerrainValid, hasOcean);
        }

        private bool TryConsumePendingInitialEnvironmentOverride(uint vesselPid, out SegmentEnvironment environment)
        {
            if (pendingInitialEnvironmentOverrides.TryGetValue(vesselPid, out environment))
            {
                pendingInitialEnvironmentOverrides.Remove(vesselPid);
                return true;
            }

            environment = SegmentEnvironment.Atmospheric;
            return false;
        }

        private bool TryConsumePendingInitialTrajectoryPoint(uint vesselPid, out TrajectoryPoint point)
        {
            if (pendingInitialTrajectoryPoints.TryGetValue(vesselPid, out point))
            {
                pendingInitialTrajectoryPoints.Remove(vesselPid);
                return true;
            }

            point = default(TrajectoryPoint);
            return false;
        }

        internal static bool IsPendingDebrisSeedParentAnchorUsable(
            string queuedParentRecordingId,
            string expectedParentRecordingId,
            double queuedUT,
            double seedUT,
            out string reason,
            double toleranceSeconds = 1e-6)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(queuedParentRecordingId))
            {
                reason = "queued-parent-recording-id-missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(expectedParentRecordingId))
            {
                reason = "expected-parent-recording-id-missing";
                return false;
            }

            if (!string.Equals(queuedParentRecordingId, expectedParentRecordingId, StringComparison.Ordinal))
            {
                reason = "queued-parent-recording-id-mismatch";
                return false;
            }

            if (double.IsNaN(queuedUT)
                || double.IsInfinity(queuedUT)
                || double.IsNaN(seedUT)
                || double.IsInfinity(seedUT))
            {
                reason = "queued-parent-anchor-ut-invalid";
                return false;
            }

            if (double.IsNaN(toleranceSeconds)
                || double.IsInfinity(toleranceSeconds)
                || toleranceSeconds < 0.0)
            {
                reason = "queued-parent-anchor-tolerance-invalid";
                return false;
            }

            if (Math.Abs(queuedUT - seedUT) > toleranceSeconds)
            {
                reason = "queued-parent-anchor-ut-mismatch";
                return false;
            }

            return true;
        }

        private bool TryConsumePendingDebrisSeedParentAnchorPose(
            uint vesselPid,
            string expectedParentRecordingId,
            double seedUT,
            out AnchorPose pose,
            out string reason)
        {
            pose = default;
            reason = null;
            if (!pendingDebrisSeedParentAnchorPoints.TryGetValue(
                    vesselPid,
                    out (string parentRecordingId, TrajectoryPoint point) pending))
            {
                reason = "queued-parent-anchor-missing";
                return false;
            }

            pendingDebrisSeedParentAnchorPoints.Remove(vesselPid);
            if (!IsPendingDebrisSeedParentAnchorUsable(
                    pending.parentRecordingId,
                    expectedParentRecordingId,
                    pending.point.ut,
                    seedUT,
                    out reason))
            {
                return false;
            }

            if (!TryCreateAnchorPoseFromAbsolutePoint(
                    pending.point,
                    expectedParentRecordingId,
                    out pose,
                    out reason))
            {
                return false;
            }

            return true;
        }

        private static bool TryCreateAnchorPoseFromAbsolutePoint(
            TrajectoryPoint point,
            string recordingId,
            out AnchorPose pose,
            out string reason)
        {
            pose = default;
            reason = null;
            CelestialBody body = ParsekFlight.FindBodyForAnchorResolver(point.bodyName);
            if (body == null || body.bodyTransform == null)
            {
                reason = "queued-parent-anchor-body-unresolved";
                return false;
            }

            Vector3d worldPos;
            try
            {
                worldPos = body.GetWorldSurfacePosition(point.latitude, point.longitude, point.altitude);
            }
            catch (Exception)
            {
                reason = "queued-parent-anchor-world-position-unresolved";
                return false;
            }

            Quaternion worldRotation = TrajectoryMath.PureMultiply(
                body.bodyTransform.rotation,
                TrajectoryMath.SanitizeQuaternion(point.rotation));
            pose = new AnchorPose(worldPos, worldRotation, -1, recordingId);
            if (!IsFiniteAnchorPose(pose))
            {
                reason = "queued-parent-anchor-pose-nonfinite";
                pose = default;
                return false;
            }

            return true;
        }

        internal static string ClassifyBackgroundRelativeFrameContract(
            string sourceLabel,
            AnchorCandidateSource source,
            bool hasCurrentAnchorCandidate,
            bool activeReFlyParentMatch,
            bool hasPreReFlyAnchorSnapshot)
        {
            string normalized = sourceLabel ?? string.Empty;
            if (string.Equals(normalized, "queued-parent-seed", StringComparison.Ordinal))
                return "recorded";

            if (string.Equals(normalized, "live", StringComparison.Ordinal)
                || string.Equals(normalized, "live-warn-fallback", StringComparison.Ordinal))
            {
                return "live";
            }

            if (string.Equals(normalized, "recorded", StringComparison.Ordinal)
                || !hasCurrentAnchorCandidate)
            {
                return activeReFlyParentMatch && hasPreReFlyAnchorSnapshot
                    ? "frozen-refly"
                    : "recorded";
            }

            return source == AnchorCandidateSource.Live ? "live" : "recorded";
        }

        internal static bool IsActiveReFlyParentMatch(
            string activeReFlyRecordingId,
            string parentRecordingId)
        {
            return !string.IsNullOrEmpty(activeReFlyRecordingId)
                && !string.IsNullOrEmpty(parentRecordingId)
                && string.Equals(activeReFlyRecordingId, parentRecordingId, StringComparison.Ordinal);
        }

        private string ClassifyBackgroundRelativeFrameContract(
            string anchorRecordingId,
            string sourceLabel,
            AnchorCandidateSource source,
            bool hasCurrentAnchorCandidate,
            bool hasPreReFlyAnchorSnapshot)
        {
            var marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            return ClassifyBackgroundRelativeFrameContract(
                sourceLabel,
                source,
                hasCurrentAnchorCandidate,
                IsActiveReFlyParentMatch(marker, anchorRecordingId),
                hasPreReFlyAnchorSnapshot);
        }

        private bool HasActiveReFlyPreReFlyAnchorSnapshot(string anchorRecordingId)
        {
            var marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            if (!IsActiveReFlyParentMatch(marker, anchorRecordingId)
                || tree?.Recordings == null
                || !tree.Recordings.TryGetValue(anchorRecordingId, out Recording anchorRecording)
                || anchorRecording == null)
            {
                return false;
            }

            return anchorRecording.HasPreReFlyAnchorTrajectory(marker.SessionId);
        }

        private static bool IsActiveReFlyParentMatch(
            ReFlySessionMarker marker,
            string parentRecordingId)
        {
            return marker != null
                && IsActiveReFlyParentMatch(
                    marker.ActiveReFlyRecordingId,
                    parentRecordingId);
        }

        private string FormatActiveReFlyParentDriftFromRecorded(
            string parentRecordingId,
            uint diagnosticPid,
            double ut)
        {
            var marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            bool activeReFlyParentMatch = IsActiveReFlyParentMatch(marker, parentRecordingId);
            if (!activeReFlyParentMatch)
                return null;

            bool liveResolved = TryResolveLiveParentWorldPosition(
                parentRecordingId,
                diagnosticPid,
                out Vector3d liveWorldPos);
            bool recordedResolved = TryResolveBackgroundRecordedAnchorPose(
                parentRecordingId,
                ut,
                out AnchorPose recordedPose,
                out _);

            return FormatActiveReFlyParentDriftFromRecorded(
                activeReFlyParentMatch,
                liveResolved,
                liveWorldPos,
                recordedResolved,
                recordedPose.WorldPos);
        }

        internal static string FormatActiveReFlyParentDriftFromRecorded(
            bool activeReFlyParentMatch,
            bool liveResolved,
            Vector3d liveWorldPos,
            bool recordedResolved,
            Vector3d recordedWorldPos)
        {
            if (!activeReFlyParentMatch)
                return null;

            if (!liveResolved
                || !recordedResolved
                || !IsFinite(liveWorldPos)
                || !IsFinite(recordedWorldPos))
            {
                return "(unresolved)";
            }

            double drift = (liveWorldPos - recordedWorldPos).magnitude;
            return IsFinite(drift)
                ? drift.ToString("F3", CultureInfo.InvariantCulture)
                : "(unresolved)";
        }

        internal static string FormatParentDriftLogField(string parentDriftFromRecorded)
        {
            return parentDriftFromRecorded != null
                ? $"parentDriftFromRecorded={parentDriftFromRecorded} "
                : string.Empty;
        }

        private bool TryResolveLiveParentWorldPosition(
            string parentRecordingId,
            uint diagnosticPid,
            out Vector3d liveWorldPos)
        {
            liveWorldPos = Vector3d.zero;

            Vessel liveParent = diagnosticPid != 0u
                ? FlightRecorder.FindVesselByPid(diagnosticPid)
                : null;
            if ((liveParent == null || !liveParent.loaded)
                && tree?.Recordings != null
                && !string.IsNullOrEmpty(parentRecordingId)
                && tree.Recordings.TryGetValue(parentRecordingId, out Recording parentRecording)
                && parentRecording != null
                && parentRecording.VesselPersistentId != 0u)
            {
                liveParent = FlightRecorder.FindLaunchMatchedVesselForRecording(parentRecording);
            }

            if (liveParent != null && liveParent.loaded && liveParent.transform != null)
            {
                liveWorldPos = (Vector3d)liveParent.transform.position;
                return IsFinite(liveWorldPos);
            }

            return false;
        }

        private static bool IsFiniteAnchorPose(AnchorPose pose)
        {
            return IsFinite(pose.WorldPos)
                && IsFinite(pose.WorldRotation);
        }

        private static bool IsFinite(Vector3d value)
        {
            return IsFinite(value.x)
                && IsFinite(value.y)
                && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x)
                && IsFinite(value.y)
                && IsFinite(value.z)
                && IsFinite(value.w);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// Opens a new TrackSection for a background vessel.
        /// Sets source = Background.
        /// </summary>
        private void StartBackgroundTrackSection(
            BackgroundVesselState state, SegmentEnvironment env, ReferenceFrame refFrame, double ut)
        {
            state.currentTrackSection = new TrackSection
            {
                environment = env,
                referenceFrame = refFrame,
                startUT = ut,
                source = TrackSectionSource.Background,
                anchorRecordingId = refFrame == ReferenceFrame.Relative
                    ? state.currentAnchorRecordingId
                    : null,
                anchorVesselId = 0u,
                frames = new List<TrajectoryPoint>(),
                bodyFixedFrames = refFrame == ReferenceFrame.Relative
                    ? new List<TrajectoryPoint>()
                    : null,
                checkpoints = new List<OrbitSegment>(),
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            };
            state.trackSectionActive = true;
            // Phase 7: reset per-section clearance accumulators (mirror
            // FlightRecorder.StartNewTrackSection). Even when the new section
            // is not a surface-clearance environment, resetting is cheap and
            // keeps state crisp.
            state.surfaceMobileSamplesThisSection = 0;
            state.surfaceMobileMinClearanceThisSection = double.NaN;
            state.surfaceMobileMaxClearanceThisSection = double.NaN;
            state.surfaceMobileClearanceSumThisSection = 0.0;
            state.sectionFrameWarpFlags.Clear();
            state.sectionProximitySilences.Clear();
            ParsekLog.Info("BgRecorder",
                $"TrackSection started: env={env} ref={refFrame} source=Background " +
                $"pid={state.vesselPid} at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Appends one entry to <paramref name="state"/>'s per-section rails-flag
        /// list in lockstep with a <c>currentTrackSection.frames.Add</c>. Records
        /// whether the sample was taken under rails warp so the section-close
        /// sparse-sampling check can classify each gap; physics warp reads false
        /// because its frames are sampled like 1x frames. On-rails BG samples never
        /// reach these append paths (the per-frame tick early-returns on
        /// <c>bgVessel.packed</c>).
        /// </summary>
        private static void AppendSectionFrameWarpFlag(BackgroundVesselState state)
        {
            state?.sectionFrameWarpFlags.Add(FlightRecorder.IsRailsWarpActiveForDiagnostics());
            CloseProximitySilenceAtCommittedFrame(state);
        }

        /// <summary>
        /// An event-driven frame (a structural snapshot at a joint break, a boundary point)
        /// can be committed while the vessel is still out of range, and the section can close
        /// in the same event chain (debris that hits the ground and is destroyed) before any
        /// in-range tick ends the silence. A window still open at a commit means every tick
        /// since it opened was skipped for range, so it ends at that frame; a seed carrying a
        /// UT not after the silence start leaves it open.
        /// </summary>
        private static void CloseProximitySilenceAtCommittedFrame(BackgroundVesselState state)
        {
            if (state == null || double.IsNaN(state.proximitySilenceStartUT))
                return;
            List<TrajectoryPoint> frames = state.currentTrackSection.frames;
            if (frames == null || frames.Count == 0)
                return;
            double frameUT = frames[frames.Count - 1].ut;
            if (frameUT > state.proximitySilenceStartUT)
                CloseProximitySilence(state, frameUT);
        }

        /// <summary>
        /// Opens a sampling silence at the first tick the vessel is skipped for being out of
        /// proximity range (later skipped ticks extend the same silence).
        /// </summary>
        private static void NoteProximitySilenceTick(BackgroundVesselState state, double ut)
        {
            if (state == null || !double.IsNaN(state.proximitySilenceStartUT))
                return;
            state.proximitySilenceStartUT = ut;
            ParsekLog.Verbose("BgRecorder",
                $"Proximity sampling silence started: pid={state.vesselPid} " +
                $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Closes an open proximity silence at the first tick the vessel is no longer
        /// skipped for range (or at a frame committed while still out of range, see
        /// <see cref="CloseProximitySilenceAtCommittedFrame"/>), so the section-close gap check
        /// excuses exactly the out-of-range time: an in-range stall after the return stays
        /// outside the window and still WARNs.
        /// </summary>
        private static void CloseProximitySilence(BackgroundVesselState state, double endUT)
        {
            if (state == null || double.IsNaN(state.proximitySilenceStartUT))
                return;
            double startUT = state.proximitySilenceStartUT;
            state.proximitySilenceStartUT = double.NaN;
            if (endUT > startUT)
                state.sectionProximitySilences.Add(
                    new FlightRecorder.SamplingSilenceWindow(startUT, endUT));
            ParsekLog.Verbose("BgRecorder",
                $"Proximity sampling silence ended: pid={state.vesselPid} " +
                $"startUT={startUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"endUT={endUT.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        private static void AppendFrameToCurrentTrackSection(
            BackgroundVesselState state,
            TrajectoryPoint point,
            TrajectoryPoint? bodyFixedPrimaryPoint = null)
        {
            if (state == null || !state.trackSectionActive || state.currentTrackSection.frames == null)
                return;

            state.currentTrackSection.frames.Add(point);
            AppendSectionFrameWarpFlag(state);
            if (bodyFixedPrimaryPoint.HasValue && state.currentTrackSection.bodyFixedFrames != null)
                state.currentTrackSection.bodyFixedFrames.Add(bodyFixedPrimaryPoint.Value);
            if (float.IsNaN(state.currentTrackSection.minAltitude)
                || point.altitude < state.currentTrackSection.minAltitude)
            {
                state.currentTrackSection.minAltitude = (float)point.altitude;
            }
            if (float.IsNaN(state.currentTrackSection.maxAltitude)
                || point.altitude > state.currentTrackSection.maxAltitude)
            {
                state.currentTrackSection.maxAltitude = (float)point.altitude;
            }
        }

        private static void ApplyInitialTrajectoryPoint(
            BackgroundVesselState state,
            Recording treeRec,
            TrajectoryPoint point,
            TrajectoryPoint? bodyFixedPrimaryPoint = null)
        {
            // Bug #419: ApplyTrajectoryPointToRecording rejects non-monotonic appends. If
            // the flat-points append was rejected, the seed must NOT enter the track
            // section either — otherwise the same point re-materializes in
            // treeRec.Points on the next FlushTrackSectionsToRecording call via
            // AppendPointsFromTrackSections. Similarly, do not advance lastRecordedUT /
            // lastRecordedVelocity from a rejected seed, or the next frame-delta check
            // would be keyed on a phantom time.
            bool accepted = treeRec == null || ApplyTrajectoryPointToRecording(treeRec, point);
            if (!accepted)
            {
                ParsekLog.Warn("BgRecorder",
                    $"Initial trajectory point rejected (non-monotonic): pid={state.vesselPid} " +
                    $"ut={point.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"recId={treeRec.RecordingId} — dropped from track section and state (#419)");
                return;
            }

            AppendFrameToCurrentTrackSection(state, point, bodyFixedPrimaryPoint);
            state.lastRecordedUT = point.ut;
            state.lastRecordedVelocity = point.velocity;

            ParsekLog.Info("BgRecorder", string.Format(CultureInfo.InvariantCulture,
                "Initial trajectory point seeded: pid={0} ut={1:F2} " +
                "lat={2:F4} lon={3:F4} alt={4:F0} vel=({5:F2},{6:F2},{7:F2}) |vel|={8:F2}",
                state.vesselPid,
                point.ut,
                point.latitude,
                point.longitude,
                point.altitude,
                point.velocity.x,
                point.velocity.y,
                point.velocity.z,
                point.velocity.magnitude));
        }

        internal static bool ApplyTrajectoryPointToRecording(Recording treeRec, TrajectoryPoint point)
        {
            if (treeRec == null)
                return false;

            // Bug #419: enforce monotonicity at the choke point. Every sampler path that
            // appends to treeRec.Points — breakup initial seeds, on-rails seeds,
            // background fallback seeds — funnels through here. If a caller tries to
            // append a point whose UT is strictly less than the current last point's UT
            // (e.g., a belated breakup-initial seed consumed after physics-frame samples
            // already advanced, or a duplicate suffix re-append from a flush path),
            // reject the point with a warn log so binary-search and interpolation
            // consumers keep their monotonicity invariant. Equal UTs are tolerated —
            // duplicate-UT samples occur at boundary seeds and are deduped downstream.
            // Returns false when rejected so state-advancing callers (track-section
            // append, lastRecordedUT bookkeeping) can short-circuit too — otherwise the
            // same rejected point would re-enter treeRec.Points through the track-section
            // flush path (TrajectoryTextSidecarCodec.AppendPointsFromTrackSections).
            if (!FlightRecorder.IsAppendUTMonotonic(treeRec.Points, point.ut))
            {
                TrajectoryPoint last = treeRec.Points[treeRec.Points.Count - 1];
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Warn("BgRecorder",
                    $"ApplyTrajectoryPointToRecording: rejected non-monotonic UT " +
                    $"recId={treeRec.RecordingId} " +
                    $"incoming ut={point.ut.ToString("R", ic)} " +
                    $"< last ut={last.ut.ToString("R", ic)} " +
                    $"(points={treeRec.Points.Count}, dropped to preserve monotonicity — #419)");
                return false;
            }

            treeRec.Points.Add(point);
            treeRec.MarkFilesDirty();
            if (double.IsNaN(treeRec.ExplicitEndUT) || treeRec.ExplicitEndUT < point.ut)
                treeRec.ExplicitEndUT = point.ut;
            return true;
        }

        /// <summary>
        /// Returns the last trajectory frame of a background vessel's current TrackSection,
        /// or null if empty/inactive. Used to capture a boundary point before closing (#283).
        /// </summary>
        private static TrajectoryPoint? GetLastBackgroundFrame(BackgroundVesselState state)
        {
            if (state.trackSectionActive && state.currentTrackSection.frames != null
                && state.currentTrackSection.frames.Count > 0)
                return state.currentTrackSection.frames[state.currentTrackSection.frames.Count - 1];
            return null;
        }

        /// <summary>
        /// Seeds a background vessel's current (newly opened) TrackSection with a boundary
        /// point from the previous section, eliminating position discontinuities (#283).
        /// </summary>
        private static void SeedBackgroundBoundaryPoint(BackgroundVesselState state, TrajectoryPoint? point)
        {
            if (!point.HasValue) return;
            AddFrameToActiveTrackSection(state, point.Value);
            ParsekLog.Verbose("BgRecorder",
                $"Boundary point seeded: pid={state.vesselPid} ut={point.Value.ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        private static void AddFrameToActiveTrackSection(
            BackgroundVesselState state,
            TrajectoryPoint point,
            TrajectoryPoint? bodyFixedPrimaryPoint = null)
        {
            if (!state.trackSectionActive || state.currentTrackSection.frames == null)
                return;

            state.currentTrackSection.frames.Add(point);
            AppendSectionFrameWarpFlag(state);
            if (bodyFixedPrimaryPoint.HasValue && state.currentTrackSection.bodyFixedFrames != null)
                state.currentTrackSection.bodyFixedFrames.Add(bodyFixedPrimaryPoint.Value);
            if (float.IsNaN(state.currentTrackSection.minAltitude)
                || point.altitude < state.currentTrackSection.minAltitude)
            {
                state.currentTrackSection.minAltitude = (float)point.altitude;
            }

            if (float.IsNaN(state.currentTrackSection.maxAltitude)
                || point.altitude > state.currentTrackSection.maxAltitude)
            {
                state.currentTrackSection.maxAltitude = (float)point.altitude;
            }
        }

        private static void AppendBoundaryPointToRecording(Recording treeRec, TrajectoryPoint point, uint vesselPid)
        {
            if (treeRec == null)
                return;

            treeRec.Points.Add(point);
            treeRec.MarkFilesDirty();
            treeRec.ExplicitEndUT = point.ut;

            ParsekLog.Verbose("BgRecorder", $"Boundary point sampled: pid={vesselPid} " +
                $"UT={point.ut:F1} alt={point.altitude:F0} pts={treeRec.Points.Count}");
        }

        /// <summary>
        /// Closes the current TrackSection for a background vessel and appends it
        /// to the per-vessel trackSections list.
        /// </summary>
        private void CloseBackgroundTrackSection(BackgroundVesselState state, double ut)
        {
            if (!state.trackSectionActive) return;

            state.currentTrackSection.endUT = ut;

            // Compute approximate sample rate
            if (state.currentTrackSection.frames != null && state.currentTrackSection.frames.Count > 1)
            {
                double duration = state.currentTrackSection.endUT - state.currentTrackSection.startUT;
                if (duration > 0)
                    state.currentTrackSection.sampleRateHz =
                        (float)(state.currentTrackSection.frames.Count / duration);
            }

            int frameCount = state.currentTrackSection.frames?.Count ?? 0;
            int bodyFixedFrameCount = state.currentTrackSection.bodyFixedFrames?.Count ?? 0;
            int checkpointCount = state.currentTrackSection.checkpoints?.Count ?? 0;
            double sectionDuration = ut - state.currentTrackSection.startUT;
            TrackSectionCloseDisposition disposition = TrackSectionCloseClassifier.Classify(
                frameCount,
                bodyFixedFrameCount,
                checkpointCount,
                sectionDuration,
                state.currentTrackSection.referenceFrame,
                state.currentTrackSection.isBoundarySeam);

            // Discard a short PAYLOAD-FREE section (no frames, no bodyFixedFrames, no
            // checkpoints) regardless of reference frame. Mirror of
            // FlightRecorder.CloseCurrentTrackSection's payload-free discard; see that
            // method for the rationale. THE BG CASE IS THE ONE THAT COST A DEFECT: an
            // undock split opens an Absolute section at the undock instant and closes it
            // 0.02s later with zero frames, before the Relative section anchored to the
            // active sibling opens. That empty shell disabled both codec defences for the
            // whole recording, so the Relative section's anchor-local metre offsets were
            // persisted as flat lat/lon points and maxDist read ~735 km for a rover that
            // never left the pad area (todo
            // UNDOCK-BG-CHILD-WRITES-RELATIVE-METRES-AS-FLAT-LAT-LON).
            if (disposition == TrackSectionCloseDisposition.DiscardPayloadFree)
            {
                state.trackSectionActive = false;
                ParsekLog.Verbose("BgRecorder",
                    "TrackSection discarded (payload-free: no frames, bodyFixedFrames or checkpoints): " +
                    $"env={state.currentTrackSection.environment} ref={state.currentTrackSection.referenceFrame} " +
                    $"pid={state.vesselPid} " +
                    $"startUT={state.currentTrackSection.startUT.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"endUT={ut.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"duration={sectionDuration.ToString("F3", CultureInfo.InvariantCulture)}s");
                return;
            }

            // Discard seed-only transient RELATIVE sections that were closed
            // within one physics frame of opening. Mirror of
            // FlightRecorder.CloseCurrentTrackSection's discard (see that
            // method for the full rationale and scope notes). The canonical
            // BG case: ExitBackgroundRelativeMode fires from the force-Absolute
            // gate one physics frame after an environment-boundary handler
            // opened a Relative section with only the env-boundary seed
            // point, producing a 1-frame 0.02s Relative section that
            // would otherwise generate a multi-meter
            // boundaryDiscontinuityMeters against the next Absolute section
            // and a matching anchor-correction offset at playback. The
            // observed user-visible symptom: vessel ghosts spawning ~24m
            // displaced from the live re-fly vessel on entry. 0.05s threshold
            // = the tightest production min sample interval (Full-tier
            // ProximitySamplingCadence). Restricted to Relative to preserve
            // legitimate Absolute single-frame finalizations used by
            // FinalizeAllForCommit; isBoundarySeam-flagged sections are
            // also exempted for the optimizer split-suppression contract
            // (TrackSection.isBoundarySeam, docs/dev/plans/optimizer-persistence-split.md §5).
            // The predicate itself lives in TrackSectionCloseClassifier.Classify so both
            // recorders read one decision and it is unit-testable.
            if (disposition == TrackSectionCloseDisposition.DiscardSeedOnlyRelativeTransient)
            {
                state.trackSectionActive = false;
                ParsekLog.Verbose("BgRecorder",
                    $"TrackSection discarded (seed-only Relative transient, frames={frameCount} " +
                    $"duration={sectionDuration.ToString("F4", CultureInfo.InvariantCulture)}s): " +
                    $"env={state.currentTrackSection.environment} ref={state.currentTrackSection.referenceFrame} " +
                    $"pid={state.vesselPid} " +
                    $"startUT={state.currentTrackSection.startUT.ToString("F3", CultureInfo.InvariantCulture)}");
                return;
            }

            state.trackSections.Add(state.currentTrackSection);
            state.trackSectionActive = false;

            float configuredMaxSampleInterval = ParsekSettings.Current?.maxSampleInterval
                ?? ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium);
            double sparseGapThreshold =
                FlightRecorder.ResolveSparseGapWarningThreshold(configuredMaxSampleInterval);
            FlightRecorder.SectionGapStats gapStats =
                FlightRecorder.ComputeSectionGapStats(
                    state.currentTrackSection.frames,
                    largeGapThresholdSeconds: sparseGapThreshold,
                    warpFlags: state.sectionFrameWarpFlags,
                    silences: state.sectionProximitySilences);
            if (state.currentTrackSection.referenceFrame == ReferenceFrame.Relative
                && string.IsNullOrWhiteSpace(state.currentTrackSection.anchorRecordingId))
            {
                ParsekLog.Warn("BgRecorder",
                    $"Relative TrackSection closing without anchorRecordingId: " +
                    $"pid={state.vesselPid} startUT={state.currentTrackSection.startUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"endUT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
            }

            string anchorSuffix = state.currentTrackSection.referenceFrame == ReferenceFrame.Relative
                ? $" anchorRecordingId={state.currentTrackSection.anchorRecordingId ?? "(none)"}"
                : string.Empty;
            ParsekLog.Info("BgRecorder",
                $"TrackSection closed: env={state.currentTrackSection.environment} " +
                $"ref={state.currentTrackSection.referenceFrame} " +
                $"frames={frameCount} checkpoints={checkpointCount} " +
                $"duration={sectionDuration.ToString("F2", CultureInfo.InvariantCulture)}s " +
                $"avgGap={gapStats.AverageGapSeconds.ToString("F3", CultureInfo.InvariantCulture)}s " +
                $"maxGap={gapStats.MaxGapSeconds.ToString("F3", CultureInfo.InvariantCulture)}s " +
                $"largeGaps={gapStats.LargeGapCount} " +
                $"pid={state.vesselPid}{anchorSuffix}");

            if (gapStats.LargeGapCount > 0)
            {
                bool warn = FlightRecorder.ShouldWarnOnSparseSampling(
                    gapStats.LargeGapCountOffRails);
                string message =
                    $"TrackSection sparse sampling: pid={state.vesselPid} " +
                    $"env={state.currentTrackSection.environment} " +
                    $"ref={state.currentTrackSection.referenceFrame} frames={frameCount} " +
                    $"maxGap={gapStats.MaxGapSeconds.ToString("F3", CultureInfo.InvariantCulture)}s " +
                    $"threshold={sparseGapThreshold.ToString("F2", CultureInfo.InvariantCulture)}s " +
                    $"largeGaps={gapStats.LargeGapCount} largeGapsOffRails={gapStats.LargeGapCountOffRails} " +
                    $"largeGapsOutOfRange={gapStats.LargeGapCountInSilence}";
                if (warn)
                    ParsekLog.Warn("BgRecorder", message);
                else
                    ParsekLog.Verbose("BgRecorder", message);
            }

            // Phase 7: per-surface-section clearance distribution
            // diagnostic. Mirrors FlightRecorder.CloseCurrentTrackSection so
            // BG-recorded surface vessels emit the same `clearanceMin/Max/Avg/N`
            // line as foreground-recorded surface vessels. Suppressed when no
            // surface samples were captured (non-surface sections
            // and Relative-frame sections leave the counter at zero).
            if (FlightRecorder.IsSurfaceClearanceEnvironment(state.currentTrackSection.environment)
                && state.surfaceMobileSamplesThisSection > 0)
            {
                CultureInfo ic = CultureInfo.InvariantCulture;
                double avg = state.surfaceMobileClearanceSumThisSection / state.surfaceMobileSamplesThisSection;
                ParsekLog.Verbose("Pipeline-Terrain",
                    $"BG section close env={state.currentTrackSection.environment} pid={state.vesselPid} " +
                    $"clearanceMin={state.surfaceMobileMinClearanceThisSection.ToString("F3", ic)}m " +
                    $"clearanceMax={state.surfaceMobileMaxClearanceThisSection.ToString("F3", ic)}m " +
                    $"clearanceAvg={avg.ToString("F3", ic)}m " +
                    $"N={state.surfaceMobileSamplesThisSection}");
            }

            // Defensive accumulator reset (mirrors P3-3 follow-up on
            // FlightRecorder.CloseCurrentTrackSection — closing without
            // immediately opening must not leave stale state).
            state.surfaceMobileSamplesThisSection = 0;
            state.surfaceMobileMinClearanceThisSection = double.NaN;
            state.surfaceMobileMaxClearanceThisSection = double.NaN;
            state.surfaceMobileClearanceSumThisSection = 0.0;
        }

        /// <summary>
        /// Copies all accumulated TrackSections from a BackgroundVesselState to the
        /// recording's TrackSections list.
        /// </summary>
        private static void FlushTrackSectionsToRecording(BackgroundVesselState state, Recording treeRec)
        {
            if (state.trackSections.Count == 0) return;

            for (int i = 0; i < state.trackSections.Count; i++)
            {
                treeRec.TrackSections.Add(state.trackSections[i]);
            }
            treeRec.MarkFilesDirty();
            int dedupedPointCopies = TrajectoryTextSidecarCodec.AppendPointsFromTrackSections(state.trackSections, treeRec.Points);
            int dedupedOrbitCopies = TrajectoryTextSidecarCodec.AppendOrbitSegmentsFromTrackSections(state.trackSections, treeRec.OrbitSegments);
            treeRec.CachedStats = null;
            treeRec.CachedStatsPointCount = 0;
            DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                treeRec,
                $"FlushTrackSectionsToRecording pid={state.vesselPid}");

            ParsekLog.Info("BgRecorder",
                $"Flushed {state.trackSections.Count} TrackSections to recording: " +
                $"pid={state.vesselPid} recId={state.recordingId} " +
                $"points={treeRec.Points.Count} orbitSegments={treeRec.OrbitSegments.Count} " +
                $"dedupedPointCopies={dedupedPointCopies} dedupedOrbitCopies={dedupedOrbitCopies}");

            // Clear after flush to prevent duplicate sections if flushed again
            // (e.g., FinalizeAllForCommit followed by Shutdown)
            state.trackSections.Clear();
        }

        #endregion
    }
}
