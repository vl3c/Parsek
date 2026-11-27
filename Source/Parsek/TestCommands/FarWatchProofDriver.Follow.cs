using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Proof = Parsek.TestCommands.TestCommandFarWatchProof;

namespace Parsek.TestCommands
{
    // Following a ghost by orbit rewrite (proofs 4, 5, 6), the synthetic far move, the solver
    // toggle, the unlisted-vessel test (proof 11), the warp soak (proof 3) and the camera
    // precision reading.
    internal sealed partial class FarWatchProofDriver
    {
        private const double FloorBaseMeters = 20.0;
        private const double OriginShiftMeters = 5000.0;
        private const int StaleCaptureFrames = 120;

        private struct GhostCapture
        {
            public IPlaybackTrajectory Traj;
            public GhostPlaybackState State;
            public double PlaybackUT;
            public double AtUT;
            public int Frame;
        }

        private readonly Dictionary<int, GhostCapture> captures = new Dictionary<int, GhostCapture>();
        private GhostPlaybackEngine hookedEngine;
        private Func<int, IPlaybackTrajectory, GhostPlaybackState, double, double> origRenderDistance;
        private Func<int, IPlaybackTrajectory, GhostPlaybackState, double, double> origActiveDistance;
        private bool hooksWrapped;

        private bool following;
        private int followIndex = -1;
        private int fixedIndex = -1;
        private string followTree;
        private int followTicks;
        private bool lastResolveOk;
        private Vector3d prevR, lastVDiff;
        private double prevUT = double.NaN;
        private CelestialBody prevBody, lastDroneBody;
        private string lastGhostBody;
        private double lastTerrainUnderGhost = double.NaN, lastGhostAlt = double.NaN, lastGridStep = double.NaN;
        private Vector3d camD1, camD2;
        private int camSamples;
        private bool unlistActive;
        private CelestialBody lastWriteBody;
        private double lastWriteLat, lastWriteLon, lastWriteAlt;

        // No ghost to follow this tick (a gap between members, or past the replay): the
        // drone holds its last written point body-fixed instead of coasting on a sub-orbital
        // arc into the ground.
        private void HoldLastPoint()
        {
            if (lastWriteBody == null || drone == null) return;
            Vector3d pos = lastWriteBody.GetWorldSurfacePosition(lastWriteLat, lastWriteLon, lastWriteAlt);
            WriteDrone(lastWriteBody, pos, lastWriteBody.getRFrmVel(pos), true);
        }

        // ----- Ghost position source: the engine's own distance resolvers, wrapped -----

        private void WrapEngineHooks()
        {
            ParsekFlight pf = ParsekFlight.Instance;
            GhostPlaybackEngine eng = pf != null ? pf.Engine : null;
            if (eng == null || eng == hookedEngine) return;
            UnwrapEngineHooks();
            var renderOrig = eng.ResolvePlaybackDistanceOverride;
            var activeOrig = eng.ResolvePlaybackActiveVesselDistanceOverride;
            origRenderDistance = renderOrig;
            origActiveDistance = activeOrig;
            eng.ResolvePlaybackDistanceOverride = (i, t, s, u) =>
            {
                CaptureGhost(i, t, s, u);
                return renderOrig != null ? renderOrig(i, t, s, u) : double.NaN;
            };
            eng.ResolvePlaybackActiveVesselDistanceOverride = (i, t, s, u) =>
            {
                CaptureGhost(i, t, s, u);
                return activeOrig != null ? activeOrig(i, t, s, u) : double.NaN;
            };
            hookedEngine = eng;
            hooksWrapped = true;
        }

        private void UnwrapEngineHooks()
        {
            if (hookedEngine != null)
            {
                hookedEngine.ResolvePlaybackDistanceOverride = origRenderDistance;
                hookedEngine.ResolvePlaybackActiveVesselDistanceOverride = origActiveDistance;
            }
            hookedEngine = null;
            hooksWrapped = false;
            captures.Clear();
        }

        private void CaptureGhost(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (traj == null) return;
            captures[index] = new GhostCapture
            {
                Traj = traj,
                State = state,
                PlaybackUT = playbackUT,
                AtUT = Planetarium.GetUniversalTime(),
                Frame = Time.frameCount,
            };
        }

