using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Parsek.TestCommands;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Holds one case vessel at a fixed pose relative to the host every physics step (after the
    /// simulation, a WaitForFixedUpdate loop), writes its temperatures back, and optionally
    /// writes a control state into its ctrlState in Update / FixedUpdate while counting how often
    /// something else changed it in between. THROWAWAY E0 spike instrument.
    /// </summary>
    internal sealed class FxSpikeHold : MonoBehaviour
    {
        public Vessel Real;
        public Vessel Host;
        private Vector3 offLocal;
        private Quaternion rotLocal;
        private bool armed;
        public double MaxPreWriteDriftM;
        public double SumPreWriteDriftM;
        public int Steps;
        public double MaxLateDriftM;
        public int LateChecks;
        public double MaxAngleDriftDeg;
        public double SpawnTemp = double.NaN, SpawnSkinTemp = double.NaN;
        public double MaxTempSeen = double.MinValue, MaxSkinSeen = double.MinValue;
        public bool HoldTemps = true;
        public bool WriteCtrl;
        public float CtrlX, CtrlY, CtrlZ;
        public int CtrlChecks, CtrlOverUpdate, CtrlOverFixed, CtrlOverAfterPhysics;
        public string FirstOverwrite;
        private bool ctrlWritten;

        public void Arm(Vessel real, Vessel host)
        {
            Real = real; Host = host;
            Rigidbody hr = host.rootPart.Rigidbody, rr = real.rootPart.Rigidbody;
            Vector3 hp = hr != null ? hr.position : host.rootPart.transform.position;
            Quaternion hq = hr != null ? hr.rotation : host.rootPart.transform.rotation;
            Vector3 rp = rr != null ? rr.position : real.rootPart.transform.position;
            Quaternion rq = rr != null ? rr.rotation : real.rootPart.transform.rotation;
            offLocal = Quaternion.Inverse(hq) * (rp - hp);
            rotLocal = Quaternion.Inverse(hq) * rq;
            SpawnTemp = real.rootPart.temperature;
            SpawnSkinTemp = real.rootPart.skinTemperature;
            armed = true;
        }

        private IEnumerator Start()
        {
            var w = new WaitForFixedUpdate();
            while (true)
            {
                yield return w;
                Step();
            }
        }

        private bool Targets(out Vector3 target, out Quaternion targetRot, out Rigidbody hr, out Rigidbody rr)
        {
            target = Vector3.zero; targetRot = Quaternion.identity; hr = null; rr = null;
            if (!armed || Real == null || Host == null || Real.rootPart == null || Host.rootPart == null) return false;
            hr = Host.rootPart.Rigidbody; rr = Real.rootPart.Rigidbody;
            Vector3 hp = hr != null ? hr.position : Host.rootPart.transform.position;
            Quaternion hq = hr != null ? hr.rotation : Host.rootPart.transform.rotation;
            target = hp + hq * offLocal;
            targetRot = hq * rotLocal;
            return true;
        }

        private void Step()
        {
            if (!Targets(out Vector3 target, out Quaternion targetRot, out Rigidbody hr, out Rigidbody rr)) return;
            Part p = Real.rootPart;
            Vector3 cur = rr != null ? rr.position : p.transform.position;
            Quaternion curRot = rr != null ? rr.rotation : p.transform.rotation;
            double d = (cur - target).magnitude;
            Steps++;
            SumPreWriteDriftM += d;
            if (d > MaxPreWriteDriftM) MaxPreWriteDriftM = d;
            double ang = Quaternion.Angle(curRot, targetRot);
            if (ang > MaxAngleDriftDeg) MaxAngleDriftDeg = ang;
            if (rr != null)
            {
                rr.position = target;
                rr.rotation = targetRot;
                rr.velocity = hr != null ? hr.GetPointVelocity(target) : Vector3.zero;
                rr.angularVelocity = hr != null ? hr.angularVelocity : Vector3.zero;
            }
            p.transform.SetPositionAndRotation(target, targetRot);
            if (p.temperature > MaxTempSeen) MaxTempSeen = p.temperature;
            if (p.skinTemperature > MaxSkinSeen) MaxSkinSeen = p.skinTemperature;
            if (HoldTemps && !double.IsNaN(SpawnTemp))
            {
                p.temperature = SpawnTemp;
                p.skinTemperature = SpawnSkinTemp;
            }
            p.blackBodyRadiationAlphaMult = 0f;
            if (WriteCtrl && ctrlWritten) CheckCtrl(ref CtrlOverAfterPhysics, "afterPhysics");
        }

        private void LateUpdate()
        {
            if (!Targets(out Vector3 target, out Quaternion _, out Rigidbody _, out Rigidbody _)) return;
            double d = (Real.rootPart.transform.position - target).magnitude;
            LateChecks++;
            if (d > MaxLateDriftM) MaxLateDriftM = d;
        }

        private void CheckCtrl(ref int counter, string where)
        {
            if (Real == null || Real.ctrlState == null) return;
            CtrlChecks++;
            FlightCtrlState s = Real.ctrlState;
            if (Mathf.Abs(s.X - CtrlX) > 1e-4f || Mathf.Abs(s.Y - CtrlY) > 1e-4f || Mathf.Abs(s.Z - CtrlZ) > 1e-4f)
            {
                counter++;
                if (FirstOverwrite == null)
                    FirstOverwrite = $"{where} frame={Time.frameCount} X={s.X:F3} Y={s.Y:F3} Z={s.Z:F3}";
            }
        }

        private void WriteCtrlNow()
        {
            if (Real == null || Real.ctrlState == null) return;
            Real.ctrlState.X = CtrlX; Real.ctrlState.Y = CtrlY; Real.ctrlState.Z = CtrlZ;
            ctrlWritten = true;
        }

        private void Update()
        {
            if (!WriteCtrl) return;
            if (ctrlWritten) CheckCtrl(ref CtrlOverUpdate, "update");
            WriteCtrlNow();
        }

        private void FixedUpdate()
        {
            if (!WriteCtrl) return;
            if (ctrlWritten) CheckCtrl(ref CtrlOverFixed, "fixed");
            WriteCtrlNow();
        }

        public string Describe()
        {
            double mean = Steps > 0 ? SumPreWriteDriftM / Steps : 0;
            return $"steps={Steps} preWriteMaxM={MaxPreWriteDriftM:F4} preWriteMeanM={mean:F4} lateMaxM={MaxLateDriftM:F5} " +
                $"lateChecks={LateChecks} angMaxDeg={MaxAngleDriftDeg:F3} temp0={SpawnTemp:F1} tempMax={MaxTempSeen:F1} skin0={SpawnSkinTemp:F1} skinMax={MaxSkinSeen:F1}";
        }
    }

    /// <summary>
    /// P8 E0 SPIKE (design-part-ghost-parity.md Appendix E, E0). THROWAWAY, never merged. Answers
    /// U1-U10: a lone held engine ignites and burns; RCS fires from a written ctrlState; the
    /// test-stand hold; particle / Waterfall renderers in a capture camera; GetParticles on both
    /// sides; render cost; world-space smoke on the real and the ghost; a part light on a plate;
    /// what a tint rotation does to max-channel coverage. Writes Screenshots/ppfx-*.png and one
    /// log line per measure under [PartParityFxSpike].
    /// </summary>
    public class PartParityFxSpikeInGameTests
    {
        private readonly InGameTestRunner runner;
        public PartParityFxSpikeInGameTests(InGameTestRunner runner) { this.runner = runner; }

        internal const string Tag = "PartParityFxSpike";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const int Tile = 512;
        private const int Samples = 16;
        private const float SpawnLiveTimeoutSeconds = 30f;
        private const double AlongTrackMeters = 120.0;
        private static readonly int[] FlameThresholds = { 4, 8, 16, 32 };

        private static readonly string[] OrbitEngines =
            { "liquidEngine2.v2", "solidBooster.sm.v2", "ionEngine", "nuclearEngine", "liquidEngine", "RAPIER" };
        private static readonly string[] AirEngines = { "JetEngine", "RAPIER", "solidBooster.sm.v2" };
        private static readonly string[] RcsParts = { "RCSBlock.v2", "RCSblock.01.small" };
        private static readonly string[] SpotLights = { "spotLight1", "spotLight1.v2" };
        private static readonly string[] PointLightCandidates = { "domeLight1", "navLight1", "stripLight1", "groundLight1", "spotLight3", "groundLight2" };

        private sealed class Rig : IDisposable
        {
            public GameObject CamGo;
            public Camera Cam;
            public RenderTexture Msaa, Resolve;
            public Texture2D Readback;
            public double RenderMs, ReadMs;
            public int Renders;
            public Dictionary<string, double> MsByLayer = new Dictionary<string, double>();
            public Dictionary<string, int> NByLayer = new Dictionary<string, int>();

            public static Rig Create()
            {
                var r = new Rig();
                r.CamGo = new GameObject("PartParityFxSpikeCamera");
                r.Cam = r.CamGo.AddComponent<Camera>();
                r.Cam.enabled = false;
                r.Cam.orthographic = true;
                r.Cam.clearFlags = CameraClearFlags.SolidColor;
                r.Cam.renderingPath = RenderingPath.Forward;
                r.Cam.allowHDR = false;
                r.Cam.allowMSAA = true;
                r.Cam.useOcclusionCulling = false;
                r.Msaa = new RenderTexture(Tile, Tile, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                r.Msaa.Create();
                r.Resolve = new RenderTexture(Tile, Tile, 0, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
                r.Resolve.Create();
                r.Readback = new Texture2D(Tile, Tile, TextureFormat.RGBA32, false);
                return r;
            }

            public byte[] Render(Color clear, string layer)
            {
                Color bg = clear; bg.a = 0f;
                Cam.backgroundColor = bg;
                Cam.targetTexture = Msaa;
                RenderTexture before = RenderTexture.active;
                var sw = Stopwatch.StartNew();
                Cam.Render();
                double renderMs = sw.Elapsed.TotalMilliseconds;
                sw.Reset(); sw.Start();
                Graphics.Blit(Msaa, Resolve);
                RenderTexture.active = Resolve;
                Readback.ReadPixels(new Rect(0, 0, Tile, Tile), 0, 0, false);
                Color32[] px = Readback.GetPixels32();
                RenderTexture.active = before;
                Cam.targetTexture = null;
                var bytes = new byte[px.Length * 4];
                for (int y = 0; y < Tile; y++)
                {
                    int srcRow = (Tile - 1 - y) * Tile;
                    for (int x = 0; x < Tile; x++)
                    {
                        Color32 c = px[srcRow + x];
                        int o = (y * Tile + x) * 4;
                        bytes[o] = c.r; bytes[o + 1] = c.g; bytes[o + 2] = c.b; bytes[o + 3] = c.a;
                    }
                }
                double readMs = sw.Elapsed.TotalMilliseconds;
                RenderMs += renderMs; ReadMs += readMs; Renders++;
                if (!MsByLayer.ContainsKey(layer)) { MsByLayer[layer] = 0; NByLayer[layer] = 0; }
                MsByLayer[layer] += renderMs; NByLayer[layer]++;
                return bytes;
            }

            public void Dispose()
            {
                if (CamGo != null) UnityEngine.Object.Destroy(CamGo);
                if (Msaa != null) { Msaa.Release(); UnityEngine.Object.Destroy(Msaa); }
                if (Resolve != null) { Resolve.Release(); UnityEngine.Object.Destroy(Resolve); }
                if (Readback != null) UnityEngine.Object.Destroy(Readback);
            }

            public string CostLine()
            {
                var sb = new StringBuilder();
                foreach (var kv in MsByLayer)
                    sb.Append(' ').Append(kv.Key).Append('=').Append((kv.Value / Math.Max(1, NByLayer[kv.Key])).ToString("F2", IC))
                      .Append("ms/").Append(NByLayer[kv.Key].ToString(IC));
                return $"renders={Renders} renderMeanMs={(RenderMs / Math.Max(1, Renders)).ToString("F2", IC)} " +
                    $"readMeanMs={(ReadMs / Math.Max(1, Renders)).ToString("F2", IC)} byLayer:{sb}";
            }
        }

        /// <summary>Renderers of one side split by kind.</summary>
        private sealed class SideLayers
        {
            public string Name;
            public List<Renderer> Body = new List<Renderer>();
            public List<Renderer> Flame = new List<Renderer>();
            public List<Renderer> Smoke = new List<Renderer>();
            public List<Renderer> Waterfall = new List<Renderer>();
            public List<Renderer> Other = new List<Renderer>();
            public List<ParticleSystem> Systems = new List<ParticleSystem>();
            public SortedDictionary<string, int> Shaders = new SortedDictionary<string, int>();
            public SortedSet<int> Layers = new SortedSet<int>();
            public int KspEmitters;
            public IEnumerable<Renderer> All()
            {
                foreach (var r in Body) yield return r;
                foreach (var r in Flame) yield return r;
                foreach (var r in Smoke) yield return r;
                foreach (var r in Other) yield return r;
            }
            public string Describe()
            {
                var sh = new StringBuilder();
                foreach (var kv in Shaders) sh.Append(kv.Key).Append('x').Append(kv.Value.ToString(IC)).Append(';');
                var ls = new List<string>(); foreach (int l in Layers) ls.Add(l.ToString(IC));
                return $"{Name}: body={Body.Count} flame={Flame.Count} (waterfall={Waterfall.Count}) smoke={Smoke.Count} other={Other.Count} " +
                    $"systems={Systems.Count} kspEmitters={KspEmitters} layers=[{string.Join(",", ls.ToArray())}] shaders=[{sh}]";
            }
        }

        private static bool IsAdditive(Material m)
        {
            if (m == null || m.shader == null) return false;
            string n = m.shader.name.ToLowerInvariant();
            if (n.Contains("add")) return true;
            if (m.HasProperty("_DstBlend") && m.GetInt("_DstBlend") == 1) return true;
            return false;
        }

        /// <summary>Classify every renderer under <paramref name="root"/> (skipping anything under
        /// <paramref name="exclude"/>).</summary>
        private static SideLayers Classify(string name, Transform root, Transform exclude)
        {
            var s = new SideLayers { Name = name };
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (exclude != null && (r.transform == exclude || r.transform.IsChildOf(exclude))) continue;
                Material m = r.sharedMaterial;
                string shader = m != null && m.shader != null ? m.shader.name : "<none>";
                string key = r.GetType().Name + ":" + shader;
                s.Shaders.TryGetValue(key, out int c); s.Shaders[key] = c + 1;
                s.Layers.Add(r.gameObject.layer);
                if (r is ParticleSystemRenderer)
                {
                    if (IsAdditive(m)) s.Flame.Add(r); else s.Smoke.Add(r);
                }
                else if (r is MeshRenderer || r is SkinnedMeshRenderer)
                {
                    if (shader.StartsWith("Waterfall", StringComparison.OrdinalIgnoreCase)) { s.Flame.Add(r); s.Waterfall.Add(r); }
                    else s.Body.Add(r);
                }
                else s.Other.Add(r);
            }
            foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps == null) continue;
                if (exclude != null && (ps.transform == exclude || ps.transform.IsChildOf(exclude))) continue;
                s.Systems.Add(ps);
            }
            foreach (KSPParticleEmitter e in root.GetComponentsInChildren<KSPParticleEmitter>(true))
            {
                if (e == null) continue;
                if (exclude != null && (e.transform == exclude || e.transform.IsChildOf(exclude))) continue;
                s.KspEmitters++;
            }
            return s;
        }

        private struct ParticleStats
        {
            public int Alive, PsCount, Live;
            public double SpeedMean, AlongP95, AcrossP95, CentroidX, CentroidZ, AlongMean;
            public float MaxLifetime;
        }

        private static readonly ParticleSystem.Particle[] Buf = new ParticleSystem.Particle[20000];

        /// <summary>Live particles of a side's systems, positions in <paramref name="partT"/>'s
        /// space (exhaust along local -Y: along = -y from the part origin).</summary>
        private static ParticleStats ReadParticles(List<ParticleSystem> systems, Transform partT, Func<ParticleSystem, bool> filter)
        {
            var st = new ParticleStats();
            var along = new List<double>(); var across = new List<double>();
            double speed = 0, cx = 0, cz = 0;
            foreach (ParticleSystem ps in systems)
            {
                if (ps == null) continue;
                if (filter != null && !filter(ps)) continue;
                var main = ps.main;
                float lt = main.startLifetime.constantMax;
                if (lt > st.MaxLifetime) st.MaxLifetime = lt;
                if (ps.isPlaying || ps.isEmitting) st.Live++;
                st.PsCount += ps.particleCount;
                int n = ps.GetParticles(Buf);
                bool world = main.simulationSpace == ParticleSystemSimulationSpace.World;
                for (int i = 0; i < n; i++)
                {
                    Vector3 wp = world ? Buf[i].position : ps.transform.TransformPoint(Buf[i].position);
                    Vector3 lp = partT.InverseTransformPoint(wp);
                    along.Add(-lp.y);
                    across.Add(Math.Sqrt(lp.x * lp.x + lp.z * lp.z));
                    cx += lp.x; cz += lp.z;
                    speed += Buf[i].velocity.magnitude;
                }
                st.Alive += n;
            }
            if (st.Alive > 0)
            {
                st.SpeedMean = speed / st.Alive;
                st.CentroidX = cx / st.Alive; st.CentroidZ = cz / st.Alive;
                double sum = 0; foreach (double a in along) sum += a; st.AlongMean = sum / along.Count;
                along.Sort(); across.Sort();
                st.AlongP95 = along[(int)Math.Min(along.Count - 1, Math.Floor(0.95 * along.Count))];
                st.AcrossP95 = across[(int)Math.Min(across.Count - 1, Math.Floor(0.95 * across.Count))];
            }
            return st;
        }

        private static string Fmt(ParticleStats s) =>
            $"alive={s.Alive} psCount={s.PsCount} live={s.Live} speed={s.SpeedMean:F2} alongMean={s.AlongMean:F2} alongP95={s.AlongP95:F2} " +
            $"acrossP95={s.AcrossP95:F2} centroid=({s.CentroidX:F3},{s.CentroidZ:F3}) maxLifetime={s.MaxLifetime:F2}";

        /// <summary>Per pixel accumulation of one side / view / layer over N samples.</summary>
        private sealed class Accum
        {
            public int N;
            public int[][] Covered = new int[FlameThresholds.Length][];
            public int[] SumR = new int[Tile * Tile], SumG = new int[Tile * Tile], SumB = new int[Tile * Tile];
            public long Saturated; public int SatRenders; public int MaxSaturated;
            public Accum() { for (int i = 0; i < FlameThresholds.Length; i++) Covered[i] = new int[Tile * Tile]; }

            /// <summary>Flame: covered by max channel at each threshold; colour summed at T=16.</summary>
            public void AddFlame(byte[] img)
            {
                N++;
                int sat = 0;
                for (int p = 0, o = 0; p < Tile * Tile; p++, o += 4)
                {
                    int r = img[o], g = img[o + 1], b = img[o + 2];
                    int mx = Math.Max(r, Math.Max(g, b));
                    if (r == 255 || g == 255 || b == 255) sat++;
                    for (int t = 0; t < FlameThresholds.Length; t++)
                        if (mx >= FlameThresholds[t]) Covered[t][p]++;
                    if (mx >= 16) { SumR[p] += r; SumG[p] += g; SumB[p] += b; }
                }
                Saturated += sat; SatRenders++; if (sat > MaxSaturated) MaxSaturated = sat;
            }

            /// <summary>Smoke: matte over black and white, covered at alpha 128.</summary>
            public void AddSmoke(byte[] black, byte[] white)
            {
                N++;
                for (int p = 0, o = 0; p < Tile * Tile; p++, o += 4)
                {
                    int dr = white[o] - black[o], dg = white[o + 1] - black[o + 1], db = white[o + 2] - black[o + 2];
                    double a = 1.0 - (Math.Max(0, dr) + Math.Max(0, dg) + Math.Max(0, db)) / (3.0 * 255.0);
                    for (int t = 0; t < FlameThresholds.Length; t++)
                        if (a * 255.0 >= FlameThresholds[t] * 8) Covered[t][p]++; // 32, 64, 128, 256(never)
                    if (a >= 0.5)
                    {
                        SumR[p] += (int)Math.Min(255.0, black[o] / a);
                        SumG[p] += (int)Math.Min(255.0, black[o + 1] / a);
                        SumB[p] += (int)Math.Min(255.0, black[o + 2] / a);
                    }
                }
            }

            public int Count(int thresholdIndex, int minK)
            {
                int c = 0; int[] cov = Covered[thresholdIndex];
                for (int p = 0; p < cov.Length; p++) if (cov[p] >= minK) c++;
                return c;
            }

            /// <summary>Mean chromaticity (r, g) and luminance over the core.</summary>
            public void CoreColour(int ti, out double cr, out double cg, out double lum)
            {
                int[] cov = Covered[ti]; int minK = Math.Max(1, (N + 1) / 2);
                double sr = 0, sg = 0, sb = 0; long k = 0;
                for (int p = 0; p < cov.Length; p++)
                {
                    if (cov[p] < minK) continue;
                    sr += SumR[p]; sg += SumG[p]; sb += SumB[p]; k += cov[p];
                }
                double tot = sr + sg + sb;
                cr = tot > 0 ? sr / tot : 0; cg = tot > 0 ? sg / tot : 0;
                lum = k > 0 ? (0.2126 * sr + 0.7152 * sg + 0.0722 * sb) / k : 0;
            }

            /// <summary>Envelope extent along image Y (top-down rows), in px.</summary>
            public void Extent(int ti, int minK, out int rowMin, out int rowMax, out int colMin, out int colMax)
            {
                rowMin = int.MaxValue; rowMax = -1; colMin = int.MaxValue; colMax = -1;
                int[] cov = Covered[ti];
                for (int p = 0; p < cov.Length; p++)
                {
                    if (cov[p] < minK) continue;
                    int y = p / Tile, x = p % Tile;
                    if (y < rowMin) rowMin = y; if (y > rowMax) rowMax = y;
                    if (x < colMin) colMin = x; if (x > colMax) colMax = x;
                }
            }

            public byte[] ToTile(int ti)
            {
                var o = new byte[Tile * Tile * 4];
                int[] cov = Covered[ti];
                for (int p = 0; p < cov.Length; p++)
                {
                    int k = cov[p];
                    if (k == 0) continue;
                    o[p * 4] = (byte)Math.Min(255, SumR[p] / Math.Max(1, k));
                    o[p * 4 + 1] = (byte)Math.Min(255, SumG[p] / Math.Max(1, k));
                    o[p * 4 + 2] = (byte)Math.Min(255, SumB[p] / Math.Max(1, k));
                    o[p * 4 + 3] = (byte)Math.Min(255, (int)Math.Floor(255.0 * k / Math.Max(1, N) + 0.5));
                }
                return o;
            }

            public string Describe(string tag)
            {
                var sb = new StringBuilder();
                for (int t = 0; t < FlameThresholds.Length; t++)
                    sb.Append($" env{FlameThresholds[t]}={Count(t, 1)} core{FlameThresholds[t]}={Count(t, Math.Max(1, (N + 1) / 2))}");
                CoreColour(2, out double cr, out double cg, out double lum);
                Extent(2, 1, out int r0, out int r1, out int c0, out int c1);
                string sat = SatRenders > 0 ? $" satMeanPx={(Saturated / (double)SatRenders):F1} satMaxPx={MaxSaturated}" : "";
                return $"{tag} N={N}{sb} chroma=({cr:F4},{cg:F4}) lum={lum:F1} rows={r0}-{r1} cols={c0}-{c1}{sat}";
            }
        }

        private struct View { public string Name; public Vector3 Dir; public Vector3 Up; public bool Far; }

        private static readonly View[] FxViews =
        {
            new View { Name = "fx-side-a", Dir = Vector3.back, Up = Vector3.up, Far = false },
            new View { Name = "fx-far", Dir = Vector3.back, Up = Vector3.up, Far = true },
            new View { Name = "fx-bottom", Dir = Vector3.down, Up = Vector3.back, Far = false },
        };

        // ===================================================================================
        // entry points
        // ===================================================================================

        [InGameTest(Category = "PartParityFxSpike", Scene = GameScenes.FLIGHT,
            Description = "P8 E0 spike (orbit): held engines, RCS, plate lights; Screenshots/ppfx-*.png")]
        public IEnumerator OrbitSpike()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext()) yield return unpackWait.Current;
            Vessel host = FlightGlobals.ActiveVessel;
            if (host == null) InGameAssert.Skip("no active vessel");
            if (host.situation != Vessel.Situations.ORBITING) InGameAssert.Skip("needs an orbiting host");
            IEnumerator body = RunAll(host, false);
            while (body.MoveNext()) yield return body.Current;
        }

        [InGameTest(Category = "PartParityFxSpikeAir", Scene = GameScenes.FLIGHT,
            Description = "P8 E0 spike (air): held jets at 1600 m over KSC; Screenshots/ppfx-*.png")]
        public IEnumerator AirSpike()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext()) yield return unpackWait.Current;
            Vessel host = FlightGlobals.ActiveVessel;
            if (host == null) InGameAssert.Skip("no active vessel");
            if (!(host.situation == Vessel.Situations.PRELAUNCH || host.situation == Vessel.Situations.LANDED))
                InGameAssert.Skip("needs a host on the pad");
            IEnumerator body = RunAll(host, true);
            while (body.MoveNext()) yield return body.Current;
        }

        private static int filesWritten;
        private static List<string> failures;

        private IEnumerator RunAll(Vessel host, bool air)
        {
            filesWritten = 0;
            failures = new List<string>();
            bool cheatBefore = CheatOptions.InfinitePropellant;
            CheatOptions.InfinitePropellant = true;
            ParsekLog.Info(Tag, $"env: air={air} host={host.vesselName} sit={host.situation} gfx={SystemInfo.graphicsDeviceType} " +
                $"unity={Application.unityVersion} fixedDt={Time.fixedDeltaTime:F4} krakensbane={(Krakensbane.GetFrameVelocity().magnitude):F1} " +
                $"cheat InfinitePropellant {cheatBefore} -> {CheatOptions.InfinitePropellant}");
            Rig rig = Rig.Create();
            int cases = 0;
            try
            {
                foreach (string name in air ? AirEngines : OrbitEngines)
                {
                    if (PartLoader.getPartInfoByName(name) == null) { ParsekLog.Info(Tag, $"engine {name}: not loaded"); continue; }
                    cases++;
                    IEnumerator c = EngineCase(rig, host, name, air);
                    while (c.MoveNext()) yield return c.Current;
                }
                if (!air)
                {
                    foreach (string name in RcsParts)
                    {
                        if (PartLoader.getPartInfoByName(name) == null) { ParsekLog.Info(Tag, $"rcs {name}: not loaded"); continue; }
                        cases++;
                        IEnumerator c = RcsCase(rig, host, name);
                        while (c.MoveNext()) yield return c.Current;
                    }
                    var lightParts = new List<string>();
                    foreach (string n in SpotLights) if (PartLoader.getPartInfoByName(n) != null) { lightParts.Add(n); break; }
                    foreach (string n in PointLightCandidates)
                    {
                        AvailablePart ap = PartLoader.getPartInfoByName(n);
                        if (ap == null || ap.partPrefab == null) continue;
                        var types = new List<string>();
                        foreach (Light l in ap.partPrefab.GetComponentsInChildren<Light>(true)) types.Add(l.type.ToString());
                        ParsekLog.Info(Tag, $"light candidate {n}: prefab lights=[{string.Join(",", types.ToArray())}]");
                        if (types.Contains("Point")) { lightParts.Add(n); break; }
                    }
                    foreach (string n in lightParts)
                    {
                        cases++;
                        IEnumerator c = PlateCase(rig, host, n);
                        while (c.MoveNext()) yield return c.Current;
                    }
                }
            }
            finally
            {
                CheatOptions.InfinitePropellant = cheatBefore;
                ParsekLog.Info(Tag, $"cheat InfinitePropellant restored -> {CheatOptions.InfinitePropellant}; cost {rig.CostLine()}");
                rig.Dispose();
            }
            ParsekLog.Info(Tag, $"summary: cases={cases} files={filesWritten} failures={failures.Count} [{string.Join("; ", failures.ToArray())}]");
        }

        // ===================================================================================
        // spawn / teardown
        // ===================================================================================

        private static uint Spawn(Vessel host, string partName, bool air, Action<ConfigNode, string> customize, string label)
        {
            AvailablePart info = PartLoader.getPartInfoByName(partName);
            uint fid = GhostCoOrbitalRig.UniqueFlightIds(1)[0];
            ConfigNode p = GhostCoOrbitalRig.AuthorPartNode(info, fid, 0, Vector3.zero, null, null, customize);
            Orbit src = host.orbit;
            ConfigNode vnode = ProtoVessel.CreateVesselNode("PPFX " + label, VesselType.Probe,
                new Orbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN, src.argumentOfPeriapsis,
                    src.meanAnomalyAtEpoch, src.epoch, src.referenceBody), 0, new[] { p });
            if (!air)
                return GhostCoOrbitalRig.SpawnCoOrbital(vnode, host, AlongTrackMeters, label, Tag);
            CelestialBody body = host.mainBody;
            PartParityAirPlan.SlotLatLon(0, 1, body.Radius, out double lat, out double lon);
            return GhostCoOrbitalRig.SpawnInAir(vnode, body, lat, lon, PartParityStateRules.AirSpawnAltitudeM, 0.0, label, Tag);
        }

        private static IEnumerator WaitLive(uint pid, Action<Vessel> done)
        {
            float deadline = Time.realtimeSinceStartup + SpawnLiveTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                Vessel v = FlightRecorder.FindVesselByPid(pid);
                if (v != null && v.loaded && !v.packed && v.rootPart != null) { done(v); yield break; }
                yield return null;
            }
            done(null);
        }

        private static void Teardown(Vessel v, uint pid, List<GhostCoOrbitalRig.LiveGhost> ghosts, FxSpikeHold hold, string label)
        {
            if (hold != null) UnityEngine.Object.Destroy(hold.gameObject);
            if (ghosts != null) foreach (var g in ghosts) GhostCoOrbitalRig.DestroyLiveGhost(g);
            Vessel real = v ?? FlightRecorder.FindVesselByPid(pid);
            if (real != null && real != FlightGlobals.ActiveVessel && real.state != Vessel.State.DEAD)
            {
                try { real.Die(); } catch (Exception ex) { ParsekLog.Info(Tag, $"teardown {label}: Die threw {ex.Message}"); }
            }
            ParsekLog.Info(Tag, $"teardown {label} pid={pid}");
        }

        private static FxSpikeHold StartHold(Vessel real, Vessel host)
        {
            var go = new GameObject("PartParityFxSpikeHold");
            var h = go.AddComponent<FxSpikeHold>();
            h.Arm(real, host);
            return h;
        }

        private static IEnumerator Seconds(float s)
        {
            float end = Time.realtimeSinceStartup + s;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        private static GhostCoOrbitalRig.LiveGhost BuildGhost(ConfigNode stowed, Vessel real, List<PartEvent> events, string label)
        {
            double now = Planetarium.GetUniversalTime();
            if (!GhostCoOrbitalRig.TryBuildGhostFromSnapshot(stowed.CreateCopy(), "ppfx-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    "ParsekTest_ppfx_" + label, events, real.vesselName, real.persistentId, now - 2.0,
                    out GhostCoOrbitalRig.LiveGhost g, out string fail))
            {
                failures.Add(label + ": ghost build " + fail);
                return null;
            }
            int applied = GhostCoOrbitalRig.ApplySeedPrefix(g, now);
            Transform rootT = real.rootPart.transform;
            g.Build.root.transform.SetParent(rootT, false);
            g.Build.root.transform.localPosition = Vector3.zero;
            g.Build.root.transform.localRotation = Quaternion.identity;
            g.Build.root.transform.localScale = Vector3.one;
            GhostCoOrbitalRig.NeutralisePhysics(g.Build.root, out int _, out int _);
            g.Build.root.SetActive(true);
            // The first unsuppressed frame of ApplyFrameVisuals, minus reentry FX and launch dust.
            bool prior = g.State.visualFxSuppressed;
            g.State.visualFxSuppressed = false;
            GhostPlaybackLogic.UpdateEvaJetpackPlumeForFrame(g.State);
            GhostPlaybackLogic.RestoreAllRcsEmissions(g.State);
            GhostPlaybackLogic.UnmuteAllAudio(g.State);
            if (GhostPlaybackEngine.ShouldRestartEngineFxAfterSuppression(false, prior))
                GhostPlaybackLogic.RestoreActiveEngineFx(g.State);
            GhostPlaybackLogic.UpdateAudioAtmosphere(g.State);
            int eng = g.State.engineInfos?.Count ?? 0, rcs = g.State.rcsInfos?.Count ?? 0;
            var powers = new StringBuilder();
            if (g.State.engineInfos != null)
                foreach (var kv in g.State.engineInfos)
                    powers.Append($"eng[{kv.Key}] src={kv.Value.fxSource} power={kv.Value.currentPower:F3} ps={kv.Value.particleSystems.Count} kspE={kv.Value.kspEmitters.Count}; ");
            if (g.State.rcsInfos != null)
                foreach (var kv in g.State.rcsInfos)
                    powers.Append($"rcs[{kv.Key}] power={kv.Value.currentPower:F3} ps={kv.Value.particleSystems.Count} kspE={kv.Value.kspEmitters.Count}; ");
            ParsekLog.Info(Tag, $"ghost {label}: events={events?.Count ?? 0} applied={applied} priorSuppressed={prior} engineInfos={eng} rcsInfos={rcs} {powers}");
            return g;
        }

        private static List<Renderer> OtherRenderers(Vessel real)
        {
            var list = new List<Renderer>();
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v == real) continue;
                foreach (Part p in v.parts)
                    if (p != null) list.AddRange(p.GetComponentsInChildren<Renderer>(true));
            }
            return list;
        }

        // ===================================================================================
        // engines (U1, U3, U4, U5, U6, U7, U8, U10)
        // ===================================================================================

        private IEnumerator EngineCase(Rig rig, Vessel host, string partName, bool air)
        {
            string label = PartParitySpikeLabel(partName) + (air ? "-air" : "");
            bool isRapier = partName == "RAPIER";
            bool wantPrimary = air; // RAPIER: air-breathing in the air, closed cycle in orbit
            uint pid = Spawn(host, partName, air, (mn, modName) =>
            {
                if (modName == "ModuleEngines" || modName == "ModuleEnginesFX")
                {
                    mn.SetValue("independentThrottle", "True", true);
                    mn.SetValue("independentThrottlePercentage", "100", true);
                    mn.SetValue("thrustPercentage", "100", true);
                    mn.SetValue("exhaustDamage", "False", true);
                }
                if (modName == "MultiModeEngine")
                    mn.SetValue("autoSwitch", "False", true);
            }, label);
            if (pid == 0) { failures.Add(label + ": spawn pid=0"); yield break; }
            Vessel real = null;
            FxSpikeHold hold = null;
            var ghosts = new List<GhostCoOrbitalRig.LiveGhost>();
            var swatch = Stopwatch.StartNew();
            try
            {
                IEnumerator w = WaitLive(pid, v => real = v);
                while (w.MoveNext()) yield return w.Current;
                if (real == null) { failures.Add(label + ": never live"); yield break; }
                hold = StartHold(real, host);
                IEnumerator s1 = Seconds(1.0f);
                while (s1.MoveNext()) yield return s1.Current;
                if (real.rootPart == null || real.state == Vessel.State.DEAD) { failures.Add(label + ": died settling"); yield break; }
                Part part = real.rootPart;
                ConfigNode stowed = FlightRecorder.TryBackupSnapshot(real);
                var engines = FlightRecorder.CacheEngineModules(real);
                MultiModeEngine mme = part.FindModuleImplementing<MultiModeEngine>();
                if (isRapier && mme != null)
                {
                    mme.autoSwitch = false;
                    if (!wantPrimary && mme.runningPrimary) mme.SetSecondary(true);
                    if (wantPrimary && !mme.runningPrimary) mme.SetPrimary(true);
                }
                // Hold drift before the press, as the baseline.
                string holdIdle = hold.Describe();
                hold.MaxPreWriteDriftM = 0; hold.SumPreWriteDriftM = 0; hold.Steps = 0; hold.MaxLateDriftM = 0; hold.LateChecks = 0; hold.MaxAngleDriftDeg = 0;
                ModuleEngines target = null; int targetIdx = -1;
                foreach (var e in engines)
                {
                    bool active = mme == null || (mme.runningPrimary ? e.engine.engineID == mme.primaryEngineID : e.engine.engineID == mme.secondaryEngineID);
                    ParsekLog.Info(Tag, $"engine {label}: module midx={e.moduleIndex} id={e.engine.engineID} type={e.engine.GetType().Name} " +
                        $"isEnabled={e.engine.isEnabled} selected={active} indep={e.engine.independentThrottle} indepPct={e.engine.independentThrottlePercentage} " +
                        $"throttleLocked={e.engine.throttleLocked} maxThrust={e.engine.maxThrust} responseTime={e.engine.useEngineResponseTime} " +
                        $"controllable={real.IsControllable} exhaustDamage={e.engine.exhaustDamage}");
                    if (active && target == null) { target = e.engine; targetIdx = e.moduleIndex; }
                }
                if (target == null) { failures.Add(label + ": no engine module"); yield break; }
                BaseEvent act = target.Events["Activate"];
                float pressT = Time.realtimeSinceStartup;
                double pressUT = Planetarium.GetUniversalTime();
                if (act != null) act.Invoke(); else target.Activate();
                ParsekLog.Info(Tag, $"press {label}: Activate event={(act != null)} mme={(mme != null ? (mme.runningPrimary ? "primary" : "secondary") : "none")}");
                float request = target.throttleLocked ? target.thrustPercentage * 0.01f : target.independentThrottlePercentage * 0.01f * target.thrustPercentage * 0.01f;
                float timeout = air ? 20f : 10f;
                float doneT = -1f;
                var trace = new StringBuilder();
                float nextTrace = pressT;
                while (Time.realtimeSinceStartup - pressT < timeout)
                {
                    bool ign = FlightRecorder.ShouldRecordEngineAsIgnited(target.EngineIgnited, target.isOperational, target.finalThrust);
                    if (Time.realtimeSinceStartup >= nextTrace)
                    {
                        trace.Append($"[{(Time.realtimeSinceStartup - pressT):F1}s ign={target.EngineIgnited} op={target.isOperational} thr={target.currentThrottle:F3} " +
                            $"fin={target.finalThrust:F2} flame={target.flameout} mach={real.mach:F3}] ");
                        nextTrace += 0.5f;
                    }
                    if (ign && Math.Abs(target.currentThrottle - request) <= 0.01f) { doneT = Time.realtimeSinceStartup - pressT; break; }
                    yield return null;
                }
                ParsekLog.Info(Tag, $"U1 ignite {label}: done={(doneT >= 0)} t={doneT:F2}s request={request:F3} ign={target.EngineIgnited} op={target.isOperational} " +
                    $"currentThrottle={target.currentThrottle:F3} finalThrust={target.finalThrust:F2} maxThrust={target.maxThrust:F1} flameout={target.flameout} " +
                    $"status='{target.status}' mach={real.mach:F3} atm={real.atmDensity:F4} controllable={real.IsControllable} cheat={CheatOptions.InfinitePropellant} trace={trace}");
                if (doneT < 0) failures.Add(label + ": not ignited / spooled in time");
                float power = FlightRecorder.ComputeRecordedEnginePower(target.currentThrottle, target.finalThrust, target.maxThrust);
                double ignUT = Planetarium.GetUniversalTime();
                var events = new List<PartEvent>
                {
                    new PartEvent { ut = ignUT - 1.0, partPersistentId = part.persistentId, eventType = PartEventType.EngineIgnited,
                        partName = part.partInfo.name, value = power, moduleIndex = targetIdx },
                };
                var ghost = BuildGhost(stowed, real, events, label);
                if (ghost == null) yield break;
                ghosts.Add(ghost);
                float primeT = Time.realtimeSinceStartup;
                Transform ghostRoot = ghost.Build.root.transform;
                SideLayers realL = Classify("real", part.transform, ghostRoot);
                SideLayers ghostL = Classify("ghost", ghostRoot, null);
                ParsekLog.Info(Tag, $"layers {label}: {realL.Describe()} | {ghostL.Describe()}");

                // Warm-up / plateau per side (U5 counts on both sides).
                var realSeries = new List<int>(); var ghostSeries = new List<int>();
                float realPlat = -1, ghostPlat = -1;
                float realMaxLt = ReadParticles(realL.Systems, part.transform, null).MaxLifetime;
                foreach (KSPParticleEmitter e in part.GetComponentsInChildren<KSPParticleEmitter>(true))
                    if (!e.transform.IsChildOf(ghostRoot) && e.maxEnergy > realMaxLt) realMaxLt = e.maxEnergy;
                float ghostMaxLt = ReadParticles(ghostL.Systems, part.transform, null).MaxLifetime;
                float lastSample = 0;
                while (Time.realtimeSinceStartup - primeT < 8f)
                {
                    float t = Time.realtimeSinceStartup;
                    if (t - lastSample >= 0.25f)
                    {
                        lastSample = t;
                        realSeries.Add(ReadParticles(realL.Systems, part.transform, null).Alive);
                        ghostSeries.Add(ReadParticles(ghostL.Systems, part.transform, null).Alive);
                        if (realPlat < 0 && t - pressT >= realMaxLt && Stable(realSeries)) realPlat = t - pressT;
                        if (ghostPlat < 0 && t - primeT >= ghostMaxLt && Stable(ghostSeries)) ghostPlat = t - primeT;
                        if (realPlat >= 0 && ghostPlat >= 0) break;
                    }
                    yield return null;
                }
                ParsekLog.Info(Tag, $"plateau {label}: realMaxLifetime={realMaxLt:F2} ghostMaxLifetime={ghostMaxLt:F2} realPlateauS={realPlat:F2} (from press) " +
                    $"ghostPlateauS={ghostPlat:F2} (from prime) realSeries=[{Join(realSeries)}] ghostSeries=[{Join(ghostSeries)}]");

                // U5 per-system read on both sides.
                LogSystems(label, "real", realL.Systems);
                LogSystems(label, "ghost", ghostL.Systems);

                // Re-classify after plateau (lazily created Waterfall / particle renderers).
                realL = Classify("real", part.transform, ghostRoot);
                ghostL = Classify("ghost", ghostRoot, null);
                ParsekLog.Info(Tag, $"layers-after {label}: {realL.Describe()} | {ghostL.Describe()}");

                List<Renderer> others = OtherRenderers(real);
                IEnumerator cap = CaptureFx(rig, label, part.transform, realL, ghostL, others, hold, real);
                while (cap.MoveNext()) yield return cap.Current;

                // U10: intact ghost again vs tint-rotated and tint-greened ghost, fx-side-a flame only.
                IEnumerator u10 = TintControl(rig, label, part.transform, realL, ghostL, others);
                while (u10.MoveNext()) yield return u10.Current;

                ParsekLog.Info(Tag, $"U3 hold {label}: idle[{holdIdle}] burning[{hold.Describe()}] finalThrust={target.finalThrust:F2} " +
                    $"mass={real.totalMass:F3} accel={(target.finalThrust / Math.Max(0.001, real.totalMass)):F1}m/s2 caseWallS={swatch.Elapsed.TotalSeconds:F1}");
            }
            finally
            {
                Teardown(real, pid, ghosts, hold, label);
            }
            yield return null; yield return null;
        }

        private static string PartParitySpikeLabel(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s ?? "") sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
            return sb.ToString();
        }

        private static bool Stable(List<int> series)
        {
            if (series.Count < 3) return false;
            int a = series[series.Count - 3], b = series[series.Count - 1];
            if (b == 0) return false;
            return Math.Abs(b - a) <= 0.1 * Math.Max(a, b);
        }

        private static string Join(List<int> l)
        {
            var s = new List<string>(); foreach (int i in l) s.Add(i.ToString(IC)); return string.Join(",", s.ToArray());
        }

        private static void LogSystems(string label, string side, List<ParticleSystem> systems)
        {
            var sb = new StringBuilder();
            int i = 0;
            foreach (ParticleSystem ps in systems)
            {
                if (ps == null) continue;
                KSPParticleEmitter kpe = ps.GetComponent<KSPParticleEmitter>();
                int n = ps.GetParticles(Buf);
                var r = ps.GetComponent<ParticleSystemRenderer>();
                string sh = r != null && r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "-";
                string tint = "-";
                if (r != null && r.sharedMaterial != null)
                {
                    if (r.sharedMaterial.HasProperty("_TintColor")) tint = "T" + r.sharedMaterial.GetColor("_TintColor");
                    else if (r.sharedMaterial.HasProperty("_Color")) tint = "C" + r.sharedMaterial.GetColor("_Color");
                }
                sb.Append($"#{i} {ps.name} space={ps.main.simulationSpace} get={n} count={ps.particleCount} playing={ps.isPlaying} " +
                    $"kspE={(kpe != null)}{(kpe != null ? $"(emit={kpe.emit},ws={kpe.useWorldSpace},maxE={kpe.maxEnergy:F2})" : "")} " +
                    $"rend={(r != null && r.enabled)} shader={sh} tint={tint}; ");
                i++;
            }
            ParsekLog.Info(Tag, $"U5 systems {label} {side}: {sb}");
        }

        private static void SetOff(IEnumerable<Renderer> rs, bool off, Dictionary<Renderer, bool> saved)
        {
            foreach (Renderer r in rs)
            {
                if (r == null) continue;
                if (saved != null && !saved.ContainsKey(r)) saved[r] = r.forceRenderingOff;
                r.forceRenderingOff = off;
            }
        }

        private static Bounds LocalBounds(Transform partT, IEnumerable<Renderer> rs, ref bool any, Bounds b)
        {
            foreach (Renderer r in rs)
            {
                if (r == null || !r.gameObject.activeInHierarchy) continue;
                Bounds wb = r.bounds;
                if (wb.size.sqrMagnitude <= 0f) continue;
                Vector3 c = wb.center, e = wb.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = c + new Vector3((i & 1) != 0 ? e.x : -e.x, (i & 2) != 0 ? e.y : -e.y, (i & 4) != 0 ? e.z : -e.z);
                    Vector3 l = partT.InverseTransformPoint(corner);
                    if (!any) { b = new Bounds(l, Vector3.zero); any = true; } else b.Encapsulate(l);
                }
            }
            return b;
        }

        /// <summary>Frames the camera on a part-local box for a view.</summary>
        private static void Frame(Camera cam, Transform partT, Bounds local, View v)
        {
            Vector3 dir = partT.TransformDirection(v.Dir).normalized;
            Vector3 up = partT.TransformDirection(v.Up).normalized;
            Quaternion rot = Quaternion.LookRotation(-dir, up);
            Vector3 centerW = partT.TransformPoint(local.center);
            float reach = local.extents.magnitude;
            cam.transform.SetPositionAndRotation(centerW + dir * (reach + 2f), rot);
            float halfW = 0, halfH = 0, dMin = float.MaxValue, dMax = float.MinValue;
            Vector3 c = local.center, e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = partT.TransformPoint(c + new Vector3((i & 1) != 0 ? e.x : -e.x, (i & 2) != 0 ? e.y : -e.y, (i & 4) != 0 ? e.z : -e.z));
                Vector3 l = cam.transform.InverseTransformPoint(corner);
                halfW = Mathf.Max(halfW, Mathf.Abs(l.x)); halfH = Mathf.Max(halfH, Mathf.Abs(l.y));
                dMin = Mathf.Min(dMin, l.z); dMax = Mathf.Max(dMax, l.z);
            }
            cam.orthographicSize = Mathf.Max(0.05f, Mathf.Max(halfW, halfH) * 1.08f);
            cam.nearClipPlane = Mathf.Max(0.01f, dMin - 0.5f);
            cam.farClipPlane = dMax + 0.5f;
        }

        private sealed class LightRig : IDisposable
        {
            private Light[] scene; private bool[] was; private Color amb; private UnityEngine.Rendering.AmbientMode ambMode; private int pix;
            private GameObject key, fill;
            public int SceneLights => scene.Length;
            public LightRig(Quaternion camRot, Vector3 up, bool keyFill, Color ambient, ICollection<Light> keepOn)
            {
                scene = UnityEngine.Object.FindObjectsOfType<Light>();
                was = new bool[scene.Length];
                for (int i = 0; i < scene.Length; i++)
                {
                    was[i] = scene[i].enabled;
                    scene[i].enabled = keepOn != null && keepOn.Contains(scene[i]) && was[i];
                }
                amb = RenderSettings.ambientLight; ambMode = RenderSettings.ambientMode; pix = QualitySettings.pixelLightCount;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = ambient;
                QualitySettings.pixelLightCount = 8;
                if (keyFill)
                {
                    key = new GameObject("PPFXKey"); var k = key.AddComponent<Light>();
                    k.type = LightType.Directional; k.intensity = 1f; k.shadows = LightShadows.None; k.cullingMask = -1; k.renderMode = LightRenderMode.ForcePixel;
                    key.transform.rotation = Quaternion.LookRotation(camRot * new Vector3(0.5f, -0.6f, 1f).normalized, up);
                    fill = new GameObject("PPFXFill"); var f = fill.AddComponent<Light>();
                    f.type = LightType.Directional; f.intensity = 0.45f; f.shadows = LightShadows.None; f.cullingMask = -1; f.renderMode = LightRenderMode.ForcePixel;
                    fill.transform.rotation = Quaternion.LookRotation(camRot * new Vector3(-0.6f, 0.3f, 1f).normalized, up);
                }
            }
            public void Dispose()
            {
                if (key != null) UnityEngine.Object.DestroyImmediate(key);
                if (fill != null) UnityEngine.Object.DestroyImmediate(fill);
                for (int i = 0; i < scene.Length; i++) if (scene[i] != null) scene[i].enabled = was[i];
                RenderSettings.ambientMode = ambMode; RenderSettings.ambientLight = amb; QualitySettings.pixelLightCount = pix;
            }
        }

        private static int MaskOf(params SideLayers[] sides)
        {
            int m = 0;
            foreach (var s in sides) foreach (int l in s.Layers) m |= 1 << l;
            return m;
        }

        /// <summary>
        /// FX accumulation: N samples per view round robin (one view per frame), each sample
        /// rendering each side's flame (over black) and smoke (black + white). Also K=6 strip frames
        /// (body + fx, dark grey) of fx-side-a per side, particle stats each sample, the U4 saturation
        /// counts and the U6 render cost.
        /// </summary>
        private IEnumerator CaptureFx(Rig rig, string label, Transform partT, SideLayers realL, SideLayers ghostL,
            List<Renderer> others, FxSpikeHold hold, Vessel real)
        {
            // Framing: near = body box stretched along -Y to three body lengths; far = body + every FX bound, clamped to 80 m.
            bool any = false;
            Bounds bodyB = LocalBounds(partT, realL.Body, ref any, new Bounds());
            bodyB = LocalBounds(partT, ghostL.Body, ref any, bodyB);
            if (!any) bodyB = new Bounds(Vector3.zero, Vector3.one);
            Bounds nearB = bodyB;
            float len = Mathf.Max(0.5f, bodyB.size.y);
            nearB.Encapsulate(new Vector3(bodyB.center.x, bodyB.min.y - 2f * len, bodyB.center.z));
            Bounds farB = bodyB; bool anyF = true;
            var fxR = new List<Renderer>(); fxR.AddRange(realL.Flame); fxR.AddRange(realL.Smoke); fxR.AddRange(ghostL.Flame); fxR.AddRange(ghostL.Smoke);
            farB = LocalBounds(partT, fxR, ref anyF, farB);
            bool clamped = false;
            if (farB.min.y < bodyB.min.y - 80f) { Vector3 mn = farB.min; mn.y = bodyB.min.y - 80f; Vector3 mx = farB.max; farB.SetMinMax(mn, mx); clamped = true; }
            if (farB.max.y > bodyB.max.y + 80f) { Vector3 mx = farB.max; mx.y = bodyB.max.y + 80f; Vector3 mn = farB.min; farB.SetMinMax(mn, mx); clamped = true; }
            Vector3 fe = farB.extents;
            if (fe.x > 80f || fe.z > 80f) { farB.extents = new Vector3(Mathf.Min(fe.x, 80f), fe.y, Mathf.Min(fe.z, 80f)); clamped = true; }
            ParsekLog.Info(Tag, $"framing {label}: body={bodyB.center}/{bodyB.size} near={nearB.center}/{nearB.size} far={farB.center}/{farB.size} clamped={clamped}");

            int mask = MaskOf(realL, ghostL);
            rig.Cam.cullingMask = mask;
            var acc = new Dictionary<string, Accum>();
            Func<string, Accum> A = k => { if (!acc.TryGetValue(k, out Accum a)) { a = new Accum(); acc[k] = a; } return a; };
            var strips = new List<byte[]>[] { new List<byte[]>(), new List<byte[]>() };
            var realP = new List<ParticleStats>(); var ghostP = new List<ParticleStats>();
            var realSmokeP = new List<ParticleStats>(); var ghostSmokeP = new List<ParticleStats>();
            var smokeSys = new HashSet<ParticleSystem>();
            foreach (var r in realL.Smoke) { var ps = r.GetComponent<ParticleSystem>(); if (ps != null) smokeSys.Add(ps); }
            foreach (var r in ghostL.Smoke) { var ps = r.GetComponent<ParticleSystem>(); if (ps != null) smokeSys.Add(ps); }
            float t0 = Time.realtimeSinceStartup;
            int frames = Samples * FxViews.Length;
            for (int f = 0; f < frames; f++)
            {
                yield return new WaitForEndOfFrame();
                if (real == null || real.rootPart == null) { failures.Add(label + ": real lost mid-capture"); yield break; }
                View v = FxViews[f % FxViews.Length];
                Frame(rig.Cam, partT, v.Far ? farB : (v.Name == "fx-bottom" ? farB : nearB), v);
                realP.Add(ReadParticles(realL.Systems, partT, null));
                ghostP.Add(ReadParticles(ghostL.Systems, partT, null));
                realSmokeP.Add(ReadParticles(realL.Systems, partT, ps => smokeSys.Contains(ps)));
                ghostSmokeP.Add(ReadParticles(ghostL.Systems, partT, ps => smokeSys.Contains(ps)));
                var saved = new Dictionary<Renderer, bool>();
                Quaternion camRot = rig.Cam.transform.rotation;
                using (new LightRig(camRot, rig.Cam.transform.up, true, new Color(0.25f, 0.25f, 0.25f, 1f), null))
                {
                    try
                    {
                        SetOff(others, true, saved);
                        SideLayers[] sides = { realL, ghostL };
                        for (int si = 0; si < 2; si++)
                        {
                            SetOff(realL.All(), true, saved); SetOff(ghostL.All(), true, saved);
                            SideLayers s = sides[si];
                            SetOff(s.Flame, false, saved);
                            A(v.Name + "/" + s.Name + "/flame").AddFlame(rig.Render(Color.black, "flame"));
                            SetOff(s.Flame, true, saved);
                            SetOff(s.Smoke, false, saved);
                            byte[] bk = rig.Render(Color.black, "smoke");
                            byte[] wh = rig.Render(Color.white, "smoke");
                            A(v.Name + "/" + s.Name + "/smoke").AddSmoke(bk, wh);
                            SetOff(s.Smoke, true, saved);
                            if (v.Name == "fx-side-a" && strips[si].Count < 6 && (f / FxViews.Length) % 2 == 0)
                            {
                                SetOff(s.All(), false, saved);
                                strips[si].Add(rig.Render(new Color(0.15f, 0.15f, 0.15f), "composite"));
                                SetOff(s.All(), true, saved);
                            }
                            if (f == 0)
                            {
                                SetOff(s.Body, false, saved);
                                rig.Render(Color.black, "body");
                                SetOff(s.Body, true, saved);
                            }
                        }
                    }
                    finally
                    {
                        foreach (var kv in saved) if (kv.Key != null) kv.Key.forceRenderingOff = kv.Value;
                    }
                }
            }
            float wall = Time.realtimeSinceStartup - t0;
            foreach (var kv in acc) ParsekLog.Info(Tag, "U4 accum " + label + " " + kv.Value.Describe(kv.Key));
            ParsekLog.Info(Tag, $"U6 capture {label}: frames={frames} wallS={wall:F2} perFrameMs={(1000.0 * wall / frames):F1} mask=0x{mask:X} {rig.CostLine()}");
            ParsekLog.Info(Tag, $"U5/U8 particles {label}: real {Fmt(Mean(realP))} | ghost {Fmt(Mean(ghostP))}");
            ParsekLog.Info(Tag, $"U8 smoke {label}: real {Fmt(Mean(realSmokeP))} | ghost {Fmt(Mean(ghostSmokeP))} hold[{hold.Describe()}] kb={Krakensbane.GetFrameVelocity().magnitude:F1}");
            // Split halves of the sample particle stats (odd / even) for the real side alone.
            var even = new List<ParticleStats>(); var odd = new List<ParticleStats>();
            for (int i = 0; i < realP.Count; i++) (i % 2 == 0 ? even : odd).Add(realP[i]);
            ParsekLog.Info(Tag, $"halves {label}: realEven {Fmt(Mean(even))} | realOdd {Fmt(Mean(odd))}");

            // Atlas: rows real-flame, ghost-flame, real-smoke, ghost-smoke; cols the FX views (threshold 16 / alpha 128).
            var rows = new byte[4][][];
            string[] keys = { "real/flame", "ghost/flame", "real/smoke", "ghost/smoke" };
            for (int r = 0; r < 4; r++)
            {
                rows[r] = new byte[FxViews.Length][];
                for (int c = 0; c < FxViews.Length; c++)
                {
                    string k = FxViews[c].Name + "/" + keys[r];
                    rows[r][c] = acc.TryGetValue(k, out Accum a) ? a.ToTile(keys[r].EndsWith("smoke") ? 2 : 2) : new byte[Tile * Tile * 4];
                }
            }
            WriteAtlas("ppfx-" + label + "-fx.png", rows, FxViews.Length);
            if (strips[0].Count > 0 && strips[1].Count > 0)
            {
                int k = Math.Min(strips[0].Count, strips[1].Count);
                var srows = new byte[2][][];
                for (int si = 0; si < 2; si++) { srows[si] = new byte[k][]; for (int i = 0; i < k; i++) srows[si][i] = strips[si][i]; }
                WriteAtlas("ppfx-" + label + "-strip.png", srows, k);
            }
        }

        private static ParticleStats Mean(List<ParticleStats> l)
        {
            var m = new ParticleStats();
            if (l.Count == 0) return m;
            double a = 0, pc = 0, sp = 0, am = 0, ap = 0, xp = 0, cx = 0, cz = 0;
            foreach (var s in l) { a += s.Alive; pc += s.PsCount; sp += s.SpeedMean; am += s.AlongMean; ap += s.AlongP95; xp += s.AcrossP95; cx += s.CentroidX; cz += s.CentroidZ; if (s.MaxLifetime > m.MaxLifetime) m.MaxLifetime = s.MaxLifetime; m.Live = Math.Max(m.Live, s.Live); }
            int n = l.Count;
            m.Alive = (int)(a / n); m.PsCount = (int)(pc / n); m.SpeedMean = sp / n; m.AlongMean = am / n; m.AlongP95 = ap / n; m.AcrossP95 = xp / n; m.CentroidX = cx / n; m.CentroidZ = cz / n;
            return m;
        }

        private static void WriteAtlas(string file, byte[][][] rows, int cols)
        {
            try
            {
                byte[] img = PartParityImage.BuildAtlas(rows, cols, Tile, out int w, out int h);
                byte[] png = PartParityPng.EncodeRgba(w, h, img);
                string dir = Path.Combine(KSPUtil.ApplicationRootPath ?? string.Empty, "Screenshots");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, file), png);
                filesWritten++;
                ParsekLog.Info(Tag, $"wrote Screenshots/{file} {w}x{h} bytes={png.Length}");
            }
            catch (Exception ex) { failures.Add(file + ": " + ex.GetType().Name + " " + ex.Message); }
        }

        /// <summary>U10: fx-side-a flame layer of the ghost only, N samples each: intact (A), intact
        /// again (B, the noise floor), tint channels rotated (r,g,b)->(b,r,g) on instanced materials
        /// (C), tint (0, max, 0) (D). Also the real side (R) over the same frames.</summary>
        private IEnumerator TintControl(Rig rig, string label, Transform partT, SideLayers realL, SideLayers ghostL, List<Renderer> others)
        {
            bool any = false;
            Bounds bodyB = LocalBounds(partT, realL.Body, ref any, new Bounds());
            if (!any) bodyB = new Bounds(Vector3.zero, Vector3.one);
            Bounds nearB = bodyB; float len = Mathf.Max(0.5f, bodyB.size.y);
            nearB.Encapsulate(new Vector3(bodyB.center.x, bodyB.min.y - 2f * len, bodyB.center.z));
            var results = new Dictionary<string, Accum>();
            var instanced = new List<KeyValuePair<Renderer, Material>>();
            string[] passes = { "A", "B", "C", "D" };
            var tintLog = new StringBuilder();
            foreach (string pass in passes)
            {
                if (pass == "C" || pass == "D")
                {
                    foreach (var kv in instanced) { if (kv.Key != null) { var inst = kv.Key.material; kv.Key.sharedMaterial = kv.Value; UnityEngine.Object.Destroy(inst); } }
                    instanced.Clear();
                    foreach (Renderer r in ghostL.Flame)
                    {
                        if (r == null || r.sharedMaterial == null) continue;
                        Material orig = r.sharedMaterial;
                        Material inst = r.material;
                        instanced.Add(new KeyValuePair<Renderer, Material>(r, orig));
                        foreach (string prop in new[] { "_TintColor", "_Color" })
                        {
                            if (!inst.HasProperty(prop)) continue;
                            Color c = inst.GetColor(prop);
                            Color n = pass == "C" ? new Color(c.b, c.r, c.g, c.a) : new Color(0f, Mathf.Max(c.r, Mathf.Max(c.g, c.b)), 0f, c.a);
                            inst.SetColor(prop, n);
                            if (tintLog.Length < 600) tintLog.Append($"{pass}:{r.name}.{prop} {c} -> {n}; ");
                        }
                    }
                }
                var a = new Accum(); var ra = new Accum();
                for (int i = 0; i < Samples; i++)
                {
                    yield return new WaitForEndOfFrame();
                    if (partT == null) yield break;
                    Frame(rig.Cam, partT, nearB, FxViews[0]);
                    var saved = new Dictionary<Renderer, bool>();
                    using (new LightRig(rig.Cam.transform.rotation, rig.Cam.transform.up, true, new Color(0.25f, 0.25f, 0.25f, 1f), null))
                    {
                        try
                        {
                            SetOff(others, true, saved);
                            SetOff(realL.All(), true, saved); SetOff(ghostL.All(), true, saved);
                            SetOff(ghostL.Flame, false, saved);
                            a.AddFlame(rig.Render(Color.black, "flame-u10"));
                            SetOff(ghostL.Flame, true, saved);
                            SetOff(realL.Flame, false, saved);
                            ra.AddFlame(rig.Render(Color.black, "flame-u10"));
                        }
                        finally { foreach (var kv in saved) if (kv.Key != null) kv.Key.forceRenderingOff = kv.Value; }
                    }
                }
                results[pass] = a;
                ParsekLog.Info(Tag, $"U10 {label} pass={pass}: " + a.Describe("ghost") + " || " + ra.Describe("real"));
            }
            foreach (var kv in instanced) { if (kv.Key != null) { var inst = kv.Key.material; kv.Key.sharedMaterial = kv.Value; UnityEngine.Object.Destroy(inst); } }
            ParsekLog.Info(Tag, $"U10 tints {label}: {tintLog}");
        }

        // ===================================================================================
        // RCS (U2)
        // ===================================================================================

        private static readonly Vector3[] Directions = BuildDirections();

        private static Vector3[] BuildDirections()
        {
            var l = new List<Vector3>();
            int[] v = { -1, 0, 1 };
            for (int pass = 1; pass <= 3; pass++)
                foreach (int x in v) foreach (int y in v) foreach (int z in v)
                        {
                            int nz = (x != 0 ? 1 : 0) + (y != 0 ? 1 : 0) + (z != 0 ? 1 : 0);
                            if (nz == pass) l.Add(new Vector3(x, y, z).normalized);
                        }
            return l.ToArray();
        }

        private IEnumerator RcsCase(Rig rig, Vessel host, string partName)
        {
            string label = PartParitySpikeLabel(partName);
            uint pid = Spawn(host, partName, false, null, label);
            if (pid == 0) { failures.Add(label + ": spawn pid=0"); yield break; }
            Vessel real = null; FxSpikeHold hold = null;
            var ghosts = new List<GhostCoOrbitalRig.LiveGhost>();
            try
            {
                IEnumerator w = WaitLive(pid, v => real = v);
                while (w.MoveNext()) yield return w.Current;
                if (real == null) { failures.Add(label + ": never live"); yield break; }
                hold = StartHold(real, host);
                IEnumerator s1 = Seconds(1.0f); while (s1.MoveNext()) yield return s1.Current;
                Part part = real.rootPart;
                ConfigNode stowed = FlightRecorder.TryBackupSnapshot(real);
                var rcsMods = FlightRecorder.CacheRcsModules(real);
                if (rcsMods.Count == 0) { failures.Add(label + ": no rcs"); yield break; }
                ModuleRCS rcs = rcsMods[0].rcs; int midx = rcsMods[0].moduleIndex;
                Transform refT = real.ReferenceTransform;
                // Choose input: the most nozzles at >= 0.5 of the strongest, then the smallest spread, then the largest mean.
                Vector3 best = Vector3.zero; int bestN = -1; float bestSpread = float.MaxValue, bestMean = -1;
                var cand = new StringBuilder();
                foreach (Vector3 d in Directions)
                {
                    Vector3 worldIn = refT.rotation * new Vector3(d.x, d.y, d.z);
                    var forces = new List<float>();
                    foreach (Transform t in rcs.thrusterTransforms)
                    {
                        Vector3 axis = rcs.useZaxis ? t.forward : t.up;
                        forces.Add(Mathf.Clamp01(Mathf.Max(Vector3.Dot(axis, worldIn), 0f)));
                    }
                    float mx = 0; foreach (float f in forces) mx = Mathf.Max(mx, f);
                    var fired = new List<float>(); foreach (float f in forces) if (mx > 0 && f >= 0.5f * mx) fired.Add(f);
                    float mean = 0, mn = float.MaxValue, mxx = 0; foreach (float f in fired) { mean += f; mn = Mathf.Min(mn, f); mxx = Mathf.Max(mxx, f); }
                    mean = fired.Count > 0 ? mean / fired.Count : 0; float spread = fired.Count > 0 ? mxx - mn : float.MaxValue;
                    if (fired.Count > bestN || (fired.Count == bestN && (spread < bestSpread - 1e-4f || (Math.Abs(spread - bestSpread) <= 1e-4f && mean > bestMean + 1e-4f))))
                    { best = d; bestN = fired.Count; bestSpread = spread; bestMean = mean; }
                }
                // ctrlState mapping (ModuleRCS.Update): vessel-local inputLin = (X, Z, Y).
                hold.CtrlX = best.x; hold.CtrlZ = best.y; hold.CtrlY = best.z;
                ParsekLog.Info(Tag, $"U2 rcs {label}: nozzles={rcs.thrusterTransforms.Count} useZaxis={rcs.useZaxis} thrusterPower={rcs.thrusterPower} " +
                    $"refT={(refT != null ? refT.name : "null")} rootT={part.transform.name} controllable={real.IsControllable} chosen={best} predictedFired={bestN} spread={bestSpread:F3} mean={bestMean:F3} " +
                    $"ctrlBefore=({real.ctrlState.X:F2},{real.ctrlState.Y:F2},{real.ctrlState.Z:F2}) rcsGroup={real.ActionGroups[KSPActionGroup.RCS]} rcsEnabled={rcs.rcsEnabled}");
                hold.WriteCtrl = true;
                real.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
                int activeFrames = 0, firstActive = -1, frame = 0;
                float pressT = Time.realtimeSinceStartup;
                var trace = new StringBuilder();
                while (Time.realtimeSinceStartup - pressT < 5f)
                {
                    yield return new WaitForFixedUpdate();
                    frame++;
                    bool active = rcs.rcs_active && rcs.rcsEnabled;
                    float sum = 0; int on = 0;
                    if (rcs.thrustForces != null) foreach (float f in rcs.thrustForces) { sum += f; if (f > 0.01f) on++; }
                    if (active && sum > 0) { activeFrames++; if (firstActive < 0) firstActive = frame; }
                    if (frame <= 3 || frame % 25 == 0)
                        trace.Append($"[f{frame} act={rcs.rcs_active} on={on} sum={sum:F2} ctrl=({real.ctrlState.X:F2},{real.ctrlState.Y:F2},{real.ctrlState.Z:F2})] ");
                    if (activeFrames >= 8 + 25) break;
                }
                string forceList = rcs.thrustForces != null ? string.Join(",", Array.ConvertAll(rcs.thrustForces, x => x.ToString("F2", IC))) : "null";
                float power = FlightRecorder.ComputeRcsPower(rcs.thrustForces, rcs.thrusterPower);
                ParsekLog.Info(Tag, $"U2 fire {label}: activeFrames={activeFrames} firstActiveFrame={firstActive} forces=[{forceList}] power={power:F3} " +
                    $"overwrites update={hold.CtrlOverUpdate} fixed={hold.CtrlOverFixed} afterPhysics={hold.CtrlOverAfterPhysics} checks={hold.CtrlChecks} first={hold.FirstOverwrite ?? "none"} " +
                    $"hold[{hold.Describe()}] trace={trace}");
                double ut = Planetarium.GetUniversalTime();
                var events = new List<PartEvent> { new PartEvent { ut = ut - 1.0, partPersistentId = part.persistentId,
                    eventType = PartEventType.RCSActivated, partName = part.partInfo.name, value = power, moduleIndex = midx } };
                var ghost = BuildGhost(stowed, real, events, label);
                if (ghost == null) yield break;
                ghosts.Add(ghost);
                IEnumerator s2 = Seconds(2.0f); while (s2.MoveNext()) yield return s2.Current;
                Transform gr = ghost.Build.root.transform;
                SideLayers realL = Classify("real", part.transform, gr);
                SideLayers ghostL = Classify("ghost", gr, null);
                ParsekLog.Info(Tag, $"layers {label}: {realL.Describe()} | {ghostL.Describe()}");
                LogSystems(label, "real", realL.Systems);
                LogSystems(label, "ghost", ghostL.Systems);
                // Per real nozzle: which of the real systems emit (a nozzle's systems sit under its thruster transform).
                var perNozzle = new StringBuilder();
                for (int i = 0; i < rcs.thrusterTransforms.Count; i++)
                {
                    Transform t = rcs.thrusterTransforms[i];
                    int alive = 0;
                    foreach (ParticleSystem ps in t.GetComponentsInChildren<ParticleSystem>(true)) alive += ps.particleCount;
                    perNozzle.Append($"{i}:{(rcs.thrustForces != null && i < rcs.thrustForces.Length ? rcs.thrustForces[i] : -1):F2}/{alive} ");
                }
                ParsekLog.Info(Tag, $"U2 nozzles {label}: force/alive {perNozzle}");
                List<Renderer> others = OtherRenderers(real);
                IEnumerator cap = CaptureFx(rig, label, part.transform, realL, ghostL, others, hold, real);
                while (cap.MoveNext()) yield return cap.Current;
                ParsekLog.Info(Tag, $"U2 end {label}: overwrites update={hold.CtrlOverUpdate} fixed={hold.CtrlOverFixed} afterPhysics={hold.CtrlOverAfterPhysics} checks={hold.CtrlChecks} hold[{hold.Describe()}]");
            }
            finally
            {
                if (real != null) { try { real.ActionGroups.SetGroup(KSPActionGroup.RCS, false); } catch { } }
                Teardown(real, pid, ghosts, hold, label);
            }
            yield return null; yield return null;
        }

        // ===================================================================================
        // light on a plate (U9)
        // ===================================================================================

        private IEnumerator PlateCase(Rig rig, Vessel host, string partName)
        {
            string label = PartParitySpikeLabel(partName) + "-plate";
            uint pid = Spawn(host, partName, false, null, label);
            if (pid == 0) { failures.Add(label + ": spawn pid=0"); yield break; }
            Vessel real = null; FxSpikeHold hold = null; GameObject plate = null; Material plateMat = null;
            var ghosts = new List<GhostCoOrbitalRig.LiveGhost>();
            try
            {
                IEnumerator w = WaitLive(pid, v => real = v);
                while (w.MoveNext()) yield return w.Current;
                if (real == null) { failures.Add(label + ": never live"); yield break; }
                hold = StartHold(real, host);
                IEnumerator s1 = Seconds(1.0f); while (s1.MoveNext()) yield return s1.Current;
                Part part = real.rootPart;
                ConfigNode stowed = FlightRecorder.TryBackupSnapshot(real);
                ModuleLight ml = part.FindModuleImplementing<ModuleLight>();
                if (ml == null) { failures.Add(label + ": no ModuleLight"); yield break; }
                ml.LightsOn();
                IEnumerator s2 = Seconds(ml.lightBrightenSpeed + 1.5f); while (s2.MoveNext()) yield return s2.Current;
                var events = new List<PartEvent> { new PartEvent { ut = Planetarium.GetUniversalTime() - 1.0, partPersistentId = part.persistentId,
                    eventType = PartEventType.LightOn, partName = part.partInfo.name, value = 0f, moduleIndex = 0 } };
                var ghost = BuildGhost(stowed, real, events, label);
                if (ghost != null) ghosts.Add(ghost);
                IEnumerator s3 = Seconds(1.0f); while (s3.MoveNext()) yield return s3.Current;
                Transform gr = ghost != null ? ghost.Build.root.transform : null;
                var realLights = new List<Light>(); var ghostLights = new List<Light>();
                foreach (Light l in part.GetComponentsInChildren<Light>(true)) if (gr == null || !l.transform.IsChildOf(gr)) realLights.Add(l);
                if (gr != null) foreach (Light l in gr.GetComponentsInChildren<Light>(true)) ghostLights.Add(l);
                var ld = new StringBuilder();
                foreach (Light l in realLights) ld.Append($"real {l.name} type={l.type} en={l.enabled} act={l.gameObject.activeInHierarchy} int={l.intensity:F2} range={l.range:F1} spot={l.spotAngle:F1} col={l.color} mask=0x{l.cullingMask:X} mode={l.renderMode} layer={l.gameObject.layer}; ");
                foreach (Light l in ghostLights) ld.Append($"ghost {l.name} type={l.type} en={l.enabled} act={l.gameObject.activeInHierarchy} int={l.intensity:F2} range={l.range:F1} spot={l.spotAngle:F1} col={l.color} mask=0x{l.cullingMask:X} mode={l.renderMode}; ");
                ParsekLog.Info(Tag, $"U9 lights {label}: isOn={ml.isOn} brighten={ml.lightBrightenSpeed} {ld}");
                Light main = null;
                foreach (Light l in realLights) if (l.enabled && l.gameObject.activeInHierarchy) { main = l; break; }
                if (main == null && realLights.Count > 0) main = realLights[0];
                if (main == null) { failures.Add(label + ": no Light"); yield break; }
                // Layer: the part's first rendered renderer.
                int layer = 0;
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true)) if (r.enabled && (gr == null || !r.transform.IsChildOf(gr))) { layer = r.gameObject.layer; break; }
                bool spot = main.type == LightType.Spot;
                float dist = spot ? 3f : 1f;
                float size = spot ? Mathf.Max(2f, 2f * dist * Mathf.Tan(main.spotAngle * 0.5f * Mathf.Deg2Rad) * 1.3f) : 3f;
                Vector3 fwd = main.transform.forward;
                Vector3 plateCenter = main.transform.position + fwd * dist;
                plate = GameObject.CreatePrimitive(PrimitiveType.Quad);
                UnityEngine.Object.DestroyImmediate(plate.GetComponent<Collider>());
                plate.name = "PPFXPlate";
                plate.layer = layer;
                Shader sh = Shader.Find("KSP/Diffuse") ?? Shader.Find("Diffuse") ?? Shader.Find("Standard");
                plateMat = new Material(sh) { color = new Color(0.7f, 0.7f, 0.7f, 1f) };
                plate.GetComponent<Renderer>().sharedMaterial = plateMat;
                plate.transform.position = plateCenter;
                plate.transform.rotation = Quaternion.LookRotation(fwd, Mathf.Abs(Vector3.Dot(fwd, part.transform.up)) > 0.9f ? part.transform.forward : part.transform.up);
                plate.transform.localScale = new Vector3(size, size, 1f);
                bool maskReaches = (main.cullingMask & (1 << layer)) != 0;
                // Camera on the light side, looking at the plate.
                rig.Cam.transform.SetPositionAndRotation(plateCenter - fwd * (dist * 0.5f), Quaternion.LookRotation(fwd, plate.transform.up));
                rig.Cam.orthographicSize = size * 0.5f;
                rig.Cam.nearClipPlane = 0.01f; rig.Cam.farClipPlane = dist + 1f;
                rig.Cam.cullingMask = 1 << layer;
                Renderer plateR = plate.GetComponent<Renderer>();
                var everything = new List<Renderer>(OtherRenderers(real));
                everything.AddRange(part.GetComponentsInChildren<Renderer>(true));
                var res = new StringBuilder();
                byte[] realImg = null, ghostImg = null, offImg = null;
                string[] passes = { "real", "ghost", "off", "real-otherlayer" };
                foreach (string pass in passes)
                {
                    yield return new WaitForEndOfFrame();
                    var keep = new List<Light>();
                    if (pass == "real" || pass == "real-otherlayer") keep.AddRange(realLights);
                    if (pass == "ghost") keep.AddRange(ghostLights);
                    var savedR = new Dictionary<Renderer, bool>();
                    var savedLights = new Dictionary<Light, bool>();
                    int savedLayer = plate.layer;
                    try
                    {
                        // The other side's lights off for this render (the rig keeps only 'keep' on).
                        if (pass == "real-otherlayer") { plate.layer = 15; rig.Cam.cullingMask = 1 << 15; }
                        else rig.Cam.cullingMask = 1 << layer;
                        foreach (Light l in ghostLights) { savedLights[l] = l.enabled; if (pass != "ghost") l.enabled = false; }
                        foreach (Light l in realLights) { savedLights[l] = l.enabled; if (pass == "ghost" || pass == "off") l.enabled = false; }
                        var keepOn = new HashSet<Light>(keep);
                        using (new LightRig(rig.Cam.transform.rotation, rig.Cam.transform.up, false, Color.black, keepOn))
                        {
                            SetOff(everything, true, savedR);
                            plateR.forceRenderingOff = false;
                            byte[] img = rig.Render(Color.black, "plate");
                            int lit2 = 0, lit8 = 0, lit32 = 0; double lum = 0;
                            for (int o = 0; o < img.Length; o += 4)
                            {
                                double L = 0.2126 * img[o] + 0.7152 * img[o + 1] + 0.0722 * img[o + 2];
                                lum += L; if (L >= 2) lit2++; if (L >= 8) lit8++; if (L >= 32) lit32++;
                            }
                            int total = Tile * Tile;
                            res.Append($"{pass}: lit2={(100.0 * lit2 / total):F2}% lit8={(100.0 * lit8 / total):F2}% lit32={(100.0 * lit32 / total):F2}% meanLum={(lum / total):F2}; ");
                            if (pass == "real") realImg = img; else if (pass == "ghost") ghostImg = img; else if (pass == "off") offImg = img;
                        }
                    }
                    finally
                    {
                        foreach (var kv in savedR) if (kv.Key != null) kv.Key.forceRenderingOff = kv.Value;
                        foreach (var kv in savedLights) if (kv.Key != null) kv.Key.enabled = kv.Value;
                        plate.layer = savedLayer;
                    }
                }
                ParsekLog.Info(Tag, $"U9 plate {label}: light={main.name} type={main.type} range={main.range:F1} spot={main.spotAngle:F1} mask=0x{main.cullingMask:X} " +
                    $"plateLayer={layer} maskReachesLayer={maskReaches} dist={dist} size={size:F2} shader={sh?.name} ghostLights={ghostLights.Count} {res}");
                if (realImg != null)
                    WriteAtlas("ppfx-" + label + ".png", new[] { new[] { realImg, ghostImg ?? new byte[Tile * Tile * 4], offImg ?? new byte[Tile * Tile * 4] } }, 3);
            }
            finally
            {
                if (plate != null) UnityEngine.Object.Destroy(plate);
                if (plateMat != null) UnityEngine.Object.Destroy(plateMat);
                Teardown(real, pid, ghosts, hold, label);
            }
            yield return null; yield return null;
        }
    }
}
