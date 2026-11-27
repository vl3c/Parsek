using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;
using Parsek.TestCommands;

namespace Parsek.InGameTests
{
    // THROWAWAY flight rig v2 (never merged; ghost-fx-9 picture re-shoot, 2026-11-27).
    // Ground cases north of the pad, each recorded by the real recorder and replayed as a ghost
    // while a viewer vessel flies past at about 80 m/s. The camera is fixed to the GROUND beside
    // the case (body-fixed position and aim), so an effect that stays where it was made stays in
    // frame and one that rides along with the flying viewer slides out of it. Every pass shoots a
    // burst: one frame every 0.25 s from 0.25 s before the event to 3 s after it.
    //   boom-stock  ghost crash explosion on its normal flight path (stock FXMonger.Explode).
    //   boom-built  the same, with FXMonger made unavailable for the ending so the ghost takes
    //               Parsek's own built explosion (the pause-menu / Space Center path #2837 fixed).
    //   puff        a decoupler firing on the ground (Parsek's part puff on the ghost).
    //   dust        a small rocket lifting off from the grass (Parsek's launch dust on the ghost).
    // "real" is the recorded flight itself (for the booms: the real craft exploded while a viewer
    // flies past), "before" puts back main's look (upright dust at the ghost root, hand-built
    // fallback explosion; rig v3 for PR #2856), "after" is the build as is.
    // Rig v4 (hand-built explosion restyle): the after pass hides the kept stock copies too, so
    // boom-built draws the hand-built explosion, and "fx9-tiers" then sets off, on the grass at
    // boom-built's site, each of stock's seven explosions (a kept silent copy, left) beside the
    // hand-built explosion of the same power (right), one tier at a time, under a fixed camera.
    [DefaultExecutionOrder(30000)]
    internal sealed class GhostFx9Rig : MonoBehaviour
    {
        private const string Tag = "GhostFx9";
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        private const double CaseSpacing = 24.0;
        private const double ViewerLead = 2.0;
        private const double ViewerSpeed = 400.0;
        // KSP's FloatingOrigin moves the origin when the active vessel is 500 m from it (logged as
        // foThreshold), so the viewer must cover several hundred metres while an effect lives.
        private const double ViewerNorth = 80.0, ViewerAgl = 60.0, ViewerUp = 24.0;
        private const double CaseNorthSpacing = 150.0;
        private const double CamSouth = 40.0, CamWest = 25.0, CamAgl = 8.0, AimAgl = 3.0;

        internal sealed class Case
        {
            public string Kind, Id, RecordingId;
            public double StartUt, EventUt, EndUt, Lat, Lon, Alt, Duration, EventAt;
            public double GroundAlt;
        }

        private static readonly List<Case> cases = new List<Case>();
        private static GhostFx9Rig live;

        private Case camCase;
        private bool before;
        private string pass;
        private readonly Dictionary<int, Vector3d> spawnLla = new Dictionary<int, Vector3d>();
        private readonly Dictionary<int, double> spawnUt = new Dictionary<int, double>();
        private CelestialBody body;

        internal static void OnMark(string label)
        {
            if (label == null || !label.StartsWith("fx9-", StringComparison.Ordinal)) return;
            if (live != null) Destroy(live.gameObject);
            var go = new GameObject("GhostFx9Rig");
            live = go.AddComponent<GhostFx9Rig>();
            GameEvents.onHideUI.Fire();
            ParsekLog.Info(Tag, "mark " + label);
            switch (label)
            {
                case "fx9-real": live.StartCoroutine(live.Real()); break;
                case "fx9-ghost-before": live.StartCoroutine(live.Ghosts("before", true)); break;
                case "fx9-ghost-after": live.StartCoroutine(live.Ghosts("after", false, true)); break;
                case "fx9-tiers": live.StartCoroutine(live.Tiers()); break;
            }
        }

        private void OnDestroy()
        {
            RestoreFxMonger();
            HideStockCopies(false);
            if (FlightCamera.fetch != null) FlightCamera.fetch.ActivateUpdate();
        }

        // ---- before mode: what the main build before #2837 did

        private readonly HashSet<int> legacyDust = new HashSet<int>();

        // Runs after the engine placed the dust and before the particle update emits, so the
        // frame emits from main's pose: the ghost root, identity rotation, main's sheet shape.
        private void LateUpdate()
        {
            if (before)
                foreach (ParticleSystem ps in FindObjectsOfType<ParticleSystem>())
                {
                    if (ps == null || ps.gameObject.name != "ParsekLaunchDust") continue;
                    Transform t = ps.transform;
                    t.localPosition = Vector3.zero;
                    t.localRotation = Quaternion.identity;
                    if (legacyDust.Add(ps.GetInstanceID()))
                    {
                        var main = ps.main;
                        main.startLifetime = 2.5f;
                        main.startSpeed = 6f;
                        var sol = ps.sizeOverLifetime;
                        sol.size = new ParticleSystem.MinMaxCurve(1f,
                            new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(1f, 2.2f)));
                        ParsekLog.Info(Tag, pass + " dust put back to main's look id=" + ps.GetInstanceID().ToString(Ic));
                    }
                }
            TrackFreeFx();
            Pose();
        }

