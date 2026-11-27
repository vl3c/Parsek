using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Parsek.TestCommands;

namespace Parsek.InGameTests
{
    // THROWAWAY flight rig (never merged; ghost-fx-9). Ordinary flights recorded by the real
    // recorder (a reentry, a decouple and a mid-air end, a low launch with dust, a lamp toggle and
    // a Flea burn), then their ghosts replayed beside a flying viewer of the same kind. "before"
    // strips the floating-origin mover off the FX Parsek builds itself and the lamp fade; "after"
    // is the branch as built.
    [DefaultExecutionOrder(30000)]
    internal sealed class GhostFx9Rig : MonoBehaviour
    {
        private const string Tag = "GhostFx9";
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        internal sealed class Case
        {
            public string Kind, Id, RecordingId;
            public double StartUt, EventUt, Lat, Lon, Alt, Duration, EastSpeed, Descent, OffUt;
            public Vector3 CamDirLocal, FocusLocal;
            public float CamDist;
            public List<double> Shots = new List<double>();
        }

        private static readonly List<Case> cases = new List<Case>();
        private static GhostFx9Rig live;

        private Transform camFrame;
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
                case "fx9-ghost-after": live.StartCoroutine(live.Ghosts("after", false)); break;
            }
        }

        private void OnDestroy()
        {
            if (FlightCamera.fetch != null) FlightCamera.fetch.ActivateUpdate();
        }

        // ---- before mode: what the main build does

        private static bool IsParsekBuiltFx(GameObject go)
        {
            string n = go.name;
            if (n == "ReentryFire" || n == "ParsekLaunchDust") return true;
            string root = go.transform.root.name;
            return root == "GhostPartPuffFx" || root == "GhostExplosionFx";
        }

        private void LateUpdate()
        {
            if (before)
            {
                foreach (GhostWorldSpaceEmitter e in FindObjectsOfType<GhostWorldSpaceEmitter>())
                    if (e != null && IsParsekBuiltFx(e.gameObject)) DestroyImmediate(e);
                if (ParsekFlight.Instance != null)
                    foreach (var kv in ParsekFlight.Instance.ghostStates)
                        if (kv.Value != null && kv.Value.lightPlaybackStates != null)
                            foreach (LightPlaybackState l in kv.Value.lightPlaybackStates.Values)
                                l.fadeStartUT = double.NaN;
            }
            TrackFreeFx();
            Pose();
        }

        private void TrackFreeFx()
        {
            if (body == null) return;
            foreach (ParticleSystem ps in FindObjectsOfType<ParticleSystem>())
            {
                GameObject root = ps.transform.root.gameObject;
                if (root.name != "GhostPartPuffFx" && root.name != "GhostExplosionFx") continue;
                int id = root.GetInstanceID();
                if (spawnLla.ContainsKey(id)) continue;
                Vector3d p = root.transform.position;
                spawnLla[id] = new Vector3d(body.GetLatitude(p), body.GetLongitude(p), body.GetAltitude(p));
                spawnUt[id] = Planetarium.GetUniversalTime();
                ParsekLog.Info(Tag, pass + " free fx spawned " + root.name + " id=" + id.ToString(Ic) +
                    " ut=" + spawnUt[id].ToString("F2", Ic) + " alt=" + spawnLla[id].z.ToString("F1", Ic));
            }
        }

        private void OnEnable() { Camera.onPreCull += PreCull; }
        private void OnDisable() { Camera.onPreCull -= PreCull; }
        private void PreCull(Camera c)
        {
            if (FlightCamera.fetch != null && c == FlightCamera.fetch.mainCamera) Pose();
        }

        private void Pose()
        {
            if (camFrame == null || camCase == null || FlightCamera.fetch == null || body == null) return;
            Vector3 up = (Vector3)(camFrame.position - body.position).normalized;
            Vector3 focus = camFrame.TransformPoint(camCase.FocusLocal);
            Vector3 dir = camFrame.TransformDirection(camCase.CamDirLocal).normalized;
            Vector3 pos = focus + dir * camCase.CamDist;
            FlightCamera.fetch.GetCameraTransform().SetPositionAndRotation(pos, Quaternion.LookRotation(focus - pos, up));
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

        // ---- authoring

        private static ConfigNode Author(string kind, string name, Vessel anchor)
        {
            string[] stack;
            switch (kind)
            {
                case "reentry": stack = new[] { "probeCoreOcto.v2", "fuelTank" }; break;
                case "decouple": stack = new[] { "probeCoreOcto.v2", "Decoupler.1", "fuelTank" }; break;
                case "dust": stack = new[] { "probeCoreOcto.v2", "fuelTank", "liquidEngine2.v2" }; break;
                case "flea": stack = new[] { "probeCoreOcto.v2", "fuelTankSmallFlat", "solidBooster.sm.v2" }; break;
                default: stack = new[] { "probeCoreOcto.v2", "fuelTankSmallFlat" }; break;
            }
            var infos = new List<AvailablePart>();
            foreach (string s in stack)
            {
                AvailablePart a = PartLoader.getPartInfoByName(s);
                if (a == null) { ParsekLog.Info(Tag, "author " + name + ": missing part " + s); return null; }
                infos.Add(a);
            }
            AvailablePart lamp = null;
            if (kind == "lamp")
            {
                lamp = PartLoader.getPartInfoByName("spotLight1");
                if (lamp == null) { ParsekLog.Info(Tag, "author " + name + ": missing spotLight1"); return null; }
            }
            Func<AvailablePart, string, Vector3> node = (a, id) => a.partPrefab.FindAttachNode(id) != null ? a.partPrefab.FindAttachNode(id).position : Vector3.zero;
            var ids = GhostCoOrbitalRig.UniqueFlightIds(infos.Count + 1);
            var parts = new List<ConfigNode>();
            Vector3 pos = Vector3.zero;
            for (int i = 0; i < infos.Count; i++)
            {
                if (i > 0) pos += node(infos[i - 1], "bottom") - node(infos[i], "top");
                var att = new Dictionary<string, int>();
                if (i > 0) att["top"] = i - 1;
                if (i < infos.Count - 1) att["bottom"] = i + 1;
                parts.Add(GhostCoOrbitalRig.AuthorPartNode(infos[i], ids[i], i == 0 ? 0 : i - 1, pos, att, null, null));
            }
            if (lamp != null)
            {
                ConfigNode l = GhostCoOrbitalRig.AuthorPartNode(lamp, ids[infos.Count], 0, new Vector3(0.35f, -0.1f, 0f), null, null, null);
                l.AddValue("srfN", "srfAttach, 0");
                parts.Add(l);
            }
            Orbit o = anchor.orbit;
            return ProtoVessel.CreateVesselNode(name, VesselType.Ship,
                new Orbit(o.inclination, o.eccentricity, o.semiMajorAxis, o.LAN, o.argumentOfPeriapsis, o.meanAnomalyAtEpoch, o.epoch, o.referenceBody),
                0, parts.ToArray());
        }

        private static uint Spawn(Case c, string name, Vessel anchor, double dLat)
        {
            ConfigNode node = Author(c.Kind, name, anchor);
            if (node == null) return 0u;
            CelestialBody b = anchor.mainBody;
            double lat = c.Lat + dLat;
            Vector3d worldPos = b.GetWorldSurfacePosition(lat, c.Lon, c.Alt);
            Vector3d up = (worldPos - b.position).normalized;
            Vector3d east = Vector3d.Exclude(up, b.GetWorldSurfacePosition(lat, c.Lon + 0.001, c.Alt) - worldPos).normalized;
            Quaternion worldRot = c.Kind == "reentry"
                ? Quaternion.LookRotation((Vector3)up, (Vector3)east)
                : GhostCoOrbitalRig.UprightRotation(b, lat, c.Lon, c.Alt, out _);
            Quaternion srfRel = Quaternion.Inverse(b.bodyTransform.rotation) * worldRot;
            Vector3d surfaceVel = east * c.EastSpeed - up * c.Descent;
            Vector3d orbitalVel = surfaceVel + b.getRFrmVel(worldPos);
            uint pid = VesselSpawner.SpawnAtPosition(node, b, lat, c.Lon, c.Alt, orbitalVel, Planetarium.GetUniversalTime(),
                surfaceRelativeRotation: srfRel);
            // Stock loads a flying vessel only within 2.25 km of the active one; the cases sit up
            // to 12 km away from it, so this vessel loads and unpacks out to 30 km.
            Vessel sv = pid != 0u ? FlightRecorder.FindVesselByPid(pid) : null;
            if (sv != null && sv.vesselRanges != null)
            {
                foreach (VesselRanges.Situation s in new[] { sv.vesselRanges.flying, sv.vesselRanges.subOrbital, sv.vesselRanges.landed, sv.vesselRanges.prelaunch })
                {
                    s.load = 30000f; s.unload = 32000f; s.pack = 31000f; s.unpack = 29000f;
                }
            }
            ParsekLog.Info(Tag, "ranges " + name + " extended=" + (sv != null));
            ParsekLog.Info(Tag, "spawn " + name + " pid=" + pid.ToString(Ic) + " alt=" + c.Alt.ToString("F0", Ic) +
                " east=" + c.EastSpeed.ToString("F0", Ic) + " descent=" + c.Descent.ToString("F0", Ic));
            return pid;
        }

        private IEnumerator WaitLoaded(uint pid, Action<Vessel> found)
        {
            float end = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < end)
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

        // A case vessel must still fly when the next one spawns 20 s later: a crash hands the
        // active vessel back to the pad craft and Parsek resumes the lead tree on it.
        private static List<Case> Plan(Vessel host)
        {
            double lat = host.latitude, lon = host.longitude;
            var list = new List<Case>();
            Func<string, Case> mk = k => new Case { Kind = k, Id = k, Lat = lat, Lon = lon };
            Case lead = mk("lead"); lead.Duration = 1.0; list.Add(lead);
            Case dust = mk("dust"); dust.Lon = lon + 0.004; dust.Alt = -1; dust.Duration = 5.0;
            dust.CamDirLocal = new Vector3(1f, 0.25f, 0f); dust.CamDist = 45f; dust.FocusLocal = new Vector3(0f, -5f, 0f);
            dust.Shots.AddRange(new[] { 1.0, 2.0, 3.0 }); list.Add(dust);
            Case lamp = mk("lamp"); lamp.Lon = lon + 0.006; lamp.Alt = 2600; lamp.Descent = 1; lamp.Duration = 5.0;
            lamp.CamDirLocal = new Vector3(1f, 0.2f, 0.4f); lamp.CamDist = 8f; lamp.Shots.AddRange(new[] { 1.15, 1.4, 3.15, 3.4 }); list.Add(lamp);
            Case flea = mk("flea"); flea.Lon = lon + 0.008; flea.Alt = 1100; flea.Descent = 1; flea.Duration = 4.0;
            flea.CamDirLocal = new Vector3(1f, -0.3f, 0f); flea.CamDist = 25f; flea.FocusLocal = new Vector3(0f, -4f, 0f);
            flea.Shots.AddRange(new[] { 2.0, 3.0 }); list.Add(flea);
            Case dec = mk("decouple"); dec.Lon = lon + 0.010; dec.Alt = 3600; dec.Descent = 40; dec.Duration = 6.0;
            dec.CamDirLocal = new Vector3(1f, 0.1f, 0.2f); dec.CamDist = 35f; dec.FocusLocal = new Vector3(0f, -2f, 0f);
            dec.Shots.AddRange(new[] { 1.8, 2.4, 3.5, 5.5 }); list.Add(dec);
            Case re = mk("reentry"); re.Lon = lon + 0.02; re.Alt = 12000; re.EastSpeed = 1100; re.Duration = 5.0;
            re.CamDirLocal = new Vector3(0f, 0.2f, 1f); re.CamDist = 60f; re.FocusLocal = new Vector3(0f, -10f, 0f);
            re.Shots.AddRange(new[] { 2.0, 3.0, 4.0 }); list.Add(re);
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
            // Rewind-to-Launch strips the pad craft, and a save with no vessel loads into the
            // Space Center; an anchor beside the pad, spawned before the lead recording (and so in
            // its rewind save), gives the ghost passes a FLIGHT scene to start from.
            Case anchor = new Case { Kind = "anchor", Id = "anchor", Lat = host.latitude + 0.0008, Lon = host.longitude };
            anchor.Alt = body.TerrainAltitude(anchor.Lat, anchor.Lon) + 1.5;
            uint anchorPid = Spawn(anchor, "Fx9 anchor", host, 0.0);
            Vessel anchorVessel = null;
            yield return WaitLoaded(anchorPid, x => anchorVessel = x);
            ParsekLog.Info(Tag, "anchor pid=" + anchorPid.ToString(Ic) + " loaded=" + (anchorVessel != null));
            double baseUt = Planetarium.GetUniversalTime() + 2.0;
            Vessel previous = host;
            for (int i = 0; i < plan.Count; i++)
            {
                Case c = plan[i];
                yield return WaitUt(baseUt + 20.0 * i);
                Vessel v;
                if (c.Kind == "lead")
                {
                    v = host;
                }
                else
                {
                    if (c.Alt < 0) c.Alt = body.TerrainAltitude(c.Lat, c.Lon) + 8.0;
                    uint pid = Spawn(c, "Fx9 " + c.Id, previous, 0.0);
                    if (pid == 0u) continue;
                    v = null;
                    yield return WaitLoaded(pid, x => v = x);
                    if (v == null) { ParsekLog.Info(Tag, "case " + c.Id + ": never loaded"); continue; }
                    FlightGlobals.ForceSetActiveVessel(v);
                    yield return WaitUnpacked(v);
                    v.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
                }
                flight.StartRecording();
                c.StartUt = Planetarium.GetUniversalTime();
                if (previous != null && previous != v && previous != host) { previous.Die(); yield return null; }
                camCase = c; camFrame = v.rootPart.transform;
                FlightCamera.fetch.DeactivateUpdate();
                StartCoroutine(CaseActions(c, v, "real"));
                foreach (double s in c.Shots)
                {
                    yield return WaitUt(c.StartUt + s);
                    string label = "fx9-" + c.Id + "-t" + s.ToString("F2", Ic) + "-real";
                    Pose();
                    Shoot(label);
                    ParsekLog.Info(Tag, "shot " + label + " ut=" + Planetarium.GetUniversalTime().ToString("R", Ic) +
                        " alt=" + v.altitude.ToString("F0", Ic) + " srf=" + v.srfSpeed.ToString("F0", Ic) +
                        " frameVel=" + Krakensbane.GetFrameVelocity().magnitude.ToString("F1", Ic));
                    if (c.Kind == "flea") Sparks(label, v.gameObject);
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
                        if (c.RecordingId == null) c.RecordingId = r.RecordingId;
                        r.TerminalStateValue = TerminalState.Destroyed;
                        r.VesselSnapshot = null;
                    }
                }
                ParsekLog.Info(Tag, "case " + c.Id + " committed tree=" + treeId + " rec=" + c.RecordingId +
                    " start=" + c.StartUt.ToString("R", Ic) + " event=" + c.EventUt.ToString("R", Ic));
                cases.Add(c);
                camFrame = null; camCase = null;
                FlightCamera.fetch.ActivateUpdate();
                previous = v;
            }
            ParsekLog.Info(Tag, "REAL DONE cases=" + cases.Count.ToString(Ic) + " ut=" + Planetarium.GetUniversalTime().ToString("F1", Ic));
        }

        // The case's own actions, on the real vessel or on the viewer beside the ghost.
        private IEnumerator CaseActions(Case c, Vessel v, string who)
        {
            bool real = who == "real";
            switch (c.Kind)
            {
                case "dust":
                {
                    ModuleEngines e = Module<ModuleEngines>(v);
                    yield return WaitUt(c.StartUt + 0.5);
                    if (real) c.EventUt = Planetarium.GetUniversalTime();
                    if (e != null) e.Activate();
                    v.ctrlState.mainThrottle = 1f;
                    if (FlightGlobals.ActiveVessel == v) FlightInputHandler.state.mainThrottle = 1f;
                    break;
                }
                case "flea":
                {
                    yield return WaitUt(c.StartUt + 1.0);
                    if (real) c.EventUt = Planetarium.GetUniversalTime();
                    ModuleEngines e = Module<ModuleEngines>(v);
                    if (e != null) e.Activate();
                    break;
                }
                case "decouple":
                {
                    yield return WaitUt(c.StartUt + 1.5);
                    if (real)
                    {
                        c.EventUt = Planetarium.GetUniversalTime();
                        ModuleDecouple d = Module<ModuleDecouple>(v);
                        if (d != null) d.Decouple();
                    }
                    break;
                }
                case "lamp":
                {
                    if (!real) break;
                    ModuleLight l = Module<ModuleLight>(v);
                    yield return WaitUt(c.StartUt + 1.0);
                    c.EventUt = Planetarium.GetUniversalTime();
                    if (l != null) l.LightsOn();
                    yield return SampleRealLamp(c, v, c.EventUt, "on");
                    yield return WaitUt(c.StartUt + 3.0);
                    c.OffUt = Planetarium.GetUniversalTime();
                    if (l != null) l.LightsOff();
                    yield return SampleRealLamp(c, v, c.OffUt, "off");
                    break;
                }
            }
        }

        private static readonly double[] LampOffsets = { 0.05, 0.15, 0.3, 0.6, 1.2, 1.8 };

        private IEnumerator SampleRealLamp(Case c, Vessel v, double at, string what)
        {
            Light light = null;
            foreach (Part p in v.parts) { var ls = p.FindModelComponents<Light>(); if (ls != null && ls.Count > 0) light = ls[0]; }
            foreach (double off in LampOffsets)
            {
                if (what == "on" && at + off > c.StartUt + 3.0) break;
                yield return WaitUt(at + off);
                ParsekLog.Info(Tag, "lamp real " + what + " +" + off.ToString("F2", Ic) + "s intensity=" +
                    (light != null ? light.intensity.ToString("F3", Ic) : "-") + " enabled=" + (light != null && light.enabled));
            }
        }

        // ---- ghost passes

        private static GhostPlaybackState FindGhost(string recId)
        {
            if (ParsekFlight.Instance == null) return null;
            foreach (var kv in ParsekFlight.Instance.ghostStates)
                if (kv.Value != null && kv.Value.recordingId == recId && kv.Value.ghost != null) return kv.Value;
            return null;
        }

        private IEnumerator Ghosts(string name, bool strip)
        {
            pass = name;
            before = strip;
            Vessel previous = FlightGlobals.ActiveVessel;
            body = previous.mainBody;
            foreach (Case c in cases)
            {
                if (c.Kind == "lead") continue;
                if (Planetarium.GetUniversalTime() > c.StartUt - 0.3)
                {
                    ParsekLog.Info(Tag, "ghost " + pass + " " + c.Id + ": late ut=" + Planetarium.GetUniversalTime().ToString("F1", Ic));
                    continue;
                }
                Vessel viewer = null;
                yield return WaitUt(c.StartUt - 1.0);
                double dLat = c.Kind == "reentry" ? 0.0012 : c.Kind == "lamp" ? 0.0003 : 0.0006;
                // The viewer leaves 1 s early from 1 s up-range (on rails until loaded, so no drag),
                // passing the ghost's start beside it at the ghost's own speed.
                Case vc = new Case { Kind = c.Kind, Lat = c.Lat, Alt = c.Alt + c.Descent, EastSpeed = c.EastSpeed, Descent = c.Descent, StartUt = c.StartUt, EventUt = c.EventUt };
                vc.Lon = c.Lon - c.EastSpeed * 1.0 / (body.Radius + c.Alt) * 180.0 / Math.PI;
                uint pid = Spawn(vc, "Viewer " + c.Id, previous, dLat);
                yield return WaitLoaded(pid, x => viewer = x);
                if (viewer != null)
                {
                    FlightGlobals.ForceSetActiveVessel(viewer);
                    yield return WaitUnpacked(viewer);
                    viewer.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
                    if (previous != null && previous != viewer) previous.Die();
                    previous = viewer;
                }
                else ParsekLog.Info(Tag, "ghost " + pass + " " + c.Id + ": viewer never loaded");
                if (viewer != null) StartCoroutine(CaseActions(c, viewer, "viewer"));
                FlightCamera.fetch.DeactivateUpdate();
                if (c.Kind == "decouple") StartCoroutine(FallbackExplosion(c));
                if (c.Kind == "lamp") StartCoroutine(SampleGhostLamp(c));
                foreach (double s in c.Shots)
                {
                    double shotUt = c.StartUt + s;
                    while (Planetarium.GetUniversalTime() < shotUt)
                    {
                        GhostPlaybackState g = FindGhost(c.RecordingId);
                        camCase = g != null ? c : null; camFrame = g != null ? g.ghost.transform : null;
                        yield return null;
                    }
                    GhostPlaybackState gs = FindGhost(c.RecordingId);
                    string label = "fx9-" + c.Id + "-t" + s.ToString("F2", Ic) + "-" + pass;
                    if (gs == null) { ParsekLog.Info(Tag, "shot " + label + " MISSING ghost"); continue; }
                    camCase = c; camFrame = gs.ghost.transform;
                    Pose();
                    Shoot(label);
                    Vessel a = FlightGlobals.ActiveVessel;
                    ParsekLog.Info(Tag, "shot " + label + " ut=" + Planetarium.GetUniversalTime().ToString("R", Ic) +
                        " ghostActive=" + gs.ghost.activeInHierarchy +
                        " dist=" + Vector3.Distance(gs.ghost.transform.position, a.transform.position).ToString("F0", Ic) +
                        " viewer=" + a.vesselName + " viewerSrf=" + a.srfSpeed.ToString("F0", Ic) +
                        " frameVel=" + Krakensbane.GetFrameVelocity().magnitude.ToString("F1", Ic));
                    Measure(label, c, gs);
                    yield return null;
                }
                camCase = null; camFrame = null;
                FlightCamera.fetch.ActivateUpdate();
                yield return null;
            }
            before = false;
            ParsekLog.Info(Tag, "GHOST DONE " + pass + " ut=" + Planetarium.GetUniversalTime().ToString("F1", Ic));
        }

        private IEnumerator SampleGhostLamp(Case c)
        {
            foreach (var at in new[] { new KeyValuePair<string, double>("on", c.EventUt), new KeyValuePair<string, double>("off", c.OffUt) })
                foreach (double off in LampOffsets)
                {
                    if (at.Key == "on" && at.Value + off > c.StartUt + 3.0) break;
                    yield return WaitUt(at.Value + off);
                    GhostPlaybackState g = FindGhost(c.RecordingId);
                    Light light = null;
                    if (g != null && g.lightInfos != null)
                        foreach (LightGhostInfo li in g.lightInfos.Values)
                            if (li != null && li.lights != null && li.lights.Count > 0) light = li.lights[0];
                    ParsekLog.Info(Tag, "lamp " + pass + " " + at.Key + " +" + off.ToString("F2", Ic) + "s intensity=" +
                        (light != null ? light.intensity.ToString("F3", Ic) : "-") + " enabled=" + (light != null && light.enabled));
                }
        }

        private IEnumerator FallbackExplosion(Case c)
        {
            yield return WaitUt(c.StartUt + 2.5);
            GhostPlaybackState g = FindGhost(c.RecordingId);
            if (g == null) yield break;
            GameObject ex = GhostVisualBuilder.SpawnExplosionFx(g.ghost.transform.position + (Vector3)(g.ghost.transform.up * 15f), 4f);
            if (ex == null) yield break;
            yield return null;
            foreach (double off in new[] { 1.0, 2.0 })
            {
                yield return WaitUt(c.StartUt + 2.5 + off);
                if (ex == null) yield break;
                string label = "fx9-fallback-explosion-t" + off.ToString("F1", Ic) + "-" + pass;
                Free(label, ex);
            }
        }

        // ---- measurements

        private void Measure(string label, Case c, GhostPlaybackState g)
        {
            switch (c.Kind)
            {
                case "reentry":
                {
                    foreach (ParticleSystem ps in g.ghost.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        if (ps.gameObject.name != "ReentryFire") continue;
                        Vessel a = FlightGlobals.ActiveVessel;
                        Vector3 vel = a != null ? (Vector3)a.srf_velocity.normalized : Vector3.zero;
                        Cloud(label + " fire", ps, g.ghost.transform.position, vel);
                    }
                    break;
                }
                case "dust":
                {
                    foreach (ParticleSystem ps in g.ghost.GetComponentsInChildren<ParticleSystem>(true))
                        if (ps.gameObject.name == "ParsekLaunchDust")
                        {
                            Cloud(label + " dust", ps, g.ghost.transform.position, Vector3.zero);
                            Vector3d gp = g.ghost.transform.position;
                            double gAgl = body.GetAltitude(gp) - body.TerrainAltitude(body.GetLatitude(gp), body.GetLongitude(gp));
                            ParsekLog.Info(Tag, "stats " + label + " ghostAgl=" + gAgl.ToString("F1", Ic));
                        }
                    break;
                }
                case "decouple":
                {
                    foreach (ParticleSystem ps in FindObjectsOfType<ParticleSystem>())
                    {
                        GameObject root = ps.transform.root.gameObject;
                        if (root.name == "GhostPartPuffFx" && ps.gameObject == root) Free(label + " puff", root);
                    }
                    break;
                }
                case "flea":
                    Sparks(label, g.ghost);
                    break;
            }
        }

        private static ParticleSystem.Particle[] buffer = new ParticleSystem.Particle[4096];

        private void Cloud(string label, ParticleSystem ps, Vector3 anchor, Vector3 dir)
        {
            int n = ps.GetParticles(buffer);
            if (n == 0) { ParsekLog.Info(Tag, "stats " + label + " n=0"); return; }
            double sumDist = 0, sumBehind = 0, max = 0, sumAgl = 0;
            Vector3d centroid = Vector3d.zero;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = buffer[i].position - anchor;
                sumDist += d.magnitude; max = Math.Max(max, d.magnitude);
                if (dir != Vector3.zero) sumBehind += -Vector3.Dot(d, dir);
                centroid += (Vector3d)buffer[i].position;
                Vector3d p = buffer[i].position;
                sumAgl += body.GetAltitude(p) - body.TerrainAltitude(body.GetLatitude(p), body.GetLongitude(p));
            }
            ParsekLog.Info(Tag, "stats " + label + " n=" + n.ToString(Ic) + " meanDist=" + (sumDist / n).ToString("F1", Ic) +
                "m max=" + max.ToString("F1", Ic) + "m meanBehind=" + (sumBehind / n).ToString("F1", Ic) +
                "m meanAgl=" + (sumAgl / n).ToString("F1", Ic) + "m");
        }

        private void Free(string label, GameObject root)
        {
            int id = root.GetInstanceID();
            Vector3d lla;
            if (!spawnLla.TryGetValue(id, out lla)) { ParsekLog.Info(Tag, "stats " + label + " untracked"); return; }
            Vector3d spawnNow = body.GetWorldSurfacePosition(lla.x, lla.y, lla.z);
            int total = 0; Vector3d centroid = Vector3d.zero;
            foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                int n = ps.GetParticles(buffer);
                for (int i = 0; i < n; i++) centroid += (Vector3d)buffer[i].position;
                total += n;
            }
            if (total == 0) { ParsekLog.Info(Tag, "stats " + label + " n=0"); return; }
            centroid /= total;
            double age = Planetarium.GetUniversalTime() - spawnUt[id];
            ParsekLog.Info(Tag, "stats " + label + " n=" + total.ToString(Ic) + " age=" + age.ToString("F2", Ic) +
                "s centroidFromSpawnPoint=" + (centroid - spawnNow).magnitude.ToString("F1", Ic) +
                "m rootFromSpawnPoint=" + ((Vector3d)root.transform.position - spawnNow).magnitude.ToString("F1", Ic) + "m");
        }

        private static void Sparks(string label, GameObject root)
        {
            int systems = 0, particles = 0;
            foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps.gameObject.name.IndexOf("Sparks", StringComparison.OrdinalIgnoreCase) < 0) continue;
                systems++; particles += ps.particleCount;
            }
            ParsekLog.Info(Tag, "stats " + label + " sparksSystems=" + systems.ToString(Ic) + " sparksParticles=" + particles.ToString(Ic));
        }
    }
}
