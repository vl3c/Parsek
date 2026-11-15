using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Per-vessel state initialization: on-rails and loaded state setup, inherited engine state and part-state seeding.
    internal partial class BackgroundRecorder
    {
        private static bool WillInitializeOnRailsWithPlayablePayload(Vessel v)
        {
            bool isLanded = v.situation == Vessel.Situations.LANDED ||
                            v.situation == Vessel.Situations.SPLASHED;
            if (isLanded || v.situation == Vessel.Situations.PRELAUNCH)
                return false;

            if (v.mainBody != null &&
                FlightRecorder.ShouldSkipOrbitSegmentForAtmosphere(
                    v.mainBody.atmosphere, v.altitude, v.mainBody.atmosphereDepth))
            {
                return false;
            }

            return v.orbit != null;
        }

        private void InitializeOnRailsState(Vessel v, uint vesselPid, string recordingId)
        {
            double ut = Planetarium.GetUniversalTime();
            Recording treeRec;
            bool hasTreeRecording = tree.Recordings.TryGetValue(recordingId, out treeRec);

            // Destroyed-remnant guard runs FIRST — before the pending initial
            // trajectory point is consumed/applied and before any state-machine
            // branch writes landed/orbiting surface metadata. A destroyed recording
            // must NOT have an onRailsStates entry at all: leaving one would let
            // UpdateOnRails / FinalizeAllForCommit iterate over it (the secondary
            // guards on those paths catch the case, but the cleanest invariant is
            // "destroyed recordings are not in BG-tracking structures"). The pending
            // initial point is still drained from the dictionary so it does not leak
            // across to a future recording reusing the same pid.
            if (hasTreeRecording && treeRec.VesselDestroyed)
            {
                TrajectoryPoint discardedPoint;
                TryConsumePendingInitialTrajectoryPoint(vesselPid, out discardedPoint);
                ParsekLog.Verbose("BgRecorder",
                    $"InitializeOnRailsState: skipping destroyed recording — no on-rails state created " +
                    $"pid={vesselPid} body={v.mainBody?.name} sit={v.situation} rec={recordingId}");
                return;
            }

            TrajectoryPoint initialTrajectoryPoint;
            bool hasInitialTrajectoryPoint = TryConsumePendingInitialTrajectoryPoint(vesselPid, out initialTrajectoryPoint);
            if (hasInitialTrajectoryPoint && initialTrajectoryPoint.ut > ut)
                initialTrajectoryPoint.ut = ut;
            if (hasInitialTrajectoryPoint && hasTreeRecording)
            {
                // #419: only log "seeded" if the flat-append was accepted. A rejection
                // already produces its own warn inside ApplyTrajectoryPointToRecording.
                if (ApplyTrajectoryPointToRecording(treeRec, initialTrajectoryPoint))
                {
                    ParsekLog.Info("BgRecorder", string.Format(CultureInfo.InvariantCulture,
                        "Initial trajectory point seeded (on-rails): pid={0} ut={1:F2} " +
                        "lat={2:F4} lon={3:F4} alt={4:F0} vel=({5:F2},{6:F2},{7:F2}) |vel|={8:F2}",
                        vesselPid,
                        initialTrajectoryPoint.ut,
                        initialTrajectoryPoint.latitude,
                        initialTrajectoryPoint.longitude,
                        initialTrajectoryPoint.altitude,
                        initialTrajectoryPoint.velocity.x,
                        initialTrajectoryPoint.velocity.y,
                        initialTrajectoryPoint.velocity.z,
                        initialTrajectoryPoint.velocity.magnitude));
                }
            }

            var state = new BackgroundOnRailsState
            {
                vesselPid = vesselPid,
                recordingId = recordingId,
                lastExplicitEndUpdate = ut
            };

            bool isLanded = v.situation == Vessel.Situations.LANDED ||
                            v.situation == Vessel.Situations.SPLASHED;

            if (isLanded)
            {
                state.isLanded = true;
                state.hasOpenOrbitSegment = false;

                // Capture SurfacePosition
                if (hasTreeRecording)
                {
                    treeRec.SurfacePos = new SurfacePosition
                    {
                        body = v.mainBody?.name ?? "Kerbin",
                        latitude = v.latitude,
                        longitude = v.longitude,
                        altitude = v.altitude,
                        rotation = v.srfRelRotation,
                        rotationRecorded = true,
                        situation = v.situation == Vessel.Situations.SPLASHED
                            ? SurfaceSituation.Splashed
                            : SurfaceSituation.Landed
                    };
                    treeRec.ExplicitEndUT = ut;
                }

                ParsekLog.Verbose("BgRecorder", $"On-rails state initialized (landed): pid={vesselPid} " +
                    $"body={v.mainBody?.name} sit={v.situation}");
            }
            else if (v.mainBody != null &&
                     FlightRecorder.ShouldSkipOrbitSegmentForAtmosphere(
                         v.mainBody.atmosphere, v.altitude, v.mainBody.atmosphereDepth))
            {
                // In atmosphere — Keplerian orbit ignores drag, skip orbit segment
                state.hasOpenOrbitSegment = false;

                if (hasTreeRecording)
                {
                    treeRec.ExplicitEndUT = ut;
                }

                string atmoOrbitShape = v.orbit != null
                    ? string.Format(CultureInfo.InvariantCulture,
                        " sma={0:F1} ecc={1:F4} periAlt={2:F1} apoAlt={3:F1}",
                        v.orbit.semiMajorAxis, v.orbit.eccentricity, v.orbit.PeA, v.orbit.ApA)
                    : "";
                ParsekLog.Verbose("BgRecorder", $"On-rails state initialized (atmosphere, skip orbit): pid={vesselPid} " +
                    $"body={v.mainBody?.name} alt={v.altitude:F0} atmoDepth={v.mainBody.atmosphereDepth:F0}{atmoOrbitShape}");
            }
            else if (v.orbit != null)
            {
                // Open orbit segment
                state.currentOrbitSegment = CreateOrbitSegmentFromVessel(
                    v,
                    hasInitialTrajectoryPoint ? initialTrajectoryPoint.ut : ut,
                    "Kerbin");
                LogBackgroundReFlyOrbitSegmentVelocityNonFinite(
                    recordingId,
                    v,
                    hasInitialTrajectoryPoint ? initialTrajectoryPoint.ut : ut,
                    "on-rails-orbit");
                state.hasOpenOrbitSegment = true;

                if (hasTreeRecording)
                {
                    treeRec.ExplicitEndUT = ut;
                }

                ParsekLog.Verbose("BgRecorder", string.Format(CultureInfo.InvariantCulture,
                    "On-rails state initialized (orbiting): pid={0} body={1} sma={2:F1} ecc={3:F4} " +
                    "periAlt={4:F1} apoAlt={5:F1} inc={6:F2}",
                    vesselPid,
                    v.mainBody?.name,
                    v.orbit.semiMajorAxis,
                    v.orbit.eccentricity,
                    v.orbit.PeA,
                    v.orbit.ApA,
                    v.orbit.inclination));
            }
            else
            {
                // No orbit and not landed — edge case (e.g. vessel on launchpad with no orbit)
                state.hasOpenOrbitSegment = false;

                if (hasTreeRecording)
                {
                    treeRec.ExplicitEndUT = ut;
                }

                ParsekLog.Verbose("BgRecorder", $"On-rails state initialized (no orbit): pid={vesselPid}");
            }

            onRailsStates[vesselPid] = state;
        }

        private void InitializeLoadedState(Vessel v, uint vesselPid, string recordingId,
            InheritedEngineState? inherited = null)
        {
            var state = new BackgroundVesselState
            {
                vesselPid = vesselPid,
                recordingId = recordingId
            };
            if (v != null && v.transform != null)
            {
                state.lastWorldRotation = TrajectoryMath.SanitizeQuaternion(v.transform.rotation);
                state.hasLastWorldRotation = true;
            }

            // Cache engine/RCS/robotic modules
            state.cachedEngines = FlightRecorder.CacheEngineModules(v);
            state.cachedRcsModules = FlightRecorder.CacheRcsModules(v);
            state.cachedRoboticModules = FlightRecorder.CacheRoboticModules(v);

            // Seed all tracking sets with current part state (mirrors FlightRecorder.SeedExistingPartStates)
            SeedBackgroundPartStates(v, state);

            MergeInheritedLoadedEngineState(v, vesselPid, recordingId, inherited, state);

            // Emit seed events ONLY if the recording has no part events yet.
            // If a prior active recorder (or earlier background load) already seeded events,
            // re-emitting at the current UT would poison FindLastInterestingUT for boring-tail
            // trimming (bug A / #263 sibling — rover recording not trimmed because of stale
            // DeployableExtended events at UT 2068).
            Recording treeRecForSeed;
            bool hasTreeRecording = TrySeedLoadedPartEvents(v, vesselPid, recordingId, state, out treeRecForSeed);

            // Initialize environment tracking and open first TrackSection
            double ut = Planetarium.GetUniversalTime();
            TrajectoryPoint initialTrajectoryPoint;
            bool hasInitialTrajectoryPoint = TryConsumePendingInitialTrajectoryPoint(vesselPid, out initialTrajectoryPoint);
            if (hasInitialTrajectoryPoint && initialTrajectoryPoint.ut > ut)
                initialTrajectoryPoint.ut = ut;
            SegmentEnvironment initialEnv = ResolveLoadedInitialEnvironment(v, vesselPid, state);
            state.environmentHysteresis = new EnvironmentHysteresis(initialEnv);

            // PR 3b §"Initial seed point coordinate transform": when the recording
            // carries the debris parent-anchor contract, open the first track section
            // as Relative-to-parent and convert the body-fixed seed point into anchor-
            // local metres BEFORE it lands in the section. Without this, the seed has
            // CLAUDE.md "metres-as-degrees" mismatch — its lat/lon/alt are body-fixed
            // degrees but downstream playback in a Relative section interprets them
            // as anchor-local (dx, dy, dz) metres.
            //
            // Resolve the recording: TrySeedLoadedPartEvents already provides
            // `treeRecForSeed` when `hasTreeRecording == true`; fall back to a tree
            // lookup when seeding was skipped (e.g. recording already had part events).
            Recording treeRecForDebris = ResolveDebrisSeedRecording(hasTreeRecording, treeRecForSeed, recordingId);
            bool debrisContractApplies = treeRecForDebris != null
                && !string.IsNullOrEmpty(treeRecForDebris.ParentAnchorRecordingId)
                && hasInitialTrajectoryPoint;

            bool debrisSeedOpened = false;
            if (debrisContractApplies)
            {
                double seedParentDistance = ResolveDebrisParentDistanceMeters(
                    treeRecForDebris,
                    v,
                    out Recording parentRec,
                    out Vessel parentVessel);
                bool seedWithinRelativeRange = IsFinite(seedParentDistance)
                    && seedParentDistance <= DebrisHalfFidelityProximityRangeMeters;
                SeedDebrisProximityTier(state, treeRecForDebris, seedParentDistance);
                if (parentRec == null
                    || parentVessel == null
                    || !parentVessel.loaded
                    || parentVessel.packed
                    || !seedWithinRelativeRange)
                {
                    FallBackToAbsoluteDebrisSeed(
                        v,
                        vesselPid,
                        recordingId,
                        treeRecForDebris,
                        parentRec,
                        parentVessel,
                        seedParentDistance,
                        ut,
                        initialTrajectoryPoint);
                }
                else
                {
                    // Same vesselTransform contract as TryResolveBackgroundAnchorPoseForCandidate:
                    // the seed fallback (used when both queued-parent-seed and
                    // recorded-parent-pose resolution fail) and the live candidate's
                    // stored WorldPos must share the surface-pose frame the parent's
                    // recorded lla lives in. GetWorldPos3D (CoMD) here would re-introduce
                    // a smaller version of the same CoM-vs-vesselTransform mismatch on
                    // the warn-logged failure path.
                    var liveAnchorPose = new AnchorPose(
                        (Vector3d)parentVessel.transform.position,
                        parentVessel.transform.rotation,
                        -1,
                        treeRecForDebris.ParentAnchorRecordingId);

                    var liveCandidate = new RecordingAnchorCandidate(
                        treeRecForDebris.ParentAnchorRecordingId,
                        liveAnchorPose.WorldPos,
                        liveAnchorPose.WorldRotation,
                        AnchorCandidateSource.Live,
                        diagnosticPid: parentVessel.persistentId,
                        ghostIndex: -1,
                        isSealed: false,
                        isSameReplayPoint: false,
                        isSameVesselLineage: false);
                    SetBackgroundCurrentAnchor(state, liveCandidate);

                    AnchorPose seedAnchorPose;
                    string seedAnchorSource;
                    string seedRecordedFallbackFrameContract;
                    ResolveDebrisSeedAnchorPose(
                        vesselPid,
                        recordingId,
                        treeRecForDebris,
                        initialTrajectoryPoint,
                        ut,
                        liveAnchorPose,
                        out seedAnchorPose,
                        out seedAnchorSource,
                        out seedRecordedFallbackFrameContract);

                    StartBackgroundTrackSection(state, initialEnv, ReferenceFrame.Relative,
                        initialTrajectoryPoint.ut);
                    TrajectoryPoint absoluteSeedPoint = initialTrajectoryPoint;
                    bool seedRelativeApplied = ApplyBackgroundRelativeOffsetForAnchorPose(
                        state,
                        ref initialTrajectoryPoint,
                        v,
                        seedAnchorPose,
                        treeRecForDebris.ParentAnchorRecordingId,
                        AnchorCandidateSource.Live,
                        parentVessel.persistentId,
                        logSample: true,
                        sourceLabel: seedAnchorSource);
                    string seedFrameContract = ClassifyBackgroundRelativeFrameContract(
                        treeRecForDebris.ParentAnchorRecordingId,
                        seedAnchorSource,
                        AnchorCandidateSource.Live,
                        hasCurrentAnchorCandidate: string.Equals(seedAnchorSource, "live-warn-fallback", StringComparison.Ordinal),
                        hasPreReFlyAnchorSnapshot: HasActiveReFlyPreReFlyAnchorSnapshot(
                            treeRecForDebris.ParentAnchorRecordingId));
                    string seedParentDrift = FormatActiveReFlyParentDriftFromRecorded(
                        treeRecForDebris.ParentAnchorRecordingId,
                        parentVessel.persistentId,
                        initialTrajectoryPoint.ut);
                    string seedParentDriftField = FormatParentDriftLogField(seedParentDrift);
                    string seedFocusRecordingId = string.IsNullOrEmpty(recordingId) ? "(missing)" : recordingId;
                    LogDebrisSeedRelativeConversionDiagnostics(
                        vesselPid,
                        recordingId,
                        treeRecForDebris.ParentAnchorRecordingId,
                        v,
                        parentVessel,
                        absoluteSeedPoint,
                        initialTrajectoryPoint,
                        seedAnchorPose,
                        seedAnchorSource,
                        seedFrameContract,
                        seedParentDrift,
                        seedFocusRecordingId,
                        seedRecordedFallbackFrameContract,
                        seedRelativeApplied,
                        ut);
                    if (!seedRelativeApplied)
                    {
                        ParsekLog.Warn("BgRecorder",
                            $"InitializeLoadedState: debris seed transform failed, " +
                            $"section opened Relative but offset not applied: " +
                            $"pid={vesselPid} recId={recordingId} parentRecId={treeRecForDebris.ParentAnchorRecordingId}");
                    }
                    ActivateBackgroundHighFidelitySampling(
                        state,
                        initialTrajectoryPoint.ut,
                        "initial-trajectory-point-debris");
                    // v9 surface clearance must reach the very first
                    // body-fixed shadow frame too -- otherwise surface debris
                    // replays its seed at raw recorded altitude while every
                    // subsequent shadow frame gets terrain correction,
                    // producing a one-frame visual hop at terrain transitions.
                    // The in-frames point is anchor-local metres under a
                    // Relative section, so ApplySurfaceClearanceToPoint
                    // correctly gate-skips it (the helper's frame gate
                    // rejects Relative). Mirrors the periodic-sample wiring
                    // at OnBackgroundPhysicsFrame and the structural-event
                    // flush wiring -- those previously got the body-fixed
                    // shadow clearance, but the seed path did not.
                    ApplySurfaceClearanceToPoint(state, v, ref initialTrajectoryPoint);
                    if (seedRelativeApplied)
                    {
                        ApplySurfaceClearanceToBodyFixedShadow(
                            state, v, ref absoluteSeedPoint);
                    }
                    if (hasTreeRecording)
                        ApplyInitialTrajectoryPoint(
                            state, treeRecForSeed, initialTrajectoryPoint, absoluteSeedPoint);
                    else
                        AppendFrameToCurrentTrackSection(
                            state, initialTrajectoryPoint, absoluteSeedPoint);
                    debrisSeedOpened = true;

                    LogDebrisSeedContractApplied(
                        vesselPid,
                        recordingId,
                        treeRecForDebris,
                        parentVessel,
                        seedFrameContract,
                        seedParentDriftField,
                        seedAnchorSource,
                        seedFocusRecordingId,
                        seedRecordedFallbackFrameContract,
                        initialTrajectoryPoint);
                }
            }

            if (!debrisSeedOpened)
            {
                OpenAbsoluteInitialTrackSection(
                    state,
                    initialEnv,
                    hasInitialTrajectoryPoint,
                    initialTrajectoryPoint,
                    ut,
                    hasTreeRecording,
                    treeRecForSeed);
            }

            ParsekLog.Verbose("BgRecorder", $"Loaded state initialized: pid={vesselPid} " +
                $"engines={state.cachedEngines?.Count ?? 0} rcs={state.cachedRcsModules?.Count ?? 0} " +
                $"robotics={state.cachedRoboticModules?.Count ?? 0} initialEnv={initialEnv}");

            loadedStates[vesselPid] = state;
        }

        /// <summary>
        /// The environment a loaded background vessel's first section opens in: a queued
        /// override (consumed) when one is pending, otherwise the live classification.
        /// </summary>
        private SegmentEnvironment ResolveLoadedInitialEnvironment(
            Vessel v,
            uint vesselPid,
            BackgroundVesselState state)
        {
            SegmentEnvironment overrideEnv;
            SegmentEnvironment initialEnv;
            if (TryConsumePendingInitialEnvironmentOverride(vesselPid, out overrideEnv))
            {
                initialEnv = overrideEnv;
                ParsekLog.Verbose("BgRecorder",
                    $"InitializeLoadedState: consumed initial environment override for pid={vesselPid}: {initialEnv}");
            }
            else
            {
                bool hasAtmo = v.mainBody != null && v.mainBody.atmosphere;
                double atmoDepth = hasAtmo ? v.mainBody.atmosphereDepth : 0;
                double approachAlt = (!hasAtmo && v.mainBody != null)
                    ? FlightRecorder.ComputeApproachAltitude(v.mainBody) : 0;
                initialEnv = ClassifyBackgroundEnvironment(
                    hasAtmo, v.altitude, atmoDepth,
                    (int)v.situation, v.srfSpeed, state.cachedEngines, approachAlt,
                    v.isEVA, v.heightFromTerrain,
                    EnvironmentDetector.IsHeightFromTerrainValid(v.heightFromTerrain),
                    v.mainBody != null && v.mainBody.ocean);
            }
            return initialEnv;
        }

        /// <summary>
        /// The recording the debris parent-anchor contract reads: the part-event seed's
        /// recording when seeding found it, else a tree lookup by id.
        /// </summary>
        private Recording ResolveDebrisSeedRecording(
            bool hasTreeRecording,
            Recording treeRecForSeed,
            string recordingId)
        {
            Recording treeRecForDebris = hasTreeRecording ? treeRecForSeed : null;
            if (treeRecForDebris == null && tree?.Recordings != null && !string.IsNullOrEmpty(recordingId))
                tree.Recordings.TryGetValue(recordingId, out treeRecForDebris);
            return treeRecForDebris;
        }

        /// <summary>
        /// Seeds the debris proximity tier from the seed-time parent distance and logs the
        /// transition from no tier.
        /// </summary>
        private static void SeedDebrisProximityTier(
            BackgroundVesselState state,
            Recording treeRecForDebris,
            double seedParentDistance)
        {
            state.debrisProximityDistanceMeters = seedParentDistance;
            state.debrisProximityTier = ProximitySamplingCadence.Resolve(
                seedParentDistance,
                DebrisFullFidelityProximityRangeMeters,
                DebrisHalfFidelityProximityRangeMeters,
                out state.debrisProximityReason);
            LogDebrisProximityTierTransition(
                state.vesselPid,
                treeRecForDebris,
                ProximitySamplingTier.None,
                state.debrisProximityTier,
                seedParentDistance,
                state.debrisProximityReason,
                "InitializeLoadedState");
        }

        /// <summary>
        /// The debris contract cannot open Relative (no loaded, unpacked parent in relative
        /// range): drops the queued parent seed pose and warns with the seed diagnostics.
        /// </summary>
        private void FallBackToAbsoluteDebrisSeed(
            Vessel v,
            uint vesselPid,
            string recordingId,
            Recording treeRecForDebris,
            Recording parentRec,
            Vessel parentVessel,
            double seedParentDistance,
            double ut,
            TrajectoryPoint initialTrajectoryPoint)
        {
            pendingDebrisSeedParentAnchorPoints.Remove(vesselPid);
            ParsekLog.Warn("BgRecorder",
                $"InitializeLoadedState: debris contract uses Absolute seed, " +
                $"falling back to Absolute seed: pid={vesselPid} recId={recordingId} " +
                $"parentRecId={treeRecForDebris.ParentAnchorRecordingId} " +
                $"parentRecResolved={parentRec != null} " +
                $"parentVesselResolved={parentVessel != null} " +
                $"parentVesselLoaded={parentVessel != null && parentVessel.loaded} " +
                $"parentVesselPacked={parentVessel != null && parentVessel.packed} " +
                $"parentDistance={seedParentDistance.ToString("F1", CultureInfo.InvariantCulture)}m " +
                $"relativeEntryRange={DebrisHalfFidelityProximityRangeMeters.ToString("F1", CultureInfo.InvariantCulture)}m " +
                $"initUT={ut.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"seed={DescribeTrajectoryPointForDiagnostics(initialTrajectoryPoint)} " +
                $"{DescribeVesselRootForDiagnostics("child", v)}");
        }

        /// <summary>
        /// The parent pose a debris seed is converted against: the queued split-time parent
        /// pose, else the parent's recorded pose at the seed UT, else the live parent pose
        /// (warned).
        /// </summary>
        private void ResolveDebrisSeedAnchorPose(
            uint vesselPid,
            string recordingId,
            Recording treeRecForDebris,
            TrajectoryPoint initialTrajectoryPoint,
            double ut,
            AnchorPose liveAnchorPose,
            out AnchorPose seedAnchorPose,
            out string seedAnchorSource,
            out string seedRecordedFallbackFrameContract)
        {
            seedAnchorPose = liveAnchorPose;
            seedAnchorSource = "live-warn-fallback";
            string queuedSeedAnchorReason;
            RelativeAnchorResolveFailure recordedSeedAnchorFailure = default;
            seedRecordedFallbackFrameContract = "unresolved";
            if (TryConsumePendingDebrisSeedParentAnchorPose(
                    vesselPid,
                    treeRecForDebris.ParentAnchorRecordingId,
                    initialTrajectoryPoint.ut,
                    out AnchorPose queuedSeedAnchorPose,
                    out queuedSeedAnchorReason))
            {
                seedAnchorPose = queuedSeedAnchorPose;
                seedAnchorSource = "queued-parent-seed";
                ParsekLog.Verbose("BgRecorder",
                    $"InitializeLoadedState: consumed queued debris seed parent anchor: " +
                    $"pid={vesselPid} recId={recordingId} " +
                    $"parentRecId={treeRecForDebris.ParentAnchorRecordingId} " +
                    $"seedUT={initialTrajectoryPoint.ut.ToString("F3", CultureInfo.InvariantCulture)}");
            }
            else if (TryResolveBackgroundRecordedAnchorPose(
                    treeRecForDebris.ParentAnchorRecordingId,
                    initialTrajectoryPoint.ut,
                    out AnchorPose recordedSeedAnchorPose,
                    out recordedSeedAnchorFailure))
            {
                seedAnchorPose = recordedSeedAnchorPose;
                seedAnchorSource = "recorded";
                seedRecordedFallbackFrameContract = ClassifyBackgroundRelativeFrameContract(
                    treeRecForDebris.ParentAnchorRecordingId,
                    seedAnchorSource,
                    AnchorCandidateSource.Ghost,
                    hasCurrentAnchorCandidate: false,
                    hasPreReFlyAnchorSnapshot: HasActiveReFlyPreReFlyAnchorSnapshot(
                        treeRecForDebris.ParentAnchorRecordingId));
            }
            else
            {
                ParsekLog.Warn("BgRecorder",
                    $"InitializeLoadedState: debris seed recorded parent pose unavailable, " +
                    $"falling back to live parent pose: pid={vesselPid} recId={recordingId} " +
                    $"parentRecId={treeRecForDebris.ParentAnchorRecordingId} " +
                    $"reason={RelativeAnchorResolveFailure.ReasonOrFallback(recordedSeedAnchorFailure, "unknown")} " +
                    $"queuedReason={queuedSeedAnchorReason ?? "unknown"} " +
                    $"seedUT={initialTrajectoryPoint.ut.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"initUT={ut.ToString("F3", CultureInfo.InvariantCulture)}");
            }
        }

        /// <summary>The Info line of a debris seed opened Relative to its parent.</summary>
        private static void LogDebrisSeedContractApplied(
            uint vesselPid,
            string recordingId,
            Recording treeRecForDebris,
            Vessel parentVessel,
            string seedFrameContract,
            string seedParentDriftField,
            string seedAnchorSource,
            string seedFocusRecordingId,
            string seedRecordedFallbackFrameContract,
            TrajectoryPoint initialTrajectoryPoint)
        {
            ParsekLog.Info("BgRecorder",
                $"Debris parent-anchor contract applied at seed: pid={vesselPid} " +
                $"recId={recordingId} parentRecId={treeRecForDebris.ParentAnchorRecordingId} " +
                $"parentPid={parentVessel.persistentId} " +
                $"frameContract={seedFrameContract} " +
                seedParentDriftField +
                $"seedAnchorSource={seedAnchorSource} seedFocusRecordingId={seedFocusRecordingId} " +
                $"seedRecordedFallbackFrameContract={seedRecordedFallbackFrameContract} " +
                $"seedUT={initialTrajectoryPoint.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"|offset|=(dx={initialTrajectoryPoint.latitude:F2},dy={initialTrajectoryPoint.longitude:F2},dz={initialTrajectoryPoint.altitude:F2})");
        }

        /// <summary>
        /// Opens the first section Absolute (no debris Relative seed): at the seed UT when a seed
        /// is pending (which also opens the high-fidelity window and lands the seed), else now.
        /// </summary>
        private void OpenAbsoluteInitialTrackSection(
            BackgroundVesselState state,
            SegmentEnvironment initialEnv,
            bool hasInitialTrajectoryPoint,
            TrajectoryPoint initialTrajectoryPoint,
            double ut,
            bool hasTreeRecording,
            Recording treeRecForSeed)
        {
            StartBackgroundTrackSection(state, initialEnv, ReferenceFrame.Absolute,
                hasInitialTrajectoryPoint ? initialTrajectoryPoint.ut : ut);
            if (hasInitialTrajectoryPoint)
            {
                ActivateBackgroundHighFidelitySampling(
                    state,
                    initialTrajectoryPoint.ut,
                    "initial-trajectory-point");
                if (hasTreeRecording)
                    ApplyInitialTrajectoryPoint(state, treeRecForSeed, initialTrajectoryPoint);
                else
                    AppendFrameToCurrentTrackSection(state, initialTrajectoryPoint);
            }
        }

        private void MergeInheritedLoadedEngineState(
            Vessel v,
            uint vesselPid,
            string recordingId,
            InheritedEngineState? inherited,
            BackgroundVesselState state)
        {
            // Bug #298: merge inherited parent engine/RCS state for child debris.
            // SeedEngines checks engine.isOperational which is false after fuel severance,
            // so child debris recordings get zero engine seed events. Merge from the parent's
            // known-good state to fill in the gaps.
            if (inherited.HasValue)
            {
                var inh = inherited.Value;
                ParsekLog.Verbose("BgRecorder",
                    $"InitializeLoadedState: inherited state for pid={vesselPid}: " +
                    $"engines={inh.activeEngineKeys?.Count ?? 0} rcs={inh.activeRcsKeys?.Count ?? 0} " +
                    $"childParts={v.parts?.Count ?? 0}");

                var childPartPids = new HashSet<uint>();
                if (v.parts != null)
                    for (int i = 0; i < v.parts.Count; i++)
                        if (v.parts[i] != null)
                            childPartPids.Add(v.parts[i].persistentId);

                int merged = MergeInheritedEngineState(inherited,
                    state.activeEngineKeys, state.lastThrottle,
                    state.activeRcsKeys, state.lastRcsThrottle,
                    childPartPids, state.allEngineKeys);
                if (merged > 0)
                    ParsekLog.Info("BgRecorder",
                        $"Merged {merged} inherited engine/RCS key(s) for pid={vesselPid} recId={recordingId} (#298)");
                else
                    ParsekLog.Verbose("BgRecorder",
                        $"InitializeLoadedState: 0 inherited keys merged for pid={vesselPid} " +
                        $"(no matching PIDs on child, or all already seeded)");
            }
            else
            {
                ParsekLog.Verbose("BgRecorder",
                    $"InitializeLoadedState: no inherited state for pid={vesselPid} (inherited=null)");
            }
        }

        private bool TrySeedLoadedPartEvents(
            Vessel v,
            uint vesselPid,
            string recordingId,
            BackgroundVesselState state,
            out Recording treeRecForSeed)
        {
            bool hasTreeRecording = tree.Recordings.TryGetValue(recordingId, out treeRecForSeed);
            if (!hasTreeRecording)
            {
                // Tree recording not found — unexpected state. The caller supplied a recordingId
                // that doesn't exist in the tree's Recordings dict, which means a lifecycle bug
                // somewhere upstream (tree corrupted, recording deleted without updating the
                // background map, race between StopRecording and InitializeLoadedState, etc.).
                // Log loud so we notice; seeding is skipped because there's no target anyway.
                ParsekLog.Warn("BgRecorder",
                    $"InitializeLoadedState: tree recording '{recordingId}' not found for " +
                    $"pid={vesselPid} — skipping seed events (upstream lifecycle bug)");
            }
            else if (treeRecForSeed.PartEvents.Count > 0)
            {
                // M6 half two (consume side). A recording that already has events used to mean
                // "seeding would poison FindLastInterestingUT, write nothing" — which was right for
                // the seed, and wrong for a rails round trip: SeedBackgroundPartStates had just
                // re-synced every tracking set to live truth, so any change that happened across
                // the warp was ERASED rather than deferred. If this vessel packed onto rails and is
                // now unpacking, reconcile against the state it carried in.
                if (!TryEmitRailsSpanDiff(v, vesselPid, recordingId, state, treeRecForSeed))
                {
                    ParsekLog.Verbose("BgRecorder",
                        $"InitializeLoadedState: skipping seed events for pid={vesselPid} recId={recordingId} " +
                        $"— recording already has {treeRecForSeed.PartEvents.Count} part event(s)");
                }
            }
            else
            {
                // A full seed supersedes any stale rails-span snapshot: an empty PartEvents list
                // means this is not the same continuous recording the snapshot was taken against.
                railsSpanPartStates.Remove(vesselPid);

                var partNamesByPid = new Dictionary<uint, string>();
                if (v.parts != null)
                {
                    for (int i = 0; i < v.parts.Count; i++)
                    {
                        Part p = v.parts[i];
                        if (p != null && !partNamesByPid.ContainsKey(p.persistentId))
                            partNamesByPid[p.persistentId] = p.partInfo?.name ?? "unknown";
                    }
                }
                double seedUT = Planetarium.GetUniversalTime();
                var seedSets = BuildPartTrackingSetsFromState(state);
                var seedEvents = PartStateSeeder.EmitSeedEvents(seedSets, partNamesByPid, seedUT, "BgRecorder");
                if (seedEvents.Count > 0)
                {
                    treeRecForSeed.PartEvents.AddRange(seedEvents);
                    // If the very next event is an OnSave (e.g., the player
                    // F5s immediately after a background vessel enters loaded
                    // physics), the seed events would otherwise be lost because
                    // nothing else marks dirty until the next poll emits an event.
                    treeRecForSeed.MarkFilesDirty();
                }
            }

            return hasTreeRecording;
        }

        /// <summary>
        /// M6 half two: reconciles a re-entering BG vessel's freshly seeded tracking state against
        /// the snapshot taken when it packed onto rails, appending only the events that actually
        /// changed across the span. Returns false (writing nothing) when no rails-span snapshot is
        /// pending for this pid, which is every path other than a loaded→rails→loaded round trip.
        ///
        /// <para>
        /// The snapshot is consumed either way: a stale one must not survive to be diffed against a
        /// later, unrelated re-entry.
        /// </para>
        /// </summary>
        private bool TryEmitRailsSpanDiff(
            Vessel v, uint vesselPid, string recordingId,
            BackgroundVesselState state, Recording treeRec)
        {
            PartTrackingSets preRails;
            if (!railsSpanPartStates.TryGetValue(vesselPid, out preRails) || preRails == null)
                return false;

            railsSpanPartStates.Remove(vesselPid);

            var partNamesByPid = new Dictionary<uint, string>();
            if (v?.parts != null)
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p != null && !partNamesByPid.ContainsKey(p.persistentId))
                        partNamesByPid[p.persistentId] = p.partInfo?.name ?? "unknown";
                }
            }

            double diffUT = Planetarium.GetUniversalTime();
            var diffEvents = PartStateSeeder.EmitDiffEvents(
                preRails, BuildPartTrackingSetsFromState(state), partNamesByPid, diffUT, "BgRecorder");

            if (diffEvents.Count > 0)
            {
                treeRec.PartEvents.AddRange(diffEvents);
                treeRec.MarkFilesDirty();
            }

            ParsekLog.Info("BgRecorder",
                $"Rails-span reconcile for pid={vesselPid} rec={recordingId}: " +
                $"{diffEvents.Count} event(s) at UT={diffUT.ToString("F1", CultureInfo.InvariantCulture)}");
            return true;
        }

        /// <summary>
        /// Builds a PartTrackingSets struct from a BackgroundVesselState's tracking fields.
        /// Used by both InitializeLoadedState (seed events) and SeedBackgroundPartStates (seed state).
        /// </summary>
        private static PartTrackingSets BuildPartTrackingSetsFromState(BackgroundVesselState state)
        {
            return new PartTrackingSets
            {
                deployedFairings = state.deployedFairings,
                jettisonedShrouds = state.jettisonedShrouds,
                parachuteStates = state.parachuteStates,
                extendedDeployables = state.extendedDeployables,
                brokenDeployables = state.brokenDeployables,
                activeConverterParts = state.activeConverterParts,
                damagedWheelParts = state.damagedWheelParts,
                jetpackDeployedParts = state.jetpackDeployedParts,
                ragdollParts = state.ragdollParts,
                lightsOn = state.lightsOn,
                blinkingLights = state.blinkingLights,
                lightBlinkRates = state.lightBlinkRates,
                deployedGear = state.deployedGear,
                openCargoBays = state.openCargoBays,
                deployedLadders = state.deployedLadders,
                deployedAnimationGroups = state.deployedAnimationGroups,
                deployedAnimateGenericModules = state.deployedAnimateGenericModules,
                deployedAeroSurfaceModules = state.deployedAeroSurfaceModules,
                deployedControlSurfaceModules = state.deployedControlSurfaceModules,
                deployedRobotArmScannerModules = state.deployedRobotArmScannerModules,
                animateHeatLevels = state.animateHeatLevels,
                activeEngineKeys = state.activeEngineKeys,
                allEngineKeys = state.allEngineKeys,
                lastThrottle = state.lastThrottle,
                activeRcsKeys = state.activeRcsKeys,
                lastRcsThrottle = state.lastRcsThrottle,
            };
        }

        /// <summary>
        /// Pre-populates a BackgroundVesselState's tracking sets with the current
        /// state of every part on the vessel. Delegates to shared PartStateSeeder.
        /// </summary>
        private static void SeedBackgroundPartStates(Vessel v, BackgroundVesselState state)
        {
            PartStateSeeder.SeedPartStates(v,
                BuildPartTrackingSetsFromState(state),
                state.cachedEngines, state.cachedRcsModules,
                seedColorChangerLights: false, logTag: "BgRecorder");
        }

        /// <summary>
        /// Creates an InheritedEngineState snapshot from a BackgroundVesselState.
        /// Returns null if no engines or RCS are active. Used in HandleBackgroundVesselSplit
        /// to capture parent state before CloseParentRecording destroys loadedStates.
        /// Defined here (not on the struct) because BackgroundVesselState is a private inner class.
        /// </summary>
        private static InheritedEngineState? InheritedStateFromBackgroundVessel(BackgroundVesselState state)
        {
            if (state == null) return null;
            bool hasEngines = state.activeEngineKeys != null && state.activeEngineKeys.Count > 0;
            bool hasRcs = state.activeRcsKeys != null && state.activeRcsKeys.Count > 0;
            if (!hasEngines && !hasRcs) return null;

            return new InheritedEngineState
            {
                activeEngineKeys = hasEngines ? new HashSet<ulong>(state.activeEngineKeys) : null,
                engineThrottles = hasEngines && state.lastThrottle != null
                    ? new Dictionary<ulong, float>(state.lastThrottle) : null,
                activeRcsKeys = hasRcs ? new HashSet<ulong>(state.activeRcsKeys) : null,
                rcsThrottles = hasRcs && state.lastRcsThrottle != null
                    ? new Dictionary<ulong, float>(state.lastRcsThrottle) : null
            };
        }

        /// <summary>
        /// Merges inherited engine/RCS state from a parent vessel into a child's
        /// tracking collections. Only keys whose decoded PID matches a part on the
        /// child vessel are merged. Upgrades throttle if the inherited value is higher
        /// than the live-seeded value — SeedEngines may find throttle=0 (KSP timing)
        /// while the parent had the engine at full power before breakup.
        /// Pure static method for testability. Bug #298.
        /// </summary>
        internal static int MergeInheritedEngineState(
            InheritedEngineState? inherited,
            HashSet<ulong> targetActiveEngineKeys,
            Dictionary<ulong, float> targetLastThrottle,
            HashSet<ulong> targetActiveRcsKeys,
            Dictionary<ulong, float> targetLastRcsThrottle,
            HashSet<uint> childPartPids,
            HashSet<ulong> allEngineKeys = null)
        {
            if (!inherited.HasValue) return 0;
            var inh = inherited.Value;
            int merged = 0;
            int skippedNonOperational = 0;

            if (inh.activeEngineKeys != null)
            {
                foreach (ulong key in inh.activeEngineKeys)
                {
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(key, out pid, out midx);
                    if (!childPartPids.Contains(pid)) continue; // engine not on this child vessel

                    float inhThrottle = 1f;
                    if (inh.engineThrottles != null)
                    {
                        float t;
                        if (inh.engineThrottles.TryGetValue(key, out t))
                            inhThrottle = t;
                    }

                    if (targetActiveEngineKeys.Contains(key))
                    {
                        // Already seeded by SeedEngines — upgrade throttle if inherited is higher.
                        // SeedEngines may find throttle=0 (KSP hasn't propagated throttle to
                        // debris yet) while the parent had the engine at full power.
                        float existing = 0f;
                        targetLastThrottle.TryGetValue(key, out existing);
                        if (inhThrottle > existing)
                        {
                            targetLastThrottle[key] = inhThrottle;
                            merged++;
                            ParsekLog.Verbose("BgRecorder",
                                $"Inherited engine throttle upgraded: pid={pid} midx={midx} " +
                                $"{existing:F2}→{inhThrottle:F2} (#298)");
                        }
                        continue;
                    }

                    // T58: SeedEngines adds ALL engines to allEngineKeys (operational or not).
                    // If the key is in allEngineKeys but NOT in activeEngineKeys, SeedEngines
                    // found the engine but it's non-operational (fuel severed, flameout after
                    // staging). Don't override with inherited state — the child's assessment
                    // is correct. Only inherit when the engine wasn't found at all (timing).
                    if (allEngineKeys != null && allEngineKeys.Contains(key))
                    {
                        skippedNonOperational++;
                        ParsekLog.Verbose("BgRecorder",
                            $"Inherited engine skipped (non-operational on child): pid={pid} midx={midx} " +
                            $"inhThrottle={inhThrottle:F2} (T58)");
                        continue;
                    }

                    targetActiveEngineKeys.Add(key);
                    targetLastThrottle[key] = inhThrottle;
                    merged++;
                    ParsekLog.Verbose("BgRecorder",
                        $"Inherited engine key merged: pid={pid} midx={midx} throttle={inhThrottle:F2} (#298)");
                }
            }

            if (skippedNonOperational > 0)
                ParsekLog.Info("BgRecorder",
                    $"Skipped {skippedNonOperational} inherited engine(s) — non-operational on child (T58)");

            if (inh.activeRcsKeys != null)
            {
                foreach (ulong key in inh.activeRcsKeys)
                {
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(key, out pid, out midx);
                    if (!childPartPids.Contains(pid)) continue;

                    float inhThrottle = 1f;
                    if (inh.rcsThrottles != null)
                    {
                        float t;
                        if (inh.rcsThrottles.TryGetValue(key, out t))
                            inhThrottle = t;
                    }

                    if (targetActiveRcsKeys.Contains(key))
                    {
                        float existing = 0f;
                        targetLastRcsThrottle.TryGetValue(key, out existing);
                        if (inhThrottle > existing)
                        {
                            targetLastRcsThrottle[key] = inhThrottle;
                            merged++;
                            ParsekLog.Verbose("BgRecorder",
                                $"Inherited RCS throttle upgraded: pid={pid} midx={midx} " +
                                $"{existing:F2}→{inhThrottle:F2} (#298)");
                        }
                        continue;
                    }

                    targetActiveRcsKeys.Add(key);
                    targetLastRcsThrottle[key] = inhThrottle;
                    merged++;
                    ParsekLog.Verbose("BgRecorder",
                        $"Inherited RCS key merged: pid={pid} midx={midx} throttle={inhThrottle:F2} (#298)");
                }
            }

            return merged;
        }
    }
}
