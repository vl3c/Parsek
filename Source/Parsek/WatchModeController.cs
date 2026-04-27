using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens.Mapview;
using UnityEngine;

namespace Parsek
{
    internal enum WatchCameraMode { Free, HorizonLocked }
    internal enum HorizonForwardSource
    {
        ProjectedVelocity,
        LastForwardFallback,
        ArbitraryPerpendicularFallback
    }

    internal enum OverlapBridgeRetargetState
    {
        None = 0,
        KeepBridge = 1,
        RetargetToPrimary = 2,
        ExitWatch = 3,
    }

    internal struct WatchCameraTransitionState
    {
        public float Distance;
        public float Pitch;
        public float Heading;
        public WatchCameraMode Mode;
        public bool UserModeOverride;
        public bool HasTargetRotation;
        public Quaternion TargetRotation;
        public bool HasWorldOrbitDirection;
        public Vector3 WorldOrbitDirection;
    }

    /// <summary>
    /// Owns camera-follow (watch mode) state and methods.
    /// Extracted from ParsekFlight to isolate watch-mode lifecycle.
    /// Watch mode locks the flight camera onto a ghost vessel, tracking it
    /// through loop/overlap cycle transitions, and restoring the camera
    /// to the active vessel on exit.
    /// </summary>
    // [ERS-exempt — Phase 3] WatchModeController tracks watchedRecordingIndex /
    // lineageProtectionRecordingIndex as indices into RecordingStore.CommittedRecordings
    // and passes the raw list to GhostPlaybackLogic helpers that bounds-check
    // against the same index space. Routing through EffectiveState.ComputeERS()
    // here would de-align indices and break camera lineage protection across
    // warp and chain transitions.
    // TODO(phase 6+): migrate WatchModeController to recording-id-keyed lineage tracking.
    internal class WatchModeController
    {
        private readonly ParsekFlight host;

        internal const string WatchModeLockId = "ParsekWatch";
        internal const ControlTypes WatchModeLockMask =
            ControlTypes.STAGING | ControlTypes.THROTTLE |
            ControlTypes.VESSEL_SWITCHING | ControlTypes.EVA_INPUT |
            ControlTypes.CAMERAMODES;
        // Manual watch entry keeps the legacy 300km affordance. Exit adds a
        // 5km hysteresis band so a target that transfers/rebuilds near the cutoff
        // does not enter and leave watch on the same frame.
        internal const float WatchEnterCutoffMeters = 300_000f;
        internal const float WatchExitCutoffMeters = 305_000f;
        // Watch-mode tunables (entry camera defaults, pending-bridge budget) live
        // in ParsekConfig.cs under WatchMode.*. WatchModeLockId / WatchModeLockMask
        // are KSP ControlLocks identifiers and stay local.
        internal static Func<float> RealtimeNow = GetRealtimeSafe;
        internal static Func<double> CurrentUTNow = GetCurrentUTSafe;
        internal static Func<float> CurrentWarpRateNow = GetCurrentWarpRateSafe;

        // Camera follow state — transient, never serialized
        private int watchedRecordingIndex = -1;       // -1 = not watching
        private string watchedRecordingId;             // stable across index shifts
        private float watchStartTime;                  // Time.time when watch mode was entered
        private long watchedOverlapCycleIndex = -1;    // which overlap cycle the camera is following (-1 = ready for next, -2 = holding after explosion)
        private double overlapRetargetAfterUT = -1;    // delay re-target after watched cycle explodes
        private GameObject overlapCameraAnchor;        // temp anchor so FlightCamera doesn't reference destroyed ghost
        private int overlapBridgeLastRetryFrame = -1;  // prevent double-counting bridge retries within one frame
        private Vessel savedCameraVessel;
        private float savedCameraDistance;
        private float savedCameraPitch;
        private float savedCameraHeading;
        private float watchEndHoldUntilRealTime = -1;  // non-looped end hold timer
        private float watchEndHoldMaxRealTime = -1;
        private double watchEndHoldPendingActivationUT = double.NaN;
        private int lineageProtectionRecordingIndex = -1; // post-watch debris visibility root
        private string lineageProtectionRecordingId;
        private double lineageProtectionUntilUT = double.NaN;
        private float savedPivotSharpness = 0.5f;
        private int watchNoTargetFrames;               // consecutive frames with no valid camera target (safety net)

        // Horizon-locked camera mode state
        private WatchCameraMode currentCameraMode = WatchCameraMode.Free;
        private bool userModeOverride;  // true when user pressed toggle; cleared on EnterWatchMode
        private bool hasRememberedFreeCameraState;
        private WatchCameraTransitionState rememberedFreeCameraState;
        private bool hasRememberedHorizonLockedCameraState;
        private WatchCameraTransitionState rememberedHorizonLockedCameraState;

        // Lazy-initialized GUI styles for the watch mode overlay
        private GUIStyle watchOverlayStyle;
        private GUIStyle watchOverlayHintStyle;
        private string lastLoggedWatchTargetMismatch;
        private string lastLoggedWatchFocusKey;
        private string lastLoggedHorizonVectorKey;
        private bool lastMapViewEnabled;
        private bool pendingMapFocusRestore;
        internal bool ensureGhostOrbitRenderersAttempted;

        internal WatchModeController(ParsekFlight host)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
        }

        // === Properties for external callers ===

        internal bool IsWatchingGhost => watchedRecordingIndex >= 0;
        internal int WatchedRecordingIndex => watchedRecordingIndex;
        internal int WatchProtectionRecordingIndex => ResolveWatchProtectionRecordingIndex();
        internal long WatchedLoopCycleIndex => watchedOverlapCycleIndex;
        internal WatchCameraMode CurrentCameraMode => currentCameraMode;
        internal void ExitWatchModePreservingLineage(bool skipCameraRestore = false) =>
            ExitWatchMode(skipCameraRestore, preserveLineageProtection: true);

        private int ResolveWatchProtectionRecordingIndex()
        {
            if (watchedRecordingIndex >= 0)
                return watchedRecordingIndex;

            RefreshLineageProtection();
            return lineageProtectionRecordingIndex;
        }

        private void RefreshLineageProtection()
        {
            if (lineageProtectionRecordingIndex < 0)
                return;

            if (Planetarium.fetch != null
                && !double.IsNaN(lineageProtectionUntilUT)
                && Planetarium.GetUniversalTime() > lineageProtectionUntilUT)
            {
                ClearLineageProtection(
                    $"Lineage protection expired for recording #{lineageProtectionRecordingIndex} at UT {lineageProtectionUntilUT:F1}");
            }
        }

        private void ClearLineageProtection(string logMessage = null)
        {
            if (!string.IsNullOrEmpty(logMessage))
                ParsekLog.Info("CameraFollow", logMessage);

            lineageProtectionRecordingIndex = -1;
            lineageProtectionRecordingId = null;
            lineageProtectionUntilUT = double.NaN;
        }

        private void PreserveLineageProtectionOnExit()
        {
            var committed = RecordingStore.CommittedRecordings;
            if (watchedRecordingIndex < 0 || watchedRecordingIndex >= committed.Count)
            {
                ClearLineageProtection();
                return;
            }

            double currentUT = Planetarium.fetch != null
                ? Planetarium.GetUniversalTime()
                : committed[watchedRecordingIndex].EndUT;
            double protectionUntilUT = GhostPlaybackLogic.ComputeWatchLineageProtectionUntilUT(
                committed,
                RecordingStore.CommittedTrees,
                watchedRecordingIndex,
                currentUT);

            if (double.IsNaN(protectionUntilUT) || protectionUntilUT < currentUT)
            {
                ClearLineageProtection();
                return;
            }

            lineageProtectionRecordingIndex = watchedRecordingIndex;
            lineageProtectionRecordingId = committed[watchedRecordingIndex].RecordingId;
            lineageProtectionUntilUT = protectionUntilUT;
            ParsekLog.Info("CameraFollow",
                $"Retaining watched-lineage debris protection for #{watchedRecordingIndex} " +
                $"\"{committed[watchedRecordingIndex].VesselName}\" until UT {protectionUntilUT:F1}");
        }

        private static void RemoveWatchModeControlLockSafe()
        {
            try
            {
                InputLockManager.RemoveControlLock(WatchModeLockId);
            }
            catch (System.Security.SecurityException)
            {
                // Unit-test host does not provide the real KSP input manager.
            }
            catch (MethodAccessException)
            {
                // Same fallback for non-Unity unit-test environments.
            }
        }