        // Main has no stock copies: hide them for the before pass so the fallback draws the
        // hand-built explosion, and give them back after it.
        private static FieldInfo copyHolder, copyArray;
        private static object heldHolder, heldCopies;

        private static void HideStockCopies(bool hide)
        {
            if (copyHolder == null)
            {
                Type t = typeof(StockExplosionPrefabCache);
                copyHolder = t.GetField("holder", BindingFlags.Static | BindingFlags.NonPublic);
                copyArray = t.GetField("copies", BindingFlags.Static | BindingFlags.NonPublic);
            }
            if (copyHolder == null || copyArray == null) { ParsekLog.Warn(Tag, "stock copy fields not found"); return; }
            if (hide)
            {
                heldHolder = copyHolder.GetValue(null);
                heldCopies = copyArray.GetValue(null);
                copyHolder.SetValue(null, null);
                copyArray.SetValue(null, null);
            }
            else if (heldHolder != null)
            {
                copyHolder.SetValue(null, heldHolder);
                copyArray.SetValue(null, heldCopies);
                heldHolder = null;
                heldCopies = null;
            }
            ParsekLog.Info(Tag, "stock copies " + (hide ? "hidden" : "restored") +
                " has=" + StockExplosionPrefabCache.HasCopies);
        }

        private void TrackFreeFx()
        {
            if (body == null) return;
            foreach (ParticleSystem ps in FindObjectsOfType<ParticleSystem>())
            {
                GameObject root = ps.transform.root.gameObject;
                if (root.name != "GhostPartPuffFx" && root.name != "GhostExplosionFx"
                    && root.name != "GhostStockExplosionFx") continue;
                int id = root.GetInstanceID();
                if (spawnLla.ContainsKey(id)) continue;
                Vector3d p = root.transform.position;
                spawnLla[id] = new Vector3d(body.GetLatitude(p), body.GetLongitude(p), body.GetAltitude(p));
                spawnUt[id] = Planetarium.GetUniversalTime();
                ParsekLog.Info(Tag, pass + " free fx spawned " + root.name + " id=" + id.ToString(Ic) +
                    " ut=" + spawnUt[id].ToString("F2", Ic) + " alt=" + spawnLla[id].z.ToString("F1", Ic));
            }
        }

        // ---- the ground camera: south-west of the case, 12 m up, aimed 6 m above its base

        private void OnEnable() { Camera.onPreCull += PreCull; GameEvents.onFloatingOriginShift.Add(OnOriginShift); }
        private void OnDisable() { Camera.onPreCull -= PreCull; GameEvents.onFloatingOriginShift.Remove(OnOriginShift); }
        private void PreCull(Camera c)
        {
            if (FlightCamera.fetch != null && c == FlightCamera.fetch.mainCamera) Pose();
        }

        private void Pose()
        {
            if (tierCam) { PoseTiers(); return; }
            if (camCase == null || FlightCamera.fetch == null || body == null) return;
            Case c = camCase;
            Vector3d target = body.GetWorldSurfacePosition(c.Lat, c.Lon, c.GroundAlt + AimAgl);
            double camLat = c.Lat - DegLat(CamSouth), camLon = c.Lon - DegLon(CamWest, c.Lat);
            double camGround = body.TerrainAltitude(camLat, camLon);
            Vector3d pos = body.GetWorldSurfacePosition(camLat, camLon, camGround + CamAgl);
            Vector3 up = (Vector3)(pos - body.position).normalized;
            FlightCamera.fetch.GetCameraTransform().SetPositionAndRotation((Vector3)pos, Quaternion.LookRotation((Vector3)(target - pos), up));
        }

        private double DegLat(double meters) => meters / body.Radius * 180.0 / Math.PI;
        private double DegLon(double meters, double lat) => DegLat(meters) / Math.Max(0.05, Math.Cos(lat * Math.PI / 180.0));

        private void OnOriginShift(Vector3d offset, Vector3d nonKrakensbane)
        {
            ParsekLog.Info(Tag, (pass ?? "-") + " origin shift ut=" + Planetarium.GetUniversalTime().ToString("F2", Ic) +
                " offset=" + offset.magnitude.ToString("F1", Ic));
        }

        private static IEnumerator WaitUt(double ut)
        {
            while (Planetarium.GetUniversalTime() < ut) yield return null;
        }

