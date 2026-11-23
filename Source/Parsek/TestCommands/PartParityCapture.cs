using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>The pure camera math of the capture (design 4.3): ortho size, clip planes and
    /// the projected box area the blob percentages divide by.</summary>
    internal static class PartParityCaptureMath
    {
        /// <summary>Framing margin on the ortho size (design 4.3: shared box plus 10 %).</summary>
        internal const double OrthoMargin = 1.10;

        /// <summary>The hero view's vertical field of view, degrees.</summary>
        internal const double HeroFovDeg = 30.0;

        /// <summary>The camera stands this far outside the box (metres) on the view axis.</summary>
        internal const double CameraStandoffM = 2.0;

        /// <summary>
        /// One ortho size for every orthographic view of a case: the largest half-extent of
        /// the shared box (an axis view's half-width and half-height are box half-extents)
        /// plus the margin, so every view of a case has the same pixels per metre.
        /// </summary>
        internal static double OrthoSize(double[] sharedExtents)
        {
            if (sharedExtents == null || sharedExtents.Length != 3) return 1.0;
            double e = Math.Max(sharedExtents[0], Math.Max(sharedExtents[1], sharedExtents[2]));
            return e > 0.0 ? e * OrthoMargin : 0.5;
        }

        /// <summary>Clip planes from the shared box's depth range in camera space, with a margin
        /// of 5 % of the box radius plus 5 cm: nothing outside the box can enter, no case mesh
        /// can be cut.</summary>
        internal static void ClipPlanes(double depthMin, double depthMax, double boxRadius, out double near, out double far)
        {
            double margin = 0.05 * boxRadius + 0.05;
            near = Math.Max(0.01, depthMin - margin);
            far = Math.Max(near + 0.01, depthMax + margin);
        }

        /// <summary>The area in pixels of a camera-space rectangle of half-size
        /// (<paramref name="halfW"/>, <paramref name="halfH"/>) metres under an ortho camera.</summary>
        internal static double ProjectedAreaPx(double halfW, double halfH, double orthoSize, int tile)
        {
            if (orthoSize <= 0.0 || tile <= 0) return 0.0;
            double pxPerM = tile / (2.0 * orthoSize);
            return 4.0 * halfW * halfH * pxPerM * pxPerM;
        }

        /// <summary>The hero camera distance that fits a sphere of <paramref name="radius"/> in
        /// the field of view with the ortho margin.</summary>
        internal static double HeroDistance(double radius)
        {
            double half = HeroFovDeg * 0.5 * Math.PI / 180.0;
            return Math.Max(0.1, radius) * OrthoMargin / Math.Sin(half);
        }
    }

    /// <summary>One view's pictures and the numbers the sidecar records about its camera.</summary>
    internal sealed class PartParityViewShot
    {
        internal string View;
        internal byte[] Real;
        internal byte[] Ghost;
        internal PartParityMatteStats RealMatte;
        internal PartParityMatteStats GhostMatte;
        internal double OrthoSize;
        internal double ProjectedBoxAreaPx;
        internal double Near;
        internal double Far;
        internal int SceneLightsOff;
        internal string Error;

        /// <summary>Every side's tile, in side order (the first two are Real and Ghost).</summary>
        internal byte[][] Sides;
        internal PartParityMatteStats[] SideMattes;
    }

    /// <summary>
    /// The capture primitive (design 4.4), lifted from the P0 spike that flew green on both
    /// harness installs: a disabled camera of our own (<c>RenderingPath.Forward</c>) renders
    /// on demand into an MSAA 4 target, <c>Graphics.Blit</c> resolves it into a non-MSAA
    /// target, <c>ReadPixels</c> reads it back. One view's hide / render / read / restore
    /// runs synchronously in <see cref="CaptureView"/>: both sides (each with every other
    /// renderer <c>forceRenderingOff</c>) x both clears (black, white), under the neutral
    /// light rig (every scene light off and restored, camera-relative key + fixed fill, no
    /// shadows, flat ambient, pinned <c>pixelLightCount</c>). One instance per step owns the
    /// camera and the target pair; <see cref="Dispose"/> releases them.
    /// </summary>
    internal sealed class PartParityCapture : IDisposable
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        internal readonly int Tile;
        private GameObject camGo;
        private Camera cam;
        private RenderTexture msaa;
        private RenderTexture resolve;
        private Texture2D readback;

        private PartParityCapture(int tile) { Tile = tile; }

        internal static PartParityCapture Create(int tile)
        {
            var c = new PartParityCapture(tile);
            c.camGo = new GameObject("PartParityCaptureCamera");
            c.cam = c.camGo.AddComponent<Camera>();
            c.cam.enabled = false;
            c.cam.clearFlags = CameraClearFlags.SolidColor;
            c.cam.renderingPath = RenderingPath.Forward;
            c.cam.allowHDR = false;
            c.cam.allowMSAA = true;
            c.cam.useOcclusionCulling = false;
            c.msaa = new RenderTexture(tile, tile, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            c.msaa.Create();
            c.resolve = new RenderTexture(tile, tile, 0, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            c.resolve.Create();
            c.readback = new Texture2D(tile, tile, TextureFormat.RGBA32, false);
            ParsekLog.Info(PartParitySweepRunner.Tag, $"capture: tile={tile.ToString(Ic)} msaa.antiAliasing={c.msaa.antiAliasing.ToString(Ic)} " +
                $"msaaCreated={c.msaa.IsCreated()} resolveCreated={c.resolve.IsCreated()} gfx={SystemInfo.graphicsDeviceType} " +
                $"msaaTex={SystemInfo.supportsMultisampledTextures.ToString(Ic)}");
            return c;
        }

        public void Dispose()
        {
            if (camGo != null) UnityEngine.Object.Destroy(camGo);
            if (msaa != null) { msaa.Release(); UnityEngine.Object.Destroy(msaa); }
            if (resolve != null) { resolve.Release(); UnityEngine.Object.Destroy(resolve); }
            if (readback != null) UnityEngine.Object.Destroy(readback);
            camGo = null; cam = null; msaa = null; resolve = null; readback = null;
        }

        /// <summary>The camera position and up of a view, both in PART-LOCAL axes.</summary>
        internal static bool TryViewAxes(string view, out Vector3 camDirLocal, out Vector3 upLocal, out bool perspective)
        {
            perspective = false;
            upLocal = Vector3.up;
            switch (view)
            {
                case TestCommandPartParitySweep.ViewSideA: camDirLocal = Vector3.back; return true;
                case TestCommandPartParitySweep.ViewSideB: camDirLocal = Vector3.right; return true;
                case TestCommandPartParitySweep.ViewTop: camDirLocal = Vector3.up; upLocal = Vector3.forward; return true;
                case TestCommandPartParitySweep.ViewBottom: camDirLocal = Vector3.down; upLocal = Vector3.back; return true;
                case TestCommandPartParitySweep.ViewSideC: camDirLocal = Vector3.forward; return true;
                case TestCommandPartParitySweep.ViewSideD: camDirLocal = Vector3.left; return true;
                case TestCommandPartParitySweep.ViewHero:
                    camDirLocal = new Vector3(1f, 0.6f, -1f).normalized;
                    perspective = true;
                    return true;
                default:
                    camDirLocal = Vector3.zero;
                    return false;
            }
        }

        /// <summary>
        /// Captures one view of one case. <paramref name="all"/> is every renderer of both
        /// sides (each forced off while the other side renders); <paramref name="showReal"/>
        /// and <paramref name="showGhost"/> are the renderers each side shows, at their own
        /// <c>forceRenderingOff</c> state. Every flag, light and setting is restored before
        /// it returns, on every path. Never throws: a failure is <c>shot.Error</c>.
        /// </summary>
        internal PartParityViewShot CaptureView(
            Transform partT, PartParityAabb sharedLocal, double orthoSize, string view,
            IList<Renderer> all, IList<Renderer> showReal, IList<Renderer> showGhost, int cullingMask)
            => CaptureSides(partT, sharedLocal, orthoSize, view, all, new[] { showReal, showGhost }, cullingMask);

        /// <summary>
        /// As <see cref="CaptureView"/> for N sides (design D.5: real, the event-route ghost, the
        /// start-route ghost, the stowed reference), rendered in order, each with every other
        /// renderer of <paramref name="all"/> forced off, two clears each. The first two sides
        /// land in <c>Real</c> / <c>Ghost</c> (a two-side call is exactly the stowed capture);
        /// every side, the first two included, lands in <c>Sides</c> / <c>SideMattes</c>.
        /// </summary>
        internal PartParityViewShot CaptureSides(
            Transform partT, PartParityAabb sharedLocal, double orthoSize, string view,
            IList<Renderer> all, IList<IList<Renderer>> sides, int cullingMask)
        {
            var shot = new PartParityViewShot { View = view, OrthoSize = orthoSize };
            if (!TryViewAxes(view, out Vector3 dirLocal, out Vector3 upLocal, out bool perspective))
            {
                shot.Error = "unknown view";
                return shot;
            }
            var saved = new Dictionary<Renderer, bool>();
            foreach (Renderer r in all) if (r != null && !saved.ContainsKey(r)) saved[r] = r.forceRenderingOff;
            Light[] sceneLights = null;
            bool[] lightWasEnabled = null;
            Color ambientBefore = RenderSettings.ambientLight;
            UnityEngine.Rendering.AmbientMode ambientModeBefore = RenderSettings.ambientMode;
            int pixelLightsBefore = QualitySettings.pixelLightCount;
            RenderTexture activeBefore = RenderTexture.active;
            GameObject keyGo = null, fillGo = null;
            try
            {
                Quaternion rot = PoseCamera(partT, sharedLocal, orthoSize, dirLocal, upLocal, perspective, shot);
                cam.cullingMask = cullingMask;
                cam.targetTexture = msaa;
                sceneLights = UnityEngine.Object.FindObjectsOfType<Light>();
                lightWasEnabled = new bool[sceneLights.Length];
                for (int i = 0; i < sceneLights.Length; i++)
                {
                    lightWasEnabled[i] = sceneLights[i].enabled;
                    sceneLights[i].enabled = false;
                }
                shot.SceneLightsOff = sceneLights.Length;
                keyGo = MakeLight("PartParityKey", 1.0f, rot * new Vector3(0.5f, -0.6f, 1f).normalized, partT.TransformDirection(upLocal));
                fillGo = MakeLight("PartParityFill", 0.45f, rot * new Vector3(-0.6f, 0.3f, 1f).normalized, partT.TransformDirection(upLocal));
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.25f, 0.25f, 0.25f, 1f);
                QualitySettings.pixelLightCount = 4;

                int n = sides != null ? sides.Count : 0;
                shot.Sides = new byte[n][];
                shot.SideMattes = new PartParityMatteStats[n];
                for (int s = 0; s < n; s++)
                    shot.Sides[s] = RenderSide(saved, sides[s], out shot.SideMattes[s]);
                if (n > 0) { shot.Real = shot.Sides[0]; shot.RealMatte = shot.SideMattes[0]; }
                if (n > 1) { shot.Ghost = shot.Sides[1]; shot.GhostMatte = shot.SideMattes[1]; }
            }
            catch (Exception ex)
            {
                shot.Error = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                foreach (var kv in saved) if (kv.Key != null) kv.Key.forceRenderingOff = kv.Value;
                if (keyGo != null) UnityEngine.Object.DestroyImmediate(keyGo);
                if (fillGo != null) UnityEngine.Object.DestroyImmediate(fillGo);
                if (sceneLights != null)
                    for (int i = 0; i < sceneLights.Length; i++)
                        if (sceneLights[i] != null) sceneLights[i].enabled = lightWasEnabled[i];
                RenderSettings.ambientMode = ambientModeBefore;
                RenderSettings.ambientLight = ambientBefore;
                QualitySettings.pixelLightCount = pixelLightsBefore;
                if (cam != null) cam.targetTexture = null;
                RenderTexture.active = activeBefore;
            }
            return shot;
        }

        private Quaternion PoseCamera(Transform partT, PartParityAabb box, double orthoSize,
            Vector3 dirLocal, Vector3 upLocal, bool perspective, PartParityViewShot shot)
        {
            double[] c = box.Center(), e = box.Extents();
            Vector3 centerW = partT.TransformPoint(new Vector3((float)c[0], (float)c[1], (float)c[2]));
            Vector3 dir = partT.TransformDirection(dirLocal).normalized;
            Vector3 up = partT.TransformDirection(upLocal).normalized;
            Quaternion rot = Quaternion.LookRotation(-dir, up);
            double radius = Math.Sqrt(e[0] * e[0] + e[1] * e[1] + e[2] * e[2]);
            double dist = perspective ? PartParityCaptureMath.HeroDistance(radius) : radius + PartParityCaptureMath.CameraStandoffM;
            cam.transform.SetPositionAndRotation(centerW + dir * (float)dist, rot);
            cam.orthographic = !perspective;
            if (perspective) cam.fieldOfView = (float)PartParityCaptureMath.HeroFovDeg;
            else cam.orthographicSize = (float)orthoSize;

            double halfW = 0, halfH = 0, dMin = double.MaxValue, dMax = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var cornerLocal = new Vector3(
                    (float)(c[0] + ((i & 1) != 0 ? e[0] : -e[0])),
                    (float)(c[1] + ((i & 2) != 0 ? e[1] : -e[1])),
                    (float)(c[2] + ((i & 4) != 0 ? e[2] : -e[2])));
                Vector3 inCam = cam.transform.InverseTransformPoint(partT.TransformPoint(cornerLocal));
                halfW = Math.Max(halfW, Math.Abs(inCam.x));
                halfH = Math.Max(halfH, Math.Abs(inCam.y));
                dMin = Math.Min(dMin, inCam.z);
                dMax = Math.Max(dMax, inCam.z);
            }
            PartParityCaptureMath.ClipPlanes(dMin, dMax, radius, out double near, out double far);
            cam.nearClipPlane = (float)near;
            cam.farClipPlane = (float)far;
            shot.Near = near;
            shot.Far = far;
            shot.ProjectedBoxAreaPx = perspective ? 0.0 : PartParityCaptureMath.ProjectedAreaPx(halfW, halfH, orthoSize, Tile);
            return rot;
        }

        private static GameObject MakeLight(string name, float intensity, Vector3 forward, Vector3 up)
        {
            var go = new GameObject(name);
            Light l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = intensity;
            l.color = Color.white;
            l.shadows = LightShadows.None;
            l.cullingMask = -1;
            l.renderMode = LightRenderMode.ForcePixel;
            go.transform.rotation = Quaternion.LookRotation(forward, up);
            return go;
        }

        private byte[] RenderSide(Dictionary<Renderer, bool> saved, IList<Renderer> show, out PartParityMatteStats matte)
        {
            foreach (var kv in saved) if (kv.Key != null) kv.Key.forceRenderingOff = true;
            if (show != null)
                foreach (Renderer r in show)
                    if (r != null) r.forceRenderingOff = saved.TryGetValue(r, out bool was) ? was : false;
            byte[] overBlack = RenderOnce(Color.black);
            byte[] overWhite = RenderOnce(Color.white);
            return PartParityMetrics.ComposeMatte(overBlack, overWhite, out matte);
        }

        // =====================================================================================
        // the FX frames (design-part-ghost-parity.md E.3): layered renders outside CaptureView
        // =====================================================================================

        /// <summary>
        /// One FX frame: every renderer of <paramref name="all"/> off, every scene light off
        /// except the kept ones, the camera posed per render, each render showing only the
        /// listed renderers. Everything is restored by <see cref="Dispose"/>, so the frame lives
        /// inside one synchronous capture call. Not re-entrant: one frame at a time.
        /// </summary>
        internal FxFrame BeginFxFrame(IList<Renderer> all, int cullingMask, bool rigLights)
            => new FxFrame(this, all, cullingMask, rigLights);

        internal sealed class FxFrame : IDisposable
        {
            private readonly PartParityCapture owner;
            private readonly Dictionary<Renderer, bool> saved = new Dictionary<Renderer, bool>();
            private readonly Light[] sceneLights;
            private readonly bool[] lightWasEnabled;
            private readonly Color ambientBefore;
            private readonly UnityEngine.Rendering.AmbientMode ambientModeBefore;
            private readonly int pixelLightsBefore;
            private readonly RenderTexture activeBefore;
            private readonly bool rigLights;
            private GameObject keyGo, fillGo;
            private HashSet<Light> lit = new HashSet<Light>();

            internal FxFrame(PartParityCapture owner, IList<Renderer> all, int cullingMask, bool rigLights)
            {
                this.owner = owner;
                this.rigLights = rigLights;
                ambientBefore = RenderSettings.ambientLight;
                ambientModeBefore = RenderSettings.ambientMode;
                pixelLightsBefore = QualitySettings.pixelLightCount;
                activeBefore = RenderTexture.active;
                foreach (Renderer r in all)
                    if (r != null && !saved.ContainsKey(r)) saved[r] = r.forceRenderingOff;
                sceneLights = UnityEngine.Object.FindObjectsOfType<Light>();
                lightWasEnabled = new bool[sceneLights.Length];
                for (int i = 0; i < sceneLights.Length; i++)
                {
                    lightWasEnabled[i] = sceneLights[i].enabled;
                    sceneLights[i].enabled = false;
                }
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = rigLights ? new Color(0.25f, 0.25f, 0.25f, 1f) : Color.black;
                QualitySettings.pixelLightCount = 8;
                owner.cam.cullingMask = cullingMask;
                owner.cam.targetTexture = owner.msaa;
            }

            /// <summary>Keeps these lights on (their own enabled state) until the frame ends.
            /// A light that was already off when the frame began stays off.</summary>
            internal void KeepLights(IEnumerable<Light> on)
            {
                lit = new HashSet<Light>(on);
                for (int i = 0; i < sceneLights.Length; i++)
                    if (sceneLights[i] != null)
                        sceneLights[i].enabled = lit.Contains(sceneLights[i]) && lightWasEnabled[i];
            }

            /// <summary>The ortho camera's pose for the next renders; the rig key and fill lights
            /// (when the frame has them) stand on it.</summary>
            internal void Pose(Vector3 position, Quaternion rotation, double orthoSize, double near, double far)
            {
                Camera cam = owner.cam;
                cam.transform.SetPositionAndRotation(position, rotation);
                cam.orthographic = true;
                cam.orthographicSize = (float)orthoSize;
                cam.nearClipPlane = (float)Math.Max(0.01, near);
                cam.farClipPlane = (float)Math.Max(cam.nearClipPlane + 0.01, far);
                if (!rigLights) return;
                Vector3 up = rotation * Vector3.up;
                if (keyGo != null) UnityEngine.Object.DestroyImmediate(keyGo);
                if (fillGo != null) UnityEngine.Object.DestroyImmediate(fillGo);
                keyGo = MakeLight("PartParityFxKey", 1.0f, rotation * new Vector3(0.5f, -0.6f, 1f).normalized, up);
                fillGo = MakeLight("PartParityFxFill", 0.45f, rotation * new Vector3(-0.6f, 0.3f, 1f).normalized, up);
            }

            internal Vector3 ToCamera(Vector3 world) => owner.cam.transform.InverseTransformPoint(world);

            /// <summary>One render of exactly <paramref name="show"/> (at each renderer's own
            /// <c>forceRenderingOff</c> state when the frame began) over <paramref name="clear"/>,
            /// as RGBA8 top-down.</summary>
            internal byte[] Render(IList<Renderer> show, Color clear)
            {
                foreach (var kv in saved) if (kv.Key != null) kv.Key.forceRenderingOff = true;
                if (show != null)
                    foreach (Renderer r in show)
                    {
                        if (r == null) continue;
                        if (saved.TryGetValue(r, out bool was)) r.forceRenderingOff = was;
                        else r.forceRenderingOff = false;
                    }
                return owner.RenderOnce(clear);
            }

            public void Dispose()
            {
                foreach (var kv in saved) if (kv.Key != null) kv.Key.forceRenderingOff = kv.Value;
                if (keyGo != null) UnityEngine.Object.DestroyImmediate(keyGo);
                if (fillGo != null) UnityEngine.Object.DestroyImmediate(fillGo);
                keyGo = null;
                fillGo = null;
                for (int i = 0; i < sceneLights.Length; i++)
                    if (sceneLights[i] != null) sceneLights[i].enabled = lightWasEnabled[i];
                RenderSettings.ambientMode = ambientModeBefore;
                RenderSettings.ambientLight = ambientBefore;
                QualitySettings.pixelLightCount = pixelLightsBefore;
                if (owner.cam != null) owner.cam.targetTexture = null;
                RenderTexture.active = activeBefore;
            }
        }

        private byte[] RenderOnce(Color clear)
        {
            Color bg = clear;
            bg.a = 0f;
            cam.backgroundColor = bg;
            cam.Render();
            Graphics.Blit(msaa, resolve);
            RenderTexture.active = resolve;
            readback.ReadPixels(new Rect(0, 0, Tile, Tile), 0, 0, false);
            Color32[] px = readback.GetPixels32();
            var bytes = new byte[px.Length * 4];
            // GetPixels32 rows are bottom-up; written top-down here.
            for (int y = 0; y < Tile; y++)
            {
                int srcRow = (Tile - 1 - y) * Tile;
                for (int x = 0; x < Tile; x++)
                {
                    Color32 p = px[srcRow + x];
                    int o = (y * Tile + x) * 4;
                    bytes[o] = p.r;
                    bytes[o + 1] = p.g;
                    bytes[o + 2] = p.b;
                    bytes[o + 3] = p.a;
                }
            }
            return bytes;
        }
    }
}