        private static Vessel GetActiveVesselSafe()
        {
            try
            {
                return FlightGlobals.ActiveVessel;
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
            catch (MethodAccessException)
            {
                return null;
            }
        }

        private static bool IsUnityObjectAvailable(UnityEngine.Object obj)
        {
            if (ReferenceEquals(obj, null))
                return false;

            try
            {
                return obj != null;
            }
            catch (System.Security.SecurityException)
            {
                return true;
            }
            catch (MethodAccessException)
            {
                return true;
            }
        }

        private static void DestroyUnityObjectSafe(UnityEngine.Object obj)
        {
            if (!IsUnityObjectAvailable(obj))
                return;

            try
            {
                UnityEngine.Object.Destroy(obj);
            }
            catch (System.Security.SecurityException)
            {
                // Unit-test host may provide inert Unity objects without runtime backing.
            }
            catch (MethodAccessException)
            {
                // Same fallback for non-Unity unit-test environments.
            }
        }

        /// <summary>
        /// Returns true when an overlap cycle lifecycle event (ExplosionHoldStart / End)
        /// should be ignored because it concerns a cycle the user is not watching.
        /// Real cycle indices are >= 0; sentinel values (-1 ready-for-next, -2 holding)
        /// never match a real event cycle index, so they too are ignored and cannot be
        /// clobbered mid-flight. Relies on the invariant that
        /// <c>GhostPlaybackEngine.UpdateExpireAndPositionOverlaps</c> (the only emission
        /// site for these two action types, around line 1186) always populates
        /// <c>NewCycleIndex</c> from the expiring ghost's non-negative
        /// <c>loopCycleIndex</c>; a hypothetical negative emission would silently match
        /// a sentinel.
        /// </summary>
        internal static bool ShouldIgnoreOverlapCycleEvent(long eventCycleIndex, long watchedCycleIndex)
        {
            return eventCycleIndex != watchedCycleIndex;
        }

        private void DestroyOverlapCameraAnchor()
        {
            if (!IsUnityObjectAvailable(overlapCameraAnchor))
            {
                overlapCameraAnchor = null;
                return;
            }

            DestroyUnityObjectSafe(overlapCameraAnchor);
            overlapCameraAnchor = null;
        }

        private static float GetRealtimeSafe()
        {
            try
            {
                return Time.time;
            }
            catch (System.Security.SecurityException)
            {
                return 0f;
            }
            catch (MethodAccessException)
            {
                return 0f;
            }
        }

        private static double GetCurrentUTSafe()
        {
            try
            {
                return Planetarium.GetUniversalTime();
            }
            catch (System.Security.SecurityException)
            {
                return double.NaN;
            }
            catch (MethodAccessException)
            {
                return double.NaN;
            }
        }

        private static float GetCurrentWarpRateSafe()
        {
            try
            {
                return TimeWarp.CurrentRate;
            }
            catch (System.Security.SecurityException)
            {
                return 1f;
            }
            catch (MethodAccessException)
            {
                return 1f;
            }
        }

        private static int GetFrameCountSafe()
        {
            try
            {
                return Time.frameCount;
            }
            catch (System.Security.SecurityException)
            {
                return -1;
            }
            catch (MethodAccessException)
            {
                return -1;
            }
        }

        private static FlightCamera GetFlightCameraSafe()
        {
            try
            {
                return FlightCamera.fetch;
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
            catch (MethodAccessException)
            {
                return null;
            }
        }

        internal static string FormatWatchDistanceForLogs(double distanceMeters)
        {
            if (double.IsNaN(distanceMeters) || double.IsInfinity(distanceMeters) || distanceMeters < 0)
                return "?";
            return distanceMeters < 1000.0
                ? distanceMeters.ToString("F0", CultureInfo.InvariantCulture) + "m"
                : (distanceMeters / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + "km";
        }

        internal static string FormatVector3ForLogs(Vector3 value)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "({0:F1},{1:F1},{2:F1})", value.x, value.y, value.z);
        }

        internal static bool IsWithinWatchEntryRange(double distanceMeters)
        {
            return IsFiniteWatchDistance(distanceMeters)
                && distanceMeters < WatchEnterCutoffMeters;
        }

        internal static bool IsWithinWatchExitRange(double distanceMeters)
        {
            return IsFiniteWatchDistance(distanceMeters)
                && distanceMeters < WatchExitCutoffMeters;
        }

        internal static bool ShouldExitWatchForDistance(double distanceMeters)
        {
            return IsFiniteWatchDistance(distanceMeters)
                && distanceMeters >= WatchExitCutoffMeters;
        }

        internal static bool ShouldForceWatchedFullFidelityAtDistance(
            bool isWatchedGhost,
            double distanceMeters)
        {
            return isWatchedGhost
                && IsFiniteWatchDistance(distanceMeters)
                && !ShouldExitWatchForDistance(distanceMeters);
        }

        private static bool IsFiniteWatchDistance(double distanceMeters)
        {
            return !double.IsNaN(distanceMeters)
                && !double.IsInfinity(distanceMeters)
                && distanceMeters >= 0.0;
        }

        internal static bool IsFiniteVector3(Vector3 value)
        {
            return !float.IsNaN(value.x)
                && !float.IsNaN(value.y)
                && !float.IsNaN(value.z)
                && !float.IsInfinity(value.x)
                && !float.IsInfinity(value.y)
                && !float.IsInfinity(value.z);
        }

        internal static Vector3 OrbitDirectionFromAngles(float pitch, float heading)
        {
            float pitchRad = pitch * Mathf.Deg2Rad;
            float headingRad = heading * Mathf.Deg2Rad;
            return new Vector3(
                Mathf.Sin(headingRad) * Mathf.Cos(pitchRad),
                Mathf.Sin(pitchRad),
                Mathf.Cos(headingRad) * Mathf.Cos(pitchRad));
        }

        internal static string FormatRotationBasisForLogs(Quaternion rotation)
        {
            Vector3 forward = RotateVectorByQuaternion(rotation, Vector3.forward);
            Vector3 up = RotateVectorByQuaternion(rotation, Vector3.up);
            Vector3 right = RotateVectorByQuaternion(rotation, Vector3.right);
            return
                $"fwd={FormatVector3ForLogs(forward)} " +
                $"up={FormatVector3ForLogs(up)} " +
                $"right={FormatVector3ForLogs(right)}";
        }

        internal static bool TryResolveWorldOrbitDirection(
            Quaternion cameraFrameRotation,
            float pitch,
            float heading,
            out Vector3 worldOrbitDirection)
        {
            worldOrbitDirection = Vector3.zero;
            Vector3 orbitDirection = RotateVectorByQuaternion(
                cameraFrameRotation,
                OrbitDirectionFromAngles(pitch, heading));
            float sqrMagnitude = orbitDirection.sqrMagnitude;
            if (sqrMagnitude <= 1e-6f || !IsFiniteVector3(orbitDirection))
                return false;

            worldOrbitDirection = orbitDirection / Mathf.Sqrt(sqrMagnitude);
            return true;
        }

        internal static bool TryGetCurrentFlightCameraPivotRotation(
            FlightCamera flightCamera,
            out Quaternion pivotRotation)
        {
            pivotRotation = new Quaternion(0f, 0f, 0f, 1f);
            if (flightCamera == null)
                return false;

            Quaternion rawPivotRotation;
            try
            {
                rawPivotRotation = flightCamera.pivotRotation;
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
            catch (MethodAccessException)
            {
                return false;
            }

            float sqrMagnitude =
                rawPivotRotation.x * rawPivotRotation.x
                + rawPivotRotation.y * rawPivotRotation.y
                + rawPivotRotation.z * rawPivotRotation.z
                + rawPivotRotation.w * rawPivotRotation.w;
            if (sqrMagnitude <= 1e-12f
                || float.IsNaN(sqrMagnitude)
                || float.IsInfinity(sqrMagnitude))
            {
                return false;
            }

            pivotRotation = NormalizeQuaternion(rawPivotRotation);
            return true;
        }

        private static bool TryResolveWorldOrbitDirectionForLogs(
            WatchCameraTransitionState cameraState,
            out Vector3 worldOrbitDirection)
        {
            worldOrbitDirection = Vector3.zero;
            if (cameraState.HasWorldOrbitDirection)
            {
                worldOrbitDirection = cameraState.WorldOrbitDirection.normalized;
                return true;
            }

            if (!cameraState.HasTargetRotation)
                return false;

            worldOrbitDirection = RotateVectorByQuaternion(
                cameraState.TargetRotation,
                OrbitDirectionFromAngles(cameraState.Pitch, cameraState.Heading)).normalized;
            return true;
        }

        private void LogCapturedWatchCameraState(
            string context,
            int targetIndex,
            string targetRecordingId,
            WatchCameraTransitionState cameraState)
        {
            FlightCamera flightCamera = FlightCamera.fetch;
            if (flightCamera?.transform == null)
                return;

            Transform sourceTarget = flightCamera.Target;
            bool hasCapturedWorldOrbitDirection = TryResolveWorldOrbitDirectionForLogs(
                cameraState,
                out var capturedWorldOrbitDirection);
            bool hasPivotRotation = TryGetCurrentFlightCameraPivotRotation(
                flightCamera,
                out var pivotRotation);
            Vector3 pivotWorldOrbitDirection = Vector3.zero;
            bool hasPivotWorldOrbitDirection = hasPivotRotation
                && TryResolveWorldOrbitDirection(
                    pivotRotation,
                    cameraState.Pitch,
                    cameraState.Heading,
                    out pivotWorldOrbitDirection);
            bool hasGeometricWorldOrbitDirection = TryGetCurrentFlightCameraGeometricOrbitDirection(
                flightCamera,
                out var orbitCenter,
                out var geometricWorldOrbitDirection);
            string sourceBasis = cameraState.HasTargetRotation
                ? FormatRotationBasisForLogs(cameraState.TargetRotation)
                : "basis=?";
            string pivotBasis = hasPivotRotation
                ? FormatRotationBasisForLogs(pivotRotation)
                : "basis=?";

            ParsekLog.Info("CameraFollow",
                $"Watch camera capture ({context}): rec=#{targetIndex} id={targetRecordingId ?? "null"} " +
                $"sourceTarget={sourceTarget?.name ?? "null"} cameraPos={FormatVector3ForLogs(flightCamera.transform.position)} " +
                $"orbitCenter={FormatVector3ForLogs(orbitCenter)} distance={cameraState.Distance.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"pitch={cameraState.Pitch.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"hdg={cameraState.Heading.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"mode={cameraState.Mode} override={cameraState.UserModeOverride} " +
                $"capturedWorldOrbit={(hasCapturedWorldOrbitDirection ? FormatVector3ForLogs(capturedWorldOrbitDirection) : "?")} " +
                $"pivotWorldOrbit={(hasPivotWorldOrbitDirection ? FormatVector3ForLogs(pivotWorldOrbitDirection) : "?")} " +
                $"geometricWorldOrbit={(hasGeometricWorldOrbitDirection ? FormatVector3ForLogs(geometricWorldOrbitDirection) : "?")} " +
                $"targetDir={FormatVector3ForLogs(flightCamera.targetDirection)} " +
                $"endDir={FormatVector3ForLogs(flightCamera.endDirection)} " +
                $"pivotBasis={pivotBasis} targetBasis={sourceBasis}");
        }

        private void LogAppliedWatchCameraTransfer(
            string context,
            GhostPlaybackState state,
            WatchCameraTransitionState cameraState,
            Transform watchTarget,
            float pitch,
            float heading)
        {
            Vector3 localOrbitDirection = OrbitDirectionFromAngles(pitch, heading);
            bool hasSourceWorldOrbit = TryResolveWorldOrbitDirectionForLogs(
                cameraState,
                out var sourceWorldOrbit);
            bool hasResolvedWorldOrbit = watchTarget != null;
            Vector3 resolvedWorldOrbit = hasResolvedWorldOrbit
                ? RotateVectorByQuaternion(watchTarget.rotation, localOrbitDirection).normalized
                : Vector3.zero;
            string targetBasis = watchTarget != null
                ? FormatRotationBasisForLogs(watchTarget.rotation)
                : "basis=?";

            ParsekLog.Info("CameraFollow",
                $"Watch camera apply ({context}): rec=#{watchedRecordingIndex} id={watchedRecordingId ?? "null"} " +
                $"target={watchTarget?.name ?? "null"} targetPos={(watchTarget != null ? FormatVector3ForLogs(watchTarget.position) : "?")} " +
                $"pivotPos={(state?.cameraPivot != null ? FormatVector3ForLogs(state.cameraPivot.position) : "?")} " +
                $"distance={cameraState.Distance.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"pitch={pitch.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"hdg={heading.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"localOrbit={FormatVector3ForLogs(localOrbitDirection)} " +
                $"sourceWorldOrbit={(hasSourceWorldOrbit ? FormatVector3ForLogs(sourceWorldOrbit) : "?")} " +
                $"resolvedWorldOrbit={(hasResolvedWorldOrbit ? FormatVector3ForLogs(resolvedWorldOrbit) : "?")} " +
                $"mode={cameraState.Mode} override={cameraState.UserModeOverride} " +
                $"{targetBasis}");
        }

        private string ResolveWatchStateSource(GhostPlaybackState state)
        {
            if (state == null) return "missing";
            if (watchedOverlapCycleIndex == -2) return "hold";

            GhostPlaybackState primary;
            bool hasPrimary = host.Engine.ghostStates.TryGetValue(watchedRecordingIndex, out primary)
                && primary != null;
            if (hasPrimary && ReferenceEquals(primary, state))
                return "primary";
            return "overlap";
        }

        private double ResolveWatchDistanceMeters(GhostPlaybackState state)
        {
            if (state == null) return double.NaN;

            double distMeters = state.lastDistance;
            if ((double.IsNaN(distMeters) || distMeters <= 0)
                && state.ghost != null
                && FlightGlobals.ActiveVessel != null)
            {
                distMeters = Vector3d.Distance(
                    state.ghost.transform.position,
                    FlightGlobals.ActiveVessel.transform.position);
            }

            return distMeters;
        }

        private string BuildWatchFocusSummary(GhostPlaybackState state)
        {
            var committed = RecordingStore.CommittedRecordings;
            string vesselName = watchedRecordingIndex >= 0 && watchedRecordingIndex < committed.Count
                ? committed[watchedRecordingIndex].VesselName
                : "?";
            Transform expectedTarget = state != null
                ? (GetWatchTarget(state.cameraPivot) ?? state.ghost?.transform)
                : null;
            Transform actualTarget = FlightCamera.fetch != null ? FlightCamera.fetch.Target : null;
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            double distMeters = ResolveWatchDistanceMeters(state);
            string ghostBody = state?.lastInterpolatedBodyName ?? "null";
            string activeBody = activeVessel?.mainBody?.name ?? "null";
            string activeName = activeVessel?.vesselName ?? "null";
            string zone = state != null ? state.currentZone.ToString() : "None";
            bool targetMatches = expectedTarget != null && actualTarget == expectedTarget;

            return
                $"watch=active rec=#{watchedRecordingIndex} id={watchedRecordingId ?? "null"} " +
                $"vessel=\"{vesselName}\" source={ResolveWatchStateSource(state)} cycle={watchedOverlapCycleIndex} " +
                $"mode={currentCameraMode} expectedTarget={expectedTarget?.name ?? "null"} " +
                $"actualTarget={actualTarget?.name ?? "null"} targetMatches={targetMatches} " +
                $"zone={zone} dist={FormatWatchDistanceForLogs(distMeters)} " +
                $"alt={(state != null ? state.lastInterpolatedAltitude.ToString("F0", CultureInfo.InvariantCulture) : "?")}m " +
                $"ghostBody={ghostBody} activeVessel=\"{activeName}\" activeBody={activeBody}";
        }

        private string BuildWatchFocusKey(GhostPlaybackState state)
        {
            Transform expectedTarget = state != null
                ? (GetWatchTarget(state.cameraPivot) ?? state.ghost?.transform)
                : null;
            Transform actualTarget = FlightCamera.fetch != null ? FlightCamera.fetch.Target : null;
            string zone = state != null ? state.currentZone.ToString() : "None";

            return $"{watchedRecordingId ?? "null"}|{watchedRecordingIndex}|{ResolveWatchStateSource(state)}|" +
                   $"{watchedOverlapCycleIndex}|{currentCameraMode}|{expectedTarget?.name ?? "null"}|" +
                   $"{actualTarget?.name ?? "null"}|{zone}";
        }

        internal string DescribeWatchFocusForLogs()
        {
            if (watchedRecordingIndex < 0)
                return "watch=inactive";
            return BuildWatchFocusSummary(FindWatchedGhostState());
        }

        internal string DescribeWatchEligibilityForLogs(int index)
        {
            var ghostStates = host.Engine.ghostStates;
            GhostPlaybackState state;
            bool hasGhost = ghostStates.TryGetValue(index, out state) && state != null && state.ghost != null;
            string ghostBody = hasGhost ? (state.lastInterpolatedBodyName ?? "null") : "null";
            string activeBodyName = FlightGlobals.ActiveVessel?.mainBody?.name;
            string activeBody = activeBodyName ?? "null";
            double distMeters = hasGhost ? ResolveWatchDistanceMeters(state) : double.NaN;
            bool sameBody = hasGhost
                && !string.IsNullOrEmpty(ghostBody)
                && !string.IsNullOrEmpty(activeBodyName)
                && ghostBody == activeBodyName;
            bool inRange = hasGhost && IsWithinWatchEntryRange(distMeters);
            string zone = hasGhost ? state.currentZone.ToString() : "None";
            bool isWatched = index == watchedRecordingIndex;

            return
                $"watchEval(rec=#{index} watched={isWatched} watchedIndex={watchedRecordingIndex} " +
                $"hasGhost={hasGhost} ghostActive={(hasGhost && state.ghost.activeSelf)} zone={zone} " +
                $"dist={FormatWatchDistanceForLogs(distMeters)} " +
                $"enterCutoff={(WatchEnterCutoffMeters / 1000f).ToString("F0", CultureInfo.InvariantCulture)}km " +
                $"exitCutoff={(WatchExitCutoffMeters / 1000f).ToString("F0", CultureInfo.InvariantCulture)}km " +
                $"ghostBody={ghostBody} activeBody={activeBody} sameBody={sameBody} inRange={inRange})";
        }

        // === Camera event handlers for engine loop/overlap cycle transitions ===

        internal void HandleLoopCameraAction(CameraActionEvent evt)
        {
            if (watchedRecordingIndex != evt.Index) return; // not watching this recording

            switch (evt.Action)
            {
                case CameraActionType.ExitWatch:
                    ExitWatchModePreservingLineage();
                    break;

                case CameraActionType.ExplosionHoldStart:
                    DestroyOverlapCameraAnchor();
                    overlapCameraAnchor = new UnityEngine.GameObject("ParsekLoopCameraAnchor");
                    overlapCameraAnchor.transform.position = evt.AnchorPosition;
                    if (FlightCamera.fetch != null)
                        FlightCamera.fetch.SetTargetTransform(overlapCameraAnchor.transform);
                    overlapRetargetAfterUT = evt.HoldUntilUT;
                    watchedOverlapCycleIndex = -2;
                    ParsekLog.Info("CameraFollow",
                        $"Loop: camera holding at explosion for #{evt.Index}");
                    break;

                case CameraActionType.ExplosionHoldEnd:
                    // Non-destroyed loop boundary: ghost will be destroyed and respawned.
                    // Create a temporary camera anchor at the ghost's last position to bridge
                    // the gap between destroy and respawn — without this, FlightCamera detects
                    // a null target and snaps back to the active vessel.
                    watchedOverlapCycleIndex = -1;
                    overlapRetargetAfterUT = -1;
                    watchNoTargetFrames = 0;
                    overlapBridgeLastRetryFrame = -1;
                    DestroyOverlapCameraAnchor();
                    overlapCameraAnchor = new UnityEngine.GameObject("ParsekLoopCameraBridge");
                    overlapCameraAnchor.transform.position = evt.AnchorPosition;
                    if (FlightCamera.fetch != null)
                        FlightCamera.fetch.SetTargetTransform(overlapCameraAnchor.transform);
                    ParsekLog.Info("CameraFollow",
                        $"Loop: camera bridged at cycle boundary for #{evt.Index}");
                    break;

                case CameraActionType.RetargetToNewGhost:
                    if (watchedOverlapCycleIndex == -1 && evt.GhostPivot != null && FlightCamera.fetch != null)
                    {
                        TryRetargetWatchCameraPreservingState(
                            FindGhostStateByCameraPivot(evt.GhostPivot),
                            evt.GhostPivot);
                        watchedOverlapCycleIndex = evt.NewCycleIndex;
                        // Clean up the bridge anchor now that camera is on the new ghost
                        DestroyOverlapCameraAnchor();
                        ParsekLog.Info("CameraFollow",
                            $"Loop: camera retargeted to ghost #{evt.Index} cycle={evt.NewCycleIndex}");
                    }
                    break;
            }
        }

        internal void HandleOverlapCameraAction(CameraActionEvent evt)
        {
            if (watchedRecordingIndex != evt.Index) return;

            switch (evt.Action)
            {
                case CameraActionType.RetargetToNewGhost:
                    if (watchedOverlapCycleIndex == -1 && evt.GhostPivot != null && FlightCamera.fetch != null)
                    {
                        TryRetargetWatchCameraPreservingState(
                            FindGhostStateByCameraPivot(evt.GhostPivot),
                            evt.GhostPivot);
                        watchedOverlapCycleIndex = evt.NewCycleIndex;
                        DestroyOverlapCameraAnchor();
                        ParsekLog.Info("CameraFollow",
                            $"Overlap: camera retargeted to ghost #{evt.Index} cycle={evt.NewCycleIndex}");
                    }
                    break;

                case CameraActionType.ExplosionHoldStart:
                    // Only react when the expiring cycle is the one we're watching.
                    // Other cycles of the same recording expiring must not yank the camera.
                    if (ShouldIgnoreOverlapCycleEvent(evt.NewCycleIndex, watchedOverlapCycleIndex))
                    {
                        // Rate-limit key is per (recording, action) — NOT per cycle index —
                        // because cycle indices grow without bound over a session, which
                        // would leak one rate-limit-state entry per expired non-watched
                        // cycle in ParsekLog.rateLimitStateByKey. The message still carries
                        // the cycle numbers for diagnostic value.
                        ParsekLog.VerboseRateLimited("CameraFollow",
                            $"overlap-hold-start-skip-{evt.Index}",
                            $"Overlap: hold start for #{evt.Index} cycle={evt.NewCycleIndex} " +
                            $"ignored (watching cycle={watchedOverlapCycleIndex})");
                        break;
                    }
                    overlapRetargetAfterUT = evt.HoldUntilUT;
                    watchedOverlapCycleIndex = -2;
                    watchNoTargetFrames = 0;
                    overlapBridgeLastRetryFrame = -1;
                    DestroyOverlapCameraAnchor();
                    overlapCameraAnchor = new UnityEngine.GameObject("ParsekOverlapCameraAnchor");
                    overlapCameraAnchor.transform.position = evt.AnchorPosition;
                    if (FlightCamera.fetch != null)
                        FlightCamera.fetch.SetTargetTransform(overlapCameraAnchor.transform);
                    ParsekLog.Info("CameraFollow",
                        $"Overlap: camera holding at explosion for #{evt.Index} cycle={evt.NewCycleIndex}");
                    break;

                case CameraActionType.ExplosionHoldEnd:
                    if (ShouldIgnoreOverlapCycleEvent(evt.NewCycleIndex, watchedOverlapCycleIndex))
                    {
                        // See overlap-hold-start-skip above for why the key omits cycle index.
                        ParsekLog.VerboseRateLimited("CameraFollow",
                            $"overlap-hold-end-skip-{evt.Index}",
                            $"Overlap: hold end for #{evt.Index} cycle={evt.NewCycleIndex} " +
                            $"ignored (watching cycle={watchedOverlapCycleIndex})");
                        break;
                    }
                    watchedOverlapCycleIndex = -1;
                    overlapRetargetAfterUT = -1;
                    watchNoTargetFrames = 0;
                    overlapBridgeLastRetryFrame = -1;
                    DestroyOverlapCameraAnchor();
                    overlapCameraAnchor = new UnityEngine.GameObject("ParsekOverlapCameraBridge");
                    overlapCameraAnchor.transform.position = evt.AnchorPosition;
                    if (FlightCamera.fetch != null)
                        FlightCamera.fetch.SetTargetTransform(overlapCameraAnchor.transform);
                    if (TryResolveOverlapBridgeRetarget())
                    {
                        ParsekLog.Info("CameraFollow",
                            $"Overlap: camera bridged at quiet expiry for #{evt.Index} cycle={evt.NewCycleIndex}");
                    }
                    break;
            }
        }

        /// <summary>
        /// Returns true if recording at index has a live playback shell.
        /// Hidden-tier ghosts may have unloaded visuals but are still watchable.
        /// </summary>
        internal bool HasActiveGhost(int index)
        {
            var ghostStates = host.Engine.ghostStates;
            GhostPlaybackState s;
            return ghostStates.TryGetValue(index, out s) && s != null;
        }

        /// <summary>
        /// Returns true if the ghost at index is within the watch camera range.
        /// Active watches use the wider exit cutoff; idle watch affordances use
        /// the entry cutoff.
        /// </summary>
        internal bool IsGhostWithinVisualRange(int index)
        {
            var ghostStates = host.Engine.ghostStates;
            GhostPlaybackState s = index == watchedRecordingIndex
                ? FindWatchedGhostState()
                : null;
            if (s == null && (!ghostStates.TryGetValue(index, out s) || s == null))
                return false;
            return index == watchedRecordingIndex
                ? IsWithinWatchExitRange(s.lastDistance)
                : IsWithinWatchEntryRange(s.lastDistance);
        }

        internal bool IsWatchedGhostState(int recordingIndex, GhostPlaybackState state)
        {
            if (state == null) return false;
            return recordingIndex == watchedRecordingIndex
                && state.loopCycleIndex == watchedOverlapCycleIndex;
        }

        /// <summary>
        /// Returns true if the ghost at index is on the same celestial body as the active vessel.
        /// </summary>
        internal bool IsGhostOnSameBody(int index)
        {
            return host.Engine.IsGhostOnBody(index, FlightGlobals.ActiveVessel?.mainBody?.name);
        }

        /// <summary>
        /// Draws the on-screen overlay when in watch mode: vessel name and return hint.
        /// Called from OnGUI when watchedRecordingIndex >= 0.
        /// </summary>
        internal void DrawWatchModeOverlay()
        {
            if (watchOverlayStyle == null)
            {
                watchOverlayStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperCenter,
                    normal = { textColor = Color.white }
                };
                watchOverlayHintStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    alignment = TextAnchor.UpperCenter,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.7f) }
                };
            }

            string vesselName = "";
            var committed = RecordingStore.CommittedRecordings;
            if (watchedRecordingIndex >= 0 && watchedRecordingIndex < committed.Count)
                vesselName = committed[watchedRecordingIndex].VesselName;

            // Compute distance from ghost to active vessel
            string distText = "";
            var ghostStates = host.Engine.ghostStates;
            GhostPlaybackState watchState;
            if (ghostStates.TryGetValue(watchedRecordingIndex, out watchState)
                && watchState?.ghost != null && FlightGlobals.ActiveVessel != null)
            {
                double dist = Vector3d.Distance(
                    (Vector3d)watchState.ghost.transform.position,
                    (Vector3d)FlightGlobals.ActiveVessel.transform.position);
                if (dist < 1000)
                    distText = dist.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " m";
                else
                    distText = (dist / 1000).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " km";
            }

            float boxW = 300f, boxH = 50f;
            float x = (Screen.width * 0.5f - boxW) / 2f; // centered in the left half of the screen
            float y = 10f;
            Rect bgRect = new Rect(x, y, boxW, boxH);

            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(bgRect, Texture2D.whiteTexture);
            GUI.color = Color.white;

            string modeLabel = currentCameraMode == WatchCameraMode.HorizonLocked
                ? " [Horizon]" : " [Free]";
            string title = string.IsNullOrEmpty(distText)
                ? "Watching: " + vesselName + modeLabel
                : "Watching: " + vesselName + "  (" + distText + ")" + modeLabel;
            GUI.Label(new Rect(x, y + 5, boxW, 22f), title, watchOverlayStyle);
            GUI.Label(new Rect(x, y + 27, boxW, 18f), "[ ] return  |  V toggle camera", watchOverlayHintStyle);
        }

