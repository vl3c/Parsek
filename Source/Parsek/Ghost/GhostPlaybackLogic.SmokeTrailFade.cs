using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // Engine smoke trails faded by air density, transcribed from stock SmokeTrailControl.
    internal static partial class GhostPlaybackLogic
    {
        #region Smoke trail fade

        /// <summary>Stock <c>SmokeTrailControl.Update</c> draws the trail only while its
        /// air-density factor is above this literal.</summary>
        internal const float SmokeTrailVisibleAirDensity = 0.001f;

        /// <summary>Drawn fade levels between the visibility threshold and full density. The
        /// material tint is rewritten only when the level changes, so a climbing ghost costs a
        /// handful of writes across the whole fade band, not one per frame.</summary>
        internal const int SmokeTrailFadeLevels = 32;

        /// <summary>Altitude change (metres) below which the cached air density is reused. The
        /// stock fade band spans about 19 km on Kerbin, so 10 m is far below one fade level.</summary>
        internal const double SmokeTrailDensityReuseMeters = 10.0;

        /// <summary>
        /// Stock's <c>airDensity = Mathf.InverseLerp(fadeEndDns, fadeStartDns, (float)ship_dns)</c>,
        /// with <c>Mathf.InverseLerp</c>'s own body (0 when the two ends are equal, else the
        /// clamped fraction) so it runs headless.
        /// </summary>
        internal static float ComputeSmokeTrailAirDensity(float fadeEndDns, float fadeStartDns, double density)
        {
            if (fadeEndDns == fadeStartDns) return 0f;
            float value = (float)density;
            if (float.IsNaN(value)) return 0f;
            float t = (value - fadeEndDns) / (fadeStartDns - fadeEndDns);
            if (t < 0f) return 0f;
            if (t > 1f) return 1f;
            return t;
        }

        /// <summary>0 when stock would disable the renderer, else 1..<see cref="SmokeTrailFadeLevels"/>.</summary>
        internal static int QuantizeSmokeTrailAirDensity(float airDensity)
        {
            if (!(airDensity > SmokeTrailVisibleAirDensity)) return 0;
            int level = 1 + (int)Math.Round(Math.Min(airDensity, 1f) * (SmokeTrailFadeLevels - 1));
            return Math.Min(level, SmokeTrailFadeLevels);
        }

        /// <summary>The colour stock writes to the trail material at <paramref name="airDensity"/>:
        /// a lerp from <c>fadedColor</c> to <c>maxAlpha</c> on every channel when
        /// <c>fadeToColor</c>, else white with alpha <c>Lerp(0, maxAlpha, airDensity)</c>.</summary>
        internal static Color ComputeSmokeTrailTint(
            float maxAlpha, bool fadeToColor, Color fadedColor, float airDensity)
        {
            float t = airDensity < 0f ? 0f : (airDensity > 1f ? 1f : airDensity);
            if (fadeToColor)
            {
                return new Color(
                    fadedColor.r + (maxAlpha - fadedColor.r) * t,
                    fadedColor.g + (maxAlpha - fadedColor.g) * t,
                    fadedColor.b + (maxAlpha - fadedColor.b) * t,
                    fadedColor.a + (maxAlpha - fadedColor.a) * t);
            }
            return new Color(1f, 1f, 1f, maxAlpha * t);
        }

        /// <summary>Whether the air density at the ghost has to be evaluated at all: a body
        /// without an atmosphere, or an altitude at or above its top, is vacuum (density 0).</summary>
        internal static bool IsInsideAtmosphere(bool bodyHasAtmosphere, double atmosphereDepth, double altitude)
        {
            if (!bodyHasAtmosphere) return false;
            if (double.IsNaN(altitude)) return false;
            return altitude < atmosphereDepth;
        }

        /// <summary>Whether the cached density can stand for the ghost's current altitude.</summary>
        internal static bool CanReuseSmokeTrailDensity(
            string cachedBodyName, double cachedAltitude, string bodyName, double altitude)
        {
            if (cachedBodyName == null || bodyName == null) return false;
            if (!string.Equals(cachedBodyName, bodyName, StringComparison.Ordinal)) return false;
            if (double.IsNaN(cachedAltitude) || double.IsNaN(altitude)) return false;
            return Math.Abs(altitude - cachedAltitude) < SmokeTrailDensityReuseMeters;
        }

        /// <summary>True when any engine of the ghost is lit and carries a captured smoke fade,
        /// the only case in which <see cref="UpdateEngineSmokeTrailFades"/> does any work.</summary>
        internal static bool HasLitEngineSmokeTrail(GhostPlaybackState state)
        {
            if (state?.engineInfos == null) return false;
            foreach (var info in state.engineInfos.Values)
            {
                if (info != null && info.currentPower > 0f
                    && info.smokeTrailFades != null && info.smokeTrailFades.Count > 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Drives every lit engine's smoke trail by the air density at the ghost, the way stock
        /// <c>SmokeTrailControl</c> drives the real one (todo GHOST-SMOKE-IN-VACUUM): no smoke in
        /// vacuum, a fading trail in the upper atmosphere, full smoke low down. Called once per
        /// visible, unsuppressed frame after the part events; returns at the first check for a
        /// ghost with no lit smoking engine. Stock reads the ACTIVE vessel's density
        /// (<c>FlightGlobals.ship_dns</c>); a ghost replays the vessel it recorded, so it reads
        /// its own, from the body's base pressure and temperature curves at its playback altitude.
        /// </summary>
        internal static void UpdateEngineSmokeTrailFades(GhostPlaybackState state)
        {
            if (!HasLitEngineSmokeTrail(state)) return;
            if (!TryResolveSmokeTrailDensity(state, out double density)) return;

            int changed = 0;
            int hidden = 0;
            int drawn = 0;
            foreach (var info in state.engineInfos.Values)
            {
                if (info == null || info.currentPower <= 0f || info.smokeTrailFades == null) continue;
                for (int i = 0; i < info.smokeTrailFades.Count; i++)
                {
                    GhostSmokeTrailFade fade = info.smokeTrailFades[i];
                    if (fade == null || fade.renderer == null) continue;
                    float airDensity = ComputeSmokeTrailAirDensity(fade.fadeEndDns, fade.fadeStartDns, density);
                    int level = QuantizeSmokeTrailAirDensity(airDensity);
                    if (level == 0) hidden++; else drawn++;
                    if (level == fade.appliedLevel) continue;
                    ApplySmokeTrailFade(state, fade, level, airDensity);
                    changed++;
                }
            }

            if (changed > 0)
                ParsekLog.VerboseRateLimited("GhostVisual", $"smoke-fade-{state.vesselName}",
                    $"Smoke trail fade: ghost \"{state.vesselName}\" body={state.lastInterpolatedBodyName} " +
                    $"alt={state.lastInterpolatedAltitude.ToString("F0", CultureInfo.InvariantCulture)} " +
                    $"density={density.ToString("G4", CultureInfo.InvariantCulture)} " +
                    $"drawn={drawn} hidden={hidden} changed={changed}", 5.0);
        }

        /// <summary>
        /// Re-applies a lit engine's last known smoke visibility after an emission write
        /// re-enabled every renderer of the engine (<see cref="SetEngineEmissionWithOutcome"/>),
        /// so a throttle event or an FX restore in vacuum does not flash the trail back on for a
        /// frame. A fade never written yet (-1) is left to the next per-frame drive.
        /// </summary>
        internal static void ReapplySmokeTrailVisibility(EngineGhostInfo info)
        {
            if (info?.smokeTrailFades == null) return;
            for (int i = 0; i < info.smokeTrailFades.Count; i++)
            {
                GhostSmokeTrailFade fade = info.smokeTrailFades[i];
                if (fade == null || fade.renderer == null || fade.appliedLevel != 0) continue;
                fade.renderer.enabled = false;
            }
        }

        // Renderer / Material ECalls; NoInlining keeps them out of UpdateEngineSmokeTrailFades,
        // which a headless test calls.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplySmokeTrailFade(
            GhostPlaybackState state, GhostSmokeTrailFade fade, int level, float airDensity)
        {
            fade.appliedLevel = level;
            if (level == 0)
            {
                fade.renderer.enabled = false;
                return;
            }
            // A fade with no colour name is visibility only (stock FXFadeByPressure writes no tint),
            // so it needs no material instance.
            if (string.IsNullOrEmpty(fade.shaderColorName))
            {
                fade.renderer.enabled = true;
                return;
            }
            Material material = fade.material;
            if (material == null)
            {
                // Renderer.material clones the shared material once; the clone is registered with
                // the ghost so DestroyGhostResources frees it with the others.
                material = fade.renderer.material;
                fade.material = material;
                if (material != null)
                {
                    if (state.materials == null) state.materials = new System.Collections.Generic.List<Material>();
                    state.materials.Add(material);
                }
            }
            if (material != null)
                material.SetColor(fade.shaderColorName,
                    ComputeSmokeTrailTint(fade.maxAlpha, fade.fadeToColor, fade.fadedColor, airDensity));
            fade.renderer.enabled = true;
        }

        private static bool TryResolveSmokeTrailDensity(GhostPlaybackState state, out double density)
        {
            string bodyName = state.lastInterpolatedBodyName;
            double altitude = state.lastInterpolatedAltitude;
            if (CanReuseSmokeTrailDensity(state.smokeDensityBodyName, state.smokeDensityAltitude,
                    bodyName, altitude))
            {
                density = state.smokeDensity;
                return true;
            }
            if (string.IsNullOrEmpty(bodyName) || double.IsNaN(altitude))
            {
                density = 0.0;
                return false;
            }
            if (!TryComputeBodyAirDensity(bodyName, altitude, out density))
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"smoke-fade-nobody-{bodyName}",
                    $"Smoke trail fade: body '{bodyName}' not resolved; smoke left as is", 30.0);
                return false;
            }
            state.smokeDensityBodyName = bodyName;
            state.smokeDensityAltitude = altitude;
            state.smokeDensity = density;
            return true;
        }

        // CelestialBody / FlightGlobals reads, kept out of the callers above so a headless test of
        // the pure helpers never JITs them (mono runs FlightGlobals' initializer at JIT time).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryComputeBodyAirDensity(string bodyName, double altitude, out double density)
        {
            density = 0.0;
            // Plain loop: no predicate closure on a per-frame path.
            CelestialBody body = null;
            var bodies = FlightGlobals.Bodies;
            if (bodies != null)
                for (int i = 0; i < bodies.Count; i++)
                    if (bodies[i] != null && bodies[i].name == bodyName) { body = bodies[i]; break; }
            if (body == null) return false;
            if (!IsInsideAtmosphere(body.atmosphere, body.atmosphereDepth, altitude)) return true;
            double pressure = body.GetPressure(altitude);
            double temperature = body.GetTemperature(altitude);
            if (double.IsNaN(pressure) || pressure <= 0.0 || double.IsNaN(temperature) || temperature <= 0.0)
                return true;
            density = body.GetDensity(pressure, temperature);
            if (double.IsNaN(density) || density < 0.0) density = 0.0;
            return true;
        }

        #endregion
    }
}
