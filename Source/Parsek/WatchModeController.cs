using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal enum WatchCameraMode { Free, HorizonLocked }

    /// <summary>
    /// Owns camera-follow (watch mode) state and methods.
    /// Extracted from ParsekFlight to isolate watch-mode lifecycle.
    /// Watch mode locks the flight camera onto a ghost vessel, tracking it
    /// through loop/overlap cycle transitions, and restoring the camera
    /// to the active vessel on exit.
    /// </summary>
    internal class WatchModeController
    {
        private readonly ParsekFlight host;

        internal const string WatchModeLockId = "ParsekWatch";
        internal const ControlTypes WatchModeLockMask =
            ControlTypes.STAGING | ControlTypes.THROTTLE |
            ControlTypes.VESSEL_SWITCHING | ControlTypes.EVA_INPUT |
            ControlTypes.CAMERAMODES;

        // Camera follow state — transient, never serialized
        private int watchedRecordingIndex = -1;       // -1 = not watching
        private string watchedRecordingId;             // stable across index shifts
        private float watchStartTime;                  // Time.time when watch mode was entered
        private long watchedOverlapCycleIndex = -1;    // which overlap cycle the camera is following (-1 = ready for next, -2 = holding after explosion)
        private double overlapRetargetAfterUT = -1;    // delay re-target after watched cycle explodes
        private GameObject overlapCameraAnchor;        // temp anchor so FlightCamera doesn't reference destroyed ghost
        private Vessel savedCameraVessel;
        private float savedCameraDistance;
        private float savedCameraPitch;
        private float savedCameraHeading;
        private float watchEndHoldUntilRealTime = -1;  // non-looped end hold timer (real time, warp-independent)
        private float savedPivotSharpness = 0.5f;
        private int watchNoTargetFrames;               // consecutive frames with no valid camera target (safety net)

        // Horizon-locked camera mode state
        private WatchCameraMode currentCameraMode = WatchCameraMode.Free;
        private bool userModeOverride;  // true when user pressed toggle; cleared on EnterWatchMode

        // Lazy-initialized GUI styles for the watch mode overlay
        private GUIStyle watchOverlayStyle;
        private GUIStyle watchOverlayHintStyle;

        internal WatchModeController(ParsekFlight host)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
        }

        // === Properties for external callers ===

        internal bool IsWatchingGhost => watchedRecordingIndex >= 0;
        internal int WatchedRecordingIndex => watchedRecordingIndex;
        internal WatchCameraMode CurrentCameraMode => currentCameraMode;

        // === Camera event handlers for engine loop/overlap cycle transitions ===

        internal void HandleLoopCameraAction(CameraActionEvent evt)
        {
            if (watchedRecordingIndex != evt.Index) return; // not watching this recording

            switch (evt.Action)
            {
                case CameraActionType.ExitWatch:
                    ExitWatchMode();
                    break;

                case CameraActionType.ExplosionHoldStart:
                    if (overlapCameraAnchor != null) UnityEngine.Object.Destroy(overlapCameraAnchor);
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
                    if (overlapCameraAnchor != null) UnityEngine.Object.Destroy(overlapCameraAnchor);
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
                        FlightCamera.fetch.SetTargetTransform(GetWatchTarget(evt.GhostPivot));
                        watchedOverlapCycleIndex = evt.NewCycleIndex;
                        // Clean up the bridge anchor now that camera is on the new ghost
                        if (overlapCameraAnchor != null) { UnityEngine.Object.Destroy(overlapCameraAnchor); overlapCameraAnchor = null; }
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
                        FlightCamera.fetch.SetTargetTransform(GetWatchTarget(evt.GhostPivot));
                        watchedOverlapCycleIndex = evt.NewCycleIndex;
                        if (overlapCameraAnchor != null) { UnityEngine.Object.Destroy(overlapCameraAnchor); overlapCameraAnchor = null; }
                        ParsekLog.Info("CameraFollow",
                            $"Overlap: camera retargeted to ghost #{evt.Index} cycle={evt.NewCycleIndex}");
                    }
                    break;

                case CameraActionType.ExplosionHoldStart:
                    overlapRetargetAfterUT = evt.HoldUntilUT;
                    watchedOverlapCycleIndex = -2;
                    if (overlapCameraAnchor != null) UnityEngine.Object.Destroy(overlapCameraAnchor);
                    overlapCameraAnchor = new UnityEngine.GameObject("ParsekOverlapCameraAnchor");
                    overlapCameraAnchor.transform.position = evt.AnchorPosition;
                    if (FlightCamera.fetch != null)
                        FlightCamera.fetch.SetTargetTransform(overlapCameraAnchor.transform);
                    ParsekLog.Info("CameraFollow",
                        $"Overlap: camera holding at explosion for #{evt.Index} cycle={evt.NewCycleIndex}");
                    break;
            }
        }

        /// <summary>
        /// Returns true if recording at index has an active ghost (exists and not null).
        /// </summary>
        internal bool HasActiveGhost(int index)
        {
            var ghostStates = host.Engine.ghostStates;
            GhostPlaybackState s;
            return ghostStates.TryGetValue(index, out s) && s != null && s.ghost != null;
        }

        /// <summary>
        /// Returns true if the ghost at index is within the camera cutoff distance,
        /// or if it's an orbital recording (exempt from cutoff -- naturally travels far).
        /// </summary>
        internal bool IsGhostWithinVisualRange(int index)
        {
            var ghostStates = host.Engine.ghostStates;
            GhostPlaybackState s;
            if (!ghostStates.TryGetValue(index, out s) || s == null) return false;
            // Orbital recordings are always "in range" for watch purposes
            var committed = RecordingStore.CommittedRecordings;
            if (index >= 0 && index < committed.Count && committed[index].HasOrbitSegments)
                return true;
            float cutoffKm = ParsekSettings.Current?.ghostCameraCutoffKm ?? 300f;
            return GhostPlaybackLogic.IsWithinWatchRange(s.lastDistance, cutoffKm);
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

            // Ghost must exist
            var ghostStates = host.Engine.ghostStates;
            GhostPlaybackState gs;
            if (!ghostStates.TryGetValue(index, out gs) || gs == null || gs.ghost == null)
                return;

            // Ghost must be on the same body as the active vessel
            string ghostBody = gs.lastInterpolatedBodyName;
            string activeBody = FlightGlobals.ActiveVessel?.mainBody?.name;
            if (string.IsNullOrEmpty(ghostBody) || string.IsNullOrEmpty(activeBody) || ghostBody != activeBody)
                return;

            // Distance guard: KSP rendering breaks when camera is far from the active vessel
            // (FloatingOrigin, terrain, atmosphere, skybox are all anchored to active vessel).
            // Refuse watch if ghost is beyond the user's camera cutoff distance setting.
            // Orbital ghosts are exempt -- they naturally travel far during ascent/orbit.
            var rec = committed[index];
            float maxWatchKm = ParsekSettings.Current?.ghostCameraCutoffKm ?? 300f;
            if (FlightGlobals.ActiveVessel != null && gs.ghost != null && !rec.HasOrbitSegments)
            {
                float distKm = (float)(Vector3d.Distance(
                    gs.ghost.transform.position, FlightGlobals.ActiveVessel.transform.position) / 1000.0);
                if (distKm > maxWatchKm)
                {
                    ParsekLog.Info("CameraFollow",
                        $"EnterWatchMode refused: ghost #{index} \"{committed[index].VesselName}\" " +
                        $"is {distKm.ToString("F0", CultureInfo.InvariantCulture)}km from active vessel (max {maxWatchKm.ToString("F0", CultureInfo.InvariantCulture)}km)");
                    ParsekLog.ScreenMessage($"Ghost too far to watch ({distKm:F0}km, max {maxWatchKm:F0}km)", 3f);
                    return;
                }
            }

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

            // If already watching a different recording, exit first (switch case -- preserve camera state)
            bool switching = watchedRecordingIndex >= 0 && watchedRecordingIndex != index;
            if (switching)
            {
                ParsekLog.Info("CameraFollow", $"Switching watch from #{watchedRecordingIndex} to #{index} \"{committed[index].VesselName}\"");
                ExitWatchMode(skipCameraRestore: true);
            }

            watchedRecordingIndex = index;
            watchedRecordingId = committed[index].RecordingId;
            watchStartTime = Time.time;

            // Reset camera mode state for new watch session
            userModeOverride = false;
            currentCameraMode = WatchCameraMode.Free; // auto-detect will set this on first frame

            // If the ghost is currently beyond visual range and the recording loops,
            // reset the loop phase so the ghost starts from the beginning of the recording
            // (at the pad) instead of wherever it is mid-flight (e.g. near the Mun).
            if (gs.currentZone == RenderingZone.Beyond && host.ShouldLoopPlaybackForWatch(rec))
                ResetLoopPhaseForWatch(index, gs, rec);

            // Save camera state only when entering fresh (not switching between ghosts)
            if (!switching)
            {
                savedCameraVessel = FlightGlobals.ActiveVessel;
                savedCameraDistance = FlightCamera.fetch.Distance;
                savedCameraPitch = FlightCamera.fetch.camPitch;
                savedCameraHeading = FlightCamera.fetch.camHdg;
                savedPivotSharpness = FlightCamera.fetch.pivotTranslateSharpness;
            }

            // Disable KSP's internal pivot tracking -- we drive the camera manually
            FlightCamera.fetch.pivotTranslateSharpness = 0f;

            // Point camera at ghost (use cameraPivot or horizonProxy based on mode)
            var watchTarget = GetWatchTarget(gs.cameraPivot) ?? gs.ghost.transform;
            FlightCamera.fetch.SetTargetTransform(watchTarget);
            FlightCamera.fetch.SetDistance(50f);  // override [75,400] entry clamp
            watchedOverlapCycleIndex = gs.loopCycleIndex; // track which cycle we're following
            ParsekLog.Info("CameraFollow",
                $"EnterWatchMode: ghost #{index} \"{committed[index].VesselName}\"" +
                $" target='{watchTarget.name}' pivotLocal=({watchTarget.localPosition.x:F2},{watchTarget.localPosition.y:F2},{watchTarget.localPosition.z:F2})" +
                $" ghostPos=({gs.ghost.transform.position.x:F1},{gs.ghost.transform.position.y:F1},{gs.ghost.transform.position.z:F1})" +
                $" camDist={FlightCamera.fetch.Distance.ToString("F1", CultureInfo.InvariantCulture)}");

            // Block inputs that could affect the active vessel
            InputLockManager.SetControlLock(WatchModeLockMask, WatchModeLockId);
            ParsekLog.Verbose("CameraFollow", $"InputLockManager control lock \"{WatchModeLockId}\" set: {WatchModeLockMask}");

            // Clear hold timer and safety counter
            watchEndHoldUntilRealTime = -1;
            watchNoTargetFrames = 0;

            string body = gs.lastInterpolatedBodyName ?? "?";
            string altStr = gs.lastInterpolatedAltitude.ToString("F0", CultureInfo.InvariantCulture);
            ParsekLog.Info("CameraFollow",
                $"Entering watch mode for recording #{index} \"{committed[index].VesselName}\" \u2014 ghost at alt {altStr}m on {body}");
        }

        /// <summary>
        /// Resets the loop phase offset so a beyond-range looping ghost restarts from
        /// the beginning of the recording when the player enters watch mode.
        /// </summary>
        private void ResetLoopPhaseForWatch(int index, GhostPlaybackState gs, Recording rec)
        {
            double currentUT = Planetarium.GetUniversalTime();
            double duration = rec.EndUT - rec.StartUT;
            double intervalSeconds = host.GetLoopIntervalSecondsForWatch(rec);
            double cycleDuration = duration + intervalSeconds;
            if (cycleDuration <= GhostPlaybackLogic.MinLoopDurationSeconds)
                cycleDuration = duration;

            var loopPhaseOffsets = host.Engine.loopPhaseOffsets;

            // Current elapsed time (with any existing offset)
            double elapsed = currentUT - rec.StartUT;
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
            gs.ghost.SetActive(true);
            gs.currentZone = RenderingZone.Physics;
            gs.playbackIndex = 0;

            // Position ghost at first trajectory point so camera targets the pad
            if (rec.Points != null && rec.Points.Count > 0)
                host.PositionGhostAtForWatch(gs.ghost, rec.Points[0]);

            ParsekLog.Info("CameraFollow",
                string.Format(CultureInfo.InvariantCulture,
                    "Watch mode loop reset: ghost #{0} \"{1}\" cycleTime={2:F1}s -> offset={3:F1}s (ghost repositioned to recording start)",
                    index, rec.VesselName, cycleTime, newOffset));
        }

        /// <summary>
        /// Exit watch mode: return camera to the active vessel.
        /// When skipCameraRestore is true (switching between ghosts), the camera is not restored.
        /// </summary>
        internal void ExitWatchMode(bool skipCameraRestore = false)
        {
            if (watchedRecordingIndex < 0) return;
            if (FlightCamera.fetch != null)
                FlightCamera.fetch.pivotTranslateSharpness = savedPivotSharpness;

            // Restore camera to the active vessel (unless switching between ghosts)
            if (!skipCameraRestore)
            {
                var committed = RecordingStore.CommittedRecordings;
                string recVesselName = watchedRecordingIndex < committed.Count
                    ? committed[watchedRecordingIndex].VesselName : "?";
                string targetName = savedCameraVessel != null
                    ? savedCameraVessel.vesselName
                    : (FlightGlobals.ActiveVessel?.vesselName ?? "unknown");
                ParsekLog.Info("CameraFollow",
                    $"Exiting watch mode for recording #{watchedRecordingIndex} \"{recVesselName}\" \u2014 returning to {targetName}");

                if (FlightCamera.fetch != null)
                {
                    if (savedCameraVessel != null && savedCameraVessel.gameObject != null)
                    {
                        FlightCamera.fetch.SetTargetVessel(savedCameraVessel);
                        FlightCamera.fetch.SetDistance(savedCameraDistance);
                        FlightCamera.fetch.camPitch = savedCameraPitch;
                        FlightCamera.fetch.camHdg = savedCameraHeading;
                        ParsekLog.Verbose("CameraFollow",
                            $"FlightCamera.SetTargetVessel restored to {savedCameraVessel.vesselName}, distance={savedCameraDistance.ToString("F1", CultureInfo.InvariantCulture)}");
                    }
                    else if (FlightGlobals.ActiveVessel != null)
                    {
                        FlightCamera.fetch.SetTargetVessel(FlightGlobals.ActiveVessel);
                        ParsekLog.Verbose("CameraFollow",
                            $"FlightCamera.SetTargetVessel restored to {FlightGlobals.ActiveVessel.vesselName}, distance={savedCameraDistance.ToString("F1", CultureInfo.InvariantCulture)}");
                    }
                }
            }

            // Remove input locks
            InputLockManager.RemoveControlLock(WatchModeLockId);
            ParsekLog.Verbose("CameraFollow", $"InputLockManager control lock \"{WatchModeLockId}\" removed");

            watchedRecordingIndex = -1;
            watchedRecordingId = null;
            watchedOverlapCycleIndex = -1;
            overlapRetargetAfterUT = -1;
            if (overlapCameraAnchor != null) { UnityEngine.Object.Destroy(overlapCameraAnchor); overlapCameraAnchor = null; }
            savedCameraVessel = null;
            savedCameraDistance = 0f;
            savedCameraPitch = 0f;
            savedCameraHeading = 0f;
            watchEndHoldUntilRealTime = -1;
            watchNoTargetFrames = 0;
            currentCameraMode = WatchCameraMode.Free;
            userModeOverride = false;
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
            double threshold = hasAtmosphere ? atmosphereDepth : 50000.0;
            return altitude < threshold;
        }

        /// <summary>
        /// Computes the horizon-plane forward direction from velocity and up vector.
        /// Projects velocity onto the horizon plane (perpendicular to up). Falls back
        /// to lastForward when velocity is near zero, then to an arbitrary perpendicular.
        /// Pure vector math — testable outside Unity runtime.
        /// </summary>
        internal static Vector3 ComputeHorizonForward(Vector3 up, Vector3 velocity, Vector3 lastForward)
        {
            Vector3 forward = Vector3.ProjectOnPlane(velocity, up);
            if (forward.sqrMagnitude < 0.01f)
            {
                forward = Vector3.ProjectOnPlane(lastForward, up);
                if (forward.sqrMagnitude < 0.0001f)
                {
                    forward = Vector3.Cross(up, Vector3.right);
                    if (forward.sqrMagnitude < 0.0001f)
                        forward = Vector3.Cross(up, Vector3.forward);
                }
            }
            forward.Normalize();
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
            Vector3 worldDir = oldTargetRot * localDir;
            Vector3 newLocalDir = Quaternion.Inverse(newTargetRot) * worldDir;

            // Decompose back to pitch/hdg
            float newPitch = Mathf.Asin(Mathf.Clamp(newLocalDir.y, -1f, 1f)) * Mathf.Rad2Deg;
            float newHdg = Mathf.Atan2(newLocalDir.x, newLocalDir.z) * Mathf.Rad2Deg;

            return (newPitch, newHdg);
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

            // Compensate pitch/heading to prevent visual snap on mode switch
            Quaternion oldRot = oldTarget != null ? oldTarget.rotation : Quaternion.identity;
            Quaternion newRot = newTarget.rotation;
            var (newPitch, newHdg) = CompensateCameraAngles(
                oldRot, newRot, FlightCamera.fetch.camPitch, FlightCamera.fetch.camHdg);

            FlightCamera.fetch.SetTargetTransform(newTarget);
            FlightCamera.fetch.camPitch = newPitch;
            FlightCamera.fetch.camHdg = newHdg;
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

            userModeOverride = true;
            currentCameraMode = currentCameraMode == WatchCameraMode.Free
                ? WatchCameraMode.HorizonLocked
                : WatchCameraMode.Free;

            ApplyCameraTarget(state);

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

            // Auto-detect mode (unless user overrode)
            if (!userModeOverride && body != null)
            {
                bool shouldLock = ShouldAutoHorizonLock(
                    body.atmosphere, body.atmosphereDepth, state.lastInterpolatedAltitude);
                var autoMode = shouldLock ? WatchCameraMode.HorizonLocked : WatchCameraMode.Free;
                if (autoMode != currentCameraMode)
                {
                    currentCameraMode = autoMode;
                    ApplyCameraTarget(state);
                    ParsekLog.Info("CameraFollow",
                        string.Format(CultureInfo.InvariantCulture,
                            "Watch camera auto-switched to {0} (alt={1:F0}m, body={2})",
                            currentCameraMode, state.lastInterpolatedAltitude,
                            state.lastInterpolatedBodyName));
                }
            }

            // Always update horizonProxy rotation (keeps it ready for smooth switch)
            if (body != null && state.cameraPivot != null)
            {
                Vector3 ghostPos = state.cameraPivot.position;
                Vector3 up = (ghostPos - body.position).normalized;

                var (rotation, forward) = ComputeHorizonRotation(
                    up, state.lastInterpolatedVelocity, state.lastValidHorizonForward);
                state.horizonProxy.rotation = rotation;
                state.lastValidHorizonForward = forward;
            }
        }

        /// <summary>
        /// Pure static helper: computes the new watch index after a recording is deleted.
        /// Returns (newIndex, newId) where newIndex=-1 means watch mode should exit.
        /// </summary>
        internal static (int newIndex, string newId) ComputeWatchIndexAfterDelete(
            int watchedIndex, string watchedId, int deletedIndex,
            List<Recording> recordings)
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
        internal void TransferWatchToNextSegment(int nextIndex)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (nextIndex < 0 || nextIndex >= committed.Count) return;

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
                    $"Auto-follow target #{nextIndex} has no active ghost — staying on current");
                return;
            }

            // Preserve original camera state across the transition
            // (ExitWatchMode clears these, but we want Backspace to restore to the original vessel)
            Vessel preservedVessel = savedCameraVessel;
            float preservedDistance = savedCameraDistance;
            float preservedPitch = savedCameraPitch;
            float preservedHeading = savedCameraHeading;

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

            var segTarget = GetWatchTarget(gs.cameraPivot) ?? gs.ghost.transform;
            FlightCamera.fetch.SetTargetTransform(segTarget);
            InputLockManager.SetControlLock(WatchModeLockMask, WatchModeLockId);
            ParsekLog.Verbose("CameraFollow",
                $"InputLockManager control lock \"{WatchModeLockId}\" re-set after transfer");

            watchEndHoldUntilRealTime = -1;

            // Reset watch start time so the zone-exemption logging starts fresh
            // for the new segment (no stale elapsed time from the previous segment)
            watchStartTime = Time.time;

            ParsekLog.Info("CameraFollow",
                $"TransferWatch re-target: ghost #{nextIndex} \"{newName}\"" +
                $" target='{segTarget.name}' pivotLocal=({segTarget.localPosition.x:F2},{segTarget.localPosition.y:F2},{segTarget.localPosition.z:F2})" +
                $" ghostPos=({gs.ghost.transform.position.x:F1},{gs.ghost.transform.position.y:F1},{gs.ghost.transform.position.z:F1})" +
                $" camDist={FlightCamera.fetch.Distance:F1}" +
                $" watchStartTime={watchStartTime.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Checks whether the watched ghost is still active. Exits watch mode if the
        /// primary ghost is gone and no matching overlap cycle exists.
        /// </summary>
        internal void ValidateWatchedGhostStillActive()
        {
            if (watchedRecordingIndex < 0) return;
            if (host.Engine.HasActiveGhost(watchedRecordingIndex)) return;

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
                ExitWatchMode();
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
                if (overlapCameraAnchor != null) { UnityEngine.Object.Destroy(overlapCameraAnchor); overlapCameraAnchor = null; }
            }

            // Delayed re-target after watched overlap cycle exploded
            if (TryResolveOverlapRetarget())
                return;

            // Find the ghost we're actually following -- may be an overlap ghost, not the primary
            GhostPlaybackState state = FindWatchedGhostState();

            if (FlightCamera.fetch == null || FlightCamera.fetch.transform == null
                || FlightCamera.fetch.transform.parent == null)
                return;

            if (state == null || state.cameraPivot == null)
            {
                // No valid target -- count frames and exit if persistent
                watchNoTargetFrames++;
                if (watchNoTargetFrames >= 3)
                {
                    ParsekLog.Warn("CameraFollow",
                        $"No valid camera target for {watchNoTargetFrames} frames \u2014 exiting watch mode");
                    ExitWatchMode();
                }
                return;
            }

            // Valid target found -- reset safety counter
            watchNoTargetFrames = 0;

            // Keep sharpness zeroed -- KSP resets it on various events
            FlightCamera.fetch.pivotTranslateSharpness = 0f;

            // Drive camera orbit center to the cameraPivot's world position
            FlightCamera.fetch.transform.parent.position = state.cameraPivot.position;

            // Update horizon proxy rotation and auto-detect camera mode
            UpdateHorizonProxy(state);
        }

        /// <summary>
        /// Processes the watch-end hold timer: tries auto-follow to continuation each frame,
        /// destroys ghost and exits watch mode when expired.
        /// Returns true if watch camera update should return early (hold active or resolved).
        /// </summary>
        private bool ProcessWatchEndHoldTimer()
        {
            if (watchEndHoldUntilRealTime <= 0)
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
                    TransferWatchToNextSegment(nextTarget);
                    return true;
                }
            }

            // Hold expired -- no continuation found, destroy and exit
            if (Time.time >= watchEndHoldUntilRealTime)
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
                ExitWatchMode();
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
                        state = primary;
                }

                // Fallback: tracked cycle not found -- switch to primary if available
                if (state == null)
                {
                    GhostPlaybackState primary;
                    if (ghostStates.TryGetValue(watchedRecordingIndex, out primary)
                        && primary != null && primary.ghost != null
                        && primary.cameraPivot != null)
                    {
                        state = primary;
                        watchedOverlapCycleIndex = primary.loopCycleIndex;
                        if (FlightCamera.fetch != null)
                            FlightCamera.fetch.SetTargetTransform(GetWatchTarget(primary.cameraPivot));
                        ParsekLog.Info("CameraFollow",
                            $"Watched cycle lost \u2014 falling back to primary cycle={primary.loopCycleIndex}");
                    }
                }
            }
            else
            {
                ghostStates.TryGetValue(watchedRecordingIndex, out state);
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
                if (overlapCameraAnchor != null)
                {
                    UnityEngine.Object.Destroy(overlapCameraAnchor);
                    overlapCameraAnchor = null;
                }
                overlapRetargetAfterUT = -1;

                // Immediately target the current primary ghost so FlightCamera
                // doesn't reference the destroyed anchor
                GhostPlaybackState primary;
                if (FlightCamera.fetch != null
                    && ghostStates.TryGetValue(watchedRecordingIndex, out primary)
                    && primary != null && primary.ghost != null)
                {
                    var target = GetWatchTarget(primary.cameraPivot) ?? primary.ghost.transform;
                    FlightCamera.fetch.SetTargetTransform(target);
                    watchedOverlapCycleIndex = primary.loopCycleIndex;
                    watchNoTargetFrames = 0;
                    ParsekLog.Info("CameraFollow",
                        $"Overlap: hold ended, now following cycle={primary.loopCycleIndex}");
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
            if (watchedRecordingIndex < 0) return;

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
                var target = GetWatchTarget(ws.cameraPivot) ?? ws.ghost.transform;
                FlightCamera.fetch.SetTargetTransform(target);
                ParsekLog.Info("CameraFollow",
                    $"onVesselChange re-target: ghost #{watchedRecordingIndex}" +
                    $" target='{target.name}' localPos=({target.localPosition.x:F2},{target.localPosition.y:F2},{target.localPosition.z:F2})" +
                    $" worldPos=({target.position.x:F1},{target.position.y:F1},{target.position.z:F1})" +
                    $" camDist={FlightCamera.fetch.Distance:F1}");
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
        internal void StartWatchHold(float holdUntilRealTime)
        {
            watchEndHoldUntilRealTime = holdUntilRealTime;
            ParsekLog.Info("CameraFollow",
                $"Watch hold timer set: holdUntilRealTime={holdUntilRealTime:F1} (watched #{watchedRecordingIndex})");
        }
    }
}
