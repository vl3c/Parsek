using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The far-watch state machine (docs/dev/design-far-watch.md sections 4, 6, 7, 9.2):
    /// builds the camera drone beside the ship, hands focus to it, holds every vessel packed,
    /// moves the drone with the watched ghost (or keeps it beside the ship), hands focus back
    /// and tears down. One instance per flight scene, owned by <see cref="ParsekFlight"/>.
    /// Every end goes through <see cref="RequestEnd"/>; the pure decisions live in
    /// <see cref="FarWatchTransitions"/> and <see cref="CameraDrone"/>.
    /// In step 2 it is reachable only through the test-command seam (TestCommandFarWatch).
    /// </summary>
    // [ERS-exempt] The watched ghost is resolved by watch index and recording id in the raw
    // committed list, the index space WatchModeController and the playback engine key on.
    internal sealed class FarWatchController
    {
        private const string Tag = "FarWatch";
        private const string DroneTag = "CameraDrone";
        private const string CameraTag = "CameraFollow";
        internal const string InputLockId = "ParsekFarWatch";
        internal const ControlTypes InputLockMask =
            ControlTypes.QUICKSAVE | ControlTypes.QUICKLOAD | ControlTypes.STAGING
            | ControlTypes.THROTTLE | ControlTypes.EVA_INPUT | ControlTypes.VESSEL_SWITCHING;

        internal const int SpawnLoadMaxFrames = 30;
        internal const int DroneLoadedSettleFrames = 2;
        internal const int ShipLoadedSettleFrames = 3;
        internal const int ShipLoadMaxFrames = 300;
        internal const int RecorderBackgroundMaxUpdates = 3;
        internal const int HoldTicksAfterTerrain = 10;
        internal const int TerrainWaitMaxTicks = 1500;
        internal const int InvalidStatesToEnd = 3;
        internal const double TickSummaryIntervalSeconds = 5.0;

        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        private readonly ParsekFlight host;
        private FarWatchState state = FarWatchState.Idle;
        private FarWatchEndReason endReason = FarWatchEndReason.None;

        private Vessel drone;
        private Vessel ship;
        private string shipName;
        private int ghostIndex = -1;
        private string ghostRecordingId;
        private bool followGhost;
        private bool destroyingDrone;
        private bool switchedOut;
        // The ship was lost after the hand-over out (7.6, V2): stock's flight-ended screen shows
        // once the ship is gone from the vessel list (a save taken inside onVesselWillDestroy
        // would still find it).
        private bool shipLostScreenPending;
        internal const int ShipLostScreenDelayFrames = 2;
        private bool cameraLost;

        // Hold point (beside the ship, or the last written point).
        private bool surfaceHeld;
        private CelestialBody holdBody;
        private double holdLat, holdLon, holdAlt;

        private int framesInState;
        private int shipLoadedFrames;
        private bool handbackRetried;
        private int recorderWaitUpdates;

        // Follow history (body-relative position at the previous tick).
        private Vector3d prevR;
        private double prevUT = double.NaN;
        private CelestialBody prevBody;
        private Vector3d lastVDiff;
        private int invalidStreak;

        // Camera: offset captured in onVesselSwitching, and the ship's watch-entry values.
        private bool switchCameraCaptured;
        private Vector3 switchCameraOffset;
        private FlightCamera.Modes switchCameraMode;
        private bool shipCameraSaved;
        private float shipCameraDistance;
        private float shipCameraPitchDeg;
        private float shipCameraHeadingDeg;

        private bool autosaveStopped;
        private bool inputLockSet;

        // Teardown.
        private bool teardownWaitTerrain;
        private int terrainReadyTicks;
        private int teardownTicks;

        // Per-tick batch counters, one summary every TickSummaryIntervalSeconds.
        private int winTicks, winWrites, winSkips, winFloorClamps, winHoldRenewals;
        private double winMaxDev;

        internal FarWatchController(ParsekFlight host)
        {
            this.host = host;
        }

        internal FarWatchState State => state;
        internal bool IsRunning => FarWatchTransitions.IsRunning(state);
        internal FarWatchEndReason LastEndReason => endReason;
        internal Vessel Ship => ship;
        internal int GhostIndex => ghostIndex;
        internal bool IsFollowingGhost => followGhost;

        // ------------------------------------------------------------------ start

        /// <summary>
        /// Starts a far watch on recording <paramref name="recordingIndex"/> (the near watch is
        /// entered first when it is not already on that ghost). Returns false with a refusal
        /// reason when entry is refused; nothing is built then.
        /// </summary>
        internal bool TryStart(int recordingIndex, bool follow, out string refusal)
        {
            refusal = FarWatchTransitions.EvaluateEntryRefusal(host.BuildFarWatchEntryInputs(recordingIndex, IsRunning, follow));
            if (refusal != null)
            {
                ParsekLog.Info(Tag, string.Format(ic, "start refused reason={0} rec=#{1}", refusal, recordingIndex));
                return false;
            }

            Vessel active = FlightGlobals.ActiveVessel;
            // Follow needs the ghost camera; a hold watch keeps the camera on the drone beside
            // the ship, so a ghost beyond the near-watch range (or on another body) is fine.
            if (follow && (!host.IsWatchingGhost || host.WatchedRecordingIndex != recordingIndex))
                host.EnterWatchMode(recordingIndex);
            if (follow && (!host.IsWatchingGhost || host.WatchedRecordingIndex != recordingIndex))
            {
                refusal = "watch-entry-refused";
                ParsekLog.Info(Tag, string.Format(ic, "start refused reason={0} rec=#{1}", refusal, recordingIndex));
                return false;
            }

            ship = active;
            shipName = active.vesselName;
            ghostIndex = recordingIndex;
            var committed = RecordingStore.CommittedRecordings;
            ghostRecordingId = recordingIndex < committed.Count ? committed[recordingIndex].RecordingId : null;
            followGhost = follow;
            endReason = FarWatchEndReason.None;
            ResetPerWatchState();

            TimeWarp.SetRate(0, true);
            CameraDroneIdentity.SetShip(active.persistentId, active.id);
            Transition(FarWatchTrigger.StartAdmitted, FarWatchEndReason.None);

            CelestialBody body = active.mainBody;
            Vector3d droneWorld;
            bool held;
            if (!CameraDrone.TryComputeBesideShip(active.GetWorldPos3D(), body.position, active.obt_velocity,
                    active.LandedOrSplashed, CameraDrone.SpawnOffsetMeters, out droneWorld, out held))
            {
                FailSpawn(FarWatchEndReason.BuildFailed, "placement-degenerate");
                refusal = "placement-degenerate";
                return false;
            }
            Vector3d vel = active.obt_velocity;
            if (held)
            {
                SetHoldPoint(body, droneWorld);
                vel = body.getRFrmVel(droneWorld);
            }
            else
            {
                surfaceHeld = false;
                holdBody = body;
            }

            string why;
            Vessel built = CameraDrone.TryBuild(body, droneWorld, vel, held, out why);
            if (built == null)
            {
                FailSpawn(FarWatchEndReason.BuildFailed, why);
                refusal = "build-failed:" + why;
                return false;
            }
            drone = built;
            ParsekLog.Info(DroneTag, string.Format(ic,
                "drone placed beside ship pid={0} shipPid={1} distToShip={2} surfaceHeld={3} body={4}",
                built.persistentId, active.persistentId,
                (built.GetWorldPos3D() - active.GetWorldPos3D()).magnitude.ToString("F1", ic), held, body.bodyName));
            return true;
        }

        private void ResetPerWatchState()
        {
            shipLostScreenPending = false;
            drone = null;
            destroyingDrone = false;
            switchedOut = false;
            cameraLost = false;
            framesInState = 0;
            shipLoadedFrames = 0;
            handbackRetried = false;
            recorderWaitUpdates = 0;
            prevUT = double.NaN;
            prevBody = null;
            invalidStreak = 0;
            switchCameraCaptured = false;
            shipCameraSaved = false;
            teardownWaitTerrain = false;
            terrainReadyTicks = 0;
            teardownTicks = 0;
        }

        private void FailSpawn(FarWatchEndReason reason, string why)
        {
            ParsekLog.Warn(Tag, string.Format(ic, "spawn failed reason={0} detail={1}", reason, why ?? "(none)"));
            Transition(FarWatchTrigger.SpawnFailed, reason);
            ResolveAbort();
        }

        // ------------------------------------------------------------------ per-frame (Update)

        /// <summary>Per-frame progress of the frame-counted waits, and the camera net (7.5).</summary>
        internal void Update()
        {
            if (!IsRunning)
                return;
            framesInState++;
            CheckCamera();
            if (FarWatchTransitions.ShouldShowShipLostScreen(state, shipLostScreenPending, framesInState, ShipLostScreenDelayFrames))
            {
                shipLostScreenPending = false;
                ShowShipLostFlightResults();
            }
            switch (state)
            {
                case FarWatchState.Spawning:
                    UpdateSpawning();
                    break;
                case FarWatchState.HandoverOut:
                    UpdateHandoverOut();
                    break;
                case FarWatchState.HandoverBack:
                    UpdateHandoverBack();
                    break;
            }
        }

        private void UpdateSpawning()
        {
            if (drone == null)
            {
                FailSpawn(FarWatchEndReason.BuildFailed, "drone-null");
                return;
            }
            if (!drone.loaded)
            {
                if (framesInState > SpawnLoadMaxFrames)
                {
                    FailSpawn(FarWatchEndReason.SpawnTimeout, "not-loaded-in-" + SpawnLoadMaxFrames.ToString(ic) + "-frames");
                }
                return;
            }
            shipLoadedFrames++;
            if (shipLoadedFrames < DroneLoadedSettleFrames)
                return;
            CheckBuiltModules();
            BeginHandoverOut();
        }

        private void CheckBuiltModules()
        {
            var partModules = new List<string>();
            var vesselModules = new List<string>();
            CameraDrone.CollectModuleNames(drone, partModules, vesselModules);
            List<string> problems = CameraDrone.DescribeModuleProblems(partModules, vesselModules);
            if (problems.Count > 0)
                ParsekLog.Warn(DroneTag, "drone module contract differs: " + string.Join(",", problems.ToArray()));
            else
                ParsekLog.Verbose(DroneTag, "drone module contract ok: part=" + string.Join("+", partModules.ToArray())
                    + " vessel=" + string.Join("+", vesselModules.ToArray()));
        }

        private void BeginHandoverOut()
        {
            Transition(FarWatchTrigger.DroneLoaded, FarWatchEndReason.None);
            StopAutosave();
            SetInputLock();
            host.DisarmPostSwitchAutoRecordForFarWatch();
            SaveShipCamera();

            if (!drone.loaded)
            {
                ParsekLog.Error(Tag, "HandoverOut: drone not loaded at the switch (loaded-before-switch rule)");
                Transition(FarWatchTrigger.SwitchOutFailed, FarWatchEndReason.SwitchFailed);
                ResolveAbort();
                return;
            }
            bool ok = ForceSwitch(drone, "drone");
            if (!ok)
            {
                Transition(FarWatchTrigger.SwitchOutFailed, FarWatchEndReason.SwitchFailed);
                ResolveAbort();
                return;
            }
            switchedOut = true;
            recorderWaitUpdates = 0;
            try
            {
                drone.DetachPatchedConicsSolver();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(DroneTag, "DetachPatchedConicsSolver threw " + ex.GetType().Name);
            }
            host.RequestWatchMapFocusForFarWatch();
        }

        private void UpdateHandoverOut()
        {
            if (!switchedOut)
                return;
            if (host.IsRecorderBackgroundedOrIdleForFarWatch())
            {
                Transition(FarWatchTrigger.SwitchedToDrone, FarWatchEndReason.None);
                return;
            }
            recorderWaitUpdates++;
            if (recorderWaitUpdates > RecorderBackgroundMaxUpdates)
            {
                ParsekLog.Warn(Tag, string.Format(ic,
                    "recorder not backgrounded {0} updates after the switch; ending the watch",
                    recorderWaitUpdates));
                RequestEnd(FarWatchEndReason.RecorderNotBackgrounded);
            }
        }

        private void UpdateHandoverBack()
        {
            if (ship == null)
                return;
            if (!ship.loaded)
            {
                shipLoadedFrames = 0;
                if (framesInState > ShipLoadMaxFrames)
                {
                    if (!handbackRetried)
                    {
                        handbackRetried = true;
                        framesInState = 0;
                        ParsekLog.Error(Tag, string.Format(ic,
                            "ship not loaded {0} frames into HandoverBack; retrying the placement at {1} m",
                            ShipLoadMaxFrames, CameraDrone.HandbackRetryOffsetMeters));
                        PlaceBesideShip(CameraDrone.HandbackRetryOffsetMeters);
                    }
                    else
                    {
                        ParsekLog.Error(Tag, "ship never loaded on HandoverBack; the drone stays parked");
                        Transition(FarWatchTrigger.ShipNeverLoaded, FarWatchEndReason.ShipNeverLoaded);
                    }
                }
                return;
            }
            shipLoadedFrames++;
            if (shipLoadedFrames < ShipLoadedSettleFrames)
                return;
            if (!ForceSwitch(ship, "ship"))
                return;
            Transition(FarWatchTrigger.HandbackSwitched, endReason);
            RestoreShipCamera();
            teardownWaitTerrain = ship.LandedOrSplashed;
            terrainReadyTicks = 0;
            teardownTicks = 0;
            DestroyDrone("hand-back");
        }

        private bool ForceSwitch(Vessel target, string role)
        {
            bool loaded = target != null && target.loaded;
            bool packed = target != null && target.packed;
            bool ok = false;
            if (!loaded)
            {
                ParsekLog.Error(Tag, "ForceSetActiveVessel refused: target=" + role + " not loaded");
            }
            else
            {
                CameraDroneIdentity.BeginSwitch();
                try
                {
                    ok = FlightGlobals.ForceSetActiveVessel(target);
                }
                catch (Exception ex)
                {
                    ParsekLog.Error(Tag, "ForceSetActiveVessel threw " + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    CameraDroneIdentity.EndSwitch();
                }
            }
            ParsekLog.Info(Tag, string.Format(ic,
                "ForceSetActiveVessel target={0} loaded={1} packed={2} result={3}", role, loaded, packed, ok));
            return ok;
        }

        // ------------------------------------------------------------------ physics tick

        /// <summary>
        /// Every physics tick while the drone exists (section 6.3 / 6.4): renew the unpack hold,
        /// keep the drone at its hold point or on the ghost, and run the teardown's terrain wait.
        /// </summary>
        internal void Tick()
        {
            if (!IsRunning)
                return;
            winTicks++;
            OrbitPhysicsManager.HoldVesselUnpack(2);
            winHoldRenewals++;

            if (state == FarWatchState.Teardown)
            {
                TickTeardown();
            }
            else if (drone != null && !destroyingDrone)
            {
                if (state == FarWatchState.Following && followGhost)
                    TickFollow();
                else if (surfaceHeld)
                    WriteHoldPoint();
            }

            ParsekLog.VerboseRateLimited(DroneTag, "far-watch-tick",
                FarWatchTransitions.FormatTickSummary(
                    winTicks, winWrites, winSkips, winFloorClamps, winMaxDev, winHoldRenewals),
                TickSummaryIntervalSeconds);
        }

        private void TickTeardown()
        {
            teardownTicks++;
            if (teardownWaitTerrain && ship != null && ship.mainBody != null)
            {
                Vector3d sp = ship.GetWorldPos3D();
                Vector3d up = (sp - ship.mainBody.position).normalized;
                RaycastHit hit;
                bool hitTerrain = Physics.Raycast((Vector3)(sp + up * 20.0), (Vector3)(-up), out hit, 200f, 1 << 15);
                terrainReadyTicks = hitTerrain ? terrainReadyTicks + 1 : 0;
                if (terrainReadyTicks < HoldTicksAfterTerrain && teardownTicks < TerrainWaitMaxTicks)
                    return;
                if (terrainReadyTicks < HoldTicksAfterTerrain)
                    ParsekLog.Warn(Tag, string.Format(ic,
                        "terrain under the ship never answered in {0} ticks; releasing the hold", teardownTicks));
            }
            else if (teardownTicks < HoldTicksAfterTerrain)
            {
                return;
            }
            FinishTeardown();
        }

        private void FinishTeardown()
        {
            RemoveInputLock();
            RestartAutosave();
            Transition(FarWatchTrigger.TeardownComplete, endReason);
            CameraDroneIdentity.Clear("teardown");
            FarWatchAwardScope.Clear();
            ParsekLog.Info(DroneTag, string.Format(ic, "hold released after {0} ticks (terrainWait={1})",
                teardownTicks, teardownWaitTerrain));
            ClearRefs();
        }

        private void TickFollow()
        {
            var committed = RecordingStore.CommittedRecordings;
            if (!RekeyToWatchedRecording(committed))
                return;
            int index = ResolveGhostIndex(committed);
            if (index < 0)
            {
                ParsekLog.Warn(Tag, "watched ghost recording is gone; ending the watch");
                RequestEnd(FarWatchEndReason.GhostGone);
                return;
            }
            Recording rec = committed[index];
            double ut = Planetarium.GetUniversalTime();
            double playbackUT = ut;
            double loopUT;
            long cycle;
            bool inPause;
            if (host.TryComputeLoopPlaybackUTForWatch(rec, ut, out loopUT, out cycle, out inPause, index))
                playbackUT = loopUT;

            Vector3d world;
            if (!host.Positioner.TryResolvePlaybackWorldPosition(index, rec, null, playbackUT, out world))
            {
                winSkips++;
                return;
            }
            CelestialBody body = FlightGlobals.getMainBody(world);
            if (body == null)
            {
                winSkips++;
                return;
            }
            if (ship != null && ship.mainBody != null && body != ship.mainBody)
            {
                ParsekLog.Info(Tag, string.Format(ic,
                    "ghost left the ship's body ghostBody={0} shipBody={1} (other bodies are a later step)",
                    body.bodyName, ship.mainBody.bodyName));
                RequestEnd(FarWatchEndReason.GhostLeftBody);
                return;
            }

            Vector3d r = world - body.position;
            Vector3d vDiff = lastVDiff;
            if (prevBody == body && !double.IsNaN(prevUT) && ut > prevUT)
                vDiff = (r - prevR) / (ut - prevUT);
            prevR = r;
            prevUT = ut;
            prevBody = body;
            lastVDiff = vDiff;
            Vector3d rfrm = body.getRFrmVel(world);
            bool rotating = FlightGlobals.RefFrameIsRotating;
            Vector3d vObt = rotating ? vDiff + rfrm : vDiff;
            Vector3d vSrf = rotating ? vDiff : vDiff - rfrm;

            ApplyFloor(body, ref world, ref vObt, vSrf, r);

            CameraDroneStateCheck check = CameraDrone.ValidateState(world - body.position, vObt, body.Radius, body.gravParameter);
            if (check != CameraDroneStateCheck.Valid)
            {
                invalidStreak++;
                winSkips++;
                ParsekLog.WarnRateLimited(DroneTag, "far-watch-invalid-state",
                    string.Format(ic, "invalid drone state check={0} streak={1} rec=#{2}", check, invalidStreak, index), 5.0);
                if (invalidStreak >= InvalidStatesToEnd)
                    RequestEnd(FarWatchEndReason.InvalidState);
                return;
            }
            invalidStreak = 0;
            double dev = (drone.GetWorldPos3D() - world).magnitude;
            if (dev > winMaxDev) winMaxDev = dev;
            if (CameraDrone.WriteState(drone, body, world, vObt))
                winWrites++;
        }

        /// <summary>
        /// Keeps a follow watch on watch mode's target: a chain segment transfer or a ghost switch
        /// moves the watch to another recording and the drone follows it; a watch that ended
        /// without reaching the far-watch teardown ends the far watch. False when the tick stops.
        /// </summary>
        private bool RekeyToWatchedRecording(IReadOnlyList<Recording> committed)
        {
            bool watching = host.IsWatchingGhost;
            int watchedIndex = watching ? host.WatchedRecordingIndex : -1;
            string watchedId = watchedIndex >= 0 && watchedIndex < committed.Count && committed[watchedIndex] != null
                ? committed[watchedIndex].RecordingId : null;
            FarWatchFollowRekey decision = FarWatchTransitions.DecideFollowRekey(
                watching, watchedIndex, watchedId, ghostIndex, ghostRecordingId);
            switch (decision)
            {
                case FarWatchFollowRekey.Keep:
                    return true;
                case FarWatchFollowRekey.Rekey:
                    ParsekLog.Info(Tag, string.Format(ic,
                        "follow re-keyed to the watched recording from=#{0} id={1} to=#{2} id={3}",
                        ghostIndex, ghostRecordingId ?? "(null)", watchedIndex, watchedId ?? "(null)"));
                    ghostIndex = watchedIndex;
                    ghostRecordingId = watchedId;
                    prevBody = null;
                    prevUT = double.NaN;
                    invalidStreak = 0;
                    return true;
                default:
                    ParsekLog.Warn(Tag, string.Format(ic,
                        "follow watch lost its watch-mode target rec=#{0} id={1}; ending the watch",
                        ghostIndex, ghostRecordingId ?? "(null)"));
                    RequestEnd(FarWatchEndReason.WatchExit);
                    return false;
            }
        }

        private int ResolveGhostIndex(IReadOnlyList<Recording> committed)
        {
            if (ghostIndex >= 0 && ghostIndex < committed.Count
                && committed[ghostIndex] != null && committed[ghostIndex].RecordingId == ghostRecordingId)
                return ghostIndex;
            for (int i = 0; i < committed.Count; i++)
            {
                if (committed[i] != null && committed[i].RecordingId == ghostRecordingId)
                {
                    ghostIndex = i;
                    return i;
                }
            }
            return -1;
        }

        private void ApplyFloor(CelestialBody body, ref Vector3d world, ref Vector3d vObt, Vector3d vSrf, Vector3d r)
        {
            double lat = body.GetLatitude(world);
            double lon = body.GetLongitude(world);
            double alt = body.GetAltitude(world);
            double terrain = SafeTerrain(body, lat, lon);
            bool ocean = body.ocean && terrain < 0.0;
            Vector3d up = r.normalized;
            double vUp = Vector3d.Dot(vSrf, up);
            double g = body.gravParameter / Math.Max(1.0, r.sqrMagnitude);
            double floor = CameraDrone.TerrainFloorMeters(vUp, TimeWarp.CurrentRate * Time.fixedDeltaTime, g);
            bool clamped;
            double written = CameraDrone.ClampAltitudeToFloor(alt, terrain, ocean, floor, out clamped);
            if (!clamped)
                return;
            winFloorClamps++;
            world = body.GetWorldSurfacePosition(lat, lon, written);
            if (vUp < 0.0)
                vObt -= up * vUp;
        }

        private static double SafeTerrain(CelestialBody body, double lat, double lon)
        {
            if (body == null || body.pqsController == null) return 0.0;
            try { return body.TerrainAltitude(lat, lon, true); }
            catch (Exception) { return 0.0; }
        }

        private void SetHoldPoint(CelestialBody body, Vector3d world)
        {
            surfaceHeld = true;
            holdBody = body;
            holdLat = body.GetLatitude(world);
            holdLon = body.GetLongitude(world);
            holdAlt = body.GetAltitude(world);
        }

        private void WriteHoldPoint()
        {
            if (holdBody == null) return;
            Vector3d pos = holdBody.GetWorldSurfacePosition(holdLat, holdLon, holdAlt);
            if (CameraDrone.WriteState(drone, holdBody, pos, holdBody.getRFrmVel(pos)))
                winWrites++;
        }

        /// <summary>Puts the drone <paramref name="offsetMeters"/> beside the ship (7.2 step 1).</summary>
        private void PlaceBesideShip(double offsetMeters)
        {
            if (drone == null || ship == null || ship.mainBody == null)
                return;
            CelestialBody body = ship.mainBody;
            double ut = Planetarium.GetUniversalTime();
            bool landed = ship.LandedOrSplashed;
            Vector3d shipPos = landed
                ? body.GetWorldSurfacePosition(ship.latitude, ship.longitude, ship.altitude)
                : ship.orbit.getPositionAtUT(ut);
            Vector3d shipVel = landed ? body.getRFrmVel(shipPos) : ship.orbit.getOrbitalVelocityAtUT(ut).xzy;
            Vector3d pos;
            bool held;
            if (!CameraDrone.TryComputeBesideShip(shipPos, body.position, shipVel, landed, offsetMeters, out pos, out held))
            {
                ParsekLog.Error(DroneTag, "hand-back placement degenerate; drone left where it is");
                return;
            }
            if (drone.mainBody != body)
                ParsekLog.Info(DroneTag, string.Format(ic, "hand-back crosses bodies {0} -> {1}",
                    drone.mainBody != null ? drone.mainBody.bodyName : "(none)", body.bodyName));
            if (held)
            {
                SetHoldPoint(body, pos);
                WriteHoldPoint();
            }
            else
            {
                surfaceHeld = false;
                holdBody = body;
                CameraDrone.WriteState(drone, body, pos, shipVel);
            }
            ParsekLog.Info(DroneTag, string.Format(ic,
                "drone placed for hand-back offset={0} surfaceHeld={1} body={2} shipLoaded={3}",
                offsetMeters.ToString("F0", ic), held, body.bodyName, ship.loaded));
        }

        // ------------------------------------------------------------------ end

        /// <summary>
        /// The one entry for every end of a far watch (design 7.5). From Spawning it aborts (the
        /// drone is not active); from HandoverOut / Following it starts the hand-back; a scene
        /// exit tears down with no switch. Later states ignore it.
        /// </summary>
        internal void RequestEnd(FarWatchEndReason reason)
        {
            if (!IsRunning)
            {
                ParsekLog.Verbose(Tag, "RequestEnd ignored (idle) reason=" + reason);
                return;
            }
            if (reason == FarWatchEndReason.SceneExit)
            {
                OnSceneExit();
                return;
            }
            switch (state)
            {
                case FarWatchState.Spawning:
                    Transition(FarWatchTrigger.EndRequested, reason);
                    ResolveAbort();
                    break;
                case FarWatchState.HandoverOut:
                case FarWatchState.Following:
                    Transition(FarWatchTrigger.EndRequested, reason);
                    BeginHandoverBack();
                    break;
                default:
                    ParsekLog.Verbose(Tag, string.Format(ic,
                        "RequestEnd ignored in state={0} reason={1}", state, reason));
                    break;
            }
        }

        private void BeginHandoverBack()
        {
            TimeWarp.SetRate(0, true);
            followGhost = false;
            shipLoadedFrames = 0;
            if (host.IsWatchingGhost)
                host.ExitWatchMode(skipCameraRestore: true);
            PlaceBesideShip(CameraDrone.HandbackOffsetMeters);
        }

        /// <summary>
        /// Scene exit (7.5): no switch; stop owning the drone and keep its ids for the next
        /// save / load strip. The hold and autosave go with the scene; the input lock is global
        /// and is removed here.
        /// </summary>
        /// <summary>
        /// The in-game test runner's between-run reset: end a far watch in this frame, with no
        /// hand-back wait. The drone is destroyed only once it is not the active vessel (a switch
        /// to the loaded ship first); with no ship to switch to the watch and its ids are kept and
        /// false returned, so the caller must not drop the ids of a drone that still exists.
        /// </summary>
        internal bool EndForTestRunnerReset(string reason)
        {
            if (!IsRunning)
                return true;
            Vessel active = null;
            try { active = FlightGlobals.ActiveVessel; } catch (Exception) { }
            bool droneActive = drone != null && active == drone;
            bool shipLoaded = ship != null && ship.loaded;
            FarWatchImmediateEnd decision = FarWatchTransitions.DecideImmediateEnd(drone != null, droneActive, shipLoaded);
            ParsekLog.Info(Tag, string.Format(ic,
                "test-runner reset reason={0} state={1} decision={2} droneActive={3} shipLoaded={4}",
                reason ?? "(none)", state, decision, droneActive, shipLoaded));
            if (decision == FarWatchImmediateEnd.KeepUntilGone)
            {
                ParsekLog.Warn(Tag, "test-runner reset: the drone is active and no loaded ship can take over; far watch and drone ids kept");
                RequestEnd(FarWatchEndReason.TestRunnerReset);
                return false;
            }
            if (decision == FarWatchImmediateEnd.SwitchToShipThenDestroy)
            {
                if (!ForceSwitch(ship, "ship"))
                {
                    ParsekLog.Warn(Tag, "test-runner reset: switch to the ship failed; far watch and drone ids kept");
                    RequestEnd(FarWatchEndReason.TestRunnerReset);
                    return false;
                }
                RestoreShipCamera();
            }
            if (drone != null)
                DestroyDrone("test-runner-reset");
            RemoveInputLock();
            RestartAutosave();
            Transition(FarWatchTrigger.SceneExit, FarWatchEndReason.TestRunnerReset);
            CameraDroneIdentity.Clear("test-runner-reset");
            FarWatchAwardScope.Clear();
            ClearRefs();
            return true;
        }

        internal void OnSceneExit()
        {
            if (!IsRunning)
                return;
            endReason = FarWatchEndReason.SceneExit;
            Transition(FarWatchTrigger.SceneExit, FarWatchEndReason.SceneExit);
            followGhost = false;
            RemoveInputLock();
            if (CameraDroneIdentity.Exists)
                CameraDroneIdentity.MarkStripPending();
            else
                CameraDroneIdentity.Clear("scene-exit-no-drone");
            ClearRefs();
        }

        /// <summary>ParsekFlight.OnDestroy: a running watch ends like a scene exit.</summary>
        internal void Dispose()
        {
            if (IsRunning)
                OnSceneExit();
        }

        /// <summary>
        /// The drone is being destroyed (7.6). Our own Die at hand-back or abort is expected;
        /// anything else is a loss.
        /// </summary>
        internal void OnDroneWillDestroy(Vessel v)
        {
            if (destroyingDrone || state == FarWatchState.Teardown)
            {
                ParsekLog.Verbose(DroneTag, "drone destroy observed (expected) pid=" + (v != null ? v.persistentId.ToString(ic) : "?"));
                drone = null;
                return;
            }
            bool wasActive = v != null && v == FlightGlobals.ActiveVessel;
            ParsekLog.Error(DroneTag, string.Format(ic, "drone lost state={0} active={1} pid={2}",
                state, wasActive, v != null ? v.persistentId : 0u));
            drone = null;
            if (!IsRunning)
                return;
            if (!FarWatchTransitions.TryTransition(state, FarWatchTrigger.DroneLost, out FarWatchState _))
                return;
            Transition(FarWatchTrigger.DroneLost, FarWatchEndReason.DroneLost);
            ResolveAbort();
        }

        /// <summary>Any vessel destroyed: the player ship lost during the watch (7.6).</summary>
        internal void OnVesselWillDestroy(Vessel v)
        {
            if (!IsRunning || v == null || ship == null || v != ship)
                return;
            ParsekLog.Error(Tag, string.Format(ic, "player ship lost during far watch state={0} shipPid={1}",
                state, v.persistentId));
            ship = null;
            // From here no save may look for the ship: the strip removes the drone with no ship
            // repair and never refuses the save (stock's flight-ended screen saves on its exits).
            CameraDroneIdentity.MarkShipLost();
            if (FarWatchTransitions.TryTransition(state, FarWatchTrigger.ShipLost, out FarWatchState _))
            {
                Transition(FarWatchTrigger.ShipLost, FarWatchEndReason.ShipLost);
                followGhost = false;
                ResolveAbort();
                // Still aborted: the drone is the active vessel and no ship is left to hand back
                // to. Stock shows nothing (the ship was not active), so Parsek shows its screen.
                shipLostScreenPending = state == FarWatchState.Aborted;
                if (shipLostScreenPending)
                    ParsekLog.Info(Tag, string.Format(ic,
                        "ship lost with the drone active: stock's flight-ended screen shows in {0} frames",
                        ShipLostScreenDelayFrames));
            }
        }

        /// <summary>
        /// Stock's flight-ended screen, shown the way <c>FlightLogger.waitAndEndFlight</c> shows it
        /// (decompiled 1.12.5): the persistent save, then <c>FlightResultsDialog.Display</c> with
        /// its exit controls (Revert and Space Center) and the catastrophic-failure message. Both
        /// buttons leave the flight through stock code; every save strips the drone (9.1).
        /// </summary>
        private void ShowShipLostFlightResults()
        {
            try
            {
                Game game = HighLogic.CurrentGame;
                if (game == null)
                {
                    ParsekLog.Warn(Tag, "ship-lost screen: no current game; nothing shown");
                    return;
                }
                if (game.Mode == Game.Modes.MISSION)
                {
                    ParsekLog.Info(Tag, "ship-lost screen: mission mode, stock shows no flight results; nothing shown");
                    return;
                }
                GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                KSP.UI.Dialogs.FlightResultsDialog.showExitControls = true;
                KSP.UI.Dialogs.FlightResultsDialog.allowClosingDialog = true;
                KSP.UI.Dialogs.FlightResultsDialog.Display(
                    KSP.Localization.Localizer.Format("#autoLOC_135586",
                        KSP.Localization.Localizer.Format("#autoLOC_135143")));
                ParsekLog.Info(Tag, string.Format(ic,
                    "ship-lost screen shown (stock FlightResultsDialog, exit controls on) ship='{0}'",
                    shipName ?? "(unknown)"));
            }
            catch (Exception e)
            {
                ParsekLog.Error(Tag, "ship-lost screen failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// Aborted (7.6): a drone that is not active is destroyed and the watch ends at once; a
        /// lost active drone hands focus to a loaded ship. With no loaded ship the watch stays
        /// Aborted with the drone parked (or dead) and the fences up until the scene exits.
        /// </summary>
        private void ResolveAbort()
        {
            if (state != FarWatchState.Aborted)
                return;
            Vessel active = FlightGlobals.ActiveVessel;
            bool droneActive = drone != null && active == drone;
            bool droneAlive = drone != null && !droneActive;
            if (!switchedOut || (droneAlive && active == ship))
            {
                if (drone != null)
                    DestroyDrone("abort");
                RemoveInputLock();
                RestartAutosave();
                Transition(FarWatchTrigger.AbortResolved, endReason);
                CameraDroneIdentity.Clear("abort");
                FarWatchAwardScope.Clear();
                ClearRefs();
                return;
            }
            if (ship != null && ship.loaded && active != ship && endReason == FarWatchEndReason.DroneLost)
            {
                if (ForceSwitch(ship, "ship"))
                {
                    Transition(FarWatchTrigger.AbortToTeardown, endReason);
                    RestoreShipCamera();
                    teardownWaitTerrain = ship.LandedOrSplashed;
                    terrainReadyTicks = 0;
                    teardownTicks = 0;
                    return;
                }
            }
            ParsekLog.Error(Tag, string.Format(ic,
                "far watch aborted with no hand-back reason={0} shipLoaded={1} droneAlive={2}; waiting for the scene exit",
                endReason, ship != null && ship.loaded, drone != null));
        }

        private void DestroyDrone(string reason)
        {
            if (drone == null) return;
            destroyingDrone = true;
            Vessel d = drone;
            try
            {
                CameraDrone.Destroy(d, reason);
            }
            finally
            {
                drone = null;
                destroyingDrone = false;
            }
        }

        private void ClearRefs()
        {
            drone = null;
            ship = null;
            ghostIndex = -1;
            ghostRecordingId = null;
            followGhost = false;
            surfaceHeld = false;
            holdBody = null;
        }

        // ------------------------------------------------------------------ camera

        /// <summary>
        /// <c>onVesselSwitching(from, to)</c> while the drone exists: capture the camera's offset
        /// from its pivot and the camera mode, so the view is restored by world position (7.7).
        /// </summary>
        internal void OnVesselSwitching(Vessel from, Vessel to)
        {
            if (!IsRunning) return;
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null) return;
            Transform pivot = cam.GetPivot();
            if (pivot == null) return;
            switchCameraOffset = cam.transform.position - pivot.position;
            switchCameraMode = cam.mode;
            switchCameraCaptured = true;
            ParsekLog.Verbose(CameraTag, string.Format(ic,
                "far-watch switch capture from={0} to={1} mode={2} offsetM={3}",
                from != null ? from.persistentId : 0u, to != null ? to.persistentId : 0u, cam.mode,
                switchCameraOffset.magnitude.ToString("F1", ic)));
        }

        /// <summary>
        /// <c>OnVesselSwitchComplete(drone)</c>, after watch mode re-targeted the ghost: place the
        /// camera by world position (never copying pitch / heading across targets, 7.7).
        /// </summary>
        internal void OnDroneArrival()
        {
            ParsekLog.Info(Tag, string.Format(ic, "drone arrival handled state={0} watching={1}",
                state, host.IsWatchingGhost));
            if (!host.IsWatchingGhost)
            {
                RetargetCameraToDrone("drone-arrival-no-watch");
                return;
            }
            RestoreSwitchCamera();
        }

        private void RestoreSwitchCamera()
        {
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null || !switchCameraCaptured) return;
            if (!IsPositionRestoreMode(switchCameraMode))
            {
                ParsekLog.Info(CameraTag, "far-watch camera position restore skipped: mode=" + switchCameraMode);
                return;
            }
            Transform pivot = cam.GetPivot();
            if (pivot == null) return;
            if (cam.mode != switchCameraMode)
                cam.setModeImmediate(switchCameraMode);
            cam.SetCamCoordsFromPosition(pivot.position + switchCameraOffset);
            ParsekLog.Info(CameraTag, string.Format(ic,
                "far-watch camera placed by world position mode={0} offsetM={1}",
                cam.mode, switchCameraOffset.magnitude.ToString("F1", ic)));
        }

        internal static bool IsPositionRestoreMode(FlightCamera.Modes mode)
        {
            return mode == FlightCamera.Modes.AUTO || mode == FlightCamera.Modes.FREE
                || mode == FlightCamera.Modes.ORBITAL || mode == FlightCamera.Modes.CHASE;
        }

        private void SaveShipCamera()
        {
            if (shipCameraSaved) return;
            if (host.TryGetWatchSavedCameraForFarWatch(out Vessel v, out float dist, out float pitchDeg, out float hdgDeg)
                && v == ship)
            {
                shipCameraDistance = dist;
                shipCameraPitchDeg = pitchDeg;
                shipCameraHeadingDeg = hdgDeg;
                shipCameraSaved = true;
                ParsekLog.Verbose(CameraTag, string.Format(ic,
                    "far-watch saved the ship's watch-entry camera dist={0} pitchDeg={1} hdgDeg={2}",
                    dist.ToString("F1", ic), pitchDeg.ToString("F1", ic), hdgDeg.ToString("F1", ic)));
            }
        }

        /// <summary>Hand-back: the watch-entry values go back onto the ship they were captured on (7.2 step 5).</summary>
        private void RestoreShipCamera()
        {
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null || ship == null) return;
            cam.SetTargetVessel(ship);
            if (shipCameraSaved)
            {
                cam.SetDistance(shipCameraDistance);
                cam.camPitch = shipCameraPitchDeg * Mathf.Deg2Rad;
                cam.camHdg = shipCameraHeadingDeg * Mathf.Deg2Rad;
            }
            ParsekLog.Info(CameraTag, string.Format(ic,
                "far-watch hand-back camera restored onto the ship saved={0} shipPid={1}",
                shipCameraSaved, ship.persistentId));
        }

        /// <summary>
        /// Same-frame re-target on a watch exit while the drone exists (7.5): the active drone in
        /// Vessel mode (which has the destroy rescue), never a ghost transform.
        /// </summary>
        internal static void RetargetCameraToDrone(string context)
        {
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null) return;
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null || !CameraDrone.Is(active))
                return;
            cam.TargetActiveVessel();
            ParsekLog.Info(CameraTag, "far-watch camera re-targeted to the drone context=" + (context ?? "(none)"));
        }

        /// <summary>
        /// The camera net's re-target (7.5): the active vessel in Vessel mode, the drone while it
        /// is active and the ship in every other state the drone exists in, never a ghost.
        /// </summary>
        private static void RetargetCameraToActiveVessel(string context)
        {
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null) return;
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null)
            {
                ParsekLog.Warn(CameraTag, "far-watch camera net: no active vessel to re-target to context=" + (context ?? "(none)"));
                return;
            }
            cam.TargetActiveVessel();
            ParsekLog.Info(CameraTag, string.Format(ic,
                "far-watch camera re-targeted to the active vessel pid={0} drone={1} context={2}",
                active.persistentId, CameraDrone.Is(active), context ?? "(none)"));
        }

        private void CheckCamera()
        {
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null)
            {
                if (!cameraLost)
                {
                    cameraLost = true;
                    ParsekLog.Error(CameraTag, "FlightCamera.fetch is null during far watch (camera lost; unrecoverable in this scene)");
                }
                return;
            }
            if (!FarWatchTransitions.CameraNetRuns(state))
                return;
            Transform target = cam.Target;
            if (target == null)
            {
                RetargetCameraToActiveVessel("net-null-target");
                return;
            }
            GhostPlaybackEngine engine = host.Engine;
            if (engine == null || engine.ghostStates == null)
                return;
            int watched = host.IsWatchingGhost ? host.WatchedRecordingIndex : -1;
            foreach (var kv in engine.ghostStates)
            {
                if (kv.Key == watched || kv.Value == null || kv.Value.ghost == null)
                    continue;
                if (target.IsChildOf(kv.Value.ghost.transform))
                {
                    ParsekLog.Warn(CameraTag, string.Format(ic,
                        "far-watch camera net: camera under unowned ghost #{0} '{1}' state={2}; re-targeting to the active vessel",
                        kv.Key, kv.Value.ghost.name, state));
                    RetargetCameraToActiveVessel("net-unowned-ghost");
                    return;
                }
            }
        }

        // ------------------------------------------------------------------ saves and input

        private void StopAutosave()
        {
            FlightAutoSave fas = FlightAutoSave.fetch;
            if (fas == null)
            {
                ParsekLog.Verbose(Tag, "autosave not present; nothing to stop");
                return;
            }
            fas.StopAllCoroutines();
            autosaveStopped = true;
            ParsekLog.Info(Tag, "autosave coroutine stopped for the far watch");
        }

        private void RestartAutosave()
        {
            if (!autosaveStopped) return;
            autosaveStopped = false;
            FlightAutoSave fas = FlightAutoSave.fetch;
            if (fas == null) return;
            try
            {
                fas.Start();
                ParsekLog.Info(Tag, "autosave coroutine restarted");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "autosave restart threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void SetInputLock()
        {
            InputLockManager.SetControlLock(InputLockMask, InputLockId);
            inputLockSet = true;
            ParsekLog.Info(Tag, "input lock set " + InputLockId + ": " + InputLockMask);
        }

        private void RemoveInputLock()
        {
            if (!inputLockSet) return;
            InputLockManager.RemoveControlLock(InputLockId);
            inputLockSet = false;
            ParsekLog.Info(Tag, "input lock removed " + InputLockId);
        }

        // ------------------------------------------------------------------ transitions

        private void Transition(FarWatchTrigger trigger, FarWatchEndReason reason)
        {
            FarWatchState from = state;
            FarWatchState to;
            if (!FarWatchTransitions.TryTransition(from, trigger, out to))
            {
                ParsekLog.Warn(Tag, string.Format(ic, "illegal transition ignored state={0} trigger={1}", from, trigger));
                return;
            }
            state = to;
            framesInState = 0;
            if (reason != FarWatchEndReason.None)
                endReason = reason;
            double ut = 0.0;
            try { ut = Planetarium.GetUniversalTime(); } catch (Exception) { }
            ParsekLog.Info(Tag, FarWatchTransitions.FormatTransitionLine(
                from, to, endReason, ghostRecordingId, shipName,
                CameraDroneIdentity.ShipPid, CameraDroneIdentity.DronePid, ut));
        }
    }
}
