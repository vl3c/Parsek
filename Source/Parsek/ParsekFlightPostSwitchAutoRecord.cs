using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // The post-switch auto-record watch: after a switch to an idle vessel, arms a watch, captures a
    // baseline, evaluates each physics frame whether the player changed the vessel (engines, RCS,
    // attitude, orbit, landed motion, manifest, part state) and starts the recording when one did;
    // also the deferred EVA auto-record. The watch state stays on ParsekFlight (its physics-frame
    // patch and the in-game tests read it); ParsekFlight forwards OnPostSwitchAutoRecordPhysicsFrame.
    internal sealed class ParsekFlightPostSwitchAutoRecord
    {
        private readonly ParsekFlight host;

        internal ParsekFlightPostSwitchAutoRecord(ParsekFlight host)
        {
            this.host = host;
        }

        private const double PostSwitchLandedMotionDistanceThreshold = 0.5;

        private const double PostSwitchLandedMotionSpeedThreshold = 0.5;

        private const double PostSwitchOrbitSemiMajorAxisThresholdMeters = 5.0;

        private const double PostSwitchOrbitEccentricityThreshold = 1e-6;

        private const double PostSwitchOrbitAngleThresholdDegrees = 0.01;

        private const float PostSwitchAttitudeChangeThresholdDegrees = 3f;

        private const double PostSwitchAttitudeChangeDebounceSeconds = 0.2;

        private const double PostSwitchResourceDeltaEpsilon = 0.01;

        private const double PostSwitchManifestEvaluationIntervalSeconds = 0.25;

        internal const int PostSwitchManifestDeltaSkipped = -1;

        internal void ClearPendingEvaAutoRecordState()
        {
            host.pendingAutoRecord = false;
        }

        internal static ParsekFlight.AutoRecordLaunchDecision EvaluateAutoRecordLaunchDecision(
            bool isRecording,
            bool isActiveVessel,
            Vessel.Situations fromSituation,
            bool autoRecordOnLaunchEnabled,
            double lastLandedUt,
            double currentUt,
            double landedSettleThreshold,
            bool suppressForTimeJumpTransient)
        {
            if (isRecording)
                return ParsekFlight.AutoRecordLaunchDecision.SkipAlreadyRecording;

            // Time-jump transient suppression is checked before the active-vessel guard
            // so the skip decision (and its INFO log) fires for any vessel whose situation
            // changes during the jump window — including synthetic spawn vessels that the
            // playback policy materializes during a Real Spawn Control / Timeline FF jump.
            // The original #526 bug was an active-vessel `PRELAUNCH -> FLYING` flicker, but
            // labelling non-active flickers as `SkipInactiveVessel` hid the time-jump origin
            // and made the in-game canaries unable to confirm the suppression branch fired.
            if (suppressForTimeJumpTransient)
                return ParsekFlight.AutoRecordLaunchDecision.SkipTimeJumpTransient;

            if (!isActiveVessel)
                return ParsekFlight.AutoRecordLaunchDecision.SkipInactiveVessel;

            if (fromSituation == Vessel.Situations.PRELAUNCH)
            {
                return autoRecordOnLaunchEnabled
                    ? ParsekFlight.AutoRecordLaunchDecision.StartFromPrelaunch
                    : ParsekFlight.AutoRecordLaunchDecision.SkipDisabled;
            }

            if (fromSituation == Vessel.Situations.LANDED)
            {
                double settledTime = lastLandedUt >= 0
                    ? currentUt - lastLandedUt
                    : 0;
                if (settledTime < landedSettleThreshold)
                    return ParsekFlight.AutoRecordLaunchDecision.SkipBounce;

                return autoRecordOnLaunchEnabled
                    ? ParsekFlight.AutoRecordLaunchDecision.StartFromSettledLanded
                    : ParsekFlight.AutoRecordLaunchDecision.SkipDisabled;
            }

            return ParsekFlight.AutoRecordLaunchDecision.SkipNotLaunchTransition;
        }

        // Decision for the first-staging-on-pad auto-record trigger (OnStageActivate).
        // Mirrors EvaluateAutoRecordLaunchDecision's guard ordering: an already-running
        // recorder short-circuits first, then the time-jump transient suppression, so a
        // synthetic spawn vessel staging during an FF / warp-to-time window cannot
        // spuriously start a recording. Only fires while the active vessel is PRELAUNCH
        // (still on the pad); in-flight staging is left to the existing recorder, and the
        // normal clamp-release launch is still covered by the PRELAUNCH->FLYING trigger
        // (whichever fires first wins via the isRecording guard).
        internal static ParsekFlight.AutoRecordStagingDecision EvaluateAutoRecordStagingDecision(
            bool isRecording,
            bool hasActiveVessel,
            bool activeVesselIsPrelaunch,
            bool autoRecordOnLaunchEnabled,
            bool suppressForTimeJumpTransient)
        {
            if (isRecording)
                return ParsekFlight.AutoRecordStagingDecision.SkipAlreadyRecording;

            if (suppressForTimeJumpTransient)
                return ParsekFlight.AutoRecordStagingDecision.SkipTimeJumpTransient;

            if (!hasActiveVessel)
                return ParsekFlight.AutoRecordStagingDecision.SkipNoActiveVessel;

            if (!activeVesselIsPrelaunch)
                return ParsekFlight.AutoRecordStagingDecision.SkipNotOnPad;

            if (!autoRecordOnLaunchEnabled)
                return ParsekFlight.AutoRecordStagingDecision.SkipDisabled;

            return ParsekFlight.AutoRecordStagingDecision.StartFromPrelaunchStaging;
        }

        internal static bool ShouldQueueAutoRecordOnEva(
            bool hasSourceVessel,
            bool autoRecordOnEvaEnabled)
        {
            return hasSourceVessel && autoRecordOnEvaEnabled;
        }

        internal static bool ShouldStartDeferredAutoRecordEva(
            bool pendingAutoRecord,
            bool isRecording,
            bool hasActiveVessel,
            bool activeVesselIsEva)
        {
            return pendingAutoRecord && !isRecording && hasActiveVessel && activeVesselIsEva;
        }

        internal static bool ShouldArmPostSwitchAutoRecord(
            bool autoRecordOnFirstModificationAfterSwitchEnabled,
            bool isRecording,
            bool hasNewVessel,
            bool newVesselIsGhost)
        {
            return autoRecordOnFirstModificationAfterSwitchEnabled
                && !isRecording
                && hasNewVessel
                && !newVesselIsGhost;
        }

        internal static ParsekFlight.PostSwitchAutoRecordArmDecision EvaluatePostSwitchAutoRecordArmDecision(
            bool shouldArm,
            bool trackedInActiveTree,
            bool newVesselIsEva)
        {
            if (!shouldArm)
                return ParsekFlight.PostSwitchAutoRecordArmDecision.None;

            // A tracked background member is an existing tree recording: switching back
            // to it should promote it to foreground on the first modification, whether
            // or not it is an EVA kerbal. (An EVA kerbal re-controlled after a vessel
            // switch lives here; without this it stays background-tracked and the
            // missed-vessel-switch recovery loops every frame, never promoting.)
            if (trackedInActiveTree)
                return ParsekFlight.PostSwitchAutoRecordArmDecision.ArmTrackedBackgroundMember;

            // Outsider (not in any tree): a fresh EVA kerbal is owned by the dedicated
            // OnCrewOnEva / EVA-branch + "Auto-record on EVA" path, so do not also arm
            // an outsider standalone recording for it. Non-EVA outsiders still arm.
            return newVesselIsEva
                ? ParsekFlight.PostSwitchAutoRecordArmDecision.None
                : ParsekFlight.PostSwitchAutoRecordArmDecision.ArmOutsider;
        }

        internal static ParsekFlight.PostSwitchAutoRecordSuppressionReason EvaluatePostSwitchAutoRecordSuppression(
            bool autoRecordOnFirstModificationAfterSwitchEnabled,
            bool isRecording,
            bool hasActiveVessel,
            uint activeVesselPid,
            uint armedVesselPid,
            bool activeVesselIsGhost,
            bool isRestoringActiveTree,
            bool hasPendingTransition,
            bool isWarpActive,
            bool activeVesselPacked)
        {
            if (!autoRecordOnFirstModificationAfterSwitchEnabled)
                return ParsekFlight.PostSwitchAutoRecordSuppressionReason.Disabled;
            if (isRecording)
                return ParsekFlight.PostSwitchAutoRecordSuppressionReason.AlreadyRecording;
            if (!hasActiveVessel)
                return ParsekFlight.PostSwitchAutoRecordSuppressionReason.NoActiveVessel;
            if (activeVesselPid == 0 || activeVesselPid != armedVesselPid)
                return ParsekFlight.PostSwitchAutoRecordSuppressionReason.ActiveVesselMismatch;
            if (activeVesselIsGhost)
                return ParsekFlight.PostSwitchAutoRecordSuppressionReason.GhostMapVessel;
            if (isRestoringActiveTree)
                return ParsekFlight.PostSwitchAutoRecordSuppressionReason.RestoreInProgress;
            if (hasPendingTransition)
                return ParsekFlight.PostSwitchAutoRecordSuppressionReason.PendingTransition;
            if (isWarpActive)
                return ParsekFlight.PostSwitchAutoRecordSuppressionReason.WarpActive;
            if (activeVesselPacked)
                return ParsekFlight.PostSwitchAutoRecordSuppressionReason.PackedOrOnRails;
            return ParsekFlight.PostSwitchAutoRecordSuppressionReason.None;
        }

        /// <summary>
        /// The post-switch module-cache INVALIDATION signal: the caches are stale when a
        /// module edit marked them dirty, or when the vessel part count no longer matches
        /// the count the caches were built from (undock / decouple / EVA). Extracted from
        /// <c>EvaluatePostSwitchAutoRecordTrigger</c> so the derivation itself is testable
        /// without a live Vessel; the call site is byte-identical to the inline expression.
        /// </summary>
        internal static bool NeedsPostSwitchModuleCacheRefresh(
            bool moduleCachesDirty,
            int cachedPartCount,
            int currentPartCount)
        {
            return moduleCachesDirty || cachedPartCount != currentPartCount;
        }

        internal static bool ShouldEvaluatePostSwitchManifestDiff(
            double currentUT,
            double nextManifestEvaluationUt,
            bool needsCacheRefresh)
        {
            return needsCacheRefresh || currentUT >= nextManifestEvaluationUt;
        }

        internal static string BuildPostSwitchAutoRecordDecisionStateKey(
            ParsekFlight.PostSwitchAutoRecordSuppressionReason suppression,
            bool baselineCaptured,
            bool waitingForSettle,
            ParsekFlight.PostSwitchAutoRecordTrigger trigger)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "suppression={0}|baseline={1}|settle={2}|trigger={3}",
                suppression,
                baselineCaptured ? 1 : 0,
                waitingForSettle ? 1 : 0,
                trigger);
        }

        internal static string BuildPostSwitchManifestDeltaStateKey(
            int crewDeltaKeys,
            int resourceDeltaKeys,
            int inventoryDeltaKeys,
            int partStateTokenDelta,
            bool crewChanged,
            bool resourceChanged,
            bool partStateChanged)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "crew={0}|resource={1}|inventory={2}|partState={3}|crewChanged={4}|resourceChanged={5}|partStateChanged={6}",
                FormatPostSwitchManifestDeltaCount(crewDeltaKeys),
                FormatPostSwitchManifestDeltaCount(resourceDeltaKeys),
                FormatPostSwitchManifestDeltaCount(inventoryDeltaKeys),
                FormatPostSwitchManifestDeltaCount(partStateTokenDelta),
                crewChanged ? 1 : 0,
                resourceChanged ? 1 : 0,
                partStateChanged ? 1 : 0);
        }

        private static string FormatPostSwitchManifestDeltaCount(int count)
        {
            return count == PostSwitchManifestDeltaSkipped
                ? "skipped"
                : count.ToString(CultureInfo.InvariantCulture);
        }

        internal static string FormatPostSwitchAutoRecordDecisionSummary(
            uint armedPid,
            uint activePid,
            bool baselineCaptured,
            ParsekFlight.PostSwitchAutoRecordSuppressionReason suppression,
            bool waitingForSettle,
            double comparisonsReadyUt,
            double currentUt,
            ParsekFlight.PostSwitchAutoRecordTrigger trigger)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Post-switch watch decision: pid={0} activePid={1} baseline={2} suppression={3} waitingForSettle={4} readyAt={5:F1} now={6:F1} trigger={7}",
                armedPid,
                activePid,
                baselineCaptured,
                suppression,
                waitingForSettle,
                comparisonsReadyUt,
                currentUt,
                trigger);
        }

        private static void LogPostSwitchAutoRecordDecision(
            ParsekFlight.PostSwitchAutoRecordState state,
            uint activePid,
            ParsekFlight.PostSwitchAutoRecordSuppressionReason suppression,
            bool waitingForSettle,
            double currentUt,
            ParsekFlight.PostSwitchAutoRecordTrigger trigger)
        {
            if (state == null)
                return;

            string stateKey = BuildPostSwitchAutoRecordDecisionStateKey(
                suppression,
                state.BaselineCaptured,
                waitingForSettle,
                trigger);
            ParsekLog.VerboseOnChange("Flight",
                "post-switch-watch-" + state.VesselPid.ToString(CultureInfo.InvariantCulture),
                stateKey,
                FormatPostSwitchAutoRecordDecisionSummary(
                    state.VesselPid,
                    activePid,
                    state.BaselineCaptured,
                    suppression,
                    waitingForSettle,
                    state.ComparisonsReadyUt,
                    currentUt,
                    trigger));
        }

        private bool HasPendingPostSwitchAutoRecordTransition()
        {
            return host.pendingTreeDockMerge
                || host.pendingSplitRecorder != null
                || host.pendingSplitInProgress
                || host.pendingBoardingTargetPid != 0
                || host.pendingDockMergedPid != 0
                || (host.recorder != null && host.recorder.ChainToVesselPending);
        }

        internal void ArmPostSwitchAutoRecord(
            Vessel vessel,
            string armReason,
            bool trackedInActiveTree,
            string freshStartParentRecordingId)
        {
            if (vessel == null)
                return;

            host.postSwitchAutoRecord = new ParsekFlight.PostSwitchAutoRecordState
            {
                VesselPid = vessel.persistentId,
                VesselName = vessel.vesselName ?? "Unknown",
                ArmedAtUt = Planetarium.GetUniversalTime(),
                ArmedReason = armReason,
                TrackedInActiveTree = trackedInActiveTree,
                FreshStartParentRecordingId = freshStartParentRecordingId
            };

            ParsekLog.Info("Flight",
                $"Post-switch auto-record armed: vessel='{host.postSwitchAutoRecord.VesselName}' " +
                $"pid={host.postSwitchAutoRecord.VesselPid} tracked={trackedInActiveTree} " +
                $"reason={armReason}");
        }

        internal void DisarmPostSwitchAutoRecord(string reason)
        {
            if (host.postSwitchAutoRecord == null)
                return;

            ParsekLog.Info("Flight",
                $"Post-switch auto-record disarmed: vessel='{host.postSwitchAutoRecord.VesselName}' " +
                $"pid={host.postSwitchAutoRecord.VesselPid} reason={reason}");
            host.postSwitchAutoRecord = null;
        }

        // ---------------------------------------------------------------------
        // End Phase C: stock-action intent consume.
        // ---------------------------------------------------------------------

        private static double NormalizeAngleDeltaDegrees(double a, double b)
        {
            double delta = Math.Abs(a - b) % 360.0;
            return delta > 180.0 ? 360.0 - delta : delta;
        }

        private static ParsekFlight.PostSwitchOrbitSnapshot CapturePostSwitchOrbitSnapshot(Vessel v)
        {
            if (v == null || v.orbit == null || v.mainBody == null)
                return default;

            return new ParsekFlight.PostSwitchOrbitSnapshot
            {
                IsValid = true,
                BodyName = v.mainBody.name,
                SemiMajorAxis = v.orbit.semiMajorAxis,
                Eccentricity = v.orbit.eccentricity,
                Inclination = v.orbit.inclination,
                LongitudeOfAscendingNode = v.orbit.LAN,
                ArgumentOfPeriapsis = v.orbit.argumentOfPeriapsis
            };
        }

        private static void RefreshPostSwitchAutoRecordModuleCaches(
            ParsekFlight.PostSwitchAutoRecordState state,
            Vessel v)
        {
            if (state == null || v == null)
                return;

            state.CachedPartCount = v.parts != null ? v.parts.Count : 0;

            state.CachedEngines = FlightRecorder.CacheEngineModules(v);
            state.ActiveEngineKeys.Clear();
            state.EngineThrottleMap.Clear();

            for (int i = 0; i < state.CachedEngines.Count; i++)
            {
                var (part, engine, moduleIndex) = state.CachedEngines[i];
                if (part == null || engine == null)
                    continue;

                if (!engine.EngineIgnited || !engine.isOperational)
                    continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                state.ActiveEngineKeys.Add(key);
                state.EngineThrottleMap[key] = engine.currentThrottle;
            }

            state.CachedRcsModules = FlightRecorder.CacheRcsModules(v);
            state.RcsFrameCountMap.Clear();
            state.ActiveRcsKeys.Clear();
            state.RcsThrottleMap.Clear();

            for (int i = 0; i < state.CachedRcsModules.Count; i++)
            {
                var (part, rcs, moduleIndex) = state.CachedRcsModules[i];
                if (part == null || rcs == null)
                    continue;

                if (!rcs.rcs_active || !rcs.rcsEnabled)
                    continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                state.RcsFrameCountMap[key] = FlightRecorder.RcsDebounceFrameThreshold;
                state.ActiveRcsKeys.Add(key);
                state.RcsThrottleMap[key] =
                    FlightRecorder.ComputeRcsPower(rcs.thrustForces, rcs.thrusterPower);
            }

            state.ModuleCachesDirty = false;
        }

        private bool CapturePostSwitchAutoRecordBaseline(
            ParsekFlight.PostSwitchAutoRecordState state,
            Vessel v,
            double currentUT)
        {
            if (state == null || v == null)
                return false;

            ConfigNode vesselSnapshot = FlightRecorder.TryBackupSnapshot(v);
            state.BaselineCaptured = true;
            state.BaselineCapturedUt = currentUT;
            state.ComparisonsReadyUt =
                v.situation == Vessel.Situations.LANDED || v.situation == Vessel.Situations.SPLASHED
                    ? currentUT + ParsekFlight.LandedSettleThreshold
                    : currentUT;
            state.NextManifestEvaluationUt = currentUT;
            state.BaselineWorldPosition = v.GetWorldPos3D();
            state.BaselineWorldRotation = TrajectoryMath.SanitizeQuaternion(v.transform.rotation);
            state.BaselineSeedPoint = FlightRecorder.BuildTrajectoryPoint(
                v,
                FlightRecorder.SampleCurrentVelocity(v),
                currentUT);
            state.AttitudeThresholdExceededAtUt = double.NaN;
            state.BaselineOrbit = CapturePostSwitchOrbitSnapshot(v);
            state.BaselineResources = VesselSnapshotOps.ExtractResourceManifest(vesselSnapshot);
            state.BaselineInventory = VesselSnapshotOps.ExtractInventoryManifest(vesselSnapshot, out _);
            state.BaselineCrew = VesselSpawner.ExtractCrewManifest(vesselSnapshot);
            state.BaselinePartStateTokens = CapturePostSwitchPartStateTokens(v);
            RefreshPostSwitchAutoRecordModuleCaches(state, v);

            ParsekLog.Info("Flight",
                $"Post-switch baseline captured: vessel='{state.VesselName}' pid={state.VesselPid} " +
                $"tracked={state.TrackedInActiveTree} settleUntil={state.ComparisonsReadyUt:F1}");
            return true;
        }

        private static HashSet<string> CapturePostSwitchPartStateTokens(Vessel v)
        {
            var tokens = new HashSet<string>();
            if (v == null || v.parts == null)
                return tokens;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null)
                    continue;

                ModuleDeployablePart deployable = p.FindModuleImplementing<ModuleDeployablePart>();
                if (deployable != null)
                {
                    ModuleDeployablePart.DeployState ds = deployable.deployState;
                    if (ds == ModuleDeployablePart.DeployState.EXTENDED)
                        tokens.Add($"deployable:{p.persistentId}");
                }

                var fairing = p.FindModuleImplementing<ModuleProceduralFairing>();
                if (fairing != null)
                {
                    try
                    {
                        if (fairing.GetScalar >= 0.5f)
                            tokens.Add($"fairing:{p.persistentId}");
                    }
                    catch
                    {
                    }
                }

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null)
                        continue;

                    if (module is ModuleWheels.ModuleWheelDeployment wheel)
                    {
                        FlightRecorder.ClassifyGearState(
                            wheel.stateString,
                            out bool isDeployed,
                            out bool isRetracted);
                        if (isDeployed && !isRetracted)
                            tokens.Add($"gear:{p.persistentId}:{m}");
                        continue;
                    }

                    if (module is ModuleCargoBay cargoBay)
                    {
                        int deployIdx = cargoBay.DeployModuleIndex;
                        if (deployIdx >= 0 && deployIdx < p.Modules.Count
                            && p.Modules[deployIdx] is ModuleAnimateGeneric animate)
                        {
                            FlightRecorder.ClassifyCargoBayState(
                                animate.animTime,
                                cargoBay.closedPosition,
                                FlightRecorder.ResolveCargoBayDeployLimitStop(
                                    animate, cargoBay.closedPosition),
                                out bool isOpen,
                                out bool isClosed);
                            if (isOpen && !isClosed)
                                tokens.Add($"cargo:{p.persistentId}:{m}");
                        }
                        continue;
                    }

                    if (string.Equals(module.moduleName, "RetractableLadder", StringComparison.Ordinal))
                    {
                        if (FlightRecorder.TryClassifyRetractableLadderState(
                                module,
                                out bool isDeployed,
                                out bool isRetracted)
                            && isDeployed && !isRetracted)
                        {
                            tokens.Add($"ladder:{p.persistentId}:{m}");
                        }
                        continue;
                    }

                    if (string.Equals(module.moduleName, "ModuleAnimationGroup", StringComparison.Ordinal))
                    {
                        if (FlightRecorder.TryClassifyAnimationGroupState(
                                module,
                                out bool isDeployed,
                                out bool isRetracted)
                            && isDeployed && !isRetracted)
                        {
                            tokens.Add($"animation-group:{p.persistentId}:{m}");
                        }
                        continue;
                    }

                    if (string.Equals(module.moduleName, "ModuleAeroSurface", StringComparison.Ordinal))
                    {
                        if (FlightRecorder.TryClassifyAeroSurfaceState(
                                module,
                                out bool isDeployed,
                                out bool isRetracted)
                            && isDeployed && !isRetracted)
                        {
                            tokens.Add($"aero-surface:{p.persistentId}:{m}");
                        }
                        continue;
                    }

                    if (string.Equals(module.moduleName, "ModuleControlSurface", StringComparison.Ordinal))
                    {
                        if (FlightRecorder.TryClassifyControlSurfaceState(
                                module,
                                out bool isDeployed,
                                out bool isRetracted)
                            && isDeployed && !isRetracted)
                        {
                            tokens.Add($"control-surface:{p.persistentId}:{m}");
                        }
                        continue;
                    }

                    if (string.Equals(module.moduleName, "ModuleRobotArmScanner", StringComparison.Ordinal))
                    {
                        if (FlightRecorder.TryClassifyRobotArmScannerState(
                                module,
                                out bool isDeployed,
                                out bool isRetracted)
                            && isDeployed && !isRetracted)
                        {
                            tokens.Add($"robot-arm:{p.persistentId}:{m}");
                        }
                    }
                }
            }

            return tokens;
        }

        private ParsekFlight.PostSwitchAutoRecordTrigger EvaluatePostSwitchAutoRecordTrigger(
            Vessel v,
            ParsekFlight.PostSwitchAutoRecordState state,
            double currentUT)
        {
            int currentPartCount = v != null && v.parts != null ? v.parts.Count : 0;
            bool needsCacheRefresh = NeedsPostSwitchModuleCacheRefresh(
                state.ModuleCachesDirty, state.CachedPartCount, currentPartCount);
            bool evaluateManifestDiff = ShouldEvaluatePostSwitchManifestDiff(
                currentUT,
                state.NextManifestEvaluationUt,
                needsCacheRefresh);
            RefreshPostSwitchAutoRecordModuleCachesIfNeeded(state, v, needsCacheRefresh);

            bool engineTriggered = EvaluatePostSwitchEngineActivity(state, currentUT);
            bool rcsTriggered = EvaluatePostSwitchRcsActivity(state, currentUT);
            bool attitudeChanged = EvaluatePostSwitchAttitudeChange(v, state, currentUT);

            bool crewChanged = false;
            bool resourceChanged = false;
            bool partStateChanged = false;
            EvaluatePostSwitchManifestChanges(
                v,
                state,
                currentUT,
                evaluateManifestDiff,
                out crewChanged,
                out resourceChanged,
                out partStateChanged);

            bool landedMotionChanged = EvaluatePostSwitchLandedMotion(v, state);
            bool orbitChanged = EvaluatePostSwitchOrbitChange(v, state);

            return EvaluatePostSwitchAutoRecordTrigger(
                engineTriggered,
                rcsTriggered,
                attitudeChanged,
                crewChanged,
                resourceChanged,
                partStateChanged,
                landedMotionChanged,
                orbitChanged);
        }

        private void RefreshPostSwitchAutoRecordModuleCachesIfNeeded(
            ParsekFlight.PostSwitchAutoRecordState state,
            Vessel v,
            bool needsCacheRefresh)
        {
            if (needsCacheRefresh)
            {
                RefreshPostSwitchAutoRecordModuleCaches(state, v);
                ParsekLog.VerboseRateLimited("Flight", "post-switch-auto-record-cache-refresh",
                    $"Post-switch watch refreshed module caches: pid={state.VesselPid} " +
                    $"parts={state.CachedPartCount}", 1.0);
            }
        }

        private static bool EvaluatePostSwitchEngineActivity(
            ParsekFlight.PostSwitchAutoRecordState state,
            double currentUT)
        {
            bool engineTriggered = false;
            for (int i = 0; i < state.CachedEngines.Count && !engineTriggered; i++)
            {
                var (part, engine, moduleIndex) = state.CachedEngines[i];
                if (part == null || engine == null)
                    continue;

                var engineEvents = new List<PartEvent>();
                FlightRecorder.CheckEngineTransition(
                    FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex),
                    part.persistentId,
                    moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    engine.EngineIgnited && engine.isOperational,
                    engine.currentThrottle,
                    state.ActiveEngineKeys,
                    state.EngineThrottleMap,
                    currentUT,
                    engineEvents);
                engineTriggered = engineEvents.Count > 0;
            }

            return engineTriggered;
        }

        private static bool EvaluatePostSwitchRcsActivity(
            ParsekFlight.PostSwitchAutoRecordState state,
            double currentUT)
        {
            bool rcsTriggered = false;
            for (int i = 0; i < state.CachedRcsModules.Count && !rcsTriggered; i++)
            {
                var (part, rcs, moduleIndex) = state.CachedRcsModules[i];
                if (part == null || rcs == null)
                    continue;

                var rcsEvents = new List<PartEvent>();
                FlightRecorder.ProcessRcsDebounce(
                    FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex),
                    part.persistentId,
                    moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    rcs.rcs_active && rcs.rcsEnabled,
                    rcs.thrustForces,
                    rcs.thrusterPower,
                    currentUT,
                    state.RcsFrameCountMap,
                    state.ActiveRcsKeys,
                    state.RcsThrottleMap,
                    rcsEvents);
                rcsTriggered = rcsEvents.Count > 0;
            }

            return rcsTriggered;
        }

        private static bool EvaluatePostSwitchAttitudeChange(
            Vessel v,
            ParsekFlight.PostSwitchAutoRecordState state,
            double currentUT)
        {
            Quaternion currentWorldRotation = TrajectoryMath.SanitizeQuaternion(v.transform.rotation);
            float attitudeDeltaDegrees = ComputePostSwitchAttitudeDeltaDegrees(
                state.BaselineWorldRotation,
                currentWorldRotation);
            bool attitudeChanged = HasMeaningfulAttitudeChange(
                state.VesselPid,
                v.persistentId,
                state.BaselineCaptured,
                state.BaselineWorldRotation,
                currentWorldRotation);
            if (!attitudeChanged)
            {
                if (!double.IsNaN(state.AttitudeThresholdExceededAtUt))
                {
                    ParsekLog.Verbose("Flight",
                        $"Post-switch attitude trigger ignored: pid={state.VesselPid} " +
                        $"angle={attitudeDeltaDegrees:F2} threshold={PostSwitchAttitudeChangeThresholdDegrees:F2} " +
                        $"reason=debounce-reset");
                    state.AttitudeThresholdExceededAtUt = double.NaN;
                }
            }
            else if (double.IsNaN(state.AttitudeThresholdExceededAtUt))
            {
                state.AttitudeThresholdExceededAtUt = currentUT;
                attitudeChanged = false;
                ParsekLog.Verbose("Flight",
                    $"Post-switch attitude trigger pending: pid={state.VesselPid} " +
                    $"angle={attitudeDeltaDegrees:F2} threshold={PostSwitchAttitudeChangeThresholdDegrees:F2} " +
                    $"debounce={PostSwitchAttitudeChangeDebounceSeconds:F2}s");
            }
            else if (currentUT - state.AttitudeThresholdExceededAtUt
                < PostSwitchAttitudeChangeDebounceSeconds)
            {
                attitudeChanged = false;
            }
            else
            {
                ParsekLog.Info("Flight",
                    $"Post-switch attitude trigger accepted: pid={state.VesselPid} " +
                    $"angle={attitudeDeltaDegrees:F2} threshold={PostSwitchAttitudeChangeThresholdDegrees:F2} " +
                    $"debounce={currentUT - state.AttitudeThresholdExceededAtUt:F2}s");
            }

            return attitudeChanged;
        }

        private static void EvaluatePostSwitchManifestChanges(
            Vessel v,
            ParsekFlight.PostSwitchAutoRecordState state,
            double currentUT,
            bool evaluateManifestDiff,
            out bool crewChanged,
            out bool resourceChanged,
            out bool partStateChanged)
        {
            crewChanged = false;
            resourceChanged = false;
            partStateChanged = false;
            if (evaluateManifestDiff)
            {
                ConfigNode currentSnapshot = FlightRecorder.TryBackupSnapshot(v);
                var currentCrew = VesselSpawner.ExtractCrewManifest(currentSnapshot);
                var crewDelta = CrewManifest.ComputeCrewDelta(state.BaselineCrew, currentCrew);
                int crewDeltaKeys = CountMeaningfulCrewDelta(crewDelta);
                int resourceDeltaKeys = PostSwitchManifestDeltaSkipped;
                int inventoryDeltaKeys = PostSwitchManifestDeltaSkipped;
                int partStateTokenDelta = PostSwitchManifestDeltaSkipped;
                crewChanged = crewDeltaKeys > 0;
                if (!crewChanged)
                {
                    var currentResources = VesselSnapshotOps.ExtractResourceManifest(currentSnapshot);
                    var resourceDelta = ResourceManifest.ComputeResourceDelta(
                        state.BaselineResources,
                        currentResources);
                    resourceDeltaKeys = CountMeaningfulResourceDelta(resourceDelta);
                    resourceChanged = resourceDeltaKeys > 0;
                }

                if (!crewChanged && !resourceChanged)
                {
                    var currentPartStateTokens = CapturePostSwitchPartStateTokens(v);
                    partStateTokenDelta = CountPartStateTokenDelta(
                        state.BaselinePartStateTokens,
                        currentPartStateTokens);
                    if (partStateTokenDelta > 0)
                    {
                        partStateChanged = true;
                    }
                    else
                    {
                        var currentInventory = VesselSnapshotOps.ExtractInventoryManifest(currentSnapshot, out _);
                        var inventoryDelta = InventoryManifest.ComputeInventoryDelta(
                            state.BaselineInventory,
                            currentInventory);
                        inventoryDeltaKeys = CountMeaningfulInventoryDelta(inventoryDelta);
                        partStateChanged = inventoryDeltaKeys > 0;
                    }
                }

                state.NextManifestEvaluationUt =
                    currentUT + PostSwitchManifestEvaluationIntervalSeconds;
                ParsekLog.VerboseOnChange("Flight",
                    "post-switch-manifest-delta-" + state.VesselPid.ToString(CultureInfo.InvariantCulture),
                    BuildPostSwitchManifestDeltaStateKey(
                        crewDeltaKeys,
                        resourceDeltaKeys,
                        inventoryDeltaKeys,
                        partStateTokenDelta,
                        crewChanged,
                        resourceChanged,
                        partStateChanged),
                    FormatPostSwitchManifestDeltaSummary(
                        state.VesselPid,
                        crewDeltaKeys,
                        resourceDeltaKeys,
                        inventoryDeltaKeys,
                        partStateTokenDelta,
                        crewChanged,
                        resourceChanged,
                        partStateChanged,
                        state.NextManifestEvaluationUt));
            }
        }

        private static bool EvaluatePostSwitchLandedMotion(
            Vessel v,
            ParsekFlight.PostSwitchAutoRecordState state)
        {
            double distanceDelta = Vector3d.Distance(state.BaselineWorldPosition, v.GetWorldPos3D());
            return (v.situation == Vessel.Situations.LANDED || v.situation == Vessel.Situations.SPLASHED)
                && HasMeaningfulLandedMotionChange(distanceDelta, v.srfSpeed);
        }

        private static bool EvaluatePostSwitchOrbitChange(
            Vessel v,
            ParsekFlight.PostSwitchAutoRecordState state)
        {
            return v.situation != Vessel.Situations.LANDED
                && v.situation != Vessel.Situations.SPLASHED
                && v.mainBody != null
                && (!v.mainBody.atmosphere || v.altitude > v.mainBody.atmosphereDepth)
                && HasMeaningfulOrbitChange(
                    state.BaselineOrbit,
                    CapturePostSwitchOrbitSnapshot(v));
        }

        private void PrepareActiveTreeForFreshPostSwitchRecording(
            Vessel v,
            ParsekFlight.PostSwitchAutoRecordState state,
            double currentUT)
        {
            if (host.activeTree == null || v == null)
                return;

            string recordingId = Guid.NewGuid().ToString("N");
            var recording = new Recording
            {
                RecordingId = recordingId,
                TreeId = host.activeTree.Id,
                VesselPersistentId = v.persistentId,
                VesselName = Recording.ResolveLocalizedName(v.vesselName) ?? v.vesselName ?? "Unknown",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                // Pin start-of-recording controller identity for the fresh post-switch
                // root so a later BG go-on-rails identity-loss check has the original
                // controllable-part PIDs to compare against.
                Controllers = ControllerInfo.CaptureFromVessel(v)
            };

            ConfigNode startSnapshot = FlightRecorder.TryBackupSnapshot(v);
            if (startSnapshot != null)
                recording.GhostVisualSnapshot = startSnapshot;

            host.activeTree.AddOrReplaceRecording(recording);
            host.activeTree.ActiveRecordingId = recordingId;
            host.activeTree.BackgroundMap.Remove(v.persistentId);

            if (!string.IsNullOrEmpty(state.FreshStartParentRecordingId)
                && host.activeTree.Recordings.TryGetValue(state.FreshStartParentRecordingId, out var parent))
            {
                string branchPointId = Guid.NewGuid().ToString("N");
                var branchPoint = new BranchPoint
                {
                    Id = branchPointId,
                    UT = currentUT,
                    Type = BranchPointType.Launch,
                    ParentRecordingIds = new List<string> { state.FreshStartParentRecordingId },
                    ChildRecordingIds = new List<string> { recordingId }
                };
                parent.ChildBranchPointId = branchPointId;
                recording.ParentBranchPointId = branchPointId;
                host.activeTree.BranchPoints.Add(branchPoint);
            }
        }

        private void SeedPostSwitchAlignmentWindow(Vessel v, ParsekFlight.PostSwitchAutoRecordState state)
        {
            if (v == null || state == null || host.recorder == null || !host.recorder.IsRecording)
                return;

            if (state.BaselineSeedPoint.HasValue)
            {
                host.recorder.SeedTrajectoryPoint(
                    state.BaselineSeedPoint.Value,
                    v,
                    "post-switch attitude baseline");
            }

            host.recorder.SamplePosition(v);
            ParsekLog.Verbose("Flight",
                $"Post-switch alignment window seeded: pid={state.VesselPid} " +
                $"baselineUT={state.BaselineCapturedUt:F2} currentUT={Planetarium.GetUniversalTime():F2}");
        }

        private bool TryStartPostSwitchAutoRecord(
            Vessel v,
            ParsekFlight.PostSwitchAutoRecordState state,
            ParsekFlight.PostSwitchAutoRecordTrigger trigger,
            double currentUT)
        {
            bool activeVesselTrackedInBackground = host.activeTree != null
                && host.activeTree.BackgroundMap.ContainsKey(v.persistentId);
            var decision = EvaluatePostSwitchAutoRecordStartDecision(
                state.VesselPid,
                v.persistentId,
                host.activeTree != null,
                activeVesselTrackedInBackground,
                canRestorePendingTrackedTree: false,
                suppressStart: false);

            ParsekLog.Info("Flight",
                $"Post-switch trigger accepted: vessel='{v.vesselName}' pid={v.persistentId} " +
                $"trigger={trigger} decision={decision}");

            switch (decision)
            {
                case ParsekFlight.PostSwitchAutoRecordStartDecision.PromoteTrackedRecording:
                    if (host.activeTree != null
                        && host.activeTree.BackgroundMap.TryGetValue(v.persistentId, out string recordingId))
                    {
                        host.backgroundRecorder?.OnVesselRemovedFromBackground(v.persistentId);
                        host.PromoteRecordingFromBackground(recordingId, v);
                    }
                    break;

                case ParsekFlight.PostSwitchAutoRecordStartDecision.RestoreAndPromoteTrackedRecording:
                    ParsekLog.Verbose("Flight",
                        "Post-switch trigger hit restore-and-promote seam, but #534 restore wiring is gated on this branch");
                    return false;

                case ParsekFlight.PostSwitchAutoRecordStartDecision.StartFreshRecording:
                    PrepareActiveTreeForFreshPostSwitchRecording(v, state, currentUT);
                    host.StartRecording(suppressStartScreenMessage: true);
                    if (host.IsRecording)
                    {
                        host.Log($"Auto-record started (post-switch {trigger})");
                        host.ScreenMessage("Recording STARTED (auto - post switch)", 2f);
                    }
                    break;
            }

            if (!host.IsRecording)
                return false;

            if (trigger == ParsekFlight.PostSwitchAutoRecordTrigger.AttitudeChange)
                SeedPostSwitchAlignmentWindow(v, state);

            DisarmPostSwitchAutoRecord($"recording started via {trigger}");
            return true;
        }

        internal void OnPostSwitchAutoRecordPhysicsFrame(Vessel v)
        {
            ParsekFlight.PostSwitchAutoRecordState state = host.postSwitchAutoRecord;
            if (state == null)
                return;

            uint activePid = v != null ? v.persistentId : 0;
            double currentUT = Planetarium.GetUniversalTime();
            var suppression = EvaluatePostSwitchAutoRecordSuppression(
                ParsekSettings.Current?.autoRecordOnFirstModificationAfterSwitch != false,
                host.IsRecording,
                v != null,
                activePid,
                state.VesselPid,
                v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId),
                ParsekFlight.restoringActiveTree,
                HasPendingPostSwitchAutoRecordTransition(),
                host.IsAnyWarpActive(),
                v != null && v.packed);

            if (suppression == ParsekFlight.PostSwitchAutoRecordSuppressionReason.Disabled
                || suppression == ParsekFlight.PostSwitchAutoRecordSuppressionReason.AlreadyRecording)
            {
                LogPostSwitchAutoRecordDecision(
                    state,
                    activePid,
                    suppression,
                    waitingForSettle: false,
                    currentUt: currentUT,
                    trigger: ParsekFlight.PostSwitchAutoRecordTrigger.None);
                DisarmPostSwitchAutoRecord($"suppressed by {suppression}");
                return;
            }

            if (suppression != ParsekFlight.PostSwitchAutoRecordSuppressionReason.None)
            {
                LogPostSwitchAutoRecordDecision(
                    state,
                    activePid,
                    suppression,
                    waitingForSettle: false,
                    currentUt: currentUT,
                    trigger: ParsekFlight.PostSwitchAutoRecordTrigger.None);
                return;
            }

            if (!state.BaselineCaptured)
            {
                LogPostSwitchAutoRecordDecision(
                    state,
                    activePid,
                    suppression,
                    waitingForSettle: false,
                    currentUt: currentUT,
                    trigger: ParsekFlight.PostSwitchAutoRecordTrigger.None);
                CapturePostSwitchAutoRecordBaseline(state, v, currentUT);
                return;
            }

            if (currentUT < state.ComparisonsReadyUt)
            {
                LogPostSwitchAutoRecordDecision(
                    state,
                    activePid,
                    suppression,
                    waitingForSettle: true,
                    currentUt: currentUT,
                    trigger: ParsekFlight.PostSwitchAutoRecordTrigger.None);
                return;
            }

            ParsekFlight.PostSwitchAutoRecordTrigger trigger =
                EvaluatePostSwitchAutoRecordTrigger(v, state, currentUT);
            LogPostSwitchAutoRecordDecision(
                state,
                activePid,
                suppression,
                waitingForSettle: false,
                currentUt: currentUT,
                trigger: trigger);
            if (trigger == ParsekFlight.PostSwitchAutoRecordTrigger.None)
                return;

            TryStartPostSwitchAutoRecord(v, state, trigger, currentUT);
        }

        /// <summary>
        /// Handles deferred auto-record for EVA: starts recording when vessel switch
        /// to the EVA kerbal is complete.
        /// </summary>
        internal void HandleDeferredAutoRecordEva()
        {
            bool hasActiveVessel = FlightGlobals.ActiveVessel != null;
            if (!ShouldStartDeferredAutoRecordEva(
                host.pendingAutoRecord,
                host.IsRecording,
                hasActiveVessel,
                activeVesselIsEva: hasActiveVessel && FlightGlobals.ActiveVessel.isEVA))
                return;

            uint activePid = FlightGlobals.ActiveVessel?.persistentId ?? 0u;
            string invalidHeadReason;
            if (!ParsekFlight.CanStartRecorderWithActiveTreeHead(host.activeTree, activePid, out invalidHeadReason))
            {
                ParsekLog.WarnRateLimited(
                    "Flight",
                    ParsekFlight.BuildInvalidActiveTreeHeadRateLimitKey(
                        "deferred-eva",
                        host.activeTree,
                        activePid,
                        invalidHeadReason),
                    $"HandleDeferredAutoRecordEva: clearing pending auto-record because active tree head " +
                    $"cannot accept a recorder (reason={invalidHeadReason}, activePid={activePid}, " +
                    $"tree={host.activeTree?.Id ?? "null"}, activeRec={host.activeTree?.ActiveRecordingId ?? "null"})");
                ClearPendingEvaAutoRecordState();
                host.ScreenMessage("Recording blocked (invalid tree head)", 2f);
                return;
            }

            host.StartRecording(suppressStartScreenMessage: true);

            if (host.IsRecording)
            {
                host.pendingAutoRecord = false;
                host.Log("Auto-record started (EVA from pad)");
                host.ScreenMessage("Recording STARTED (auto - EVA from pad)", 2f);
            }
            // else: StartRecording failed (paused game or no active vessel).
            // Keep pending flags so we retry next frame; fall through to
            // HandleInput/playback so they aren't starved.
        }

        internal static bool HasMeaningfulLandedMotionChange(
            double distanceDeltaMeters,
            double speedMetersPerSecond)
        {
            return distanceDeltaMeters >= PostSwitchLandedMotionDistanceThreshold
                || speedMetersPerSecond >= PostSwitchLandedMotionSpeedThreshold;
        }

        internal static float ComputePostSwitchAttitudeDeltaDegrees(
            Quaternion baselineWorldRotation,
            Quaternion currentWorldRotation)
        {
            return Quaternion.Angle(
                TrajectoryMath.NormalizeQuaternionForComparison(baselineWorldRotation),
                TrajectoryMath.NormalizeQuaternionForComparison(currentWorldRotation));
        }

        internal static bool HasMeaningfulAttitudeChange(
            uint armedVesselPid,
            uint activeVesselPid,
            bool hasBaselineRotation,
            Quaternion baselineWorldRotation,
            Quaternion currentWorldRotation,
            float thresholdDegrees = PostSwitchAttitudeChangeThresholdDegrees)
        {
            if (!hasBaselineRotation || armedVesselPid == 0 || activeVesselPid == 0
                || armedVesselPid != activeVesselPid)
            {
                return false;
            }

            return ComputePostSwitchAttitudeDeltaDegrees(
                baselineWorldRotation,
                currentWorldRotation) >= thresholdDegrees;
        }

        internal static bool HasMeaningfulOrbitChange(
            ParsekFlight.PostSwitchOrbitSnapshot baseline,
            ParsekFlight.PostSwitchOrbitSnapshot current)
        {
            if (!baseline.IsValid || !current.IsValid)
                return false;

            if (!string.Equals(baseline.BodyName, current.BodyName, StringComparison.Ordinal))
                return true;

            if (Math.Abs(current.SemiMajorAxis - baseline.SemiMajorAxis) >=
                PostSwitchOrbitSemiMajorAxisThresholdMeters)
                return true;
            if (Math.Abs(current.Eccentricity - baseline.Eccentricity) >=
                PostSwitchOrbitEccentricityThreshold)
                return true;
            if (NormalizeAngleDeltaDegrees(
                    current.Inclination,
                    baseline.Inclination) >= PostSwitchOrbitAngleThresholdDegrees)
                return true;
            if (NormalizeAngleDeltaDegrees(
                    current.LongitudeOfAscendingNode,
                    baseline.LongitudeOfAscendingNode) >= PostSwitchOrbitAngleThresholdDegrees)
                return true;
            return NormalizeAngleDeltaDegrees(
                current.ArgumentOfPeriapsis,
                baseline.ArgumentOfPeriapsis) >= PostSwitchOrbitAngleThresholdDegrees;
        }

        internal static bool HasMeaningfulCrewDelta(Dictionary<string, int> delta)
        {
            if (delta == null) return false;
            foreach (var entry in delta)
            {
                if (entry.Value != 0)
                    return true;
            }
            return false;
        }

        internal static int CountMeaningfulCrewDelta(Dictionary<string, int> delta)
        {
            if (delta == null) return 0;
            int count = 0;
            foreach (var entry in delta)
            {
                if (entry.Value != 0)
                    count++;
            }
            return count;
        }

        internal static bool HasMeaningfulResourceDelta(
            Dictionary<string, double> delta,
            double epsilon = PostSwitchResourceDeltaEpsilon)
        {
            if (delta == null) return false;
            foreach (var entry in delta)
            {
                if (Math.Abs(entry.Value) > epsilon)
                    return true;
            }
            return false;
        }

        internal static int CountMeaningfulResourceDelta(
            Dictionary<string, double> delta,
            double epsilon = PostSwitchResourceDeltaEpsilon)
        {
            if (delta == null) return 0;
            int count = 0;
            foreach (var entry in delta)
            {
                if (Math.Abs(entry.Value) > epsilon)
                    count++;
            }
            return count;
        }

        internal static bool HasMeaningfulInventoryDelta(Dictionary<string, InventoryItem> delta)
        {
            if (delta == null) return false;
            foreach (var entry in delta)
            {
                if (entry.Value.count != 0 || entry.Value.slotsTaken != 0)
                    return true;
            }
            return false;
        }

        internal static int CountMeaningfulInventoryDelta(Dictionary<string, InventoryItem> delta)
        {
            if (delta == null) return 0;
            int count = 0;
            foreach (var entry in delta)
            {
                if (entry.Value.count != 0 || entry.Value.slotsTaken != 0)
                    count++;
            }
            return count;
        }

        internal static bool HasMeaningfulPartStateTokenChange(
            ICollection<string> baselineTokens,
            ICollection<string> currentTokens)
        {
            int baselineCount = baselineTokens != null ? baselineTokens.Count : 0;
            int currentCount = currentTokens != null ? currentTokens.Count : 0;
            if (baselineCount != currentCount)
                return true;
            if (baselineCount == 0)
                return false;

            var baselineSet = baselineTokens as HashSet<string> ?? new HashSet<string>(baselineTokens);
            foreach (string token in currentTokens)
            {
                if (!baselineSet.Contains(token))
                    return true;
            }
            return false;
        }

        internal static int CountPartStateTokenDelta(
            ICollection<string> baselineTokens,
            ICollection<string> currentTokens)
        {
            int baselineCount = baselineTokens != null ? baselineTokens.Count : 0;
            int currentCount = currentTokens != null ? currentTokens.Count : 0;
            if (baselineCount == 0)
                return currentCount;
            if (currentCount == 0)
                return baselineCount;

            var baselineSet = baselineTokens as HashSet<string> ?? new HashSet<string>(baselineTokens);
            var currentSet = currentTokens as HashSet<string> ?? new HashSet<string>(currentTokens);
            int delta = 0;
            foreach (string token in currentSet)
            {
                if (!baselineSet.Contains(token))
                    delta++;
            }
            foreach (string token in baselineSet)
            {
                if (!currentSet.Contains(token))
                    delta++;
            }
            return delta;
        }

        internal static string FormatPostSwitchManifestDeltaSummary(
            uint vesselPid,
            int crewDeltaKeys,
            int resourceDeltaKeys,
            int inventoryDeltaKeys,
            int partStateTokenDelta,
            bool crewChanged,
            bool resourceChanged,
            bool partStateChanged,
            double nextEvaluationUt)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Post-switch manifest delta: pid={0} crewChanged={1} resourceChanged={2} partStateChanged={3} crewDeltaKeys={4} resourceDeltaKeys={5} inventoryDeltaKeys={6} partStateTokenDelta={7} nextEvalUT={8:F1}",
                vesselPid,
                crewChanged,
                resourceChanged,
                partStateChanged,
                FormatPostSwitchManifestDeltaCount(crewDeltaKeys),
                FormatPostSwitchManifestDeltaCount(resourceDeltaKeys),
                FormatPostSwitchManifestDeltaCount(inventoryDeltaKeys),
                FormatPostSwitchManifestDeltaCount(partStateTokenDelta),
                nextEvaluationUt);
        }

        internal static string FormatSplitSkipSummary(
            string source,
            string reason,
            string activeRecordingId,
            uint sourcePid,
            uint targetPid,
            bool pendingSplitInProgress)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0}: split path skipped reason={1} activeRec={2} sourcePid={3} targetPid={4} pendingSplitInProgress={5}",
                string.IsNullOrEmpty(source) ? "(unknown)" : source,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                string.IsNullOrEmpty(activeRecordingId) ? "(none)" : activeRecordingId,
                sourcePid,
                targetPid,
                pendingSplitInProgress);
        }

        internal static bool HasMeaningfulPartStateChange(IEnumerable<PartEventType> changedTypes)
        {
            if (changedTypes == null) return false;
            foreach (PartEventType type in changedTypes)
            {
                if (GhostingTriggerClassifier.IsGhostingTrigger(type))
                    return true;
            }
            return false;
        }

        internal static ParsekFlight.PostSwitchAutoRecordTrigger EvaluatePostSwitchAutoRecordTrigger(
            bool engineTriggered,
            bool rcsTriggered,
            bool attitudeChanged,
            bool crewChanged,
            bool resourceChanged,
            bool partStateChanged,
            bool landedMotionChanged,
            bool orbitChanged)
        {
            if (engineTriggered)
                return ParsekFlight.PostSwitchAutoRecordTrigger.EngineActivity;
            if (rcsTriggered)
                return ParsekFlight.PostSwitchAutoRecordTrigger.SustainedRcsActivity;
            if (attitudeChanged)
                return ParsekFlight.PostSwitchAutoRecordTrigger.AttitudeChange;
            if (crewChanged)
                return ParsekFlight.PostSwitchAutoRecordTrigger.CrewChange;
            if (resourceChanged)
                return ParsekFlight.PostSwitchAutoRecordTrigger.ResourceChange;
            if (partStateChanged)
                return ParsekFlight.PostSwitchAutoRecordTrigger.PartStateChange;
            if (landedMotionChanged)
                return ParsekFlight.PostSwitchAutoRecordTrigger.LandedMotion;
            if (orbitChanged)
                return ParsekFlight.PostSwitchAutoRecordTrigger.OrbitChange;
            return ParsekFlight.PostSwitchAutoRecordTrigger.None;
        }

        internal static ParsekFlight.PostSwitchAutoRecordStartDecision EvaluatePostSwitchAutoRecordStartDecision(
            uint armedVesselPid,
            uint activeVesselPid,
            bool hasActiveTree,
            bool activeVesselTrackedInBackground,
            bool canRestorePendingTrackedTree,
            bool suppressStart)
        {
            if (suppressStart || armedVesselPid == 0 || activeVesselPid == 0
                || armedVesselPid != activeVesselPid)
                return ParsekFlight.PostSwitchAutoRecordStartDecision.None;
            if (hasActiveTree && activeVesselTrackedInBackground)
                return ParsekFlight.PostSwitchAutoRecordStartDecision.PromoteTrackedRecording;
            if (canRestorePendingTrackedTree)
                return ParsekFlight.PostSwitchAutoRecordStartDecision.RestoreAndPromoteTrackedRecording;
            return ParsekFlight.PostSwitchAutoRecordStartDecision.StartFreshRecording;
        }
    }
}
