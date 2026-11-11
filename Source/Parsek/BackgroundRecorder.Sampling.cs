using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Trajectory sampling: orbit segments and trajectory points from a vessel, sample diagnostics, boundary samples and the debris-aware sampling cadence.
    internal partial class BackgroundRecorder
    {
        /// <summary>
        /// Creates an OrbitSegment from a vessel's current orbital elements.
        /// Used by OnBackgroundVesselSOIChanged, CheckpointAllVessels, and InitializeOnRailsState.
        /// </summary>
        private static OrbitSegment CreateOrbitSegmentFromVessel(Vessel v, double startUT, string defaultBodyName)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                inclination = v.orbit.inclination,
                eccentricity = v.orbit.eccentricity,
                semiMajorAxis = v.orbit.semiMajorAxis,
                longitudeOfAscendingNode = v.orbit.LAN,
                argumentOfPeriapsis = v.orbit.argumentOfPeriapsis,
                meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch,
                epoch = v.orbit.epoch,
                bodyName = v.mainBody?.name ?? defaultBodyName
            };
        }

        private static TrajectoryPoint CreateTrajectoryPointFromVessel(Vessel v, double ut)
        {
            Vector3 velocity = v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());

            return new TrajectoryPoint
            {
                ut = ut,
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.srfRelRotation,
                velocity = velocity,
                bodyName = v.mainBody?.name ?? "Unknown",
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0,
                // Phase 7: packed/on-rails background vessels do not have
                // surface-clearance sections; leave clearance as NaN sentinel.
                recordedGroundClearance = double.NaN
            };
        }

        private void CloseOrbitSegment(BackgroundOnRailsState state, double ut)
        {
            if (!state.hasOpenOrbitSegment) return;

            state.currentOrbitSegment.endUT = ut;

            Recording treeRec;
            bool appendedCheckpoint = false;
            string checkpointSkipReason = null;
            if (tree.Recordings.TryGetValue(state.recordingId, out treeRec))
            {
                appendedCheckpoint = OrbitSegmentCheckpointBridge.TryAppendClosedCheckpointSection(
                    treeRec,
                    state.currentOrbitSegment,
                    markDirty: true,
                    out checkpointSkipReason);
                treeRec.ExplicitEndUT = ut;
            }

            state.hasOpenOrbitSegment = false;

            ParsekLog.Verbose("BgRecorder", $"Orbit segment closed: pid={state.vesselPid} " +
                $"UT={state.currentOrbitSegment.startUT:F1}-{ut:F1} body={state.currentOrbitSegment.bodyName} " +
                $"checkpointSection={(appendedCheckpoint ? "added" : "skipped:" + (checkpointSkipReason ?? "unknown"))}");
        }

        private static bool TryResolveRootPartSurfacePose(
            Vessel v,
            out double latitude,
            out double longitude,
            out double altitude,
            out Quaternion rotation)
        {
            latitude = v != null ? v.latitude : 0.0;
            longitude = v != null ? v.longitude : 0.0;
            altitude = v != null ? v.altitude : 0.0;
            rotation = v != null ? v.srfRelRotation : Quaternion.identity;

            if (v?.mainBody == null || v.rootPart == null || v.rootPart.transform == null || v.mainBody.bodyTransform == null)
                return false;

            Vector3d worldPos = v.rootPart.transform.position;
            latitude = v.mainBody.GetLatitude(worldPos);
            longitude = v.mainBody.GetLongitude(worldPos);
            altitude = v.mainBody.GetAltitude(worldPos);
            rotation = Quaternion.Inverse(v.mainBody.bodyTransform.rotation) * v.rootPart.transform.rotation;
            return true;
        }

        private static void LogDebrisSeedRelativeConversionDiagnostics(
            uint vesselPid,
            string recordingId,
            string parentRecordingId,
            Vessel childVessel,
            Vessel parentVessel,
            TrajectoryPoint absoluteSeedPoint,
            TrajectoryPoint relativeSeedPoint,
            AnchorPose anchorPose,
            string anchorSource,
            string frameContract,
            string parentDriftFromRecorded,
            string seedFocusRecordingId,
            string seedRecordedFallbackFrameContract,
            bool relativeApplied,
            double initUT)
        {
            bool hasSeedWorld = TryResolveTrajectoryPointWorldPosition(
                absoluteSeedPoint,
                childVessel,
                out Vector3d seedWorld);
            string seedAnchorDelta = hasSeedWorld
                ? DiagnosticFormatters.FormatVector3d(seedWorld - anchorPose.WorldPos)
                : "unresolved";
            string parentDriftField = FormatParentDriftLogField(parentDriftFromRecorded);

            ParsekLog.Verbose("BgRecorder",
                $"Debris seed relative conversion diagnostics: pid={vesselPid} " +
                $"recId={recordingId} parentRecId={parentRecordingId} " +
                $"relativeApplied={(relativeApplied ? "T" : "F")} " +
                $"initUT={initUT.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"seedUT={absoluteSeedPoint.ut.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"init-seed-dt={(initUT - absoluteSeedPoint.ut).ToString("F3", CultureInfo.InvariantCulture)} " +
                $"anchorSource={anchorSource ?? "unknown"} " +
                $"frameContract={frameContract ?? "unknown"} " +
                parentDriftField +
                $"seedFocusRecordingId={seedFocusRecordingId ?? "(missing)"} " +
                $"seedRecordedFallbackFrameContract={seedRecordedFallbackFrameContract ?? "unresolved"} " +
                $"seedAbs={DescribeTrajectoryPointForDiagnostics(absoluteSeedPoint)} " +
                $"seedWorld={(hasSeedWorld ? DiagnosticFormatters.FormatVector3d(seedWorld) : "unresolved")} " +
                $"anchorWorld={DiagnosticFormatters.FormatVector3d(anchorPose.WorldPos)} " +
                $"seed-anchor={seedAnchorDelta} " +
                $"relativeOffset={DiagnosticFormatters.FormatVector3d(new Vector3d(relativeSeedPoint.latitude, relativeSeedPoint.longitude, relativeSeedPoint.altitude))} " +
                $"{DescribeVesselRootForDiagnostics("child", childVessel)} " +
                $"{DescribeVesselRootForDiagnostics("parent", parentVessel)}");
        }

        private static void LogFirstDebrisOrdinarySampleDiagnostics(
            BackgroundVesselState state,
            Recording treeRec,
            Vessel bgVessel,
            TrajectoryPoint absolutePoint,
            TrajectoryPoint recordedPoint,
            bool relativeApplied,
            double sampleUT)
        {
            if (state == null
                || treeRec == null
                || string.IsNullOrEmpty(treeRec.ParentAnchorRecordingId)
                || state.loggedFirstDebrisOrdinarySample)
            {
                return;
            }

            state.loggedFirstDebrisOrdinarySample = true;
            bool hasAbsoluteWorld = TryResolveTrajectoryPointWorldPosition(
                absolutePoint,
                bgVessel,
                out Vector3d absoluteWorld);
            Vector3d rootWorld = bgVessel?.rootPart?.transform != null
                ? (Vector3d)bgVessel.rootPart.transform.position
                : Vector3d.zero;
            bool hasRootWorld = bgVessel?.rootPart?.transform != null;
            string absoluteRootDelta = hasAbsoluteWorld && hasRootWorld
                ? DiagnosticFormatters.FormatVector3d(absoluteWorld - rootWorld)
                : "unresolved";
            string seedToSampleDt = IsFiniteUT(state.lastRecordedUT)
                ? (sampleUT - state.lastRecordedUT).ToString("F3", CultureInfo.InvariantCulture)
                : "n/a";

            ParsekLog.Verbose("BgRecorder",
                $"First debris ordinary sample diagnostics: pid={state.vesselPid} " +
                $"recId={treeRec.RecordingId} parentRecId={treeRec.ParentAnchorRecordingId} " +
                $"sampleUT={sampleUT.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"prevRecordedUT={(IsFiniteUT(state.lastRecordedUT) ? state.lastRecordedUT.ToString("F3", CultureInfo.InvariantCulture) : "n/a")} " +
                $"sample-prev-dt={seedToSampleDt} " +
                $"relativeApplied={(relativeApplied ? "T" : "F")} " +
                $"absolutePoint={DescribeTrajectoryPointForDiagnostics(absolutePoint)} " +
                $"recordedPoint={DescribeTrajectoryPointForDiagnostics(recordedPoint)} " +
                $"absoluteWorld={(hasAbsoluteWorld ? DiagnosticFormatters.FormatVector3d(absoluteWorld) : "unresolved")} " +
                $"rootWorld={(hasRootWorld ? DiagnosticFormatters.FormatVector3d(rootWorld) : "unresolved")} " +
                $"absolute-root={absoluteRootDelta} " +
                $"{DescribeVesselRootForDiagnostics("sample", bgVessel)}");
        }

        private static bool TryResolveTrajectoryPointWorldPosition(
            TrajectoryPoint point,
            Vessel contextVessel,
            out Vector3d worldPosition)
        {
            worldPosition = Vector3d.zero;
            CelestialBody body = contextVessel?.mainBody;
            if ((body == null || !string.Equals(body.name, point.bodyName, StringComparison.Ordinal))
                && FlightGlobals.Bodies != null)
            {
                body = FlightGlobals.Bodies.Find(b =>
                    b != null && string.Equals(b.name, point.bodyName, StringComparison.Ordinal));
            }

            if (body == null)
                return false;

            try
            {
                worldPosition = body.GetWorldSurfacePosition(
                    point.latitude,
                    point.longitude,
                    point.altitude);
                return true;
            }
            catch (Exception)
            {
                worldPosition = Vector3d.zero;
                return false;
            }
        }

        private static string DescribeVesselRootForDiagnostics(string label, Vessel vessel)
        {
            if (vessel == null)
                return label + "=null";

            Part rootPart = vessel.rootPart;
            string vesselWorld = SafeFormatVesselWorldPosition(vessel);
            if (rootPart == null)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}Vessel pid={1} name='{2}' vesselWorld={3} root=null",
                    label,
                    vessel.persistentId,
                    vessel.vesselName ?? "unknown",
                    vesselWorld);
            }

            string rootWorld = rootPart.transform != null
                ? DiagnosticFormatters.FormatVector3d(rootPart.transform.position)
                : "no-transform";
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}Vessel pid={1} name='{2}' vesselWorld={3} root='{4}' rootPid={5} rootWorld={6} srfAttach={7}",
                label,
                vessel.persistentId,
                vessel.vesselName ?? "unknown",
                vesselWorld,
                rootPart.partInfo?.name ?? rootPart.name ?? "unknown",
                rootPart.persistentId,
                rootWorld,
                DiagnosticFormatters.DescribeSurfaceAttachNode(rootPart));
        }

        private static string SafeFormatVesselWorldPosition(Vessel vessel)
        {
            try
            {
                return vessel != null
                    ? DiagnosticFormatters.FormatVector3d(vessel.GetWorldPos3D())
                    : "null";
            }
            catch (Exception)
            {
                // Diagnostics should not fail sampling if KSP cannot resolve the
                // world position during scene/load transitions.
                return "unresolved";
            }
        }

        private static string DescribeTrajectoryPointForDiagnostics(TrajectoryPoint point)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "ut={0:F3} body={1} lla=({2:F4},{3:F4},{4:F2}) rot={5} vel={6}",
                point.ut,
                point.bodyName ?? "unknown",
                point.latitude,
                point.longitude,
                point.altitude,
                DiagnosticFormatters.FormatQuaternion(point.rotation),
                DiagnosticFormatters.FormatVector3(point.velocity));
        }

        internal static bool ShouldPreferRootPartSurfacePoseForBackgroundSample(Recording treeRec)
        {
            // Parent-anchored debris snapshots and seed samples are root-part based.
            // Keep periodic background samples on the same pose contract so the ghost root
            // does not jump from a root-part seed to a vessel-origin ordinary sample.
            return treeRec != null && !string.IsNullOrEmpty(treeRec.ParentAnchorRecordingId);
        }

        internal static float ResolveBackgroundAttitudeMinSampleInterval(
            bool highFidelityActive,
            float effectiveMotionMinSampleInterval,
            float foregroundMinSampleInterval)
        {
            // highFidelityActive is already folded into
            // effectiveMotionMinSampleInterval by the caller; attitude uses the
            // more aggressive of motion cadence and foreground attitude floor.
            _ = highFidelityActive;
            // The two-arg helper currently returns the configured foreground
            // minimum unchanged; keep the call here so this background path stays
            // aligned if foreground cadence policy grows another override.
            float foregroundAttitudeMin = FlightRecorder.ResolveEffectiveMinSampleInterval(
                true,
                foregroundMinSampleInterval);
            return Math.Min(effectiveMotionMinSampleInterval, foregroundAttitudeMin);
        }

        internal static TrajectoryPoint CreateAbsoluteTrajectoryPointFromVessel(
            Vessel v,
            double ut,
            Vector3? explicitVelocity = null,
            bool preferRootPartSurfacePose = false)
        {
            Vector3 velocity = explicitVelocity ?? (v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity()));

            double latitude = v.latitude;
            double longitude = v.longitude;
            double altitude = v.altitude;
            Quaternion rotation = v.srfRelRotation;
            string llaSource = "vessel-fields";
            if (preferRootPartSurfacePose)
            {
                llaSource = TryResolveRootPartSurfacePose(v, out latitude, out longitude, out altitude, out rotation)
                    ? "root-part"
                    : "root-part-fallback-vessel-fields";
            }
            else if (!v.packed && v.mainBody != null && v.transform != null)
            {
                // Packed/on-rails background vessels are positioned by orbit propagation,
                // not PhysX; keep those on the Vessel-field path.
                // Match FlightRecorder.BuildTrajectoryPoint for loaded vessels:
                // Vessel.latitude/longitude/altitude can lag transform.position
                // by one physics tick, which is visible right after separation.
                Vector3d freshWorldPos = v.transform.position;
                latitude = v.mainBody.GetLatitude(freshWorldPos);
                longitude = v.mainBody.GetLongitude(freshWorldPos);
                altitude = v.mainBody.GetAltitude(freshWorldPos);
                llaSource = "transform";
            }

            TrajectoryPoint pt = new TrajectoryPoint
            {
                ut = ut,
                latitude = latitude,
                longitude = longitude,
                altitude = altitude,
                rotation = rotation,
                velocity = velocity,
                bodyName = v.mainBody?.name ?? "Unknown",
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0,
                // Phase 7: NaN sentinel for non-surface points.
                recordedGroundClearance = double.NaN
            };
            // ---- Trace-Sep: log every BG per-tick + structural-event sample ----
            if (v != null && (TraceSeparation.RecordingWindowActive || TraceSeparation.PlaybackWindowActive))
            {
                Vector3d worldPos = v.mainBody != null
                    ? v.mainBody.GetWorldSurfacePosition(pt.latitude, pt.longitude, pt.altitude)
                    : Vector3d.zero;
                Vector3d transformPos = v.transform != null ? (Vector3d)v.transform.position : Vector3d.zero;
                // tickSinceBreak parity with FG BuildTP. See
                // FlightRecorder.BuildTrajectoryPoint trace comment for the
                // diagnostic value of this field.
                double lastBreak = TraceSeparation.LastRecordingTriggerUT;
                double tickSinceBreak = double.NaN;
                if (!double.IsNaN(lastBreak))
                {
                    float dt = Time.fixedDeltaTime;
                    if (dt > 0f && !float.IsNaN(dt) && !float.IsInfinity(dt))
                        tickSinceBreak = (ut - lastBreak) / dt;
                }
                TraceSeparation.RecordLog("BG_CreateAbs",
                    "pid=" + v.persistentId +
                    " name='" + v.vesselName + "'" +
                    " ut=" + ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    " tickSinceBreak=" + (double.IsNaN(tickSinceBreak) ? "NaN" : tickSinceBreak.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)) +
                    " packed=" + v.packed +
                    " explicitVel=" + explicitVelocity.HasValue +
                    " preferRoot=" + preferRootPartSurfacePose +
                    " llaSource=" + llaSource +
                    " LLA=(" + pt.latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    "," + pt.longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    "," + pt.altitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")" +
                    " worldFromLLA=" + TraceSeparation.FormatVector3d(worldPos) +
                    " transformPos=" + TraceSeparation.FormatVector3d(transformPos) +
                    " transformVsLLAdelta=" + TraceSeparation.FormatVector3d(transformPos - worldPos) +
                    " |delta|=" + (transformPos - worldPos).magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    " velIn=" + TraceSeparation.FormatVector3(velocity) + " |v|=" + velocity.magnitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    " srfVel=" + TraceSeparation.FormatVector3(v.srf_velocity) + " |sv|=" + v.srf_velocity.magnitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            // ---- /Trace-Sep ----
            return pt;
        }

        private static void LogBackgroundReFlyOrbitSegmentVelocityNonFinite(
            string recordingId,
            Vessel vessel,
            double startUT,
            string context)
        {
            FlightRecorder.LogReFlyOrbitSegmentVelocityNonFinite(
                recordingId,
                vessel,
                startUT,
                "BgRecorder",
                context);
        }

        private void FlushLoadedStateForOnRailsTransition(
            BackgroundVesselState loadedState,
            Recording flushRec,
            SegmentEnvironment nextEnv,
            bool willHavePlayableOnRailsPayload,
            TrajectoryPoint boundaryPoint,
            double ut)
        {
            SegmentEnvironment previousEnv = loadedState.trackSectionActive
                ? loadedState.currentTrackSection.environment
                : loadedState.environmentHysteresis != null
                    ? loadedState.environmentHysteresis.CurrentEnvironment
                    : SegmentEnvironment.Atmospheric;
            bool persistNoPayloadBoundarySection = ShouldPersistNoPayloadOnRailsBoundaryTrackSection(
                previousEnv, nextEnv, willHavePlayableOnRailsPayload);

            if (loadedState.isRelativeMode)
            {
                ParsekLog.Info("BgRecorder",
                    $"RELATIVE mode cleared on rails transition: pid={loadedState.vesselPid} " +
                    $"anchorRecordingId={loadedState.currentAnchorRecordingId ?? "(none)"} " +
                    $"diagnosticPid={loadedState.currentAnchorCandidate.DiagnosticPid}");
                ClearBackgroundCurrentAnchor(loadedState);
            }

            CloseBackgroundTrackSection(loadedState, ut);

            if (persistNoPayloadBoundarySection)
            {
                StartBackgroundTrackSection(loadedState, nextEnv, ReferenceFrame.Absolute, ut);
                AddFrameToActiveTrackSection(loadedState, boundaryPoint);
                // Mark the section as a recorder bookkeeping artifact: RecordingOptimizer
                // never treats boundaries on either side of a seam-flagged section as
                // split candidates (hard step-1 override, ahead of body / Surface /
                // ExoPropulsive short-circuits). See TrackSection.isBoundarySeam and
                // docs/dev/plans/optimizer-persistence-split.md §5.
                // TrackSection is a struct; mutate via field assignment on the held copy.
                loadedState.currentTrackSection.isBoundarySeam = true;
                CloseBackgroundTrackSection(loadedState, ut);
                // Read the flag from the closed section's held copy. CloseBackgroundTrackSection
                // doesn't zero `currentTrackSection`, so the flag survives. This phrasing keeps
                // the log honest if a future producer ever shares this code path with a non-seam
                // emit (today the persistNoPayloadBoundarySection branch always sets the flag).
                string seamSuffix = loadedState.currentTrackSection.isBoundarySeam ? " (seam=1)" : "";
                ParsekLog.Info("BgRecorder",
                    $"Persisted no-payload on-rails boundary section: pid={loadedState.vesselPid} " +
                    $"{previousEnv}->{nextEnv} at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}{seamSuffix}");
            }

            FlushTrackSectionsToRecording(loadedState, flushRec);
            flushRec.ExplicitEndUT = ut;

            if (!persistNoPayloadBoundarySection)
                AppendBoundaryPointToRecording(flushRec, boundaryPoint, loadedState.vesselPid);

            DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                flushRec,
                $"FlushLoadedStateForOnRailsTransition pid={loadedState.vesselPid}");
        }

        private void SampleBoundaryPoint(Vessel v, Recording treeRec, double ut)
        {
            if (v == null) return;

            TrajectoryPoint point = CreateAbsoluteTrajectoryPointFromVessel(v, ut);
            AppendBoundaryPointToRecording(treeRec, point, v.persistentId);
        }

        /// <summary>
        /// Computes the distance in meters between a background vessel and the focused vessel.
        /// Returns double.MaxValue if there is no focused vessel (defensive).
        /// Uses an injectable override for testing; falls back to FlightGlobals.ActiveVessel.
        /// </summary>
        internal double ComputeDistanceToFocusedVessel(Vessel bgVessel)
        {
            if (distanceOverrideForTesting != null)
                return distanceOverrideForTesting(bgVessel);

            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null) return double.MaxValue;
            return Vector3d.Distance(active.transform.position, bgVessel.transform.position);
        }

        /// <summary>
        /// Formats a sample interval for log messages.
        /// </summary>
        private static string FormatInterval(double interval)
        {
            if (interval >= ProximityRateSelector.OutOfRangeInterval || double.IsInfinity(interval))
                return "none";
            return interval.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "s";
        }

        private static bool IsBackgroundHighFidelitySamplingActive(
            BackgroundVesselState state,
            double currentUT,
            double proximityDistanceMeters)
        {
            return state != null
                && FlightRecorder.IsHighFidelitySamplingActive(
                    currentUT,
                    state.highFidelitySamplingUntilUT,
                    proximityDistanceMeters);
        }

        // Predicate gate for the debris parent-anchored sampling caps below.
        // Both the proximity-tier (min) cap and the configured-max backstop cap
        // share this gate so they always engage or disengage together.
        //
        // KEEP debris-only: the dual conjunct `IsDebris && ParentAnchorRecordingId != null`
        // is intentional - the cap was sized for short-lived debris orbiting a parent
        // for tens of seconds. Controlled-decoupled children (extension of the parent-anchor
        // contract) also carry ParentAnchorRecordingId but record indefinitely, so admitting
        // them here would multiply long-loiter sample storage 6-16x against an MIN-floor
        // / MAX-backstop tuning that was never meant for their lifetime profile. If a
        // future fidelity regression is reported for radial-breakup controlled children,
        // file a separate follow-up rather than collapsing this gate.
        internal static bool IsDebrisAwareSampleCapEligible(Recording treeRec)
        {
            return treeRec != null
                && treeRec.IsDebris
                && !string.IsNullOrEmpty(treeRec.ParentAnchorRecordingId);
        }

        private double ResolveDebrisParentDistanceMeters(Recording debrisRecording, Vessel debrisVessel)
        {
            if (!IsDebrisAwareSampleCapEligible(debrisRecording)
                || debrisVessel == null)
            {
                return double.NaN;
            }

            return ResolveDebrisParentDistanceMeters(
                debrisRecording,
                debrisVessel,
                out _,
                out _);
        }

        private double ResolveDebrisParentDistanceMeters(
            Recording debrisRecording,
            Vessel debrisVessel,
            out Recording parentRecording,
            out Vessel parentVessel)
        {
            parentRecording = null;
            parentVessel = null;

            if (debrisRecording == null
                || debrisVessel == null
                || tree?.Recordings == null
                || string.IsNullOrEmpty(debrisRecording.ParentAnchorRecordingId))
            {
                return double.NaN;
            }

            if (!tree.Recordings.TryGetValue(
                    debrisRecording.ParentAnchorRecordingId,
                    out parentRecording)
                || parentRecording == null
                || parentRecording.VesselPersistentId == 0u)
            {
                return double.NaN;
            }

            parentVessel = FlightRecorder.FindLaunchMatchedVesselForRecording(parentRecording);
            if (parentVessel == null || !parentVessel.loaded || parentVessel.packed)
                return double.NaN;

            // Use CoM (GetWorldPos3D) rather than vesselTransform.position here.
            // Pose composition deliberately uses vesselTransform to stay symmetric
            // with the recorded lat/lon/alt frame (CoM-to-vesselTransform vector is
            // ~10 m for radial boosters), but for the cadence-tier thresholds
            // (250 / 500 / 550 m) the CoM vs vesselTransform delta is well below
            // the boundary resolution and CoM is the cheaper, more conventional
            // proximity metric.
            Vector3d parentWorldPos = parentVessel.GetWorldPos3D();
            Vector3d debrisWorldPos = debrisVessel.GetWorldPos3D();
            if (!IsFinite(parentWorldPos) || !IsFinite(debrisWorldPos))
                return double.NaN;

            return Vector3d.Distance(parentWorldPos, debrisWorldPos);
        }

        private ProximitySamplingTier UpdateDebrisProximityState(
            BackgroundVesselState state,
            Recording treeRec,
            Vessel debrisVessel)
        {
            if (state == null)
                return ProximitySamplingTier.None;

            ProximitySamplingTier previousTier = state.debrisProximityTier;
            double distance = ResolveDebrisParentDistanceMeters(treeRec, debrisVessel);
            ProximitySamplingTier tier = ProximitySamplingTier.None;
            string reason = null;
            if (IsDebrisAwareSampleCapEligible(treeRec))
            {
                tier = ProximitySamplingCadence.Resolve(
                    distance,
                    DebrisFullFidelityProximityRangeMeters,
                    DebrisHalfFidelityProximityRangeMeters,
                    out reason);
            }

            if (previousTier != tier)
            {
                LogDebrisProximityTierTransition(
                    state.vesselPid,
                    treeRec,
                    previousTier,
                    tier,
                    distance,
                    reason,
                    "UpdateDebrisProximityState");
            }

            state.debrisProximityTier = tier;
            state.debrisProximityDistanceMeters = distance;
            state.debrisProximityReason = reason;
            return tier;
        }

        private static void LogDebrisProximityTierTransition(
            uint vesselPid,
            Recording treeRec,
            ProximitySamplingTier previousTier,
            ProximitySamplingTier newTier,
            double distance,
            string reason,
            string site)
        {
            if (previousTier == newTier)
                return;

            ParsekLog.Info(
                "Anchor",
                $"debris-proximity-tier-transition: site={site ?? "(unknown)"} " +
                $"pid={vesselPid.ToString(CultureInfo.InvariantCulture)} " +
                $"recId={treeRec?.RecordingId ?? "(none)"} " +
                $"parentRecId={treeRec?.ParentAnchorRecordingId ?? "(none)"} " +
                $"old={previousTier} new={newTier} " +
                $"distance={(IsFinite(distance) ? distance.ToString("F1", CultureInfo.InvariantCulture) : "NaN")}m " +
                $"reason={reason ?? "ineligible"}");
        }

        private static bool ShouldUseDebrisRelativeSection(BackgroundVesselState state)
        {
            if (state == null)
                return false;

            bool alreadyRelative = state.isRelativeMode
                || (state.trackSectionActive
                    && state.currentTrackSection.referenceFrame == ReferenceFrame.Relative);
            return ShouldUseDebrisRelativeSectionForDistance(
                state.debrisProximityDistanceMeters,
                alreadyRelative);
        }

        internal static bool ShouldUseDebrisRelativeSectionForDistance(
            double distanceMeters,
            bool alreadyRelative)
        {
            if (!IsFinite(distanceMeters))
                return false;

            double threshold = alreadyRelative
                ? DebrisRelativeSectionExitMeters
                : DebrisHalfFidelityProximityRangeMeters;
            return distanceMeters <= threshold;
        }

        // For parent-anchored debris recordings whose parent recording id is known, cap the
        // proximity-derived sample interval (the adaptive sampler's MIN floor)
        // at MidInterval (0.5 s) regardless of distance from the focused vessel.
        // Without this cap, radial debris that flies past 1000 m of its parent
        // during high-velocity breakup falls into the FarRange tier (2.0 s gap),
        // producing visibly choppy trajectory rendering.
        //
        // Out-of-range tier is preserved as-is; the later proximity skip is
        // bypassed only when the parent-proximity debris tier is active. Legacy
        // v11 debris (no
        // ParentAnchorRecordingId) and non-debris BG vessels keep the unaltered
        // tier table.
        internal static double ResolveDebrisAwareSampleInterval(
            double tierInterval,
            Recording treeRec)
        {
            if (!IsDebrisAwareSampleCapEligible(treeRec)) return tierInterval;
            if (tierInterval >= ProximityRateSelector.OutOfRangeInterval) return tierInterval;
            return Math.Min(tierInterval, ProximityRateSelector.MidInterval);
        }

        internal static bool ShouldSkipTrajectorySamplingForProximity(
            double proximityInterval,
            bool highFidelityActive,
            ProximitySamplingTier debrisTier)
        {
            return proximityInterval >= ProximityRateSelector.OutOfRangeInterval
                && !highFidelityActive
                && debrisTier == ProximitySamplingTier.None;
        }

        // Companion to ResolveDebrisAwareSampleInterval that bounds the adaptive
        // sampler's MAX backstop at MidInterval (0.5 s). Without this cap,
        // parent-anchored debris with stable velocity after the 3-second
        // high-fidelity post-decouple window only samples when the configured
        // max backstop fires — 3.0 s on Medium and 8.0 s on Low — even though
        // the proximity-tier floor was capped to 0.5 s. ShouldRecordPoint takes
        // the backstop as `if (elapsed >= maxInterval) return true`, so the
        // floor cap alone does not enforce the claimed 0.5 s sample-gap ceiling
        // for stable-velocity drift.
        //
        // Capped value composes with FlightRecorder.ResolveEffectiveMaxSampleInterval:
        // during the high-fidelity window that helper already returns
        // min(configuredMax, configuredMin) which is tighter than 0.5 s, so the
        // cap here is a no-op inside the window and a 0.5 s ceiling outside it.
        internal static float ResolveDebrisAwareMaxSampleInterval(
            float configuredMax,
            Recording treeRec)
        {
            if (!IsDebrisAwareSampleCapEligible(treeRec)) return configuredMax;
            return Math.Min(configuredMax, (float)ProximityRateSelector.MidInterval);
        }

        private static void ActivateBackgroundHighFidelitySampling(
            BackgroundVesselState state,
            double eventUT,
            string reason)
        {
            if (state == null || double.IsNaN(eventUT) || double.IsInfinity(eventUT))
                return;

            float configuredMax = ParsekSettings.Current?.maxSampleInterval
                ?? ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium);
            double windowSeconds = FlightRecorder.ResolveHighFidelitySamplingWindowSeconds(configuredMax);
            double untilUT = eventUT + windowSeconds;
            bool extendsWindow = double.IsNaN(state.highFidelitySamplingUntilUT)
                || untilUT > state.highFidelitySamplingUntilUT + 0.001;
            state.highFidelitySamplingUntilUT = Math.Max(
                double.IsNaN(state.highFidelitySamplingUntilUT)
                    ? double.MinValue
                    : state.highFidelitySamplingUntilUT,
                untilUT);
            state.highFidelitySamplingReason = reason ?? "unknown";

            if (extendsWindow)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Info("BgRecorder",
                    $"High-fidelity sampling window active: pid={state.vesselPid} " +
                    $"reason={state.highFidelitySamplingReason} " +
                    $"eventUT={eventUT.ToString("F2", ic)} " +
                    $"untilUT={state.highFidelitySamplingUntilUT.ToString("F2", ic)} " +
                    $"windowSeconds={windowSeconds.ToString("F3", ic)} " +
                    $"proximityRange={FlightRecorder.HighFidelityProximityRangeMeters.ToString("F1", ic)}m " +
                    $"intervalPolicy=configured-min-sample-interval");
            }
        }
    }
}