        private bool IsCandidate(int index, GhostCapture c, double ut)
        {
            if (Time.frameCount - c.Frame > StaleCaptureFrames) return false;
            var rec = c.Traj as Recording;
            if (rec == null || rec.IsDebris) return false;
            if (!string.IsNullOrEmpty(followTree) && rec.TreeId != followTree) return false;
            double p = c.PlaybackUT + (ut - c.AtUT);
            return p >= c.Traj.StartUT && p <= c.Traj.EndUT;
        }

        private int PickMember(double ut)
        {
            if (fixedIndex >= 0) return fixedIndex;
            if (followIndex >= 0 && captures.TryGetValue(followIndex, out GhostCapture cur) && IsCandidate(followIndex, cur, ut))
                return followIndex;
            ParsekFlight pf = ParsekFlight.Instance;
            int watched = pf != null && pf.IsWatchingGhost ? pf.WatchedRecordingIndex : -1;
            if (watched >= 0 && captures.TryGetValue(watched, out GhostCapture w) && IsCandidate(watched, w, ut))
                return watched;
            int best = -1;
            foreach (KeyValuePair<int, GhostCapture> kv in captures)
                if (IsCandidate(kv.Key, kv.Value, ut) && (best < 0 || kv.Key < best)) best = kv.Key;
            return best;
        }

        private bool TryResolveFollowed(double ut, out Vector3d world, out CelestialBody body)
        {
            world = Vector3d.zero;
            body = null;
            ParsekFlight pf = ParsekFlight.Instance;
            if (pf == null || followIndex < 0 || !captures.TryGetValue(followIndex, out GhostCapture c)) return false;
            if (Time.frameCount - c.Frame > StaleCaptureFrames) return false;
            double p = c.PlaybackUT + (ut - c.AtUT);
            if (!pf.Positioner.TryResolvePlaybackWorldPosition(followIndex, c.Traj, c.State, p, out world)) return false;
            body = FlightGlobals.getMainBody(world);
            return body != null;
        }

        private Transform FollowedGhostPivot()
        {
            ParsekFlight pf = ParsekFlight.Instance;
            if (pf == null || pf.Engine == null || followIndex < 0) return null;
            if (!pf.Engine.TryGetGhostState(followIndex, out GhostPlaybackState st) || st == null || st.ghost == null
                || !st.ghost.activeInHierarchy) return null;
            return st.cameraPivot != null ? st.cameraPivot : st.ghost.transform;
        }

        // ----- Follow (proofs 4, 5, 6) -----

        private IEnumerator OpFollow(Proof.Args a)
        {
            if (drone == null || drone != FlightGlobals.ActiveVessel) { Finish("drone-not-active"); yield break; }
            WrapEngineHooks();
            followTree = a.Tree;
            fixedIndex = a.Index;
            followIndex = fixedIndex;
            following = true;
            anchorOn = false;
            prevUT = double.NaN;
            prevBody = null;
            float start = Time.realtimeSinceStartup;
            while (!lastResolveOk && Time.realtimeSinceStartup - start < 30f) yield return null;
            if (!lastResolveOk)
            {
                Obs("follow", "ut", Ut(), "result", "no-ghost", "captures", N(captures.Count), "tree", followTree ?? "-");
                Finish("no-ghost", "captures", N(captures.Count));
                yield break;
            }
            float near = Time.realtimeSinceStartup;
            while (CurrentDeviation() > 500.0 && Time.realtimeSinceStartup - near < 10f) yield return null;
            ParsekFlight pf = ParsekFlight.Instance;
            bool watchTried = false;
            if (a.Watch && pf != null && !pf.IsWatchingGhost)
            {
                watchTried = true;
                pf.EnterWatchMode(followIndex);
            }
            else if (!a.Watch)
            {
                ApplyCameraAfterSwitch(true);
            }
            float hold = Time.realtimeSinceStartup;
            double box = double.IsNaN(a.Seconds) ? 5.0 : a.Seconds;
            while (Time.realtimeSinceStartup - hold < box) yield return null;
            string member = captures.TryGetValue(followIndex, out GhostCapture c) ? c.Traj.VesselName : "-";
            Obs("follow", "ut", Ut(), "result", "ok", "index", N(followIndex), "member", member,
                "tree", followTree ?? "-", "devM", F(CurrentDeviation()), "watchTried", B(watchTried),
                "watching", B(pf != null && pf.IsWatchingGhost), "watchedIndex", pf != null && pf.IsWatchingGhost ? N(pf.WatchedRecordingIndex) : "-",
                "camTarget", FlightCamera.fetch != null && FlightCamera.fetch.Target != null ? FlightCamera.fetch.Target.name : "-",
                "captures", N(captures.Count));
            Finish("ok", "index", N(followIndex), "member", member, "devM", F(CurrentDeviation()));
        }