        /// <summary>
        /// Captures the current FlightCamera orbit and target basis so watch-mode
        /// transitions can preserve the current world-facing direction.
        /// </summary>
        private static bool TryCaptureCurrentFlightCameraState(
            WatchCameraMode mode,
            bool userModeOverride,
            out WatchCameraTransitionState cameraState)
        {
            cameraState = default(WatchCameraTransitionState);

            FlightCamera flightCamera = FlightCamera.fetch;
            if (flightCamera == null)
                return false;

            cameraState.Distance = flightCamera.Distance;
            // KSP stores camPitch / camHdg in RADIANS; Parsek's OrbitDirectionFromAngles
            // / CompensateCameraAngles / DecomposeOrbitDirectionInTargetFrame all treat
            // their angle arguments as DEGREES (they Deg2Rad internally). Convert at the
            // KSP boundary so every WatchCameraTransitionState field is in degrees.
            cameraState.Pitch = flightCamera.camPitch * Mathf.Rad2Deg;
            cameraState.Heading = flightCamera.camHdg * Mathf.Rad2Deg;
            cameraState.Mode = mode;
            cameraState.UserModeOverride = userModeOverride;
            if (flightCamera.Target != null)
            {
                cameraState.HasTargetRotation = true;
                cameraState.TargetRotation = flightCamera.Target.rotation;
            }
            if (TryGetCurrentFlightCameraWorldOrbitDirection(
                    flightCamera,
                    out var worldOrbitDirection))
            {
                cameraState.HasWorldOrbitDirection = true;
                cameraState.WorldOrbitDirection = worldOrbitDirection;
            }
            return true;
        }

        internal static bool TryGetCurrentFlightCameraWorldOrbitDirection(
            FlightCamera flightCamera,
            out Vector3 worldOrbitDirection)
        {
            worldOrbitDirection = Vector3.zero;
            if (flightCamera == null)
                return false;

            if (TryGetCurrentFlightCameraPivotRotation(flightCamera, out var pivotRotation)
                && TryResolveWorldOrbitDirection(
                    pivotRotation,
                    flightCamera.camPitch * Mathf.Rad2Deg,
                    flightCamera.camHdg * Mathf.Rad2Deg,
                    out worldOrbitDirection))
            {
                return true;
            }

            return TryGetCurrentFlightCameraGeometricOrbitDirection(
                flightCamera,
                out _,
                out worldOrbitDirection);
        }

        internal static bool TryGetCurrentFlightCameraGeometricOrbitDirection(
            FlightCamera flightCamera,
            out Vector3 orbitCenter,
            out Vector3 worldOrbitDirection)
        {
            orbitCenter = Vector3.zero;
            worldOrbitDirection = Vector3.zero;
            if (flightCamera?.transform == null)
                return false;

            if (flightCamera.transform.parent != null)
            {
                orbitCenter = flightCamera.transform.parent.position;
            }
            else if (flightCamera.Target != null)
            {
                orbitCenter = flightCamera.Target.position;
            }
            else
            {
                return false;
            }

            Vector3 orbitVector = flightCamera.transform.position - orbitCenter;
            float sqrMagnitude = orbitVector.sqrMagnitude;
            if (sqrMagnitude <= 1e-6f || !IsFiniteVector3(orbitVector))
            {
                return false;
            }

            worldOrbitDirection = orbitVector / Mathf.Sqrt(sqrMagnitude);
            return true;
        }

        /// <summary>
        /// Captures the live ghost-relative camera orbit so manual watch switches
        /// can reuse it on the next target instead of resetting to the default entry view.
        /// </summary>
        private bool TryCaptureActiveWatchCameraState(out WatchCameraTransitionState cameraState)
        {
            return TryCaptureCurrentFlightCameraState(
                currentCameraMode,
                userModeOverride,
                out cameraState);
        }

        private void RememberWatchCameraState(WatchCameraTransitionState cameraState)
        {
            switch (cameraState.Mode)
            {
                case WatchCameraMode.Free:
                    rememberedFreeCameraState = cameraState;
                    hasRememberedFreeCameraState = true;
                    break;

                case WatchCameraMode.HorizonLocked:
                    rememberedHorizonLockedCameraState = cameraState;
                    hasRememberedHorizonLockedCameraState = true;
                    break;
            }
        }

        private bool TryGetRememberedWatchCameraState(
            WatchCameraMode mode,
            out WatchCameraTransitionState cameraState)
        {
            cameraState = default(WatchCameraTransitionState);
            switch (mode)
            {
                case WatchCameraMode.Free:
                    if (!hasRememberedFreeCameraState)
                        return false;
                    cameraState = rememberedFreeCameraState;
                    return true;

                case WatchCameraMode.HorizonLocked:
                    if (!hasRememberedHorizonLockedCameraState)
                        return false;
                    cameraState = rememberedHorizonLockedCameraState;
                    return true;

                default:
                    return false;
            }
        }

        private void ClearRememberedWatchCameraStates()
        {
            hasRememberedFreeCameraState = false;
            rememberedFreeCameraState = default(WatchCameraTransitionState);
            hasRememberedHorizonLockedCameraState = false;
            rememberedHorizonLockedCameraState = default(WatchCameraTransitionState);
        }

        /// <summary>
        /// Returns a copy of <paramref name="cameraState"/> with the world-direction
        /// and source-target-rotation basis fields cleared so restoring the state
        /// on a different ghost re-applies the captured (pitch, hdg) directly
        /// relative to the new target's transform. Used for mode-swap bookmarks
        /// (V-toggle, auto-mode atmosphere crossings) and for explicit user W->W
        /// switches, where the captured world-direction would otherwise decompose
        /// into a visually surprising local angle on the destination ghost's
        /// rotated basis (its horizon proxy / camera pivot has rotated continuously
        /// during the seconds the user spent on another ghost). Chain transfers
        /// (TransferWatchToNextSegment) keep the world-direction path because the
        /// auto-handoff applies on the same frame with no drift window.
        /// </summary>
        internal static WatchCameraTransitionState MakeWatchCameraStateTargetRelative(
            WatchCameraTransitionState cameraState)
        {
            cameraState.HasTargetRotation = false;
            cameraState.TargetRotation = Quaternion.identity;
            cameraState.HasWorldOrbitDirection = false;
            cameraState.WorldOrbitDirection = Vector3.zero;
            return cameraState;
        }

        private void RememberWatchCameraStateAsTargetRelative(WatchCameraTransitionState cameraState)
        {
            RememberWatchCameraState(MakeWatchCameraStateTargetRelative(cameraState));
        }

        private void RememberCurrentWatchCameraState()
        {
            if (TryCaptureActiveWatchCameraState(out var cameraState))
                RememberWatchCameraStateAsTargetRelative(cameraState);
        }

        private bool TryRestoreRememberedWatchCameraState(
            GhostPlaybackState state,
            WatchCameraMode mode,
            bool modeOverride,
            string logContext)
        {
            if (!TryGetRememberedWatchCameraState(mode, out var cameraState))
            {
                if (!string.IsNullOrEmpty(logContext))
                {
                    ParsekLog.Verbose("CameraFollow",
                        $"Watch camera mode restore skipped ({logContext}): mode={mode} hasSavedState=False");
                }
                return false;
            }

            cameraState.Mode = mode;
            cameraState.UserModeOverride = modeOverride;
            return TryApplySwitchedWatchCameraState(state, cameraState, logContext);
        }

        internal static WatchCameraMode ResolveSwitchCameraMode(
            WatchCameraMode currentMode,
            bool userModeOverride,
            bool hasAtmosphere,
            double atmosphereDepth,
            double altitude)
        {
            if (userModeOverride)
                return currentMode;

            return ShouldAutoHorizonLock(hasAtmosphere, atmosphereDepth, altitude)
                ? WatchCameraMode.HorizonLocked
                : WatchCameraMode.Free;
        }

        internal static (float pitch, float heading) CompensateTransferredWatchAngles(
            WatchCameraTransitionState currentState,
            Quaternion newTargetRotation)
        {
            if (currentState.HasWorldOrbitDirection)
                return DecomposeOrbitDirectionInTargetFrame(
                    currentState.WorldOrbitDirection,
                    newTargetRotation);

            if (!currentState.HasTargetRotation)
                return (currentState.Pitch, currentState.Heading);

            return CompensateCameraAngles(
                currentState.TargetRotation,
                newTargetRotation,
                currentState.Pitch,
                currentState.Heading);
        }

        /// <summary>
        /// Builds the canonical framing used when the player enters watch mode on
        /// a ghost for the first time (not a W->W switch). We intentionally do not
        /// transfer angles from the active vessel — an on-pad rocket's camera
        /// basis has nothing to do with the ghost's horizon basis, and decomposing
        /// one into the other produces arbitrary off-axis framings. Instead place
        /// the camera behind the ghost at a fixed default pitch/heading/distance,
        /// which matches the expected "default KSC-side orientation".
        /// </summary>
        internal static WatchCameraTransitionState PrepareFreshWatchCameraState(
            WatchCameraTransitionState currentState,
            bool hasAtmosphere,
            double atmosphereDepth,
            double altitude)
        {
            var mode = ResolveSwitchCameraMode(
                currentState.Mode,
                userModeOverride: false,
                hasAtmosphere,
                atmosphereDepth,
                altitude);
            return new WatchCameraTransitionState
            {
                Distance = WatchMode.EntryDistance,
                Pitch = WatchMode.EntryPitchDegrees,
                Heading = WatchMode.EntryHeadingDegrees,
                Mode = mode,
                UserModeOverride = false,
                HasTargetRotation = false,
                HasWorldOrbitDirection = false
            };
        }

        private WatchCameraTransitionState ResolveSwitchCameraStateForGhost(
            WatchCameraTransitionState currentState,
            GhostPlaybackState state)
        {
            CelestialBody body = ResolveBody(state);
            bool hasAtmosphere = body != null && body.atmosphere;
            double atmosphereDepth = body != null ? body.atmosphereDepth : 0.0;
            double altitude = state != null ? state.lastInterpolatedAltitude : 0.0;
            currentState.Mode = ResolveSwitchCameraMode(
                currentState.Mode,
                currentState.UserModeOverride,
                hasAtmosphere,
                atmosphereDepth,
                altitude);
            return currentState;
        }

        private WatchCameraTransitionState BuildCanonicalFreshWatchCameraState(
            GhostPlaybackState state)
        {
            CelestialBody body = ResolveBody(state);
            bool hasAtmosphere = body != null && body.atmosphere;
            double atmosphereDepth = body != null ? body.atmosphereDepth : 0.0;
            double altitude = state != null ? state.lastInterpolatedAltitude : 0.0;
            // default(WatchCameraTransitionState).Mode is Free; PrepareFreshWatchCameraState
            // upgrades it to HorizonLocked only under ShouldAutoHorizonLock (in-atmosphere
            // low altitude). The body parameters drive that decision.
            return PrepareFreshWatchCameraState(
                default(WatchCameraTransitionState),
                hasAtmosphere,
                atmosphereDepth,
                altitude);
        }

        private bool TryApplySwitchedWatchCameraState(
            GhostPlaybackState state,
            WatchCameraTransitionState cameraState,
            string logContext = null)
        {
            FlightCamera flightCamera = FlightCamera.fetch;
            if (flightCamera == null || state == null)
                return false;

            currentCameraMode = cameraState.Mode;
            userModeOverride = cameraState.UserModeOverride;

            PrimeWatchTargetOrientation(state);

            Transform watchTarget = GetWatchTarget(state.cameraPivot) ?? state.ghost?.transform;
            var (pitch, heading) = watchTarget != null
                ? CompensateTransferredWatchAngles(cameraState, watchTarget.rotation)
                : (cameraState.Pitch, cameraState.Heading);
            if (watchTarget != null)
                flightCamera.SetTargetTransform(watchTarget);

            flightCamera.SetDistance(cameraState.Distance);
            // pitch / heading are in degrees (Parsek's internal convention); convert
            // to radians for KSP's FlightCamera, which uses radians throughout.
            flightCamera.camPitch = pitch * Mathf.Deg2Rad;
            flightCamera.camHdg = heading * Mathf.Deg2Rad;

            if (flightCamera.transform?.parent != null && state.cameraPivot != null)
                flightCamera.transform.parent.position = state.cameraPivot.position;

            if (!string.IsNullOrEmpty(logContext))
                LogAppliedWatchCameraTransfer(
                    logContext,
                    state,
                    cameraState,
                    watchTarget,
                    pitch,
                    heading);

            return true;
        }

        internal static bool TryResolveRetargetedWatchAngles(
            bool hasCapturedState,
            WatchCameraTransitionState cameraState,
            Quaternion newTargetRotation,
            out float pitch,
            out float heading)
        {
            pitch = cameraState.Pitch;
            heading = cameraState.Heading;
            if (!hasCapturedState)
                return false;

            (pitch, heading) = CompensateTransferredWatchAngles(cameraState, newTargetRotation);
            return true;
        }

        private bool TryRetargetWatchCameraPreservingState(Transform newTarget)
        {
            FlightCamera flightCamera = FlightCamera.fetch;
            if (flightCamera == null || newTarget == null)
                return false;

            bool hasCapturedState = TryCaptureActiveWatchCameraState(out var cameraState);
            flightCamera.SetTargetTransform(newTarget);
            if (TryResolveRetargetedWatchAngles(
                    hasCapturedState,
                    cameraState,
                    newTarget.rotation,
                    out var pitchDeg,
                    out var headingDeg))
            {
                flightCamera.camPitch = pitchDeg * Mathf.Deg2Rad;
                flightCamera.camHdg = headingDeg * Mathf.Deg2Rad;
            }

            return true;
        }

        private bool TryRetargetWatchCameraPreservingState(
            GhostPlaybackState state,
            Transform fallbackTarget = null)
        {
            if (state != null)
            {
                // HorizonLocked mode targets horizonProxy, whose rotation is only valid
                // after we prime the replacement ghost for the current frame.
                PrimeWatchTargetOrientation(state);
                Transform target = GetWatchTarget(state.cameraPivot) ?? state.ghost?.transform;
                if (TryRetargetWatchCameraPreservingState(target))
                    return true;
            }

            return TryRetargetWatchCameraPreservingState(fallbackTarget);
        }

