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

        private const float PostSwitchRotationCommandEpsilon = 0.01f;

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

            ParsekFlight.PostSwitchAutoRecordState previous = host.postSwitchAutoRecord;
            List<ParsekFlight.PostSwitchStructuralSplit> carried = null;
            if (previous != null && previous.PendingStructuralSplits != null
                && previous.PendingStructuralSplits.Count > 0)
            {
                int duplicatesRefused;
                carried = CarryStructuralSplitsAcrossRearm(
                    previous.PendingStructuralSplits, previous.VesselPid, vessel.persistentId,
                    out duplicatesRefused);
                string line =
                    $"Post-switch watch re-armed while holding {previous.PendingStructuralSplits.Count} captured " +
                    $"separation(s): oldPid={previous.VesselPid} newPid={vessel.persistentId} " +
                    $"carried={carried.Count} duplicatesRefused={duplicatesRefused} reason={armReason}";
                if (WatchReplaceDropsCaptures(previous.PendingStructuralSplits.Count, carried.Count))
                    ParsekLog.Warn("Flight", line + " - the separations belong to a vessel no watch follows; not recorded");
                else
                    ParsekLog.Info("Flight", line);
            }

            host.postSwitchAutoRecord = new ParsekFlight.PostSwitchAutoRecordState
            {
                VesselPid = vessel.persistentId,
                VesselName = vessel.vesselName ?? "Unknown",
                ArmedAtUt = Planetarium.GetUniversalTime(),
                ArmedReason = armReason,
                TrackedInActiveTree = trackedInActiveTree,
                FreshStartParentRecordingId = freshStartParentRecordingId
            };
            if (carried != null)
                host.postSwitchAutoRecord.PendingStructuralSplits.AddRange(carried);

            ParsekLog.Info("Flight",
                $"Post-switch auto-record armed: vessel='{host.postSwitchAutoRecord.VesselName}' " +
                $"pid={host.postSwitchAutoRecord.VesselPid} tracked={trackedInActiveTree} " +
                $"reason={armReason}");
        }

        internal void DisarmPostSwitchAutoRecord(string reason)
        {
            if (host.postSwitchAutoRecord == null)
                return;

            int droppedSplits = host.postSwitchAutoRecord.PendingStructuralSplits?.Count ?? 0;
            ParsekLog.Info("Flight",
                $"Post-switch auto-record disarmed: vessel='{host.postSwitchAutoRecord.VesselName}' " +
                $"pid={host.postSwitchAutoRecord.VesselPid} reason={reason}" +
                (droppedSplits > 0 ? $" pendingStructuralSplits={droppedSplits}" : ""));
            if (droppedSplits > 0)
            {
                ParsekLog.Warn("Flight",
                    $"Post-switch watch disarmed with {droppedSplits} separation(s) its recording never took: " +
                    $"pid={host.postSwitchAutoRecord.VesselPid} reason={reason}");
            }
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
            state.BaselineSasEnabled = ReadSasEnabled(v);
            state.BaselineSasMode = ReadSasMode(v);
            state.RotationCommandSeen = false;
            state.PassiveAttitudeDriftDegrees = 0f;
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
            if (FollowPassiveOutsiderAttitude(v, state, currentWorldRotation, currentUT))
                return false;
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

        // An outsider's attitude counts only from the first rotation the player commands
        // (design 5.4 rule 5): until then the baseline follows the vessel, so a tumble, the
        // co-rotating world frame of a low orbit or the attitude a packed vessel comes back with
        // after warp never accumulates into a trigger. A tracked background member is already
        // part of the recording, so any change promotes it as before. True: no attitude trigger
        // this frame.
        private static bool FollowPassiveOutsiderAttitude(
            Vessel v,
            ParsekFlight.PostSwitchAutoRecordState state,
            Quaternion currentWorldRotation,
            double currentUT)
        {
            bool sasEnabled = ReadSasEnabled(v);
            int sasMode = ReadSasMode(v);
            FlightCtrlState input = FlightInputHandler.state;
            FlightCtrlState ctrl = v.ctrlState;
            bool command = IsPlayerRotationCommand(
                state.BaselineSasEnabled, state.BaselineSasMode, sasEnabled, sasMode,
                input != null ? input.pitch : 0f, input != null ? input.yaw : 0f, input != null ? input.roll : 0f,
                ctrl != null ? ctrl.pitch : 0f, ctrl != null ? ctrl.yaw : 0f, ctrl != null ? ctrl.roll : 0f);
            PostSwitchAttitudeBaselineStep step = DecidePostSwitchAttitudeBaselineStep(
                state.TrackedInActiveTree, state.RotationCommandSeen, command);
            if (step == PostSwitchAttitudeBaselineStep.Measure)
                return false;
            if (step == PostSwitchAttitudeBaselineStep.FollowPassive)
            {
                float driftDegrees = ComputePostSwitchAttitudeDeltaDegrees(
                    state.BaselineWorldRotation, currentWorldRotation);
                state.PassiveAttitudeDriftDegrees += driftDegrees;
                state.BaselineWorldRotation = currentWorldRotation;
                state.AttitudeThresholdExceededAtUt = double.NaN;
                ParsekLog.VerboseOnChange("Flight",
                    "post-switch-attitude-passive-" + state.VesselPid.ToString(CultureInfo.InvariantCulture),
                    state.PassiveAttitudeDriftDegrees >= PostSwitchAttitudeChangeThresholdDegrees ? "over" : "under",
                    string.Format(CultureInfo.InvariantCulture,
                        "Post-switch attitude follows passive motion: pid={0} passiveDrift={1:F2} " +
                        "threshold={2:F2} sas={3} - no rotation command yet, not a change",
                        state.VesselPid, state.PassiveAttitudeDriftDegrees,
                        PostSwitchAttitudeChangeThresholdDegrees, sasEnabled));
                return true;
            }

            state.RotationCommandSeen = true;
            state.BaselineWorldRotation = currentWorldRotation;
            state.AttitudeThresholdExceededAtUt = double.NaN;
            state.BaselineSeedPoint = FlightRecorder.BuildTrajectoryPoint(
                v, FlightRecorder.SampleCurrentVelocity(v), currentUT);
            ParsekLog.Info("Flight", string.Format(CultureInfo.InvariantCulture,
                "Post-switch rotation command seen: pid={0} sas={1}->{2} mode={3}->{4} " +
                "passiveDriftIgnored={5:F2} - attitude measured from here",
                state.VesselPid, state.BaselineSasEnabled, sasEnabled,
                state.BaselineSasMode, sasMode, state.PassiveAttitudeDriftDegrees));
            return true;
        }

        /// <summary>What the attitude baseline does on one evaluated frame.</summary>
        internal enum PostSwitchAttitudeBaselineStep
        {
            /// <summary>Measure the attitude change from the baseline (a tracked background
            /// member, or an outsider after its first rotation command).</summary>
            Measure = 0,
            /// <summary>An outsider with no rotation command yet: the baseline follows the
            /// vessel, so passive motion never counts.</summary>
            FollowPassive = 1,
            /// <summary>An outsider's first rotation command: the baseline is taken here and
            /// measured from the next frame on.</summary>
            LatchCommand = 2,
        }

        /// <summary>Pure: the attitude baseline step (design 5.4 rule 5).</summary>
        internal static PostSwitchAttitudeBaselineStep DecidePostSwitchAttitudeBaselineStep(
            bool trackedInActiveTree, bool rotationCommandSeen, bool rotationCommandNow)
        {
            if (trackedInActiveTree || rotationCommandSeen)
                return PostSwitchAttitudeBaselineStep.Measure;
            return rotationCommandNow
                ? PostSwitchAttitudeBaselineStep.LatchCommand
                : PostSwitchAttitudeBaselineStep.FollowPassive;
        }

        private static bool ReadSasEnabled(Vessel v)
        {
            return v != null && v.Autopilot != null && v.Autopilot.Enabled;
        }

        private static int ReadSasMode(Vessel v)
        {
            return v != null && v.Autopilot != null ? (int)v.Autopilot.Mode : -1;
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
            // A body change is an SOI crossing the vessel coasts through on its own (a burn shows
            // first as an element change on the old body, and its fuel as a resource change):
            // an outsider's orbit baseline moves to the new body instead of triggering
            // (design 5.4 rule 5). A tracked background member keeps promoting on it.
            if (!state.TrackedInActiveTree)
            {
                ParsekFlight.PostSwitchOrbitSnapshot current = CapturePostSwitchOrbitSnapshot(v);
                if (IsPassiveSoiTransition(state.BaselineOrbit, current))
                {
                    ParsekLog.Info("Flight",
                        $"Post-switch orbit baseline moved to the new body: pid={state.VesselPid} " +
                        $"from={state.BaselineOrbit.BodyName} to={current.BodyName} - an SOI change " +
                        "while coasting is not a change");
                    state.BaselineOrbit = current;
                    return false;
                }
            }

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

        /// <summary>
        /// Owner ruling Q1b: a fresh post-switch recording starts on a vessel whose recording
        /// in the live tree already ended in a prediction, so that prediction is dropped and
        /// the ended recording stops at its last real sample instead of replaying a predicted
        /// ending beside the real flight.
        /// </summary>
        private void DropPredictedEndingOfEndedRecording(Vessel v, double currentUT)
        {
            if (host.activeTree == null || v == null)
                return;
            string leafId = PredictedTakeoverLeaf.ResolveInTree(
                host.activeTree, v.persistentId,
                v.id != Guid.Empty ? v.id.ToString("N") : null);
            if (string.IsNullOrEmpty(leafId)
                || !host.activeTree.Recordings.TryGetValue(leafId, out Recording leaf))
            {
                return;
            }
            ParsekFlight.DropPredictedEndingForLiveTakeover(
                leaf, v, currentUT, "PostSwitchAutoRecord.StartFreshRecording");
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

        /// <summary>
        /// What a separation means for the post-switch watch
        /// (<see cref="EvaluatePostSwitchStructuralSplit"/>).
        /// </summary>
        internal enum PostSwitchStructuralSplitDecision
        {
            NoWatch = 0,
            RecorderBound = 1,
            NotWatchedVessel = 2,
            Capture = 3,
            // The watched vessel is a background member of the active tree: the background
            // recorder's own split detection (HandleBackgroundVesselSplit) records the split.
            BackgroundRecorderOwns = 4,
        }

        /// <summary>
        /// Pure: whether a separation (<paramref name="originalPid"/> lost
        /// <paramref name="newPid"/>) is one the armed post-switch watch must capture: the watch
        /// is armed, no recorder is bound and no split is parked (those paths own a split), and
        /// the original vessel is the watched one (KSP keeps the original pid across an undock
        /// or a decouple; the separated side is the new vessel). A watched vessel the active tree
        /// already tracks in the background is left to the background recorder, which records
        /// its splits itself (capturing it too would record the child twice).
        /// </summary>
        internal static PostSwitchStructuralSplitDecision EvaluatePostSwitchStructuralSplit(
            bool watchArmed,
            uint watchedPid,
            bool recorderBound,
            bool splitInProgress,
            uint originalPid,
            uint newPid,
            bool watchedTrackedInActiveTree = false)
        {
            if (!watchArmed)
                return PostSwitchStructuralSplitDecision.NoWatch;
            if (recorderBound || splitInProgress)
                return PostSwitchStructuralSplitDecision.RecorderBound;
            if (watchedPid == 0u || originalPid != watchedPid || newPid == 0u || newPid == watchedPid)
                return PostSwitchStructuralSplitDecision.NotWatchedVessel;
            if (watchedTrackedInActiveTree)
                return PostSwitchStructuralSplitDecision.BackgroundRecorderOwns;
            return PostSwitchStructuralSplitDecision.Capture;
        }

        /// <summary>
        /// A separation of the vessel the post-switch watch is armed for, while nothing records
        /// it (owner ruling 2026-11-18, todo POST-SWITCH-WATCH-SPLIT-NOT-RECORDED): captures the
        /// vessel that came off (snapshot and pose at the split) so the next watch frame starts
        /// the watched vessel's recording and gives the separated vessel its own child
        /// recording. Returns true when captured.
        /// </summary>
        internal bool TryCapturePostSwitchStructuralSplit(
            Vessel originalVessel,
            Vessel newVessel,
            string cause,
            string source,
            TrajectoryPoint? childSeed = null)
        {
            ParsekFlight.PostSwitchAutoRecordState state = host.postSwitchAutoRecord;
            uint originalPid = originalVessel != null ? originalVessel.persistentId : 0u;
            uint newPid = newVessel != null ? newVessel.persistentId : 0u;
            var decision = EvaluatePostSwitchStructuralSplit(
                watchArmed: state != null,
                watchedPid: state != null ? state.VesselPid : 0u,
                recorderBound: host.recorder != null && host.recorder.IsRecording,
                splitInProgress: host.pendingSplitInProgress,
                originalPid: originalPid,
                newPid: newPid,
                watchedTrackedInActiveTree: state != null
                    && IsWatchedVesselTrackedInBackground(
                        host.activeTree != null ? host.activeTree.BackgroundMap : null, state.VesselPid));
            if (decision != PostSwitchStructuralSplitDecision.Capture)
            {
                if (decision == PostSwitchStructuralSplitDecision.BackgroundRecorderOwns)
                {
                    ParsekLog.Info("Flight",
                        $"Post-switch watch: separation left to the background recorder ({source}): " +
                        $"watchedPid={originalPid} is a tracked background member, newPid={newPid}");
                }
                else if (decision != PostSwitchStructuralSplitDecision.NoWatch)
                {
                    ParsekLog.Verbose("Flight",
                        $"Post-switch watch: separation not captured ({source}): decision={decision} " +
                        $"originalPid={originalPid} newPid={newPid} watchedPid={state?.VesselPid ?? 0u}");
                }
                return false;
            }
            if (GhostMapPresence.IsGhostOrDroneVessel(newPid))
            {
                ParsekLog.Verbose("Flight",
                    $"Post-switch watch: separation not captured ({source}): newPid={newPid} is a ghost map vessel");
                return false;
            }

            for (int i = 0; i < state.PendingStructuralSplits.Count; i++)
            {
                if (state.PendingStructuralSplits[i] != null && state.PendingStructuralSplits[i].ChildPid == newPid)
                {
                    ParsekLog.Verbose("Flight",
                        $"Post-switch watch: separation already captured ({source}): childPid={newPid}, duplicate refused");
                    return true;
                }
            }

            double ut = Planetarium.GetUniversalTime();
            ConfigNode preSnapshot = FlightRecorder.TryBackupSnapshot(newVessel);
            TrajectoryPoint? prePoint = childSeed
                ?? BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                    newVessel, ut, preferRootPartSurfacePose: true);
            bool hasController = ParsekFlight.ShouldRecordAsOwnBranch(newVessel);
            // The watched side too: when stock moves focus to the vessel that came off, the
            // watch re-arms on it and the watched side becomes the separated vessel
            // (CarryStructuralSplitsAcrossRearm).
            state.PendingStructuralSplits.Add(new ParsekFlight.PostSwitchStructuralSplit
            {
                ChildPid = newPid,
                HasController = hasController,
                Cause = cause,
                UT = ut,
                ChildRootPartPid = newVessel.rootPart != null ? newVessel.rootPart.persistentId : 0u,
                PreSnapshot = preSnapshot,
                PrePoint = prePoint,
                Source = source,
                OriginalPid = originalPid,
                OriginalHasController = ParsekFlight.ShouldRecordAsOwnBranch(originalVessel),
                OriginalRootPartPid = originalVessel.rootPart != null ? originalVessel.rootPart.persistentId : 0u,
                OriginalPreSnapshot = FlightRecorder.TryBackupSnapshot(originalVessel),
                OriginalPrePoint = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                    originalVessel, ut, preferRootPartSurfacePose: true),
            });
            ParsekLog.Info("Flight",
                $"Post-switch watch: separation of the watched vessel captured ({source}): " +
                $"watchedPid={originalPid} childPid={newPid} name='{newVessel.vesselName}' cause={cause} " +
                $"hasController={hasController} preSnapshot={preSnapshot != null} " +
                $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)} pending={state.PendingStructuralSplits.Count}");
            return true;
        }

        /// <summary>
        /// Pure: the captured separations a watch re-armed from
        /// <paramref name="previousWatchedPid"/> to <paramref name="newWatchedPid"/> keeps. Every
        /// capture names both vessels of its split with their own split-time data, so the
        /// captures together know every vessel of the separation. When the new watch is on one
        /// of them (stock moved focus to a vessel that came off, possibly over several re-arms
        /// before the watch started), the carried set is every OTHER vessel of the split, each
        /// once and with its own data, so all of them are recorded when the new watch starts. A
        /// re-arm on the same vessel keeps the separated vessels. A re-arm on an unrelated vessel
        /// keeps none: the separations belong to a vessel no watch follows any more.
        /// <paramref name="duplicatesRefused"/> counts captures whose child was already carried.
        /// </summary>
        internal static List<ParsekFlight.PostSwitchStructuralSplit> CarryStructuralSplitsAcrossRearm(
            IReadOnlyList<ParsekFlight.PostSwitchStructuralSplit> captures,
            uint previousWatchedPid,
            uint newWatchedPid,
            out int duplicatesRefused)
        {
            duplicatesRefused = 0;
            var result = new List<ParsekFlight.PostSwitchStructuralSplit>();
            if (captures == null || captures.Count == 0 || newWatchedPid == 0u)
                return result;

            // Every vessel the captures know, first occurrence wins, in capture order.
            var order = new List<uint>();
            var sides = new Dictionary<uint, ParsekFlight.PostSwitchStructuralSplit>();
            var seenChildren = new HashSet<uint>();
            for (int i = 0; i < captures.Count; i++)
            {
                var c = captures[i];
                if (c == null)
                    continue;
                if (c.ChildPid != 0u && !seenChildren.Add(c.ChildPid))
                    duplicatesRefused++;
                AddSide(order, sides, c.ChildPid, c.HasController, c.ChildRootPartPid,
                    c.PreSnapshot, c.PrePoint, c);
                AddSide(order, sides, c.OriginalPid, c.OriginalHasController, c.OriginalRootPartPid,
                    c.OriginalPreSnapshot, c.OriginalPrePoint, c);
            }

            bool sameVessel = newWatchedPid == previousWatchedPid;
            if (!sameVessel && !sides.ContainsKey(newWatchedPid))
                return result;

            ParsekFlight.PostSwitchStructuralSplit watched;
            sides.TryGetValue(newWatchedPid, out watched);
            for (int i = 0; i < order.Count; i++)
            {
                uint pid = order[i];
                if (pid == newWatchedPid)
                    continue;
                // A same-vessel re-arm carries only the separated vessels, never a side that
                // was only ever a watched (original) vessel.
                if (sameVessel && !seenChildren.Contains(pid))
                    continue;
                var side = sides[pid];
                result.Add(new ParsekFlight.PostSwitchStructuralSplit
                {
                    ChildPid = pid,
                    HasController = side.HasController,
                    Cause = side.Cause,
                    UT = side.UT,
                    ChildRootPartPid = side.ChildRootPartPid,
                    PreSnapshot = side.PreSnapshot,
                    PrePoint = side.PrePoint,
                    Source = sameVessel ? side.Source : side.Source + "+focus-moved",
                    OriginalPid = newWatchedPid,
                    OriginalHasController = watched != null && watched.HasController,
                    OriginalRootPartPid = watched != null ? watched.ChildRootPartPid : 0u,
                    OriginalPreSnapshot = watched != null ? watched.PreSnapshot : null,
                    OriginalPrePoint = watched != null ? watched.PrePoint : null,
                });
            }
            return result;
        }

        // One vessel of a captured split with its own split-time data (held in the Child*
        // fields of a side record); the first record of a pid wins.
        private static void AddSide(
            List<uint> order,
            Dictionary<uint, ParsekFlight.PostSwitchStructuralSplit> sides,
            uint pid, bool hasController, uint rootPartPid,
            ConfigNode snapshot, TrajectoryPoint? point,
            ParsekFlight.PostSwitchStructuralSplit from)
        {
            if (pid == 0u || sides.ContainsKey(pid))
                return;
            order.Add(pid);
            sides[pid] = new ParsekFlight.PostSwitchStructuralSplit
            {
                ChildPid = pid,
                HasController = hasController,
                ChildRootPartPid = rootPartPid,
                PreSnapshot = snapshot,
                PrePoint = point,
                Cause = from.Cause,
                UT = from.UT,
                Source = from.Source,
            };
        }

        internal static bool IsWatchedVesselTrackedInBackground(
            IReadOnlyDictionary<uint, string> backgroundMap, uint watchedPid)
        {
            return backgroundMap != null && watchedPid != 0u && backgroundMap.ContainsKey(watchedPid);
        }

        internal static bool WatchReplaceDropsCaptures(int capturedCount, int carriedCount)
        {
            return capturedCount > 0 && carriedCount == 0;
        }

        internal void OnDecoupleNewVesselForPostSwitchWatch(Vessel originalVessel, Vessel newVessel)
        {
            if (host.postSwitchAutoRecord == null || newVessel == null)
                return;
            TryCapturePostSwitchStructuralSplit(
                originalVessel, newVessel, "DECOUPLE", "onPartDeCoupleNewVesselComplete");
        }

        /// <summary>
        /// Feeds the separations the watch captured to the crash coalescer under the recording
        /// that just started for the watched vessel, so each separated vessel gets its own child
        /// recording from the split time. Returns how many were fed.
        /// </summary>
        private int FeedPostSwitchStructuralSplits(
            List<ParsekFlight.PostSwitchStructuralSplit> splits, string reason)
        {
            if (splits == null || splits.Count == 0)
                return 0;
            if (host.activeTree == null || string.IsNullOrEmpty(host.activeTree.ActiveRecordingId)
                || host.crashCoalescer == null)
            {
                ParsekLog.Warn("Flight",
                    $"Post-switch watch: {splits.Count} captured separation(s) not fed ({reason}): " +
                    $"tree={(host.activeTree != null)} activeRec={host.activeTree?.ActiveRecordingId ?? "(none)"}");
                return 0;
            }

            string parentRecordingId = host.activeTree.ActiveRecordingId;
            uint recordedPid = host.recorder != null ? host.recorder.RecordingVesselId : 0u;
            var fedPids = new HashSet<uint>();
            int fed = 0;
            int refused = 0;
            for (int i = 0; i < splits.Count; i++)
            {
                var split = splits[i];
                // The coalescer keeps a list, so one vessel fed twice would get two child
                // recordings; the recorded vessel is the parent, never its own child.
                if (split == null || split.ChildPid == 0u || split.ChildPid == recordedPid
                    || !fedPids.Add(split.ChildPid))
                {
                    refused++;
                    continue;
                }
                fed++;
                host.crashCoalescer.OnSplitEvent(
                    split.UT, split.ChildPid, split.HasController,
                    splitCause: split.Cause,
                    preSnapshot: split.PreSnapshot,
                    preTrajectoryPoint: split.PrePoint,
                    decouplerPartId: string.Equals(split.Cause, "DECOUPLE", StringComparison.Ordinal)
                        ? split.ChildRootPartPid : 0u);
            }
            ParsekLog.Info("Flight",
                $"Post-switch watch: fed {fed} captured separation(s) to the coalescer ({reason}): " +
                $"parentRec={parentRecordingId} recordedPid={recordedPid} refusedDuplicates={refused} " +
                $"firstUT={splits[0].UT.ToString("F2", CultureInfo.InvariantCulture)}");
            return fed;
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

            if (decision == ParsekFlight.PostSwitchAutoRecordStartDecision.StartFreshRecording
                && host.activeTree != null)
            {
                RecordingTree matchedTree;
                bool matched = ParsekFlight.TryFindCommittedTreeMatchingVessel(
                    v.persistentId,
                    v.id != Guid.Empty ? v.id.ToString("N") : null,
                    out matchedTree);
                if (IsPostSwitchGraftOfSeparateCommittedVessel(
                        decision, hasActiveTree: true, matched, matchedTree?.Id, host.activeTree.Id))
                {
                    ParsekLog.Info("Flight", FormatPostSwitchGraftRefusal(
                        v.vesselName, v.persistentId, trigger, matchedTree?.Id, host.activeTree.Id));
                    DisarmPostSwitchAutoRecord("separate-committed-vessel");
                    return false;
                }
            }

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
                    DropPredictedEndingOfEndedRecording(v, currentUT);
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
                v != null && GhostMapPresence.IsGhostOrDroneVessel(v.persistentId),
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
                // Another path already started the watched vessel's recording: the captured
                // separations still branch off it.
                if (suppression == ParsekFlight.PostSwitchAutoRecordSuppressionReason.AlreadyRecording
                    && state.PendingStructuralSplits.Count > 0
                    && host.recorder != null && host.recorder.RecordingVesselId == state.VesselPid)
                {
                    FeedPostSwitchStructuralSplits(
                        new List<ParsekFlight.PostSwitchStructuralSplit>(state.PendingStructuralSplits),
                        "watched vessel already recording");
                    state.PendingStructuralSplits.Clear();
                }
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

            // A separation of the watched vessel is a modification on its own: start now, past
            // the baseline and settle gates, and branch the separated vessels off the recording.
            if (state.PendingStructuralSplits.Count > 0)
            {
                var splits = new List<ParsekFlight.PostSwitchStructuralSplit>(state.PendingStructuralSplits);
                LogPostSwitchAutoRecordDecision(
                    state,
                    activePid,
                    suppression,
                    waitingForSettle: false,
                    currentUt: currentUT,
                    trigger: ParsekFlight.PostSwitchAutoRecordTrigger.StructuralSplit);
                // Taken off the state first: the recorder start disarms the watch.
                state.PendingStructuralSplits.Clear();
                if (TryStartPostSwitchAutoRecord(
                        v, state, ParsekFlight.PostSwitchAutoRecordTrigger.StructuralSplit, currentUT))
                {
                    FeedPostSwitchStructuralSplits(splits, "post-switch structural split start");
                }
                else
                {
                    // A refused start (the restore-and-promote seam) refuses every frame, so
                    // retrying would only repeat it: nothing records the watched vessel.
                    ParsekLog.Warn("Flight",
                        $"Post-switch watch: recording did not start; " +
                        $"{splits.Count} captured separation(s) not recorded (pid={state.VesselPid})");
                }
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
            if (hasActiveVessel && CameraDrone.Is(FlightGlobals.ActiveVessel))
            {
                ParsekLog.VerboseRateLimited("FarWatch", "deferred-autorecord-eva-drone",
                    "HandleDeferredAutoRecordEva: skipped while the far-watch camera drone is active");
                return;
            }
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

        /// <summary>
        /// Pure: whether the player (or an autopilot acting for them) commanded a rotation of the
        /// vessel since the baseline: SAS switched on or off, another SAS mode selected, the
        /// player's raw rotation input, or, with SAS off, a rotation on the vessel's own control
        /// state (keys, trim, an autopilot mod, kRPC). With SAS on the control state also
        /// carries SAS's own corrections, which only hold the attitude, so it is not read then.
        /// </summary>
        internal static bool IsPlayerRotationCommand(
            bool sasEnabledAtBaseline, int sasModeAtBaseline,
            bool sasEnabledNow, int sasModeNow,
            float inputPitch, float inputYaw, float inputRoll,
            float ctrlPitch, float ctrlYaw, float ctrlRoll)
        {
            if (sasEnabledNow != sasEnabledAtBaseline)
                return true;
            if (sasEnabledNow && sasModeNow != sasModeAtBaseline)
                return true;
            if (IsRotationAxisCommanded(inputPitch, inputYaw, inputRoll))
                return true;
            return !sasEnabledNow && IsRotationAxisCommanded(ctrlPitch, ctrlYaw, ctrlRoll);
        }

        private static bool IsRotationAxisCommanded(float pitch, float yaw, float roll)
        {
            return Math.Abs(pitch) > PostSwitchRotationCommandEpsilon
                || Math.Abs(yaw) > PostSwitchRotationCommandEpsilon
                || Math.Abs(roll) > PostSwitchRotationCommandEpsilon;
        }

        /// <summary>
        /// Pure: the vessel crossed into another body's sphere of influence since the baseline.
        /// </summary>
        internal static bool IsPassiveSoiTransition(
            ParsekFlight.PostSwitchOrbitSnapshot baseline,
            ParsekFlight.PostSwitchOrbitSnapshot current)
        {
            return baseline.IsValid && current.IsValid
                && !string.Equals(baseline.BodyName, current.BodyName, StringComparison.Ordinal);
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

        /// <summary>
        /// Pure: a fresh post-switch recording under a live tree attaches the vessel to that
        /// tree (a <c>Launch</c> child of the recording the player left). For a vessel of a
        /// separately committed tree that is a second history for it and ends its own mission
        /// (owner ruling Q16: it continues its own mission through the committed-tree clone, which
        /// needs the live tree committed first, the pre-switch Case C). The vessel cycle and the
        /// map Switch-To take Case C before switching; this refuses the graft for any switch that
        /// still reaches the watcher (a press while another merge dialog is open, a switch stock
        /// makes itself after a crash). A vessel of the live tree's own committed original is not
        /// separate.
        /// </summary>
        internal static bool IsPostSwitchGraftOfSeparateCommittedVessel(
            ParsekFlight.PostSwitchAutoRecordStartDecision decision,
            bool hasActiveTree,
            bool matchedCommittedTree,
            string matchedTreeId,
            string activeTreeId)
        {
            if (decision != ParsekFlight.PostSwitchAutoRecordStartDecision.StartFreshRecording
                || !hasActiveTree || !matchedCommittedTree || string.IsNullOrEmpty(matchedTreeId))
                return false;
            return !string.Equals(matchedTreeId, activeTreeId, StringComparison.Ordinal);
        }

        internal static string FormatPostSwitchGraftRefusal(
            string vesselName, uint pid, ParsekFlight.PostSwitchAutoRecordTrigger trigger,
            string matchedTreeId, string activeTreeId)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Post-switch fresh start refused: vessel='{0}' pid={1} trigger={2} " +
                "reason=separate-committed-vessel matchedTreeId={3} activeTreeId={4} - " +
                "not attached to the live tree; it continues its own mission once the live tree is committed",
                vesselName ?? "<unnamed>", pid, trigger,
                matchedTreeId ?? "<null>", activeTreeId ?? "<null>");
        }
    }
}