        private double CurrentDeviation()
        {
            Transform pivot = FollowedGhostPivot();
            if (pivot == null || drone == null) return double.NaN;
            return ((Vector3d)drone.transform.position - (Vector3d)pivot.position).magnitude;
        }

        private void StopFollow(string why)
        {
            if (following)
                Obs("follow-stop", "ut", Ut(), "why", why, "index", N(followIndex), "ticks", N(followTicks));
            following = false;
            followIndex = -1;
            lastWriteBody = null;
            fixedIndex = -1;
            lastResolveOk = false;
        }

        private void TickFollow()
        {
            if (!following || drone == null) return;
            winTicks++;
            followTicks++;
            double ut = Planetarium.GetUniversalTime();
            if (followIndex < 0 || followTicks % 25 == 0 || !lastResolveOk)
            {
                int pick = PickMember(ut);
                if (pick != followIndex && pick >= 0)
                {
                    Obs("follow-member", "ut", Ut(), "from", N(followIndex), "to", N(pick),
                        "name", captures.TryGetValue(pick, out GhostCapture c) ? c.Traj.VesselName : "-");
                    followIndex = pick;
                    prevUT = double.NaN;
                }
            }
            lastResolveOk = TryResolveFollowed(ut, out Vector3d world, out CelestialBody body);
            if (!lastResolveOk)
            {
                winMiss++;
                HoldLastPoint();
                return;
            }
            var sw = Stopwatch.StartNew();
            Vector3d r = world - body.position;
            Vector3d vDiff = lastVDiff;
            if (prevBody == body && !double.IsNaN(prevUT) && ut > prevUT) vDiff = (r - prevR) / (ut - prevUT);
            prevR = r;
            prevUT = ut;
            prevBody = body;
            lastVDiff = vDiff;
            Vector3d rfrm = body.getRFrmVel(world);
            bool rotating = FlightGlobals.RefFrameIsRotating;
            Vector3d vObt = rotating ? vDiff + rfrm : vDiff;
            Vector3d vSrf = rotating ? vDiff : vDiff - rfrm;
            ApplyFloor(body, ref world, ref vObt, vSrf, r);
            WriteDrone(body, world, vObt, true);
            lastWriteBody = body;
            lastWriteLat = body.GetLatitude(world);
            lastWriteLon = body.GetLongitude(world);
            lastWriteAlt = body.GetAltitude(world);
            double ms = ElapsedMs(sw);
            winWrites++;
            winWriteMsSum += ms;
            if (ms > winWriteMsMax) winWriteMsMax = ms;
            double speedDiff = Math.Abs(drone.obt_velocity.magnitude - vObt.magnitude);
            if (speedDiff > winSpeedDiffMax) winSpeedDiffMax = speedDiff;
            if (lastDroneBody != body)
            {
                Obs("drone-body", "ut", Ut(), "from", lastDroneBody != null ? lastDroneBody.bodyName : "-",
                    "to", body.bodyName, "dominant", FlightGlobals.currentMainBody != null ? FlightGlobals.currentMainBody.bodyName : "-",
                    "events", EventsSinceMark());
                lastDroneBody = body;
            }
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
            double floor = Proof.TerrainFloorMeters(vUp, TimeWarp.CurrentRate * Time.fixedDeltaTime, g, FloorBaseMeters);
            double written = Proof.ClampAltitudeToFloor(alt, terrain, ocean, floor, out bool clamped);
            if (clamped)
            {
                winFloorClamps++;
                world = body.GetWorldSurfacePosition(lat, lon, written);
                if (vUp < 0.0) vObt -= up * vUp;
            }
            double ground = ocean ? Math.Max(0.0, terrain) : terrain;
            if (written - ground < winMinAboveTerrain) winMinAboveTerrain = written - ground;
            lastGhostBody = body.bodyName;
            lastTerrainUnderGhost = terrain;
            lastGhostAlt = alt;
        }

