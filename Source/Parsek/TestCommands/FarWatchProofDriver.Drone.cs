using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Proof = Parsek.TestCommands.TestCommandFarWatchProof;

namespace Parsek.TestCommands
{
    // The drone itself: build, inert, hold, the two switches with the camera re-apply, park,
    // destroy, the forced death (proof 14) and the proof-only Harmony patches.
    internal sealed partial class FarWatchProofDriver
    {
        internal const string DroneName = "Parsek Camera Drone";
        private const string ControlName = "Parsek Proof Control";
        private const string LockId = "ParsekFarWatchProof";
        private const double SpawnOffsetMeters = 50.0;
        private const double ParkOffsetMeters = 300.0;

        private Vessel drone;
        private Vessel ship;
        private Vessel control;
        private string dronePart;
        private int patchedCount;

        // Hold and surface anchor.
        private bool holdOn;
        private bool anchorOn;
        private CelestialBody anchorBody;
        private double anchorLat, anchorLon, anchorAlt;
        private int terrainReadyTicks;

        // The ship camera as the player left it (restored on hand-back).
        private float shipCamDist = float.NaN, shipCamPitch, shipCamHdg, shipCamSharp = 0.5f;
        internal static bool SwitchWindow;

        // ----- Build (proof 1) -----

        private IEnumerator OpBuild(Proof.Args a)
        {
            if (drone != null) { Finish("drone-exists"); yield break; }
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null || active.isEVA || active.mainBody == null) { Finish("no-ship"); yield break; }
            ship = active;
            ShipGuidForPatches = ship.id;
            CaptureShipCamera();
            string variant = string.IsNullOrEmpty(a.Variant) || a.Variant == "auto"
                ? (ship.LandedOrSplashed ? "surface" : "orbit") : a.Variant;
            CelestialBody body = ship.mainBody;
            Vector3d shipPos = ship.GetWorldPos3D();
            Vector3d up = (shipPos - body.position).normalized;
            Vector3d pos;
            Vector3d vel = ship.obt_velocity;
            if (variant == "surface")
            {
                pos = shipPos + up * SpawnOffsetMeters;
                SetAnchor(body, pos);
                // Stock's own velocity for a body-fixed point (Vessel.GoOnRails, landed case).
                vel = body.getRFrmVel(pos);
            }
            else
            {
                Vector3d normal = Vector3d.Cross(shipPos - body.position, ship.obt_velocity).normalized;
                pos = shipPos + normal * SpawnOffsetMeters;
                anchorOn = false;
            }
            double ut = Planetarium.GetUniversalTime();
            float realStart = Time.realtimeSinceStartup;
            Vessel v = LoadOneParter(DroneName, body, pos, vel, variant == "surface" ? "FLYING" : "ORBITING", true, out string why);
            if (v == null) { Finish("load-failed-" + why); yield break; }
            drone = v;
            holdOn = true;
            int frames = 0;
            while (!(v.loaded && frames >= 2))
            {
                if (v.loaded) frames++;
                if (Time.realtimeSinceStartup - realStart > 30f) { Finish("never-loaded"); yield break; }
                yield return null;
            }
            MakeDroneInert(v, "build");
            yield return null;
            LogBuild(v, variant, ut);
            Finish("ok", "variant", variant, "part", dronePart, "pid", U(v.persistentId),
                "distToShip", F((v.GetWorldPos3D() - ship.GetWorldPos3D()).magnitude));
        }

