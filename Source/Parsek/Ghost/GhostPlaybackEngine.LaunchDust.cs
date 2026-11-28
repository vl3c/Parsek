using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // S3 launch dust, part parity P9 Y3: stock's ModuleSurfaceFX rule per engine, the raycasts and
    // the ghost's dust emitter. The decisions are pure in GhostPlaybackLogic.LaunchDust.cs; every
    // Unity call sits in a [MethodImpl(NoInlining)] helper the headless tests never enter.
    internal partial class GhostPlaybackEngine
    {
        #region Launch dust

        /// <summary>
        /// S3 launch dust, driven once per visible frame (a warp-suppressed frame drives it to
        /// zero). Everything else is <see cref="DriveLaunchDustFrame"/>, handed the engine's own
        /// per-frame lazy-build counter: the cap the lazy reentry build uses, so a pad full of
        /// simultaneously-launching looped ghosts never builds every emitter on one frame (#450).
        /// </summary>
        private void UpdateLaunchDust(
            int recIdx, GhostPlaybackState state, IPlaybackTrajectory traj, float warpRate)
        {
            if (state == null || state.ghost == null) return;

            if (GhostPlaybackLogic.ShouldSuppressVisualFx(warpRate))
            {
                DriveLaunchDustToZero(state);
                return;
            }

            DriveLaunchDustFrame(recIdx, state, ref frameLazyReentryBuildCount);
        }

        /// <summary>
        /// One launch dust frame, stock's rule (decompiled <c>ModuleSurfaceFX.Update</c>, KSP 1.12.5):
        /// every lit engine that a <c>ModuleSurfaceFX</c> drives raycasts along its own thrust axis
        /// for that module's <c>maxDistance</c>; a terrain hit raises dust at the hit point with
        /// <c>thrust share * fxMax * (1 - d / maxDistance)^falloff</c>, a hit on the pad's
        /// <c>LaunchpadFX</c> collider raises none, and nothing within the reach raises none. The
        /// engines' terrain hits feed ONE emitter per ghost at their strength-weighted mean, the way
        /// stock's <c>SurfaceFX</c> combines the engines it merges (stock merges hits that land
        /// within about 2.2 m of each other, so a single nozzle or a tight cluster draws the same;
        /// a wide cluster draws one cloud at the mean where stock draws one per spot).
        ///
        /// Called by <see cref="UpdateLaunchDust"/> and by the part parity dust cases, which hold a
        /// built ghost over a surface. The emitter builds lazily under
        /// <paramref name="lazyBuildCount"/>'s cap; a build that fails is never retried.
        /// </summary>
        internal static void DriveLaunchDustFrame(int recIdx, GhostPlaybackState state, ref int lazyBuildCount)
        {
            if (state == null) return;
            if (state.ghost == null || !GhostPlaybackLogic.HasLaunchDustDemand(state, out float maxReach))
            {
                LogLaunchDustOnChange(recIdx, state, 0, 0, 0, 0f, 0f, LaunchDustSkipNoEngine);
                DriveLaunchDustToZero(state);
                return;
            }

            var terrain = new LaunchDustAggregate();
            var water = new LaunchDustAggregate();
            int padHits = 0;
            int colliderId = 0;
            bool atmosphere = false;
            string skip = TraceLaunchDustSources(
                state, maxReach, ref terrain, ref water, ref padHits, ref colliderId, ref atmosphere);
            int bits = (terrain.count > 0 ? 1 : 0) | (water.count > 0 ? 2 : 0) | (padHits > 0 ? 4 : 0);
            float terrainScale = terrain.FxScale;
            LogLaunchDustOnChange(recIdx, state, bits, colliderId, padHits, terrainScale, water.FxScale, skip);
            if (skip != null || terrainScale <= 0f)
            {
                DriveLaunchDustToZero(state);
                return;
            }

            if (state.launchDustInfo == null) state.launchDustInfo = new LaunchDustInfo();
            LaunchDustInfo info = state.launchDustInfo;
            if (info.terrain == null)
            {
                if (info.terrainUnavailable) return;
                if (lazyBuildCount >= GhostPlayback.MaxLazyReentryBuildsPerFrame)
                {
                    ParsekLog.VerboseRateLimited("GhostVisual", $"launch-dust-throttle-{recIdx}",
                        $"Launch dust build throttled for ghost #{recIdx}; retrying next frame " +
                        $"(used {lazyBuildCount}/{GhostPlayback.MaxLazyReentryBuildsPerFrame})", 1.0);
                    return;
                }
                lazyBuildCount++;
                info.terrain = GhostVisualBuilder.TryBuildLaunchDust(state.ghost, state.vesselName, water: false);
                if (info.terrain == null)
                {
                    info.terrainUnavailable = true;
                    return;
                }
            }

            terrain.TryResolve(out Vector3 position, out Vector3 normal, out Vector3 push);
            DriveLaunchDustEmitter(info.terrain, terrainScale, position, normal, push, atmosphere);
            info.lastIntensity = terrainScale;
        }

        internal const string LaunchDustSkipNoEngine = "no lit engine with a surface effect";
        internal const string LaunchDustSkipSettings = "stock surface effects are off in the settings";
        internal const string LaunchDustSkipNoBody = "no body";
        internal const string LaunchDustSkipHigh = "above every surface";

        /// <summary>
        /// The raycasts: each lit engine with a <c>ModuleSurfaceFX</c> casts from its ghost thrust
        /// transform along its forward axis on stock's layers for its own reach, the result goes
        /// through stock's classification (<see cref="GhostPlaybackLogic.ClassifySurfaceFxRay"/>)
        /// into the terrain or water aggregate, or counts as a pad hit. Returns null when it traced,
        /// else why it did not (a constant, so nothing is allocated).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string TraceLaunchDustSources(
            GhostPlaybackState state, float maxReach,
            ref LaunchDustAggregate terrain, ref LaunchDustAggregate water, ref int padHits, ref int colliderId,
            ref bool atmosphere)
        {
            if (!SurfaceFxEnabledInSettings()) return LaunchDustSkipSettings;
            CelestialBody body = ResolveLaunchDustBody(state.lastInterpolatedBodyName);
            if (body == null) return LaunchDustSkipNoBody;
            atmosphere = body.atmosphere;
            Vector3d bodyPos = body.position;
            double radius = body.Radius;
            double ghostAltitude = ((Vector3d)state.ghost.transform.position - bodyPos).magnitude - radius;
            if (!GhostPlaybackLogic.ShouldProbeLaunchDust(ghostAltitude, maxReach)) return LaunchDustSkipHigh;
            bool ocean = body.ocean;

            foreach (var kvp in state.engineInfos)
            {
                EngineGhostInfo info = kvp.Value;
                GhostSurfaceFxSource src = info?.surfaceFx;
                if (src == null || !(info.currentPower > 0f) || src.thrustTransform == null) continue;
                float thrust = GhostPlaybackLogic.ComputeSurfaceFxThrustScale(
                    info.currentPower, info.usesThrustFraction, info.thrustFraction, src.fxMax);
                if (thrust <= 0f) continue;

                Transform trf = src.thrustTransform;
                Vector3 pos = trf.position;
                Vector3 fwd = trf.forward;
                Vector3d radial = (Vector3d)pos - bodyPos;
                double engineAltitude = radial.magnitude - radius;
                if (ocean && !(engineAltitude > 0.0)) continue;

                bool hit = Physics.Raycast(pos, fwd, out RaycastHit rh, src.maxDistance,
                    GhostPlaybackLogic.LaunchDustSurfaceLayerMask);
                Collider c = hit ? rh.collider : null;
                double hitAltitude = hit ? ((Vector3d)rh.point - bodyPos).magnitude - radius : double.NaN;
                Vector3 up = (Vector3)radial.normalized;
                LaunchDustSurface kind = GhostPlaybackLogic.ClassifySurfaceFxRay(
                    hit, hitAltitude, hit ? rh.distance : float.NaN,
                    c != null && c.CompareTag(GhostPlaybackLogic.LaunchpadFxTag),
                    c != null && c.CompareTag(GhostPlaybackLogic.WheelPistonColliderTag),
                    ocean, engineAltitude, Vector3.Dot(fwd, -up), src.maxDistance, out float distance);
                if (kind == LaunchDustSurface.None) continue;
                if (kind == LaunchDustSurface.Launchpad)
                {
                    padHits++;
                    continue;
                }

                float scaled = GhostPlaybackLogic.ComputeSurfaceFxScaled(thrust, distance, src.maxDistance, src.falloff);
                Vector3 point = kind == LaunchDustSurface.Water ? pos + fwd * distance : rh.point;
                Vector3 normal = kind == LaunchDustSurface.Water ? up : rh.normal;
                Vector3 flow = GhostPlaybackLogic.ComputeSurfaceFxFlow(point, pos, normal, thrust);
                if (kind == LaunchDustSurface.Water) water.Add(point, normal, flow, scaled);
                else terrain.Add(point, normal, flow, scaled);
                if (colliderId == 0 && c != null) colliderId = c.GetInstanceID();
            }
            return null;
        }

        // Stock skips every surface effect when the player turned them off (GameSettings.SURFACE_FX).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SurfaceFxEnabledInSettings()
        {
            try { return GameSettings.SURFACE_FX; }
            catch (Exception) { return true; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static CelestialBody ResolveLaunchDustBody(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName)) return null;
            try { return FlightGlobals.GetBodyByName(bodyName); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Stock's <c>SurfaceFX.LateUpdate</c> on a ghost emitter (decompiled, KSP 1.12.5): the effect
        /// stands at the combined hit point, its up the surface normal and its forward the push
        /// (<c>LookRotation(Vsrf, upAxis)</c>); the dust and wake always play and the cloud only
        /// where the body has an atmosphere; every drawn system keeps its own colour with start
        /// alpha <c>Lerp(0, 0.5, fxScale)</c> and its emission rate untouched, which is what makes
        /// stock's dust a faint haze that thins as the engine climbs; the dust's and cloud's live
        /// particles are pushed along the surface by <c>Vsrf * (remaining / lifetime)^0.5</c> each
        /// frame. The built fallback disc is lifted a little so its billboards are not half buried.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DriveLaunchDustEmitter(
            LaunchDustEmitter e, float fxScale, Vector3 position, Vector3 normal, Vector3 push, bool atmosphere)
        {
            if (e?.root == null || e.dust == null) return;
            if (normal.sqrMagnitude < 1e-8f) normal = Vector3.up;
            normal.Normalize();
            Vector3 forward = GhostPlaybackLogic.ResolveSurfaceFxForward(push, normal);
            Vector3 at = e.stockPrefab ? position : position + normal * GhostPlaybackLogic.LaunchDustGroundLiftMeters;
            e.root.transform.SetPositionAndRotation(at, Quaternion.LookRotation(forward, normal));

            float alpha = GhostPlaybackLogic.ComputeSurfaceFxAlpha(fxScale);
            bool cloud = atmosphere && e.cloud != null;
            if (cloud) DriveSurfaceFxSystem(e.cloud, e.cloudColor, alpha, push, e.buffer);
            else if (e.cloud != null && e.cloud.isEmitting) e.cloud.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            DriveSurfaceFxSystem(e.dust, e.dustColor, alpha, push, e.buffer);
            if (e.wake != null) DriveSurfaceFxSystem(e.wake, e.wakeColor, alpha, Vector3.zero, null);
            e.fxScale = fxScale;
            e.emitting = true;
        }

        private static void DriveSurfaceFxSystem(
            ParticleSystem ps, Color rgb, float alpha, Vector3 push, ParticleSystem.Particle[] buffer)
        {
            if (!ps.isPlaying) ps.Play(false);
            var main = ps.main;
            main.startColor = new Color(rgb.r, rgb.g, rgb.b, alpha);
            if (buffer == null || push.sqrMagnitude <= 0f) return;
            float lifetime = main.startLifetime.constant;
            int n = ps.GetParticles(buffer);
            for (int i = 0; i < n; i++)
                buffer[i].velocity += push * GhostPlaybackLogic.ComputeSurfaceFxPushWeight(buffer[i].remainingLifetime, lifetime);
            ps.SetParticles(buffer, n);
        }

        /// <summary>
        /// One Verbose line when what the dust does changes (the surfaces drawn, the first collider
        /// hit, the pad hits, each strength in tenths, or the reason it is skipped), and nothing on
        /// the frames between: the message is built only on a change, so a steady frame allocates
        /// nothing.
        /// </summary>
        private static void LogLaunchDustOnChange(
            int recIdx, GhostPlaybackState state, int surfaceBits, int colliderId, int padHits,
            float terrainScale, float waterScale, string skipReason)
        {
            int signature = GhostPlaybackLogic.ComputeLaunchDustLogSignature(
                surfaceBits | (Math.Min(padHits, 7) << 3), colliderId, terrainScale, waterScale);
            if (skipReason != null) signature = unchecked(signature * 31 + skipReason.Length);
            if (signature == state.launchDustLogSignature) return;
            state.launchDustLogSignature = signature;
            if (!ParsekLog.IsVerboseEnabled) return;
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.VerboseRateLimited("GhostVisual", $"launch-dust-{recIdx}",
                skipReason != null
                    ? $"Launch dust ghost #{recIdx} \"{state.vesselName}\": none ({skipReason})"
                    : $"Launch dust ghost #{recIdx} \"{state.vesselName}\" (stock ModuleSurfaceFX rule): " +
                      $"terrain={terrainScale.ToString("F3", ic)} water={waterScale.ToString("F3", ic)} " +
                      $"padHits={padHits.ToString(ic)} collider={colliderId.ToString(ic)}" +
                      (surfaceBits == 0 ? " (no surface within reach)"
                          : (surfaceBits & 1) == 0 ? " (no dust: stock's rule)" : ""),
                0.5);
        }

        /// <summary>
        /// Drives the dust to nothing. The memo-free managed part first: a ghost with no emitter
        /// built returns before any Unity call, so the headless gates never reach one.
        /// </summary>
        private static void DriveLaunchDustToZero(GhostPlaybackState state)
        {
            LaunchDustInfo info = state?.launchDustInfo;
            if (info == null) return;
            info.lastIntensity = 0f;
            if (info.terrain != null) StopLaunchDustEmitter(info.terrain);
            if (info.water != null) StopLaunchDustEmitter(info.water);
        }

        /// <summary>
        /// Stops an emitter's emission (stock disables it at zero strength); what it has emitted
        /// lives out its life. The early-out is the emitter's own flag, which every stop and the
        /// loop-cycle clear reset.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StopLaunchDustEmitter(LaunchDustEmitter e)
        {
            e.fxScale = 0f;
            if (!e.emitting) return;
            e.emitting = false;
            if (e.cloud != null) e.cloud.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            if (e.dust != null) e.dust.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            if (e.wake != null) e.wake.Stop(false, ParticleSystemStopBehavior.StopEmitting);
        }

        #endregion
    }
}
