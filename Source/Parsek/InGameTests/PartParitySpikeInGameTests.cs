using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Pure helpers for the P0 capture spike: a minimal RGBA8 PNG writer (zlib over
    /// DeflateStream, no ImageConversionModule reference) and the double-clear matte.
    /// </summary>
    internal static class PartParitySpikeMath
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        internal static uint Crc32(byte[] data, int offset, int count)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
                c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        internal static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521u;
                b = (b + a) % 65521u;
            }
            return (b << 16) | a;
        }

        /// <summary>RGBA8, rows top-down, filter 0 on every row.</summary>
        internal static byte[] EncodePngRgba(int width, int height, byte[] rgbaTopDown)
        {
            if (width <= 0 || height <= 0 || rgbaTopDown == null || rgbaTopDown.Length != width * height * 4)
                throw new ArgumentException("bad image size");
            int stride = width * 4;
            var raw = new byte[(stride + 1) * height];
            for (int y = 0; y < height; y++)
            {
                raw[y * (stride + 1)] = 0;
                Buffer.BlockCopy(rgbaTopDown, y * stride, raw, y * (stride + 1) + 1, stride);
            }
            byte[] deflated;
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true))
                    ds.Write(raw, 0, raw.Length);
                deflated = ms.ToArray();
            }
            var zlib = new byte[deflated.Length + 6];
            zlib[0] = 0x78; zlib[1] = 0x9C;
            Buffer.BlockCopy(deflated, 0, zlib, 2, deflated.Length);
            uint adler = Adler32(raw);
            zlib[zlib.Length - 4] = (byte)(adler >> 24);
            zlib[zlib.Length - 3] = (byte)(adler >> 16);
            zlib[zlib.Length - 2] = (byte)(adler >> 8);
            zlib[zlib.Length - 1] = (byte)adler;

            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var ihdr = new byte[13];
                WriteBE(ihdr, 0, (uint)width);
                WriteBE(ihdr, 4, (uint)height);
                ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
                WriteChunk(png, "IHDR", ihdr);
                WriteChunk(png, "IDAT", zlib);
                WriteChunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        private static void WriteBE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBE(len, 0, (uint)data.Length);
            s.Write(len, 0, 4);
            var body = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
            Buffer.BlockCopy(data, 0, body, 4, data.Length);
            s.Write(body, 0, body.Length);
            var crc = new byte[4];
            WriteBE(crc, 0, Crc32(body, 0, body.Length));
            s.Write(crc, 0, 4);
        }

        internal struct MatteStats
        {
            public int Opaque;     // alpha >= 0.98
            public int Edge;       // 0.02 < alpha < 0.98
            public int Inverted;   // white render darker than black render on some channel by > 2/255
        }

        /// <summary>
        /// Composes straight-alpha RGBA from a render over black and the same render over
        /// white (both RGBA8, any row order, same order out). alpha = 1 - mean(white - black);
        /// colour = black / alpha.
        /// </summary>
        internal static byte[] ComposeMatte(byte[] overBlack, byte[] overWhite, out MatteStats stats)
        {
            if (overBlack == null || overWhite == null || overBlack.Length != overWhite.Length)
                throw new ArgumentException("matte inputs differ");
            stats = new MatteStats();
            var outp = new byte[overBlack.Length];
            for (int i = 0; i < overBlack.Length; i += 4)
            {
                int dr = overWhite[i] - overBlack[i];
                int dg = overWhite[i + 1] - overBlack[i + 1];
                int db = overWhite[i + 2] - overBlack[i + 2];
                if (dr < -2 || dg < -2 || db < -2) stats.Inverted++;
                double a = 1.0 - (Math.Max(0, dr) + Math.Max(0, dg) + Math.Max(0, db)) / (3.0 * 255.0);
                if (a < 0) a = 0;
                if (a > 1) a = 1;
                if (a >= 0.98) stats.Opaque++;
                else if (a > 0.02) stats.Edge++;
                for (int c = 0; c < 3; c++)
                {
                    double v = a > 0.004 ? overBlack[i + c] / a : 0.0;
                    outp[i + c] = (byte)Math.Min(255.0, Math.Round(v));
                }
                outp[i + 3] = (byte)Math.Round(a * 255.0);
            }
            return outp;
        }

        /// <summary>Coverage (alpha >= 128) of each side and their intersection-over-union.</summary>
        internal static void CompareCoverage(byte[] a, byte[] b, out int covA, out int covB, out int xor, out double iou)
        {
            covA = 0; covB = 0; xor = 0;
            int inter = 0, union = 0;
            for (int i = 3; i < a.Length; i += 4)
            {
                bool ia = a[i] >= 128, ib = b[i] >= 128;
                if (ia) covA++;
                if (ib) covB++;
                if (ia != ib) xor++;
                if (ia && ib) inter++;
                if (ia || ib) union++;
            }
            iou = union > 0 ? (double)inter / union : 1.0;
        }

        /// <summary>Mean absolute RGB difference over pixels both sides cover, 0-255.</summary>
        internal static double MeanColourDelta(byte[] a, byte[] b)
        {
            long sum = 0; int n = 0;
            for (int i = 0; i < a.Length; i += 4)
            {
                if (a[i + 3] < 250 || b[i + 3] < 250) continue;
                sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
                n++;
            }
            return n > 0 ? sum / (3.0 * n) : 0.0;
        }

        internal static string SafeLabel(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s ?? "")
                sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
            return sb.ToString();
        }
    }

    /// <summary>
    /// P0 CAPTURE SPIKE for docs/dev/design-part-ghost-parity.md. Throwaway: answers whether a
    /// dedicated orthographic Camera.Render into an MSAA RenderTexture works inside KSP's camera
    /// stack, whether the black / white double-clear matte is clean, whether the neutral light
    /// rig (scene lights off, key + fill, pinned pixelLightCount) lights KSP part shaders, and
    /// whether forceRenderingOff hides one side of a co-located real / ghost pair cleanly.
    /// For each candidate part (and one non-base variant when the part offers one) it spawns
    /// a one-part vessel co-orbital with the active vessel, builds the ghost from the real
    /// vessel's own backup snapshot, parents it to the real root with zero offset, and writes
    /// Screenshots/pps-&lt;case&gt;-&lt;view&gt;-&lt;real|ghost&gt;.png for four part-local views.
    /// </summary>
    public class PartParitySpikeInGameTests
    {
        private readonly InGameTestRunner runner;
        public PartParitySpikeInGameTests(InGameTestRunner runner) { this.runner = runner; }

        internal const string Tag = "PartParitySpike";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const int Tile = 512;
        private const float SpawnLiveTimeoutSeconds = 30f;
        private const float SettleSeconds = 1.5f;
        private const double AlongTrackMeters = 60.0;

        private static readonly string[] CandidateParts =
        {
            "fuelTank", "liquidEngine2", "mk1pod.v2", "probeCoreOcto.v2", "restock-nosecone-0625-1",
        };

        private struct ViewDef
        {
            public string Name;
            public Vector3 CamDirLocal;
            public Vector3 UpLocal;
        }

        private static readonly ViewDef[] Views =
        {
            new ViewDef { Name = "side-a", CamDirLocal = Vector3.back, UpLocal = Vector3.up },
            new ViewDef { Name = "side-b", CamDirLocal = Vector3.right, UpLocal = Vector3.up },
            new ViewDef { Name = "top", CamDirLocal = Vector3.up, UpLocal = Vector3.forward },
            new ViewDef { Name = "bottom", CamDirLocal = Vector3.down, UpLocal = Vector3.back },
        };

        private sealed class SpikeCase
        {
            public string PartName;
            public string Variant;
            public string Label;
        }

        [InGameTest(Category = "PartParitySpike", Scene = GameScenes.FLIGHT,
            Description = "P0 capture spike: one-part vessels beside co-located ghosts, dedicated ortho camera + double-clear matte, Screenshots/pps-*.png")]
        public IEnumerator CaptureSpike()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null)
                InGameAssert.Skip("no active vessel");
            if (active.Landed || active.Splashed || active.situation == Vessel.Situations.PRELAUNCH)
                InGameAssert.Skip("the spike spawns co-orbital and needs an orbiting host");

            int texLimitBefore = QualitySettings.masterTextureLimit;
            ulong texMemBefore = Texture.currentTextureMemory;
            int pixelLightsBefore = QualitySettings.pixelLightCount;
            ParsekLog.Info(Tag, $"env: unity={Application.unityVersion} gfx={SystemInfo.graphicsDeviceType} " +
                $"msaaTex={SystemInfo.supportsMultisampledTextures.ToString(IC)} qualityAA={QualitySettings.antiAliasing.ToString(IC)} " +
                $"masterTextureLimit={texLimitBefore.ToString(IC)} texMem={texMemBefore.ToString(IC)} " +
                $"pixelLightCount={pixelLightsBefore.ToString(IC)} colorSpace={QualitySettings.activeColorSpace} " +
                $"ambientMode={RenderSettings.ambientMode} ambient={RenderSettings.ambientLight}");
            if (texLimitBefore != 0)
            {
                QualitySettings.masterTextureLimit = 0;
                for (int i = 0; i < 5; i++) yield return null;
                ParsekLog.Info(Tag, $"masterTextureLimit {texLimitBefore.ToString(IC)} -> {QualitySettings.masterTextureLimit.ToString(IC)} " +
                    $"texMem {texMemBefore.ToString(IC)} -> {Texture.currentTextureMemory.ToString(IC)}");
            }

            var cases = BuildCases();
            var failures = new List<string>();
            int written = 0;
            GameObject camGo = null;
            RenderTexture msaa = null, resolve = null;
            Texture2D readback = null;
            try
            {
                camGo = new GameObject("PartParitySpikeCamera");
                Camera cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.renderingPath = RenderingPath.Forward;
                cam.allowHDR = false;
                cam.allowMSAA = true;
                cam.useOcclusionCulling = false;
                msaa = new RenderTexture(Tile, Tile, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                msaa.Create();
                resolve = new RenderTexture(Tile, Tile, 0, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
                resolve.Create();
                readback = new Texture2D(Tile, Tile, TextureFormat.RGBA32, false);
                ParsekLog.Info(Tag, $"targets: msaa.antiAliasing={msaa.antiAliasing.ToString(IC)} created={msaa.IsCreated()} resolve.created={resolve.IsCreated()}");

                foreach (SpikeCase sc in cases)
                {
                    IEnumerator one = RunCase(sc, active, cam, msaa, resolve, readback, failures, n => written += n);
                    while (one.MoveNext())
                        yield return one.Current;
                }
            }
            finally
            {
                if (camGo != null) UnityEngine.Object.Destroy(camGo);
                if (msaa != null) { msaa.Release(); UnityEngine.Object.Destroy(msaa); }
                if (resolve != null) { resolve.Release(); UnityEngine.Object.Destroy(resolve); }
                if (readback != null) UnityEngine.Object.Destroy(readback);
                if (QualitySettings.masterTextureLimit != texLimitBefore)
                    QualitySettings.masterTextureLimit = texLimitBefore;
            }

            ParsekLog.Info(Tag, $"summary: cases={cases.Count.ToString(IC)} files={written.ToString(IC)} failures={failures.Count.ToString(IC)}");
            if (failures.Count > 0)
                InGameAssert.Fail("PartParitySpike: " + string.Join("; ", failures.ToArray()));
        }

        private static List<SpikeCase> BuildCases()
        {
            var list = new List<SpikeCase>();
            foreach (string name in CandidateParts)
            {
                AvailablePart info = PartLoader.getPartInfoByName(name);
                if (info == null || info.partPrefab == null)
                {
                    ParsekLog.Info(Tag, $"case {name}: not loaded, skipped");
                    continue;
                }
                list.Add(new SpikeCase { PartName = name, Variant = null, Label = PartParitySpikeMath.SafeLabel(name) });
                ModulePartVariants mv = info.partPrefab.FindModuleImplementing<ModulePartVariants>();
                List<string> offered = mv != null ? mv.GetVariantNames() : null;
                string baseName = info.partPrefab.baseVariant != null ? info.partPrefab.baseVariant.Name : null;
                ParsekLog.Info(Tag, $"case {name}: title='{info.title}' variants=[{(offered != null ? string.Join(",", offered.ToArray()) : "")}] base={baseName ?? "<none>"}");
                if (offered != null)
                {
                    foreach (string v in offered)
                    {
                        if (v != baseName)
                        {
                            list.Add(new SpikeCase { PartName = name, Variant = v, Label = PartParitySpikeMath.SafeLabel(name + "-" + v) });
                            break;
                        }
                    }
                }
            }
            return list;
        }

        private static IEnumerator RunCase(SpikeCase sc, Vessel active, Camera cam,
            RenderTexture msaa, RenderTexture resolve, Texture2D readback, List<string> failures, Action<int> onWritten)
        {
            AvailablePart info = PartLoader.getPartInfoByName(sc.PartName);
            uint flightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
            ConfigNode p = ProtoVessel.CreatePartNode(info.name, flightId);
            p.SetValue("parent", "0", true);
            p.SetValue("position", KSPUtil.WriteVector(Vector3.zero), true);
            p.RemoveValues("attN");
            if (info.partPrefab.attachNodes != null)
                foreach (AttachNode an in info.partPrefab.attachNodes)
                    p.AddValue("attN", an.id + ", -1");
            if (sc.Variant != null)
                p.SetValue("moduleVariantName", sc.Variant, true);
            foreach (PartModule m in info.partPrefab.Modules)
            {
                if (m == null) continue;
                ConfigNode mn = p.AddNode("MODULE");
                mn.AddValue("name", m.moduleName);
                mn.AddValue("isEnabled", "True");
                if (m.moduleName == "ModulePartVariants" && sc.Variant != null)
                    mn.AddValue("selectedVariant", sc.Variant);
            }
            Orbit src = active.orbit;
            ConfigNode vnode = ProtoVessel.CreateVesselNode("PPS " + sc.Label, VesselType.Probe,
                new Orbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                    src.argumentOfPeriapsis, src.meanAnomalyAtEpoch, src.epoch, src.referenceBody), 0, new[] { p });

            double ut = Planetarium.GetUniversalTime();
            if (!TrajectoryMath.TryComputeCoOrbitalMeanAnomalyShift(AlongTrackMeters, active.obt_speed,
                    src.semiMajorAxis, src.referenceBody.gravParameter, out double dM))
            {
                failures.Add($"{sc.Label}: no co-orbital shift");
                yield break;
            }
            var orbit = new Orbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                src.argumentOfPeriapsis, src.meanAnomalyAtEpoch + dM, src.epoch, src.referenceBody);
            orbit.Init();
            orbit.UpdateFromUT(ut);
            uint pid = VesselSpawner.SpawnAtPosition(vnode, src.referenceBody, active.latitude, active.longitude,
                active.altitude, active.obt_velocity, ut, terminalState: TerminalState.Orbiting, orbitOverride: orbit);
            if (pid == 0)
            {
                failures.Add($"{sc.Label}: spawn pid=0");
                yield break;
            }

            Vessel real = null;
            GameObject ghostRoot = null;
            try
            {
                float deadline = Time.realtimeSinceStartup + SpawnLiveTimeoutSeconds;
                while (Time.realtimeSinceStartup < deadline)
                {
                    Vessel v = FlightRecorder.FindVesselByPid(pid);
                    if (v != null && v.loaded && !v.packed && v.parts != null && v.parts.Count > 0) { real = v; break; }
                    yield return null;
                }
                if (real == null)
                {
                    failures.Add($"{sc.Label}: never loaded+unpacked");
                    yield break;
                }
                float settleEnd = Time.realtimeSinceStartup + SettleSeconds;
                while (Time.realtimeSinceStartup < settleEnd) yield return null;
                if (real.rootPart == null || real.state == Vessel.State.DEAD)
                {
                    failures.Add($"{sc.Label}: died settling");
                    yield break;
                }

                // Real renderer list BEFORE the ghost is parented under the real root.
                Renderer[] realRs = real.rootPart.GetComponentsInChildren<Renderer>(true);

                ConfigNode snapshot = FlightRecorder.TryBackupSnapshot(real);
                if (snapshot == null) { failures.Add($"{sc.Label}: no snapshot"); yield break; }
                string snapVariant = null;
                ConfigNode sp = snapshot.GetNode("PART");
                if (sp != null) snapVariant = sp.GetValue("moduleVariantName");
                double t0 = Planetarium.GetUniversalTime();
                var rec = new Recording
                {
                    RecordingId = "pps-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                    RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                    VesselName = real.vesselName,
                    VesselPersistentId = real.persistentId,
                    ExplicitStartUT = t0,
                    ExplicitEndUT = t0 + 100.0,
                    GhostVisualSnapshot = snapshot.CreateCopy(),
                    VesselSnapshot = snapshot.CreateCopy(),
                };
                GhostBuildResult build = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(rec, "ParsekTest_pps_" + rec.RecordingId);
                if (build == null || build.root == null) { failures.Add($"{sc.Label}: ghost not built"); yield break; }
                ghostRoot = build.root;
                Transform rootT = real.rootPart.transform;
                ghostRoot.transform.SetParent(rootT, false);
                ghostRoot.transform.localPosition = Vector3.zero;
                ghostRoot.transform.localRotation = Quaternion.identity;
                ghostRoot.transform.localScale = Vector3.one;
                ghostRoot.SetActive(true);
                foreach (Collider c in ghostRoot.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                foreach (Rigidbody rb in ghostRoot.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.detectCollisions = false; }
                Renderer[] ghostRs = ghostRoot.GetComponentsInChildren<Renderer>(true);

                ParsekLog.Info(Tag, $"case {sc.Label}: real pid={pid.ToString(IC)} parts={real.parts.Count.ToString(IC)} " +
                    $"requestedVariant={sc.Variant ?? "<base>"} snapshotVariant={snapVariant ?? "<none>"} " +
                    $"realRenderers={DescribeRenderers(realRs)} ghostRenderers={DescribeRenderers(ghostRs)}");

                // Shared framing box: union of the two sides' visible mesh renderers.
                Bounds box = new Bounds(rootT.position, Vector3.zero);
                bool any = false;
                Accumulate(realRs, ref box, ref any);
                Bounds realBox = box; bool realAny = any;
                Bounds ghostBox = new Bounds(rootT.position, Vector3.zero); bool ghostAny = false;
                Accumulate(ghostRs, ref ghostBox, ref ghostAny);
                if (ghostAny) { if (any) box.Encapsulate(ghostBox); else box = ghostBox; any = true; }
                if (!any) { failures.Add($"{sc.Label}: no visible mesh renderers"); yield break; }
                ParsekLog.Info(Tag, $"case {sc.Label}: box center={rootT.InverseTransformPoint(box.center)} size={box.size} " +
                    $"realSize={(realAny ? realBox.size.ToString() : "none")} ghostSize={(ghostAny ? ghostBox.size.ToString() : "none")}");

                int mask = 0;
                foreach (Renderer r in realRs) if (r != null) mask |= 1 << r.gameObject.layer;
                foreach (Renderer r in ghostRs) if (r != null) mask |= 1 << r.gameObject.layer;
                cam.cullingMask = mask;

                foreach (ViewDef view in Views)
                {
                    yield return new WaitForEndOfFrame();
                    if (real == null || real.rootPart == null) { failures.Add($"{sc.Label}: real lost mid-capture"); yield break; }
                    byte[] realImg, ghostImg;
                    string err = CaptureView(cam, msaa, resolve, readback, rootT, box, view, realRs, ghostRs,
                        sc.Label, out realImg, out ghostImg);
                    if (err != null) { failures.Add($"{sc.Label} {view.Name}: {err}"); continue; }
                    PartParitySpikeMath.CompareCoverage(realImg, ghostImg, out int covR, out int covG, out int xor, out double iou);
                    double dColour = PartParitySpikeMath.MeanColourDelta(realImg, ghostImg);
                    ParsekLog.Info(Tag, $"measure case={sc.Label} view={view.Name} realCov={covR.ToString(IC)} ghostCov={covG.ToString(IC)} " +
                        $"xor={xor.ToString(IC)} iou={iou.ToString("F4", IC)} meanColourDelta={dColour.ToString("F2", IC)}");
                    foreach (var pair in new[] { new KeyValuePair<string, byte[]>("real", realImg), new KeyValuePair<string, byte[]>("ghost", ghostImg) })
                    {
                        string file = "pps-" + sc.Label + "-" + view.Name + "-" + pair.Key + ".png";
                        string err2 = WriteTopDownPng(file, pair.Value);
                        if (err2 != null) failures.Add(file + ": " + err2);
                        else onWritten(1);
                    }
                }
            }
            finally
            {
                if (ghostRoot != null)
                {
                    ghostRoot.transform.SetParent(null, false);
                    UnityEngine.Object.Destroy(ghostRoot);
                }
                Vessel v = real ?? FlightRecorder.FindVesselByPid(pid);
                if (v != null && v != FlightGlobals.ActiveVessel && v.state != Vessel.State.DEAD)
                {
                    try { v.Die(); } catch (Exception ex) { ParsekLog.Info(Tag, $"teardown {sc.Label}: Die threw {ex.Message}"); }
                }
                ParsekLog.Info(Tag, $"teardown case {sc.Label} pid={pid.ToString(IC)}");
            }
            yield return null;
            yield return null;
        }

        /// <summary>One view: both sides, black and white clears, under the neutral light rig.</summary>
        private static string CaptureView(Camera cam, RenderTexture msaa, RenderTexture resolve, Texture2D readback,
            Transform partT, Bounds box, ViewDef view, Renderer[] realRs, Renderer[] ghostRs, string label,
            out byte[] realImg, out byte[] ghostImg)
        {
            realImg = null; ghostImg = null;
            Vector3 dir = partT.TransformDirection(view.CamDirLocal).normalized;
            Vector3 up = partT.TransformDirection(view.UpLocal).normalized;
            Quaternion rot = Quaternion.LookRotation(-dir, up);
            float reach = box.extents.magnitude;
            Vector3 camPos = box.center + dir * (reach + 2f);
            cam.transform.SetPositionAndRotation(camPos, rot);
            // Ortho extent and depth from the 8 corners of the shared box in camera space.
            float halfW = 0f, halfH = 0f, dMin = float.MaxValue, dMax = float.MinValue;
            Vector3 c = box.center, e = box.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = c + new Vector3((i & 1) != 0 ? e.x : -e.x, (i & 2) != 0 ? e.y : -e.y, (i & 4) != 0 ? e.z : -e.z);
                Vector3 local = cam.transform.InverseTransformPoint(corner);
                halfW = Mathf.Max(halfW, Mathf.Abs(local.x));
                halfH = Mathf.Max(halfH, Mathf.Abs(local.y));
                dMin = Mathf.Min(dMin, local.z);
                dMax = Mathf.Max(dMax, local.z);
            }
            cam.orthographicSize = Mathf.Max(halfW, halfH) * 1.1f;
            cam.nearClipPlane = Mathf.Max(0.01f, dMin - 0.5f);
            cam.farClipPlane = dMax + 0.5f;
            cam.targetTexture = msaa;

            // Light rig: every scene light off (saved), key + fill on, ambient and pixel lights pinned.
            Light[] sceneLights = UnityEngine.Object.FindObjectsOfType<Light>();
            var wasEnabled = new bool[sceneLights.Length];
            for (int i = 0; i < sceneLights.Length; i++) { wasEnabled[i] = sceneLights[i].enabled; sceneLights[i].enabled = false; }
            Color ambientBefore = RenderSettings.ambientLight;
            UnityEngine.Rendering.AmbientMode ambientModeBefore = RenderSettings.ambientMode;
            int pixelLightsBefore = QualitySettings.pixelLightCount;
            GameObject keyGo = new GameObject("PartParitySpikeKey");
            GameObject fillGo = new GameObject("PartParitySpikeFill");
            bool[] realForceBefore = new bool[realRs.Length];
            bool[] ghostForceBefore = new bool[ghostRs.Length];
            for (int i = 0; i < realRs.Length; i++) if (realRs[i] != null) realForceBefore[i] = realRs[i].forceRenderingOff;
            for (int i = 0; i < ghostRs.Length; i++) if (ghostRs[i] != null) ghostForceBefore[i] = ghostRs[i].forceRenderingOff;
            RenderTexture activeBefore = RenderTexture.active;
            try
            {
                Light key = keyGo.AddComponent<Light>();
                key.type = LightType.Directional; key.intensity = 1.0f; key.color = Color.white;
                key.shadows = LightShadows.None; key.cullingMask = -1; key.renderMode = LightRenderMode.ForcePixel;
                keyGo.transform.rotation = Quaternion.LookRotation(rot * new Vector3(0.5f, -0.6f, 1f).normalized, up);
                Light fill = fillGo.AddComponent<Light>();
                fill.type = LightType.Directional; fill.intensity = 0.45f; fill.color = Color.white;
                fill.shadows = LightShadows.None; fill.cullingMask = -1; fill.renderMode = LightRenderMode.ForcePixel;
                fillGo.transform.rotation = Quaternion.LookRotation(rot * new Vector3(-0.6f, 0.3f, 1f).normalized, up);
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.25f, 0.25f, 0.25f, 1f);
                QualitySettings.pixelLightCount = 4;

                for (int side = 0; side < 2; side++)
                {
                    Renderer[] other = side == 0 ? ghostRs : realRs;
                    Renderer[] mine = side == 0 ? realRs : ghostRs;
                    foreach (Renderer r in other) if (r != null) r.forceRenderingOff = true;
                    foreach (Renderer r in mine) if (r != null) r.forceRenderingOff = false;
                    byte[] overBlack = RenderOnce(cam, msaa, resolve, readback, Color.black);
                    byte[] overWhite = RenderOnce(cam, msaa, resolve, readback, Color.white);
                    byte[] img = PartParitySpikeMath.ComposeMatte(overBlack, overWhite, out PartParitySpikeMath.MatteStats ms);
                    ParsekLog.Info(Tag, $"matte case={label} view={view.Name} side={(side == 0 ? "real" : "ghost")} " +
                        $"opaque={ms.Opaque.ToString(IC)} edge={ms.Edge.ToString(IC)} inverted={ms.Inverted.ToString(IC)} " +
                        $"ortho={cam.orthographicSize.ToString("F3", IC)} near={cam.nearClipPlane.ToString("F2", IC)} far={cam.farClipPlane.ToString("F2", IC)} " +
                        $"mask=0x{cam.cullingMask.ToString("X", IC)} sceneLightsOff={sceneLights.Length.ToString(IC)}");
                    if (side == 0) realImg = img; else ghostImg = img;
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                for (int i = 0; i < realRs.Length; i++) if (realRs[i] != null) realRs[i].forceRenderingOff = realForceBefore[i];
                for (int i = 0; i < ghostRs.Length; i++) if (ghostRs[i] != null) ghostRs[i].forceRenderingOff = ghostForceBefore[i];
                UnityEngine.Object.DestroyImmediate(keyGo);
                UnityEngine.Object.DestroyImmediate(fillGo);
                for (int i = 0; i < sceneLights.Length; i++) if (sceneLights[i] != null) sceneLights[i].enabled = wasEnabled[i];
                RenderSettings.ambientMode = ambientModeBefore;
                RenderSettings.ambientLight = ambientBefore;
                QualitySettings.pixelLightCount = pixelLightsBefore;
                cam.targetTexture = null;
                RenderTexture.active = activeBefore;
            }
        }

        private static byte[] RenderOnce(Camera cam, RenderTexture msaa, RenderTexture resolve, Texture2D readback, Color clear)
        {
            Color bg = clear; bg.a = 0f;
            cam.backgroundColor = bg;
            cam.Render();
            Graphics.Blit(msaa, resolve);
            RenderTexture.active = resolve;
            readback.ReadPixels(new Rect(0, 0, Tile, Tile), 0, 0, false);
            Color32[] px = readback.GetPixels32();
            var bytes = new byte[px.Length * 4];
            // GetPixels32 rows are bottom-up; flip to top-down here.
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
            return bytes;
        }

        private static string WriteTopDownPng(string fileName, byte[] rgba)
        {
            try
            {
                string dir = Path.Combine(KSPUtil.ApplicationRootPath ?? string.Empty, "Screenshots");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                byte[] png = PartParitySpikeMath.EncodePngRgba(Tile, Tile, rgba);
                File.WriteAllBytes(Path.Combine(dir, fileName), png);
                ParsekLog.Info(Tag, $"wrote Screenshots/{fileName} bytes={png.Length.ToString(IC)}");
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static void Accumulate(Renderer[] rs, ref Bounds b, ref bool any)
        {
            foreach (Renderer r in rs)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (r.bounds.size.magnitude > 30f) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
        }

        private static string DescribeRenderers(Renderer[] rs)
        {
            int mesh = 0, skinned = 0, particle = 0, other = 0, visible = 0;
            var layers = new SortedSet<int>();
            foreach (Renderer r in rs)
            {
                if (r == null) continue;
                if (r is MeshRenderer) mesh++;
                else if (r is SkinnedMeshRenderer) skinned++;
                else if (r is ParticleSystemRenderer) particle++;
                else other++;
                if (r.enabled && r.gameObject.activeInHierarchy) visible++;
                layers.Add(r.gameObject.layer);
            }
            var ls = new List<string>();
            foreach (int l in layers) ls.Add(l.ToString(IC));
            return $"mesh={mesh.ToString(IC)},skinned={skinned.ToString(IC)},particle={particle.ToString(IC)},other={other.ToString(IC)}," +
                $"visible={visible.ToString(IC)},layers=[{string.Join(",", ls.ToArray())}]";
        }
    }
}