        private GhostPlaybackState FindGhostStateByCameraPivot(Transform ghostPivot)
        {
            if (ghostPivot == null)
                return null;

            var ghostStates = host.Engine.ghostStates;
            GhostPlaybackState primary;
            if (ghostStates.TryGetValue(watchedRecordingIndex, out primary)
                && primary != null
                && primary.cameraPivot == ghostPivot)
            {
                return primary;
            }

            List<GhostPlaybackState> overlaps;
            if (host.Engine.TryGetOverlapGhosts(watchedRecordingIndex, out overlaps) && overlaps != null)
            {
                for (int i = 0; i < overlaps.Count; i++)
                {
                    GhostPlaybackState overlap = overlaps[i];
                    if (overlap != null && overlap.cameraPivot == ghostPivot)
                        return overlap;
                }
            }

            return null;
        }

        /// <summary>
        /// Enter watch mode: point the camera at a ghost vessel.
        /// If already watching the same recording, toggles off (exits watch mode).
        /// If watching a different recording, switches to the new one (preserves camera state).
        /// </summary>
        internal void EnterWatchMode(int index)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (index < 0 || index >= committed.Count) return;

            // Toggle off: if already watching this recording, exit
            if (watchedRecordingIndex == index)
            {
                ExitWatchMode();
                return;
            }

            Recording rec;
            GhostPlaybackState gs;
            if (!TryResolveWatchEntryState(index, committed, out rec, out gs))
                return;

            ShowUnattendedFlightWarningIfNeeded();

            // If already watching a different recording, exit first (switch case -- preserve camera state)
            bool switching = watchedRecordingIndex >= 0 && watchedRecordingIndex != index;
            WatchCameraTransitionState switchCameraState = default(WatchCameraTransitionState);
            bool hasSwitchCameraState = switching && TryCaptureActiveWatchCameraState(out switchCameraState);
            if (switching)
            {
                if (hasSwitchCameraState)
                {
                    LogCapturedWatchCameraState(
                        "switch-source",
                        index,
                        rec.RecordingId,
                        switchCameraState);
                }
                else
                {
                    ParsekLog.Warn("CameraFollow",
                        $"Watch camera capture unavailable (switch-source): rec=#{index} id={rec.RecordingId ?? "null"}");
                }
            }
            else
            {
                // Fresh entry intentionally does NOT capture the active vessel's
                // camera orientation. The player-vessel frame (upright on the pad,
                // for example) is meaningless relative to the ghost's horizon
                // basis, so decomposing one into the other produces arbitrary
                // side-of-vessel framings that flip with velocity direction.
                ParsekLog.Info("CameraFollow",
                    $"Watch camera capture (fresh-entry-source): rec=#{index} id={rec.RecordingId ?? "null"} " +
                    $"using canonical framing (no active-vessel transfer) " +
                    $"pitch={WatchMode.EntryPitchDegrees.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"hdg={WatchMode.EntryHeadingDegrees.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"distance={WatchMode.EntryDistance.ToString("F1", CultureInfo.InvariantCulture)}");
            }
            if (switching && hasSwitchCameraState)
            {
                // Explicit user W->W switches let many frames pass between
                // capture (on the source ghost) and apply (on the destination
                // ghost) — the source ghost's basis has rotated continuously
                // during the gap, so the captured world-orbit-direction would
                // decompose into a visually surprising local angle on the
                // destination ghost's rotated basis. Drop the world-direction
                // and apply the captured (pitch, hdg) directly relative to the
                // new target. Chain transfers (TransferWatchToNextSegment) keep
                // the world-direction path because they auto-handoff in a
                // single frame with no drift window.
                switchCameraState = MakeWatchCameraStateTargetRelative(switchCameraState);
                RememberWatchCameraState(switchCameraState);
            }
            bool preservedHasRememberedFreeCameraState = hasRememberedFreeCameraState;
            WatchCameraTransitionState preservedFreeCameraState = rememberedFreeCameraState;
            bool preservedHasRememberedHorizonLockedCameraState = hasRememberedHorizonLockedCameraState;
            WatchCameraTransitionState preservedHorizonLockedCameraState = rememberedHorizonLockedCameraState;
            Vessel preservedCameraVessel = savedCameraVessel;
            float preservedCameraDistance = savedCameraDistance;
            float preservedCameraPitch = savedCameraPitch;
            float preservedCameraHeading = savedCameraHeading;
            if (switching)
            {
                ParsekLog.Info("CameraFollow", $"Switching watch from #{watchedRecordingIndex} to #{index} \"{committed[index].VesselName}\"");
                ExitWatchMode(skipCameraRestore: true);
            }

            if (!TryStartWatchSession(index, rec, gs, out gs))
                return;

            if (switching)
            {
                hasRememberedFreeCameraState = preservedHasRememberedFreeCameraState;
                rememberedFreeCameraState = preservedFreeCameraState;
                hasRememberedHorizonLockedCameraState = preservedHasRememberedHorizonLockedCameraState;
                rememberedHorizonLockedCameraState = preservedHorizonLockedCameraState;
            }

            // Save camera state only when entering fresh (not switching between ghosts)
            if (!switching)
            {
                savedCameraVessel = FlightGlobals.ActiveVessel;
                savedCameraDistance = FlightCamera.fetch.Distance;
                // Internal convention: store pitch/hdg in degrees (see TryCaptureCurrentFlightCameraState).
                savedCameraPitch = FlightCamera.fetch.camPitch * Mathf.Rad2Deg;
                savedCameraHeading = FlightCamera.fetch.camHdg * Mathf.Rad2Deg;
                savedPivotSharpness = FlightCamera.fetch.pivotTranslateSharpness;
            }
            else
            {
                // Keep Backspace restore pointed at the original player vessel/camera state
                // even after hopping between multiple ghosts in one watch session.
                // Manual W->W retargeting is still the same watch session, so an explicit
                // V-toggle override should stay in force until the user exits watch mode.
                savedCameraVessel = preservedCameraVessel;
                savedCameraDistance = preservedCameraDistance;
                savedCameraPitch = preservedCameraPitch;
                savedCameraHeading = preservedCameraHeading;
            }

            // Disable KSP's internal pivot tracking -- we drive the camera manually
            FlightCamera.fetch.pivotTranslateSharpness = 0f;

            bool restoredSwitchCameraState = switching
                && hasSwitchCameraState
                && TryApplySwitchedWatchCameraState(
                    gs,
                    ResolveSwitchCameraStateForGhost(switchCameraState, gs),
                    logContext: "switch-apply");
            bool restoredFreshEntryCameraState = !switching
                && TryApplySwitchedWatchCameraState(
                    gs,
                    BuildCanonicalFreshWatchCameraState(gs),
                    logContext: "fresh-entry-apply");

            if (!restoredSwitchCameraState && !restoredFreshEntryCameraState)
            {
                // Fall back to the legacy entry framing if we couldn't capture/apply the
                // source camera basis (for example if FlightCamera was unavailable).
                var watchTarget = GetWatchTarget(gs.cameraPivot) ?? gs.ghost?.transform;
                if (watchTarget != null)
                    FlightCamera.fetch.SetTargetTransform(watchTarget);
                FlightCamera.fetch.SetDistance(WatchMode.EntryDistance);  // override [75,400] entry clamp
                ParsekLog.Warn("CameraFollow",
                    $"Watch camera transfer fallback: rec=#{index} id={rec.RecordingId ?? "null"} " +
                    $"switching={switching} hasSwitchState={hasSwitchCameraState} " +
                    $"target={watchTarget?.name ?? "null"} " +
                    $"targetPos={(watchTarget != null ? FormatVector3ForLogs(watchTarget.position) : "?")} " +
                    $"targetBasis={(watchTarget != null ? FormatRotationBasisForLogs(watchTarget.rotation) : "basis=?")}");
            }
            RememberCurrentWatchCameraState();

            var activeWatchTarget = GetWatchTarget(gs.cameraPivot) ?? gs.ghost?.transform;
            watchedOverlapCycleIndex = gs.loopCycleIndex; // track which cycle we're following
            if (activeWatchTarget != null && gs.ghost != null)
            {
                ParsekLog.Info("CameraFollow",
                    $"EnterWatchMode: ghost #{index} \"{committed[index].VesselName}\"" +
                    $" target='{activeWatchTarget.name}' pivotLocal=({activeWatchTarget.localPosition.x:F2},{activeWatchTarget.localPosition.y:F2},{activeWatchTarget.localPosition.z:F2})" +
                    $" ghostPos=({gs.ghost.transform.position.x:F1},{gs.ghost.transform.position.y:F1},{gs.ghost.transform.position.z:F1})" +
                    $" camDist={FlightCamera.fetch.Distance.ToString("F1", CultureInfo.InvariantCulture)}");
            }
            else
            {
                ParsekLog.Info("CameraFollow",
                    $"EnterWatchMode: ghost #{index} \"{committed[index].VesselName}\" waiting for rebuilt camera target");
            }

            // Block inputs that could affect the active vessel
            InputLockManager.SetControlLock(WatchModeLockMask, WatchModeLockId);
            ParsekLog.Verbose("CameraFollow", $"InputLockManager control lock \"{WatchModeLockId}\" set: {WatchModeLockMask}");

            ResetWatchEntryTransientState();

            string body = gs.lastInterpolatedBodyName ?? "?";
            string altStr = gs.lastInterpolatedAltitude.ToString("F0", CultureInfo.InvariantCulture);
            ParsekLog.Info("CameraFollow",
                $"Entering watch mode for recording #{index} \"{committed[index].VesselName}\" \u2014 ghost at alt {altStr}m on {body}");
            LogWatchFocusStateChanged(gs, force: true, context: "enter");
        }

        private bool TryResolveWatchEntryState(
            int index,
            IReadOnlyList<Recording> committed,
            out Recording rec,
            out GhostPlaybackState gs)
        {
            rec = null;
            gs = null;

            // Phase 7 of Rewind-to-Staging (design §3.3): during an active re-fly
            // session, recordings in the session's suppressed subtree have no
            // physical presence — refuse watch-mode entry on them.
            if (SessionSuppressionState.IsActive
                && SessionSuppressionState.IsSuppressedRecordingIndex(index))
            {
                ParsekLog.Info("ReFlySession",
                    $"Watch mode entry refused: recording #{index} " +
                    $"\"{committed[index].VesselName}\" is session-suppressed by active re-fly");
                return false;
            }

            // Ghost playback state must exist. Hidden-tier ghosts may not currently
            // have a loaded mesh, but watch mode is allowed to force a rebuild.
            var ghostStates = host.Engine.ghostStates;
            if (!ghostStates.TryGetValue(index, out gs) || gs == null)
                return false;

            // Ghost must be on the same body as the active vessel
            string ghostBody = gs.lastInterpolatedBodyName;
            string activeBody = FlightGlobals.ActiveVessel?.mainBody?.name;
            if (string.IsNullOrEmpty(ghostBody) || string.IsNullOrEmpty(activeBody) || ghostBody != activeBody)
                return false;

            rec = committed[index];

            // Distance guard: KSP rendering breaks when camera is far from the active vessel
            // (FloatingOrigin, terrain, atmosphere, skybox are all anchored to active vessel).
            // Refuse new watch entry if the ghost is at or beyond the entry cutoff.
            if (FlightGlobals.ActiveVessel != null)
            {
                double distMeters = gs.lastDistance;
                if (distMeters <= 0 && gs.ghost != null)
                {
                    distMeters = Vector3d.Distance(
                        gs.ghost.transform.position, FlightGlobals.ActiveVessel.transform.position);
                }
                if (!IsWithinWatchEntryRange(distMeters))
                {
                    float distKm = (float)(distMeters / 1000.0);
                    float maxWatchKm = WatchEnterCutoffMeters / 1000f;
                    ParsekLog.Info("CameraFollow",
                        $"EnterWatchMode refused: ghost #{index} \"{committed[index].VesselName}\" " +
                        $"is {distKm.ToString("F0", CultureInfo.InvariantCulture)}km from active vessel (max {maxWatchKm.ToString("F0", CultureInfo.InvariantCulture)}km)");
                    ParsekLog.ScreenMessage($"Ghost too far to watch ({distKm:F0}km, max {maxWatchKm:F0}km)", 3f);
                    return false;
                }
            }

            return true;
        }

        private static void ShowUnattendedFlightWarningIfNeeded()
        {
            // Flight warning: if active vessel is in an unsafe state, show a brief screen message
            var av = FlightGlobals.ActiveVessel;
            if (av != null)
            {
                double pe = av.orbit != null ? av.orbit.PeA : 0;
                double atmoHeight = av.mainBody != null ? av.mainBody.atmosphereDepth : 0;
                if (!IsVesselSituationSafe(av.situation, pe, atmoHeight))
                {
                    ParsekLog.Verbose("CameraFollow", $"Showing flight warning \u2014 active vessel situation: {av.situation}");
                    ParsekLog.ScreenMessage("Your vessel continues unattended", 3f);
                }
            }
        }

        private void ResetWatchEntryTransientState()
        {
            // Reset entry-time hold and diagnostic state.
            watchEndHoldUntilRealTime = -1;
            watchEndHoldMaxRealTime = -1;
            watchEndHoldPendingActivationUT = double.NaN;
            watchNoTargetFrames = 0;
            lastLoggedWatchTargetMismatch = null;
            lastLoggedWatchFocusKey = null;
        }

        internal bool TryStartWatchSession(int index, Recording rec, GhostPlaybackState currentState,
            out GhostPlaybackState loadedState)
        {
            loadedState = null;
            if (rec == null || currentState == null)
                return false;

            // If the ghost is currently beyond visual range and the recording loops,
            // reset the loop phase so the ghost starts from the beginning of the recording
            // (at the pad) instead of wherever it is mid-flight (e.g. near the Mun).
            double watchLoadUT = Planetarium.fetch != null
                ? Planetarium.GetUniversalTime()
                : rec.EndUT;
            bool shouldLoopPlayback = host.ShouldLoopPlaybackForWatch(rec);
            double loopIntervalSeconds = shouldLoopPlayback
                ? host.GetLoopIntervalSecondsForWatch(rec, index)
                : 0.0;
            // #381: overlap dispatch is period < duration, not interval < 0.
            // #409: share the effective-loop-duration helper with ResolveWatchPlaybackUT.
            double watchRecDuration = GhostPlaybackEngine.EffectiveLoopDuration(rec);
            bool usesOverlapLooping = shouldLoopPlayback
                && GhostPlaybackLogic.IsOverlapLoop(loopIntervalSeconds, watchRecDuration);
            bool resetLoopPhaseForWatch = currentState.currentZone == RenderingZone.Beyond
                && shouldLoopPlayback
                && !usesOverlapLooping;
            if (resetLoopPhaseForWatch)
            {
                ResetLoopPhaseForWatch(index, currentState, rec);
                watchLoadUT = GhostPlaybackEngine.EffectiveLoopStartUT(rec);
            }
            else
            {
                watchLoadUT = ResolveWatchPlaybackUT(rec, currentState, watchLoadUT);
            }

            if (!host.Engine.EnsureGhostVisualsLoadedForWatch(index, rec, watchLoadUT,
                forceRebuildLoadedVisuals: resetLoopPhaseForWatch))
                return false;

            host.Engine.ghostStates.TryGetValue(index, out loadedState);
            return TryCommitWatchSessionStart(index, rec, loadedState);
        }

        internal bool TryCommitWatchSessionStart(int index, Recording rec, GhostPlaybackState loadedState)
        {
            if (rec == null || loadedState == null)
                return false;

            ClearLineageProtection();
            watchedRecordingIndex = index;
            watchedRecordingId = rec.RecordingId;
            watchStartTime = RealtimeNow();

            // Reset camera mode state for new watch session
            userModeOverride = false;
            currentCameraMode = WatchCameraMode.Free; // auto-detect will set this on first frame
            ClearRememberedWatchCameraStates();
            lastLoggedHorizonVectorKey = null;
            // Reset the one-shot EnsureGhostOrbitRenderers latch on every watch
            // session start. Entering watch while map view is already open primes
            // pendingMapFocusRestore to true without an off→on transition, so
            // UpdateMapFocusRestore's in-place reset wouldn't fire — a stale
            // latch from the previous session would then skip renderer creation
            // until the player toggles map view.
            ensureGhostOrbitRenderersAttempted = false;
            (lastMapViewEnabled, pendingMapFocusRestore) =
                InitializeMapFocusRestoreState(MapView.MapIsEnabled);
            return true;
        }

        internal void FinalizeAutomaticExitForTesting() =>
            ResetWatchState(preserveLineageProtection: true, destroyOverlapAnchor: false);

        private void ResetWatchState(bool preserveLineageProtection, bool destroyOverlapAnchor)
        {
            if (preserveLineageProtection)
                PreserveLineageProtectionOnExit();
            else
                ClearLineageProtection();

            watchedRecordingIndex = -1;
            watchedRecordingId = null;
            watchedOverlapCycleIndex = -1;
            overlapRetargetAfterUT = -1;
            if (destroyOverlapAnchor)
                DestroyOverlapCameraAnchor();
            else
                overlapCameraAnchor = null;
            overlapBridgeLastRetryFrame = -1;
            savedCameraVessel = null;
            savedCameraDistance = 0f;
            savedCameraPitch = 0f;
            savedCameraHeading = 0f;
            watchEndHoldUntilRealTime = -1;
            watchEndHoldMaxRealTime = -1;
            watchEndHoldPendingActivationUT = double.NaN;
            watchNoTargetFrames = 0;
            currentCameraMode = WatchCameraMode.Free;
            userModeOverride = false;
            ClearRememberedWatchCameraStates();
            lastLoggedWatchTargetMismatch = null;
            lastLoggedWatchFocusKey = null;
            lastLoggedHorizonVectorKey = null;
            lastMapViewEnabled = false;
            pendingMapFocusRestore = false;
            ensureGhostOrbitRenderersAttempted = false;
        }