        private Vessel LoadOneParter(string name, CelestialBody body, Vector3d worldPos, Vector3d worldObtVel,
            string sit, bool isDrone, out string why)
        {
            why = null;
            double ut = Planetarium.GetUniversalTime();
            var orbit = new Orbit();
            orbit.UpdateFromStateVectors((worldPos - body.position).xzy, worldObtVel.xzy, body, ut);
            string part = PartLoader.getPartInfoByName("strutCube") != null ? "strutCube" : "sensorBarometer";
            ConfigNode partNode = ProtoVessel.CreatePartNode(part, 0);
            ConfigNode disc = ProtoVessel.CreateDiscoveryNode(DiscoveryLevels.None, UntrackedObjectClass.A,
                double.PositiveInfinity, double.PositiveInfinity);
            ConfigNode vn = ProtoVessel.CreateVesselNode(name, VesselType.Debris, orbit, 0, new[] { partNode }, disc);
            vn.SetValue("vesselSpawning", "False", true);
            vn.SetValue("prst", "True", true);
            vn.SetValue("cln", "False", true);
            vn.SetValue("sit", sit, true);
            vn.SetValue("landed", "False", true);
            vn.SetValue("splashed", "False", true);
            vn.SetValue("skipGroundPositioning", "True", true);
            GhostMapPresence.EnsureDefensiveVesselNodes(vn);
            var pv = new ProtoVessel(vn, HighLogic.CurrentGame);
            if (isDrone)
            {
                DronePidForPatches = pv.persistentId;
                dronePart = part;
            }
            HighLogic.CurrentGame.flightState.protoVessels.Add(pv);
            pv.Load(HighLogic.CurrentGame.flightState);
            if (pv.vesselRef == null) { why = "no-vesselRef"; return null; }
            return pv.vesselRef;
        }