        private void WriteDrone(CelestialBody body, Vector3d world, Vector3d vObtWorld, bool shiftOrigin)
        {
            if (drone == null || drone.orbitDriver == null) return;
            double ut = Planetarium.GetUniversalTime();
            CelestialBody before = drone.orbit != null ? drone.orbit.referenceBody : null;
            drone.orbit.UpdateFromStateVectors((world - body.position).xzy, vObtWorld.xzy, body, ut);
            drone.orbitDriver.updateFromParameters();
            bool active = drone == FlightGlobals.ActiveVessel;
            if (active && shiftOrigin && drone.transform.position.magnitude > OriginShiftMeters)
            {
                FloatingOrigin.SetOffset(drone.transform.position);
                OrbitPhysicsManager.CheckReferenceFrame();
            }
            else if (active && before != body)
            {
                OrbitPhysicsManager.CheckReferenceFrame();
            }
            try { drone.UpdatePosVel(); }
            catch (Exception ex) { ParsekLog.VerboseRateLimited(Tag, "posvel", "UpdatePosVel threw " + ex.Message); }
        }

        // ----- MoveTo: a synthetic far point (circular equatorial orbit) -----

        private IEnumerator OpMoveTo(Proof.Args a)
        {
            if (drone == null || drone != FlightGlobals.ActiveVessel) { Finish("drone-not-active"); yield break; }
            CelestialBody body = FlightGlobals.GetBodyByName(a.Body ?? "Mun");
            if (body == null) { Finish("no-body"); yield break; }
            StopFollow("moveto");
            anchorOn = false;
            double alt = !double.IsNaN(a.Alt) ? a.Alt : (body.atmosphere ? body.atmosphereDepth + 20000.0 : 30000.0);
            double rad = body.Radius + alt;
            double vc = Math.Sqrt(body.gravParameter / rad);
            MarkEvents();
            double ut = Planetarium.GetUniversalTime();
            float start = Time.realtimeSinceStartup;
            drone.orbit.UpdateFromStateVectors(new Vector3d(rad, 0, 0), new Vector3d(0, vc, 0), body, ut);
            drone.orbitDriver.updateFromParameters();
            FloatingOrigin.SetOffset(drone.transform.position);
            OrbitPhysicsManager.CheckReferenceFrame();
            try { drone.UpdatePosVel(); } catch (Exception) { }
            double maxFrame = 0;
            double box = double.IsNaN(a.Seconds) ? 5.0 : a.Seconds;
            while (Time.realtimeSinceStartup - start < box)
            {
                maxFrame = Math.Max(maxFrame, Time.unscaledDeltaTime * 1000.0);
                yield return null;
            }
            bool pqs = body.pqsController != null && body.pqsController.isActive;
            Obs("moveto", "ut", Ut(), "body", body.bodyName, "alt", F(alt), "events", EventsSinceMark(),
                "dominant", FlightGlobals.currentMainBody != null ? FlightGlobals.currentMainBody.bodyName : "-",
                "droneBody", drone.mainBody != null ? drone.mainBody.bodyName : "-", "pqsActive", B(pqs),
                "maxFrameMs", F(maxFrame), "shipLoaded", ship != null ? B(ship.loaded) : "-",
                "sit", drone.situation.ToString(), "gee", F(drone.geeForce));
            lastDroneBody = body;
            Finish("ok", "body", body.bodyName, "events", EventsSinceMark());
        }

        // ----- Solver (proof 4: cost with the solver attached vs detached) -----

        private IEnumerator OpSolver(Proof.Args a)
        {
            if (drone == null) { Finish("no-drone"); yield break; }
            bool before = drone.patchedConicSolver != null;
            if (a.State == "detached") drone.DetachPatchedConicsSolver();
            else drone.AttachPatchedConicsSolver();
            yield return null;
            Obs("solver", "ut", Ut(), "state", a.State ?? "attached", "before", B(before),
                "after", B(drone.patchedConicSolver != null), "exceptions", N(exceptionLines));
            Finish("ok", "attached", B(drone.patchedConicSolver != null));
        }

        // ----- Unlist (proof 11) -----

