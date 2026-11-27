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
        StockDraws = 5,
        OutOfReach = 6,
        NoStockAssets = 7,
        NoMeshes = 8,
        Isolated = 9,
        FrameCap = 10,
    }

    /// <summary>Why a ghost may or may not own the reentry light this frame.</summary>
    internal enum GhostAeroLightDecision
    {
        Offer = 0,
        NoEffect = 1,
        MapView = 2,
        StockLightOn = 3,
        OutOfReach = 4,
        NoStockLight = 5,
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

        /// <summary>Most ghosts that draw their own sheath in one frame.</summary>
        internal const int MaxSheathsPerFrame = 4;

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
        /// Whether a ghost draws its own sheath this frame. Stock's gates come first (scalar above
        /// 0, map closed, thermal-colour debug off, detail level above 0), then the two ownership
        /// rules: while stock's FX camera is on, it already paints every vessel in its reach, so the
        /// ghost wears that fire and draws none of its own; beyond the reach no stock fire shows.
        /// </summary>
        internal static GhostAeroSheathDecision DecideSheath(float fxScalar, bool mapView,
            bool thermalColorsDebug, int detailLevel, bool assetsReady, bool stockFxCameraOn,
            bool withinReach, int meshCount, bool isolated, int drawnThisFrame)
        {
            if (!(fxScalar > 0f)) return GhostAeroSheathDecision.NoEffect;
            if (mapView) return GhostAeroSheathDecision.MapView;
            if (thermalColorsDebug) return GhostAeroSheathDecision.ThermalColorsDebug;
            if (detailLevel <= 0) return GhostAeroSheathDecision.DetailOff;
            if (!assetsReady) return GhostAeroSheathDecision.NoStockAssets;
            if (stockFxCameraOn) return GhostAeroSheathDecision.StockDraws;
            if (!withinReach) return GhostAeroSheathDecision.OutOfReach;
            if (meshCount <= 0) return GhostAeroSheathDecision.NoMeshes;
            if (isolated) return GhostAeroSheathDecision.Isolated;
            if (drawnThisFrame >= MaxSheathsPerFrame) return GhostAeroSheathDecision.FrameCap;
            return GhostAeroSheathDecision.Draw;
        }

        /// <summary>
        /// Whether a ghost may own the reentry light this frame. Stock lights at any detail level
        /// while its scalar is above 0 and the map is closed; the ghost light stays off while
        /// stock's own is on, and beyond the FX reach.
        /// </summary>
        internal static GhostAeroLightDecision DecideLight(float fxScalar, bool mapView,
            bool stockLightAvailable, bool stockLightOn, bool withinReach)
        {
            if (!(fxScalar > 0f)) return GhostAeroLightDecision.NoEffect;
            if (mapView) return GhostAeroLightDecision.MapView;
            if (!stockLightAvailable) return GhostAeroLightDecision.NoStockLight;
            if (stockLightOn) return GhostAeroLightDecision.StockLightOn;
            if (!withinReach) return GhostAeroLightDecision.OutOfReach;
            return GhostAeroLightDecision.Offer;
        }

        /// <summary>The nearer offer wins the one light; ties keep the first.</summary>
        internal static bool LightOfferWins(bool haveCurrent, float currentDistance, float offerDistance)
            => !haveCurrent || offerDistance < currentDistance;
    }
}
