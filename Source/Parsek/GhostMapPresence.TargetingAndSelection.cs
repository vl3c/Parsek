using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Ghost map navigation target, target verification, tracking-station focus / selection and
    // the vessel-list refresh.
    internal static partial class GhostMapPresence
    {
        internal struct TrackingStationSpawnHandoffState
        {
            internal readonly uint GhostPid;
            internal readonly bool WasNavigationTarget;
            internal readonly bool WasMapFocus;

            internal TrackingStationSpawnHandoffState(
                uint ghostPid,
                bool wasNavigationTarget,
                bool wasMapFocus)
            {
                GhostPid = ghostPid;
                WasNavigationTarget = wasNavigationTarget;
                WasMapFocus = wasMapFocus;
            }
        }

        internal enum GhostTargetVerificationStatus
        {
            Accepted,
            VerificationUnavailable,
            MissingFlightGlobals,
            NullTarget,
            CurrentMainBody,
            ParentBody,
            WrongVessel,
            WrongObject
        }

        internal static bool ShouldTransferTrackingStationNavigationTarget(
            uint ghostPid,
            uint currentTargetPid)
        {
            return ghostPid != 0
                && currentTargetPid != 0
                && ghostPid == currentTargetPid;
        }

        internal static bool ShouldTransferTrackingStationMapFocus(
            bool mapViewEnabled,
            bool hasGhostMapObject,
            bool mapCameraAlreadyFocusedGhost)
        {
            return mapViewEnabled
                && hasGhostMapObject
                && mapCameraAlreadyFocusedGhost;
        }

        internal static bool IsTrackingStationMapFocusSceneActive(
            bool mapViewEnabled,
            bool isTrackingStationScene)
        {
            return mapViewEnabled || isTrackingStationScene;
        }

        /// <summary>
        /// Set a ghost as KSP's navigation target and log success only after
        /// stock target validation has had frames to reject invalid targetables.
        /// <paramref name="recordingIndex"/> is diagnostic-only; callers may pass
        /// -1 when a selection path cannot resolve the recording index.
        /// </summary>
        internal static void SetGhostMapNavigationTarget(
            Vessel vessel,
            int recordingIndex,
            string source)
        {
            string safeSource = string.IsNullOrEmpty(source) ? "unknown" : source;
            string vesselName = vessel != null ? (vessel.vesselName ?? "Ghost") : "Ghost";
            if (vessel == null)
            {
                LogGhostTargetVerificationOutcome(
                    vesselName,
                    recordingIndex,
                    safeSource,
                    GhostTargetVerificationStatus.WrongObject,
                    "ghost-vessel-null",
                    "target-request-vessel=null",
                    "(not-captured)",
                    "(not-captured)",
                    "preflight");
                return;
            }

            if (FlightGlobals.fetch == null)
            {
                LogGhostTargetVerificationOutcome(
                    vesselName,
                    recordingIndex,
                    safeSource,
                    GhostTargetVerificationStatus.MissingFlightGlobals,
                    "FlightGlobals.fetch=null",
                    CaptureGhostTargetState(vessel, "missing-flightglobals"),
                    "(not-captured)",
                    "(not-captured)",
                    "preflight");
                return;
            }

            NormalizeGhostOrbitDriverTargetIdentity(vessel, safeSource);
            long requestId;
            unchecked
            {
                requestId = ++ghostTargetRequestSequence;
            }

            string before = CaptureGhostTargetState(vessel, "before");
            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "Ghost target request via {0}: requestId={1} recIndex={2} ghost='{3}' before=[{4}]",
                    safeSource,
                    requestId,
                    recordingIndex,
                    vesselName,
                    before));

            FlightGlobals.fetch.SetVesselTarget(vessel, overrideInputLock: true);

            string immediate = CaptureGhostTargetState(vessel, "after-set");
            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "Ghost target request via {0}: requestId={1} recIndex={2} ghost='{3}' afterSet=[{4}]",
                    safeSource,
                    requestId,
                    recordingIndex,
                    vesselName,
                    immediate));

            ParsekScenario host = ParsekScenario.Instance;
            if (host != null)
            {
                host.StartCoroutine(VerifyGhostMapNavigationTargetAfterKspValidation(
                    vessel,
                    recordingIndex,
                    safeSource,
                    vesselName,
                    before,
                    immediate,
                    requestId));
                return;
            }

            LogGhostTargetVerificationOutcome(
                vesselName,
                recordingIndex,
                safeSource,
                GhostTargetVerificationStatus.VerificationUnavailable,
                "ParsekScenario.Instance=null; post-validation target state cannot be observed",
                CaptureGhostTargetState(vessel, "unverified-no-coroutine"),
                before,
                immediate,
                "unverified-no-coroutine");
        }

        private static IEnumerator VerifyGhostMapNavigationTargetAfterKspValidation(
            Vessel vessel,
            int recordingIndex,
            string source,
            string vesselName,
            string before,
            string immediate,
            long requestId)
        {
            yield return null;
            yield return null;

            if (requestId != ghostTargetRequestSequence)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "Ghost target verification superseded via {0}: requestId={1} latestRequestId={2} recIndex={3} ghost='{4}'",
                        source ?? "unknown",
                        requestId,
                        ghostTargetRequestSequence,
                        recordingIndex,
                        vesselName ?? "Ghost"));
                yield break;
            }

            GhostTargetVerificationStatus status = EvaluateGhostTargetVerification(vessel, out string reason);
            string final = CaptureGhostTargetState(vessel, "after-ksp-validation");
            LogGhostTargetVerificationOutcome(
                vesselName,
                recordingIndex,
                source,
                status,
                reason,
                final,
                before,
                immediate,
                "after-ksp-validation");
        }

        internal static bool LogGhostTargetVerificationForTesting(
            string vesselName,
            int recordingIndex,
            string source,
            GhostTargetVerificationStatus status,
            string reason,
            string finalState)
        {
            return LogGhostTargetVerificationOutcome(
                vesselName,
                recordingIndex,
                source,
                status,
                reason,
                finalState,
                "(test-before)",
                "(test-immediate)",
                "test");
        }

        private static bool LogGhostTargetVerificationOutcome(
            string vesselName,
            int recordingIndex,
            string source,
            GhostTargetVerificationStatus status,
            string reason,
            string finalState,
            string beforeState,
            string immediateState,
            string phase)
        {
            string safeName = string.IsNullOrEmpty(vesselName) ? "Ghost" : vesselName;
            string safeSource = string.IsNullOrEmpty(source) ? "unknown" : source;
            string safeReason = string.IsNullOrEmpty(reason) ? "(none)" : reason;
            string safeFinal = string.IsNullOrEmpty(finalState) ? "(none)" : finalState;

            if (status == GhostTargetVerificationStatus.Accepted)
            {
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Ghost '{0}' set as target via {1} (verified {2}; recIndex={3}; final=[{4}])",
                        safeName,
                        safeSource,
                        phase ?? "(unknown)",
                        recordingIndex,
                        safeFinal));
                return true;
            }

            if (status == GhostTargetVerificationStatus.VerificationUnavailable)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Ghost '{0}' target verification unavailable via {1}: status={2} reason={3} recIndex={4} phase={5} before=[{6}] immediate=[{7}] final=[{8}]",
                        safeName,
                        safeSource,
                        status,
                        safeReason,
                        recordingIndex,
                        phase ?? "(unknown)",
                        string.IsNullOrEmpty(beforeState) ? "(none)" : beforeState,
                        string.IsNullOrEmpty(immediateState) ? "(none)" : immediateState,
                        safeFinal));
                return false;
            }

            ParsekLog.Warn(Tag,
                string.Format(ic,
                    "Ghost '{0}' target rejected via {1}: status={2} reason={3} recIndex={4} phase={5} before=[{6}] immediate=[{7}] final=[{8}]",
                    safeName,
                    safeSource,
                    status,
                    safeReason,
                    recordingIndex,
                    phase ?? "(unknown)",
                    string.IsNullOrEmpty(beforeState) ? "(none)" : beforeState,
                    string.IsNullOrEmpty(immediateState) ? "(none)" : immediateState,
                    safeFinal));
            return false;
        }

        private static GhostTargetVerificationStatus EvaluateGhostTargetVerification(
            Vessel ghost,
            out string reason)
        {
            reason = null;
            if (FlightGlobals.fetch == null)
            {
                reason = "FlightGlobals.fetch=null";
                return GhostTargetVerificationStatus.MissingFlightGlobals;
            }

            ITargetable target = FlightGlobals.fetch.VesselTarget;
            if (target == null)
            {
                reason = "FlightGlobals.fetch.VesselTarget=null";
                return GhostTargetVerificationStatus.NullTarget;
            }

            Vessel targetVessel = SafeGetTargetVessel(target);
            if (targetVessel != null)
            {
                if (IsSameVessel(targetVessel, ghost))
                {
                    reason = "target-vessel-matches-ghost";
                    return GhostTargetVerificationStatus.Accepted;
                }

                reason = string.Format(ic,
                    "target-vessel-mismatch ghostPid={0} targetPid={1} targetName=\"{2}\"",
                    ghost != null ? ghost.persistentId : 0u,
                    targetVessel.persistentId,
                    targetVessel.vesselName ?? "(null)");
                return GhostTargetVerificationStatus.WrongVessel;
            }

            OrbitDriver targetDriver = SafeGetTargetOrbitDriver(target);
            CelestialBody targetBody = targetDriver != null ? targetDriver.celestialBody : null;
            CelestialBody currentBody = ResolveCurrentMainBody();
            if (targetBody != null)
            {
                if (IsSameBody(targetBody, currentBody))
                {
                    reason = string.Format(ic,
                        "target-driver-celestialBody-is-current-main-body body={0}",
                        targetBody.name ?? "(null)");
                    return GhostTargetVerificationStatus.CurrentMainBody;
                }

                if (currentBody != null && currentBody.HasParent(targetBody))
                {
                    reason = string.Format(ic,
                        "target-driver-celestialBody-is-parent-body targetBody={0} currentBody={1}",
                        targetBody.name ?? "(null)",
                        currentBody.name ?? "(null)");
                    return GhostTargetVerificationStatus.ParentBody;
                }
            }

            reason = "target-is-not-the-ghost-vessel: " + DescribeTargetable(target);
            return GhostTargetVerificationStatus.WrongObject;
        }

        private static string CaptureGhostTargetState(Vessel ghost, string phase)
        {
            FlightGlobals globals = FlightGlobals.fetch;
            Vessel active = globals != null ? FlightGlobals.ActiveVessel : null;
            CelestialBody currentBody = ResolveCurrentMainBody();
            string mode = globals != null
                ? globals.vesselTargetMode.ToString()
                : "(no-flightglobals)";

            return string.Format(ic,
                "phase={0} mode={1} currentMainBody={2} active={3} target={4} activeTargetObject={5} ghost={6} ghostRegistered={7}",
                phase ?? "(null)",
                mode,
                currentBody != null ? (currentBody.name ?? "(unnamed-body)") : "(null)",
                DescribeVessel(active),
                globals != null ? DescribeTargetable(globals.VesselTarget) : "(no-flightglobals)",
                active != null ? DescribeTargetable(active.targetObject) : "(no-active-vessel)",
                DescribeVessel(ghost) + " " + BuildGhostOrbitDriverIdentity(ghost),
                IsVesselRegistered(ghost));
        }

        internal static void NormalizeGhostOrbitDriverTargetIdentity(
            Vessel vessel,
            string context)
        {
            if (vessel == null || vessel.orbitDriver == null)
                return;

            OrbitDriver driver = vessel.orbitDriver;
            bool changed = false;
            string before = BuildGhostOrbitDriverIdentity(vessel);

            if (!IsSameVessel(driver.vessel, vessel))
            {
                driver.vessel = vessel;
                changed = true;
            }

            if (driver.celestialBody != null)
            {
                driver.celestialBody = null;
                changed = true;
            }

            if (changed)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "Normalized ghost target identity for {0}: before=[{1}] after=[{2}]",
                        string.IsNullOrEmpty(context) ? "(none)" : context,
                        before,
                        BuildGhostOrbitDriverIdentity(vessel)));
            }
        }

        internal static string BuildGhostOrbitDriverIdentity(Vessel vessel)
        {
            if (vessel == null)
                return "driver=(ghost-null)";
            OrbitDriver driver = vessel.orbitDriver;
            if (driver == null)
                return "driver=(null)";

            string driverVessel = driver.vessel != null
                ? string.Format(ic,
                    "{0}/pid={1}",
                    driver.vessel.vesselName ?? "(null)",
                    driver.vessel.persistentId)
                : "(null)";
            string driverBody = driver.celestialBody != null
                ? (driver.celestialBody.name ?? "(unnamed-body)")
                : "(null)";
            string referenceBody = driver.referenceBody != null
                ? (driver.referenceBody.name ?? "(unnamed-body)")
                : "(null)";

            return string.Format(ic,
                "driverVessel={0} driverCelestialBody={1} referenceBody={2} orbit={3} mapObj={4} orbitRenderer={5}",
                driverVessel,
                driverBody,
                referenceBody,
                driver.orbit != null,
                vessel.mapObject != null,
                vessel.orbitRenderer != null);
        }

        private static string DescribeTargetable(ITargetable target)
        {
            if (target == null)
                return "null";

            string typeName = target.GetType().Name;
            Vessel vessel = SafeGetTargetVessel(target);
            OrbitDriver driver = SafeGetTargetOrbitDriver(target);
            string name = SafeGetTargetName(target);
            string vesselText = vessel != null
                ? DescribeVessel(vessel)
                : "(null)";
            string driverVessel = driver != null && driver.vessel != null
                ? DescribeVessel(driver.vessel)
                : "(null)";
            string driverBody = driver != null && driver.celestialBody != null
                ? (driver.celestialBody.name ?? "(unnamed-body)")
                : "(null)";
            string referenceBody = driver != null && driver.referenceBody != null
                ? (driver.referenceBody.name ?? "(unnamed-body)")
                : "(null)";

            return string.Format(ic,
                "type={0} name=\"{1}\" vessel={2} driverVessel={3} driverCelestialBody={4} referenceBody={5} transform={6}",
                typeName,
                name ?? "(null)",
                vesselText,
                driverVessel,
                driverBody,
                referenceBody,
                SafeHasTransform(target));
        }

        private static string DescribeVessel(Vessel vessel)
        {
            if (vessel == null)
                return "null";
            return string.Format(ic,
                "\"{0}\"/pid={1}/ghost={2}",
                vessel.vesselName ?? "(null)",
                vessel.persistentId,
                IsGhostMapVessel(vessel.persistentId));
        }

        private static Vessel SafeGetTargetVessel(ITargetable target)
        {
            if (target == null) return null;
            try { return target.GetVessel(); }
            catch { return null; }
        }

        private static OrbitDriver SafeGetTargetOrbitDriver(ITargetable target)
        {
            if (target == null) return null;
            try { return target.GetOrbitDriver(); }
            catch { return null; }
        }

        private static string SafeGetTargetName(ITargetable target)
        {
            if (target == null) return null;
            try { return target.GetName(); }
            catch { return null; }
        }

        private static bool SafeHasTransform(ITargetable target)
        {
            if (target == null) return false;
            try { return target.GetTransform() != null; }
            catch { return false; }
        }

        private static CelestialBody ResolveCurrentMainBody()
        {
            if (FlightGlobals.currentMainBody != null)
                return FlightGlobals.currentMainBody;
            if (FlightGlobals.fetch == null)
                return null;
            Vessel active = FlightGlobals.ActiveVessel;
            if (active != null && active.mainBody != null)
                return active.mainBody;
            return null;
        }

        internal static bool IsVesselRegistered(Vessel vessel)
        {
            if (vessel == null || FlightGlobals.fetch == null)
                return false;

            // O(1) persistent-id lookup against FlightGlobals.PersistentVesselIds
            // (stock FlightGlobals.FindVessel is a thin wrapper around it).
            if (vessel.persistentId != 0u)
                return FlightGlobals.FindVessel(vessel.persistentId, out _);

            // pid==0 vessels (unregistered / mid-construction) fall back to an
            // identity scan so callers still get a meaningful answer.
            if (FlightGlobals.Vessels == null)
                return false;
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                if (ReferenceEquals(FlightGlobals.Vessels[i], vessel))
                    return true;
            }
            return false;
        }

        private static bool IsSameVessel(Vessel a, Vessel b)
        {
            if (a == null || b == null)
                return false;
            return ReferenceEquals(a, b)
                || (a.persistentId != 0u && a.persistentId == b.persistentId);
        }

        private static bool IsSameBody(CelestialBody a, CelestialBody b)
        {
            if (a == null || b == null)
                return false;
            return ReferenceEquals(a, b)
                || string.Equals(a.name, b.name, StringComparison.Ordinal);
        }

        private static TrackingStationSpawnHandoffState CaptureTrackingStationSpawnHandoffState(
            int recordingIndex)
        {
            if (!vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel ghostVessel)
                || ghostVessel == null)
            {
                return default(TrackingStationSpawnHandoffState);
            }

            Vessel currentTargetVessel = FlightGlobals.fetch != null
                && FlightGlobals.fetch.VesselTarget != null
                    ? FlightGlobals.fetch.VesselTarget.GetVessel()
                    : null;

            bool wasNavigationTarget = ShouldTransferTrackingStationNavigationTarget(
                ghostVessel.persistentId,
                currentTargetVessel != null ? currentTargetVessel.persistentId : 0u);
            bool wasMapFocus = ShouldTransferTrackingStationMapFocus(
                IsTrackingStationMapFocusSceneActive(
                    MapView.MapIsEnabled,
                    HighLogic.LoadedScene == GameScenes.TRACKSTATION),
                ghostVessel.mapObject != null,
                PlanetariumCamera.fetch != null
                    && ReferenceEquals(PlanetariumCamera.fetch.target, ghostVessel.mapObject));

            return new TrackingStationSpawnHandoffState(
                ghostVessel.persistentId,
                wasNavigationTarget,
                wasMapFocus);
        }

        private static void RestoreTrackingStationSpawnHandoffState(
            uint spawnedPid,
            TrackingStationSpawnHandoffState handoffState,
            string reason,
            bool reselectSpawnedVessel)
        {
            if (spawnedPid == 0
                || (!reselectSpawnedVessel
                    && !handoffState.WasNavigationTarget
                    && !handoffState.WasMapFocus))
            {
                return;
            }

            Vessel spawned = FlightRecorder.FindVesselByPid(spawnedPid);
            if (spawned == null)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Tracking-station handoff could not restore focus/target for ghostPid={0} spawnedPid={1} reason={2} (spawned vessel not found)",
                        handoffState.GhostPid,
                        spawnedPid,
                        reason));
                return;
            }

            if (reselectSpawnedVessel)
                RestoreTrackingStationSelectedVessel(spawned, reason);

            if (handoffState.WasNavigationTarget && FlightGlobals.fetch != null)
            {
                FlightGlobals.fetch.SetVesselTarget(spawned);
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Tracking-station handoff restored nav target from ghostPid={0} to spawnedPid={1} reason={2}",
                        handoffState.GhostPid,
                        spawnedPid,
                        reason));
            }

            if (!handoffState.WasMapFocus)
                return;

            if (IsTrackingStationMapFocusSceneActive(
                    MapView.MapIsEnabled,
                    HighLogic.LoadedScene == GameScenes.TRACKSTATION)
                && PlanetariumCamera.fetch != null
                && spawned.mapObject != null)
            {
                if (TrySetReadyMapObjectTarget(
                        () => spawned.mapObject.GetName(),
                        () => PlanetariumCamera.fetch.SetTarget(spawned.mapObject),
                        out string mapObjectName,
                        out string mapObjectError))
                {
                    ParsekLog.Info(Tag,
                        string.Format(ic,
                            "Tracking-station handoff restored map focus from ghostPid={0} to spawnedPid={1} mapObject='{2}' reason={3}",
                            handoffState.GhostPid,
                            spawnedPid,
                            mapObjectName ?? "(null)",
                            reason));
                    return;
                }

                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Tracking-station handoff could not restore map focus for ghostPid={0} spawnedPid={1} reason={2}: {3}",
                        handoffState.GhostPid,
                        spawnedPid,
                        reason,
                        mapObjectError ?? "map object probe failed"));
                return;
            }

            ParsekLog.Warn(Tag,
                string.Format(ic,
                    "Tracking-station handoff could not restore map focus for ghostPid={0} spawnedPid={1} reason={2} mapView={3} camera={4} mapObject={5}",
                    handoffState.GhostPid,
                    spawnedPid,
                    reason,
                    MapView.MapIsEnabled,
                    PlanetariumCamera.fetch != null,
                    spawned.mapObject != null));
        }

        internal static bool TrySetReadyMapObjectTarget(
            Func<string> getName,
            Action setTarget,
            out string name,
            out string error)
        {
            if (!TryProbeMapObjectName(getName, out name, out error))
                return false;

            if (setTarget == null)
            {
                error = "setTarget-null";
                return false;
            }

            try
            {
                setTarget();
                return true;
            }
            catch (Exception ex)
            {
                error = string.Format(ic,
                    "SetTarget threw {0}: {1}",
                    ex.GetType().Name,
                    ex.Message);
                return false;
            }
        }

        internal static bool TryProbeMapObjectName(
            Func<string> getName,
            out string name,
            out string error)
        {
            name = null;
            error = null;

            if (getName == null)
            {
                error = "getName-null";
                return false;
            }

            try
            {
                name = getName();
                return true;
            }
            catch (Exception ex)
            {
                error = string.Format(ic,
                    "GetName threw {0}: {1}",
                    ex.GetType().Name,
                    ex.Message);
                return false;
            }
        }

        internal static bool TrySelectTrackingStationVessel(
            object trackingInstance,
            object vesselSelection,
            out string error)
        {
            error = null;

            if (trackingInstance == null || vesselSelection == null)
                return false;

            try
            {
                MethodInfo setVesselMethod = FindTrackingStationSetVesselMethod(
                    trackingInstance.GetType(),
                    vesselSelection.GetType());
                if (setVesselMethod != null)
                {
                    object[] args = BuildTrackingStationSetVesselArguments(
                        setVesselMethod,
                        vesselSelection,
                        keepFocus: false);
                    setVesselMethod.Invoke(trackingInstance, args);
                    return true;
                }

                FieldInfo selectedField = trackingInstance.GetType().GetField(
                    "selectedVessel",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (selectedField == null)
                {
                    error = "selectedVessel field and SetVessel method not found";
                    return false;
                }

                selectedField.SetValue(trackingInstance, vesselSelection);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        internal static object[] BuildTrackingStationSetVesselArguments(
            MethodInfo setVesselMethod,
            object vesselSelection,
            bool keepFocus)
        {
            if (setVesselMethod == null)
                return null;

            ParameterInfo[] parameters = setVesselMethod.GetParameters();
            if (parameters.Length == 2
                && parameters[1].ParameterType == typeof(bool))
            {
                return new[] { vesselSelection, (object)keepFocus };
            }

            return new[] { vesselSelection };
        }

        internal static bool TryRefreshLiveTrackingStationVesselList(string reason)
        {
            if (!IsTrackingStationSceneForVesselListRefresh())
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "Tracking Station vessel list refresh skipped: reason={0} scene={1}",
                        string.IsNullOrEmpty(reason) ? "(none)" : reason,
                        GetCurrentSceneName()));
                return false;
            }

            SpaceTracking tracking = UnityEngine.Object.FindObjectOfType<SpaceTracking>();
            return TryInvokeTrackingStationVesselListRefresh(
                tracking,
                reason,
                out _);
        }

        private static bool IsTrackingStationSceneForVesselListRefresh()
        {
            try
            {
                return HighLogic.LoadedScene == GameScenes.TRACKSTATION;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryInvokeTrackingStationVesselListRefresh(
            object trackingInstance,
            string reason,
            out string error)
        {
            error = null;
            string safeReason = string.IsNullOrEmpty(reason) ? "(none)" : reason;

            if (trackingInstance == null)
            {
                error = "SpaceTracking instance not found";
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Tracking Station vessel list refresh failed: reason={0} error={1}",
                        safeReason,
                        error));
                return false;
            }

            try
            {
                MethodInfo buildMethod = FindTrackingStationNoArgMethod(
                    trackingInstance.GetType(),
                    "buildVesselsList");
                if (buildMethod == null)
                {
                    error = "buildVesselsList method not found";
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "Tracking Station vessel list refresh failed: reason={0} error={1}",
                            safeReason,
                            error));
                    return false;
                }

                buildMethod.Invoke(trackingInstance, null);
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Tracking Station vessel list refreshed: reason={0}",
                        safeReason));
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                error = string.Format(ic,
                    "buildVesselsList threw {0}: {1}",
                    inner.GetType().Name,
                    inner.Message);
            }
            catch (Exception ex)
            {
                error = string.Format(ic,
                    "buildVesselsList reflection failed {0}: {1}",
                    ex.GetType().Name,
                    ex.Message);
            }

            ParsekLog.Warn(Tag,
                string.Format(ic,
                    "Tracking Station vessel list refresh failed: reason={0} error={1}",
                    safeReason,
                    error ?? "(none)"));
            return false;
        }

        private static MethodInfo FindTrackingStationNoArgMethod(
            Type trackingType,
            string methodName)
        {
            if (trackingType == null || string.IsNullOrEmpty(methodName))
                return null;

            MethodInfo[] methods = trackingType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.Name != methodName)
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 0)
                    return method;
            }

            return null;
        }

        internal static bool IsTrackingStationRecordingAlreadyMaterialized(Recording rec)
        {
            if (rec == null)
                return false;

            bool realVesselExists = false;
            if (rec.VesselPersistentId != 0)
            {
                try
                {
                    realVesselExists = GhostPlaybackLogic.RealVesselExistsForRecording(rec);
                }
                catch (Exception)
                {
                    realVesselExists = false;
                }
            }

            return ShouldSkipTrackingStationDuplicateSpawn(rec, realVesselExists);
        }

        internal static MethodInfo FindTrackingStationSetVesselMethod(
            Type trackingType,
            Type selectionType)
        {
            if (trackingType == null || selectionType == null)
                return null;

            MethodInfo[] methods = trackingType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo oneArgumentFallback = null;
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.Name != "SetVessel")
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (!IsTrackingStationSetVesselSignature(parameters, selectionType))
                    continue;

                if (parameters.Length == 2)
                    return method;
                if (oneArgumentFallback == null)
                    oneArgumentFallback = method;
            }

            return oneArgumentFallback;
        }

        private static bool IsTrackingStationSetVesselSignature(
            ParameterInfo[] parameters,
            Type selectionType)
        {
            if (parameters == null || selectionType == null)
                return false;
            if (parameters.Length != 1 && parameters.Length != 2)
                return false;
            if (!parameters[0].ParameterType.IsAssignableFrom(selectionType))
                return false;

            return parameters.Length == 1
                || parameters[1].ParameterType == typeof(bool);
        }

        private static void RestoreTrackingStationSelectedVessel(
            Vessel spawned,
            string reason)
        {
            if (spawned == null)
                return;

            SpaceTracking tracking = UnityEngine.Object.FindObjectOfType<SpaceTracking>();
            if (tracking == null)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Tracking-station handoff could not restore selected vessel for spawnedPid={0} reason={1} (SpaceTracking instance not found)",
                        spawned.persistentId,
                        reason));
                return;
            }

            if (TrySelectTrackingStationVessel(tracking, spawned, out string error))
            {
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Tracking-station handoff restored selected vessel to spawnedPid={0} reason={1}",
                        spawned.persistentId,
                        reason));
                return;
            }

            if (!string.IsNullOrEmpty(error))
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Tracking-station handoff could not restore selected vessel for spawnedPid={0} reason={1}: {2}",
                        spawned.persistentId,
                        reason,
                        error));
            }
        }
    }
}
