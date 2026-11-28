using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The constants stock's <c>AerodynamicsFX</c> reads from <c>PhysicsGlobals</c> (Physics.cfg).
    /// <see cref="StockDefaults"/> are the shipped Physics.cfg values; the live renderer replaces
    /// them with the loaded <c>PhysicsGlobals</c> once per scene.
    /// </summary>
    internal struct AeroFxConstants
    {
        public float ThermalStartMach;
        public float ThermalFullMach;
        public double VelocityExponent;
        public double DensityScalar1;
        public double DensityExponent1;
        public double DensityScalar2;
        public double DensityExponent2;
        public double DensityFadeStart;
        public float MachFxFadeStart;
        public float MachFxFadeEnd;

        internal static AeroFxConstants StockDefaults => new AeroFxConstants
        {
            ThermalStartMach = 2.5f,
            ThermalFullMach = 3.75f,
            VelocityExponent = 3.5,
            DensityScalar1 = 0.0091,
            DensityExponent1 = 0.5,
            DensityScalar2 = 0.09,
            DensityExponent2 = 2.0,
            DensityFadeStart = 0.0015,
            MachFxFadeStart = 0.25f,
            MachFxFadeEnd = 0.0875f,
        };
    }

    /// <summary>A stock <c>MinMaxFloat</c>: <c>GetLerp(t) = Mathf.Lerp(min, max, t)</c>.</summary>
    internal struct AeroFxRange
    {
        public float Min;
        public float Max;

        internal AeroFxRange(float min, float max) { Min = min; Max = max; }

        internal float Lerp(float t) => Mathf.Lerp(Min, Max, t);
    }

    /// <summary>
    /// One stock <c>AeroFXState</c> (the prefab's <c>Condensation</c> or <c>ReentryHeat</c>): the
    /// look values the effect lerps by <c>FxScalar</c>.
    /// </summary>
    internal struct AeroFxLook
    {
        public Color ColorMin;
        public Color ColorMax;
        public AeroFxRange Length;
        public AeroFxRange EdgeFade;
        public AeroFxRange Falloff1;
        public AeroFxRange Falloff2;
        public AeroFxRange Falloff3;
        public AeroFxRange Intensity;
        public AeroFxRange Wobble;
        public AeroFxRange LightPower;
    }

    /// <summary>The blended values one frame of the effect draws with.</summary>
    internal struct AeroFxDrawValues
    {
        public Color Color;
        public float Length;
        public float EdgeFade;
        public float Falloff1;
        public float Falloff2;
        public float Falloff3;
        public float Intensity;
        public float Wobble;
        public float LightPower;
    }

    /// <summary>Why a ghost's own sheath is or is not drawn this frame.</summary>
    internal enum GhostAeroSheathDecision
    {
        Draw = 0,
        NoEffect = 1,
        MapView = 2,
        ThermalColorsDebug = 3,
        DetailOff = 4,
        OutOfReach = 6,
        NoStockAssets = 7,
        NoMeshes = 8,
        Isolated = 9,
    }

    /// <summary>Why a ghost may or may not own the reentry light this frame.</summary>
    internal enum GhostAeroLightDecision
    {
        Offer = 0,
        NoEffect = 1,
        MapView = 2,
        OutOfReach = 4,
        NoStockLight = 5,
        NoMeshes = 6,
    }

    /// <summary>
    /// Diagnostic modes of the ghost reentry renderer, set only by the in-game FX rig through
    /// <c>GhostAeroFxRenderer.SetDiagnosticModeForTesting</c>; play always runs <see cref="Normal"/>.
    /// </summary>
    internal enum GhostAeroFxDiagnosticMode
    {
        Normal = 0,
        /// <summary>
        /// The sheath meshes are drawn on Parsek's FX camera with a plain bright unlit material and
        /// no replacement shader: shows whether that camera's output reaches the screen at all.
        /// </summary>
        SanityUnlit = 1,
        /// <summary>Parsek's scene depth pass is off: shows whether that pass hides the sheath.</summary>
        NoSceneDepth = 2,
    }

    /// <summary>
    /// Pure halves of the ghost reentry effect (part parity P9 Y1,
    /// docs/dev/design-part-ghost-parity.md G.7 "Y1 design"). Every rule here is stock's
    /// <c>AerodynamicsFX</c> / <c>FXCamera</c> arithmetic (KSP 1.12.5 decompile), applied to the
    /// ghost's own conditions instead of the active vessel's.
    /// </summary>
    internal static class GhostAeroFx
    {
        /// <summary><c>AerodynamicsFX.p0</c>: the heat flux at which <c>FxScalar</c> crosses 0.</summary>
        internal const double HeatFluxThreshold = 8000000.0;

        /// <summary><c>FXCamera.startFarClipPlane</c>'s field default (the prefab reads 300 too).</summary>
        internal const float StockFxReachMin = 300f;

        /// <summary><c>FXCamera.farClipPlaneBuffer</c>'s field default.</summary>
        internal const float StockFxReachBuffer = 100f;

        /// <summary><c>FXCamera.projectionRange</c>: the velocity camera's minimum far clip.</summary>
        internal const float StockVelocityCamRange = 50f;

        /// <summary>The velocity camera's starting orthographic size in <c>FXCamera.Start</c>.</summary>
        internal const float StockVelocityCamMinBox = 3f;

        /// <summary>
        /// <c>FXCamera.Start</c>'s occlusion map size when <c>GameSettings.AERO_FX_QUALITY</c> is
        /// outside 0..3 (its switch's default branch), and the size range the switch covers.
        /// </summary>
        internal const int StockOcclusionResolutionDefault = 512;
        internal const int StockOcclusionResolutionMin = 256;
        internal const int StockOcclusionResolutionMax = 2048;

        /// <summary>How long a pooled ghost depth map may sit unused before it is released.</summary>
        internal const float DepthMapIdleReleaseSeconds = 5f;

        /// <summary>
        /// The size of a ghost's own velocity depth map: stock's <c>FXCamera.occlusionMapResolution</c>
        /// (256 / 512 / 1024 / 2048 by <c>AERO_FX_QUALITY</c>), so a ghost's flame edges are as
        /// clean as the active vessel's. An unread or out-of-range value falls back to stock's
        /// default branch.
        /// </summary>
        internal static int OcclusionResolution(int stockResolution)
        {
            if (stockResolution < StockOcclusionResolutionMin || stockResolution > StockOcclusionResolutionMax)
                return StockOcclusionResolutionDefault;
            return stockResolution;
        }

        /// <summary>
        /// Whether the pooled depth map in the last slot can be released: it is past the slots
        /// this frame drew and has sat unused for <see cref="DepthMapIdleReleaseSeconds"/>.
        /// </summary>
        internal static bool ShouldReleasePooledDepthMap(int slot, int drawnThisFrame,
            float lastUsedTime, float now)
        {
            if (slot < drawnThisFrame) return false;
            if (float.IsNaN(lastUsedTime) || float.IsNaN(now)) return true;
            return now - lastUsedTime >= DepthMapIdleReleaseSeconds;
        }

        /// <summary>
        /// Whether a ghost is hidden from stock's FX camera and velocity camera this frame:
        /// only while stock's effect is on and the ghost is within its reach (stock paints nothing
        /// beyond its far clip, so masking a farther ghost would buy nothing).
        /// </summary>
        internal static bool ShouldMaskFromStockFx(bool stockFxCameraOn, bool withinReach)
            => stockFxCameraOn && withinReach;

        /// <summary>How far after stock's FX cameras the Parsek copies render (camera depth).</summary>
        internal const float CopyCameraDepthStep = 0.01f;

        /// <summary>
        /// The depth of the Parsek depth copy: just after both of stock's FX cameras (they sit at
        /// 2 or 3, <c>FXCamera.OnCameraChange</c>), so a frame renders stock's pass, then ours; the
        /// FX copy renders one step later still.
        /// </summary>
        internal static float CopyCameraDepth(float stockDepthCameraDepth, float stockFxCameraDepth)
        {
            if (float.IsNaN(stockDepthCameraDepth)) stockDepthCameraDepth = stockFxCameraDepth;
            if (float.IsNaN(stockFxCameraDepth)) stockFxCameraDepth = stockDepthCameraDepth;
            if (float.IsNaN(stockDepthCameraDepth)) return 3f + CopyCameraDepthStep;
            return Mathf.Max(stockDepthCameraDepth, stockFxCameraDepth) + CopyCameraDepthStep;
        }

        /// <summary>
        /// Whether a renderer on this layer is one stock's FX or velocity camera could draw.
        /// </summary>
        internal static bool LayerSeenByStockFx(int layer, int stockFxMask, int stockVelocityMask)
        {
            if (layer < 0 || layer > 31) return false;
            return ((stockFxMask | stockVelocityMask) & (1 << layer)) != 0;
        }

        /// <summary>
        /// Draw calls one ghost mesh costs the sheath per drawn frame: one per submesh that has a
        /// material (the renderer draws the same pairs).
        /// </summary>
        internal static int SubmeshDraws(int subMeshCount, int materialCount)
            => System.Math.Max(0, System.Math.Min(subMeshCount, materialCount));

        /// <summary>
        /// The one line a reentry FX build logs: what this ghost costs while its sheath draws, and
        /// the shared per-frame part (one scene depth pass and one light while any ghost draws).
        /// </summary>
        internal static string FormatBuildCost(int ghostIndex, string vesselName, int glowMaterials,
            int sheathMeshes, int sheathDrawCalls)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Reentry FX built for ghost #{0} \"{1}\": particles=0 glowMaterials={2} sheathMeshes={3}; " +
                "per frame while its sheath draws: drawCalls={4} velocityDepthRenders=1 " +
                "(its own depth map at stock's occlusion size); shared per frame while any ghost draws: " +
                "sceneDepthPass=1 light=1, no ghost cap; while stock's fire is on: its renderers are " +
                "hidden from stock's FX and velocity cameras",
                ghostIndex, vesselName ?? "?", glowMaterials, sheathMeshes, sheathDrawCalls);
        }

        /// <summary>
        /// Stock's <c>AerodynamicsFX.Update</c> + <c>LateUpdate</c> scalar for one set of
        /// conditions. Returns <c>FxScalar</c> (can be negative: stock draws only above 0) and
        /// the thermal blend <paramref name="state"/> (0 = condensation, 1 = reentry heat).
        /// </summary>
        internal static float ComputeFxScalar(double density, double speed, float mach,
            in AeroFxConstants c, out float state)
        {
            state = 0f;
            if (double.IsNaN(density) || double.IsNaN(speed) || float.IsNaN(mach)
                || density <= 0.0 || speed < 0.0)
                return 0f;

            // Update: density fade toward the top of the atmosphere, then the density curve.
            double densityFactor = density;
            if (densityFactor < c.DensityFadeStart && c.DensityFadeStart > 0.0)
            {
                double cutoffMultiplier = System.Math.Ceiling(1.0 / c.DensityFadeStart);
                densityFactor = densityFactor * (densityFactor * cutoffMultiplier);
            }
            densityFactor = System.Math.Pow(densityFactor, c.DensityExponent1) * c.DensityScalar1
                + System.Math.Pow(densityFactor, c.DensityExponent2) * c.DensityScalar2;
            state = Mathf.InverseLerp(c.ThermalStartMach, c.ThermalFullMach, mach);

            // LateUpdate.
            if (!(densityFactor * speed > 0.0))
                return 0f;
            double heatFlux = 0.5 * densityFactor * System.Math.Pow(speed, c.VelocityExponent);
            float fx = (float)System.Math.Min(1.0,
                (heatFlux - HeatFluxThreshold) / (5.0 * HeatFluxThreshold));
            state = Mathf.Clamp01(state);
            if (state < 1f && (float)density < c.MachFxFadeStart)
            {
                float fade = 0f;
                if ((float)density > c.MachFxFadeEnd)
                    fade = (c.MachFxFadeEnd - (float)density) / (c.MachFxFadeEnd - c.MachFxFadeStart);
                fx *= Mathf.Lerp(fade, 1f, state);
            }
            return fx;
        }

        /// <summary>
        /// The heat share of the effect, which drives the ghost's glow and char layers: stock's
        /// scalar times its thermal blend, so the condensation of a fast pass in dense air does not
        /// heat the hull (dark below Mach 2.5, as the glow was before).
        /// </summary>
        internal static float ThermalShare(float fxScalar, float state)
            => Mathf.Clamp01(fxScalar) * Mathf.Clamp01(state);

        /// <summary>
        /// <c>AerodynamicsFX.LateUpdate</c>'s blend: every look value is
        /// <c>Lerp(Condensation.x.GetLerp(FxScalar), ReentryHeat.x.GetLerp(FxScalar), state)</c>.
        /// The light power is floored at 0 (Unity clamps a negative light intensity the same way).
        /// </summary>
        internal static AeroFxDrawValues Blend(in AeroFxLook condensation, in AeroFxLook heat,
            float fxScalar, float state)
        {
            Color c0 = Color.Lerp(condensation.ColorMin, condensation.ColorMax, fxScalar);
            Color c1 = Color.Lerp(heat.ColorMin, heat.ColorMax, fxScalar);
            Color color = Color.Lerp(c0, c1, state);
            color.a = 1f;
            return new AeroFxDrawValues
            {
                Color = color,
                Length = Mix(condensation.Length, heat.Length, fxScalar, state),
                EdgeFade = Mix(condensation.EdgeFade, heat.EdgeFade, fxScalar, state),
                Falloff1 = Mix(condensation.Falloff1, heat.Falloff1, fxScalar, state),
                Falloff2 = Mix(condensation.Falloff2, heat.Falloff2, fxScalar, state),
                Falloff3 = Mix(condensation.Falloff3, heat.Falloff3, fxScalar, state),
                Intensity = Mix(condensation.Intensity, heat.Intensity, fxScalar, state),
                Wobble = Mix(condensation.Wobble, heat.Wobble, fxScalar, state),
                LightPower = Mathf.Max(0f, Mix(condensation.LightPower, heat.LightPower, fxScalar, state)),
            };
        }

        private static float Mix(AeroFxRange a, AeroFxRange b, float fxScalar, float state)
            => Mathf.Lerp(a.Lerp(fxScalar), b.Lerp(fxScalar), state);

        /// <summary>
        /// <c>AerodynamicsFX</c> picks <c>min(count - detailLevel, count - 1)</c> and
        /// <c>FXCamera.OnPreRender</c> clamps it into the array.
        /// </summary>
        internal static int ShaderLod(int shaderCount, int detailLevel)
        {
            if (shaderCount <= 0) return -1;
            int lod = System.Math.Min(shaderCount - detailLevel, shaderCount - 1);
            return Mathf.Clamp(lod, 0, shaderCount - 1);
        }

        /// <summary><c>_FXColor</c>'s alpha in <c>FXCamera.OnPreRender</c>.</summary>
        internal static float FxColorAlpha(float intensity, int shaderLod)
            => intensity * (shaderLod + 1);

        /// <summary>
        /// How far from the flight camera stock's FX camera draws: its far clip,
        /// <c>max(startFarClipPlane, FlightCamera.Distance + farClipPlaneBuffer)</c>.
        /// </summary>
        internal static float StockReach(float flightCameraDistance)
        {
            if (float.IsNaN(flightCameraDistance) || flightCameraDistance < 0f)
                flightCameraDistance = 0f;
            return Mathf.Max(StockFxReachMin, flightCameraDistance + StockFxReachBuffer);
        }

        /// <summary>Whether a ghost of this radius at this distance from the camera is within reach.</summary>
        internal static bool WithinReach(float cameraDistance, float boundsRadius, float reach)
        {
            if (float.IsNaN(cameraDistance) || float.IsInfinity(cameraDistance)) return false;
            return cameraDistance - Mathf.Max(0f, boundsRadius) <= reach;
        }

        /// <summary>The velocity camera's box (half size) and far clip for a ghost of this size.</summary>
        internal static void VelocityCamBox(float ghostSize, out float box, out float far)
        {
            if (float.IsNaN(ghostSize) || ghostSize < 0f) ghostSize = 0f;
            box = Mathf.Max(StockVelocityCamMinBox, ghostSize);
            far = Mathf.Max(StockVelocityCamRange, box + ghostSize);
        }

        /// <summary>
        /// Whether a ghost draws its own sheath this frame (owner rulings 2026-11-28 01:06 and
        /// 01:07: every ghost always wears its own fire, with its own direction and strength, even
        /// while stock's fire is on; no ghost cap). Stock's gates come first (scalar above 0, map
        /// closed, thermal-colour debug off, detail level above 0), then stock's reach (its FX
        /// camera's far clip), visible meshes and the isolation test mode.
        /// </summary>
        internal static GhostAeroSheathDecision DecideSheath(float fxScalar, bool mapView,
            bool thermalColorsDebug, int detailLevel, bool assetsReady, bool withinReach,
            int meshCount, bool isolated)
        {
            if (!(fxScalar > 0f)) return GhostAeroSheathDecision.NoEffect;
            if (mapView) return GhostAeroSheathDecision.MapView;
            if (thermalColorsDebug) return GhostAeroSheathDecision.ThermalColorsDebug;
            if (detailLevel <= 0) return GhostAeroSheathDecision.DetailOff;
            if (!assetsReady) return GhostAeroSheathDecision.NoStockAssets;
            if (!withinReach) return GhostAeroSheathDecision.OutOfReach;
            if (meshCount <= 0) return GhostAeroSheathDecision.NoMeshes;
            if (isolated) return GhostAeroSheathDecision.Isolated;
            return GhostAeroSheathDecision.Draw;
        }

        /// <summary>
        /// Whether a ghost may own the reentry light this frame. Stock lights at any detail level
        /// while its scalar is above 0 and the map is closed; the ghost light also needs the FX
        /// reach and a visible mesh (a hidden ghost, a loop pause, lights nothing). Stock's own
        /// light being on does not stop it: the ghost's fire is its own.
        /// </summary>
        internal static GhostAeroLightDecision DecideLight(float fxScalar, bool mapView,
            bool stockLightAvailable, bool withinReach, int visibleMeshCount)
        {
            if (!(fxScalar > 0f)) return GhostAeroLightDecision.NoEffect;
            if (mapView) return GhostAeroLightDecision.MapView;
            if (!stockLightAvailable) return GhostAeroLightDecision.NoStockLight;
            if (!withinReach) return GhostAeroLightDecision.OutOfReach;
            if (visibleMeshCount <= 0) return GhostAeroLightDecision.NoMeshes;
            return GhostAeroLightDecision.Offer;
        }

        /// <summary>The nearer offer wins the one light; ties keep the first.</summary>
        internal static bool LightOfferWins(bool haveCurrent, float currentDistance, float offerDistance)
            => !haveCurrent || offerDistance < currentDistance;

        /// <summary>
        /// Whether a ghost mesh is one stock's FX camera would draw: its renderer on, its object
        /// active and its layer one that camera sees (stock's camera draws layers 0 and 17, so a
        /// part model on another layer, a plume or a transparent helper, wears no fire on stock's
        /// vessel either).
        /// </summary>
        internal static bool SheathMeshVisible(bool rendererEnabled, bool activeInHierarchy, int layer,
            int stockFxMask)
            => rendererEnabled && activeInHierarchy && LayerSeenByStockFx(layer, stockFxMask, 0);

        /// <summary>Frames a diagnostic capture waits for a drawing frame before it gives up.</summary>
        internal const int DiagnosticCaptureTimeoutFrames = 300;

        /// <summary>Whether a capture requested at <paramref name="requestFrame"/> is abandoned.</summary>
        internal static bool ShouldAbandonCapture(int requestFrame, int frame)
            => frame - requestFrame > DiagnosticCaptureTimeoutFrames;

        /// <summary>Whether Parsek's scene depth pass runs in this diagnostic mode.</summary>
        internal static bool SceneDepthPassOn(GhostAeroFxDiagnosticMode mode)
            => mode != GhostAeroFxDiagnosticMode.NoSceneDepth;

        /// <summary>Whether the FX camera draws through stock's replacement shader in this mode.</summary>
        internal static bool UsesReplacementShader(GhostAeroFxDiagnosticMode mode)
            => mode != GhostAeroFxDiagnosticMode.SanityUnlit;

        /// <summary>
        /// A one-line summary of a captured colour image: how many pixels differ from the clear
        /// colour, the brightest channel values among them and their mean alpha.
        /// </summary>
        internal static string DescribePixels(Color32[] pixels, Color32 background)
        {
            if (pixels == null || pixels.Length == 0) return "pixels=0";
            int covered = 0;
            byte maxR = 0, maxG = 0, maxB = 0;
            long alphaSum = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                if (p.r == background.r && p.g == background.g && p.b == background.b && p.a == background.a)
                    continue;
                covered++;
                if (p.r > maxR) maxR = p.r;
                if (p.g > maxG) maxG = p.g;
                if (p.b > maxB) maxB = p.b;
                alphaSum += p.a;
            }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "pixels={0} covered={1} ({2:F3}%) maxRGB=({3},{4},{5}) meanAlphaCovered={6:F1}",
                pixels.Length, covered, 100.0 * covered / pixels.Length, maxR, maxG, maxB,
                covered > 0 ? (double)alphaSum / covered : 0.0);
        }

        /// <summary>
        /// A one-line summary of a depth map read back as colour (depth in the red channel): its
        /// red range and how many pixels differ from the corner pixel (the cleared far value).
        /// </summary>
        internal static string DescribeDepthPixels(Color32[] pixels)
        {
            if (pixels == null || pixels.Length == 0) return "pixels=0";
            byte min = 255, max = 0;
            byte corner = pixels[0].r;
            int differ = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                byte r = pixels[i].r;
                if (r < min) min = r;
                if (r > max) max = r;
                if (r != corner) differ++;
            }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "pixels={0} red={1}..{2} corner={3} differFromCorner={4} ({5:F3}%)",
                pixels.Length, min, max, corner, differ, 100.0 * differ / pixels.Length);
        }

        /// <summary>One camera's state for the renderer's camera dump.</summary>
        internal static string FormatCameraState(string name, bool enabled, bool activeInHierarchy,
            float depth, string clearFlags, int cullingMask, string target, float near, float far,
            float fov, bool orthographic, string depthTextureMode)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "'{0}' enabled={1} active={2} depth={3:G6} clear={4} mask=0x{5:X} target={6} near={7:G4} " +
                "far={8:G6} fov={9:F1} ortho={10} depthTex={11}",
                name ?? "?", enabled, activeInHierarchy, depth, clearFlags ?? "?", cullingMask,
                target ?? "screen", near, far, fov, orthographic, depthTextureMode ?? "?");
        }
    }

    /// <summary>
    /// Keeps ghost renderers out of stock's FX and velocity cameras without touching what any
    /// other camera sees (G.7 "Y1 design", masking). Before one of stock's two cameras culls,
    /// <see cref="PreCull"/> sets <c>Renderer.forceRenderingOff</c> on each candidate that is not
    /// already off and remembers exactly those; every other camera's cull, every post-render, the
    /// renderer's next LateUpdate and its disable / destroy restore them. A renderer something else
    /// switched off is never touched, and a mask can never outlive the stock camera's render: the
    /// main camera's next cull restores it first even if stock's post-render never came. Pure over
    /// the two delegates, so the ledger is unit-tested with plain objects.
    /// </summary>
    internal sealed class StockFxMaskLedger<T> where T : class
    {
        private readonly System.Func<T, bool> isOff;
        private readonly System.Action<T, bool> setOff;
        private readonly System.Collections.Generic.List<T> candidates = new System.Collections.Generic.List<T>();
        private readonly System.Collections.Generic.List<T> masked = new System.Collections.Generic.List<T>();

        internal StockFxMaskLedger(System.Func<T, bool> isOff, System.Action<T, bool> setOff)
        {
            this.isOff = isOff ?? throw new System.ArgumentNullException(nameof(isOff));
            this.setOff = setOff ?? throw new System.ArgumentNullException(nameof(setOff));
        }

        internal int CandidateCount => candidates.Count;
        internal int MaskedCount => masked.Count;

        /// <summary>Renderers masked by the last stock-camera cull (for the frame line).</summary>
        internal int LastMaskedCount { get; private set; }

        /// <summary>Writes that failed (a destroyed renderer), summed since the last read.</summary>
        internal int FailedWrites { get; private set; }

        internal void ClearCandidates() => candidates.Clear();

        internal void AddCandidate(T item)
        {
            if (item != null) candidates.Add(item);
        }

        internal int TakeFailedWrites()
        {
            int n = FailedWrites;
            FailedWrites = 0;
            return n;
        }

        /// <summary>
        /// A camera is about to cull. Stock's FX or velocity camera masks the candidates; any
        /// other camera restores whatever is still masked. Returns how many are masked after.
        /// </summary>
        internal int PreCull(bool stockFxCamera)
        {
            Restore();
            if (!stockFxCamera) return 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                T item = candidates[i];
                bool alreadyOff;
                try { alreadyOff = isOff(item); }
                catch (System.Exception) { FailedWrites++; continue; }
                if (alreadyOff) continue;
                try
                {
                    setOff(item, true);
                    masked.Add(item);
                }
                catch (System.Exception) { FailedWrites++; }
            }
            LastMaskedCount = masked.Count;
            return masked.Count;
        }

        /// <summary>A camera finished rendering: restore everything masked.</summary>
        internal int PostRender() => Restore();

        /// <summary>Switches every masked renderer back on; always empties the masked list.</summary>
        internal int Restore()
        {
            int restored = 0;
            for (int i = 0; i < masked.Count; i++)
            {
                try
                {
                    setOff(masked[i], false);
                    restored++;
                }
                catch (System.Exception) { FailedWrites++; }
            }
            masked.Clear();
            return restored;
        }
    }
}
