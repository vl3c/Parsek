using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Parsek.TestCommands;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// THROWAWAY P9 X0 flight rig (design-part-ghost-parity.md Appendix G, G.6 U6 to U10; the
    /// SMK pattern of GHOST-SMOKE-RIDES-ACTIVE-VESSEL; never merged). Driven by two
    /// MissionMark labels:
    /// - fxr-real: spawns a probe + heat shield + chute over a staged FL-T400 / LV-T45 booster
    ///   beside the pad host, makes it ACTIVE, puts it on a steep Kerbin entry west of KSC,
    ///   records it with the real recorder, drops the booster at 45 km and photographs the
    ///   composited flight-camera frame (ScreenCapture) at marks (72 km, Mach 6, 3.75, 3, 2.5,
    ///   2) from fixed poses relative to the probe, with isolation frames (AerodynamicsFX
    ///   cameras off, then blackbody off) and a camera-distance sweep on the booster; a ghost
    ///   built from the probe's own snapshot rides 10 m beside it (does stock paint a ghost);
    ///   commits the tree.
    /// - fxr-ghost (after InvokeRewindToLaunch): watches the probe's ghost from the pad host
    ///   and photographs it at the same UTs and poses, then diffs real against ghost outside
    ///   the vessel masks.
    /// Greppable `[FxReentryRig]` Info lines, Screenshots/fxr-*.png.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    internal sealed class FxReentryRig : MonoBehaviour
    {
        private const string Tag = "FxReentryRig";
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        // Entry: 95 km up, 50 km west of the pad, 2.2 km/s surface speed, 35 deg below the
        // horizon, heading east; the marks fall roughly over and east of KSC, inside the
        // 120 km ghost visual range and the 300 km watch cutoff of the pad host.
        private const double EntryAltM = 95000.0;
        private const double EntryWestM = 50000.0;
        private const double EntrySpeedMps = 2200.0;
        private const double EntryFpaDeg = 35.0;
        private const double StageAltM = 45000.0;
        // The real leg commits by this UT at the latest (the spec waits to UT 260).
        private const double RealLegTimeoutUt = 240.0;
        private const float MainDist = 32f;
        private const float RearDist = 40f;
        private const float SideGhostOffset = 10f;
        private const int DiffThreshold = 16;
        private const int MaskDilatePx = 24;
        private static readonly float[] BoosterSweepM = { 20f, 60f, 150f, 400f, 1000f };

        internal sealed class Shot
        {
            public string Label, Mark, Kind;
            public double Ut;
            public bool BoosterFrame;
            public Vector3 DirLocal, FocusLocal;
            public float Dist;
            public bool GhostFxOff;
            public bool OnGhost;
            public List<RectInt> Masks = new List<RectInt>();
        }

        private static List<Shot> shots = new List<Shot>();
        private static readonly Dictionary<string, Color32[]> realPx = new Dictionary<string, Color32[]>();
        private static int realW, realH;
        private static readonly HashSet<string> mainRecIds = new HashSet<string>();
        private static readonly HashSet<string> boosterRecIds = new HashSet<string>();
        private static uint rootPartPid, boosterPartPid;
        private static FxReentryRig live;

        // GameSettings.AERO_FX_QUALITY: 0 = stock aero FX off (AerodynamicsFX takes its
        // disable branch while detailLevel <= 0, the FX camera stays off: no reentry flames, no
        // Mach streaks); 1..3 = on, picking FXCamera's occlusion map size (512 / 1024 / 2048;
        // 0 maps to 256) and the replacement shader index. 3 is stock's default and the highest.
        // Every harness profile pins 0 (GT-8 low-detail pin), so the rig raises it for its run.
        private const int AeroFxQualityMax = 3;
        private static bool aeroQualitySaved;
        private static int aeroQualityBefore;

        private Transform poseFrame;
        private Vector3 poseDirLocal, poseFocusLocal;
        private float poseDist;
        private bool camFxSaved;
        private float camFxExternal, camFxInternal;
        private GhostCoOrbitalRig.LiveGhost sideGhost;
        private AerodynamicsFX aero;
        private int files;

        internal static void OnMark(string label)
        {
            if (label == null || !label.StartsWith("fxr-", StringComparison.Ordinal)) return;
            if (live != null) Destroy(live.gameObject);
            var go = new GameObject("FxReentryRig");
            live = go.AddComponent<FxReentryRig>();
            GameEvents.onHideUI.Fire();
            ParsekLog.Info(Tag, "mark " + label + " ut=" + Planetarium.GetUniversalTime().ToString("0.00", Ic));
            switch (label)
            {
                case "fxr-real": live.StartCoroutine(live.Real()); break;
                case "fxr-ghost": live.StartCoroutine(live.Ghost()); break;
                default: ParsekLog.Info(Tag, "unknown mark " + label); break;
            }
        }

        // ===================================================================================
        // camera pose (applied in LateUpdate and again right before the flight camera culls)
        // ===================================================================================

        private void LateUpdate()
        {
            Pose();
            if (poseFrame != null && FlightCamera.fetch != null && FlightCamera.fetch.transform.parent != null)
            {
                // FXCamera reads FlightCamera.Distance for its far clip and occlusion size.
                float d = (FlightCamera.fetch.GetCameraTransform().position - FlightCamera.fetch.transform.parent.position).magnitude;
                FlightCamera.fetch.SetDistance(d);
            }
        }

        private void OnEnable() { Camera.onPreCull += PreCull; }
        private void OnDisable() { Camera.onPreCull -= PreCull; }

        private void PreCull(Camera c)
        {
            if (FlightCamera.fetch != null && c == FlightCamera.fetch.mainCamera) Pose();
        }

        private void OnDestroy()
        {
            RestoreAeroQuality("rig destroyed");
            RestoreCameraFx();
            if (FlightCamera.fetch != null) FlightCamera.fetch.ActivateUpdate();
            if (sideGhost != null) GhostCoOrbitalRig.DestroyLiveGhost(sideGhost);
        }

        private void Pose()
        {
            if (poseFrame == null || FlightCamera.fetch == null) return;
            Vector3 focus = poseFrame.TransformPoint(poseFocusLocal);
            Vector3 dir = poseFrame.TransformDirection(poseDirLocal).normalized;
            Vector3 pos = focus + dir * poseDist;
            Vector3 up = poseFrame.up;
            if (Vector3.Cross(up, dir).sqrMagnitude < 1e-4f) up = poseFrame.forward;
            FlightCamera.fetch.GetCameraTransform().SetPositionAndRotation(pos, Quaternion.LookRotation(focus - pos, up));
        }

        private void SetPose(Transform frame, Vector3 dirLocal, Vector3 focusLocal, float dist)
        {
            poseFrame = frame; poseDirLocal = dirLocal; poseFocusLocal = focusLocal; poseDist = dist;
            Pose();
        }

        private void DisableCameraFx()
        {
            if (!camFxSaved)
            {
                camFxExternal = GameSettings.CAMERA_FX_EXTERNAL;
                camFxInternal = GameSettings.CAMERA_FX_INTERNAL;
                camFxSaved = true;
            }
            GameSettings.CAMERA_FX_EXTERNAL = 0f;
            GameSettings.CAMERA_FX_INTERNAL = 0f;
            ParsekLog.Info(Tag, $"camera shake off: CAMERA_FX_EXTERNAL {camFxExternal.ToString("0.00", Ic)} -> 0, CAMERA_FX_INTERNAL {camFxInternal.ToString("0.00", Ic)} -> 0");
        }

        private void RestoreCameraFx()
        {
            if (!camFxSaved) return;
            GameSettings.CAMERA_FX_EXTERNAL = camFxExternal;
            GameSettings.CAMERA_FX_INTERNAL = camFxInternal;
            camFxSaved = false;
            ParsekLog.Info(Tag, $"camera shake restored: CAMERA_FX_EXTERNAL={camFxExternal.ToString("0.00", Ic)} CAMERA_FX_INTERNAL={camFxInternal.ToString("0.00", Ic)}");
        }

        // ===================================================================================
        // stock aero FX state
        // ===================================================================================

        private static int DetailLevel(AerodynamicsFX a) => a != null ? a.detailLevel : -1;

        /// <summary>Re-runs the private OnSettingsUpdate of AerodynamicsFX (copies
        /// AERO_FX_QUALITY into detailLevel) and of FXCamera (occlusion map size) - what stock's
        /// GameEvents.OnGameSettingsApplied does, without waking every other subscriber. Falls back
        /// to firing that event when the reflection lookup fails.</summary>
        private string ReapplyAeroSettings()
        {
            var sb = new StringBuilder();
            bool ok = true;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            AerodynamicsFX a = Aero();
            MethodInfo ma = typeof(AerodynamicsFX).GetMethod("OnSettingsUpdate", F, null, Type.EmptyTypes, null);
            if (a != null && ma != null) { ma.Invoke(a, null); sb.Append("aero.OnSettingsUpdate "); }
            else { ok = false; sb.Append($"aero.OnSettingsUpdate unavailable(aero={(a != null)} method={(ma != null)}) "); }
            MethodInfo mc = typeof(FXCamera).GetMethod("OnSettingsUpdate", F, null, Type.EmptyTypes, null);
            if (FXCamera.Instance != null && mc != null) { mc.Invoke(FXCamera.Instance, null); sb.Append("fxCamera.OnSettingsUpdate "); }
            else { ok = false; sb.Append($"fxCamera.OnSettingsUpdate unavailable(fxCam={(FXCamera.Instance != null)} method={(mc != null)}) "); }
            if (!ok) { GameEvents.OnGameSettingsApplied.Fire(); sb.Append("fallback GameEvents.OnGameSettingsApplied.Fire "); }
            return sb.ToString();
        }

        private void RaiseAeroQuality(string leg)
        {
            AerodynamicsFX a = Aero();
            int detailBefore = DetailLevel(a);
            if (!aeroQualitySaved) { aeroQualityBefore = GameSettings.AERO_FX_QUALITY; aeroQualitySaved = true; }
            int settingBefore = GameSettings.AERO_FX_QUALITY;
            GameSettings.AERO_FX_QUALITY = AeroFxQualityMax;
            string how = ReapplyAeroSettings();
            ParsekLog.Info(Tag, $"{leg} aero fx quality: AERO_FX_QUALITY {settingBefore.ToString(Ic)} -> {GameSettings.AERO_FX_QUALITY.ToString(Ic)} " +
                $"detailLevel {detailBefore.ToString(Ic)} -> {DetailLevel(a).ToString(Ic)} saved={aeroQualityBefore.ToString(Ic)} via {how}| {AeroLine()}");
        }

        private void RestoreAeroQuality(string why)
        {
            if (!aeroQualitySaved) return;
            AerodynamicsFX a = Aero();
            int detailBefore = DetailLevel(a);
            int settingBefore = GameSettings.AERO_FX_QUALITY;
            GameSettings.AERO_FX_QUALITY = aeroQualityBefore;
            aeroQualitySaved = false;
            string how = ReapplyAeroSettings();
            ParsekLog.Info(Tag, $"aero fx quality restored ({why}): AERO_FX_QUALITY {settingBefore.ToString(Ic)} -> {GameSettings.AERO_FX_QUALITY.ToString(Ic)} " +
                $"detailLevel {detailBefore.ToString(Ic)} -> {DetailLevel(a).ToString(Ic)} via {how}| {AeroLine()}");
        }

        private AerodynamicsFX Aero()
        {
            if (aero == null) aero = FindObjectOfType<AerodynamicsFX>();
            return aero;
        }

        private static Camera VelocityCam()
        {
            if (FXCamera.Instance == null) return null;
            FieldInfo f = typeof(FXCamera).GetField("velocityCam", BindingFlags.Instance | BindingFlags.NonPublic);
            return f != null ? f.GetValue(FXCamera.Instance) as Camera : null;
        }

        private string AeroLine()
        {
            AerodynamicsFX a = Aero();
            var sb = new StringBuilder();
            if (a == null) sb.Append("aero=none ");
            else
            {
                sb.Append($"detailLevel={a.detailLevel.ToString(Ic)} FxScalar={a.FxScalar.ToString("0.000", Ic)} state={a.state.ToString("0.000", Ic)} mach={a.machNumber.ToString("0.00", Ic)} " +
                    $"airDensity={a.airDensity.ToString("0.00000", Ic)} densityFactor={a.densityFactor.ToString("0.00000", Ic)} ");
                if (a.fxLight != null)
                    sb.Append($"fxLight={a.fxLight.enabled}/{a.fxLight.intensity.ToString("0.00", Ic)}/({a.fxLight.color.r.ToString("0.00", Ic)},{a.fxLight.color.g.ToString("0.00", Ic)},{a.fxLight.color.b.ToString("0.00", Ic)}) ");
            }
            Camera fx = FXCamera.Instance != null ? FXCamera.Instance.GetComponent<Camera>() : null;
            if (fx != null)
                sb.Append($"fxCam enabled={fx.enabled} cullingMask=0x{fx.cullingMask:X} far={fx.farClipPlane.ToString("0.0", Ic)} near={fx.nearClipPlane.ToString("0.00", Ic)} fov={fx.fieldOfView.ToString("0.0", Ic)} " +
                    $"cameraDistance={FXCamera.Instance.cameraDistance.ToString("0.0", Ic)} ");
            else sb.Append("fxCam=none ");
            Camera vc = VelocityCam();
            if (vc != null) sb.Append($"velocityCam enabled={vc.enabled} ortho={vc.orthographicSize.ToString("0.0", Ic)} far={vc.farClipPlane.ToString("0.0", Ic)} mask=0x{vc.cullingMask:X} ");
            if (FlightCamera.fetch != null)
            {
                Transform ct = FlightCamera.fetch.GetCameraTransform();
                Vessel av = FlightGlobals.ActiveVessel;
                sb.Append($"flightCamDistance={FlightCamera.fetch.Distance.ToString("0.0", Ic)} camToActive={(av != null ? (ct.position - av.transform.position).magnitude : -1f).ToString("0.0", Ic)}");
            }
            return sb.ToString();
        }

        private List<Behaviour> aeroOff;

        private void SetAeroCameras(bool on, bool alsoLight)
        {
            AerodynamicsFX a = Aero();
            if (!on)
            {
                aeroOff = new List<Behaviour>();
                var list = new List<Behaviour>();
                if (a != null && a.fxCamera != null) list.Add(a.fxCamera.GetComponent<Camera>());
                if (a != null && a.fxDepthCamera != null) list.Add(a.fxDepthCamera.GetComponent<Camera>());
                list.Add(VelocityCam());
                if (alsoLight && a != null && a.fxLight != null) list.Add(a.fxLight);
                foreach (Behaviour b in list)
                    if (b != null && b.enabled) { b.enabled = false; aeroOff.Add(b); }
            }
            else if (aeroOff != null)
            {
                foreach (Behaviour b in aeroOff) if (b != null) b.enabled = true;
                aeroOff = null;
            }
        }

        private readonly Dictionary<Part, float> bbSaved = new Dictionary<Part, float>();

        private void SetBlackbody(bool on)
        {
            if (!on)
            {
                bbSaved.Clear();
                foreach (Vessel v in FlightGlobals.VesselsLoaded)
                {
                    if (v == null || v.parts == null) continue;
                    foreach (Part p in v.parts)
                        if (p != null) { bbSaved[p] = p.blackBodyRadiationAlphaMult; p.blackBodyRadiationAlphaMult = 0f; }
                }
            }
            else
            {
                foreach (var kv in bbSaved) if (kv.Key != null) kv.Key.blackBodyRadiationAlphaMult = kv.Value;
                bbSaved.Clear();
            }
        }

        // ===================================================================================
        // frames
        // ===================================================================================

        private static IEnumerator Grab(Action<Color32[], int, int> got)
        {
            yield return new WaitForEndOfFrame();
            Texture2D t = ScreenCapture.CaptureScreenshotAsTexture();
            Color32[] px = t.GetPixels32();
            int w = t.width, h = t.height;
            Destroy(t);
            got(px, w, h);
        }

        private static List<Renderer> RenderersOf(GameObject root)
        {
            var list = new List<Renderer>();
            if (root == null) return list;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(false))
                if (r != null && r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer)) list.Add(r);
            return list;
        }

        private static List<Renderer> RenderersOf(Vessel v)
        {
            var list = new List<Renderer>();
            if (v == null || v.parts == null) return list;
            foreach (Part p in v.parts) if (p != null) list.AddRange(RenderersOf(p.gameObject));
            return list;
        }

        private static RectInt ScreenRect(List<Renderer> rs, int w, int h)
        {
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null || rs.Count == 0) return new RectInt(0, 0, 0, 0);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            bool any = false;
            foreach (Renderer r in rs)
            {
                Bounds b = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 s = cam.WorldToScreenPoint(c);
                    if (s.z <= 0f) continue;
                    any = true;
                    x0 = Math.Min(x0, s.x); y0 = Math.Min(y0, s.y); x1 = Math.Max(x1, s.x); y1 = Math.Max(y1, s.y);
                }
            }
            if (!any) return new RectInt(0, 0, 0, 0);
            int ix0 = Mathf.Clamp((int)x0, 0, w), iy0 = Mathf.Clamp((int)y0, 0, h);
            int ix1 = Mathf.Clamp((int)Math.Ceiling(x1), 0, w), iy1 = Mathf.Clamp((int)Math.Ceiling(y1), 0, h);
            return new RectInt(ix0, iy0, Math.Max(0, ix1 - ix0), Math.Max(0, iy1 - iy0));
        }

        private static string R(RectInt r) => $"[{r.x.ToString(Ic)},{r.y.ToString(Ic)},{r.width.ToString(Ic)},{r.height.ToString(Ic)}]";

        private static bool Differs(Color32 a, Color32 b) =>
            Math.Abs(a.r - b.r) >= DiffThreshold || Math.Abs(a.g - b.g) >= DiffThreshold || Math.Abs(a.b - b.b) >= DiffThreshold;

        private static int DiffIn(Color32[] a, Color32[] b, int w, RectInt r)
        {
            if (a == null || b == null || a.Length != b.Length) return -1;
            int n = 0;
            for (int y = r.y; y < r.y + r.height; y++)
                for (int x = r.x; x < r.x + r.width; x++)
                    if (Differs(a[y * w + x], b[y * w + x])) n++;
            return n;
        }

        private static int DiffAll(Color32[] a, Color32[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return -1;
            int n = 0;
            for (int i = 0; i < a.Length; i++) if (Differs(a[i], b[i])) n++;
            return n;
        }

        /// <summary>Differing pixels and mean abs channel difference outside the masks (each
        /// dilated by <see cref="MaskDilatePx"/>).</summary>
        private static string DiffOutside(Color32[] a, Color32[] b, int w, int h, List<RectInt> masks)
        {
            if (a == null || b == null || a.Length != b.Length) return "n/a";
            var inMask = new bool[w * h];
            int maskPx = 0;
            foreach (RectInt m in masks)
            {
                if (m.width <= 0 || m.height <= 0) continue;
                int x0 = Math.Max(0, m.x - MaskDilatePx), y0 = Math.Max(0, m.y - MaskDilatePx);
                int x1 = Math.Min(w, m.x + m.width + MaskDilatePx), y1 = Math.Min(h, m.y + m.height + MaskDilatePx);
                for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = y * w + x; if (!inMask[i]) { inMask[i] = true; maskPx++; } }
            }
            long sum = 0; int n = 0, diff = 0, diffBig = 0, top = 0, bottom = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (inMask[i]) continue;
                n++;
                int d = Math.Max(Math.Abs(a[i].r - b[i].r), Math.Max(Math.Abs(a[i].g - b[i].g), Math.Abs(a[i].b - b[i].b)));
                sum += d;
                if (d >= DiffThreshold) { diff++; if (i / w >= h / 2) top++; else bottom++; }
                if (d >= 48) diffBig++;
            }
            return $"outsidePx={n.ToString(Ic)} maskPx={maskPx.ToString(Ic)} diff16={diff.ToString(Ic)} ({(100.0 * diff / Math.Max(1, n)).ToString("0.00", Ic)}%) " +
                $"diff48={diffBig.ToString(Ic)} meanAbs={(sum / (double)Math.Max(1, n)).ToString("0.00", Ic)} diffTopHalf={top.ToString(Ic)} diffBottomHalf={bottom.ToString(Ic)}";
        }

        private static Color32[] DiffImage(Color32[] a, Color32[] b)
        {
            var o = new Color32[a.Length];
            for (int i = 0; i < a.Length; i++)
                o[i] = new Color32((byte)Math.Min(255, 4 * Math.Abs(a[i].r - b[i].r)), (byte)Math.Min(255, 4 * Math.Abs(a[i].g - b[i].g)),
                    (byte)Math.Min(255, 4 * Math.Abs(a[i].b - b[i].b)), 255);
            return o;
        }

        private void Png(string name, Color32[] px, int w, int h)
        {
            if (px == null) return;
            if (SpikePng.Write(name + ".png", w, h, px, Tag)) files++;
        }

        // ===================================================================================
        // the reentry vessel
        // ===================================================================================

        private static Vector3 Node(AvailablePart a, string id)
        {
            AttachNode n = a?.partPrefab != null ? a.partPrefab.FindAttachNode(id) : null;
            return n != null ? n.position : Vector3.zero;
        }

        /// <summary>Top to bottom: chute, probe (root), heat shield, TD-12, FL-T400, LV-T45.</summary>
        private static ConfigNode AuthorStack(Vessel host, out string desc)
        {
            AvailablePart chute = PartLoader.getPartInfoByName("parachuteSingle");
            AvailablePart probe = PartLoader.getPartInfoByName("probeStackSmall") ?? PartLoader.getPartInfoByName("probeCoreOcto.v2");
            AvailablePart shield = PartLoader.getPartInfoByName("HeatShield1");
            AvailablePart dec = PartLoader.getPartInfoByName("Decoupler.1");
            AvailablePart tank = PartLoader.getPartInfoByName("fuelTank");
            AvailablePart eng = PartLoader.getPartInfoByName("liquidEngine2.v2");
            desc = $"chute={chute?.name} probe={probe?.name} shield={shield?.name} dec={dec?.name} tank={tank?.name} eng={eng?.name}";
            if (probe == null || shield == null || dec == null || tank == null) return null;
            List<uint> ids = GhostCoOrbitalRig.UniqueFlightIds(6);
            var parts = new List<ConfigNode>();
            int iChute = chute != null ? 1 : -1;
            int iShield = chute != null ? 2 : 1;
            int iDec = iShield + 1, iTank = iDec + 1, iEng = eng != null ? iTank + 1 : -1;
            var probeAtt = new Dictionary<string, int> { { "bottom", iShield } };
            if (iChute > 0) probeAtt["top"] = iChute;
            parts.Add(GhostCoOrbitalRig.AuthorPartNode(probe, ids[0], 0, Vector3.zero, probeAtt, null, null));
            if (chute != null)
                parts.Add(GhostCoOrbitalRig.AuthorPartNode(chute, ids[1], 0, Node(probe, "top") - Node(chute, "bottom"),
                    new Dictionary<string, int> { { "bottom", 0 } }, null, null));
            Vector3 shieldPos = Node(probe, "bottom") - Node(shield, "top");
            parts.Add(GhostCoOrbitalRig.AuthorPartNode(shield, ids[2], 0, shieldPos,
                new Dictionary<string, int> { { "top", 0 }, { "bottom", iDec } }, null, null));
            Vector3 decPos = shieldPos + Node(shield, "bottom") - Node(dec, "top");
            parts.Add(GhostCoOrbitalRig.AuthorPartNode(dec, ids[3], iShield, decPos,
                new Dictionary<string, int> { { "top", iShield }, { "bottom", iTank } }, null, null));
            Vector3 tankPos = decPos + Node(dec, "bottom") - Node(tank, "top");
            var tankAtt = new Dictionary<string, int> { { "top", iDec } };
            if (iEng > 0) tankAtt["bottom"] = iEng;
            parts.Add(GhostCoOrbitalRig.AuthorPartNode(tank, ids[4], iDec, tankPos, tankAtt, null, null));
            if (eng != null)
                parts.Add(GhostCoOrbitalRig.AuthorPartNode(eng, ids[5], iTank, tankPos + Node(tank, "bottom") - Node(eng, "top"),
                    new Dictionary<string, int> { { "top", iTank } }, null, null));
            Orbit o = host.orbit;
            return ProtoVessel.CreateVesselNode("FXR Probe", VesselType.Probe,
                new Orbit(o.inclination, o.eccentricity, o.semiMajorAxis, o.LAN, o.argumentOfPeriapsis, o.meanAnomalyAtEpoch, o.epoch, o.referenceBody),
                0, parts.ToArray());
        }

        private static IEnumerator WaitLive(uint pid, Action<Vessel> found)
        {
            float end = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < end)
            {
                Vessel v = FlightRecorder.FindVesselByPid(pid);
                if (v != null && v.loaded && !v.packed && v.parts.Count > 0) { found(v); yield break; }
                yield return null;
            }
            found(null);
        }

        private static string Teleport(Vessel v, CelestialBody body)
        {
            double mpd = 2.0 * Math.PI * body.Radius / 360.0;
            double lat = PartParityAirPlan.PadLatitudeDeg, lon = PartParityAirPlan.PadLongitudeDeg - EntryWestM / mpd;
            Vector3d worldPos = body.GetWorldSurfacePosition(lat, lon, EntryAltM);
            Vector3d up = (worldPos - body.position).normalized;
            Vector3d eastPos = body.GetWorldSurfacePosition(lat, lon + 0.001, EntryAltM);
            Vector3d east = Vector3d.Exclude(up, eastPos - worldPos).normalized;
            double fpa = EntryFpaDeg * Math.PI / 180.0;
            Vector3d srfVel = (east * Math.Cos(fpa) - up * Math.Sin(fpa)) * EntrySpeedMps;
            Vector3d obtVel = srfVel + body.getRFrmVel(worldPos);
            // Booster first: the stack's +Y (chute end) points against the airflow.
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, -((Vector3)srfVel).normalized);
            if (!v.packed) v.GoOnRails();
            double ut = Planetarium.GetUniversalTime();
            v.SetRotation(rot);
            v.orbit.UpdateFromStateVectors((worldPos - body.position).xzy, obtVel.xzy, body, ut);
            v.orbitDriver.updateFromParameters();
            FloatingOrigin.SetOffset(v.transform.position);
            OrbitPhysicsManager.CheckReferenceFrame();
            OrbitPhysicsManager.HoldVesselUnpack(10);
            v.UpdatePosVel();
            return $"lat={lat.ToString("0.0000", Ic)} lon={lon.ToString("0.0000", Ic)} alt={EntryAltM.ToString("0", Ic)} srfSpeed={EntrySpeedMps.ToString("0", Ic)} " +
                $"fpa=-{EntryFpaDeg.ToString("0", Ic)}deg obtSpeed={obtVel.magnitude.ToString("0.0", Ic)} pe={v.orbit.PeA.ToString("0", Ic)} ap={v.orbit.ApA.ToString("0", Ic)}";
        }

        private static Part FindPart(Vessel v, uint pid)
        {
            if (v == null || v.parts == null) return null;
            foreach (Part p in v.parts) if (p != null && p.persistentId == pid) return p;
            return null;
        }

        private static Vessel FindLoadedVesselWithPart(uint pid, Vessel except)
        {
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
                if (v != null && v != except && FindPart(v, pid) != null) return v;
            return null;
        }

        private static double PadDistance(CelestialBody body, Vector3 p)
        {
            Vector3d pad = body.GetWorldSurfacePosition(PartParityAirPlan.PadLatitudeDeg, PartParityAirPlan.PadLongitudeDeg, 70.0);
            return ((Vector3d)p - pad).magnitude;
        }

        private string Trace(Vessel v, Vessel host)
        {
            double maxSkin = 0; string hot = "-";
            foreach (Part p in v.parts)
                if (p != null && p.skinTemperature > maxSkin) { maxSkin = p.skinTemperature; hot = p.partInfo.name; }
            Vessel booster = FindLoadedVesselWithPart(boosterPartPid, v);
            return $"ut={Planetarium.GetUniversalTime().ToString("0.00", Ic)} alt={v.altitude.ToString("0", Ic)} mach={v.mach.ToString("0.00", Ic)} rho={v.atmDensity.ToString("0.00000", Ic)} " +
                $"srfSpeed={v.srfSpeed.ToString("0.0", Ic)} vSpeed={v.verticalSpeed.ToString("0.0", Ic)} parts={v.parts.Count.ToString(Ic)} hottest={hot}@{maxSkin.ToString("0", Ic)}K " +
                $"padDist={PadDistance(v.mainBody, v.transform.position).ToString("0", Ic)} booster={(booster != null ? (booster.transform.position - v.transform.position).magnitude.ToString("0", Ic) + "m" : "none")} " +
                $"sit={v.situation} {AeroLine()}";
        }

        // ===================================================================================
        // real leg
        // ===================================================================================

        private IEnumerator Real()
        {
            shots = new List<Shot>();
            realPx.Clear(); mainRecIds.Clear(); boosterRecIds.Clear();
            rootPartPid = 0; boosterPartPid = 0;
            Vessel host = FlightGlobals.ActiveVessel;
            ParsekFlight flight = ParsekFlight.Instance;
            if (host == null || flight == null) { ParsekLog.Info(Tag, "real: no host / flight"); yield break; }
            CelestialBody body = host.mainBody;
            ParsekLog.Info(Tag, $"real env: host={host.vesselName} sit={host.situation} screen={Screen.width}x{Screen.height} aeroFxQuality={GameSettings.AERO_FX_QUALITY} " +
                $"thermalStart={PhysicsGlobals.AeroFXStartThermalFX.ToString("0.00", Ic)} thermalFull={PhysicsGlobals.AeroFXFullThermalFX.ToString("0.00", Ic)} " +
                $"densityFadeStart={PhysicsGlobals.AeroFXDensityFadeStart.ToString("0.0000", Ic)} watchCutoffKm={DistanceThresholds.GhostFlight.DefaultWatchCameraCutoffKm.ToString("0", Ic)} " +
                $"ghostVisualRangeM={DistanceThresholds.GhostVisualRangeMeters.ToString("0", Ic)} {AeroLine()}");
            RaiseAeroQuality("real");
            ConfigNode node = AuthorStack(host, out string partsDesc);
            ParsekLog.Info(Tag, "real author: " + partsDesc);
            if (node == null) { ParsekLog.Info(Tag, "real: ABORT parts missing"); yield break; }
            PartParityAirPlan.SlotLatLon(0, 1, body.Radius, out double lat, out double lon);
            uint pid = GhostCoOrbitalRig.SpawnInAir(node, body, lat, lon, PartParityStateRules.AirSpawnAltitudeM, 0.0, "fxr-probe", Tag);
            Vessel v = null;
            yield return WaitLive(pid, x => v = x);
            if (v == null) { ParsekLog.Info(Tag, "real: ABORT never live pid=" + pid.ToString(Ic)); yield break; }
            FlightGlobals.ForceSetActiveVessel(v);
            float sw = Time.realtimeSinceStartup + 5f;
            while (FlightGlobals.ActiveVessel != v && Time.realtimeSinceStartup < sw) yield return null;
            yield return null; yield return null;
            string tp = Teleport(v, body);
            ParsekLog.Info(Tag, "real teleport: " + tp);
            float uw = Time.realtimeSinceStartup + 6f;
            while (v.packed && Time.realtimeSinceStartup < uw) yield return null;
            if (v.packed) { ParsekLog.Info(Tag, "real: still packed after the hold, GoOffRails"); v.GoOffRails(); yield return null; }
            yield return new WaitForSeconds(0.5f);
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            yield return null;
            bool sas = v.Autopilot != null && v.Autopilot.SetMode(VesselAutopilot.AutopilotMode.Retrograde);
            ParsekLog.Info(Tag, $"real unpacked={!v.packed} sasRetrograde={sas} " + Trace(v, host));
            DisableCameraFx();

            rootPartPid = v.rootPart.persistentId;
            Part tankPart = null;
            foreach (Part p in v.parts) if (p.partInfo.name == "fuelTank") tankPart = p;
            boosterPartPid = tankPart != null ? tankPart.persistentId : 0;
            uint probeVesselPid = v.persistentId;

            // A ghost of this very vessel 10 m beside it: does stock's fire paint a ghost?
            ConfigNode snap = FlightRecorder.TryBackupSnapshot(v);
            if (snap != null && GhostCoOrbitalRig.TryBuildGhostFromSnapshot(snap, "fxr-side-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    "ParsekTest_fxr_side", null, v.vesselName, v.persistentId, Planetarium.GetUniversalTime(), out sideGhost, out string fail))
            {
                Transform g = sideGhost.Build.root.transform;
                g.SetParent(v.rootPart.transform, false);
                g.localPosition = new Vector3(0f, 0f, SideGhostOffset);
                g.localRotation = Quaternion.identity;
                GhostCoOrbitalRig.NeutralisePhysics(sideGhost.Build.root, out int _, out int _);
                sideGhost.Build.root.SetActive(true);
                ParsekLog.Info(Tag, "real side ghost renderers: " + PartParityBurstSpikeInGameTests.DescribeRenderers(sideGhost.Build.root));
            }
            else ParsekLog.Info(Tag, "real side ghost: not built");
            ParsekLog.Info(Tag, "real vessel renderers: " + PartParityBurstSpikeInGameTests.DescribeRenderers(v.gameObject));

            flight.StartRecording();
            double startUt = Planetarium.GetUniversalTime();
            ParsekLog.Info(Tag, $"real recording started ut={startUt.ToString("R", Ic)} rootPart={rootPartPid.ToString(Ic)} boosterPart={boosterPartPid.ToString(Ic)} vesselPid={probeVesselPid.ToString(Ic)}");
            if (FlightCamera.fetch != null) FlightCamera.fetch.DeactivateUpdate();
            SetPose(v.rootPart.transform, Vector3.right, new Vector3(0f, 0f, SideGhostOffset * 0.5f), MainDist);

            var done = new HashSet<string>();
            double maxMach = 0, nextTrace = 0;
            bool staged = false;
            uint boosterVesselPid = 0;
            string[] order = { "above", "m6", "m375", "m3", "m25", "m2" };
            while (true)
            {
                yield return null;
                if (v == null || v.state == Vessel.State.DEAD || v.rootPart == null) { ParsekLog.Info(Tag, "real: probe lost"); break; }
                double ut = Planetarium.GetUniversalTime();
                if (ut >= nextTrace) { nextTrace = ut + 1.0; ParsekLog.Info(Tag, "trace " + Trace(v, host)); }
                if (v.altitude < 70000.0) maxMach = Math.Max(maxMach, v.mach);
                if (!staged && v.altitude <= StageAltM)
                {
                    staged = true;
                    Part decPart = null;
                    foreach (Part p in v.parts) if (p.FindModuleImplementing<ModuleDecouplerBase>() != null) decPart = p;
                    ModuleDecouplerBase dm = decPart != null ? decPart.FindModuleImplementing<ModuleDecouplerBase>() : null;
                    BaseEvent de = dm != null ? dm.Events["Decouple"] : null;
                    if (de != null) de.Invoke(); else if (dm != null) dm.Decouple();
                    yield return null; yield return null;
                    Vessel bv = FindLoadedVesselWithPart(boosterPartPid, v);
                    boosterVesselPid = bv != null ? bv.persistentId : 0;
                    ParsekLog.Info(Tag, $"real staged at alt={v.altitude.ToString("0", Ic)} decoupler={(dm != null)} boosterVessel={boosterVesselPid.ToString(Ic)} " + Trace(v, host));
                    continue;
                }
                string mark = null;
                foreach (string m in order)
                {
                    if (done.Contains(m)) continue;
                    bool hit = m == "above" ? v.altitude <= 72000.0
                        : m == "m6" ? maxMach > 6.5 && v.mach <= 6.0
                        : m == "m375" ? maxMach > 4.0 && v.mach <= 3.75
                        : m == "m3" ? maxMach > 3.2 && v.mach <= 3.0
                        : m == "m25" ? maxMach > 2.7 && v.mach <= 2.5
                        : maxMach > 2.2 && v.mach <= 2.0;
                    if (hit) { mark = m; break; }
                }
                if (mark != null)
                {
                    // Earlier marks the descent skipped past are recorded as missed.
                    foreach (string m in order) { if (m == mark) break; if (done.Add(m)) ParsekLog.Info(Tag, "real mark " + m + " missed"); }
                    done.Add(mark);
                    yield return MarkSequence(mark, v, host);
                }
                bool end = (maxMach > 2.2 && v.mach <= 1.5) || v.altitude < 6000.0 || ut > RealLegTimeoutUt;
                if (end) { ParsekLog.Info(Tag, $"real end: mach={v.mach.ToString("0.00", Ic)} alt={v.altitude.ToString("0", Ic)} ut={ut.ToString("0.0", Ic)} maxMach={maxMach.ToString("0.00", Ic)}"); break; }
            }

            poseFrame = null;
            if (FlightCamera.fetch != null) FlightCamera.fetch.ActivateUpdate();
            string treeId = flight.ActiveTreeForDisplay != null ? flight.ActiveTreeForDisplay.Id : null;
            flight.StopRecording();
            flight.CommitTreeFlight();
            flight.StopRecording();
            if (flight.HasActiveTree) flight.AutoDiscardActiveTreeWithMessage("fxr-rig", "fxr rig", "fxr-rig", false);
            flight.SuppressCommittedSpawnedRestoreForActiveVessel("fxr-rig");
            var recDesc = new StringBuilder();
            foreach (RecordingTree t in RecordingStore.CommittedTrees)
            {
                if (t.Id != treeId) continue;
                foreach (Recording r in t.Recordings.Values)
                {
                    r.TerminalStateValue = TerminalState.Destroyed;
                    r.VesselSnapshot = null;
                    bool isMain = !r.IsDebris && r.VesselPersistentId == probeVesselPid;
                    bool isBooster = boosterVesselPid != 0 && r.VesselPersistentId == boosterVesselPid;
                    if (isMain) mainRecIds.Add(r.RecordingId);
                    else if (isBooster || r.IsDebris) boosterRecIds.Add(r.RecordingId);
                    recDesc.Append($"{r.RecordingId}:{r.VesselName}:pid={r.VesselPersistentId.ToString(Ic)}:debris={r.IsDebris}:{(isMain ? "main" : isBooster ? "booster" : "other")}; ");
                }
            }
            ParsekLog.Info(Tag, $"real committed tree={treeId} main={mainRecIds.Count.ToString(Ic)} booster={boosterRecIds.Count.ToString(Ic)} recs=[{recDesc}]");
            if (sideGhost != null) { GhostCoOrbitalRig.DestroyLiveGhost(sideGhost); sideGhost = null; }
            ModuleParachute chute = v != null ? v.FindPartModuleImplementing<ModuleParachute>() : null;
            if (chute != null) { chute.Deploy(); ParsekLog.Info(Tag, "real chute armed"); }
            RestoreCameraFx();
            RestoreAeroQuality("real leg done");
            GameEvents.onShowUI.Fire();
            ParsekLog.Info(Tag, $"REAL DONE shots={shots.Count.ToString(Ic)} storedFrames={realPx.Count.ToString(Ic)} files={files.ToString(Ic)} ut={Planetarium.GetUniversalTime().ToString("0.0", Ic)}");
            // U7 tail: the rest of the descent, until the rewind ends this scene.
            while (v != null && v.state != Vessel.State.DEAD)
            {
                yield return new WaitForSeconds(5f);
                if (v != null && v.rootPart != null) ParsekLog.Info(Tag, $"tail alt={v.altitude.ToString("0", Ic)} srfSpeed={v.srfSpeed.ToString("0.0", Ic)} parts={v.parts.Count.ToString(Ic)} sit={v.situation}");
            }
        }

        private Shot NewShot(string mark, string kind, bool booster, Vector3 dir, Vector3 focus, float dist, bool onGhost, bool ghostFxOff)
        {
            var s = new Shot
            {
                Label = "fxr-" + mark + "-" + kind, Mark = mark, Kind = kind, BoosterFrame = booster, DirLocal = dir,
                FocusLocal = focus, Dist = dist, OnGhost = onGhost, GhostFxOff = ghostFxOff,
            };
            shots.Add(s);
            return s;
        }

        /// <summary>The real side of one mark: main pose (A, 3 more A, B = aero cameras off,
        /// B2 = and the light, C1 / C2 = and blackbody off, D = restored), the rear pose (A, B),
        /// then the booster sweep.</summary>
        private IEnumerator MarkSequence(string mark, Vessel v, Vessel host)
        {
            ParsekLog.Info(Tag, $"mark {mark} begin " + Trace(v, host));
            Vector3 mainFocus = new Vector3(0f, 0f, SideGhostOffset * 0.5f);
            SetPose(v.rootPart.transform, Vector3.right, mainFocus, MainDist);
            yield return null; yield return null;
            var frames = new Dictionary<string, Color32[]>();
            var rects = new Dictionary<string, string>();
            int w = 0, h = 0;
            Vessel booster = FindLoadedVesselWithPart(boosterPartPid, v);

            Action<Shot, Color32[], int, int> keep = (s, px, ww, hh) =>
            {
                w = ww; h = hh; realW = ww; realH = hh;
                s.Ut = Planetarium.GetUniversalTime();
                // The side ghost is parented under the root part, so RenderersOf(v) also returns
                // its renderers; PPS-20c's probe rect was the union of both (230 px wide against
                // the ghost leg's 36 px), which read as a framing difference. Keep the probe alone.
                List<Renderer> probeOnly = RenderersOf(v);
                if (sideGhost != null && sideGhost.Build.root != null)
                    probeOnly.RemoveAll(r => r == null || r.transform.IsChildOf(sideGhost.Build.root.transform));
                RectInt rp = ScreenRect(probeOnly, ww, hh);
                RectInt rb = ScreenRect(RenderersOf(booster), ww, hh);
                RectInt rg = ScreenRect(sideGhost != null ? RenderersOf(sideGhost.Build.root) : new List<Renderer>(), ww, hh);
                s.Masks.Add(rp); s.Masks.Add(rb); s.Masks.Add(rg);
                frames[s.Kind] = px;
                rects[s.Kind] = $"probe={R(rp)} booster={R(rb)} sideGhost={R(rg)}";
            };

            string[] mainKinds = { "mainA", "mainA2", "mainA3", "mainA4", "mainB", "mainB2", "mainC1", "mainC2", "mainD" };
            foreach (string k in mainKinds)
            {
                bool ghostSide = k == "mainA" || k == "mainB";
                Shot s = NewShot(mark, k, false, Vector3.right, mainFocus, MainDist, ghostSide, k == "mainB");
                if (k == "mainB") SetAeroCameras(false, false);
                if (k == "mainB2") { SetAeroCameras(true, false); SetAeroCameras(false, true); }
                if (k == "mainC1") SetBlackbody(false);
                if (k == "mainD") { SetBlackbody(true); SetAeroCameras(true, false); }
                if (k != "mainA") yield return null;
                Color32[] got = null; int gw = 0, gh = 0;
                yield return Grab((px, ww, hh) => { got = px; gw = ww; gh = hh; });
                keep(s, got, gw, gh);
                if (k == "mainB") ParsekLog.Info(Tag, $"mark {mark} aero-off " + AeroLine());
            }
            SetBlackbody(true); SetAeroCameras(true, false);
            ParsekLog.Info(Tag, $"mark {mark} main rects: A {rects["mainA"]} | D {rects["mainD"]}");
            Func<string, string, string> cmp = (a, b) =>
            {
                Color32[] fa = frames.ContainsKey(a) ? frames[a] : null, fb = frames.ContainsKey(b) ? frames[b] : null;
                Shot sa = shots.Find(x => x.Mark == mark && x.Kind == a);
                if (fa == null || fb == null || sa == null) return a + "-" + b + "=n/a";
                return $"{a}-{b}: all={DiffAll(fa, fb).ToString(Ic)} probe={DiffIn(fa, fb, w, sa.Masks[0]).ToString(Ic)} booster={DiffIn(fa, fb, w, sa.Masks[1]).ToString(Ic)} " +
                    $"sideGhost={DiffIn(fa, fb, w, sa.Masks[2]).ToString(Ic)}";
            };
            ParsekLog.Info(Tag, $"mark {mark} isolation (aero = A-B, light = B-B2, blackbody = B2-C1, lag = C1-C2, restore = A-D, noise = A-A2..A4): " +
                $"{cmp("mainA", "mainB")} | {cmp("mainB", "mainB2")} | {cmp("mainB2", "mainC1")} | {cmp("mainC1", "mainC2")} | {cmp("mainA", "mainD")} | " +
                $"{cmp("mainA", "mainA2")} | {cmp("mainA", "mainA3")} | {cmp("mainA", "mainA4")}");

            // Rear quarter, from behind and above the trail.
            Vector3 rearDir = new Vector3(0.8f, 0.6f, 0.2f).normalized;
            SetPose(v.rootPart.transform, rearDir, Vector3.zero, RearDist);
            yield return null; yield return null;
            foreach (string k in new[] { "rearA", "rearB" })
            {
                Shot s = NewShot(mark, k, false, rearDir, Vector3.zero, RearDist, true, k == "rearB");
                if (k == "rearB") { SetAeroCameras(false, false); yield return null; }
                Color32[] got = null; int gw = 0, gh = 0;
                yield return Grab((px, ww, hh) => { got = px; gw = ww; gh = hh; });
                keep(s, got, gw, gh);
            }
            SetAeroCameras(true, false);
            ParsekLog.Info(Tag, $"mark {mark} rear: rects {rects["rearA"]} {cmp("rearA", "rearB")}");

            // Booster sweep: does stock draw fire on the staged booster, out to what distance.
            Part bp = booster != null ? FindPart(booster, boosterPartPid) : null;
            if (bp != null)
            {
                var sweep = new StringBuilder();
                foreach (float d in BoosterSweepM)
                {
                    string kOn = "bst" + d.ToString("0", Ic) + "A", kOff = "bst" + d.ToString("0", Ic) + "B";
                    SetPose(bp.transform, Vector3.right, Vector3.zero, d);
                    yield return null; yield return null;
                    Shot sOn = NewShot(mark, kOn, true, Vector3.right, Vector3.zero, d, d <= 150f, false);
                    Color32[] got = null; int gw = 0, gh = 0;
                    yield return Grab((px, ww, hh) => { got = px; gw = ww; gh = hh; });
                    keep(sOn, got, gw, gh);
                    string aeroAtOn = AeroLine();
                    Shot sOff = NewShot(mark, kOff, true, Vector3.right, Vector3.zero, d, false, false);
                    SetAeroCameras(false, false);
                    yield return null;
                    yield return Grab((px, ww, hh) => { got = px; gw = ww; gh = hh; });
                    keep(sOff, got, gw, gh);
                    SetAeroCameras(true, false);
                    Transform ct = FlightCamera.fetch.GetCameraTransform();
                    sweep.Append($"[d={d.ToString("0", Ic)} camToBooster={(ct.position - bp.transform.position).magnitude.ToString("0", Ic)} " +
                        $"camToProbe={(ct.position - v.transform.position).magnitude.ToString("0", Ic)} boosterToProbe={(bp.transform.position - v.transform.position).magnitude.ToString("0", Ic)} " +
                        $"rects {rects[kOn]} {cmp(kOn, kOff)} | {aeroAtOn}] ");
                }
                ParsekLog.Info(Tag, $"mark {mark} booster sweep: {sweep}");
            }
            else ParsekLog.Info(Tag, $"mark {mark} booster sweep: no loaded booster");
            SetPose(v.rootPart.transform, Vector3.right, mainFocus, MainDist);

            // Keep the frames the ghost leg compares; write the pictures.
            foreach (var kv in frames)
            {
                string label = "fxr-" + mark + "-" + kv.Key;
                if (kv.Key == "mainA" || kv.Key == "mainB" || kv.Key == "rearA" || kv.Key == "bst20A" || kv.Key == "bst60A")
                    realPx[label] = kv.Value;
                bool write = !(kv.Key == "mainA2" || kv.Key == "mainA3" || kv.Key == "mainA4" || kv.Key == "mainC2" || kv.Key.EndsWith("B", StringComparison.Ordinal) && kv.Key.StartsWith("bst", StringComparison.Ordinal));
                if (write) Png("fxr-real-" + mark + "-" + kv.Key, kv.Value, w, h);
            }
            if (frames.ContainsKey("mainA") && frames.ContainsKey("mainB"))
                Png("fxr-real-" + mark + "-aeroAB", DiffImage(frames["mainA"], frames["mainB"]), w, h);
            if (frames.ContainsKey("mainB2") && frames.ContainsKey("mainC1"))
                Png("fxr-real-" + mark + "-blackbody", DiffImage(frames["mainB2"], frames["mainC1"]), w, h);
            ParsekLog.Info(Tag, $"mark {mark} end " + Trace(v, host));
        }

        // ===================================================================================
        // ghost leg
        // ===================================================================================

        private static Transform FindGhostPart(GameObject ghost, uint pid)
        {
            if (ghost == null) return null;
            string want = "ghost_part_" + pid.ToString(Ic);
            foreach (Transform t in ghost.GetComponentsInChildren<Transform>(false))
                if (t.name == want) return t;
            return null;
        }

        private static bool FindGhost(HashSet<string> ids, uint partPid, out int index, out GhostPlaybackState state, out Transform part)
        {
            index = -1; state = null; part = null;
            ParsekFlight f = ParsekFlight.Instance;
            if (f == null) return false;
            foreach (var kv in f.ghostStates)
            {
                GhostPlaybackState s = kv.Value;
                if (s == null || s.ghost == null || !s.ghost.activeInHierarchy) continue;
                if (ids.Count > 0 && !ids.Contains(s.recordingId)) continue;
                Transform t = FindGhostPart(s.ghost, partPid);
                if (t == null) continue;
                index = kv.Key; state = s; part = t;
                return true;
            }
            return false;
        }

        private float lastWatchTry = -10f;
        private float lastWatchWhyLog = -10f;

        private void EnsureWatch(int index)
        {
            ParsekFlight f = ParsekFlight.Instance;
            if (f == null || index < 0) return;
            if (f.IsWatchingGhost && f.WatchedRecordingIndex == index) return;
            if (Time.realtimeSinceStartup - lastWatchTry < 1f) return;
            lastWatchTry = Time.realtimeSinceStartup;
            f.EnterWatchMode(index);
            ParsekLog.Info(Tag, $"ghost watch enter index={index.ToString(Ic)} watching={f.IsWatchingGhost} watchedIndex={f.WatchedRecordingIndex.ToString(Ic)}");
        }

        /// <summary>
        /// An unwatched ghost 50 km or more from the active vessel is hidden by the distance LOD
        /// (GhostPlaybackLogic.ApplyDistanceLodPolicy, LoopSimplifiedMeters), and a hidden ghost
        /// is only prewarmed inside 55 km, so at mark `above` (about 75 km from the pad host)
        /// FindGhost, which needs an active ghost, could never see it and watch mode was never
        /// entered (PPS-20c: four MISSING shots, watch entered at about 50 km). Watch mode forces
        /// the watched ghost to full fidelity, so the main recording covering this UT is watched
        /// by its committed index, from its own ghost state, without waiting for a visible ghost.
        /// Returns a one-line reason per candidate recording.
        /// </summary>
        private string WatchMainByRecording(double ut)
        {
            ParsekFlight f = ParsekFlight.Instance;
            if (f == null) return "no flight";
            var sb = new StringBuilder();
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            int pick = -1;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording r = committed[i];
                if (r == null || !mainRecIds.Contains(r.RecordingId)) continue;
                f.ghostStates.TryGetValue(i, out GhostPlaybackState st);
                bool covers = r.StartUT <= ut && ut <= r.EndUT;
                sb.Append($"[#{i.ToString(Ic)} {r.RecordingId} ut={r.StartUT.ToString("0.0", Ic)}..{r.EndUT.ToString("0.0", Ic)} covers={covers} " +
                    $"state={(st != null)} ghost={(st != null && st.ghost != null)} active={(st != null && st.ghost != null && st.ghost.activeSelf)} " +
                    $"zone={(st != null ? st.currentZone.ToString() : "-")} dist={(st != null ? st.lastDistance : -1).ToString("0", Ic)}m] ");
                if (covers && st != null && pick < 0) pick = i;
            }
            if (pick >= 0) EnsureWatch(pick);
            sb.Append($"pick={pick.ToString(Ic)} watching={f.IsWatchingGhost} watchedIndex={f.WatchedRecordingIndex.ToString(Ic)}");
            return sb.ToString();
        }

        private static string GhostFlags(GhostPlaybackState s)
        {
            if (s == null) return "none";
            ReentryFxInfo r = s.reentryFxInfo;
            return $"rec={s.recordingId} fxSuppressed={s.visualFxSuppressed} lod={s.distanceLodReduced} fidelityReduced={s.fidelityReduced} " +
                $"reentryFx={(r != null)} pendingBuild={s.reentryFxPendingBuild} intensity={(r != null ? r.lastIntensity : -1f).ToString("0.000", Ic)} " +
                $"fireParticles={(r != null && r.fireParticles != null ? r.fireParticles.particleCount : -1).ToString(Ic)} shellMeshes={(r != null && r.fireShellMeshes != null ? r.fireShellMeshes.Count : -1).ToString(Ic)}";
        }

        private IEnumerator Ghost()
        {
            if (shots == null || shots.Count == 0) { ParsekLog.Info(Tag, "ghost: no real shots in this process"); yield break; }
            Vessel active = FlightGlobals.ActiveVessel;
            ParsekLog.Info(Tag, $"ghost env: active={(active != null ? active.vesselName : "none")} sit={(active != null ? active.situation.ToString() : "-")} " +
                $"srfSpeed={(active != null ? active.srfSpeed : -1).ToString("0.0", Ic)} ut={Planetarium.GetUniversalTime().ToString("0.00", Ic)} firstShotUt={shots[0].Ut.ToString("0.00", Ic)} " +
                $"mainRecs={mainRecIds.Count.ToString(Ic)} boosterRecs={boosterRecIds.Count.ToString(Ic)} {AeroLine()}");
            DisableCameraFx();
            RaiseAeroQuality("ghost");
            if (FlightCamera.fetch != null) FlightCamera.fetch.DeactivateUpdate();
            bool loggedRenderers = false;
            int captured = 0, late = 0, missing = 0;
            var ordered = new List<Shot>(shots);
            ordered.Sort((a, b) => a.Ut.CompareTo(b.Ut));
            foreach (Shot s in ordered)
            {
                if (!s.OnGhost) continue;
                GhostPlaybackState st = null; Transform frame = null; int idx = -1;
                // Wait for the shot's UT (one frame early, so a toggle reaches the next render).
                while (Planetarium.GetUniversalTime() + 1.5 * Time.deltaTime < s.Ut)
                {
                    if (FindGhost(mainRecIds, rootPartPid, out int mi, out GhostPlaybackState ms, out Transform mt))
                    {
                        EnsureWatch(mi);
                        if (!s.BoosterFrame) SetPose(mt, s.DirLocal, s.FocusLocal, s.Dist);
                    }
                    else
                    {
                        string why = WatchMainByRecording(Planetarium.GetUniversalTime());
                        if (Time.realtimeSinceStartup - lastWatchWhyLog >= 1f)
                        {
                            lastWatchWhyLog = Time.realtimeSinceStartup;
                            ParsekLog.Info(Tag, "ghost watch-by-recording ut=" + Planetarium.GetUniversalTime().ToString("0.00", Ic) + " " + why);
                        }
                    }
                    if (s.BoosterFrame && FindGhost(boosterRecIds, boosterPartPid, out int bi, out GhostPlaybackState bs, out Transform bt))
                        SetPose(bt, s.DirLocal, s.FocusLocal, s.Dist);
                    yield return null;
                }
                if (Planetarium.GetUniversalTime() > s.Ut + 0.1) { late++; ParsekLog.Info(Tag, $"ghost shot {s.Label} late ut={Planetarium.GetUniversalTime().ToString("0.000", Ic)} want={s.Ut.ToString("0.000", Ic)}"); continue; }
                bool found = s.BoosterFrame
                    ? FindGhost(boosterRecIds, boosterPartPid, out idx, out st, out frame)
                    : FindGhost(mainRecIds, rootPartPid, out idx, out st, out frame);
                if (!found)
                {
                    missing++;
                    ParsekLog.Info(Tag, $"ghost shot {s.Label} MISSING ghost ut={Planetarium.GetUniversalTime().ToString("0.000", Ic)} why: " +
                        WatchMainByRecording(Planetarium.GetUniversalTime()));
                    continue;
                }
                SetPose(frame, s.DirLocal, s.FocusLocal, s.Dist);
                List<ParticleSystemRenderer> offRenderers = null; List<FireShellMesh> savedShell = null; ReentryFxInfo info = st.reentryFxInfo;
                if (s.GhostFxOff && info != null)
                {
                    offRenderers = new List<ParticleSystemRenderer>();
                    if (info.fireParticles != null)
                    {
                        var pr = info.fireParticles.GetComponent<ParticleSystemRenderer>();
                        if (pr != null && pr.enabled) { pr.enabled = false; offRenderers.Add(pr); }
                    }
                    savedShell = info.fireShellMeshes;
                    info.fireShellMeshes = new List<FireShellMesh>();
                }
                yield return null;
                Color32[] px = null; int w = 0, h = 0;
                yield return Grab((p, ww, hh) => { px = p; w = ww; h = hh; });
                double ut = Planetarium.GetUniversalTime();
                if (offRenderers != null) foreach (var r in offRenderers) if (r != null) r.enabled = true;
                if (savedShell != null && info != null) info.fireShellMeshes = savedShell;
                captured++;
                if (!loggedRenderers)
                {
                    loggedRenderers = true;
                    ParsekLog.Info(Tag, "ghost renderers (main): " + PartParityBurstSpikeInGameTests.DescribeRenderers(st.ghost));
                    if (FindGhost(boosterRecIds, boosterPartPid, out int _, out GhostPlaybackState b0, out Transform _))
                        ParsekLog.Info(Tag, "ghost renderers (booster): " + PartParityBurstSpikeInGameTests.DescribeRenderers(b0.ghost));
                }
                Vessel av = FlightGlobals.ActiveVessel;
                double dist = av != null ? (frame.position - av.transform.position).magnitude : -1;
                ParsekFlight f = ParsekFlight.Instance;
                FindGhost(mainRecIds, rootPartPid, out int _, out GhostPlaybackState mainSt, out Transform mainT);
                FindGhost(boosterRecIds, boosterPartPid, out int _, out GhostPlaybackState bSt, out Transform bT);
                RectInt rm = ScreenRect(mainSt != null ? RenderersOf(mainSt.ghost) : new List<Renderer>(), w, h);
                RectInt rb = ScreenRect(bSt != null ? RenderersOf(bSt.ghost) : new List<Renderer>(), w, h);
                var masks = new List<RectInt>(s.Masks) { rm, rb };
                string cmp = "no real frame stored";
                if (realPx.TryGetValue(s.Label, out Color32[] real) && real != null && real.Length == px.Length)
                {
                    cmp = "real-vs-ghost outside masks: " + DiffOutside(real, px, w, h, masks) +
                        $" | inside probe rect diff={DiffIn(real, px, w, s.Masks[0]).ToString(Ic)} ghostRect diff={DiffIn(real, px, w, rm).ToString(Ic)}";
                    Png("fxr-diff-" + s.Mark + "-" + s.Kind, DiffImage(real, px), w, h);
                }
                Png("fxr-ghost-" + s.Mark + "-" + s.Kind, px, w, h);
                ParsekLog.Info(Tag, $"ghost shot {s.Label} ut={ut.ToString("0.000", Ic)} dUt={(ut - s.Ut).ToString("0.000", Ic)} fxOff={s.GhostFxOff} index={idx.ToString(Ic)} " +
                    $"watching={(f != null && f.IsWatchingGhost)} watchedIndex={(f != null ? f.WatchedRecordingIndex : -1).ToString(Ic)} " +
                    $"ghostToActive={dist.ToString("0", Ic)}m withinWatchCutoff={(dist >= 0 && dist <= DistanceThresholds.GhostFlight.GetWatchCameraCutoffMeters())} " +
                    $"withinVisualRange={(dist >= 0 && dist <= DistanceThresholds.GhostVisualRangeMeters)} rects main={R(rm)} booster={R(rb)} " +
                    $"flags[{GhostFlags(st)}] {cmp} | {AeroLine()}");
                // Release the stored real frame once compared (memory).
                realPx.Remove(s.Label);
            }
            poseFrame = null;
            if (FlightCamera.fetch != null) FlightCamera.fetch.ActivateUpdate();
            RestoreCameraFx();
            RestoreAeroQuality("ghost leg done");
            GameEvents.onShowUI.Fire();
            ParsekLog.Info(Tag, $"GHOST DONE captured={captured.ToString(Ic)} late={late.ToString(Ic)} missing={missing.ToString(Ic)} files={files.ToString(Ic)} ut={Planetarium.GetUniversalTime().ToString("0.0", Ic)}");
        }
    }
}
