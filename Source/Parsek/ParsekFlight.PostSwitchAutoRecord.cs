using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // Post-switch auto-record: the watch armed after a vessel switch, its baseline and triggers,
    // and the decision to start recording.
    public partial class ParsekFlight
    {
        internal enum PostSwitchAutoRecordStartDecision
        {
            None,
            PromoteTrackedRecording,
            RestoreAndPromoteTrackedRecording,
            StartFreshRecording
        }

        internal enum PostSwitchAutoRecordArmDecision
        {
            None,
            ArmTrackedBackgroundMember,
            ArmOutsider
        }

        internal enum PostSwitchAutoRecordTrigger
        {
            None,
            EngineActivity,
            SustainedRcsActivity,
            AttitudeChange,
            CrewChange,
            ResourceChange,
            PartStateChange,
            LandedMotion,
            OrbitChange
        }

        internal enum PostSwitchAutoRecordSuppressionReason
        {
            None,
            Disabled,
            AlreadyRecording,
            NoActiveVessel,
            ActiveVesselMismatch,
            GhostMapVessel,
            RestoreInProgress,
            PendingTransition,
            WarpActive,
            PackedOrOnRails
        }

        internal struct PostSwitchOrbitSnapshot
        {
            public bool IsValid;
            public string BodyName;
            public double SemiMajorAxis;
            public double Eccentricity;
            public double Inclination;
            public double LongitudeOfAscendingNode;
            public double ArgumentOfPeriapsis;
        }

        private sealed class PostSwitchAutoRecordState
        {
            public uint VesselPid;
            public string VesselName;
            public double ArmedAtUt;
            public string ArmedReason;
            public bool TrackedInActiveTree;
            public string FreshStartParentRecordingId;
            public bool BaselineCaptured;
            public double BaselineCapturedUt;
            public double ComparisonsReadyUt;
            public double NextManifestEvaluationUt;
            public Vector3d BaselineWorldPosition;
            public Quaternion BaselineWorldRotation;
            public TrajectoryPoint? BaselineSeedPoint;
            public PostSwitchOrbitSnapshot BaselineOrbit;
            public double AttitudeThresholdExceededAtUt = double.NaN;
            public Dictionary<string, ResourceAmount> BaselineResources;
            public Dictionary<string, InventoryItem> BaselineInventory;
            public Dictionary<string, int> BaselineCrew;
            public HashSet<string> BaselinePartStateTokens = new HashSet<string>();
            public bool ModuleCachesDirty;
            public int CachedPartCount;
            public List<(Part part, ModuleEngines engine, int moduleIndex)> CachedEngines
                = new List<(Part, ModuleEngines, int)>();
            public HashSet<ulong> ActiveEngineKeys = new HashSet<ulong>();
            public Dictionary<ulong, float> EngineThrottleMap = new Dictionary<ulong, float>();
            public List<(Part part, ModuleRCS rcs, int moduleIndex)> CachedRcsModules
                = new List<(Part, ModuleRCS, int)>();
            public Dictionary<ulong, int> RcsFrameCountMap = new Dictionary<ulong, int>();
            public HashSet<ulong> ActiveRcsKeys = new HashSet<ulong>();
            public Dictionary<ulong, float> RcsThrottleMap = new Dictionary<ulong, float>();
        }

        private void ClearPendingEvaAutoRecordState()
        {
            pendingAutoRecord = false;
        }

        internal static AutoRecordLaunchDecision EvaluateAutoRecordLaunchDecision(
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
                return AutoRecordLaunchDecision.SkipAlreadyRecording;

            // Time-jump transient suppression is checked before the active-vessel guard
            // so the skip decision (and its INFO log) fires for any vessel whose situation
            // changes during the jump window — including synthetic spawn vessels that the
            // playback policy materializes during a Real Spawn Control / Timeline FF jump.
            // The original #526 bug was an active-vessel `PRELAUNCH -> FLYING` flicker, but
            // labelling non-active flickers as `SkipInactiveVessel` hid the time-jump origin
            // and made the in-game canaries unable to confirm the suppression branch fired.
            if (suppressForTimeJumpTransient)
                return AutoRecordLaunchDecision.SkipTimeJumpTransient;

            if (!isActiveVessel)
                return AutoRecordLaunchDecision.SkipInactiveVessel;

            if (fromSituation == Vessel.Situations.PRELAUNCH)
            {
                return autoRecordOnLaunchEnabled
                    ? AutoRecordLaunchDecision.StartFromPrelaunch
                    : AutoRecordLaunchDecision.SkipDisabled;
            }

            if (fromSituation == Vessel.Situations.LANDED)
            {
                double settledTime = lastLandedUt >= 0
                    ? currentUt - lastLandedUt
                    : 0;
                if (settledTime < landedSettleThreshold)
                    return AutoRecordLaunchDecision.SkipBounce;

                return autoRecordOnLaunchEnabled
                    ? AutoRecordLaunchDecision.StartFromSettledLanded
                    : AutoRecordLaunchDecision.SkipDisabled;
            }

            return AutoRecordLaunchDecision.SkipNotLaunchTransition;
        }

        // Decision for the first-staging-on-pad auto-record trigger (OnStageActivate).
        // Mirrors EvaluateAutoRecordLaunchDecision's guard ordering: an already-running
        // recorder short-circuits first, then the time-jump transient suppression, so a
        // synthetic spawn vessel staging during an FF / warp-to-time window cannot
        // spuriously start a recording. Only fires while the active vessel is PRELAUNCH
        // (still on the pad); in-flight staging is left to the existing recorder, and the
        // normal clamp-release launch is still covered by the PRELAUNCH->FLYING trigger
        // (whichever fires first wins via the isRecording guard).
        internal static AutoRecordStagingDecision EvaluateAutoRecordStagingDecision(
            bool isRecording,
            bool hasActiveVessel,
            bool activeVesselIsPrelaunch,
            bool autoRecordOnLaunchEnabled,
            bool suppressForTimeJumpTransient)
        {
            if (isRecording)
                return AutoRecordStagingDecision.SkipAlreadyRecording;

            if (suppressForTimeJumpTransient)
                return AutoRecordStagingDecision.SkipTimeJumpTransient;

            if (!hasActiveVessel)
                return AutoRecordStagingDecision.SkipNoActiveVessel;

            if (!activeVesselIsPrelaunch)
                return AutoRecordStagingDecision.SkipNotOnPad;

            if (!autoRecordOnLaunchEnabled)
                return AutoRecordStagingDecision.SkipDisabled;

            return AutoRecordStagingDecision.StartFromPrelaunchStaging;
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

        internal static PostSwitchAutoRecordArmDecision EvaluatePostSwitchAutoRecordArmDecision(
            bool shouldArm,
            bool trackedInActiveTree,
            bool newVesselIsEva)
        {
            if (!shouldArm)
                return PostSwitchAutoRecordArmDecision.None;

            // A tracked background member is an existing tree recording: switching back
            // to it should promote it to foreground on the first modification, whether
            // or not it is an EVA kerbal. (An EVA kerbal re-controlled after a vessel
            // switch lives here; without this it stays background-tracked and the
            // missed-vessel-switch recovery loops every frame, never promoting.)
            if (trackedInActiveTree)
                return PostSwitchAutoRecordArmDecision.ArmTrackedBackgroundMember;

            // Outsider (not in any tree): a fresh EVA kerbal is owned by the dedicated
            // OnCrewOnEva / EVA-branch + "Auto-record on EVA" path, so do not also arm
            // an outsider standalone recording for it. Non-EVA outsiders still arm.
            return newVesselIsEva
                ? PostSwitchAutoRecordArmDecision.None
                : PostSwitchAutoRecordArmDecision.ArmOutsider;
        }

        internal static PostSwitchAutoRecordSuppressionReason EvaluatePostSwitchAutoRecordSuppression(
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
                return PostSwitchAutoRecordSuppressionReason.Disabled;
            if (isRecording)
                return PostSwitchAutoRecordSuppressionReason.AlreadyRecording;
            if (!hasActiveVessel)
                return PostSwitchAutoRecordSuppressionReason.NoActiveVessel;
            if (activeVesselPid == 0 || activeVesselPid != armedVesselPid)
                return PostSwitchAutoRecordSuppressionReason.ActiveVesselMismatch;
            if (activeVesselIsGhost)
                return PostSwitchAutoRecordSuppressionReason.GhostMapVessel;
            if (isRestoringActiveTree)
                return PostSwitchAutoRecordSuppressionReason.RestoreInProgress;
            if (hasPendingTransition)
                return PostSwitchAutoRecordSuppressionReason.PendingTransition;
            if (isWarpActive)
                return PostSwitchAutoRecordSuppressionReason.WarpActive;
            if (activeVesselPacked)
                return PostSwitchAutoRecordSuppressionReason.PackedOrOnRails;
            return PostSwitchAutoRecordSuppressionReason.None;
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
            PostSwitchAutoRecordSuppressionReason suppression,
            bool baselineCaptured,
            bool waitingForSettle,
            PostSwitchAutoRecordTrigger trigger)
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
            PostSwitchAutoRecordSuppressionReason suppression,
            bool waitingForSettle,
            double comparisonsReadyUt,
            double currentUt,
            PostSwitchAutoRecordTrigger trigger)
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
            PostSwitchAutoRecordState state,
            uint activePid,
            PostSwitchAutoRecordSuppressionReason suppression,
            bool waitingForSettle,
            double currentUt,
            PostSwitchAutoRecordTrigger trigger)
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
            return pendingTreeDockMerge
                || pendingSplitRecorder != null
                || pendingSplitInProgress
                || pendingBoardingTargetPid != 0
                || pendingDockMergedPid != 0
                || (recorder != null && recorder.ChainToVesselPending);
        }

        private void ArmPostSwitchAutoRecord(
            Vessel vessel,
            string armReason,
            bool trackedInActiveTree,
            string freshStartParentRecordingId)
        {
            if (vessel == null)
                return;

            postSwitchAutoRecord = new PostSwitchAutoRecordState
            {
                VesselPid = vessel.persistentId,
                VesselName = vessel.vesselName ?? "Unknown",
                ArmedAtUt = Planetarium.GetUniversalTime(),
                ArmedReason = armReason,
                TrackedInActiveTree = trackedInActiveTree,
                FreshStartParentRecordingId = freshStartParentRecordingId
            };

            ParsekLog.Info("Flight",
                $"Post-switch auto-record armed: vessel='{postSwitchAutoRecord.VesselName}' " +
                $"pid={postSwitchAutoRecord.VesselPid} tracked={trackedInActiveTree} " +
                $"reason={armReason}");
        }

        private void DisarmPostSwitchAutoRecord(string reason)
        {
            if (postSwitchAutoRecord == null)
                return;

            ParsekLog.Info("Flight",
                $"Post-switch auto-record disarmed: vessel='{postSwitchAutoRecord.VesselName}' " +
                $"pid={postSwitchAutoRecord.VesselPid} reason={reason}");
            postSwitchAutoRecord = null;
        }

        // ---------------------------------------------------------------------
        // End Phase C: stock-action intent consume.
        // ---------------------------------------------------------------------

        private static double NormalizeAngleDeltaDegrees(double a, double b)
        {
            double delta = Math.Abs(a - b) % 360.0;
            return delta > 180.0 ? 360.0 - delta : delta;
        }

        private static PostSwitchOrbitSnapshot CapturePostSwitchOrbitSnapshot(Vessel v)
        {
            if (v == null || v.orbit == null || v.mainBody == null)
                return default;

            return new PostSwitchOrbitSnapshot
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
            PostSwitchAutoRecordState state,
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
            PostSwitchAutoRecordState state,
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
                    ? currentUT + LandedSettleThreshold
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

        private PostSwitchAutoRecordTrigger EvaluatePostSwitchAutoRecordTrigger(
            Vessel v,
            PostSwitchAutoRecordState state,
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
            PostSwitchAutoRecordState state,
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
            PostSwitchAutoRecordState state,
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
            PostSwitchAutoRecordState state,
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
            PostSwitchAutoRecordState state,
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
            PostSwitchAutoRecordState state,
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
            PostSwitchAutoRecordState state)
        {
            double distanceDelta = Vector3d.Distance(state.BaselineWorldPosition, v.GetWorldPos3D());
            return (v.situation == Vessel.Situations.LANDED || v.situation == Vessel.Situations.SPLASHED)
                && HasMeaningfulLandedMotionChange(distanceDelta, v.srfSpeed);
        }

        private static bool EvaluatePostSwitchOrbitChange(
            Vessel v,
            PostSwitchAutoRecordState state)
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
            PostSwitchAutoRecordState state,
            double currentUT)
        {
            if (activeTree == null || v == null)
                return;

            string recordingId = Guid.NewGuid().ToString("N");
            var recording = new Recording
            {
                RecordingId = recordingId,
                TreeId = activeTree.Id,
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

            activeTree.AddOrReplaceRecording(recording);
            activeTree.ActiveRecordingId = recordingId;
            activeTree.BackgroundMap.Remove(v.persistentId);

            if (!string.IsNullOrEmpty(state.FreshStartParentRecordingId)
                && activeTree.Recordings.TryGetValue(state.FreshStartParentRecordingId, out var parent))
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
                activeTree.BranchPoints.Add(branchPoint);
            }
        }

        private void SeedPostSwitchAlignmentWindow(Vessel v, PostSwitchAutoRecordState state)
        {
            if (v == null || state == null || recorder == null || !recorder.IsRecording)
                return;

            if (state.BaselineSeedPoint.HasValue)
            {
                recorder.SeedTrajectoryPoint(
                    state.BaselineSeedPoint.Value,
                    v,
                    "post-switch attitude baseline");
            }

            recorder.SamplePosition(v);
            ParsekLog.Verbose("Flight",
                $"Post-switch alignment window seeded: pid={state.VesselPid} " +
                $"baselineUT={state.BaselineCapturedUt:F2} currentUT={Planetarium.GetUniversalTime():F2}");
        }

        private bool TryStartPostSwitchAutoRecord(
            Vessel v,
            PostSwitchAutoRecordState state,
            PostSwitchAutoRecordTrigger trigger,
            double currentUT)
        {
            bool activeVesselTrackedInBackground = activeTree != null
                && activeTree.BackgroundMap.ContainsKey(v.persistentId);
            var decision = EvaluatePostSwitchAutoRecordStartDecision(
                state.VesselPid,
                v.persistentId,
                activeTree != null,
                activeVesselTrackedInBackground,
                canRestorePendingTrackedTree: false,
                suppressStart: false);

            ParsekLog.Info("Flight",
                $"Post-switch trigger accepted: vessel='{v.vesselName}' pid={v.persistentId} " +
                $"trigger={trigger} decision={decision}");

            switch (decision)
            {
                case PostSwitchAutoRecordStartDecision.PromoteTrackedRecording:
                    if (activeTree != null
                        && activeTree.BackgroundMap.TryGetValue(v.persistentId, out string recordingId))
                    {
                        backgroundRecorder?.OnVesselRemovedFromBackground(v.persistentId);
                        PromoteRecordingFromBackground(recordingId, v);
                    }
                    break;

                case PostSwitchAutoRecordStartDecision.RestoreAndPromoteTrackedRecording:
                    ParsekLog.Verbose("Flight",
                        "Post-switch trigger hit restore-and-promote seam, but #534 restore wiring is gated on this branch");
                    return false;

                case PostSwitchAutoRecordStartDecision.StartFreshRecording:
                    PrepareActiveTreeForFreshPostSwitchRecording(v, state, currentUT);
                    StartRecording(suppressStartScreenMessage: true);
                    if (IsRecording)
                    {
                        Log($"Auto-record started (post-switch {trigger})");
                        ScreenMessage("Recording STARTED (auto - post switch)", 2f);
                    }
                    break;
            }

            if (!IsRecording)
                return false;

            if (trigger == PostSwitchAutoRecordTrigger.AttitudeChange)
                SeedPostSwitchAlignmentWindow(v, state);

            DisarmPostSwitchAutoRecord($"recording started via {trigger}");
            return true;
        }

        internal void OnPostSwitchAutoRecordPhysicsFrame(Vessel v)
        {
            PostSwitchAutoRecordState state = postSwitchAutoRecord;
            if (state == null)
                return;

            uint activePid = v != null ? v.persistentId : 0;
            double currentUT = Planetarium.GetUniversalTime();
            var suppression = EvaluatePostSwitchAutoRecordSuppression(
                ParsekSettings.Current?.autoRecordOnFirstModificationAfterSwitch != false,
                IsRecording,
                v != null,
                activePid,
                state.VesselPid,
                v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId),
                restoringActiveTree,
                HasPendingPostSwitchAutoRecordTransition(),
                IsAnyWarpActive(),
                v != null && v.packed);

            if (suppression == PostSwitchAutoRecordSuppressionReason.Disabled
                || suppression == PostSwitchAutoRecordSuppressionReason.AlreadyRecording)
            {
                LogPostSwitchAutoRecordDecision(
                    state,
                    activePid,
                    suppression,
                    waitingForSettle: false,
                    currentUt: currentUT,
                    trigger: PostSwitchAutoRecordTrigger.None);
                DisarmPostSwitchAutoRecord($"suppressed by {suppression}");
                return;
            }

            if (suppression != PostSwitchAutoRecordSuppressionReason.None)
            {
                LogPostSwitchAutoRecordDecision(
                    state,
                    activePid,
                    suppression,
                    waitingForSettle: false,
                    currentUt: currentUT,
                    trigger: PostSwitchAutoRecordTrigger.None);
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
                    trigger: PostSwitchAutoRecordTrigger.None);
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
                    trigger: PostSwitchAutoRecordTrigger.None);
                return;
            }

            PostSwitchAutoRecordTrigger trigger =
                EvaluatePostSwitchAutoRecordTrigger(v, state, currentUT);
            LogPostSwitchAutoRecordDecision(
                state,
                activePid,
                suppression,
                waitingForSettle: false,
                currentUt: currentUT,
                trigger: trigger);
            if (trigger == PostSwitchAutoRecordTrigger.None)
                return;

            TryStartPostSwitchAutoRecord(v, state, trigger, currentUT);
        }

        /// <summary>
        /// Handles deferred auto-record for EVA: starts recording when vessel switch
        /// to the EVA kerbal is complete.
        /// </summary>
        private void HandleDeferredAutoRecordEva()
        {
            bool hasActiveVessel = FlightGlobals.ActiveVessel != null;
            if (!ShouldStartDeferredAutoRecordEva(
                pendingAutoRecord,
                IsRecording,
                hasActiveVessel,
                activeVesselIsEva: hasActiveVessel && FlightGlobals.ActiveVessel.isEVA))
                return;

            uint activePid = FlightGlobals.ActiveVessel?.persistentId ?? 0u;
            string invalidHeadReason;
            if (!CanStartRecorderWithActiveTreeHead(activeTree, activePid, out invalidHeadReason))
            {
                ParsekLog.WarnRateLimited(
                    "Flight",
                    BuildInvalidActiveTreeHeadRateLimitKey(
                        "deferred-eva",
                        activeTree,
                        activePid,
                        invalidHeadReason),
                    $"HandleDeferredAutoRecordEva: clearing pending auto-record because active tree head " +
                    $"cannot accept a recorder (reason={invalidHeadReason}, activePid={activePid}, " +
                    $"tree={activeTree?.Id ?? "null"}, activeRec={activeTree?.ActiveRecordingId ?? "null"})");
                ClearPendingEvaAutoRecordState();
                ScreenMessage("Recording blocked (invalid tree head)", 2f);
                return;
            }

            StartRecording(suppressStartScreenMessage: true);

            if (IsRecording)
            {
                pendingAutoRecord = false;
                Log("Auto-record started (EVA from pad)");
                ScreenMessage("Recording STARTED (auto - EVA from pad)", 2f);
            }
            // else: StartRecording failed (paused game or no active vessel).
            // Keep pending flags so we retry next frame; fall through to
            // HandleInput/playback so they aren't starved.
        }
    }
}