        private IEnumerator OpUnlist(Proof.Args a)
        {
            if (drone == null) { Finish("no-drone"); yield break; }
            bool wasListed = FlightGlobals.Vessels.Contains(drone);
            int exc0 = exceptionLines;
            int shifts0 = winOriginShifts;
            FlightGlobals.RemoveVessel(drone);
            unlistActive = true;
            Obs("unlist", "phase", "removed", "ut", Ut(), "wasListed", B(wasListed),
                "droneToOrigin", F(drone.transform.position.magnitude));
            float start = Time.realtimeSinceStartup;
            double box = double.IsNaN(a.Seconds) ? 20.0 : a.Seconds;
            while (Time.realtimeSinceStartup - start < box && drone != null) yield return null;
            unlistActive = false;
            if (drone != null && !FlightGlobals.Vessels.Contains(drone)) FlightGlobals.AddVessel(drone);
            Obs("unlist", "phase", "restored", "ut", Ut(), "listed", B(drone != null && FlightGlobals.Vessels.Contains(drone)),
                "exceptions", N(exceptionLines - exc0), "shiftsInWindow", N(winOriginShifts - shifts0),
                "droneToOrigin", drone != null ? F(drone.transform.position.magnitude) : "-");
            Finish("ok", "exceptions", N(exceptionLines - exc0));
        }

        // ----- Advance: our own warp ladder with Esc pauses (proof 3) -----

        private IEnumerator OpAdvance(Proof.Args a)
        {
            if (TimeWarp.fetch == null) { Finish("no-timewarp"); yield break; }
            double startUT = Planetarium.GetUniversalTime();
            double until = a.UntilUT;
            float[] rates = TimeWarp.fetch.warpRates;
            int pausesDone = 0, setCalls = 0, dronePackedMiss = 0, shipUnpacked = 0;
            double maxRate = 1.0;
            string result = "ok";
            while (Planetarium.GetUniversalTime() < until - 0.5)
            {
                if (TimedOut(Proof.MaxOpSeconds)) { result = "timed-out"; break; }
                double ut = Planetarium.GetUniversalTime();
                if (pausesDone < a.Pauses && ut - startUT >= (until - startUT) * (pausesDone + 1) / (a.Pauses + 1))
                {
                    TimeWarp.SetRate(0, true, false);
                    PauseMenu.Display();
                    float p = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - p < 2f) yield return null;
                    PauseMenu.Close();
                    pausesDone++;
                    Obs("pause", "ut", Ut(), "n", N(pausesDone), "dronePacked", drone != null ? B(drone.packed) : "-",
                        "shipPacked", ship != null ? B(ship.packed) : "-");
                    continue;
                }
                int want = Proof.HighestWarpIndexBefore(rates, ut, until, a.MaxRate, 3.0);
                if (want != TimeWarp.CurrentRateIndex && Time.frameCount % 10 == 0)
                {
                    TimeWarp.SetRate(want, false, false);
                    setCalls++;
                }
                maxRate = Math.Max(maxRate, TimeWarp.CurrentRate);
                if (drone != null && !drone.packed) dronePackedMiss++;
                if (ship != null && ship.loaded && !ship.packed && drone == FlightGlobals.ActiveVessel) shipUnpacked++;
                yield return null;
            }
            TimeWarp.SetRate(0, true, false);
            Obs("advance", "startUT", F(startUT), "endUT", Ut(), "untilUT", F(until), "maxRateAsked", F(a.MaxRate),
                "maxRateSeen", F(maxRate), "pauses", N(pausesDone), "setCalls", N(setCalls),
                "droneUnpackedFrames", N(dronePackedMiss), "shipUnpackedFramesWhileDrone", N(shipUnpacked),
                "terrainKills", N(terrainKillLines), "pressureKills", N(pressureKillLines),
                "active", Role(FlightGlobals.ActiveVessel), "result", result);
            Finish(result, "endUT", Ut(), "maxRateSeen", F(maxRate));
        }

        // ----- Camera precision at the ghost (LateUpdate) -----

        private void MeasureCamera()
        {
            if (!following || followIndex < 0) { camSamples = 0; return; }
            Transform pivot = FollowedGhostPivot();
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (pivot == null || cam == null) { camSamples = 0; return; }
            Vector3d d = (Vector3d)pivot.position - (Vector3d)cam.transform.position;
            if (camSamples >= 2)
            {
                double jitter = (d - 2.0 * camD1 + camD2).magnitude;
                if (jitter > winJitterMax) winJitterMax = jitter;
            }
            camD2 = camD1;
            camD1 = d;
            camSamples++;
            lastGridStep = Math.Max(Proof.FloatGridStepMeters(pivot.position.magnitude),
                Proof.FloatGridStepMeters(cam.transform.position.magnitude));
            if (drone != null)
            {
                double dev = ((Vector3d)drone.transform.position - (Vector3d)pivot.position).magnitude;
                if (dev > winDevMax) winDevMax = dev;
            }
        }
    }
}
