using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    // S3 launch dust, part parity P9 Y3: stock's ModuleSurfaceFX rule per engine along its thrust
    // axis (decompiled ModuleSurfaceFX.Update and SurfaceFX.LateUpdate, KSP 1.12.5). Pure: the
    // engine's raycasts and the particle systems live in GhostPlaybackEngine.LaunchDust.cs.
    internal static partial class GhostPlaybackLogic
    {
        #region S3 - launch dust

        /// <summary>
        /// The layers the dust looks for the ground on: stock <c>ModuleSurfaceFX</c>'s own raycast
        /// mask (decompiled, <c>0x40008000</c>: layer 15 Local Scenery, which holds the terrain and
        /// the KSC pad and runway, and layer 30, which holds the pad's <c>PadFXReceiver</c>).
        /// </summary>
        internal const int LaunchDustSurfaceLayerMask = 0x40008000;

        /// <summary>The collider tag on which stock draws the pad's own effect and no dust.</summary>
        internal const string LaunchpadFxTag = "LaunchpadFX";
        /// <summary>The collider tag on which stock draws nothing (a wheel's own piston).</summary>
        internal const string WheelPistonColliderTag = "Wheel_Piston_Collider";

        /// <summary>Stock <c>ModuleSurfaceFX</c>'s field defaults (decompiled constructor).</summary>
        internal const float SurfaceFxDefaultFxMax = 1f;
        internal const float SurfaceFxDefaultMaxDistance = 50f;
        internal const float SurfaceFxDefaultFalloff = 2f;

        /// <summary>How far above the ground the built fallback disc sits, so its billboards are not half
        /// buried (the stock prefab copy stands at the hit point, as stock's).</summary>
        internal const float LaunchDustGroundLiftMeters = 0.5f;

        /// <summary>Stock <c>SurfaceFX</c>'s start alpha at full strength: <c>Lerp(0, 0.5, fxScale)</c>.</summary>
        internal const float SurfaceFxAlphaMax = 0.5f;
        /// <summary>Stock <c>SurfaceFX.pushThreshold</c> (decompiled constructor).</summary>
        internal const float SurfaceFxPushThreshold = 0.5f;

        /// <summary>
        /// Above this altitude over the body's datum, plus the longest reach of the ghost's lit
        /// engines, the ghost makes no raycast: it is higher than the highest terrain of every
        /// stock body (Bop's peaks, about 21.8 km). A cost gate only; the raycast decides.
        /// </summary>
        internal const double LaunchDustProbeCeilingMeters = 25000.0;

        /// <summary>
        /// Stock's classification of a surface raycast hit (decompiled <c>ModuleSurfaceFX.Update</c>):
        /// a collider tagged <c>LaunchpadFX</c> is the launch pad (the pad's own effect, no dust),
        /// one tagged <c>Wheel_Piston_Collider</c> is nothing, anything else is terrain.
        /// </summary>
        internal static LaunchDustSurface ClassifyLaunchDustHit(bool launchpadTag, bool wheelPistonTag)
        {
            if (launchpadTag) return LaunchDustSurface.Launchpad;
            if (wheelPistonTag) return LaunchDustSurface.None;
            return LaunchDustSurface.Terrain;
        }

        /// <summary>Whether a surface raises dust (or spray): terrain and water do, the pad and nothing do not.</summary>
        internal static bool LaunchDustSurfaceRaisesDust(LaunchDustSurface surface)
            => surface == LaunchDustSurface.Terrain || surface == LaunchDustSurface.Water;

        /// <summary>
        /// One engine's share of stock's effect before distance: <c>GetCurrentThrust() /
        /// GetMaxThrust() * fxMax</c>. A ghost has no thrust of its own, so it is the commanded
        /// power times the engine's thrust share in the ghost's air when the engine tracks one
        /// (<see cref="EngineGhostInfo.thrustFraction"/>), else times 1.
        /// </summary>
        internal static float ComputeSurfaceFxThrustScale(
            float power, bool usesThrustFraction, float thrustFraction, float fxMax)
        {
            if (float.IsNaN(power) || power <= 0f || float.IsNaN(fxMax) || fxMax <= 0f) return 0f;
            float share = usesThrustFraction && !float.IsNaN(thrustFraction)
                ? Math.Max(0f, Math.Min(1f, thrustFraction)) : 1f;
            return Math.Min(1f, power) * share * fxMax;
        }

        /// <summary>
        /// Stock's raycast result for one engine (decompiled <c>ModuleSurfaceFX.Update</c>), given
        /// what the raycast along the thrust axis returned:
        /// <list type="bullet">
        /// <item>over an ocean body an engine at or below sea level draws nothing;</item>
        /// <item>a hit above the datum is classified by its collider's tags, its distance the hit's;</item>
        /// <item>no hit (or one below the datum) over an ocean body, with the axis pointing down and
        /// the engine above the sea but lower than the reach, is water at the sea-level point along
        /// the axis, <c>altitude / cos</c> away;</item>
        /// <item>anything else draws nothing.</item>
        /// </list>
        /// </summary>
        internal static LaunchDustSurface ClassifySurfaceFxRay(
            bool raycastHit, double hitAltitude, float hitDistance, bool launchpadTag, bool wheelPistonTag,
            bool bodyHasOcean, double engineAltitude, float cosDown, float maxDistance, out float distance)
        {
            distance = float.NaN;
            if (bodyHasOcean && !(engineAltitude > 0.0)) return LaunchDustSurface.None;
            if (raycastHit && hitAltitude >= 0.0)
            {
                distance = hitDistance;
                return ClassifyLaunchDustHit(launchpadTag, wheelPistonTag);
            }
            if (bodyHasOcean && cosDown > 0f && engineAltitude < maxDistance && engineAltitude > 0.0)
            {
                distance = (float)(engineAltitude / cosDown);
                return LaunchDustSurface.Water;
            }
            return LaunchDustSurface.None;
        }

        /// <summary>
        /// Stock's <c>ScaledFX</c>: <c>thrustScale * (1 - d / maxDistance)^falloff</c>. Zero at or past
        /// the reach, where stock's water branch can be handed a slant distance longer than the
        /// reach (its altitude test is vertical) and would square a negative base: no dust past
        /// the reach, as the land branch already guarantees.
        /// </summary>
        internal static float ComputeSurfaceFxScaled(float thrustScale, float distance, float maxDistance, float falloff)
        {
            if (float.IsNaN(thrustScale) || thrustScale <= 0f || float.IsNaN(distance) || float.IsNaN(maxDistance)
                || maxDistance <= 0f || float.IsNaN(falloff) || distance >= maxDistance)
                return 0f;
            float d = distance < 0f ? 0f : distance;
            return thrustScale * (float)Math.Pow(1.0 - d / maxDistance, falloff);
        }

        /// <summary>
        /// Stock's <c>Vsrf</c>, the direction the dust is pushed: the engine-to-hit line projected
        /// onto the surface, unit length, times the engine's thrust share. Zero when the line is
        /// along the normal to within Unity's normalize epsilon (1e-5), as <c>Vector3.normalized</c> gives.
        /// </summary>
        internal static Vector3 ComputeSurfaceFxFlow(Vector3 point, Vector3 enginePosition, Vector3 normal, float thrustScale)
        {
            Vector3 r = point - enginePosition;
            double nn = (double)normal.x * normal.x + (double)normal.y * normal.y + (double)normal.z * normal.z;
            if (nn < 1e-12) return Vector3.zero;
            double dot = ((double)r.x * normal.x + (double)r.y * normal.y + (double)r.z * normal.z) / nn;
            double px = r.x - dot * normal.x, py = r.y - dot * normal.y, pz = r.z - dot * normal.z;
            double mag = Math.Sqrt(px * px + py * py + pz * pz);
            if (mag <= 1e-5) return Vector3.zero;
            double s = thrustScale / mag;
            return new Vector3((float)(px * s), (float)(py * s), (float)(pz * s));
        }

        /// <summary>Whether the ghost is low enough to probe for a surface at all (see <see cref="LaunchDustProbeCeilingMeters"/>).</summary>
        internal static bool ShouldProbeLaunchDust(double ghostAltitude, float maxReach)
        {
            if (double.IsNaN(ghostAltitude) || double.IsInfinity(ghostAltitude)) return false;
            double reach = float.IsNaN(maxReach) || maxReach < 0f ? 0.0 : maxReach;
            return ghostAltitude <= LaunchDustProbeCeilingMeters + reach;
        }

        /// <summary>
        /// The cheapest gate: whether any engine that stock's <c>ModuleSurfaceFX</c> drives is lit,
        /// and the longest reach among them. No such engine, no dust, and nothing else runs.
        /// </summary>
        internal static bool HasLaunchDustDemand(GhostPlaybackState state, out float maxReach)
        {
            maxReach = 0f;
            if (state?.engineInfos == null) return false;
            bool any = false;
            foreach (var kvp in state.engineInfos)
            {
                EngineGhostInfo info = kvp.Value;
                if (info?.surfaceFx == null || !(info.currentPower > 0f)) continue;
                any = true;
                if (info.surfaceFx.maxDistance > maxReach) maxReach = info.surfaceFx.maxDistance;
            }
            return any;
        }

        /// <summary>
        /// The recorder's key for the engine a <c>ModuleSurfaceFX</c> reads: its position among the
        /// part's <c>ModuleEngines</c> modules (<see cref="EngineGhostInfo.moduleIndex"/>), from the
        /// module's <c>thrustProviderModuleIndex</c> into the part's whole module list. -1 when that
        /// index is out of range or names no engine (stock then draws nothing).
        /// </summary>
        internal static int ResolveSurfaceFxEngineOrdinal(int thrustProviderModuleIndex, IList<bool> moduleIsEngine)
        {
            if (moduleIsEngine == null || thrustProviderModuleIndex < 0
                || thrustProviderModuleIndex >= moduleIsEngine.Count || !moduleIsEngine[thrustProviderModuleIndex])
                return -1;
            int ordinal = 0;
            for (int i = 0; i < thrustProviderModuleIndex; i++)
                if (moduleIsEngine[i]) ordinal++;
            return ordinal;
        }

        /// <summary>Stock <c>SurfaceFX.LateUpdate</c>'s start alpha of every system it draws: <c>Lerp(0, 0.5, fxScale)</c>.</summary>
        internal static float ComputeSurfaceFxAlpha(float fxScale)
        {
            if (float.IsNaN(fxScale)) return 0f;
            return SurfaceFxAlphaMax * Math.Max(0f, Math.Min(1f, fxScale));
        }

        /// <summary>
        /// Stock <c>SurfaceFX.UpdateParticle</c>'s weight of the push added to a dust or cloud
        /// particle's velocity each frame: <c>(remainingLifetime / startLifetime)^pushThreshold</c>,
        /// so a young particle is pushed hardest. Zero for a non-positive or unreadable lifetime
        /// (stock would divide by it).
        /// </summary>
        internal static float ComputeSurfaceFxPushWeight(float remainingLifetime, float startLifetime)
        {
            if (float.IsNaN(remainingLifetime) || float.IsNaN(startLifetime) || startLifetime <= 0f
                || remainingLifetime <= 0f)
                return 0f;
            return (float)Math.Pow(remainingLifetime / startLifetime, SurfaceFxPushThreshold);
        }

        /// <summary>
        /// The effect's forward axis: stock's <c>LookRotation(Vsrf, upAxis)</c> looks along the push.
        /// A push along the normal or of zero length (a nozzle straight over flat ground) gives Unity
        /// no forward, so any axis on the surface serves; the effect is round about its up axis.
        /// </summary>
        internal static Vector3 ResolveSurfaceFxForward(Vector3 push, Vector3 normal)
        {
            Vector3 n = normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.up;
            Vector3 onSurface = push - n * Vector3.Dot(push, n);
            if (onSurface.sqrMagnitude > 1e-10f) return onSurface.normalized;
            Vector3 seed = Math.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right;
            return Vector3.Cross(seed, n).normalized;
        }

        /// <summary>
        /// What the dust log line keys on: the surfaces drawn (one bit each), the first collider
        /// hit and each surface's strength in tenths. The line is written only when this changes,
        /// so a steady frame builds no message.
        /// </summary>
        /// <summary>
        /// The coarse part of the dust log signature: which surfaces drew, whether any pad was hit,
        /// and the skip reason. A change in it is written at once; only a strength change inside one
        /// kind is rate limited. Allocation free (a string's hash allocates nothing).
        /// </summary>
        internal static int ComputeLaunchDustLogKind(int surfaceBits, int padHits, string skipReason)
        {
            unchecked
            {
                int h = (surfaceBits & 3) | (padHits > 0 ? 4 : 0);
                return skipReason == null ? h : h * 31 + skipReason.GetHashCode() + 8;
            }
        }

        /// <summary>
        /// Whether the ground under a water-branch spray point is sea: at or below the datum. An
        /// unknown height (NaN, no terrain on the body) keeps stock's water branch.
        /// </summary>
        internal static bool IsSeaUnderWaterSprayPoint(double terrainAltitude)
            => double.IsNaN(terrainAltitude) || terrainAltitude <= 0.0;

        internal static int ComputeLaunchDustLogSignature(
            int surfaceBits, int colliderId, float terrainScale, float waterScale)
        {
            unchecked
            {
                int h = surfaceBits + 1;
                h = h * 397 ^ colliderId;
                h = h * 31 + Tenths(terrainScale);
                h = h * 31 + Tenths(waterScale);
                return h;
            }
        }

        private static int Tenths(float v)
            => float.IsNaN(v) ? -1 : (int)Math.Floor(Math.Max(0f, Math.Min(1f, v)) * 10f);

        #endregion
    }

    /// <summary>
    /// One surface's sources in one frame, combined the way stock's <c>SurfaceFX.LateUpdate</c>
    /// combines the engines that feed it: the strength is the clamped sum of their <c>ScaledFX</c>;
    /// one source places the effect at its own point, normal and push; several at their
    /// <c>ScaledFX</c>-weighted mean, the push times <c>pushScale</c> (5).
    /// </summary>
    internal struct LaunchDustAggregate
    {
        /// <summary>Stock <c>SurfaceFX.pushScale</c> (decompiled constructor).</summary>
        internal const float PushScale = 5f;

        internal int count;
        private float sumScaled;
        private Vector3 sumPoint, sumNormal, sumFlow;
        private Vector3 leadPoint, leadNormal, leadFlow;

        internal void Add(Vector3 point, Vector3 normal, Vector3 flow, float scaled)
        {
            if (float.IsNaN(scaled) || scaled < 0f) scaled = 0f;
            if (count == 0)
            {
                leadPoint = point;
                leadNormal = normal;
                leadFlow = flow;
            }
            count++;
            sumScaled += scaled;
            sumPoint += point * scaled;
            sumNormal += normal * scaled;
            sumFlow += flow * scaled;
        }

        /// <summary>Stock's <c>fxScale</c>: <c>Clamp01</c> of the summed <c>ScaledFX</c>.</summary>
        internal float FxScale => count == 0 ? 0f : Math.Max(0f, Math.Min(1f, sumScaled));

        /// <summary>Where the effect stands, which way is up and the push; false with no source.</summary>
        internal bool TryResolve(out Vector3 position, out Vector3 normal, out Vector3 push)
        {
            position = Vector3.zero;
            normal = Vector3.up;
            push = Vector3.zero;
            if (count == 0) return false;
            if (count == 1)
            {
                position = leadPoint;
                normal = leadNormal;
                push = leadFlow;
                return true;
            }
            if (sumScaled <= 0f)
            {
                // Stock's weighted mean of all-zero weights is the zero vector; keep the lead's
                // place instead (nothing is drawn at zero strength anyway).
                position = leadPoint;
                normal = leadNormal;
                push = Vector3.zero;
                return true;
            }
            position = sumPoint / sumScaled;
            normal = sumNormal / sumScaled;
            push = sumFlow / sumScaled * PushScale;
            return true;
        }
    }
}