        private static void Shoot(string label)
        {
            string dir = Path.Combine(KSPUtil.ApplicationRootPath, TestCommandCaptureScreenshot.ScreenshotsDirName);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, label + ".png"), 1);
        }

        // Frames k = -1 .. 12 at eventUt + 0.25 k. Measures Parsek's own free FX and the dust.
        private IEnumerator Burst(Case c, double eventUt)
        {
            for (int k = -1; k <= 12; k++)
            {
                yield return WaitUt(eventUt + 0.25 * k);
                string label = "fx9g-" + c.Id + "-" + pass + "-k" + (k + 1).ToString("D2", Ic);
                Pose();
                Shoot(label);
                Vessel a = FlightGlobals.ActiveVessel;
                ParsekLog.Info(Tag, "shot " + label + " ut=" + Planetarium.GetUniversalTime().ToString("F2", Ic) +
                    " t=" + (Planetarium.GetUniversalTime() - eventUt).ToString("F2", Ic) +
                    " active=" + (a != null ? a.vesselName : "-") +
                    " activeSrf=" + (a != null ? a.srfSpeed.ToString("F0", Ic) : "-") +
                    " frameVel=" + Krakensbane.GetFrameVelocity().magnitude.ToString("F1", Ic) +
                    " originOffset=" + (FloatingOrigin.fetch != null ? FloatingOrigin.fetch.offset.magnitude.ToString("F1", Ic) : "-") +
                    (k == -1 && FloatingOrigin.fetch != null ? " foThreshold=" + FloatingOrigin.fetch.threshold.ToString("F1", Ic) +
                        " foVelContinuous=" + FloatingOrigin.fetch.velForContinuous.ToString("F1", Ic) +
                        " foContinuous=" + FloatingOrigin.fetch.continuous : ""));
                MeasureFree(label);
                if (c.Kind == "dust") MeasureDust(label, c);
                yield return null;
            }
        }

        // ---- authoring

        private static ConfigNode Author(string kind, string name, Vessel anchor, out double depth)
        {
            depth = 1.0;
            string[] stack;
            switch (kind)
            {
                case "puff": stack = new[] { "probeCoreOcto.v2", "fuelTankSmallFlat", "Decoupler.1", "fuelTank" }; break;
                case "dust": stack = new[] { "probeCoreOcto.v2", "fuelTank", "liquidEngine2.v2" }; break;
                case "boom": stack = new[] { "probeCoreOcto.v2", "fuelTank", "fuelTank" }; break;
                default: stack = new[] { "probeCoreOcto.v2", "fuelTankSmallFlat" }; break;
            }
            var infos = new List<AvailablePart>();
            foreach (string s in stack)
            {
                AvailablePart a = PartLoader.getPartInfoByName(s);
                if (a == null) { ParsekLog.Info(Tag, "author " + name + ": missing part " + s); return null; }
                infos.Add(a);
            }
            Func<AvailablePart, string, Vector3> node = (a, id) => a.partPrefab.FindAttachNode(id) != null ? a.partPrefab.FindAttachNode(id).position : Vector3.zero;
            var ids = GhostCoOrbitalRig.UniqueFlightIds(infos.Count);
            var parts = new List<ConfigNode>();
            Vector3 pos = Vector3.zero;
            for (int i = 0; i < infos.Count; i++)
            {
                if (i > 0) pos += node(infos[i - 1], "bottom") - node(infos[i], "top");
                var att = new Dictionary<string, int>();
                if (i > 0) att["top"] = i - 1;
                if (i < infos.Count - 1) att["bottom"] = i + 1;
                parts.Add(GhostCoOrbitalRig.AuthorPartNode(infos[i], ids[i], i == 0 ? 0 : i - 1, pos, att, null, null));
                AttachNode bn = infos[i].partPrefab.FindAttachNode("bottom");
                double low = -(pos.y + (bn != null ? bn.position.y : -1.0));
                if (low > depth) depth = low;
            }
            Orbit o = anchor.orbit;
            return ProtoVessel.CreateVesselNode(name, VesselType.Ship,
                new Orbit(o.inclination, o.eccentricity, o.semiMajorAxis, o.LAN, o.argumentOfPeriapsis, o.meanAnomalyAtEpoch, o.epoch, o.referenceBody),
                0, parts.ToArray());
        }

        // groundClear > 0: alt is ignored and the root is put so the stack's lowest node is that
        // high above the terrain (a root at a fixed height buried the lower parts in the ground).
        private static uint Spawn(string kind, string name, Vessel anchor, double lat, double lon, double alt, double eastSpeed, double upSpeed,
            double groundClear = 0.0)
        {
            double depth;
            ConfigNode node = Author(kind, name, anchor, out depth);
            if (node == null) return 0u;
            if (groundClear > 0.0) alt = anchor.mainBody.TerrainAltitude(lat, lon) + depth + groundClear;
            CelestialBody b = anchor.mainBody;
            Vector3d worldPos = b.GetWorldSurfacePosition(lat, lon, alt);
            Vector3d up = (worldPos - b.position).normalized;
            Vector3d east = Vector3d.Exclude(up, b.GetWorldSurfacePosition(lat, lon + 0.001, alt) - worldPos).normalized;
            Quaternion worldRot = GhostCoOrbitalRig.UprightRotation(b, lat, lon, alt, out _);
            Quaternion srfRel = Quaternion.Inverse(b.bodyTransform.rotation) * worldRot;
            Vector3d surfaceVel = east * eastSpeed + up * upSpeed;
            Vector3d orbitalVel = surfaceVel + b.getRFrmVel(worldPos);
            uint pid = VesselSpawner.SpawnAtPosition(node, b, lat, lon, alt, orbitalVel, Planetarium.GetUniversalTime(),
                surfaceRelativeRotation: srfRel);
            Vessel sv = pid != 0u ? FlightRecorder.FindVesselByPid(pid) : null;
            if (sv != null && sv.vesselRanges != null)
            {
                foreach (VesselRanges.Situation s in new[] { sv.vesselRanges.flying, sv.vesselRanges.subOrbital, sv.vesselRanges.landed, sv.vesselRanges.prelaunch })
                {
                    s.load = 30000f; s.unload = 32000f; s.pack = 31000f; s.unpack = 29000f;
                }
            }
            ParsekLog.Info(Tag, "spawn " + name + " pid=" + pid.ToString(Ic) + " lat=" + lat.ToString("F5", Ic) +
                " lon=" + lon.ToString("F5", Ic) + " alt=" + alt.ToString("F1", Ic) + " east=" + eastSpeed.ToString("F0", Ic) +
                " up=" + upSpeed.ToString("F1", Ic) + " depth=" + depth.ToString("F2", Ic) + " rangesExtended=" + (sv != null) +
                " parts=" + (sv != null ? sv.parts.Count.ToString(Ic) : "-"));
            return pid;
        }

        // A viewer that passes the case abreast (ViewerNorth m north of it, ViewerAgl m up) at the event.
        private IEnumerator SpawnViewer(Case c, double eventUt, Vessel anchor, Action<Vessel> got)
        {
            double now = Planetarium.GetUniversalTime();
            double lead = Math.Max(0.5, eventUt - now);
            double vLat = c.Lat + DegLat(ViewerNorth);
            double vLon = c.Lon - DegLon(ViewerSpeed * lead, c.Lat);
            double vAlt = Math.Max(body.TerrainAltitude(vLat, vLon), c.GroundAlt) + ViewerAgl;
            // Rises and falls back to about its start height over the 5 s it is watched.
            uint pid = Spawn("viewer", "Viewer " + c.Id, anchor, vLat, vLon, vAlt, ViewerSpeed, ViewerUp);
            Vessel v = null;
            yield return WaitLoaded(pid, x => v = x);
            if (v != null)
            {
                FlightGlobals.ForceSetActiveVessel(v);
                yield return WaitUnpacked(v);
                v.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            }
            ParsekLog.Info(Tag, "viewer " + c.Id + " pid=" + pid.ToString(Ic) + " loaded=" + (v != null) +
                " lead=" + lead.ToString("F2", Ic));
            got(v);
        }

        private IEnumerator WaitLoaded(uint pid, Action<Vessel> found)
        {
            float end = Time.realtimeSinceStartup + 20f;
            while (pid != 0u && Time.realtimeSinceStartup < end)
            {
                Vessel v = FlightRecorder.FindVesselByPid(pid);
                if (v != null && v.loaded && v.parts.Count > 0) { found(v); yield break; }
                yield return null;
            }
            found(null);
        }

        private IEnumerator WaitUnpacked(Vessel v)
        {
            float end = Time.realtimeSinceStartup + 10f;
            while (v != null && v.packed && Time.realtimeSinceStartup < end) yield return null;
        }

        private static T Module<T>(Vessel v) where T : PartModule
        {
            foreach (Part p in v.parts) { T m = p.FindModuleImplementing<T>(); if (m != null) return m; }
            return null;
        }

        private List<Case> Plan(Vessel host)
        {
            double lat = host.latitude, lon = host.longitude;
            var list = new List<Case>();
            Func<string, string, double, double, Case> mk = (kind, id, north, dur) =>
            {
                var c = new Case { Kind = kind, Id = id, Lat = lat + DegLat(north), Lon = lon, Duration = dur };
                c.GroundAlt = body.TerrainAltitude(c.Lat, c.Lon);
                return c;
            };
            Case lead = mk("lead", "lead", 0.0, 1.0); list.Add(lead);
            Case b1 = mk("boom", "boom-stock", CaseNorthSpacing * 1, 4.0); list.Add(b1);
            Case b2 = mk("boom", "boom-built", CaseNorthSpacing * 2, 4.0); list.Add(b2);
            Case pf = mk("puff", "puff", CaseNorthSpacing * 3, 6.5); pf.EventAt = 2.5; list.Add(pf);
            Case du = mk("dust", "dust", CaseNorthSpacing * 4, 6.0); du.EventAt = 2.5; list.Add(du);
            return list;
        }

        // ---- real pass

        private IEnumerator Real()
        {
            pass = "real";
            cases.Clear();
            Vessel host = FlightGlobals.ActiveVessel;
            body = host.mainBody;
            ParsekFlight flight = ParsekFlight.Instance;
            List<Case> plan = Plan(host);
            // Rewind-to-Launch strips the pad craft; an anchor spawned before the lead recording
            // (so it is in the rewind save) gives the ghost passes a FLIGHT scene to start from.
            double aLat = host.latitude + DegLat(60.0), aLon = host.longitude;
            uint anchorPid = Spawn("anchor", "Fx9 anchor", host, aLat, aLon, 0, 0, 0, 0.5);
            Vessel anchorVessel = null;
            yield return WaitLoaded(anchorPid, x => anchorVessel = x);
            ParsekLog.Info(Tag, "anchor pid=" + anchorPid.ToString(Ic) + " loaded=" + (anchorVessel != null));
            double baseUt = Planetarium.GetUniversalTime() + 2.0;
            Vessel previous = host;
            for (int i = 0; i < plan.Count; i++)
            {
                Case c = plan[i];
                Vessel v;
                if (c.Kind == "lead")
                {
                    yield return WaitUt(baseUt);
                    v = host;
                }
                else
                {
                    // Spawned 4 s before its slot so it has settled on the grass when recording starts.
                    yield return WaitUt(baseUt + CaseSpacing * i - 4.0);
                    uint pid = Spawn(c.Kind, "Fx9 " + c.Id, previous ?? host, c.Lat, c.Lon, 0, 0, 0, 0.5);
                    v = null;
                    yield return WaitLoaded(pid, x => v = x);
                    if (v == null) { ParsekLog.Info(Tag, "case " + c.Id + ": never loaded"); continue; }
                    FlightGlobals.ForceSetActiveVessel(v);
                    yield return WaitUnpacked(v);
                    v.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
                    yield return WaitUt(baseUt + CaseSpacing * i);
                    if (previous != null && previous != v && previous != host && previous != anchorVessel) { previous.Die(); yield return null; }
                }
                flight.StartRecording();
                c.StartUt = Planetarium.GetUniversalTime();
                ParsekLog.Info(Tag, "case " + c.Id + " recording=" + flight.HasActiveTree + " active=" +
                    (FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.vesselName + " parts=" + FlightGlobals.ActiveVessel.parts.Count.ToString(Ic) : "-"));
                if (c.Kind == "puff" || c.Kind == "dust")
                {
                    camCase = c;
                    FlightCamera.fetch.DeactivateUpdate();
                    c.EventUt = c.StartUt + c.EventAt;
                    StartCoroutine(Burst(c, c.EventUt));
                    yield return WaitUt(c.EventUt);
                    if (c.Kind == "puff")
                    {
                        ModuleDecouple d = Module<ModuleDecouple>(v);
                        if (d != null) d.Decouple();
                    }
                    else
                    {
                        ModuleEngines e = Module<ModuleEngines>(v);
                        if (e != null) e.Activate();
                        v.ctrlState.mainThrottle = 1f;
                        FlightInputHandler.state.mainThrottle = 1f;
                    }
                    ParsekLog.Info(Tag, "real event " + c.Id + " ut=" + Planetarium.GetUniversalTime().ToString("F2", Ic));
                }
                yield return WaitUt(c.StartUt + c.Duration);
                string treeId = flight.ActiveTreeForDisplay != null ? flight.ActiveTreeForDisplay.Id : null;
                flight.StopRecording();
                flight.CommitTreeFlight();
                flight.StopRecording();
                if (flight.HasActiveTree)
                    flight.AutoDiscardActiveTreeWithMessage("fx9-rig", "fx9 rig", "fx9-rig", false);
                flight.SuppressCommittedSpawnedRestoreForActiveVessel("fx9-rig");
                foreach (RecordingTree t in RecordingStore.CommittedTrees)
                {
                    if (t.Id != treeId) continue;
                    foreach (Recording r in t.Recordings.Values)
                    {
                        if (c.RecordingId == null) { c.RecordingId = r.RecordingId; c.EndUt = r.EndUT; }
                        // Only the booms end in a crash; the rest just end (no end spawn either).
                        r.TerminalStateValue = c.Kind == "boom" ? TerminalState.Destroyed : TerminalState.Recovered;
                        r.VesselSnapshot = null;
                    }
                }
                ParsekLog.Info(Tag, "case " + c.Id + " committed tree=" + treeId + " rec=" + c.RecordingId +
                    " start=" + c.StartUt.ToString("F2", Ic) + " end=" + c.EndUt.ToString("F2", Ic) +
                    " event=" + c.EventUt.ToString("F2", Ic));
                if (c.Kind == "boom")
                {
                    // The real reference: the recorded craft itself explodes (stock) while a viewer
                    // flies past, 2 s after the recording ended.
                    camCase = c;
                    FlightCamera.fetch.DeactivateUpdate();
                    double boomUt = Planetarium.GetUniversalTime() + ViewerLead;
                    Vessel viewer = null;
                    yield return SpawnViewer(c, boomUt, anchorVessel ?? host, x => viewer = x);
                    StartCoroutine(Burst(c, boomUt));
                    yield return WaitUt(boomUt);
                    if (v != null && v.parts != null)
                        foreach (Part p in v.parts.ToArray()) if (p != null) p.explode();
                    ParsekLog.Info(Tag, "real event " + c.Id + " exploded ut=" + Planetarium.GetUniversalTime().ToString("F2", Ic));
                    yield return WaitUt(boomUt + 3.2);
                    previous = viewer;
                }
                else
                {
                    if (c.Kind != "lead") yield return WaitUt(c.EventUt + 3.2);
                    previous = v;
                }
                CommitStrayPendingTree(c.Id);
                cases.Add(c);
                camCase = null;
                FlightCamera.fetch.ActivateUpdate();
            }
            ParsekLog.Info(Tag, "REAL DONE cases=" + cases.Count.ToString(Ic) + " ut=" + Planetarium.GetUniversalTime().ToString("F1", Ic));
        }

        // A destroyed recorded vessel leaves a pending tree, which refuses every later rewind.
        private static void CommitStrayPendingTree(string caseId)
        {
            RecordingTree pending = PendingTreeSlot.PendingTree;
            if (pending == null) return;
            bool ok = TreeCommitService.CommitPendingTree();
            ParsekLog.Info(Tag, "case " + caseId + " stray pending tree '" + pending.TreeName + "' committed=" + ok);
        }

        // ---- ghost passes

        private static GhostPlaybackState FindGhost(string recId)
        {
            if (ParsekFlight.Instance == null) return null;
            foreach (var kv in ParsekFlight.Instance.ghostStates)
                if (kv.Value != null && kv.Value.recordingId == recId && kv.Value.ghost != null) return kv.Value;
            return null;
        }

        private IEnumerator Ghosts(string name, bool strip, bool hideCopies = false)
        {
            pass = name;
            before = strip;
            ParsekLog.Info(Tag, "ghost " + name + " stock copies kept=" + StockExplosionPrefabCache.HasCopies);
            if (strip || hideCopies) HideStockCopies(true);
            Vessel previous = FlightGlobals.ActiveVessel;
            body = previous.mainBody;
            foreach (Case c in cases)
            {
                if (c.Kind == "lead") continue;
                double eventUt = c.Kind == "boom" ? c.EndUt : c.EventUt;
                if (Planetarium.GetUniversalTime() > eventUt - ViewerLead - 0.5)
                {
                    ParsekLog.Info(Tag, "ghost " + pass + " " + c.Id + ": late ut=" + Planetarium.GetUniversalTime().ToString("F1", Ic));
                    continue;
                }
                yield return WaitUt(eventUt - ViewerLead - 0.3);
                Vessel viewer = null;
                yield return SpawnViewer(c, eventUt, previous, x => viewer = x);
                if (viewer != null)
                {
                    if (previous != null && previous != viewer) previous.Die();
                    previous = viewer;
                }
                camCase = c;
                FlightCamera.fetch.DeactivateUpdate();
                GhostPlaybackState g0 = FindGhost(c.RecordingId);
                ParsekLog.Info(Tag, "ghost " + pass + " " + c.Id + " present=" + (g0 != null) +
                    (g0 != null ? " active=" + g0.ghost.activeInHierarchy : ""));
                if (c.Id == "boom-built") StartCoroutine(HideFxMonger(eventUt));
                yield return Burst(c, eventUt);
                camCase = null;
                FlightCamera.fetch.ActivateUpdate();
                yield return null;
            }
            before = false;
            if (strip || hideCopies) HideStockCopies(false);
            ParsekLog.Info(Tag, "GHOST DONE " + pass + " ut=" + Planetarium.GetUniversalTime().ToString("F1", Ic));
        }

        // FXMonger.fetch is null for 0.6 s around the ghost's end, so the engine's crash takes
        // the built explosion, the path it takes with the pause menu open.
        private static FieldInfo fxFetch;
        private static FXMonger heldFx;

        private IEnumerator HideFxMonger(double endUt)
        {
            yield return WaitUt(endUt - 0.3);
            if (fxFetch == null)
                fxFetch = typeof(FXMonger).GetField("fetch", BindingFlags.Static | BindingFlags.NonPublic);
            if (fxFetch != null)
            {
                heldFx = fxFetch.GetValue(null) as FXMonger;
                fxFetch.SetValue(null, null);
                ParsekLog.Info(Tag, "fxmonger hidden ut=" + Planetarium.GetUniversalTime().ToString("F2", Ic) + " held=" + (heldFx != null));
            }
            yield return WaitUt(endUt + 0.3);
            RestoreFxMonger();
        }

        private static void RestoreFxMonger()
        {
            if (fxFetch != null && heldFx != null)
            {
                fxFetch.SetValue(null, heldFx);
                heldFx = null;
                ParsekLog.Info(Tag, "fxmonger restored ut=" + Planetarium.GetUniversalTime().ToString("F2", Ic));
            }
        }


        // ---- tier gallery: each stock explosion (kept copy, left) beside the hand-built one (right)

        private const double TierHalfSpan = 16.0, TierCamSouth = 55.0, TierCamAgl = 6.0, TierAimAgl = 5.0, TierBlastAgl = 4.0;
        private bool tierCam;
        private Case tierSite;

        private void PoseTiers()
        {
            if (tierSite == null || FlightCamera.fetch == null || body == null) return;
            Case c = tierSite;
            Vector3d target = body.GetWorldSurfacePosition(c.Lat, c.Lon, c.GroundAlt + TierAimAgl);
            double camLat = c.Lat - DegLat(TierCamSouth);
            double camGround = body.TerrainAltitude(camLat, c.Lon);
            Vector3d pos = body.GetWorldSurfacePosition(camLat, c.Lon, camGround + TierCamAgl);
            Vector3 up = (Vector3)(pos - body.position).normalized;
            FlightCamera.fetch.GetCameraTransform().SetPositionAndRotation((Vector3)pos, Quaternion.LookRotation((Vector3)(target - pos), up));
        }

        private IEnumerator Tiers()
        {
            pass = "tiers";
            Vessel active = FlightGlobals.ActiveVessel;
            body = active != null ? active.mainBody : FlightGlobals.GetHomeBody();
            // The last viewer may have crashed: seed the anchor from any vessel still there
            // (the real pass's "Fx9 anchor" is in the rewind save).
            Vessel seed = active;
            if (seed == null)
                foreach (Vessel v in FlightGlobals.Vessels)
                    if (v != null && v.mainBody == body && v.orbit != null) { seed = v; break; }
            Case site = null;
            foreach (Case c in cases) if (c.Id == "boom-built") site = c;
            if (site == null)
            {
                site = new Case { Kind = "tiers", Id = "tiers", Lat = seed.latitude, Lon = seed.longitude };
                site.GroundAlt = body.TerrainAltitude(site.Lat, site.Lon);
            }
            tierSite = new Case { Kind = "tiers", Id = "tiers", Lat = site.Lat, Lon = site.Lon, GroundAlt = body.TerrainAltitude(site.Lat, site.Lon) };
            // A still anchor behind the camera, so the scene around the site stays loaded.
            uint pid = Spawn("anchor", "Fx9 tier anchor", seed, tierSite.Lat - DegLat(TierCamSouth + 40.0), tierSite.Lon, 0, 0, 0, 0.5);
            Vessel anchor = null;
            yield return WaitLoaded(pid, x => anchor = x);
            if (anchor != null)
            {
                FlightGlobals.ForceSetActiveVessel(anchor);
                yield return WaitUnpacked(anchor);
                if (active != null && active != anchor && active.vesselName.StartsWith("Viewer", StringComparison.Ordinal)) active.Die();
            }
            ParsekLog.Info(Tag, "tiers site lat=" + tierSite.Lat.ToString("F5", Ic) + " lon=" + tierSite.Lon.ToString("F5", Ic) +
                " ground=" + tierSite.GroundAlt.ToString("F1", Ic) + " anchor=" + (anchor != null) +
                " copies=" + StockExplosionPrefabCache.HasCopies);
            tierCam = true;
            FlightCamera.fetch.DeactivateUpdate();
            double lonStep = DegLon(TierHalfSpan, tierSite.Lat);
            for (int tier = 0; tier < FallbackExplosionLook.StockPrefabCount; tier++)
            {
                double power = Math.Min(1.0, (tier + 0.5) / (FallbackExplosionLook.StockPrefabCount - 1));
                double eventUt = Planetarium.GetUniversalTime() + 1.0;
                StartCoroutine(TierBurst(tier, eventUt));
                yield return WaitUt(eventUt);
                Vector3 left = (Vector3)body.GetWorldSurfacePosition(tierSite.Lat, tierSite.Lon - lonStep,
                    body.TerrainAltitude(tierSite.Lat, tierSite.Lon - lonStep) + TierBlastAgl);
                Vector3 right = (Vector3)body.GetWorldSurfacePosition(tierSite.Lat, tierSite.Lon + lonStep,
                    body.TerrainAltitude(tierSite.Lat, tierSite.Lon + lonStep) + TierBlastAgl);
                string why;
                GameObject stock = StockExplosionPrefabCache.TrySpawn(left, power, out why);
                GameObject built = GhostVisualBuilder.SpawnExplosionFx(right, (float)(power * GhostAudioPresets.VesselLengthPowerDivisor), power);
                ParsekLog.Info(Tag, "tier " + tier.ToString(Ic) + " power=" + power.ToString("F3", Ic) +
                    " stock=" + (stock != null ? stock.name : "none (" + why + ")") +
                    " built=" + (built != null ? built.name : "none") +
                    " look=" + FallbackExplosionLook.Resolve(power).Describe() +
                    " ut=" + Planetarium.GetUniversalTime().ToString("F2", Ic));
                yield return WaitUt(eventUt + 4.6);
            }
            tierCam = false;
            FlightCamera.fetch.ActivateUpdate();
            ParsekLog.Info(Tag, "TIERS DONE ut=" + Planetarium.GetUniversalTime().ToString("F1", Ic));
        }

        // Frames k = -1 .. 14 at eventUt + 0.25 k.
        private IEnumerator TierBurst(int tier, double eventUt)
        {
            for (int k = -1; k <= 14; k++)
            {
                yield return WaitUt(eventUt + 0.25 * k);
                string label = "fx9t-tier" + tier.ToString(Ic) + "-k" + (k + 1).ToString("D2", Ic);
                PoseTiers();
                Shoot(label);
                ParsekLog.Info(Tag, "shot " + label + " ut=" + Planetarium.GetUniversalTime().ToString("F2", Ic) +
                    " t=" + (Planetarium.GetUniversalTime() - eventUt).ToString("F2", Ic));
                TrackFreeFx();
                MeasureFree(label);
                yield return null;
            }
        }

        // ---- measurements

        private static ParticleSystem.Particle[] buffer = new ParticleSystem.Particle[4096];

        private void MeasureFree(string label)
        {
            foreach (GameObject root in FindRoots())
            {
                int id = root.GetInstanceID();
                Vector3d lla;
                if (!spawnLla.TryGetValue(id, out lla)) continue;
                Vector3d spawnNow = body.GetWorldSurfacePosition(lla.x, lla.y, lla.z);
                int total = 0; Vector3d centroid = Vector3d.zero;
                foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>(true))
                {
                    int n = ps.GetParticles(buffer);
                    for (int i = 0; i < n; i++) centroid += (Vector3d)buffer[i].position;
                    total += n;
                }
                if (total == 0) { ParsekLog.Info(Tag, "stats " + label + " " + root.name + " n=0"); continue; }
                centroid /= total;
                ParsekLog.Info(Tag, "stats " + label + " " + root.name + " n=" + total.ToString(Ic) +
                    " age=" + (Planetarium.GetUniversalTime() - spawnUt[id]).ToString("F2", Ic) +
                    "s centroidFromSpawnPoint=" + (centroid - spawnNow).magnitude.ToString("F1", Ic) + "m");
            }
        }

        private static List<GameObject> FindRoots()
        {
            var roots = new List<GameObject>();
            foreach (ParticleSystem ps in FindObjectsOfType<ParticleSystem>())
            {
                GameObject root = ps.transform.root.gameObject;
                if ((root.name == "GhostPartPuffFx" || root.name == "GhostExplosionFx" || root.name == "GhostStockExplosionFx") && !roots.Contains(root)) roots.Add(root);
            }
            return roots;
        }

        private void MeasureDust(string label, Case c)
        {
            Vector3d basePos = body.GetWorldSurfacePosition(c.Lat, c.Lon, c.GroundAlt);
            foreach (ParticleSystem ps in FindObjectsOfType<ParticleSystem>())
            {
                if (ps.gameObject.name != "ParsekLaunchDust") continue;
                int n = ps.GetParticles(buffer);
                if (n == 0) { ParsekLog.Info(Tag, "stats " + label + " dust n=0"); continue; }
                Vector3d centroid = Vector3d.zero;
                for (int i = 0; i < n; i++) centroid += (Vector3d)buffer[i].position;
                centroid /= n;
                ParsekLog.Info(Tag, "stats " + label + " dust n=" + n.ToString(Ic) +
                    " centroidFromLaunchPoint=" + (centroid - basePos).magnitude.ToString("F1", Ic) + "m");
            }
        }
    }
}