        private void RestoreCameraAfterWatchExit(bool skipCameraRestore)
        {
            FlightCamera flightCamera = GetFlightCameraSafe();
            if (flightCamera != null)
                flightCamera.pivotTranslateSharpness = savedPivotSharpness;

            if (skipCameraRestore)
                return;

            var committed = RecordingStore.CommittedRecordings;
            Vessel activeVessel = GetActiveVesselSafe();
            string recVesselName = watchedRecordingIndex < committed.Count
                ? committed[watchedRecordingIndex].VesselName : "?";
            string targetName = savedCameraVessel != null
                ? savedCameraVessel.vesselName
                : (activeVessel?.vesselName ?? "unknown");
            ParsekLog.Info("CameraFollow",
                $"Exiting watch mode for recording #{watchedRecordingIndex} \"{recVesselName}\" \u2014 returning to {targetName}");

            if (flightCamera == null)
                return;

            if (savedCameraVessel != null && savedCameraVessel.gameObject != null)
            {
                flightCamera.SetTargetVessel(savedCameraVessel);
                flightCamera.SetDistance(savedCameraDistance);
                // savedCameraPitch / savedCameraHeading are stored in degrees; KSP wants radians.
                flightCamera.camPitch = savedCameraPitch * Mathf.Deg2Rad;
                flightCamera.camHdg = savedCameraHeading * Mathf.Deg2Rad;
                ParsekLog.Verbose("CameraFollow",
                    $"FlightCamera.SetTargetVessel restored to {savedCameraVessel.vesselName}, distance={savedCameraDistance.ToString("F1", CultureInfo.InvariantCulture)}");
            }
            else if (activeVessel != null)
            {
                flightCamera.SetTargetVessel(activeVessel);
                ParsekLog.Verbose("CameraFollow",
                    $"FlightCamera.SetTargetVessel restored to {activeVessel.vesselName}, distance={savedCameraDistance.ToString("F1", CultureInfo.InvariantCulture)}");
            }
        }

        /// <summary>
        /// Resets the loop phase offset so a beyond-range looping ghost restarts from
        /// the beginning of the recording when the player enters watch mode.
        /// </summary>
        private void ResetLoopPhaseForWatch(int index, GhostPlaybackState gs, Recording rec)
        {
            double currentUT = Planetarium.GetUniversalTime();
            if (!host.TryGetLoopScheduleForWatch(
                    rec,
                    index,
                    out _,
                    out double scheduleStartUT,
                    out _,
                    out double intervalSeconds))
            {
                return;
            }

            // #381: cycleDuration = launch-to-launch period (clamped). Dead-code fallback removed.
            double cycleDuration = Math.Max(intervalSeconds, LoopTiming.MinCycleDuration);

            var loopPhaseOffsets = host.Engine.loopPhaseOffsets;

            // Current elapsed time (with any existing offset)
            double elapsed = currentUT - scheduleStartUT;
            double existingOffset;
            if (loopPhaseOffsets.TryGetValue(index, out existingOffset))
                elapsed += existingOffset;

            // Compute where we are in the current cycle
            int curCycle = (int)Math.Floor(elapsed / cycleDuration);
            if (curCycle < 0) curCycle = 0;
            double cycleTime = elapsed - (curCycle * cycleDuration);

            // Shift elapsed so cycleTime becomes 0 (= recording start)
            double newOffset = (existingOffset) - cycleTime;
            loopPhaseOffsets[index] = newOffset;

            // Re-show the ghost so the camera has something to target
            PrimeLoopWatchResetState(gs);

            ParsekLog.Info("CameraFollow",
                string.Format(CultureInfo.InvariantCulture,
                    "Watch mode loop reset: ghost #{0} \"{1}\" cycleTime={2:F1}s -> offset={3:F1}s (ghost repositioned to recording start)",
                    index, rec.VesselName, cycleTime, newOffset));
        }

        internal static void PrimeLoopWatchResetState(GhostPlaybackState state)
        {
            if (state == null)
                return;

            state.currentZone = RenderingZone.Physics;
            state.playbackIndex = 0;
            state.partEventIndex = 0;
            state.pauseHidden = false;
            state.explosionFired = false;
        }

        /// <summary>
        /// Exit watch mode: return camera to the active vessel.
        /// When skipCameraRestore is true (switching between ghosts), the camera is not restored.
        /// </summary>
        internal void ExitWatchMode(bool skipCameraRestore = false, bool preserveLineageProtection = false)
        {
            if (watchedRecordingIndex < 0) return;
            RestoreCameraAfterWatchExit(skipCameraRestore);

            // Remove input locks
            RemoveWatchModeControlLockSafe();
            ParsekLog.Verbose("CameraFollow", $"InputLockManager control lock \"{WatchModeLockId}\" removed");

            ResetWatchState(preserveLineageProtection, destroyOverlapAnchor: true);
        }

        /// <summary>
        /// Determines whether a vessel's flight situation is safe for unattended flight.
        /// Safe means the vessel will not crash or deorbit while the player watches a ghost.
        /// </summary>
        internal static bool IsVesselSituationSafe(Vessel.Situations situation, double periapsis, double atmosphereAltitude)
        {
            switch (situation)
            {
                case Vessel.Situations.LANDED:
                case Vessel.Situations.SPLASHED:
                case Vessel.Situations.PRELAUNCH:
                case Vessel.Situations.DOCKED:
                    return true;

                case Vessel.Situations.ORBITING:
                    return periapsis > atmosphereAltitude;

                case Vessel.Situations.FLYING:
                case Vessel.Situations.SUB_ORBITAL:
                case Vessel.Situations.ESCAPING:
                    return false;

                default:
                    return false;
            }
        }

        // === Horizon-locked camera mode — pure static methods ===

        /// <summary>
        /// Determines whether the camera should auto-select horizon-locked mode
        /// based on altitude relative to the body's atmosphere or surface threshold.
        /// </summary>
        internal static bool ShouldAutoHorizonLock(bool hasAtmosphere, double atmosphereDepth, double altitude)
        {
            return DistanceThresholds.GhostFlight.ShouldAutoHorizonLock(
                hasAtmosphere, atmosphereDepth, altitude);
        }

        /// <summary>
        /// Atmospheric watch mode should follow surface-relative prograde.
        /// Outside the atmosphere, preserve the existing playback/inertial heading.
        /// </summary>
        internal static bool ShouldUseSurfaceRelativeWatchHeading(
            bool hasAtmosphere, double atmosphereDepth, double altitude)
        {
            return hasAtmosphere && altitude < atmosphereDepth;
        }

        /// <summary>
        /// Converts playback velocity to a body-relative velocity suitable for
        /// horizon-lock heading decisions. Playback samples are recorded in KSP's
        /// world/orbital frame, so surface-relative consumers must subtract the
        /// body's rotating-frame velocity at the ghost position.
        /// </summary>
        internal static Vector3 ComputeSurfaceRelativeVelocity(
            Vector3 playbackVelocity, Vector3 rotatingFrameVelocity)
        {
            return playbackVelocity - rotatingFrameVelocity;
        }

        /// <summary>
        /// Computes the forward vector used by horizon-locked watch mode after
        /// converting playback velocity to the rotating body's surface frame.
        /// Returns the effective heading velocity and the fallback source for diagnostics/tests.
        /// </summary>
        internal static (Vector3 forward, Vector3 horizonVelocity, Vector3 headingVelocity,
            HorizonForwardSource source) ComputeWatchHorizonForward(
                Vector3 up, Vector3 playbackVelocity, Vector3 rotatingFrameVelocity,
                Vector3 lastForward)
        {
            Vector3 headingVelocity = ComputeSurfaceRelativeVelocity(
                playbackVelocity, rotatingFrameVelocity);
            Vector3 horizonVelocity = Vector3.ProjectOnPlane(headingVelocity, up);
            Vector3 forward = horizonVelocity;
            HorizonForwardSource source = HorizonForwardSource.ProjectedVelocity;
            if (forward.sqrMagnitude < 0.01f)
            {
                forward = Vector3.ProjectOnPlane(lastForward, up);
                if (forward.sqrMagnitude < 0.0001f)
                {
                    forward = Vector3.Cross(up, Vector3.right);
                    if (forward.sqrMagnitude < 0.0001f)
                        forward = Vector3.Cross(up, Vector3.forward);
                    source = HorizonForwardSource.ArbitraryPerpendicularFallback;
                }
                else
                {
                    source = HorizonForwardSource.LastForwardFallback;
                }
            }
            forward.Normalize();
            return (forward, horizonVelocity, headingVelocity, source);
        }

        /// <summary>
        /// Computes the full horizon-lock basis used by watch mode, including the
        /// atmospheric decision for whether surface-relative heading should be used.
        /// </summary>
        internal static (Vector3 forward, Vector3 horizonVelocity, Vector3 headingVelocity,
            Vector3 appliedFrameVelocity, HorizonForwardSource source)
            ComputeWatchHorizonBasis(
                bool hasAtmosphere, double atmosphereDepth, double altitude,
                Vector3 up, Vector3 playbackVelocity, Vector3 rotatingFrameVelocity,
                Vector3 lastForward)
        {
            Vector3 appliedFrameVelocity = ShouldUseSurfaceRelativeWatchHeading(
                hasAtmosphere, atmosphereDepth, altitude)
                ? rotatingFrameVelocity
                : Vector3.zero;
            var (forward, horizonVelocity, headingVelocity, source) =
                ComputeWatchHorizonForward(
                    up, playbackVelocity, appliedFrameVelocity, lastForward);
            return (forward, horizonVelocity, headingVelocity, appliedFrameVelocity, source);
        }

        /// <summary>
        /// Computes the horizon-plane forward direction from velocity and up vector.
        /// Projects velocity onto the horizon plane (perpendicular to up). Falls back
        /// to lastForward when velocity is near zero, then to an arbitrary perpendicular.
        /// Pure vector math — testable outside Unity runtime.
        /// </summary>
        internal static Vector3 ComputeHorizonForward(Vector3 up, Vector3 velocity, Vector3 lastForward)
        {
            var (forward, _, _, _) = ComputeWatchHorizonForward(
                up, velocity, Vector3.zero, lastForward);
            return forward;
        }

        /// <summary>
        /// Computes the horizon-aligned rotation for the camera proxy.
        /// Wraps ComputeHorizonForward with Quaternion.LookRotation.
        /// </summary>
        internal static (Quaternion rotation, Vector3 forward) ComputeHorizonRotation(
            Vector3 up, Vector3 velocity, Vector3 lastForward)
        {
            Vector3 forward = ComputeHorizonForward(up, velocity, lastForward);
            return (Quaternion.LookRotation(forward, up), forward);
        }

        /// <summary>
        /// Compensates camPitch/camHdg when switching the camera target between
        /// transforms with different rotations, preventing a visual snap.
        /// Decomposes the current camera orbit direction from the old frame
        /// into equivalent angles in the new frame.
        /// </summary>
        internal static (float pitch, float hdg) CompensateCameraAngles(
            Quaternion oldTargetRot, Quaternion newTargetRot, float pitch, float hdg)
        {
            // Convert current pitch/hdg to a direction vector in world space
            // FlightCamera: pitch = elevation (degrees), hdg = azimuth (degrees)
            // Camera orbits at (hdg, pitch) relative to the target's local frame
            float pitchRad = pitch * Mathf.Deg2Rad;
            float hdgRad = hdg * Mathf.Deg2Rad;

            // Spherical to local direction (KSP convention: Y=up, Z=forward, X=right)
            Vector3 localDir = new Vector3(
                Mathf.Sin(hdgRad) * Mathf.Cos(pitchRad),
                Mathf.Sin(pitchRad),
                Mathf.Cos(hdgRad) * Mathf.Cos(pitchRad));

            // Transform to world space via old target, then back to new target's local space
            Vector3 worldDir = RotateVectorByQuaternion(oldTargetRot, localDir);
            Vector3 newLocalDir = InverseRotateVectorByQuaternion(newTargetRot, worldDir);

            // Decompose back to pitch/hdg
            float newPitch = Mathf.Asin(Mathf.Clamp(newLocalDir.y, -1f, 1f)) * Mathf.Rad2Deg;
            float newHdg = Mathf.Atan2(newLocalDir.x, newLocalDir.z) * Mathf.Rad2Deg;

            return (newPitch, newHdg);
        }

        internal static (float pitch, float hdg) DecomposeOrbitDirectionInTargetFrame(
            Vector3 worldOrbitDirection,
            Quaternion targetRotation)
        {
            Vector3 normalizedDirection = worldOrbitDirection.normalized;
            Vector3 localDir = InverseRotateVectorByQuaternion(targetRotation, normalizedDirection);
            float pitch = Mathf.Asin(Mathf.Clamp(localDir.y, -1f, 1f)) * Mathf.Rad2Deg;
            float hdg = Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg;
            return (pitch, hdg);
        }

        internal static Vector3 RotateVectorByQuaternion(Quaternion rotation, Vector3 direction)
        {
            Quaternion normalized = NormalizeQuaternion(rotation);
            Vector3 axis = new Vector3(normalized.x, normalized.y, normalized.z);
            float scalar = normalized.w;
            return 2f * Vector3.Dot(axis, direction) * axis
                + (scalar * scalar - Vector3.Dot(axis, axis)) * direction
                + 2f * scalar * Vector3.Cross(axis, direction);
        }

        internal static Vector3 InverseRotateVectorByQuaternion(Quaternion rotation, Vector3 direction)
        {
            Quaternion normalized = NormalizeQuaternion(rotation);
            Quaternion inverse = new Quaternion(
                -normalized.x,
                -normalized.y,
                -normalized.z,
                normalized.w);
            return RotateVectorByQuaternion(inverse, direction);
        }

        internal static Quaternion NormalizeQuaternion(Quaternion rotation)
        {
            float sqrMagnitude =
                rotation.x * rotation.x
                + rotation.y * rotation.y
                + rotation.z * rotation.z
                + rotation.w * rotation.w;
            if (sqrMagnitude <= 1e-12f
                || float.IsNaN(sqrMagnitude)
                || float.IsInfinity(sqrMagnitude))
            {
                return new Quaternion(0f, 0f, 0f, 1f);
            }

            float inverseMagnitude = 1f / Mathf.Sqrt(sqrMagnitude);
            return new Quaternion(
                rotation.x * inverseMagnitude,
                rotation.y * inverseMagnitude,
                rotation.z * inverseMagnitude,
                rotation.w * inverseMagnitude);
        }

        // === Horizon-locked camera mode — instance methods ===

        /// <summary>
        /// Resolves the CelestialBody for the watched ghost, reusing the
        /// ghost's cached audio body lookup to avoid per-frame linear scan.
        /// </summary>
        private CelestialBody ResolveBody(GhostPlaybackState state)
        {
            string bodyName = state.lastInterpolatedBodyName;
            if (string.IsNullOrEmpty(bodyName)) return null;
            if (state.cachedAudioBody != null && state.cachedAudioBodyName == bodyName)
                return state.cachedAudioBody;
            var body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            if (body != null)
            {
                state.cachedAudioBody = body;
                state.cachedAudioBodyName = bodyName;
            }
            return body;
        }

        /// <summary>
        /// Returns the correct camera target transform based on current mode.
        /// For cameraPivot transforms with a horizonProxy child, returns horizonProxy
        /// when in HorizonLocked mode. For anchor transforms (no children), returns
        /// the anchor itself (Free mode during explosion/bridge holds).
        /// </summary>
        private Transform GetWatchTarget(Transform cameraPivot)
        {
            if (currentCameraMode != WatchCameraMode.HorizonLocked || cameraPivot == null)
                return cameraPivot;
            var proxy = cameraPivot.Find("horizonProxy");
            return proxy != null ? proxy : cameraPivot;
        }

        /// <summary>
        /// Switches the FlightCamera target between cameraPivot and horizonProxy
        /// based on current mode, with pitch/heading compensation to prevent snap.
        /// </summary>
        private void ApplyCameraTarget(GhostPlaybackState state)
        {
            if (FlightCamera.fetch == null || state == null) return;
            Transform oldTarget = FlightCamera.fetch.Target;
            Transform newTarget = currentCameraMode == WatchCameraMode.HorizonLocked
                ? (state.horizonProxy ?? state.cameraPivot)
                : state.cameraPivot;
            if (newTarget == null) newTarget = state.cameraPivot;
            if (newTarget == null || newTarget == oldTarget) return;

            // Compensate pitch/heading to prevent visual snap on mode switch.
            // CompensateCameraAngles works in degrees; KSP's FlightCamera uses radians,
            // so convert at the boundary on both the read and the write.
            Quaternion oldRot = oldTarget != null ? oldTarget.rotation : Quaternion.identity;
            Quaternion newRot = newTarget.rotation;
            var (newPitchDeg, newHdgDeg) = CompensateCameraAngles(
                oldRot, newRot,
                FlightCamera.fetch.camPitch * Mathf.Rad2Deg,
                FlightCamera.fetch.camHdg * Mathf.Rad2Deg);

            FlightCamera.fetch.SetTargetTransform(newTarget);
            FlightCamera.fetch.camPitch = newPitchDeg * Mathf.Deg2Rad;
            FlightCamera.fetch.camHdg = newHdgDeg * Mathf.Deg2Rad;
        }

