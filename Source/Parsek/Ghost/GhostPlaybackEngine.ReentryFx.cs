using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // Reentry and launch-dust FX: per-frame drive, lazy reentry build and the zero-drive paths.
    internal partial class GhostPlaybackEngine
    {
        #region Reentry FX

        /// <summary>
        /// Update reentry visual effects for a ghost. Computes atmospheric density,
        /// Mach number, and intensity, then drives glow/fire/shell layers.
        /// </summary>
        /// <remarks>
        /// Bug #450 B3: entry condition widened to allow ghosts whose reentry build
        /// was deferred at spawn (<c>state.reentryFxPendingBuild == true</c>) to
        /// reach the body/atmosphere lookup below. The lazy build fires inside this
        /// method after the body is resolved so there is no duplicate
        /// <c>FlightGlobals.Bodies.Find</c> on the hot path. Drive-to-zero paths
        /// short-circuit to a plain return when <c>reentryFxInfo</c> is still null
        /// (nothing to drive, we're just waiting for in-atmosphere conditions).
        /// </remarks>
        internal void UpdateReentryFx(int recIdx, GhostPlaybackState state, string vesselName, float warpRate)
        {
            if (state == null || state.ghost == null) return;
            if (state.reentryFxInfo == null && !state.reentryFxPendingBuild) return;
            if (ShouldSuppressLazyReentryUntilPlaybackSync(state))
            {
                ParsekLog.VerboseRateLimited("ReentryFx", $"lazy-wait-sync-{recIdx}",
                    $"Lazy reentry build deferred for ghost #{recIdx} \"{vesselName}\" until first playback sync", 5.0);
                return;
            }

            var info = state.reentryFxInfo;
            if (info != null && GhostPlaybackLogic.ShouldSuppressVisualFx(warpRate))
            {
                DriveReentryToZero(info, recIdx, state.lastInterpolatedBodyName,
                    state.lastInterpolatedAltitude, vesselName);
                return;
            }

            string bodyName = state.lastInterpolatedBodyName;
            double altitude = state.lastInterpolatedAltitude;

            if (string.IsNullOrEmpty(bodyName))
            {
                if (info != null)
                    DriveReentryToZero(info, recIdx, bodyName, 0.0, vesselName, state);
                return;
            }

            // TODO(standalone): FlightGlobals.Bodies is a KSP global dependency.
            // For standalone extraction, inject a body-lookup delegate or interface
            // (e.g., Func<string, CelestialBody> bodyLookup) via the constructor.
            // CelestialBody is needed here for atmosphere physics (pressure, temperature,
            // density, speed of sound), so this cannot be easily abstracted away.
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            if (body == null)
            {
                ParsekLog.VerboseRateLimited("Engine", $"ghost-{recIdx}-nobody",
                    $"ReentryFx: body '{bodyName}' not found — skipping");
                if (info != null)
                    DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            if (!body.atmosphere)
            {
                if (info != null)
                    DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            if (altitude >= body.atmosphereDepth)
            {
                if (info != null)
                    DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            // Resolve surface velocity BEFORE the lazy-build decision so the speed
            // gate can fire. `ReentryPotentialSpeedFloor` (400 m/s ≈ Mach 1.2 at
            // sea-level Kerbin) is shared with `HasReentryPotential` and gates out
            // pad launches where the ghost starts at 0 m/s — without this gate,
            // ghosts whose first playback point is already in-atmosphere (every
            // KSC launch recording) would still pay the full 7 ms build on frame 1,
            // just attributed to mainLoop instead of spawn.
            //
            // The recorded velocity is surface-relative down here (below the body's
            // inverse-rotation threshold), so it must not have the body's rotating-frame
            // velocity subtracted again; the position-derived surface velocity is used
            // instead, and an unknown one reads as 0 m/s (no FX invented).
            Vector3 surfaceVel = GhostSurfaceVelocity.Usable(
                state.lastInterpolatedSurfaceVelocity, state.lastInterpolatedSurfaceVelocitySource);
            float speed = surfaceVel.magnitude;

            // Bug #450 B3: lazy-build seam. Reuses the body + atmosphere + altitude
            // + speed checks above. If the build is throttled or the gate fails,
            // return (for null info) — next frame will retry while still in
            // atmosphere. Flag cleared only when the build actually fires, see
            // TryPerformLazyReentryBuild.
            //
            // Under warp-suppression the FX subsystem would drive-to-zero anyway
            // (see GhostPlaybackLogic.ShouldSuppressVisualFx); doing a ~7 ms build
            // of FX we immediately hide contradicts the suppression policy. Defer
            // one more frame past the warp drop-out — the ghost hasn't moved
            // meaningfully during warp anyway.
            if (state.reentryFxPendingBuild)
            {
                if (GhostPlaybackLogic.ShouldSuppressVisualFx(warpRate))
                    return;
                if (!GhostPlaybackLogic.ShouldBuildLazyReentryFx(
                        state.reentryFxPendingBuild, bodyName, body.atmosphere,
                        altitude, body.atmosphereDepth,
                        speed, TrajectoryMath.ReentryPotentialSpeedFloor))
                {
                    // In-atmosphere but still too slow for FX to be imminent
                    // (typical KSC launch below ~Mach 1.2). Return — info is null,
                    // nothing to drive. Next frame will re-check.
                    return;
                }
                TryPerformLazyReentryBuild(recIdx, state, vesselName, bodyName, altitude);
                info = state.reentryFxInfo;
                if (info == null) return;
            }

            double pressure = body.GetPressure(altitude);
            double temperature = body.GetTemperature(altitude);
            if (double.IsNaN(pressure) || pressure < 0 || double.IsNaN(temperature) || temperature < 0)
            {
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            double density = body.GetDensity(pressure, temperature);
            if (double.IsNaN(density) || density < 0)
            {
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            double speedOfSound = body.GetSpeedOfSound(pressure, density);
            float machNumber = (speedOfSound > 0) ? (float)(speed / speedOfSound) : 0f;
            float rawIntensity = GhostVisualBuilder.ComputeReentryIntensity(speed, (float)density, machNumber);

            float smoothedIntensity = Mathf.Lerp(info.lastIntensity, rawIntensity,
                1f - Mathf.Exp(-GhostVisualBuilder.ReentrySmoothingRate * Time.deltaTime));
            if (smoothedIntensity < 0.001f && rawIntensity == 0f)
                smoothedIntensity = 0f;

            DriveReentryLayers(info, smoothedIntensity, surfaceVel, recIdx, bodyName, altitude, machNumber, vesselName, state);
            info.lastIntensity = smoothedIntensity;
        }

        internal static bool ShouldSuppressLazyReentryUntilPlaybackSync(GhostPlaybackState state)
        {
            return state != null
                && state.reentryFxPendingBuild
                && state.reentryFxInfo == null
                && state.deferVisibilityUntilPlaybackSync
                && state.appearanceCount == 0;
        }

        /// <summary>
        /// S3 launch dust, driven once per visible frame. Built lazily on the first frame the gate
        /// chain opens, under the SAME per-frame build cap the lazy reentry build uses — a pad full
        /// of simultaneously-launching looped ghosts must not all instantiate a particle system on
        /// one frame, which is the exact hitch #450 exists to avoid.
        ///
        /// Gate order is cheapest-first: no engine power, nothing else runs. The ground reference
        /// is latched once per ghost and NEVER invented — a recording whose points carry no
        /// recordedGroundClearance (every non-surface recording) raises no dust, permanently.
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

            float enginePower = GhostPlaybackLogic.SumEnginePower(state);
            if (enginePower <= 0f)
            {
                DriveLaunchDustToZero(state);
                return;
            }

            if (double.IsNaN(state.launchDustGroundRefAltitude) && !state.launchDustPendingBuild
                && state.launchDustInfo == null)
            {
                if (!GhostPlaybackLogic.TryLatchLaunchDustGroundReference(
                        traj?.Points, out double groundRef))
                {
                    ParsekLog.VerboseRateLimited("GhostVisual", $"launch-dust-noground-{recIdx}",
                        $"Launch dust disabled for ghost #{recIdx} \"{state.vesselName}\": no " +
                        "trajectory point carries a finite recordedGroundClearance, so there is no " +
                        "honest ground reference to measure AGL against", 60.0);
                    state.launchDustGroundRefAltitude = double.NegativeInfinity;  // latched "never"
                    return;
                }
                state.launchDustGroundRefAltitude = groundRef;
                state.launchDustPendingBuild = true;
            }

            if (double.IsInfinity(state.launchDustGroundRefAltitude))
                return;

            if (!GhostPlaybackLogic.TryComputeLaunchDustIntensity(
                    enginePower, state.lastInterpolatedAltitude,
                    state.launchDustGroundRefAltitude, out float intensity))
            {
                DriveLaunchDustToZero(state);
                return;
            }

            if (state.launchDustInfo == null)
            {
                if (!state.launchDustPendingBuild) return;
                if (frameLazyReentryBuildCount >= GhostPlayback.MaxLazyReentryBuildsPerFrame)
                {
                    ParsekLog.VerboseRateLimited("GhostVisual", $"launch-dust-throttle-{recIdx}",
                        $"Launch dust build throttled for ghost #{recIdx}; retrying next frame " +
                        $"(used {frameLazyReentryBuildCount}/{GhostPlayback.MaxLazyReentryBuildsPerFrame})", 1.0);
                    return;   // pending flag stays set — retry while the gate is still open
                }
                frameLazyReentryBuildCount++;
                state.launchDustPendingBuild = false;
                state.launchDustInfo = GhostVisualBuilder.TryBuildLaunchDust(
                    state.ghost, state.vesselName);
                if (state.launchDustInfo == null)
                {
                    state.launchDustGroundRefAltitude = double.NegativeInfinity;  // never re-try
                    return;
                }
            }

            ParticleSystem ps = state.launchDustInfo.particles;
            if (ps == null) return;

            var emission = ps.emission;
            emission.rateOverTimeMultiplier = GhostPlaybackLogic.LaunchDustEmissionMax * intensity;
            var main = ps.main;
            main.startSizeMultiplier = Mathf.Lerp(
                GhostPlaybackLogic.LaunchDustSizeMin, GhostPlaybackLogic.LaunchDustSizeMax, intensity);
            if (!ps.isPlaying) ps.Play();
            state.launchDustInfo.lastIntensity = intensity;

            ParsekLog.VerboseRateLimited("GhostVisual", $"launch-dust-{recIdx}",
                $"Launch dust ghost #{recIdx} \"{state.vesselName}\" " +
                $"intensity={intensity.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"power={enginePower.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"alt={state.lastInterpolatedAltitude.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"groundRef={state.launchDustGroundRefAltitude.ToString("F1", CultureInfo.InvariantCulture)}",
                5.0);
        }

        /// <summary>
        /// Drives the dust to nothing. The early-out is on the SYSTEM STILL PLAYING rather than on
        /// the cached lastIntensity: ResetForLoopCycle zeroes that memo without being able to touch
        /// Unity, so a memo-only guard would then refuse to stop a system that is still emitting.
        /// (In practice ReapplySpawnTimeModuleBaselinesForLoopCycle always follows and stops it,
        /// but a truth-vs-memo disagreement should not need a second method to be safe.)
        /// </summary>
        private static void DriveLaunchDustToZero(GhostPlaybackState state)
        {
            LaunchDustInfo info = state?.launchDustInfo;
            if (info?.particles == null) return;
            info.lastIntensity = 0f;
            if (!info.particles.isPlaying) return;
            var emission = info.particles.emission;
            emission.rateOverTimeMultiplier = 0f;
            info.particles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        /// <summary>
        /// Bug #450 B3: perform the deferred reentry-FX build. Called from inside
        /// <see cref="UpdateReentryFx"/> after its body/atmosphere/altitude guards have
        /// already confirmed the ghost is in atmosphere, so there is no duplicate
        /// <c>FlightGlobals.Bodies.Find</c> lookup. Respects
        /// <see cref="GhostPlayback.MaxLazyReentryBuildsPerFrame"/> to prevent a burst of simultaneous
        /// atmosphere-entries from producing the same bimodal hitch #450 is trying to
        /// eliminate.
        /// </summary>
        private void TryPerformLazyReentryBuild(
            int recIdx, GhostPlaybackState state, string vesselName,
            string bodyName, double altitude)
        {
            // Defensive idempotency: the production call site in UpdateReentryFx guards
            // on state.reentryFxPendingBuild, but this method is also exposed as a test
            // seam. A re-entrant call with the flag already cleared must be a no-op.
            if (state == null || !state.reentryFxPendingBuild) return;

            if (frameLazyReentryBuildCount >= GhostPlayback.MaxLazyReentryBuildsPerFrame)
            {
                frameLazyReentryBuildDeferred++;
                // Per-index rate-limit key so a burst of same-frame throttles does
                // not collapse into a single shared-key log line — the counter
                // captures the count, but the per-ghost identity matters for
                // post-playtest diagnosis. 1-second window still bounds volume per
                // ghost.
                ParsekLog.VerboseRateLimited("ReentryFx", $"lazy-throttle-{recIdx}",
                    $"Lazy reentry build throttled: #{recIdx} deferred to next frame " +
                    $"(used {frameLazyReentryBuildCount}/{GhostPlayback.MaxLazyReentryBuildsPerFrame})", 1.0);
                return;  // Flag stays true — retry next frame while still in atmosphere.
            }

            // Defensive: heatInfos is only nulled by ClearLoadedVisualReferences (which
            // also clears our pending flag) or by a full rebuild path. If we somehow
            // land here with a null it, clear the flag so we don't burn CPU every
            // frame on an impossible build, and let the subsystem self-heal on the
            // next rebuild.
            if (state.heatInfos == null)
            {
                state.reentryFxPendingBuild = false;
                ParsekLog.VerboseRateLimited("ReentryFx", $"lazy-noheat-{recIdx}",
                    $"Lazy reentry build skipped for #{recIdx} \"{vesselName}\" — " +
                    $"state.heatInfos is null (rebuild likely in flight); clearing flag", 5.0);
                return;
            }

            frameLazyReentryBuildCount++;
            state.reentryFxPendingBuild = false;  // One-shot: clear even if build fails.
            var built = GhostVisualBuilder.TryBuildReentryFx(
                state.ghost, state.heatInfos, recIdx, vesselName);
            state.reentryFxInfo = built;
            // Count actual builds only — the counter semantic is "FX objects produced",
            // used by the diagnostics `reentryFx built X` line. A failed TryBuildReentryFx
            // (returns null) is observable via `deferred - built > expected` and is NOT
            // a build.
            if (built != null)
            {
                DiagnosticsState.health.reentryFxBuildsThisSession++;
                ParsekLog.Verbose("ReentryFx",
                    $"Lazy reentry build fired for ghost #{recIdx} \"{vesselName}\" — " +
                    $"body={bodyName} alt={altitude:F0}m " +
                    $"(deferred at spawn, built on first atmospheric frame)");
            }
            else
            {
                ParsekLog.VerboseRateLimited("ReentryFx", $"lazy-buildnull-{recIdx}",
                    $"Lazy reentry build returned null for #{recIdx} \"{vesselName}\" — " +
                    $"body={bodyName} alt={altitude:F0}m (flag cleared, no retry)", 5.0);
            }
        }

        private void DriveReentryToZero(ReentryFxInfo info, int recIdx, string bodyName, double altitude,
            string vesselName, GhostPlaybackState state = null)
        {
            DriveReentryLayers(info, 0f, Vector3.zero, recIdx, bodyName, altitude, 0f, vesselName, state);
            info.lastIntensity = 0f;
        }

        private void DriveReentryLayers(ReentryFxInfo info, float intensity, Vector3 surfaceVel,
            int recIdx, string bodyName, double altitude, float machNumber, string vesselName,
            GhostPlaybackState state = null)
        {
            bool wasActive = info.lastIntensity > 0f;
            bool isActive = intensity > 0f;

            if (isActive && !wasActive)
            {
                float speed = surfaceVel.magnitude;
                ParsekLog.Verbose("Engine",
                    $"ReentryFx: Activated for ghost #{recIdx} \"{vesselName}\" — intensity={intensity:F2}, Mach={machNumber:F2}, speed={speed:F0} m/s, alt={altitude:F0} m, body={bodyName}");
            }
            else if (!isActive && wasActive)
            {
                float speed = surfaceVel.magnitude;
                ParsekLog.Verbose("Engine",
                    $"ReentryFx: Deactivated for ghost #{recIdx} — intensity dropped to 0 (speed={speed:F0} m/s, alt={altitude:F0} m)");
            }

            if (isActive)
            {
                ParsekLog.VerboseRateLimited("Engine", $"ghost-{recIdx}-intensity",
                    $"ReentryFx: ghost #{recIdx} intensity={intensity:F2} speed={surfaceVel.magnitude:F0} alt={altitude:F0}");
            }

            // Layer A: Heat glow (material emission)
            if (info.glowMaterials != null)
            {
                for (int i = 0; i < info.glowMaterials.Count; i++)
                {
                    HeatMaterialState ms = info.glowMaterials[i];
                    if (ms.material == null) continue;

                    if (intensity <= GhostVisualBuilder.ReentryLayerAThreshold)
                    {
                        if (!string.IsNullOrEmpty(ms.emissiveProperty))
                            ms.material.SetColor(ms.emissiveProperty, ms.coldEmission);
                        if (!string.IsNullOrEmpty(ms.colorProperty))
                            ms.material.SetColor(ms.colorProperty, ms.coldColor);
                    }
                    else
                    {
                        float glowFraction = Mathf.InverseLerp(GhostVisualBuilder.ReentryLayerAThreshold, 1f, intensity);
                        Color targetEmission = Color.Lerp(GhostVisualBuilder.ReentryHotEmissionLow,
                            GhostVisualBuilder.ReentryHotEmissionHigh, glowFraction);
                        if (!string.IsNullOrEmpty(ms.emissiveProperty))
                            ms.material.SetColor(ms.emissiveProperty,
                                Color.Lerp(ms.coldEmission, ms.coldEmission + targetEmission, glowFraction));
                        if (!string.IsNullOrEmpty(ms.colorProperty))
                            ms.material.SetColor(ms.colorProperty,
                                Color.Lerp(ms.coldColor, ms.hotColor, glowFraction));
                    }
                }
            }

            // Apply ablation char to heat shield parts
            if (state != null)
                GhostPlaybackLogic.ApplyColorChangerCharState(state, intensity);

            // Fire envelope particles. Visible density is controlled primarily by the
            // emission-rate range; maxParticles only supplies headroom for the denser stream.
            if (info.fireParticles != null)
            {
                if (intensity > GhostVisualBuilder.ReentryFireThreshold)
                {
                    float fireFraction = Mathf.InverseLerp(GhostVisualBuilder.ReentryFireThreshold, 1f, intensity);

                    var emissionMod = info.fireParticles.emission;
                    emissionMod.rateOverTimeMultiplier = Mathf.Lerp(
                        GhostVisualBuilder.ReentryFireEmissionMin,
                        GhostVisualBuilder.ReentryFireEmissionMax, fireFraction);

                    var mainMod = info.fireParticles.main;
                    mainMod.startSizeMultiplier = Mathf.Lerp(0.8f, 2.0f, fireFraction);

                    if (!info.fireParticles.isPlaying)
                        info.fireParticles.Play();
                }
                else
                {
                    if (info.fireParticles.isPlaying)
                        info.fireParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }

            // Fire shell overlay
            if (info.fireShellMeshes != null && info.fireShellMaterial != null
                && intensity > GhostVisualBuilder.ReentryFireThreshold)
            {
                Vector3 velDir = surfaceVel.normalized;
                float maxOffset = info.vesselLength * GhostVisualBuilder.ReentryFireShellMaxOffset * intensity;
                float baseTint = Mathf.Lerp(0.026f, 0.156f, intensity);

                for (int pass = 0; pass < GhostVisualBuilder.ReentryFireShellPasses; pass++)
                {
                    float t = (pass + 1f) / GhostVisualBuilder.ReentryFireShellPasses;
                    Vector3 offset = -velDir * (maxOffset * t);
                    float alpha = baseTint * (1f - t * 0.7f);
                    Color tint = GhostVisualBuilder.ReentryFireShellColor * alpha;

                    var mpb = state?.reentryMpb ?? new MaterialPropertyBlock();
                    mpb.SetColor("_TintColor", tint);

                    for (int m = 0; m < info.fireShellMeshes.Count; m++)
                    {
                        FireShellMesh fsm = info.fireShellMeshes[m];
                        if (fsm.mesh == null || fsm.transform == null) continue;
                        if (!fsm.transform.gameObject.activeInHierarchy) continue;

                        Matrix4x4 matrix = Matrix4x4.Translate(offset) * fsm.transform.localToWorldMatrix;
                        Graphics.DrawMesh(fsm.mesh, matrix, info.fireShellMaterial, 0, null, 0, mpb);
                    }
                }
            }
        }

        #endregion
    }
}