        private void MakeDroneInert(Vessel v, string context)
        {
            if (v == null || v.parts == null) return;
            int colliders = 0, renderers = 0;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                foreach (Collider c in p.GetComponentsInChildren<Collider>(true)) { c.enabled = false; colliders++; }
                foreach (Renderer r in p.GetComponentsInChildren<Renderer>(true)) { r.enabled = false; renderers++; }
            }
            GhostMapPresence.HardenGhostVesselPartPhysics(v, "FarWatchProof." + context);
            ParsekLog.Info(Tag, "inert vessel=" + Name(v) + " context=" + context + " colliders=" + N(colliders)
                + " renderers=" + N(renderers));
        }

        private void LogBuild(Vessel v, string variant, double ut)
        {
            int liveColliders = 0, liveRenderers = 0;
            var modules = new List<string>();
            foreach (Part p in v.parts)
            {
                foreach (Collider c in p.GetComponentsInChildren<Collider>(true)) if (c.enabled) liveColliders++;
                foreach (Renderer r in p.GetComponentsInChildren<Renderer>(true)) if (r.enabled && r.isVisible) liveRenderers++;
                foreach (PartModule m in p.Modules) modules.Add(m.GetType().Name);
            }
            foreach (VesselModule vm in v.vesselModules) modules.Add("VM:" + vm.GetType().Name);
            Vector3d up = (v.GetWorldPos3D() - v.mainBody.position).normalized;
            bool rayHit = false;
            if (Physics.Raycast((Vector3)(v.GetWorldPos3D() + up * 5.0), (Vector3)(-up), out RaycastHit hit, 10f))
            {
                Part hp = hit.collider != null ? hit.collider.GetComponentInParent<Part>() : null;
                rayHit = hp != null && hp.vessel == v;
            }
            bool targetable = ProbeTargetable(v);
            OrbitRendererBase rend = v.orbitDriver != null ? v.orbitDriver.Renderer : null;
            Obs("build", "ut", F(ut), "variant", variant, "part", dronePart, "pid", U(v.persistentId),
                "type", v.vesselType.ToString(), "connectionNull", B(v.connection == null),
                "commNode", B(v.connection != null && v.connection.Comm != null),
                "controllable", B(v.IsControllable), "modules", string.Join("+", modules.ToArray()),
                "liveColliders", N(liveColliders), "visibleRenderers", N(liveRenderers), "rayHit", B(rayHit),
                "discovery", v.DiscoveryInfo != null ? v.DiscoveryInfo.Level.ToString() : "-",
                "mapObject", B(v.mapObject != null),
                "orbitRenderer", rend != null ? rend.drawMode.ToString() : "none",
                "inCycle", B(v.vesselType > VesselType.SpaceObject), "targetable", B(targetable),
                "loaded", B(v.loaded), "packed", B(v.packed), "sit", v.situation.ToString(),
                "distToShip", F((v.GetWorldPos3D() - ship.GetWorldPos3D()).magnitude),
                "geeForce", F(v.geeForce), "shipPacked", B(ship.packed));
        }

        private static bool ProbeTargetable(Vessel v)
        {
            if (FlightGlobals.fetch == null) return false;
            ITargetable before = FlightGlobals.fetch.VesselTarget;
            FlightGlobals.fetch.SetVesselTarget(v, true);
            bool took = FlightGlobals.fetch.VesselTarget != null && (object)FlightGlobals.fetch.VesselTarget == v;
            FlightGlobals.fetch.SetVesselTarget(before, true);
            return took;
        }

        private void SetAnchor(CelestialBody body, Vector3d worldPos)
        {
            anchorOn = true;
            anchorBody = body;
            anchorLat = body.GetLatitude(worldPos);
            anchorLon = body.GetLongitude(worldPos);
            anchorAlt = body.GetAltitude(worldPos);
        }

        // ----- Hold, anchor, terrain wait (proofs 3, 9, 15) -----

        private void TickHold()
        {
            if (drone == null) return;
            if (holdOn)
            {
                OrbitPhysicsManager.HoldVesselUnpack(2);
                winHoldRenewals++;
            }
            if (drone.packed) winPackedTicks++;
            if (ship != null && !ship.packed) winShipUnpackedTicks++;
            if (anchorOn && followIndex < 0 && anchorBody != null)
            {
                Vector3d pos = anchorBody.GetWorldSurfacePosition(anchorLat, anchorLon, anchorAlt);
                WriteDrone(anchorBody, pos, anchorBody.getRFrmVel(pos), false);
                double terrain = SafeTerrain(anchorBody, anchorLat, anchorLon);
                double above = anchorAlt - (anchorBody.ocean ? Math.Max(0.0, terrain) : terrain);
                if (above < winMinAboveTerrain) winMinAboveTerrain = above;
            }
            if (waitTerrainForShip && ship != null)
            {
                Vector3d sp = ship.GetWorldPos3D();
                Vector3d up = (sp - ship.mainBody.position).normalized;
                bool hit = Physics.Raycast((Vector3)(sp + up * 20.0), (Vector3)(-up), out RaycastHit h, 200f, 1 << 15);
                terrainReadyTicks = hit ? terrainReadyTicks + 1 : 0;
            }
        }

        private bool waitTerrainForShip;

        internal static double SafeTerrain(CelestialBody body, double lat, double lon)
        {
            if (body == null || body.pqsController == null) return 0.0;
            try { return body.TerrainAltitude(lat, lon, true); }
            catch (Exception) { return 0.0; }
        }

        // ----- Switch (proofs 2, 9, 12) -----

        private IEnumerator OpSwitch(Proof.Args a)
        {
            bool toDrone = a.Target == "drone";
            Vessel target = toDrone ? drone : ship;
            if (target == null) { Finish("no-target"); yield break; }
            if (!target.loaded) { Finish("target-not-loaded"); yield break; }
            if (target == FlightGlobals.ActiveVessel) { Finish("already-active"); yield break; }
            if (toDrone)
            {
                CaptureShipCamera();
                holdOn = true;
            }
            string pre = RecorderState();
            ParsekFlight pf0 = ParsekFlight.Instance;
            MarkEvents();
            int frame0 = Time.frameCount;
            int resets0 = ResetContractsCalls;
            SwitchWindow = true;
            if (!toDrone) waitTerrainForShip = ship.LandedOrSplashed;
            bool ok = FlightGlobals.ForceSetActiveVessel(target);
            yield return null;
            ApplyCameraAfterSwitch(toDrone);
            yield return null;
            Transform want = toDrone ? CameraTargetForDrone() : ship.transform;
            Transform pivot = FlightCamera.fetch != null ? FlightCamera.fetch.GetPivot() : null;
            double pivotDelta = pivot != null && want != null ? (pivot.position - want.position).magnitude : double.NaN;
            for (int i = 0; i < 3; i++) yield return null;
            SwitchWindow = false;
            string camTarget = FlightCamera.fetch != null && FlightCamera.fetch.Target != null ? FlightCamera.fetch.Target.name : "-";
            string mapTarget = PlanetariumCamera.fetch != null && PlanetariumCamera.fetch.target != null
                ? PlanetariumCamera.fetch.target.name : "-";
            Obs("switch", "to", toDrone ? "drone" : "ship", "ok", B(ok), "ut", Ut(), "frames", N(Time.frameCount - frame0),
                "active", Role(FlightGlobals.ActiveVessel), "events", EventsSinceMark(),
                "reload", B(ParsekFlight.Instance != pf0), "scene", HighLogic.LoadedScene.ToString(),
                "shipLoaded", B(ship.loaded), "shipPacked", B(ship.packed), "dronePacked", B(drone != null && drone.packed),
                "camTarget", camTarget, "pivotDeltaM", F(pivotDelta), "mapTarget", mapTarget,
                "camDist", FlightCamera.fetch != null ? F(FlightCamera.fetch.Distance) : "-",
                "resetContracts", N(ResetContractsCalls - resets0), "before", pre, "after", RecorderState());
            if (!toDrone) { yield return HandBackSettle(); }
            Finish(ok ? "ok" : "force-set-refused", "events", EventsSinceMark(), "pivotDeltaM", F(pivotDelta),
                "reload", B(ParsekFlight.Instance != pf0));
        }

        // The ship takes the focus back: the hold ends once the ground under a landed ship is
        // there (a downward terrain raycast for 10 ticks), then the ship's unpack is sampled.
        private IEnumerator HandBackSettle()
        {
            float start = Time.realtimeSinceStartup;
            while (waitTerrainForShip && terrainReadyTicks < 10 && Time.realtimeSinceStartup - start < 20f)
                yield return null;
            int readyTicks = terrainReadyTicks;
            waitTerrainForShip = false;
            holdOn = false;
            float unpackStart = Time.realtimeSinceStartup;
            while (ship != null && ship.packed && Time.realtimeSinceStartup - unpackStart < 30f)
                yield return null;
            double maxSrf = 0, maxVert = 0;
            float sampleStart = Time.realtimeSinceStartup;
            while (ship != null && Time.realtimeSinceStartup - sampleStart < 5f)
            {
                if (!ship.packed)
                {
                    maxSrf = Math.Max(maxSrf, ship.srf_velocity.magnitude);
                    maxVert = Math.Max(maxVert, Math.Abs(ship.verticalSpeed));
                }
                yield return null;
            }
            Obs("bounce", "ut", Ut(), "shipSit", ship != null ? ship.situation.ToString() : "-",
                "terrainReadyTicks", N(readyTicks), "unpackWaitS", F(sampleStart - unpackStart),
                "shipPacked", B(ship != null && ship.packed), "maxSrfSpeed", F(maxSrf), "maxVertSpeed", F(maxVert),
                "terrainKills", N(terrainKillLines), "dronePacked", B(drone != null && drone.packed));
        }

        private string RecorderState()
        {
            ParsekFlight pf = ParsekFlight.Instance;
            if (pf == null) return "noflight";
            FlightRecorder rec = pf.recorder;
            RecordingTree tree = pf.activeTree;
            string bg = "-";
            if (tree != null && ship != null && tree.BackgroundMap != null
                && tree.BackgroundMap.TryGetValue(ship.persistentId, out string id)) bg = id;
            string droneBg = "-";
            if (tree != null && drone != null && tree.BackgroundMap != null
                && tree.BackgroundMap.TryGetValue(drone.persistentId, out string did)) droneBg = did;
            return "rec:" + B(rec != null && rec.IsRecording) + "/bg:" + B(rec != null && rec.IsBackgrounded)
                + "/tree:" + (tree != null ? tree.Id : "-") + "/activeRec:" + (tree != null ? tree.ActiveRecordingId ?? "-" : "-")
                + "/tag:" + (ParsekFlight.GetActiveRecordingIdForTagging() ?? "-") + "/shipBg:" + bg + "/droneBg:" + droneBg;
        }

        private void CaptureShipCamera()
        {
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null || FlightGlobals.ActiveVessel != ship) return;
            shipCamDist = cam.Distance;
            shipCamPitch = cam.camPitch;
            shipCamHdg = cam.camHdg;
            shipCamSharp = cam.pivotTranslateSharpness;
        }

        private Transform CameraTargetForDrone()
        {
            Transform ghost = FollowedGhostPivot();
            return ghost != null ? ghost : (drone != null ? drone.transform : null);
        }

        private void ApplyCameraAfterSwitch(bool toDrone)
        {
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null) return;
            if (toDrone)
            {
                Transform t = CameraTargetForDrone();
                if (t != null) cam.SetTargetTransform(t);
                cam.pivotTranslateSharpness = 0f;
            }
            else
            {
                cam.SetTargetVessel(ship);
                cam.pivotTranslateSharpness = shipCamSharp;
            }
            if (!float.IsNaN(shipCamDist))
            {
                cam.SetDistance(shipCamDist);
                cam.camPitch = shipCamPitch;
                cam.camHdg = shipCamHdg;
            }
        }

        // ----- Park (hand-back staging) -----

        private IEnumerator OpPark(Proof.Args a)
        {
            if (drone == null || ship == null) { Finish("no-drone-or-ship"); yield break; }
            ParsekFlight pf = ParsekFlight.Instance;
            if (pf != null && pf.IsWatchingGhost) pf.ExitWatchMode(true);
            StopFollow("park");
            CelestialBody body = ship.mainBody;
            double ut = Planetarium.GetUniversalTime();
            Vector3d shipPos;
            if (ship.LandedOrSplashed)
            {
                shipPos = body.GetWorldSurfacePosition(ship.latitude, ship.longitude, ship.altitude);
                Vector3d up = (shipPos - body.position).normalized;
                SetAnchor(body, shipPos + up * ParkOffsetMeters);
            }
            else
            {
                anchorOn = false;
                shipPos = ship.orbit.getPositionAtUT(ut);
                Vector3d vel = ship.orbit.getOrbitalVelocityAtUT(ut).xzy;
                Vector3d normal = Vector3d.Cross(shipPos - body.position, vel).normalized;
                WriteDrone(body, shipPos + normal * ParkOffsetMeters, vel, true);
            }
            float start = Time.realtimeSinceStartup;
            int frames = 0;
            while (!(ship.loaded && frames >= 3))
            {
                if (ship.loaded) frames++;
                if (Time.realtimeSinceStartup - start > 60f) break;
                yield return null;
            }
            double dist = (drone.GetWorldPos3D() - ship.GetWorldPos3D()).magnitude;
            Obs("park", "ut", Ut(), "shipLoaded", B(ship.loaded), "shipPacked", B(ship.packed),
                "waitS", F(Time.realtimeSinceStartup - start), "distM", F(dist),
                "droneBody", drone.mainBody != null ? drone.mainBody.bodyName : "-",
                "shipBody", body.bodyName, "anchor", B(anchorOn), "terrainKills", N(terrainKillLines));
            Finish(ship.loaded ? "ok" : "ship-not-loaded", "distM", F(dist));
        }

        // ----- Destroy -----

        private IEnumerator OpDestroy(Proof.Args a)
        {
            if (drone == null) { Finish("no-drone"); yield break; }
            if (drone == FlightGlobals.ActiveVessel) { Finish("drone-active"); yield break; }
            Vessel d = drone;
            uint pid = d.persistentId;
            MarkEvents();
            ClearDroneMotion();
            drone = null;
            d.Die();
            for (int i = 0; i < 3; i++) yield return null;
            bool inProto = HighLogic.CurrentGame.flightState.protoVessels.Exists(p => p != null && p.persistentId == pid);
            Obs("destroy", "ut", Ut(), "pid", U(pid), "inVessels", B(FlightGlobals.Vessels.Contains(d)),
                "inProto", B(inProto), "events", EventsSinceMark(), "active", Role(FlightGlobals.ActiveVessel),
                "shipPacked", ship != null ? B(ship.packed) : "-", "after", RecorderState());
            Finish("ok", "inVessels", B(FlightGlobals.Vessels.Contains(d)));
        }

        private void ClearDroneMotion()
        {
            StopFollow("clear");
            anchorOn = false;
            holdOn = false;
        }

        // ----- Forced drone death while active (proof 14) -----

        private IEnumerator OpKill(Proof.Args a)
        {
            if (drone == null || drone != FlightGlobals.ActiveVessel) { Finish("drone-not-active"); yield break; }
            bool shipLoaded = ship != null && ship.loaded;
            DateTime persist0 = SaveMtime("persistent");
            Vessel d = drone;
            MarkEvents();
            ClearDroneMotion();
            drone = null;
            if (a.Mode == "die") d.Die();
            else
            {
                Part root = d.rootPart;
                GameEvents.onCrash.Fire(new EventReport(FlightEvents.CRASH, root, d.vesselName,
                    d.mainBody != null ? d.mainBody.displayName : "", 0, "", 0f));
                if (root != null) root.explode();
            }
            float start = Time.realtimeSinceStartup;
            bool dialogEver = false, dialogBeforeShip = false;
            string firstActive = null;
            while (Time.realtimeSinceStartup - start < (double.IsNaN(a.Seconds) ? 20.0 : a.Seconds))
            {
                Vessel act = FlightGlobals.ActiveVessel;
                if (firstActive == null && act != null && act != d) firstActive = Role(act);
                bool dialog = KSP.UI.Dialogs.FlightResultsDialog.isDisplaying;
                if (dialog) dialogEver = true;
                if (dialog && act != ship) dialogBeforeShip = true;
                yield return null;
            }
            Obs("kill", "mode", a.Mode ?? "crash", "ut", Ut(), "shipLoaded", B(shipLoaded),
                "firstActiveAfter", firstActive ?? "-", "activeNow", Role(FlightGlobals.ActiveVessel),
                "dialogShown", B(dialogEver), "dialogBeforeShipActive", B(dialogBeforeShip),
                "missionEnd", ReadMissionEnd(), "persistentWritten", B(SaveMtime("persistent") != persist0),
                "scene", HighLogic.LoadedScene.ToString(), "events", EventsSinceMark());
            Finish("ok", "firstActiveAfter", firstActive ?? "-", "dialogShown", B(dialogEver));
        }

        private static string ReadMissionEnd()
        {
            if (FlightLogger.fetch == null) return "no-logger";
            FieldInfo f = AccessTools.Field(typeof(FlightLogger), "missionEnd");
            return f != null ? B((bool)f.GetValue(FlightLogger.fetch)) : "no-field";
        }

        // ----- Proof-only Harmony patches (applied on arm, removed on destroy) -----

        private void InstallPatches()
        {
            harmony = new Harmony(HarmonyId);
            TryPatch(AccessTools.Method(typeof(Contracts.ContractSystem), "ResetContracts"), nameof(ResetContractsPrefix), null);
            TryPatch(AccessTools.Method(typeof(Game), "Updated", new[] { typeof(GameScenes) }), null, nameof(GameUpdatedPostfix));
            TryPatch(AccessTools.Method(typeof(Vessel), "GoOffRails"), nameof(GoOffRailsPrefix), null);
        }

        private void TryPatch(MethodBase original, string prefix, string postfix)
        {
            if (original == null)
            {
                ParsekLog.Warn(Tag, "patch target missing prefix=" + (prefix ?? "-") + " postfix=" + (postfix ?? "-"));
                return;
            }
            try
            {
                harmony.Patch(original,
                    prefix: prefix != null ? new HarmonyMethod(typeof(FarWatchProofDriver), prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(typeof(FarWatchProofDriver), postfix) : null);
                patchedCount++;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "patch failed target=" + original.Name + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool ResetContractsPrefix()
        {
            ResetContractsCalls++;
            Vessel a = FlightGlobals.ActiveVessel;
            bool droneActive = DronePidForPatches != 0u && a != null && a.persistentId == DronePidForPatches;
            bool fence = FenceOn && (droneActive || SwitchWindow);
            if (fence) ResetContractsFenced++;
            Obs("reset-contracts", "n", N(ResetContractsCalls), "ut", Ut(), "frame", N(Time.frameCount),
                "active", Name(a), "droneActive", B(droneActive), "switchWindow", B(SwitchWindow),
                "fenceOn", B(FenceOn), "fenced", B(fence));
            return !fence;
        }

        private static void GameUpdatedPostfix(Game __result)
        {
            if (!StripOn || DronePidForPatches == 0u || __result == null || __result.flightState == null) return;
            List<ProtoVessel> list = __result.flightState.protoVessels;
            int idxBefore = __result.flightState.activeVesselIdx;
            int removed = list.RemoveAll(p => p != null && p.persistentId == DronePidForPatches);
            if (removed == 0) return;
            int shipIdx = list.FindIndex(p => p != null && p.vesselID == ShipGuidForPatches);
            if (shipIdx >= 0) __result.flightState.activeVesselIdx = shipIdx;
            StripApplied++;
            Obs("strip", "ut", Ut(), "removed", N(removed), "idxBefore", N(idxBefore),
                "idxAfter", N(__result.flightState.activeVesselIdx), "shipFound", B(shipIdx >= 0));
        }

        private static bool GoOffRailsPrefix(Vessel __instance)
        {
            if (DronePidForPatches != 0u && __instance != null && __instance.persistentId == DronePidForPatches
                && __instance.packed)
            {
                GoOffRailsRefused++;
                return false;
            }
            return true;
        }
    }
}