        /// <summary>
        /// Toggles between Free and HorizonLocked camera modes.
        /// Sets userModeOverride to prevent auto-switching.
        /// </summary>
        internal void ToggleCameraMode()
        {
            if (watchedRecordingIndex < 0) return;

            var state = FindWatchedGhostState();
            if (state == null) return;

            WatchCameraMode nextMode = currentCameraMode == WatchCameraMode.Free
                ? WatchCameraMode.HorizonLocked
                : WatchCameraMode.Free;
            if (TryCaptureActiveWatchCameraState(out var previousModeState))
                RememberWatchCameraStateAsTargetRelative(previousModeState);
            userModeOverride = true;
            currentCameraMode = nextMode;
            lastLoggedHorizonVectorKey = null;

            PrimeWatchTargetOrientation(state);
            if (!TryRestoreRememberedWatchCameraState(
                    state,
                    nextMode,
                    modeOverride: true,
                    logContext: "toggle-restore"))
            {
                ApplyCameraTarget(state);
            }
            RememberCurrentWatchCameraState();

            ParsekLog.Info("CameraFollow",
                $"Watch camera mode toggled to {currentCameraMode} (user override)");
            ParsekLog.ScreenMessage(
                currentCameraMode == WatchCameraMode.HorizonLocked
                    ? "Camera: Horizon Locked"
                    : "Camera: Free",
                2f);
        }

        /// <summary>
        /// Updates the horizonProxy rotation and auto-detects camera mode each frame.
        /// Called from UpdateWatchCamera after the camera position is driven.
        /// </summary>
        private void UpdateHorizonProxy(GhostPlaybackState state)
        {
            if (state.horizonProxy == null) return;

            CelestialBody body = ResolveBody(state);

            // Keep the proxy rotation current before any mode switch so camera-target
            // compensation sees the correct frame on the first HorizonLocked frame.
            if (body != null)
                UpdateHorizonProxyRotation(state, body);

            // Auto-detect mode (unless user overrode)
            if (!userModeOverride && body != null)
            {
                bool shouldLock = ShouldAutoHorizonLock(
                    body.atmosphere, body.atmosphereDepth, state.lastInterpolatedAltitude);
                var autoMode = shouldLock ? WatchCameraMode.HorizonLocked : WatchCameraMode.Free;
                if (autoMode != currentCameraMode)
                {
                    // Mirror the V-toggle capture — a ghost auto-crossing the
                    // atmosphere boundary must not leave a stale world-orbit
                    // vector in the outgoing mode's remembered slot, or a later
                    // auto-switch back will replay it and drift the camera.
                    if (TryCaptureActiveWatchCameraState(out var previousModeState))
                        RememberWatchCameraStateAsTargetRelative(previousModeState);
                    currentCameraMode = autoMode;
                    lastLoggedHorizonVectorKey = null;
                    if (!TryRestoreRememberedWatchCameraState(
                            state,
                            autoMode,
                            modeOverride: false,
                            logContext: "auto-mode-restore"))
                    {
                        ApplyCameraTarget(state);
                    }
                    RememberCurrentWatchCameraState();
                    ParsekLog.Info("CameraFollow",
                        string.Format(CultureInfo.InvariantCulture,
                            "Watch camera auto-switched to {0} (alt={1:F0}m, body={2})",
                            currentCameraMode, state.lastInterpolatedAltitude,
                            state.lastInterpolatedBodyName));
                }
            }
        }

        private void PrimeWatchTargetOrientation(GhostPlaybackState state)
        {
            if (state == null || currentCameraMode != WatchCameraMode.HorizonLocked)
                return;

            CelestialBody body = ResolveBody(state);
            if (body == null)
                return;

            UpdateHorizonProxyRotation(state, body);
        }

        private void UpdateHorizonProxyRotation(GhostPlaybackState state, CelestialBody body)
        {
            if (state?.horizonProxy == null || state.cameraPivot == null || body == null)
                return;

            Vector3 ghostPos = state.cameraPivot.position;
            Vector3 up = (ghostPos - body.position).normalized;
            Vector3 bodyFrameVelocity = (Vector3)body.getRFrmVel(ghostPos);
            var (forward, horizonVelocity, headingVelocity,
                appliedFrameVelocity, source) =
                ComputeWatchHorizonBasis(
                    body.atmosphere, body.atmosphereDepth, state.lastInterpolatedAltitude,
                    up, state.lastInterpolatedVelocity, bodyFrameVelocity,
                    state.lastValidHorizonForward);
            state.horizonProxy.rotation = Quaternion.LookRotation(forward, up);
            state.lastValidHorizonForward = forward;
            LogHorizonForwardState(state, up, forward, horizonVelocity,
                headingVelocity, bodyFrameVelocity, appliedFrameVelocity, source);
        }

        /// <summary>
        /// Pure static helper: computes the new watch index after a recording is deleted.
        /// Returns (newIndex, newId) where newIndex=-1 means watch mode should exit.
        /// </summary>
        internal static (int newIndex, string newId) ComputeWatchIndexAfterDelete(
            int watchedIndex, string watchedId, int deletedIndex,
            IReadOnlyList<Recording> recordings)
        {
            if (deletedIndex == watchedIndex)
                return (-1, null);

            int newIndex = watchedIndex;
            if (deletedIndex < watchedIndex)
                newIndex = watchedIndex - 1;

            // Verify by ID -- the recording at the new index should match
            if (newIndex >= 0 && newIndex < recordings.Count &&
                recordings[newIndex].RecordingId == watchedId)
            {
                return (newIndex, watchedId);
            }

            // ID mismatch -- scan for correct index
            for (int j = 0; j < recordings.Count; j++)
            {
                if (recordings[j].RecordingId == watchedId)
                    return (j, watchedId);
            }

            // Not found -- exit watch mode
            return (-1, null);
        }

        /// <summary>
        /// Finds the next recording to auto-follow when the watched recording ends.
        /// Handles two cases:
        /// 1. Chain continuation: next segment with same ChainId, ChainBranch 0, ChainIndex + 1
        /// 2. Tree branching: child recording via ChildBranchPointId, preferring same VesselPersistentId
        /// Returns the committed-list index of the next recording, or -1 if none found.
        /// Only returns a target if its ghost is already active (spawned and playing).
        /// </summary>
        internal int FindNextWatchTarget(int currentIndex, Recording currentRec)
        {
            ParsekLog.VerboseRateLimited("Watch", "findNextWatch",
                $"FindNextWatchTarget: currentIndex={currentIndex}, chainId={currentRec.ChainId ?? "null"}, chainIndex={currentRec.ChainIndex}, treeId={currentRec.TreeId ?? "null"}, childBpId={currentRec.ChildBranchPointId ?? "null"}");

            int result = GhostPlaybackLogic.FindNextWatchTarget(
                currentRec,
                RecordingStore.CommittedRecordings,
                RecordingStore.CommittedTrees,
                HasActiveGhost);

            if (result >= 0)
                ParsekLog.Info("Watch", $"FindNextWatchTarget: found target at index {result}");

            return result;
        }

        /// <summary>
        /// Transfers watch mode from the current recording to the next segment.
        /// Preserves camera state (no restore to player vessel) since we're switching between ghosts.
        /// </summary>
        internal bool TransferWatchToNextSegment(int nextIndex)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (nextIndex < 0 || nextIndex >= committed.Count) return false;

            string oldName = watchedRecordingIndex >= 0 && watchedRecordingIndex < committed.Count
                ? committed[watchedRecordingIndex].VesselName : "?";
            string newName = committed[nextIndex].VesselName;

            ParsekLog.Info("CameraFollow",
                $"Auto-following: #{watchedRecordingIndex} \"{oldName}\" -> #{nextIndex} \"{newName}\"");

            // Verify the target ghost exists before transferring
            var ghostStates = host.Engine.ghostStates;
            GhostPlaybackState gs;
            if (!ghostStates.TryGetValue(nextIndex, out gs) || gs == null || gs.ghost == null)
            {
                ParsekLog.Warn("CameraFollow",
                    $"Auto-follow target #{nextIndex} has no active ghost - deferring transfer");
                return false;
            }

            // Preserve original camera state across the transition
            // (ExitWatchMode clears these, but we want Backspace to restore to the original vessel)
            Vessel preservedVessel = savedCameraVessel;
            float preservedDistance = savedCameraDistance;
            float preservedPitch = savedCameraPitch;
            float preservedHeading = savedCameraHeading;
            WatchCameraTransitionState transferCameraState = default(WatchCameraTransitionState);
            bool hasTransferCameraState = TryCaptureActiveWatchCameraState(out transferCameraState);
            if (hasTransferCameraState)
                RememberWatchCameraState(transferCameraState);
            bool preservedHasRememberedFreeCameraState = hasRememberedFreeCameraState;
            WatchCameraTransitionState preservedFreeCameraState = rememberedFreeCameraState;
            bool preservedHasRememberedHorizonLockedCameraState = hasRememberedHorizonLockedCameraState;
            WatchCameraTransitionState preservedHorizonLockedCameraState = rememberedHorizonLockedCameraState;
            // Preserve camera mode across chain transfers — user's V toggle should stick
            var preservedCameraMode = currentCameraMode;
            bool preservedModeOverride = userModeOverride;

            // Switch watch mode: exit old (preserving camera position), enter new
            ExitWatchMode(skipCameraRestore: true);

            // Set up new watch state
            watchedRecordingIndex = nextIndex;
            watchedRecordingId = committed[nextIndex].RecordingId;

            // Restore saved camera state so Backspace returns to original vessel
            savedCameraVessel = preservedVessel;
            savedCameraDistance = preservedDistance;
            savedCameraPitch = preservedPitch;
            savedCameraHeading = preservedHeading;
            currentCameraMode = preservedCameraMode;
            userModeOverride = preservedModeOverride;
            hasRememberedFreeCameraState = preservedHasRememberedFreeCameraState;
            rememberedFreeCameraState = preservedFreeCameraState;
            hasRememberedHorizonLockedCameraState = preservedHasRememberedHorizonLockedCameraState;
            rememberedHorizonLockedCameraState = preservedHorizonLockedCameraState;
            watchedOverlapCycleIndex = gs.loopCycleIndex;
            watchNoTargetFrames = 0;
            if (FlightCamera.fetch != null)
                FlightCamera.fetch.pivotTranslateSharpness = 0f;
            PrimeWatchTargetOrientation(gs);
            bool restoredTransferCameraState = hasTransferCameraState
                && TryApplySwitchedWatchCameraState(
                    gs,
                    ResolveSwitchCameraStateForGhost(transferCameraState, gs),
                    logContext: "segment-apply");
            if (!restoredTransferCameraState)
                ApplyCameraTarget(gs);
            RememberCurrentWatchCameraState();
            if (FlightCamera.fetch?.transform?.parent != null && gs.cameraPivot != null)
                FlightCamera.fetch.transform.parent.position = gs.cameraPivot.position;
            InputLockManager.SetControlLock(WatchModeLockMask, WatchModeLockId);
            ParsekLog.Verbose("CameraFollow",
                $"InputLockManager control lock \"{WatchModeLockId}\" re-set after transfer");

            watchEndHoldUntilRealTime = -1;
            watchEndHoldMaxRealTime = -1;
            watchEndHoldPendingActivationUT = double.NaN;

            // Reset watch start time so the zone-exemption logging starts fresh
            // for the new segment (no stale elapsed time from the previous segment)
            watchStartTime = RealtimeNow();

