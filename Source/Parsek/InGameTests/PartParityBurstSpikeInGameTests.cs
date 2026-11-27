using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Parsek.TestCommands;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>Draws one mesh with Graphics.DrawMesh from Update (mode 1) or LateUpdate
    /// (mode 2), the way the ghost reentry shell is submitted. THROWAWAY X0 spike instrument.</summary>
    internal sealed class BurstSpikeDrawMeshProbe : MonoBehaviour
    {
        public Mesh Mesh;
        public Material Mat;
        public Matrix4x4 Matrix;
        public MaterialPropertyBlock Mpb;
        public int Mode;

        private void Update()
        {
            if (Mode == 1 && Mesh != null && Mat != null) Graphics.DrawMesh(Mesh, Matrix, Mat, 0, null, 0, Mpb);
        }

        private void LateUpdate()
        {
            if (Mode == 2 && Mesh != null && Mat != null) Graphics.DrawMesh(Mesh, Matrix, Mat, 0, null, 0, Mpb);
        }
    }

    /// <summary>Joint break / decouple GameEvent log for one case (instance handlers: KSP's
    /// EventData refuses static methods). THROWAWAY X0 spike instrument.</summary>
    internal sealed class BurstSpikeEvents
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        public readonly List<string> Lines = new List<string>();
        public int JointBreaks, Decouples, DecoupleCompletes;
        private EventData<PartJoint, float>.OnEvent onBreak;
        private EventData<Part>.OnEvent onDecouple;
        private EventData<Part>.OnEvent onDecoupleComplete;

        public void Subscribe()
        {
            onBreak = OnBreak;
            onDecouple = OnDecouple;
            onDecoupleComplete = OnDecoupleComplete;
            GameEvents.onPartJointBreak.Add(onBreak);
            GameEvents.onPartDeCouple.Add(onDecouple);
            GameEvents.onPartDeCoupleComplete.Add(onDecoupleComplete);
        }

        public void Unsubscribe()
        {
            if (onBreak != null) GameEvents.onPartJointBreak.Remove(onBreak);
            if (onDecouple != null) GameEvents.onPartDeCouple.Remove(onDecouple);
            if (onDecoupleComplete != null) GameEvents.onPartDeCoupleComplete.Remove(onDecoupleComplete);
            onBreak = null; onDecouple = null; onDecoupleComplete = null;
        }

        private static string P(Part p) => p == null ? "null" : $"{p.partInfo?.name}#{p.persistentId.ToString(Ic)}";

        private void OnBreak(PartJoint j, float force)
        {
            JointBreaks++;
            Lines.Add($"onPartJointBreak frame={Time.frameCount.ToString(Ic)} child={P(j?.Child)} parent={P(j?.Parent)} host={P(j?.Host)} target={P(j?.Target)} force={force.ToString("0.###", Ic)}");
        }

        private void OnDecouple(Part p)
        {
            Decouples++;
            Lines.Add($"onPartDeCouple frame={Time.frameCount.ToString(Ic)} part={P(p)}");
        }

        private void OnDecoupleComplete(Part p)
        {
            DecoupleCompletes++;
            Lines.Add($"onPartDeCoupleComplete frame={Time.frameCount.ToString(Ic)} part={P(p)}");
        }
    }

    /// <summary>PNG output for the X0 spike through the repo's own writer (no
    /// ImageConversionModule reference). THROWAWAY.</summary>
    internal static class SpikePng
    {
        /// <summary>Writes Screenshots/<paramref name="file"/> from a bottom-up Color32 image.</summary>
        internal static bool Write(string file, int w, int h, Color32[] bottomUp, string tag)
        {
            try
            {
                var bytes = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                {
                    int src = (h - 1 - y) * w;
                    for (int x = 0; x < w; x++)
                    {
                        Color32 c = bottomUp[src + x];
                        int o = (y * w + x) * 4;
                        bytes[o] = c.r; bytes[o + 1] = c.g; bytes[o + 2] = c.b; bytes[o + 3] = 255;
                    }
                }
                byte[] png = PartParityPng.EncodeRgba(w, h, bytes);
                string dir = Path.Combine(KSPUtil.ApplicationRootPath ?? string.Empty, "Screenshots");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, file), png);
                ParsekLog.Info(tag, "wrote Screenshots/" + file + " " + w.ToString(CultureInfo.InvariantCulture) + "x" +
                    h.ToString(CultureInfo.InvariantCulture) + " bytes=" + png.Length.ToString(CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Info(tag, "png " + file + ": write failed " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// P9 X0 SPIKE (design-part-ghost-parity.md Appendix G, G.6 U1 to U5). THROWAWAY, never
    /// merged. On the pad host (b1-pad-craft) it answers: U1 FXMonger's prefab table and its
    /// merge rule (same frame / consecutive frames / queue lifetime), U2 whether a new
    /// explosionObjects entry is visible in the frame it spawns and carries the prefab name,
    /// U3 the decoupler's `decouple` FXGroup burst and onPartJointBreak against a plain
    /// Part.decouple(), U4 ModuleSurfaceFX / SurfaceFX on a held burning LV-T45 over terrain,
    /// ocean and the pad plus the LaunchpadFX colliders, U5 whether Graphics.DrawMesh reaches a
    /// manual Camera.Render. Greppable `[PartParityBurstSpike]` Info lines and
    /// Screenshots/ppb-*.png atlases from an ortho capture camera.
    /// </summary>
    public class PartParityBurstSpikeInGameTests
    {
        private readonly InGameTestRunner runner;
        public PartParityBurstSpikeInGameTests(InGameTestRunner runner) { this.runner = runner; }

        internal const string Tag = "PartParityBurstSpike";
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        private const int Tile = 256;
        private const int CovThreshold = 16;
        private const float BurstWindowS = 4f;
        private const float DecoupleWindowS = 2f;
        private const float BinS = 0.25f;
        private const float SpawnLiveTimeoutS = 30f;
        // Layers no part effect sits on: 9 atmosphere, 10 scaled scenery, 18 skybox.
        private const int SceneryMask = ~((1 << 9) | (1 << 10) | (1 << 18));

        private static int filesWritten;
        private static List<string> failures;
        private static List<string> exceptions;

        /// <summary>Steps <paramref name="inner"/> and catches what it throws: logs the
        /// exception type, message and full stack (the runner records only the message), notes it,
        /// and ends just this step so the next sub-case still runs.</summary>
        private static IEnumerator Guarded(string label, IEnumerator inner)
        {
            while (true)
            {
                object cur;
                try
                {
                    if (!inner.MoveNext()) yield break;
                    cur = inner.Current;
                }
                catch (Exception ex)
                {
                    exceptions.Add(label + ": " + ex.GetType().FullName);
                    failures.Add(label + ": threw " + ex.GetType().Name);
                    ParsekLog.Warn(Tag, $"EXCEPTION in {label}: {ex.GetType().FullName}: '{ex.Message}' frame={Time.frameCount.ToString(Ic)}\n{ex}");
                    yield break;
                }
                yield return cur;
            }
        }

        // ===================================================================================
        // capture camera
        // ===================================================================================

        private sealed class Cap : IDisposable
        {
            public GameObject Go;
            public Camera Cam;
            public RenderTexture Rt;
            public Texture2D Rb;
            public int Size;
            public int Renders;
            public double RenderMs;

            public static Cap Create(int size)
            {
                var c = new Cap { Size = size };
                c.Go = new GameObject("PartParityBurstSpikeCamera");
                c.Cam = c.Go.AddComponent<Camera>();
                c.Cam.enabled = false;
                c.Cam.orthographic = true;
                c.Cam.clearFlags = CameraClearFlags.SolidColor;
                c.Cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                c.Cam.renderingPath = RenderingPath.Forward;
                c.Cam.allowHDR = false;
                c.Cam.allowMSAA = false;
                c.Cam.useOcclusionCulling = false;
                c.Rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
                c.Rt.Create();
                c.Rb = new Texture2D(size, size, TextureFormat.RGBA32, false);
                return c;
            }

            public void Aim(Vector3 target, Vector3 viewDir, Vector3 up, float half, float dist)
            {
                Vector3 d = viewDir.normalized;
                Cam.transform.position = target - d * dist;
                Cam.transform.rotation = Quaternion.LookRotation(d, up);
                Cam.orthographicSize = half;
                Cam.nearClipPlane = 0.1f;
                Cam.farClipPlane = dist * 2f;
            }

            public Color32[] Render(int mask)
            {
                Cam.cullingMask = mask;
                Cam.targetTexture = Rt;
                RenderTexture prev = RenderTexture.active;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Cam.Render();
                RenderMs += sw.Elapsed.TotalMilliseconds;
                Renders++;
                RenderTexture.active = Rt;
                Rb.ReadPixels(new Rect(0, 0, Size, Size), 0, 0, false);
                RenderTexture.active = prev;
                Cam.targetTexture = null;
                return Rb.GetPixels32();
            }

            public void Dispose()
            {
                if (Go != null) UnityEngine.Object.Destroy(Go);
                if (Rt != null) { Rt.Release(); UnityEngine.Object.Destroy(Rt); }
                if (Rb != null) UnityEngine.Object.Destroy(Rb);
            }
        }

        private static int Cov(Color32[] px)
        {
            int n = 0;
            for (int i = 0; i < px.Length; i++)
            {
                Color32 c = px[i];
                if (c.r >= CovThreshold || c.g >= CovThreshold || c.b >= CovThreshold) n++;
            }
            return n;
        }

        private static int DiffCount(Color32[] a, Color32[] b)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (Math.Abs(a[i].r - b[i].r) >= CovThreshold || Math.Abs(a[i].g - b[i].g) >= CovThreshold ||
                    Math.Abs(a[i].b - b[i].b) >= CovThreshold) n++;
            }
            return n;
        }

        private static Color32[] DiffTile(Color32[] a, Color32[] b)
        {
            var o = new Color32[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                o[i] = new Color32((byte)Math.Min(255, 4 * Math.Abs(a[i].r - b[i].r)),
                    (byte)Math.Min(255, 4 * Math.Abs(a[i].g - b[i].g)),
                    (byte)Math.Min(255, 4 * Math.Abs(a[i].b - b[i].b)), 255);
            }
            return o;
        }

        private static void MaxInto(Color32[] acc, Color32[] px)
        {
            for (int i = 0; i < acc.Length; i++)
            {
                Color32 a = acc[i], p = px[i];
                acc[i] = new Color32(Math.Max(a.r, p.r), Math.Max(a.g, p.g), Math.Max(a.b, p.b), 255);
            }
        }

        private static Color32[] Black(int n)
        {
            var o = new Color32[n];
            for (int i = 0; i < n; i++) o[i] = new Color32(0, 0, 0, 255);
            return o;
        }

        /// <summary>One PNG of tile rows (row 0 at the top), alpha forced to 255.</summary>
        private static void WriteAtlas(string file, List<List<Color32[]>> rows)
        {
            int cols = 1;
            foreach (var r in rows) cols = Math.Max(cols, r.Count);
            int nRows = Math.Max(1, rows.Count);
            int w = cols * Tile, h = nRows * Tile;
            var img = Black(w * h);
            for (int ri = 0; ri < rows.Count; ri++)
            {
                for (int ci = 0; ci < rows[ri].Count; ci++)
                {
                    Color32[] t = rows[ri][ci];
                    if (t == null) continue;
                    int x0 = ci * Tile, y0 = (nRows - 1 - ri) * Tile;
                    for (int y = 0; y < Tile; y++)
                        for (int x = 0; x < Tile; x++)
                        {
                            Color32 c = t[y * Tile + x];
                            img[(y0 + y) * w + x0 + x] = new Color32(c.r, c.g, c.b, 255);
                        }
                }
            }
            if (SpikePng.Write(file, w, h, img, Tag)) filesWritten++;
        }

        // ===================================================================================
        // entry point
        // ===================================================================================

        [InGameTest(Category = "PartParityBurstSpike", Scene = GameScenes.FLIGHT,
            Description = "P9 X0 spike: FXMonger bursts, decouple FX, launch dust, DrawMesh in a manual render; Screenshots/ppb-*.png")]
        public IEnumerator BurstSpike()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext()) yield return unpackWait.Current;
            Vessel host = FlightGlobals.ActiveVessel;
            if (host == null) InGameAssert.Skip("no active vessel");
            if (!(host.situation == Vessel.Situations.PRELAUNCH || host.situation == Vessel.Situations.LANDED))
                InGameAssert.Skip("needs a host on the pad");
            exceptions = new List<string>();
            failures = new List<string>();
            IEnumerator body = Guarded("RunAll", RunAll(host));
            while (body.MoveNext()) yield return body.Current;
            if (exceptions.Count > 0)
                InGameAssert.IsTrue(false, $"{exceptions.Count.ToString(Ic)} sub-case(s) threw (stacks in KSP.log, tag {Tag}): {string.Join("; ", exceptions.ToArray())}");
        }

        private IEnumerator RunAll(Vessel host)
        {
            filesWritten = 0;
            bool cheatBefore = CheatOptions.InfinitePropellant;
            CheatOptions.InfinitePropellant = true;
            ParsekLog.Info(Tag, $"env: host={host.vesselName} sit={host.situation} lat={host.latitude.ToString("0.00000", Ic)} lon={host.longitude.ToString("0.00000", Ic)} " +
                $"alt={host.altitude.ToString("0.0", Ic)} gfx={SystemInfo.graphicsDeviceType} unity={Application.unityVersion} " +
                $"surfaceFx={GameSettings.SURFACE_FX} krakensbane={Krakensbane.GetFrameVelocity().magnitude.ToString("0.0", Ic)} " +
                $"cheat InfinitePropellant {cheatBefore} -> {CheatOptions.InfinitePropellant}");
            Cap cap = Cap.Create(Tile);
            var sections = new List<string>();
            try
            {
                // Each section is guarded on its own: one throwing section no longer loses the rest.
                sections.Add("U5");
                IEnumerator u5 = Guarded("U5", DrawMeshSection(cap, host));
                while (u5.MoveNext()) yield return u5.Current;
                sections.Add("U1U2");
                IEnumerator u1 = Guarded("U1U2", FxMongerSection(cap, host));
                while (u1.MoveNext()) yield return u1.Current;
                sections.Add("U3");
                IEnumerator u3 = Guarded("U3", DecoupleSection(cap, host));
                while (u3.MoveNext()) yield return u3.Current;
                sections.Add("U4");
                IEnumerator u4 = Guarded("U4", DustSection(cap, host));
                while (u4.MoveNext()) yield return u4.Current;
            }
            finally
            {
                CheatOptions.InfinitePropellant = cheatBefore;
                ParsekLog.Info(Tag, $"cheat InfinitePropellant restored -> {CheatOptions.InfinitePropellant}; renders={cap.Renders.ToString(Ic)} " +
                    $"renderMeanMs={(cap.RenderMs / Math.Max(1, cap.Renders)).ToString("0.00", Ic)}");
                cap.Dispose();
            }
            ParsekLog.Info(Tag, $"summary: sections={string.Join(",", sections.ToArray())} files={filesWritten.ToString(Ic)} failures={failures.Count.ToString(Ic)} [{string.Join("; ", failures.ToArray())}] exceptions={exceptions.Count.ToString(Ic)}");
        }

        // ===================================================================================
        // geometry helpers
        // ===================================================================================

        private static Vector3 SlotPoint(CelestialBody body, int index, int count, double alt, out double lat, out double lon)
        {
            PartParityAirPlan.SlotLatLon(index, count, body.Radius, out lat, out lon);
            return body.GetWorldSurfacePosition(lat, lon, alt);
        }

        private static void Frame(CelestialBody body, Vector3 p, out Vector3 up, out Vector3 north, out Vector3 east)
        {
            up = (p - (Vector3)body.position).normalized;
            Vector3 np = body.GetWorldSurfacePosition(body.GetLatitude(p) + 0.001, body.GetLongitude(p), body.GetAltitude(p));
            north = Vector3.ProjectOnPlane(np - p, up).normalized;
            east = Vector3.Cross(up, north).normalized;
        }

        private static string V(Vector3 v) => $"({v.x.ToString("0.00", Ic)},{v.y.ToString("0.00", Ic)},{v.z.ToString("0.00", Ic)})";

        /// <summary>A point as metres north / east / up of an origin.</summary>
        private static string Neu(Vector3 p, Vector3 origin, Vector3 up, Vector3 north, Vector3 east)
        {
            Vector3 d = p - origin;
            return $"(n={Vector3.Dot(d, north).ToString("0.0", Ic)},e={Vector3.Dot(d, east).ToString("0.0", Ic)},u={Vector3.Dot(d, up).ToString("0.0", Ic)})";
        }

        private static IEnumerator Seconds(float s)
        {
            float end = Time.realtimeSinceStartup + s;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        private static string Path0(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; i < 4 && t != null; i++, t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts.ToArray());
        }

        internal static string DescribeRenderers(GameObject root)
        {
            if (root == null) return "none";
            var hist = new SortedDictionary<string, int>();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                Material m = r.sharedMaterial;
                string key = r.GetType().Name + ":" + (m != null && m.shader != null ? m.shader.name : "<none>") +
                    ":L" + r.gameObject.layer.ToString(Ic) + ":rt=" + (m != null ? m.GetTag("RenderType", false, "-") : "-") +
                    (r.enabled ? "" : ":off");
                hist.TryGetValue(key, out int c); hist[key] = c + 1;
            }
            var sb = new StringBuilder();
            foreach (var kv in hist) sb.Append(kv.Key).Append('x').Append(kv.Value.ToString(Ic)).Append(';');
            return sb.ToString();
        }

        private static string DescribeSystems(GameObject root)
        {
            if (root == null) return "none";
            var sb = new StringBuilder();
            int n = 0;
            foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps == null) continue;
                if (n++ >= 12) { sb.Append("..."); break; }
                var main = ps.main;
                sb.Append(ps.name).Append("{space=").Append(main.simulationSpace)
                  .Append(" life=").Append(main.startLifetime.constantMax.ToString("0.00", Ic))
                  .Append(" dur=").Append(main.duration.ToString("0.00", Ic))
                  .Append(" loop=").Append(main.loop)
                  .Append(" size=").Append(main.startSize.constantMax.ToString("0.0", Ic))
                  .Append(" speed=").Append(main.startSpeed.constantMax.ToString("0.0", Ic))
                  .Append(" n=").Append(ps.particleCount.ToString(Ic))
                  .Append(" playing=").Append(ps.isPlaying).Append("} ");
            }
            int ksp = root.GetComponentsInChildren<KSPParticleEmitter>(true).Length;
            int lights = root.GetComponentsInChildren<Light>(true).Length;
            return $"systems={n.ToString(Ic)} kspEmitters={ksp.ToString(Ic)} lights={lights.ToString(Ic)} [{sb}]";
        }

        private static int LayerMaskOf(IEnumerable<GameObject> roots)
        {
            int mask = 0;
            foreach (GameObject g in roots)
            {
                if (g == null) continue;
                foreach (Renderer r in g.GetComponentsInChildren<Renderer>(true))
                    if (r != null) mask |= 1 << r.gameObject.layer;
            }
            return mask;
        }

        private static int ParticleCount(IEnumerable<GameObject> roots)
        {
            int n = 0;
            foreach (GameObject g in roots)
            {
                if (g == null) continue;
                foreach (ParticleSystem ps in g.GetComponentsInChildren<ParticleSystem>(true))
                    if (ps != null) n += ps.particleCount;
            }
            return n;
        }

        private static readonly ParticleSystem.Particle[] Buf = new ParticleSystem.Particle[4096];

        /// <summary>The 95th percentile distance of the live particles from a source point.</summary>
        private static float ExtentP95(IEnumerable<GameObject> roots, Vector3 src)
        {
            var d = new List<float>();
            foreach (GameObject g in roots)
            {
                if (g == null) continue;
                foreach (ParticleSystem ps in g.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (ps == null || ps.particleCount == 0) continue;
                    int n = ps.GetParticles(Buf);
                    bool world = ps.main.simulationSpace == ParticleSystemSimulationSpace.World;
                    for (int i = 0; i < n && d.Count < 4000; i++)
                    {
                        Vector3 wp = world ? Buf[i].position : ps.transform.TransformPoint(Buf[i].position);
                        d.Add((wp - src).magnitude);
                    }
                }
            }
            if (d.Count == 0) return 0f;
            d.Sort();
            return d[Math.Min(d.Count - 1, (int)(0.95 * d.Count))];
        }

        // ===================================================================================
        // U5: Graphics.DrawMesh in a manual Camera.Render
        // ===================================================================================

        private IEnumerator DrawMeshSection(Cap cap, Vessel host)
        {
            CelestialBody body = host.mainBody;
            Vector3 p = SlotPoint(body, 0, 1, PartParityStateRules.AirSpawnAltitudeM, out _, out _);
            Frame(body, p, out Vector3 up, out Vector3 north, out Vector3 east);
            GameObject quadGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Mesh mesh = quadGo.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.Destroy(quadGo.GetComponent<Collider>());
            quadGo.SetActive(false);
            Shader sh = Shader.Find("KSP/Particles/Additive");
            if (sh == null) { failures.Add("U5: no KSP/Particles/Additive shader"); yield break; }
            var mat = new Material(sh);
            mat.SetColor("_TintColor", new Color(1f, 0.6f, 0.2f, 0.5f));
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_TintColor", new Color(1f, 0.6f, 0.2f, 0.5f));
            // The quad faces the camera: the camera looks north, so the quad's normal is south.
            Matrix4x4 m = Matrix4x4.TRS(p, Quaternion.LookRotation(north, up), Vector3.one * 10f);
            var go = new GameObject("PartParityBurstSpikeDrawMesh");
            var probe = go.AddComponent<BurstSpikeDrawMeshProbe>();
            probe.Mesh = mesh; probe.Mat = mat; probe.Matrix = m; probe.Mpb = mpb;
            cap.Aim(p, north, up, 10f, 40f);
            int mask = 1 << 0;
            var row = new List<Color32[]>();
            var results = new StringBuilder();
            try
            {
                probe.Mode = 0;
                yield return null;
                Color32[] none = cap.Render(mask);
                results.Append($"none={Cov(none).ToString(Ic)} ");
                row.Add(none);

                probe.Mode = 1;
                yield return null; yield return null;
                Color32[] u1 = cap.Render(mask);
                yield return new WaitForEndOfFrame();
                Color32[] u2 = cap.Render(mask);
                results.Append($"update+afterUpdate={Cov(u1).ToString(Ic)} update+endOfFrame={Cov(u2).ToString(Ic)} ");
                row.Add(u1); row.Add(u2);

                probe.Mode = 2;
                yield return null; yield return null;
                Color32[] l1 = cap.Render(mask);
                yield return new WaitForEndOfFrame();
                Color32[] l2 = cap.Render(mask);
                results.Append($"lateUpdate+afterUpdate={Cov(l1).ToString(Ic)} lateUpdate+endOfFrame={Cov(l2).ToString(Ic)} ");
                row.Add(l1); row.Add(l2);

                probe.Mode = 0;
                yield return null; yield return null;
                Graphics.DrawMesh(mesh, m, mat, 0, null, 0, mpb);
                Color32[] im = cap.Render(mask);
                Graphics.DrawMesh(mesh, m, mat, 0, cap.Cam, 0, mpb);
                Color32[] imCam = cap.Render(mask);
                results.Append($"immediateNullCam={Cov(im).ToString(Ic)} immediateCaptureCam={Cov(imCam).ToString(Ic)} ");
                row.Add(im); row.Add(imCam);

                // Positive control: the same material on a real MeshRenderer on layer 0.
                var ctl = GameObject.CreatePrimitive(PrimitiveType.Quad);
                UnityEngine.Object.Destroy(ctl.GetComponent<Collider>());
                ctl.layer = 0;
                ctl.transform.SetPositionAndRotation(p, Quaternion.LookRotation(north, up));
                ctl.transform.localScale = Vector3.one * 10f;
                ctl.GetComponent<MeshRenderer>().sharedMaterial = mat;
                yield return null;
                Color32[] mr = cap.Render(mask);
                results.Append($"meshRendererControl={Cov(mr).ToString(Ic)}");
                row.Add(mr);
                UnityEngine.Object.Destroy(ctl);
            }
            finally
            {
                UnityEngine.Object.Destroy(go);
                UnityEngine.Object.Destroy(quadGo);
            }
            ParsekLog.Info(Tag, $"U5 drawmesh: shader={sh.name} quad=10m mask=0x{mask:X} px@{Tile}: {results}");
            WriteAtlas("ppb-u5-drawmesh.png", new List<List<Color32[]>> { row });
            UnityEngine.Object.Destroy(mat);
        }

        // ===================================================================================
        // U1 / U2: FXMonger
        // ===================================================================================

        private static object FxField(FXMonger fx, string name)
        {
            FieldInfo f = typeof(FXMonger).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f != null ? f.GetValue(fx) : null;
        }

        private static int QueuedCount(FXMonger fx)
        {
            var l = FxField(fx, "queuedExplosions") as IList;
            return l != null ? l.Count : -1;
        }

        private static HashSet<int> LiveIds()
        {
            var set = new HashSet<int>();
            List<FXObject> objs = GhostVisualBuilder.ResolveFxMongerExplosionObjects();
            if (objs == null) return set;
            foreach (FXObject o in objs)
                if (o != null && o.effectObj != null) set.Add(o.effectObj.GetInstanceID());
            return set;
        }

        private static List<GameObject> NewObjects(HashSet<int> before)
        {
            var list = new List<GameObject>();
            List<FXObject> objs = GhostVisualBuilder.ResolveFxMongerExplosionObjects();
            if (objs == null) return list;
            foreach (FXObject o in objs)
                if (o != null && o.effectObj != null && !before.Contains(o.effectObj.GetInstanceID())) list.Add(o.effectObj);
            return list;
        }

        private sealed class Tracked { public GameObject Go; public string Name; public float Born; public float Died = -1f; public string Label; }
        private static readonly List<Tracked> tracked = new List<Tracked>();

        private static void Track(string label, List<GameObject> gos)
        {
            // A burst object FXMonger already destroyed (Explosion 0 lives about 3 s, inside the
            // 4 s capture window) is Unity-null here: reading g.name on it threw, which is what
            // stopped the X0 burst cell right after index 0 in PPS-20a / PPS-20b.
            float now = Time.realtimeSinceStartup;
            foreach (GameObject g in gos)
            {
                bool gone = g == null;
                tracked.Add(new Tracked { Go = g, Name = gone ? "destroyed-before-track" : g.name, Born = now, Label = label, Died = gone ? now : -1f });
            }
        }

        private static void PollTracked()
        {
            foreach (Tracked t in tracked)
                if (t.Died < 0f && t.Go == null) t.Died = Time.realtimeSinceStartup;
        }

        private IEnumerator FxMongerSection(Cap cap, Vessel host)
        {
            tracked.Clear();
            FXMonger fx = GhostVisualBuilder.ResolveLiveFxMonger();
            if (fx == null) { failures.Add("U1: no live FXMonger"); ParsekLog.Info(Tag, "U1 fxmonger: none live"); yield break; }
            object altStop = null;
            if (FloatingOrigin.fetch != null) altStop = FloatingOrigin.fetch.altToStopMovingExplosions;
            ParsekLog.Info(Tag, $"U1 fxmonger: explosions={(fx.explosions != null ? fx.explosions.Length : -1).ToString(Ic)} " +
                $"sounds={(fx.explosionSounds != null ? fx.explosionSounds.Length : -1).ToString(Ic)} splashes={(fx.splashes != null ? fx.splashes.Length : -1).ToString(Ic)} " +
                $"debrisExplosion={(fx.debrisExplosion != null ? fx.debrisExplosion.Length : -1).ToString(Ic)} minPower={fx.minPower.ToString("R", Ic)} " +
                $"minSqrBlast={fx.minSqrBlast.ToString("R", Ic)} minDistanceFromOtherBlasts={fx.minDistanceFromOtherBlasts.ToString("R", Ic)} " +
                $"queueRemovalTimeMultiplier={fx.queueRemovalTimeMultiplier.ToString("R", Ic)} minSqrSplash={fx.minSqrSplash.ToString("R", Ic)} " +
                $"splashMonger={fx.splashMonger} altToStopMovingExplosions={altStop} logVerbose={GameSettings.LOG_FXMONGER_VERBOSE}");
            if (fx.explosions == null || fx.explosions.Length == 0) { failures.Add("U1: empty explosions[]"); yield break; }
            int n = fx.explosions.Length;
            for (int i = 0; i < n; i++)
            {
                GameObject pre = fx.explosions[i];
                ParsekLog.Info(Tag, $"U1 prefab[{i.ToString(Ic)}] name={(pre != null ? pre.name : "null")} active={(pre != null && pre.activeSelf)} " +
                    $"layer={(pre != null ? pre.layer : -1).ToString(Ic)} renderers=[{DescribeRenderers(pre)}] {DescribeSystems(pre)} " +
                    $"sound={(fx.explosionSounds != null && fx.explosionSounds.Length > 0 ? fx.explosionSounds[(int)((double)i / Math.Max(1, n - 1) * (fx.explosionSounds.Length - 1))]?.name : "none")}");
            }

            CelestialBody body = host.mainBody;
            // --- merge experiments (U1) and same-frame visibility (U2), each at its own slot ---
            IEnumerator e;
            e = MergeCase(fx, body, "same-frame-5m", 0, 5f, 0, 0.5); while (e.MoveNext()) yield return e.Current;
            e = MergeCase(fx, body, "next-frame-5m", 1, 5f, 1, 0.5); while (e.MoveNext()) yield return e.Current;
            e = MergeCase(fx, body, "plus2-frames-5m", 2, 5f, 2, 0.5); while (e.MoveNext()) yield return e.Current;
            e = MergeCase(fx, body, "same-frame-12m", 3, 12f, 0, 0.5); while (e.MoveNext()) yield return e.Current;
            e = MergeCase(fx, body, "same-frame-5m-sum-over-1", 4, 5f, 0, 0.7); while (e.MoveNext()) yield return e.Current;

            // --- every prefab index captured on its own (U1 draws in the capture camera) ---
            var atlas = new List<List<Color32[]>>();
            for (int i = 0; i < n; i++)
            {
                IEnumerator one = Guarded("U1 burst index=" + i.ToString(Ic), BurstIndex(cap, body, i, n, atlas));
                while (one.MoveNext()) yield return one.Current;
                PollTracked();
                IEnumerator gap = Seconds(0.5f); while (gap.MoveNext()) yield return gap.Current;
            }
            WriteAtlas("ppb-u1-fxmonger.png", atlas);

            IEnumerator si = Guarded("U1 standins", StandInsSection(cap, body));
            while (si.MoveNext()) yield return si.Current;
            IEnumerator lt = Guarded("U1 lifetimes", LifetimesSection());
            while (lt.MoveNext()) yield return lt.Current;
        }

        private IEnumerator BurstIndex(Cap cap, CelestialBody body, int i, int n, List<List<Color32[]>> atlas)
        {
            {
                double power = n == 1 ? 1.0 : (i == n - 1 ? 1.0 : (i + 0.5) / (n - 1));
                Vector3 src = SlotPoint(body, 0, 1, PartParityStateRules.AirSpawnAltitudeM, out _, out _);
                HashSet<int> before = LiveIds();
                int frame0 = Time.frameCount;
                FXMonger.Explode(null, src, power);
                var rowSide = new List<Color32[]>(); var rowTop = new List<Color32[]>();
                var bins = new StringBuilder();
                List<GameObject> found = null;
                IEnumerator cb = CaptureBurst(cap, body, src, () => found != null && found.Count > 0 ? found : (found = NewObjects(before)), BurstWindowS, rowSide, rowTop, bins,
                    ups => { });
                while (cb.MoveNext()) yield return cb.Current;
                if (found == null || found.Count == 0) found = NewObjects(before);
                Track("prefab" + i.ToString(Ic), found);
                var names = new List<string>();
                foreach (GameObject g in found) names.Add(g != null ? g.name : "destroyed");
                ParsekLog.Info(Tag, $"U1 burst index={i.ToString(Ic)} power={power.ToString("0.000", Ic)} expectedIndex={((int)(Math.Min(1.0, power) * (n - 1))).ToString(Ic)} " +
                    $"spawned={found.Count.ToString(Ic)} names=[{string.Join(",", names.ToArray())}] frame0={frame0.ToString(Ic)} bins: {bins}");
                atlas.Add(rowSide); atlas.Add(rowTop);
            }
        }

        private IEnumerator StandInsSection(Cap cap, CelestialBody body)
        {
            // --- the stand-ins Parsek uses (G.1 rows 3 and 4b), for the record ---
            var standIns = new List<List<Color32[]>>();
            foreach (float len in new[] { 5f, 20f })
            {
                Vector3 src = SlotPoint(body, 0, 1, PartParityStateRules.AirSpawnAltitudeM, out _, out _);
                GameObject go = GhostVisualBuilder.SpawnExplosionFx(src, len);
                var gos = new List<GameObject> { go };
                ParsekLog.Info(Tag, $"U1 standin custom L={len.ToString("0", Ic)}: renderers=[{DescribeRenderers(go)}] {DescribeSystems(go)}");
                var rowSide = new List<Color32[]>(); var rowTop = new List<Color32[]>(); var bins = new StringBuilder();
                IEnumerator cb = CaptureBurst(cap, body, src, () => gos, BurstWindowS, rowSide, rowTop, bins, ups => { });
                while (cb.MoveNext()) yield return cb.Current;
                ParsekLog.Info(Tag, $"U1 standin custom L={len.ToString("0", Ic)} power={Mathf.Clamp01(len / 20f).ToString("0.00", Ic)} bins: {bins}");
                standIns.Add(rowSide); standIns.Add(rowTop);
                Track("custom" + len.ToString("0", Ic), gos);
            }
            {
                Vector3 src = SlotPoint(body, 0, 1, PartParityStateRules.AirSpawnAltitudeM, out _, out _);
                GameObject go = GhostVisualBuilder.SpawnPartPuffFx(src, 1.25f);
                var gos = new List<GameObject> { go };
                ParsekLog.Info(Tag, $"U1 standin puff scale=1.25: renderers=[{DescribeRenderers(go)}] {DescribeSystems(go)}");
                var rowSide = new List<Color32[]>(); var rowTop = new List<Color32[]>(); var bins = new StringBuilder();
                IEnumerator cb = CaptureBurst(cap, body, src, () => gos, 3f, rowSide, rowTop, bins, ups => { });
                while (cb.MoveNext()) yield return cb.Current;
                ParsekLog.Info(Tag, $"U1 standin puff bins: {bins}");
                standIns.Add(rowSide); standIns.Add(rowTop);
                Track("puff", gos);
            }
            WriteAtlas("ppb-u1-standins.png", standIns);
        }

        private IEnumerator LifetimesSection()
        {

            // --- lifetimes of every instance seen (U1: how long an explosion object lives) ---
            float waitEnd = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < waitEnd)
            {
                PollTracked();
                bool all = true;
                foreach (Tracked t in tracked) if (t.Died < 0f) all = false;
                if (all) break;
                yield return null;
            }
            var life = new StringBuilder();
            foreach (Tracked t in tracked)
                life.Append(t.Label).Append(':').Append(t.Name).Append('=').Append(t.Died < 0f ? ">15s-after-last" : (t.Died - t.Born).ToString("0.00", Ic) + "s").Append(' ');
            ParsekLog.Info(Tag, $"U1 lifetimes: {life}");
        }

        /// <summary>Two blasts <paramref name="apartM"/> apart, the second
        /// <paramref name="frameGap"/> frames after the first; counts the instances made, reads the
        /// queue each frame, and whether the first instance is visible in its own frame (U2).</summary>
        private IEnumerator MergeCase(FXMonger fx, CelestialBody body, string label, int slot, float apartM, int frameGap, double power)
        {
            Vector3 a = SlotPoint(body, slot, 5, PartParityStateRules.AirSpawnAltitudeM, out _, out _);
            Frame(body, a, out Vector3 up, out Vector3 north, out Vector3 east);
            Vector3 b = a + east * apartM;
            HashSet<int> before = LiveIds();
            int f0 = Time.frameCount;
            FXMonger.Explode(null, a, power);
            int q0 = QueuedCount(fx);
            if (frameGap == 0) FXMonger.Explode(null, b, power);
            int q0b = QueuedCount(fx);
            var queueTrace = new StringBuilder();
            queueTrace.Append($"afterCall={q0.ToString(Ic)}/{q0b.ToString(Ic)} ");
            yield return new WaitForEndOfFrame();
            List<GameObject> firstFrame = NewObjects(before);
            int qEnd0 = QueuedCount(fx);
            queueTrace.Append($"endOfFrame0={qEnd0.ToString(Ic)} ");
            string u2 = "none";
            if (firstFrame.Count > 0)
            {
                GameObject g = firstFrame[0];
                u2 = $"name={g.name} activeInHierarchy={g.activeInHierarchy} particles={ParticleCount(firstFrame).ToString(Ic)} posFromA={Neu(g.transform.position, a, up, north, east)}";
            }
            ParsekLog.Info(Tag, $"U2 same-frame {label}: callFrame={f0.ToString(Ic)} endFrame={Time.frameCount.ToString(Ic)} newEntries={firstFrame.Count.ToString(Ic)} first[{u2}]");
            for (int k = 1; k <= Math.Max(frameGap, 1) + 2; k++)
            {
                yield return null;
                if (k == frameGap && frameGap > 0)
                {
                    FXMonger.Explode(null, b, power);
                    queueTrace.Append($"secondCallFrame={Time.frameCount.ToString(Ic)} q={QueuedCount(fx).ToString(Ic)} ");
                }
                queueTrace.Append($"f+{k.ToString(Ic)}={QueuedCount(fx).ToString(Ic)} ");
            }
            yield return new WaitForEndOfFrame();
            List<GameObject> all = NewObjects(before);
            Track("merge-" + label, all);
            var pos = new StringBuilder();
            foreach (GameObject g in all)
                pos.Append(g.name).Append('@').Append(Neu(g.transform.position, a, up, north, east)).Append(' ');
            ParsekLog.Info(Tag, $"U1 merge {label}: apart={apartM.ToString("0.0", Ic)}m gapFrames={frameGap.ToString(Ic)} power={power.ToString("0.00", Ic)} " +
                $"instances={all.Count.ToString(Ic)} merged={(all.Count == 1)} queue[{queueTrace}] at[{pos}] (b is east of a)");
            IEnumerator gap = Seconds(1.0f); while (gap.MoveNext()) yield return gap.Current;
        }

        /// <summary>Renders the burst's objects every frame for <paramref name="window"/> seconds
        /// from a side (looking north) and a top view over black, accumulated into
        /// <see cref="BinS"/> bins (per-pixel max over the bin's frames).</summary>
        private IEnumerator CaptureBurst(Cap cap, CelestialBody body, Vector3 src, Func<List<GameObject>> objects, float window,
            List<Color32[]> rowSide, List<Color32[]> rowTop, StringBuilder bins, Action<int> unused)
        {
            Frame(body, src, out Vector3 up, out Vector3 north, out Vector3 east);
            int nBins = Mathf.CeilToInt(window / BinS);
            var accSide = new Color32[nBins][]; var accTop = new Color32[nBins][];
            var frames = new int[nBins]; var maxCov = new int[nBins]; var parts = new int[nBins]; var ext = new float[nBins];
            float t0 = Time.time;
            int firstFrameWithPx = -1, frame = 0, mask = 0;
            while (Time.time - t0 < window)
            {
                yield return new WaitForEndOfFrame();
                List<GameObject> objs = objects();
                int b = Mathf.Clamp((int)((Time.time - t0) / BinS), 0, nBins - 1);
                mask = LayerMaskOf(objs);
                Color32[] side, top;
                if (mask == 0) { side = Black(Tile * Tile); top = Black(Tile * Tile); }
                else
                {
                    cap.Aim(src, north, up, 40f, 150f);
                    side = cap.Render(mask);
                    cap.Aim(src, -up, north, 40f, 150f);
                    top = cap.Render(mask);
                }
                int c = Cov(side);
                if (c > 0 && firstFrameWithPx < 0) firstFrameWithPx = frame;
                if (accSide[b] == null) { accSide[b] = Black(Tile * Tile); accTop[b] = Black(Tile * Tile); }
                MaxInto(accSide[b], side); MaxInto(accTop[b], top);
                frames[b]++;
                if (c > maxCov[b]) maxCov[b] = c;
                parts[b] = Math.Max(parts[b], ParticleCount(objs));
                if ((frames[b] & 3) == 1) ext[b] = Math.Max(ext[b], ExtentP95(objs, src));
                frame++;
                yield return null;
            }
            for (int b = 0; b < nBins; b++)
            {
                if (accSide[b] == null) { accSide[b] = Black(Tile * Tile); accTop[b] = Black(Tile * Tile); }
                rowSide.Add(accSide[b]); rowTop.Add(accTop[b]);
                bins.Append($"[{(b * BinS).ToString("0.00", Ic)}s f={frames[b].ToString(Ic)} cov={Cov(accSide[b]).ToString(Ic)} top={Cov(accTop[b]).ToString(Ic)} " +
                    $"maxFrameCov={maxCov[b].ToString(Ic)} particles={parts[b].ToString(Ic)} p95m={ext[b].ToString("0.0", Ic)}] ");
            }
            bins.Append($"firstFrameWithPx={firstFrameWithPx.ToString(Ic)} mask=0x{mask:X} view=80m");
        }

        // ===================================================================================
        // spawn helpers
        // ===================================================================================

        private static Vector3 NodePos(AvailablePart a, string id)
        {
            AttachNode n = a?.partPrefab != null ? a.partPrefab.FindAttachNode(id) : null;
            return n != null ? n.position : Vector3.zero;
        }

        private static IEnumerator WaitLive(uint pid, Action<Vessel> done)
        {
            float deadline = Time.realtimeSinceStartup + SpawnLiveTimeoutS;
            while (Time.realtimeSinceStartup < deadline)
            {
                Vessel v = FlightRecorder.FindVesselByPid(pid);
                if (v != null && v.loaded && !v.packed && v.rootPart != null) { done(v); yield break; }
                yield return null;
            }
            done(null);
        }

        private static void KillVesselsWithParts(HashSet<uint> partPids, string label)
        {
            var kill = new List<Vessel>();
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null || v == FlightGlobals.ActiveVessel || v.state == Vessel.State.DEAD) continue;
                if (v.parts == null) continue;
                foreach (Part p in v.parts)
                    if (p != null && partPids.Contains(p.persistentId)) { kill.Add(v); break; }
            }
            foreach (Vessel v in kill)
            {
                try { v.Die(); } catch (Exception ex) { ParsekLog.Info(Tag, $"teardown {label}: Die threw {ex.Message}"); }
            }
            ParsekLog.Info(Tag, $"teardown {label}: killed={kill.Count.ToString(Ic)}");
        }

        // ===================================================================================
        // U3: decouple burst and joint break
        // ===================================================================================

        private IEnumerator DecoupleSection(Cap cap, Vessel host)
        {
            var atlas = new List<List<Color32[]>>();
            int slot = 0;
            foreach (string kind in new[] { "decoupler-root-2part", "stacked-3part", "plain-2part" })
            {
                var row = new List<Color32[]>();
                IEnumerator c = Guarded("U3 " + kind, DecoupleCase(cap, host, kind, slot++, row));
                while (c.MoveNext()) yield return c.Current;
                atlas.Add(row);
            }
            WriteAtlas("ppb-u3-decouple.png", atlas);
        }

        private IEnumerator DecoupleCase(Cap cap, Vessel host, string kind, int slot, List<Color32[]> row)
        {
            string label = "u3-" + kind;
            AvailablePart dec = PartLoader.getPartInfoByName("Decoupler.1");
            AvailablePart tank = PartLoader.getPartInfoByName("fuelTankSmallFlat");
            if (dec == null || tank == null) { failures.Add(label + ": parts missing"); ParsekLog.Info(Tag, $"U3 {label}: missing dec={dec != null} tank={tank != null}"); yield break; }
            List<uint> ids = GhostCoOrbitalRig.UniqueFlightIds(3);
            ConfigNode[] parts;
            if (kind == "decoupler-root-2part")
            {
                Vector3 tankPos = NodePos(dec, "bottom") - NodePos(tank, "top");
                parts = new[]
                {
                    GhostCoOrbitalRig.AuthorPartNode(dec, ids[0], 0, Vector3.zero, new Dictionary<string, int> { { "bottom", 1 } }, null, null),
                    GhostCoOrbitalRig.AuthorPartNode(tank, ids[1], 0, tankPos, new Dictionary<string, int> { { "top", 0 } }, null, null),
                };
            }
            else if (kind == "stacked-3part")
            {
                Vector3 decPos = NodePos(tank, "bottom") - NodePos(dec, "top");
                Vector3 tank2Pos = decPos + NodePos(dec, "bottom") - NodePos(tank, "top");
                parts = new[]
                {
                    GhostCoOrbitalRig.AuthorPartNode(tank, ids[0], 0, Vector3.zero, new Dictionary<string, int> { { "bottom", 1 } }, null, null),
                    GhostCoOrbitalRig.AuthorPartNode(dec, ids[1], 0, decPos, new Dictionary<string, int> { { "top", 0 }, { "bottom", 2 } }, null, null),
                    GhostCoOrbitalRig.AuthorPartNode(tank, ids[2], 1, tank2Pos, new Dictionary<string, int> { { "top", 1 } }, null, null),
                };
            }
            else
            {
                Vector3 tank2Pos = NodePos(tank, "bottom") - NodePos(tank, "top");
                parts = new[]
                {
                    GhostCoOrbitalRig.AuthorPartNode(tank, ids[0], 0, Vector3.zero, new Dictionary<string, int> { { "bottom", 1 } }, null, null),
                    GhostCoOrbitalRig.AuthorPartNode(tank, ids[1], 0, tank2Pos, new Dictionary<string, int> { { "top", 0 } }, null, null),
                };
            }
            CelestialBody body = host.mainBody;
            Orbit o = host.orbit;
            ConfigNode vnode = ProtoVessel.CreateVesselNode("PPB " + label, VesselType.Probe,
                new Orbit(o.inclination, o.eccentricity, o.semiMajorAxis, o.LAN, o.argumentOfPeriapsis, o.meanAnomalyAtEpoch, o.epoch, o.referenceBody), 0, parts);
            PartParityAirPlan.SlotLatLon(slot, 3, body.Radius, out double lat, out double lon);
            double alt = PartParityStateRules.AirSpawnAltitudeM;
            Quaternion rot = GhostCoOrbitalRig.UprightRotation(body, lat, lon, alt, out Vector3 _);
            uint pid = GhostCoOrbitalRig.SpawnInAir(vnode, body, lat, lon, alt, 0.0, label, Tag, rot);
            if (pid == 0) { failures.Add(label + ": spawn pid=0"); yield break; }
            var partPids = new HashSet<uint>();
            Vessel real = null;
            PartParityFxHold hold = null;
            var ev = new BurstSpikeEvents();
            try
            {
                IEnumerator w = WaitLive(pid, v => real = v);
                while (w.MoveNext()) yield return w.Current;
                if (real == null) { failures.Add(label + ": never live"); yield break; }
                foreach (Part p in real.parts) partPids.Add(p.persistentId);
                hold = PartParityFxHold.AttachAtSlot(real, host, new PartParityHeldSlot
                {
                    Body = body, Latitude = lat, Longitude = lon, AltitudeM = alt,
                    SurfaceRelativeRotation = Quaternion.Inverse(body.bodyTransform.rotation) * rot,
                });
                IEnumerator s1 = Seconds(1.5f); while (s1.MoveNext()) yield return s1.Current;
                Part decPart = null; ModuleDecouplerBase decMod = null;
                foreach (Part p in real.parts)
                {
                    var m = p.FindModuleImplementing<ModuleDecouplerBase>();
                    if (m != null) { decPart = p; decMod = m; break; }
                }
                Part target = decPart ?? (real.parts.Count > 1 ? real.parts[1] : real.rootPart);
                FXGroup grp = decMod != null ? decPart.findFxGroup(decMod.fxGroupName) : null;
                var emitters = new List<ParticleSystem>();
                if (grp != null) emitters.AddRange(grp.fxEmittersNewSystem);
                var emitterDesc = new StringBuilder();
                foreach (ParticleSystem ps in emitters)
                {
                    var r = ps != null ? ps.GetComponent<ParticleSystemRenderer>() : null;
                    emitterDesc.Append(ps != null ? ps.name : "null").Append("{pos=")
                        .Append(ps != null ? V(target.transform.InverseTransformPoint(ps.transform.position)) : "-")
                        .Append(" space=").Append(ps != null ? ps.main.simulationSpace.ToString() : "-")
                        .Append(" shader=").Append(r != null && r.sharedMaterial != null ? r.sharedMaterial.shader.name : "-")
                        .Append(" layer=").Append(ps != null ? ps.gameObject.layer.ToString(Ic) : "-").Append("} ");
                }
                var partDesc = new StringBuilder();
                foreach (Part p in real.parts)
                    partDesc.Append(p.partInfo.name).Append('#').Append(p.persistentId.ToString(Ic)).Append(" parent=")
                        .Append(p.parent != null ? p.parent.persistentId.ToString(Ic) : "none").Append("; ");
                ParsekLog.Info(Tag, $"U3 {label}: parts=[{partDesc}] decoupler={(decMod != null ? decMod.GetType().Name : "none")} " +
                    $"explosiveNode={(decMod != null ? decMod.explosiveNodeID : "-")} fxGroupName={(decMod != null ? decMod.fxGroupName : "-")} " +
                    $"group={(grp != null)} groupName={(grp != null ? grp.name : "-")} sfx={(grp != null && grp.sfx != null ? grp.sfx.name : "none")} " +
                    $"emitters={emitters.Count.ToString(Ic)} [{emitterDesc}] hold[{hold.Describe()}]");

                // Every particle system on the case parts (to see any FX on a plain break).
                var allSystems = new List<ParticleSystem>();
                foreach (Part p in real.parts) allSystems.AddRange(p.GetComponentsInChildren<ParticleSystem>(true));
                int liveBefore = 0;
                foreach (ParticleSystem ps in allSystems) if (ps != null && ps.particleCount > 0) liveBefore++;
                HashSet<int> fxBefore = LiveIds();
                Vector3 src = target.transform.position;
                Frame(body, src, out Vector3 up, out Vector3 north, out Vector3 east);
                var fxRenderers = new List<Renderer>();
                foreach (ParticleSystem ps in allSystems)
                {
                    var r = ps != null ? ps.GetComponent<Renderer>() : null;
                    if (r != null) fxRenderers.Add(r);
                }

                ev.Subscribe();
                int f0 = Time.frameCount;
                string how;
                if (kind == "plain-2part")
                {
                    Part child = real.parts.Count > 1 ? real.parts[1] : null;
                    how = "Part.decouple() on " + (child != null ? child.partInfo.name + "#" + child.persistentId.ToString(Ic) : "none");
                    if (child != null) child.decouple();
                }
                else
                {
                    BaseEvent de = decMod != null ? decMod.Events["Decouple"] : null;
                    how = "KSPEvent Decouple invoke=" + (de != null);
                    if (de != null) de.Invoke(); else if (decMod != null) decMod.Decouple();
                }
                ParsekLog.Info(Tag, $"U3 {label}: trigger {how} frame={f0.ToString(Ic)} isDecoupled={(decMod != null && decMod.isDecoupled)} " +
                    $"groupActive={(grp != null && grp.Active)}");

                // Per frame for the first 12 frames: the emitters' state.
                var trace = new StringBuilder();
                int nBins = Mathf.CeilToInt(DecoupleWindowS / BinS);
                var acc = new Color32[nBins][]; var cnt = new int[nBins]; var maxDiff = new int[nBins];
                float t0 = Time.time;
                int frame = 0;
                while (Time.time - t0 < DecoupleWindowS)
                {
                    yield return new WaitForEndOfFrame();
                    if (frame < 12)
                    {
                        int em = 0, play = 0;
                        foreach (ParticleSystem ps in emitters) if (ps != null) { em += ps.particleCount; if (ps.isPlaying) play++; }
                        int live = 0;
                        foreach (ParticleSystem ps in allSystems) if (ps != null && ps.particleCount > 0) live++;
                        trace.Append($"f{frame.ToString(Ic)}:emitterParticles={em.ToString(Ic)} playing={play.ToString(Ic)} liveSystems={live.ToString(Ic)} ");
                    }
                    // Effect pixels: all on minus effect renderers off (same frame, same pose).
                    cap.Aim(src, north, up, 6f, 40f);
                    Color32[] on = cap.Render(SceneryMask);
                    var saved = new List<Renderer>();
                    foreach (Renderer r in fxRenderers) if (r != null && r.enabled) { r.enabled = false; saved.Add(r); }
                    Color32[] off = cap.Render(SceneryMask);
                    foreach (Renderer r in saved) if (r != null) r.enabled = true;
                    int b = Mathf.Clamp((int)((Time.time - t0) / BinS), 0, nBins - 1);
                    Color32[] dt = DiffTile(on, off);
                    if (acc[b] == null) acc[b] = Black(Tile * Tile);
                    MaxInto(acc[b], dt);
                    cnt[b]++;
                    maxDiff[b] = Math.Max(maxDiff[b], DiffCount(on, off));
                    if (frame == 0) row.Add(on);
                    frame++;
                    yield return null;
                }
                var binsS = new StringBuilder();
                for (int b = 0; b < nBins; b++)
                {
                    if (acc[b] == null) acc[b] = Black(Tile * Tile);
                    row.Add(acc[b]);
                    binsS.Append($"[{(b * BinS).ToString("0.00", Ic)}s f={cnt[b].ToString(Ic)} fxPx={Cov(acc[b]).ToString(Ic)} maxFrameDiff={maxDiff[b].ToString(Ic)}] ");
                }
                List<GameObject> fxNew = NewObjects(fxBefore);
                int liveAfter = 0;
                foreach (ParticleSystem ps in allSystems) if (ps != null && ps.particleCount > 0) liveAfter++;
                var vessels = new List<string>();
                foreach (Vessel v in FlightGlobals.VesselsLoaded)
                {
                    if (v == null || v.parts == null) continue;
                    foreach (Part p in v.parts)
                        if (p != null && partPids.Contains(p.persistentId)) { vessels.Add(v.vesselName + "#" + v.persistentId.ToString(Ic) + ":" + v.parts.Count.ToString(Ic)); break; }
                }
                ParsekLog.Info(Tag, $"U3 {label}: jointBreaks={ev.JointBreaks.ToString(Ic)} decouples={ev.Decouples.ToString(Ic)} decoupleCompletes={ev.DecoupleCompletes.ToString(Ic)} " +
                    $"fxMongerNew={fxNew.Count.ToString(Ic)} liveSystemsBefore={liveBefore.ToString(Ic)} liveSystemsAfter={liveAfter.ToString(Ic)} " +
                    $"vessels=[{string.Join(",", vessels.ToArray())}] events=[{string.Join(" | ", ev.Lines.ToArray())}]");
                ParsekLog.Info(Tag, $"U3 {label}: frames {trace}");
                ParsekLog.Info(Tag, $"U3 {label}: fx bins (view 12m, side looking north) {binsS}");
            }
            finally
            {
                ev.Unsubscribe();
                if (hold != null) UnityEngine.Object.Destroy(hold.gameObject);
                KillVesselsWithParts(partPids, label);
            }
            IEnumerator gap = Seconds(0.5f); while (gap.MoveNext()) yield return gap.Current;
        }

        // ===================================================================================
        // U4: launch dust
        // ===================================================================================

        private static readonly BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static object Get(object o, string field)
        {
            if (o == null) return null;
            FieldInfo f = o.GetType().GetField(field, Inst);
            return f != null ? f.GetValue(o) : null;
        }

        private struct GroundHit { public bool Ok; public Vector3 Point; public double Alt; public string Collider; public string TagName; public int Layer; }

        private static GroundHit CastDown(CelestialBody body, Vector3 p)
        {
            Frame(body, p, out Vector3 up, out _, out _);
            var g = new GroundHit();
            if (Physics.Raycast(p + up * 300f, -up, out RaycastHit hit, 1200f, 1073774592))
            {
                g.Ok = true;
                g.Point = hit.point;
                g.Alt = body.GetAltitude(hit.point);
                g.Collider = Path0(hit.collider.transform);
                g.TagName = hit.collider.gameObject.tag;
                g.Layer = hit.collider.gameObject.layer;
            }
            return g;
        }

        private IEnumerator DustSection(Cap cap, Vessel host)
        {
            CelestialBody body = host.mainBody;
            Vector3 hostPos = host.transform.position;
            Frame(body, hostPos, out Vector3 up, out Vector3 north, out Vector3 east);

            // LaunchpadFX colliders within 3 km of the pad host.
            var pads = new List<Collider>();
            var padDesc = new StringBuilder();
            int scanned = 0;
            foreach (Collider c in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                scanned++;
                if (c == null || c.gameObject.tag != "LaunchpadFX") continue;
                if ((c.bounds.center - hostPos).magnitude > 3000f) continue;
                pads.Add(c);
                padDesc.Append(Path0(c.transform)).Append("{layer=").Append(c.gameObject.layer.ToString(Ic))
                    .Append(" type=").Append(c.GetType().Name).Append(" center=").Append(Neu(c.bounds.center, hostPos, up, north, east))
                    .Append(" size=").Append(V(c.bounds.size)).Append("} ");
            }
            ParsekLog.Info(Tag, $"U4 launchpadfx: colliders={pads.Count.ToString(Ic)} scanned={scanned.ToString(Ic)} within=3000m of host: {padDesc}");

            AvailablePart eng = PartLoader.getPartInfoByName("liquidEngine2.v2");
            if (eng == null) { failures.Add("U4: liquidEngine2.v2 not loaded"); yield break; }
            ModuleSurfaceFX prefabSfx = eng.partPrefab.FindModuleImplementing<ModuleSurfaceFX>();
            float maxD = prefabSfx != null ? prefabSfx.maxDistance : 30f;
            string ttName = prefabSfx != null && !string.IsNullOrEmpty(prefabSfx.thrustTransformName) ? prefabSfx.thrustTransformName : "thrustTransform";
            Transform tt = eng.partPrefab.FindModelTransform(ttName);
            Vector3 nozzleLocal = tt != null ? eng.partPrefab.transform.InverseTransformPoint(tt.position) : new Vector3(0f, -1f, 0f);
            Vector3 nozzleFwd = tt != null ? eng.partPrefab.transform.InverseTransformDirection(tt.forward) : Vector3.down;
            ParsekLog.Info(Tag, $"U4 engine: {eng.name} surfaceFx={(prefabSfx != null)} maxDistance={maxD.ToString("0.0", Ic)} " +
                $"falloff={(prefabSfx != null ? prefabSfx.falloff : -1f).ToString("0.00", Ic)} fxMax={(prefabSfx != null ? prefabSfx.fxMax : -1f).ToString("0.00", Ic)} " +
                $"thrustTransform={ttName} nozzleLocal={V(nozzleLocal)} nozzleForwardLocal={V(nozzleFwd)}");

            var cases = new List<KeyValuePair<string, Vector3>>(); // label -> ground point (world)
            var heights = new List<double>();
            var ocean = new List<bool>();
            // Terrain: the showcase band 200 m east of the pad, north of the pad line.
            for (int k = 0; k < 2; k++)
            {
                PartParityAirPlan.SlotLatLon(k == 0 ? 2 : 4, 6, body.Radius, out double lat, out double lon);
                Vector3 g0 = body.GetWorldSurfacePosition(lat, lon, body.TerrainAltitude(lat, lon, true));
                GroundHit gh = CastDown(body, g0);
                ParsekLog.Info(Tag, $"U4 slot terrain{k.ToString(Ic)}: lat={lat.ToString("0.00000", Ic)} lon={lon.ToString("0.00000", Ic)} pqsAlt={body.TerrainAltitude(lat, lon, true).ToString("0.00", Ic)} " +
                    $"rayOk={gh.Ok} rayAlt={gh.Alt.ToString("0.00", Ic)} collider={gh.Collider} tag={gh.TagName} layer={gh.Layer.ToString(Ic)} at={Neu(gh.Ok ? gh.Point : g0, hostPos, up, north, east)}");
                Vector3 gp = gh.Ok ? gh.Point : g0;
                double h = (k == 0 ? 0.2 : 0.7) * maxD;
                cases.Add(new KeyValuePair<string, Vector3>(k == 0 ? "terrain-0.2" : "terrain-0.7", gp)); heights.Add(h); ocean.Add(false);
                if (k == 0)
                {
                    cases.Add(new KeyValuePair<string, Vector3>("terrain-1.2-above-reach", gp)); heights.Add(1.2 * maxD); ocean.Add(false);
                }
            }
            // Ocean east of KSC: the first point on the pad's latitude at least 10 m deep.
            {
                double mpd = 2.0 * Math.PI * body.Radius / 360.0;
                double found = double.NaN;
                for (double dEast = 300.0; dEast <= 12000.0; dEast += 100.0)
                {
                    double lon = PartParityAirPlan.PadLongitudeDeg + dEast / mpd;
                    if (body.TerrainAltitude(PartParityAirPlan.PadLatitudeDeg, lon, true) < -10.0) { found = dEast + 300.0; break; }
                }
                if (double.IsNaN(found)) ParsekLog.Info(Tag, "U4 slot ocean: no water within 12 km east of the pad");
                else
                {
                    double lon = PartParityAirPlan.PadLongitudeDeg + found / mpd;
                    Vector3 gp = body.GetWorldSurfacePosition(PartParityAirPlan.PadLatitudeDeg, lon, 0.0);
                    ParsekLog.Info(Tag, $"U4 slot ocean: eastOfPad={found.ToString("0", Ic)}m seabed={body.TerrainAltitude(PartParityAirPlan.PadLatitudeDeg, lon, true).ToString("0.0", Ic)}m " +
                        $"distanceToHost={(gp - hostPos).magnitude.ToString("0", Ic)}m");
                    cases.Add(new KeyValuePair<string, Vector3>("ocean-0.4", gp)); heights.Add(0.4 * maxD); ocean.Add(true);
                }
            }
            // The pad: a LaunchpadFX surface near the host, not under it.
            {
                bool placed = false;
                foreach (float dist in new[] { 18f, 28f, 40f })
                {
                    foreach (Vector3 dir in new[] { north, east, -north, -east })
                    {
                        GroundHit gh = CastDown(body, hostPos + dir * dist);
                        if (gh.Ok && gh.TagName == "LaunchpadFX")
                        {
                            ParsekLog.Info(Tag, $"U4 slot pad: collider={gh.Collider} at={Neu(gh.Point, hostPos, up, north, east)} alt={gh.Alt.ToString("0.00", Ic)}");
                            cases.Add(new KeyValuePair<string, Vector3>("pad-0.2", gh.Point)); heights.Add(0.2 * maxD); ocean.Add(false);
                            placed = true;
                            break;
                        }
                    }
                    if (placed) break;
                }
                if (!placed) ParsekLog.Info(Tag, "U4 slot pad: no LaunchpadFX surface found 18-40 m from the host");
            }

            var atlas = new List<List<Color32[]>>();
            for (int i = 0; i < cases.Count; i++)
            {
                var row = new List<Color32[]>();
                IEnumerator c = Guarded("U4 " + cases[i].Key, DustCase(cap, host, eng, maxD, nozzleLocal, cases[i].Key, cases[i].Value, heights[i], ocean[i], row));
                while (c.MoveNext()) yield return c.Current;
                atlas.Add(row);
            }
            WriteAtlas("ppb-u4-dust.png", atlas);
        }

        private IEnumerator DustCase(Cap cap, Vessel host, AvailablePart eng, float maxD, Vector3 nozzleLocal, string kind,
            Vector3 groundPoint, double h, bool overOcean, List<Color32[]> row)
        {
            string label = "u4-" + kind;
            CelestialBody body = host.mainBody;
            double lat = body.GetLatitude(groundPoint), lon = body.GetLongitude(groundPoint);
            double groundAlt = overOcean ? 0.0 : body.GetAltitude(groundPoint);
            // Root altitude so the thrust transform sits h above the surface (upright: exhaust down).
            double rootAlt = groundAlt + h - nozzleLocal.y;
            uint fid = GhostCoOrbitalRig.UniqueFlightIds(1)[0];
            ConfigNode p = GhostCoOrbitalRig.AuthorPartNode(eng, fid, 0, Vector3.zero, null, null, (mn, modName) =>
            {
                if (modName == "ModuleEngines" || modName == "ModuleEnginesFX")
                {
                    mn.SetValue("independentThrottle", "True", true);
                    mn.SetValue("independentThrottlePercentage", "100", true);
                    mn.SetValue("thrustPercentage", "100", true);
                    mn.SetValue("exhaustDamage", "False", true);
                }
            });
            Orbit o = host.orbit;
            ConfigNode vnode = ProtoVessel.CreateVesselNode("PPB " + label, VesselType.Probe,
                new Orbit(o.inclination, o.eccentricity, o.semiMajorAxis, o.LAN, o.argumentOfPeriapsis, o.meanAnomalyAtEpoch, o.epoch, o.referenceBody), 0, new[] { p });
            Quaternion rot = GhostCoOrbitalRig.UprightRotation(body, lat, lon, rootAlt, out Vector3 _);
            uint pid = GhostCoOrbitalRig.SpawnInAir(vnode, body, lat, lon, rootAlt, 0.0, label, Tag, rot);
            if (pid == 0) { failures.Add(label + ": spawn pid=0"); yield break; }
            // A slot past the stock 2 km unpack range (the ocean) gets wider ranges for this case.
            Vessel unloaded = FlightRecorder.FindVesselByPid(pid);
            double hostDist = (body.GetWorldSurfacePosition(lat, lon, rootAlt) - (Vector3d)host.transform.position).magnitude;
            if (unloaded != null && hostDist > 1500.0)
            {
                var wide = new VesselRanges.Situation(15000f, 15500f, 16000f, 14000f);
                unloaded.vesselRanges.flying = wide; unloaded.vesselRanges.landed = new VesselRanges.Situation(wide);
                unloaded.vesselRanges.splashed = new VesselRanges.Situation(wide); unloaded.vesselRanges.subOrbital = new VesselRanges.Situation(wide);
                unloaded.vesselRanges.prelaunch = new VesselRanges.Situation(wide);
                ParsekLog.Info(Tag, $"U4 {label}: hostDistance={hostDist.ToString("0", Ic)}m, vessel ranges widened to load 15 km / unpack 14 km");
            }
            var partPids = new HashSet<uint>();
            Vessel real = null;
            PartParityFxHold hold = null;
            try
            {
                IEnumerator w = WaitLive(pid, v => real = v);
                while (w.MoveNext()) yield return w.Current;
                if (real == null) { failures.Add(label + ": never live"); ParsekLog.Info(Tag, $"U4 {label}: never live hostDistance={hostDist.ToString("0", Ic)}m"); yield break; }
                foreach (Part pp in real.parts) partPids.Add(pp.persistentId);
                hold = PartParityFxHold.AttachAtSlot(real, host, new PartParityHeldSlot
                {
                    Body = body, Latitude = lat, Longitude = lon, AltitudeM = rootAlt,
                    SurfaceRelativeRotation = Quaternion.Inverse(body.bodyTransform.rotation) * rot,
                });
                IEnumerator s1 = Seconds(1.0f); while (s1.MoveNext()) yield return s1.Current;
                Part part = real.rootPart;
                ModuleEngines engine = part.FindModuleImplementing<ModuleEngines>();
                ModuleSurfaceFX sfx = part.FindModuleImplementing<ModuleSurfaceFX>();
                if (engine == null || sfx == null) { failures.Add(label + ": no engine / surface fx module"); yield break; }
                BaseEvent act = engine.Events["Activate"];
                if (act != null) act.Invoke(); else engine.Activate();
                float pressT = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - pressT < 5f && engine.finalThrust < 0.9f * engine.maxThrust) yield return null;
                IEnumerator s2 = Seconds(3.0f); while (s2.MoveNext()) yield return s2.Current;

                Transform trf = Get(sfx, "trf") as Transform;
                Frame(body, groundPoint, out Vector3 up, out Vector3 north, out Vector3 east);
                var samples = new StringBuilder();
                for (int k = 0; k < 8; k++)
                {
                    object hit = Get(sfx, "hit");
                    object dist = Get(sfx, "distance");
                    object rh = Get(sfx, "raycastHit");
                    object fxScale = Get(sfx, "fxScale");
                    double nozzleH = trf != null ? Vector3.Dot(trf.position - groundPoint, up) : double.NaN;
                    samples.Append($"[hit={hit} dist={(dist is float df ? df.ToString("0.00", Ic) : "?")} ray={rh} fxScale={(fxScale is float fs ? fs.ToString("0.000", Ic) : "?")} " +
                        $"ScaledFX={sfx.ScaledFX.ToString("0.000", Ic)} point={Neu(sfx.point, groundPoint, up, north, east)} nozzleH={nozzleH.ToString("0.00", Ic)}] ");
                    IEnumerator s = Seconds(0.25f); while (s.MoveNext()) yield return s.Current;
                }
                object srfFx = Get(sfx, "srfFX");
                object padFx = Get(sfx, "padFX");
                double expected = (engine.finalThrust / Math.Max(1e-3, engine.maxThrust)) * sfx.fxMax *
                    Math.Pow(Math.Max(0.0, 1.0 - (Get(sfx, "distance") is float d0 ? d0 : 0f) / sfx.maxDistance), sfx.falloff);
                ParsekLog.Info(Tag, $"U4 {label}: h={h.ToString("0.0", Ic)}m ({(h / maxD).ToString("0.0", Ic)} of {maxD.ToString("0", Ic)}) ocean={overOcean} " +
                    $"thrust={engine.finalThrust.ToString("0.0", Ic)}/{engine.maxThrust.ToString("0.0", Ic)} expectedScaledFX={expected.ToString("0.000", Ic)} " +
                    $"srfFX={(srfFx != null ? ((Component)srfFx).name : "null")} padFX={(padFx != null ? ((Component)padFx).name : "null")} samples: {samples}");

                // Every SurfaceFX object in the scene, and the pad FX if any.
                var srfRenderers = new List<Renderer>();
                var sfxDesc = new StringBuilder();
                foreach (SurfaceFX s in UnityEngine.Object.FindObjectsOfType<SurfaceFX>())
                {
                    if (s == null) continue;
                    sfxDesc.Append(s.name).Append("{at=").Append(Neu(s.transform.position, groundPoint, up, north, east))
                        .Append(" scaled=").Append(s.ScaledFX.ToString("0.000", Ic))
                        .Append(" lead=").Append(s.leadSource != null ? s.leadSource.part.partInfo.name : "none")
                        .Append(" renderers=[").Append(DescribeRenderers(s.gameObject)).Append("] ").Append(DescribeSystems(s.gameObject)).Append("} ");
                    foreach (Renderer r in s.GetComponentsInChildren<Renderer>(true)) if (r != null) srfRenderers.Add(r);
                }
                if (padFx is Component pc)
                {
                    sfxDesc.Append("padFX ").Append(pc.name).Append("{renderers=[").Append(DescribeRenderers(pc.gameObject)).Append("] ")
                        .Append(DescribeSystems(pc.gameObject)).Append("} ");
                    foreach (Renderer r in pc.GetComponentsInChildren<Renderer>(true)) if (r != null) srfRenderers.Add(r);
                }
                ParsekLog.Info(Tag, $"U4 {label}: surfaceFx objects: {sfxDesc}");

                // Do they draw in the capture camera: everything vs the SurfaceFX renderers off.
                Vector3 focus = sfx.point != Vector3.zero && (sfx.point - groundPoint).magnitude < 200f ? sfx.point : groundPoint;
                var counts = new StringBuilder();
                foreach (bool top in new[] { false, true })
                {
                    yield return new WaitForEndOfFrame();
                    if (top) cap.Aim(focus, -up, north, 20f, 80f); else cap.Aim(focus + up * 4f, north, up, 20f, 120f);
                    Color32[] on = cap.Render(SceneryMask);
                    var saved = new List<Renderer>();
                    foreach (Renderer r in srfRenderers) if (r != null && r.enabled) { r.enabled = false; saved.Add(r); }
                    Color32[] off = cap.Render(SceneryMask);
                    foreach (Renderer r in saved) if (r != null) r.enabled = true;
                    row.Add(on); row.Add(off); row.Add(DiffTile(on, off));
                    counts.Append($"{(top ? "top" : "side")}: dustPx={DiffCount(on, off).ToString(Ic)} srfRenderers={srfRenderers.Count.ToString(Ic)} ");
                    yield return null;
                }
                ParsekLog.Info(Tag, $"U4 {label}: capture (40 m view, all layers but 9/10/18) {counts} hold[{hold.Describe()}]");
                engine.Shutdown();
            }
            finally
            {
                if (hold != null) UnityEngine.Object.Destroy(hold.gameObject);
                KillVesselsWithParts(partPids, label);
            }
            IEnumerator gap = Seconds(1.0f); while (gap.MoveNext()) yield return gap.Current;
        }
    }
}