            ParsekLog.Info("CameraFollow",
                $"TransferWatch re-target: ghost #{nextIndex} \"{newName}\"" +
                $" target='{(FlightCamera.fetch?.Target != null ? FlightCamera.fetch.Target.name : "null")}'" +
                $" cycle={watchedOverlapCycleIndex} mode={currentCameraMode}" +
                $" ghostPos=({gs.ghost.transform.position.x:F1},{gs.ghost.transform.position.y:F1},{gs.ghost.transform.position.z:F1})" +
                $" pivotPos=({gs.cameraPivot.position.x:F1},{gs.cameraPivot.position.y:F1},{gs.cameraPivot.position.z:F1})" +
                $" camDist={FlightCamera.fetch.Distance:F1}" +
                $" watchStartTime={watchStartTime.ToString("F2", CultureInfo.InvariantCulture)}");
            LogWatchFocusStateChanged(gs, force: true, context: "transfer");
            return true;
        }

        /// <summary>
        /// Checks whether the watched ghost is still active. Exits watch mode if the
        /// primary ghost is gone and no matching overlap cycle exists.
        /// </summary>
        internal void ValidateWatchedGhostStillActive()
        {
            if (watchedRecordingIndex < 0) return;
            if (HasPendingOverlapBridgeRetarget()) return;
            if (host.Engine.TryGetGhostState(watchedRecordingIndex, out var primary) && primary != null) return;

            // Check overlap ghosts
            bool hasOverlap = false;
            if (watchedOverlapCycleIndex >= 0)
            {
                List<GhostPlaybackState> overlaps;
                if (host.Engine.TryGetOverlapGhosts(watchedRecordingIndex, out overlaps))
                {
                    for (int i = 0; i < overlaps.Count; i++)
                    {
                        if (overlaps[i]?.loopCycleIndex == watchedOverlapCycleIndex)
                        { hasOverlap = true; break; }
                    }
                }
            }
            if (!hasOverlap && watchedOverlapCycleIndex != -2) // -2 = holding after explosion
            {
                ParsekLog.Info("CameraFollow",
                    $"Watched ghost #{watchedRecordingIndex} no longer active \u2014 exiting watch mode");
                ExitWatchModePreservingLineage();
            }
        }

        /// <summary>
        /// Drive the camera pivot position every frame during watch mode.
        /// pivotTranslateSharpness is zeroed for the entire watch session to prevent
        /// KSP from pulling the camera back toward the active vessel.
        /// </summary>
        internal void UpdateWatchCamera()
        {
            if (watchedRecordingIndex < 0) return;

            // Phase 7 of Rewind-to-Staging (design §3.3): if the watched
            // recording entered the session-suppressed subtree (e.g. player
            // just invoked an RP that supersedes the watched recording's
            // ancestor), exit watch mode so the camera falls back to the
            // newly-spawned provisional re-fly vessel / active vessel.
            if (SessionSuppressionState.IsActive
                && SessionSuppressionState.IsSuppressedRecordingIndex(watchedRecordingIndex))
            {
                ParsekLog.Info("ReFlySession",
                    $"Watch mode exited: anchor recording #{watchedRecordingIndex} " +
                    $"suppressed by session");
                ExitWatchModePreservingLineage();
                return;
            }

            UpdateMapFocusRestore();

            // Watch-end hold timer: after non-looped playback completes, the ghost
            // is held visible for a few seconds so the user sees the terminal state.
            // During the hold, try to auto-follow to a continuation ghost each frame
            // (continuation may not exist yet at completion time -- spawn race condition).
            // Once expired, destroy the ghost and exit watch mode.
            if (ProcessWatchEndHoldTimer())
                return;

            // Safety net: orphaned -2 state with invalid timer -- clear to -1 so
            // normal logic can take over (or exit watch mode via safety net below)
            if (watchedOverlapCycleIndex == -2 && overlapRetargetAfterUT <= 0)
            {
                ParsekLog.Warn("CameraFollow",
                    $"Orphaned hold state (overlapRetargetAfterUT={overlapRetargetAfterUT:F1}) \u2014 clearing");
                watchedOverlapCycleIndex = -1;
                DestroyOverlapCameraAnchor();
            }

            // Delayed re-target after watched overlap cycle exploded
            if (TryResolveOverlapRetarget())
                return;
            if (TryResolveOverlapBridgeRetarget())
                return;

            // Find the ghost we're actually following -- may be an overlap ghost, not the primary
            GhostPlaybackState state = FindWatchedGhostState();

            FlightCamera flightCamera = GetFlightCameraSafe();
            Transform cameraTransform = flightCamera != null ? flightCamera.transform : null;
            Transform cameraParent = cameraTransform != null ? cameraTransform.parent : null;
            string cameraInfraReason = ClassifyWatchCameraInfrastructure(
                flightCamera != null,
                cameraTransform != null,
                cameraParent != null);
            if (cameraInfraReason != "ready")
            {
                ParsekLog.WarnRateLimited(
                    "CameraFollow",
                    "watch-camera-infra:" + watchedRecordingIndex.ToString(CultureInfo.InvariantCulture) +
                    ":" + cameraInfraReason,
                    BuildWatchCameraInfrastructureMessage(
                        watchedRecordingIndex,
                        watchedRecordingId,
                        cameraInfraReason,
                        state?.vesselName,
                        watchedOverlapCycleIndex,
                        GetLoadedSceneNameSafe(),
                        state != null,
                        state?.ghost != null,
                        state?.cameraPivot != null),
                    minIntervalSeconds: 5.0);
                return;
            }

            if (state == null || state.cameraPivot == null)
            {
                // No valid target -- count frames and exit if persistent
                watchNoTargetFrames++;
                if (watchNoTargetFrames >= 3)
                {
                    ParsekLog.Warn("CameraFollow",
                        $"No valid camera target for {watchNoTargetFrames} frames \u2014 exiting watch mode");
                    ExitWatchModePreservingLineage();
                }
                return;
            }

            // Valid target found -- reset safety counter
            watchNoTargetFrames = 0;

            // Keep sharpness zeroed -- KSP resets it on various events
            flightCamera.pivotTranslateSharpness = 0f;

            // Drive camera orbit center to the cameraPivot's world position
            cameraParent.position = state.cameraPivot.position;

            // Update horizon proxy rotation and auto-detect camera mode
            UpdateHorizonProxy(state);
            LogWatchTargetMismatch(state);
            LogWatchFocusStateChanged(state);
        }

        private void LogMapFocusRestoreDecision(
            uint ghostPid,
            Vessel ghostVessel,
            bool hasPlanetariumCamera,
            string reason)
        {
            bool hasGhostVessel = ghostVessel != null;
            bool hasMapObject = hasGhostVessel && ghostVessel.mapObject != null;
            bool hasOrbitRenderer = hasGhostVessel && ghostVessel.orbitRenderer != null;
            string stateKey = string.Format(CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}",
                string.IsNullOrEmpty(watchedRecordingId) ? "(none)" : watchedRecordingId,
                watchedRecordingIndex,
                ghostPid,
                reason ?? "(none)");
            ParsekLog.VerboseOnChange(
                "GhostMap",
                "map-focus-restore",
                stateKey,
                BuildMapFocusRestoreDecisionMessage(
                    watchedRecordingIndex,
                    ghostPid,
                    hasGhostVessel,
                    hasMapObject,
                    hasOrbitRenderer,
                    hasPlanetariumCamera,
                    reason));
        }

        private void UpdateMapFocusRestore()
        {
            bool wasPending = pendingMapFocusRestore;
            (lastMapViewEnabled, pendingMapFocusRestore, bool shouldAttemptRestore) =
                AdvanceMapFocusRestoreState(lastMapViewEnabled, pendingMapFocusRestore, MapView.MapIsEnabled);
            // Reset the "already attempted to create renderers this pending window"
            // latch whenever pendingMapFocusRestore transitions off→on, so a later
            // pending restore still triggers one ensure pass even if the previous
            // attempt didn't find the ghost vessel yet.
            if (pendingMapFocusRestore && !wasPending)
                ensureGhostOrbitRenderersAttempted = false;
            if (!shouldAttemptRestore)
                return;

            uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(watchedRecordingIndex);
            if (ghostPid == 0)
            {
                bool hasPlanetariumCamera = PlanetariumCamera.fetch != null;
                string reason = ClassifyMapFocusRestore(
                    ghostPid,
                    hasGhostVessel: false,
                    hasMapObject: false,
                    hasPlanetariumCamera: hasPlanetariumCamera);
                LogMapFocusRestoreDecision(ghostPid, null, hasPlanetariumCamera, reason);
                return;
            }

            Vessel ghostVessel = FlightRecorder.FindVesselByPid(ghostPid);
            if (ghostVessel == null)
            {
                bool hasPlanetariumCamera = PlanetariumCamera.fetch != null;
                string reason = ClassifyMapFocusRestore(
                    ghostPid,
                    hasGhostVessel: false,
                    hasMapObject: false,
                    hasPlanetariumCamera: hasPlanetariumCamera);
                LogMapFocusRestoreDecision(ghostPid, null, hasPlanetariumCamera, reason);
                return;
            }

            if ((ghostVessel.mapObject == null || ghostVessel.orbitRenderer == null)
                && MapView.fetch != null
                && !ensureGhostOrbitRenderersAttempted)
            {
                GhostMapPresence.EnsureGhostOrbitRenderers();
                ensureGhostOrbitRenderersAttempted = true;
            }

            PlanetariumCamera planetariumCamera = PlanetariumCamera.fetch;
            string restoreReason = ClassifyMapFocusRestore(
                ghostPid,
                hasGhostVessel: true,
                hasMapObject: ghostVessel.mapObject != null,
                hasPlanetariumCamera: planetariumCamera != null);
            if (restoreReason != "ready")
            {
                LogMapFocusRestoreDecision(ghostPid, ghostVessel, planetariumCamera != null, restoreReason);
                return;
            }

            LogMapFocusRestoreDecision(ghostPid, ghostVessel, planetariumCamera != null, restoreReason);
            planetariumCamera.SetTarget(ghostVessel.mapObject);
            pendingMapFocusRestore = false;
            ParsekLog.Info("GhostMap",
                $"Restored map focus to watched ghost '{ghostVessel.vesselName}' (recIndex={watchedRecordingIndex})");
        }

        internal static (bool lastMapViewEnabled, bool pendingMapFocusRestore)
            InitializeMapFocusRestoreState(bool mapViewEnabled)
        {
            return (mapViewEnabled, mapViewEnabled);
        }

        internal static (bool lastMapViewEnabled, bool pendingMapFocusRestore, bool shouldAttemptRestore)
            AdvanceMapFocusRestoreState(
                bool lastMapViewEnabled,
                bool pendingMapFocusRestore,
                bool mapViewEnabled)
        {
            if (!mapViewEnabled)
                return (false, false, false);

            if (!lastMapViewEnabled)
                pendingMapFocusRestore = true;

            return (true, pendingMapFocusRestore, pendingMapFocusRestore);
        }

        internal static bool CanRestoreMapFocus(
            uint ghostPid,
            bool hasGhostVessel,
            bool hasMapObject,
            bool hasPlanetariumCamera)
        {
            return ClassifyMapFocusRestore(
                ghostPid,
                hasGhostVessel,
                hasMapObject,
                hasPlanetariumCamera) == "ready";
        }

        internal static string ClassifyMapFocusRestore(
            uint ghostPid,
            bool hasGhostVessel,
            bool hasMapObject,
            bool hasPlanetariumCamera)
        {
            if (ghostPid == 0)
                return "no-ghost-pid";
            if (!hasGhostVessel)
                return "ghost-vessel-missing";
            if (!hasMapObject)
                return "map-object-missing";
            if (!hasPlanetariumCamera)
                return "planetarium-camera-missing";
            return "ready";
        }

        internal static string BuildMapFocusRestoreDecisionMessage(
            int recordingIndex,
            uint ghostPid,
            bool hasGhostVessel,
            bool hasMapObject,
            bool hasOrbitRenderer,
            bool hasPlanetariumCamera,
            string reason)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Map focus restore decision: rec=#{0} ghostPid={1} hasGhostVessel={2} " +
                "mapObj={3} orbitRenderer={4} planetariumCamera={5} reason={6}",
                recordingIndex,
                ghostPid,
                hasGhostVessel,
                hasMapObject,
                hasOrbitRenderer,
                hasPlanetariumCamera,
                string.IsNullOrEmpty(reason) ? "(none)" : reason);
        }

        internal static string ClassifyWatchCameraInfrastructure(
            bool hasFlightCamera,
            bool hasTransform,
            bool hasParent)
        {
            if (!hasFlightCamera)
                return "flight-camera-missing";
            if (!hasTransform)
                return "camera-transform-missing";
            if (!hasParent)
                return "camera-parent-missing";
            return "ready";
        }

        internal static string BuildWatchCameraInfrastructureMessage(
            int recordingIndex,
            string recordingId,
            string reason,
            string vesselName = null,
            long cycleIndex = -1,
            string scene = null,
            bool hasState = false,
            bool hasGhost = false,
            bool hasCameraPivot = false)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Watch camera infrastructure unavailable: rec=#{0} id={1} vessel=\"{2}\" " +
                "cycle={3} scene={4} targetState[state={5} ghost={6} pivot={7}] reason={8}",
                recordingIndex,
                string.IsNullOrEmpty(recordingId) ? "(none)" : recordingId,
                vesselName ?? "?",
                cycleIndex,
                string.IsNullOrEmpty(scene) ? "n/a" : scene,
                hasState,
                hasGhost,
                hasCameraPivot,
                string.IsNullOrEmpty(reason) ? "(none)" : reason);
        }

        private static string GetLoadedSceneNameSafe()
        {
            try
            {
                return HighLogic.LoadedScene.ToString();
            }
            catch
            {
                return "n/a";
            }
        }

        /// <summary>
        /// Processes the watch-end hold timer: tries auto-follow to continuation each frame,
        /// destroys ghost and exits watch mode when expired.
        /// Returns true if watch camera update should return early (hold active or resolved).
        /// </summary>
        private bool ProcessWatchEndHoldTimer()
        {
            if (watchEndHoldUntilRealTime <= 0 && double.IsNaN(watchEndHoldPendingActivationUT))
                return false;

            int idx = watchedRecordingIndex;
            var committed = RecordingStore.CommittedRecordings;
            var ghostStates = host.Engine.ghostStates;

            // Try auto-follow each frame during the hold -- continuation ghost
            // may have spawned since the initial check in HandlePlaybackCompleted
            if (idx >= 0 && idx < committed.Count)
            {
                int nextTarget = FindNextWatchTarget(idx, committed[idx]);
                if (nextTarget >= 0)
                {
                    if (TransferWatchToNextSegment(nextTarget))
                    {
                        ParsekLog.Info("CameraFollow",
                            $"Watch hold auto-follow: #{idx} \u2192 #{nextTarget} (during hold period)");
                        watchEndHoldUntilRealTime = -1;
                        GhostPlaybackState held;
                        if (ghostStates.TryGetValue(idx, out held) && held != null)
                        {
                            var traj = committed[idx] as IPlaybackTrajectory;
                            host.Engine.DestroyGhost(idx, traj, default(TrajectoryPlaybackFlags),
                                reason: "auto-followed during hold");
                        }
                    }
                    else
                    {
                        ParsekLog.VerboseRateLimited("CameraFollow",
                            $"watch-hold-transfer-deferred-{idx}-{nextTarget}",
                            $"Watch hold transfer deferred: #{idx} \u2192 #{nextTarget} " +
                            "target ghost not active yet");
                    }
                    return true;
                }
            }

            if (!double.IsNaN(watchEndHoldPendingActivationUT))
            {
                double currentUT = CurrentUTNow();
                if (!double.IsNaN(currentUT) && currentUT + 0.001 < watchEndHoldPendingActivationUT)
                {
                    float recomputedHoldSeconds = GhostPlaybackLogic.ComputePendingWatchHoldSeconds(
                        0f,
                        currentUT,
                        watchEndHoldPendingActivationUT,
                        CurrentWarpRateNow());
                    float recomputedHoldUntil = RealtimeNow() + recomputedHoldSeconds;
                    if (watchEndHoldMaxRealTime > 0f)
                        recomputedHoldUntil = Mathf.Min(recomputedHoldUntil, watchEndHoldMaxRealTime);
                    if (watchEndHoldUntilRealTime < recomputedHoldUntil)
                        watchEndHoldUntilRealTime = recomputedHoldUntil;
                    return true;
                }

                watchEndHoldPendingActivationUT = double.NaN;
                float postActivationGraceUntil = RealtimeNow() + WatchMode.PendingPostActivationGraceSeconds;
                if (watchEndHoldMaxRealTime > 0f)
                    postActivationGraceUntil = Mathf.Min(postActivationGraceUntil, watchEndHoldMaxRealTime);
                if (watchEndHoldUntilRealTime < postActivationGraceUntil)
                    watchEndHoldUntilRealTime = postActivationGraceUntil;
            }

            // Hold expired -- no continuation found, destroy and exit
            if (RealtimeNow() >= watchEndHoldUntilRealTime)
            {
                ParsekLog.Info("CameraFollow",
                    $"Watch hold expired for #{idx} at t={watchEndHoldUntilRealTime:F1} \u2014 destroying ghost and exiting watch");
                watchEndHoldUntilRealTime = -1;
                GhostPlaybackState held;
                if (ghostStates.TryGetValue(idx, out held) && held != null)
                {
                    if (idx >= 0 && idx < committed.Count)
                    {
                        var traj = committed[idx] as IPlaybackTrajectory;
                        host.Engine.DestroyGhost(idx, traj, default(TrajectoryPlaybackFlags), reason: "watch hold expired");
                    }
                }
                ExitWatchModePreservingLineage();
                return true;
            }

            // Still in hold period -- don't process further (ghost is frozen at final pos)
            return true;
        }

        /// <summary>
        /// Resolves the ghost state being followed in watch mode. Searches overlap ghosts
        /// when watching a specific cycle, falls back to primary if the tracked cycle is lost.
        /// </summary>
        private GhostPlaybackState FindWatchedGhostState()
        {
            var ghostStates = host.Engine.ghostStates;
            var overlapGhosts = host.Engine.overlapGhosts;
            GhostPlaybackState state = null;
            if (watchedOverlapCycleIndex >= 0)
            {
                // Look for the watched cycle in the overlap list
                List<GhostPlaybackState> overlaps;
                if (overlapGhosts.TryGetValue(watchedRecordingIndex, out overlaps))
                {
                    for (int i = 0; i < overlaps.Count; i++)
                    {
                        if (overlaps[i] != null && overlaps[i].loopCycleIndex == watchedOverlapCycleIndex)
                        {
                            state = overlaps[i];
                            break;
                        }
                    }
                }
                // Also check if the primary IS the watched cycle
                if (state == null)
                {
                    GhostPlaybackState primary;
                    if (ghostStates.TryGetValue(watchedRecordingIndex, out primary)
                        && primary != null && primary.loopCycleIndex == watchedOverlapCycleIndex)
                    {
                        if ((primary.ghost == null || primary.cameraPivot == null)
                            && !TryEnsurePrimaryWatchGhostLoaded(primary, out primary))
                            primary = null;
                        state = primary;
                    }
                }

                // Fallback: tracked cycle not found -- switch to primary if available
                if (state == null)
                {
                    GhostPlaybackState primary;
                    if (ghostStates.TryGetValue(watchedRecordingIndex, out primary)
                        && primary != null)
                    {
                        if ((primary.ghost == null || primary.cameraPivot == null)
                            && !TryEnsurePrimaryWatchGhostLoaded(primary, out primary))
                            primary = null;
                    }
                    if (primary != null && primary.ghost != null && primary.cameraPivot != null)
                    {
                        state = primary;
                        watchedOverlapCycleIndex = primary.loopCycleIndex;
                        if (FlightCamera.fetch != null)
                            TryRetargetWatchCameraPreservingState(primary);
                        ParsekLog.Info("CameraFollow",
                            $"Watched cycle lost \u2014 falling back to primary cycle={primary.loopCycleIndex}");
                        LogWatchFocusStateChanged(primary, force: true, context: "cycle-fallback");
                    }
                }
            }
            else
            {
                ghostStates.TryGetValue(watchedRecordingIndex, out state);
                if (state != null && (state.ghost == null || state.cameraPivot == null))
                    TryEnsurePrimaryWatchGhostLoaded(state, out state);
            }
            return state;
        }

        private bool TryResolveOverlapRetarget()
        {
            if (watchedOverlapCycleIndex != -2 || overlapRetargetAfterUT <= 0)
                return false;

            var ghostStates = host.Engine.ghostStates;

            if (Planetarium.GetUniversalTime() >= overlapRetargetAfterUT)
            {
                // Destroy temp camera anchor
                DestroyOverlapCameraAnchor();
                overlapRetargetAfterUT = -1;

                // Immediately target the current primary ghost so FlightCamera
                // doesn't reference the destroyed anchor
                GhostPlaybackState primary;
                bool primaryReady = ghostStates.TryGetValue(watchedRecordingIndex, out primary)
                    && primary != null;
                if (primaryReady && (primary.ghost == null || primary.cameraPivot == null))
                    primaryReady = TryEnsurePrimaryWatchGhostLoaded(primary, out primary);

                if (FlightCamera.fetch != null
                    && primaryReady
                    && primary != null && primary.ghost != null)
                {
                    TryRetargetWatchCameraPreservingState(primary);
                    watchedOverlapCycleIndex = primary.loopCycleIndex;
                    watchNoTargetFrames = 0;
                    ParsekLog.Info("CameraFollow",
                        $"Overlap: hold ended, now following cycle={primary.loopCycleIndex}");
                    LogWatchFocusStateChanged(primary, force: true, context: "hold-ended");
                }
                else
                {
                    // No primary available yet -- set -1, let safety net below
                    // exit watch mode if no ghost appears within a few frames
                    watchedOverlapCycleIndex = -1;
                    ParsekLog.Info("CameraFollow",
                        $"Overlap: hold ended, no primary ghost available \u2014 waiting for next spawn");
                }
                return false;
            }
            else
            {
                // During hold, keep camera where it is (don't update position)
                if (FlightCamera.fetch != null)
                    FlightCamera.fetch.pivotTranslateSharpness = 0f;
                watchNoTargetFrames = 0;
                return true;
            }
        }

        private bool HasPendingOverlapBridgeRetarget()
        {
            return watchedOverlapCycleIndex == -1 && IsUnityObjectAvailable(overlapCameraAnchor);
        }

        internal static OverlapBridgeRetargetState ResolveOverlapBridgeRetargetState(
            bool hasPendingBridge,
            bool primaryReady,
            int bridgeWaitFrames,
            int maxBridgeWaitFrames)
        {
            if (!hasPendingBridge)
                return OverlapBridgeRetargetState.None;

            if (primaryReady)
                return OverlapBridgeRetargetState.RetargetToPrimary;

            if (bridgeWaitFrames >= maxBridgeWaitFrames)
                return OverlapBridgeRetargetState.ExitWatch;

            return OverlapBridgeRetargetState.KeepBridge;
        }

        internal static int AdvanceOverlapBridgeWaitFrames(
            int currentWaitFrames,
            int currentFrame,
            int lastRetryFrame)
        {
            return currentFrame == lastRetryFrame
                ? currentWaitFrames
                : currentWaitFrames + 1;
        }

        private bool TryResolveOverlapBridgeRetarget()
        {
            bool hasPendingBridge = HasPendingOverlapBridgeRetarget();
            FlightCamera flightCamera = GetFlightCameraSafe();
            bool hasFlightCamera = IsUnityObjectAvailable(flightCamera);
            int currentFrame = GetFrameCountSafe();
            var ghostStates = host.Engine.ghostStates;
            GhostPlaybackState primary;
            bool primaryReady = ghostStates.TryGetValue(watchedRecordingIndex, out primary)
                && primary != null;
            if (primaryReady && (!IsUnityObjectAvailable(primary.ghost) || !IsUnityObjectAvailable(primary.cameraPivot)))
                primaryReady = TryEnsurePrimaryWatchGhostLoaded(primary, out primary);

            bool canRetargetToPrimary = primaryReady && primary != null
                && IsUnityObjectAvailable(primary.ghost)
                && IsUnityObjectAvailable(primary.cameraPivot);
            int bridgeWaitFrames = hasPendingBridge
                ? AdvanceOverlapBridgeWaitFrames(watchNoTargetFrames, currentFrame, overlapBridgeLastRetryFrame)
                : 0;
            OverlapBridgeRetargetState bridgeState = ResolveOverlapBridgeRetargetState(
                hasPendingBridge,
                canRetargetToPrimary,
                bridgeWaitFrames,
                WatchMode.MaxPendingOverlapBridgeFrames);
            if (bridgeState == OverlapBridgeRetargetState.None)
                return false;

            overlapBridgeLastRetryFrame = currentFrame;

            if (bridgeState == OverlapBridgeRetargetState.ExitWatch)
            {
                watchNoTargetFrames = bridgeWaitFrames;
                ParsekLog.Info("CameraFollow",
                    $"Overlap: bridge expired for #{watchedRecordingIndex} after {bridgeWaitFrames} frames without a primary ghost");
                DestroyOverlapCameraAnchor();
                ExitWatchModePreservingLineage();
                return true;
            }

            if (bridgeState == OverlapBridgeRetargetState.RetargetToPrimary)
            {
                if (hasFlightCamera)
                {
                    try
                    {
                        TryRetargetWatchCameraPreservingState(primary);
                    }
                    catch (System.Security.SecurityException)
                    {
                    }
                    catch (MethodAccessException)
                    {
                    }
                }

                watchedOverlapCycleIndex = primary.loopCycleIndex;
                watchNoTargetFrames = 0;
                overlapBridgeLastRetryFrame = -1;
                DestroyOverlapCameraAnchor();
                ParsekLog.Info("CameraFollow",
                    $"Overlap: camera retargeted after quiet expiry for #{watchedRecordingIndex} cycle={primary.loopCycleIndex}");
                if (hasFlightCamera)
                    LogWatchFocusStateChanged(primary, force: true, context: "quiet-expiry-bridge");
                return false;
            }

            if (hasFlightCamera)
            {
                try
                {
                    flightCamera.pivotTranslateSharpness = 0f;
                    if (IsUnityObjectAvailable(overlapCameraAnchor)
                        && flightCamera.Target != overlapCameraAnchor.transform)
                    {
                        flightCamera.SetTargetTransform(overlapCameraAnchor.transform);
                    }
                }
                catch (System.Security.SecurityException)
                {
                }
                catch (MethodAccessException)
                {
                }
            }

            watchNoTargetFrames = bridgeWaitFrames;
            return true;
        }

        private void LogWatchTargetMismatch(GhostPlaybackState state)
        {
            if (FlightCamera.fetch == null || state == null) return;

            Transform expectedTarget = GetWatchTarget(state.cameraPivot) ?? state.ghost?.transform;
            Transform actualTarget = FlightCamera.fetch.Target;
            if (expectedTarget == null || actualTarget == expectedTarget)
            {
                lastLoggedWatchTargetMismatch = null;
                return;
            }

            string actualName = actualTarget != null ? actualTarget.name : "null";
            string mismatchKey = $"{watchedRecordingId}:{expectedTarget.name}:{actualName}:{watchedOverlapCycleIndex}:{currentCameraMode}";
            if (mismatchKey == lastLoggedWatchTargetMismatch)
                return;

            lastLoggedWatchTargetMismatch = mismatchKey;
            ParsekLog.Warn("CameraFollow",
                $"Watch target mismatch: rec=#{watchedRecordingIndex} id={watchedRecordingId ?? "null"} " +
                $"expected='{expectedTarget.name}' actual='{actualName}' cycle={watchedOverlapCycleIndex} " +
                $"mode={currentCameraMode} pivotPos=({state.cameraPivot.position.x:F1},{state.cameraPivot.position.y:F1},{state.cameraPivot.position.z:F1})");
        }

        private void LogHorizonForwardState(GhostPlaybackState state, Vector3 up, Vector3 forward,
            Vector3 horizonVelocity, Vector3 headingVelocity, Vector3 bodyFrameVelocity,
            Vector3 appliedFrameVelocity, HorizonForwardSource source)
        {
            if (state == null || currentCameraMode != WatchCameraMode.HorizonLocked)
                return;

            Vector3 rawHorizonVelocity = Vector3.ProjectOnPlane(
                state.lastInterpolatedVelocity, up);
            string velocityFrame = appliedFrameVelocity.sqrMagnitude > 0.0001f
                ? "surfaceRelative"
                : "playback";

            string rawAlignment = DescribeHorizonAlignment(forward, rawHorizonVelocity);
            string key = string.Format(CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}|{4}|{5}|{6}",
                watchedRecordingId ?? "null",
                watchedOverlapCycleIndex,
                state.lastInterpolatedBodyName ?? "null",
                currentCameraMode,
                velocityFrame,
                source,
                rawAlignment);
            string message = string.Format(CultureInfo.InvariantCulture,
                "Watch horizon basis: rec=#{0} id={1} cycle={2} mode={3} body={4} alt={5:F0}m " +
                "velocityFrame={6} source={7} rawAlignment={8} playbackVel={9} bodyVel={10} " +
                "headingVel={11} horizonVel={12} forward={13}",
                watchedRecordingIndex,
                watchedRecordingId ?? "null",
                watchedOverlapCycleIndex,
                currentCameraMode,
                state.lastInterpolatedBodyName ?? "null",
                state.lastInterpolatedAltitude,
                velocityFrame,
                source,
                rawAlignment,
                FormatVector3ForLogs(state.lastInterpolatedVelocity),
                FormatVector3ForLogs(bodyFrameVelocity),
                FormatVector3ForLogs(headingVelocity),
                FormatVector3ForLogs(horizonVelocity),
                FormatVector3ForLogs(forward));

            if (key != lastLoggedHorizonVectorKey)
            {
                lastLoggedHorizonVectorKey = key;
                ParsekLog.Info("CameraFollow", message);
            }

            ParsekLog.VerboseRateLimited(
                "CameraFollow",
                $"watch-horizon:{watchedRecordingId ?? "null"}:{watchedOverlapCycleIndex}:{currentCameraMode}",
                message,
                minIntervalSeconds: 10.0);
        }

        private static string DescribeHorizonAlignment(Vector3 forward, Vector3 velocity)
        {
            if (velocity.sqrMagnitude < 0.01f)
                return "fallback";
            return Vector3.Dot(forward, velocity) >= 0f ? "prograde" : "retrograde";
        }

        private void LogWatchFocusStateChanged(GhostPlaybackState state, bool force = false, string context = null)
        {
            if (watchedRecordingIndex < 0) return;

            string key = BuildWatchFocusKey(state);
            if (!force && key == lastLoggedWatchFocusKey)
                return;

            lastLoggedWatchFocusKey = key;
            string prefix = string.IsNullOrEmpty(context) ? "Watch focus" : $"Watch focus ({context})";
            ParsekLog.Info("CameraFollow", $"{prefix}: {BuildWatchFocusSummary(state)}");
        }

        private double ResolveWatchPlaybackUT(
            Recording rec, GhostPlaybackState currentState, double fallbackUT)
        {
            if (rec == null || !host.ShouldLoopPlaybackForWatch(rec))
                return fallbackUT;

            if (!host.TryGetLoopScheduleForWatch(
                    rec,
                    watchedRecordingIndex,
                    out double playbackStartUT,
                    out double scheduleStartUT,
                    out double resolveDuration,
                    out double intervalSeconds))
            {
                return fallbackUT;
            }

            if (GhostPlaybackLogic.IsOverlapLoop(intervalSeconds, resolveDuration))
            {
                if (currentState == null || currentState.loopCycleIndex < 0)
                    return fallbackUT;

                if (resolveDuration <= LoopTiming.MinCycleDuration)
                    return fallbackUT;

                // #443: engine's UpdateOverlapPlayback assigns loopCycleIndex using the
                // cadence-adjusted cadence (ComputeEffectiveLaunchCadence). Watch mode MUST
                // reconstruct cycleStartUT with the same effective cadence or the computed
                // phase drifts by (effective - user) * loopCycleIndex. Dispatch (IsOverlapLoop
                // above) still uses the user period — that matches the engine's dispatch at
                // GhostPlaybackEngine.cs:900.
                double effectiveCadence = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                    intervalSeconds, resolveDuration,
                    GhostPlayback.MaxOverlapGhostsPerRecording);

                return GhostPlaybackLogic.ComputeOverlapCyclePlaybackUT(
                    Planetarium.GetUniversalTime(),
                    scheduleStartUT,
                    playbackStartUT,
                    resolveDuration,
                    effectiveCadence, currentState.loopCycleIndex);
            }

            if (host.TryComputeLoopPlaybackUTForWatch(rec, Planetarium.GetUniversalTime(),
                out double loopUT, out _, out _, watchedRecordingIndex))
            {
                return loopUT;
            }

            return fallbackUT;
        }

        private bool TryEnsurePrimaryWatchGhostLoaded(
            GhostPlaybackState currentState, out GhostPlaybackState state)
        {
            state = null;
            int index = watchedRecordingIndex;
            if (index < 0)
                return false;

            var committed = RecordingStore.CommittedRecordings;
            if (index >= committed.Count)
                return false;

            var traj = committed[index] as IPlaybackTrajectory;
            if (traj == null)
                return false;

            double playbackUT = ResolveWatchPlaybackUT(committed[index], currentState,
                Planetarium.GetUniversalTime());

            if (!host.Engine.EnsureGhostVisualsLoadedForWatch(index, traj, playbackUT))
                return false;

            return host.Engine.TryGetGhostState(index, out state)
                && state != null
                && state.ghost != null
                && state.cameraPivot != null;
        }

        /// <summary>
        /// Handles camera state reset when overlap ghosts are destroyed for a recording.
        /// Called from ParsekFlight.DestroyAllOverlapGhosts after the engine destroys them.
        /// </summary>
        internal void OnOverlapGhostsDestroyed(int recIdx)
        {
            if (watchedRecordingIndex == recIdx && watchedOverlapCycleIndex != -1)
            {
                ParsekLog.Info("CameraFollow",
                    $"Overlap ghosts destroyed for watched recording #{recIdx} \u2014 resetting overlap cycle tracking from {watchedOverlapCycleIndex}");
                watchedOverlapCycleIndex = -1;
                overlapRetargetAfterUT = -1;
            }
        }

        /// <summary>
        /// Handles watch mode state update when a recording is deleted.
        /// Called from ParsekFlight.DeleteRecording.
        /// </summary>
        internal void OnRecordingDeleted(int index)
        {
            if (watchedRecordingIndex >= 0)
            {
                var result = ComputeWatchIndexAfterDelete(
                    watchedRecordingIndex, watchedRecordingId, index,
                    RecordingStore.CommittedRecordings);
                if (result.newIndex < 0)
                {
                    ParsekLog.Warn("CameraFollow",
                        $"Watched recording \"{watchedRecordingId}\" deleted \u2014 auto-exiting watch mode");
                    ExitWatchMode();
                }
                else
                {
                    int oldIdx = watchedRecordingIndex;
                    watchedRecordingIndex = result.newIndex;
                    watchedRecordingId = result.newId;
                    if (oldIdx != result.newIndex)
                        ParsekLog.Info("CameraFollow",
                            $"Recording deleted at #{index} \u2014 watchedRecordingIndex adjusted from {oldIdx} to {result.newIndex}");
                }
            }

            if (lineageProtectionRecordingIndex >= 0)
            {
                var protectionResult = ComputeWatchIndexAfterDelete(
                    lineageProtectionRecordingIndex, lineageProtectionRecordingId, index,
                    RecordingStore.CommittedRecordings);
                if (protectionResult.newIndex < 0)
                {
                    ClearLineageProtection(
                        $"Protected watch-lineage root \"{lineageProtectionRecordingId}\" deleted \u2014 clearing retained debris protection");
                }
                else
                {
                    lineageProtectionRecordingIndex = protectionResult.newIndex;
                    lineageProtectionRecordingId = protectionResult.newId;
                }
            }
        }

        /// <summary>
        /// Handles vessel switch re-targeting: when KSP switches the active vessel
        /// while in watch mode, re-point the camera at the watched ghost.
        /// </summary>
        internal void OnVesselSwitchComplete()
        {
            if (watchedRecordingIndex < 0) return;

            var ghostStates = host.Engine.ghostStates;
            GhostPlaybackState ws;
            if (ghostStates.TryGetValue(watchedRecordingIndex, out ws) && ws != null && ws.ghost != null)
            {
                TryRetargetWatchCameraPreservingState(ws);
                var boundTarget = FlightCamera.fetch?.Target ?? GetWatchTarget(ws.cameraPivot) ?? ws.ghost.transform;
                ParsekLog.Info("CameraFollow",
                    $"onVesselChange re-target: ghost #{watchedRecordingIndex}" +
                    $" target='{boundTarget?.name ?? "null"}'" +
                    $" localPos=({(boundTarget != null ? boundTarget.localPosition.x.ToString("F2", CultureInfo.InvariantCulture) : "?")}," +
                    $"{(boundTarget != null ? boundTarget.localPosition.y.ToString("F2", CultureInfo.InvariantCulture) : "?")}," +
                    $"{(boundTarget != null ? boundTarget.localPosition.z.ToString("F2", CultureInfo.InvariantCulture) : "?")})" +
                    $" worldPos=({(boundTarget != null ? boundTarget.position.x.ToString("F1", CultureInfo.InvariantCulture) : "?")}," +
                    $"{(boundTarget != null ? boundTarget.position.y.ToString("F1", CultureInfo.InvariantCulture) : "?")}," +
                    $"{(boundTarget != null ? boundTarget.position.z.ToString("F1", CultureInfo.InvariantCulture) : "?")})" +
                    $" camDist={FlightCamera.fetch.Distance:F1}");
                LogWatchFocusStateChanged(ws, force: true, context: "vessel-switch");
            }
        }

        /// <summary>
        /// Handles vessel destruction while in watch mode.
        /// </summary>
        internal void OnVesselWillDestroy(Vessel v)
        {
            if (watchedRecordingIndex < 0) return;

            // Null out saved camera vessel if it's being destroyed
            if (savedCameraVessel != null && v == savedCameraVessel)
                savedCameraVessel = null;

            // If the active vessel is destroyed while watching, exit watch mode.
            // Skip camera restore because ActiveVessel is the dying vessel -- KSP will
            // assign a new active vessel and handle the camera itself.
            if (v == FlightGlobals.ActiveVessel)
            {
                ParsekLog.Warn("CameraFollow",
                    $"Active vessel destroyed while watching ghost #{watchedRecordingIndex} \u2014 skipping camera restore (dying vessel), KSP will reassign");
                ExitWatchMode(skipCameraRestore: true);
            }
        }

        /// <summary>
        /// Sets the watch hold timer. Called by policy when playback ends for a watched recording.
        /// </summary>
        internal void StartWatchHold(
            float holdUntilRealTime,
            double pendingActivationUT = double.NaN,
            float holdMaxRealTime = -1f)
        {
            watchEndHoldUntilRealTime = holdUntilRealTime;
            watchEndHoldMaxRealTime = holdMaxRealTime > 0f ? holdMaxRealTime : holdUntilRealTime;
            watchEndHoldPendingActivationUT = pendingActivationUT;
            ParsekLog.Info("CameraFollow",
                $"Watch hold timer set: holdUntilRealTime={holdUntilRealTime:F1}" +
                (double.IsNaN(pendingActivationUT)
                    ? string.Empty
                    : $" pendingActivationUT={pendingActivationUT:F1}") +
                $" (watched #{watchedRecordingIndex})");
        }
    }
}
