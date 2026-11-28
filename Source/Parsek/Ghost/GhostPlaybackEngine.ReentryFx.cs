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
        /// Update reentry visual effects for a ghost. Computes atmospheric density, Mach number
        /// and stock's <c>FxScalar</c> / thermal blend for the ghost's own conditions, then drives
        /// the glow and char layers and offers the ghost's sheath to
        /// <see cref="GhostAeroFxRenderer"/> (part parity P9 Y1).
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
            // Every driven ghost, burning or not, is kept out of stock's own fire (G.7 masking).
            GhostAeroFxRenderer.NoteGhost(state.ghost);
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
            // gate can fire. `ReentryPotentialSpeedFloor` (the lowest speed at which
            // stock's FxScalar can rise above 0 in the densest stock surface air, Eve's,
            // less a margin) is shared with `HasReentryPotential` and gates out pad
            // launches where the ghost starts at 0 m/s: without it, every ghost whose
            // first playback point is already in atmosphere would build on frame 1.
            //
            // The recorded velocity is surface-relative down here (below the body's
            // inverse-rotation threshold), so it must not have the body's rotating-frame
            // velocity subtracted again; the position-derived surface velocity is used
            // instead, and an unknown one reads as 0 m/s (no FX invented).
            Vector3 surfaceVel = GhostSurfaceVelocity.Usable(
                state.lastInterpolatedSurfaceVelocity, state.lastInterpolatedSurfaceVelocitySource);
            // Stock reads vessel.speed (its last 10 physics frames' mean velocity); the chord is
            // the bracket's mean, so the recorded speeds give stock's reading when they are
            // surface speeds (the direction stays the chord's).
            float speed = GhostSurfaceVelocity.SpeedForAeroFx(state.lastInterpolatedSurfaceVelocity,
                state.lastInterpolatedSurfaceVelocitySource, state.lastInterpolatedStockSurfaceSpeed,
                state.lastInterpolatedStockSpeedChord, out bool stockSpeed);

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
                    // In atmosphere but slower than stock's effect can start anywhere.
                    // Return: info is null, nothing to drive. Next frame will re-check.
                    return;
                }
                TryPerformLazyReentryBuild(recIdx, state, vesselName, bodyName, altitude);
                info = state.reentryFxInfo;
                if (info == null) return;
            }

            double pressure = body.GetPressure(altitude);
            // Stock's FlightIntegrator: GetFullTemperature(altitude, the sun and latitude offset
            // GetAtmoThermalStats gives at the vessel), not the standard atmosphere's.
            double temperatureOffset = ReadAtmosphereTemperatureOffset(body, state.ghost, altitude);
            double temperature = body.GetFullTemperature(altitude, temperatureOffset);
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
            // Stock's scalar on the ghost's own density, speed and Mach (G.7 "Y1 design"); the
            // density is stock's, from the pressure and the full temperature read above.
            float rawIntensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed, (float)density, machNumber, GhostAeroFxRenderer.Constants, out float aeroState);
            info.aeroState = aeroState;
            info.lastInputs = new AeroFxInputs
            {
                Speed = speed,
                ChordSpeed = surfaceVel.magnitude,
                StockSpeed = stockSpeed,
                Density = density,
                Temperature = temperature,
                TemperatureOffset = temperatureOffset,
                Mach = machNumber,
                RawFx = rawIntensity,
            };

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

            DriveLaunchDustFrame(recIdx, state, enginePower, state.lastInterpolatedAltitude,
                state.launchDustGroundRefAltitude, ref frameLazyReentryBuildCount);
        }

        /// <summary>
        /// The launch dust frame body once the ghost has engine power and a ground reference:
        /// the intensity gate (<paramref name="altitude"/> above
        /// <paramref name="groundRefAltitude"/>), the lazy build under the shared per-frame build
        /// cap (<paramref name="lazyBuildCount"/>, the engine's own counter in production), and the
        /// emission and size drive. <see cref="UpdateLaunchDust"/> passes the ghost's interpolated
        /// altitude and its latched reference; the part parity dust cases pass a held slot's case
        /// altitude and terrain altitude. A ghost whose <c>launchDustInfo</c> is still null builds
        /// only with <c>launchDustPendingBuild</c> set; a build that returns null latches the
        /// ghost's reference to "never" as the per-frame path does.
        /// </summary>
        internal static void DriveLaunchDustFrame(
            int recIdx, GhostPlaybackState state, float enginePower,
            double altitude, double groundRefAltitude, ref int lazyBuildCount)
        {
            if (!GhostPlaybackLogic.TryComputeLaunchDustIntensity(
                    enginePower, altitude, groundRefAltitude, out float intensity))
            {
                DriveLaunchDustToZero(state);
                return;
            }

            // Stock's surface rule first (decompiled ModuleSurfaceFX.Update): the pad's own FX
            // collider raises the pad's effect and no dust, a wheel piston nothing.
            LaunchDustSurface surface = ProbeLaunchDustSurface(state, altitude, groundRefAltitude,
                out Vector3 ground, out Vector3 up, out int colliderId);
            if (!GhostPlaybackLogic.LaunchDustSurfaceRaisesDust(surface))
            {
                LogLaunchDustOnChange(recIdx, state, surface, colliderId, 0f, enginePower, altitude, groundRefAltitude);
                DriveLaunchDustToZero(state);
                return;
            }

            if (state.launchDustInfo == null)
            {
                if (!state.launchDustPendingBuild) return;
                if (lazyBuildCount >= GhostPlayback.MaxLazyReentryBuildsPerFrame)
                {
                    ParsekLog.VerboseRateLimited("GhostVisual", $"launch-dust-throttle-{recIdx}",
                        $"Launch dust build throttled for ghost #{recIdx}; retrying next frame " +
                        $"(used {lazyBuildCount}/{GhostPlayback.MaxLazyReentryBuildsPerFrame})", 1.0);
                    return;   // pending flag stays set: retry while the gate is still open
                }
                lazyBuildCount++;
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

            PlaceLaunchDustOnGround(state, ground, up);

            var emission = ps.emission;
            emission.rateOverTimeMultiplier = GhostPlaybackLogic.LaunchDustEmissionMax * intensity;
            var main = ps.main;
            main.startSizeMultiplier = Mathf.Lerp(
                GhostPlaybackLogic.LaunchDustSizeMin, GhostPlaybackLogic.LaunchDustSizeMax, intensity);
            if (!ps.isPlaying) ps.Play();
            state.launchDustInfo.lastIntensity = intensity;

            LogLaunchDustOnChange(recIdx, state, surface, colliderId, intensity, enginePower, altitude, groundRefAltitude);
        }

        /// <summary>
        /// One Verbose line when what the dust does changes (the surface, the collider hit, the
        /// intensity in tenths), and nothing on the frames between: the message is only built on
        /// a change, so a steady frame allocates nothing.
        /// </summary>
        private static void LogLaunchDustOnChange(
            int recIdx, GhostPlaybackState state, LaunchDustSurface surface, int colliderId,
            float intensity, float enginePower, double altitude, double groundRefAltitude)
        {
            int signature = GhostPlaybackLogic.ComputeLaunchDustLogSignature(surface, colliderId, intensity);
            if (signature == state.launchDustLogSignature) return;
            state.launchDustLogSignature = signature;
            if (!ParsekLog.IsVerboseEnabled) return;
            ParsekLog.VerboseRateLimited("GhostVisual", $"launch-dust-{recIdx}",
                $"Launch dust ghost #{recIdx} \"{state.vesselName}\" surface={surface} " +
                $"collider={colliderId.ToString(CultureInfo.InvariantCulture)} " +
                $"intensity={intensity.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"power={enginePower.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"alt={altitude.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"groundRef={groundRefAltitude.ToString("F1", CultureInfo.InvariantCulture)}" +
                (GhostPlaybackLogic.LaunchDustSurfaceRaisesDust(surface) ? "" : " (no dust: stock's rule)"),
                0.5);
        }

        /// <summary>
        /// Finds the surface under the ghost the way stock's <c>ModuleSurfaceFX</c> does: a raycast
        /// on its layers (<c>0x40008000</c>), the hit classified by its collider's tag
        /// (<see cref="GhostPlaybackLogic.ClassifyLaunchDustHit"/>). The ground point is the hit
        /// lifted by <see cref="GhostPlaybackLogic.LaunchDustGroundLiftMeters"/>, else the latched
        /// terrain reference (a terrain surface). The normal comes from the ghost's body; without
        /// one the ghost's own up axis stands in. No ghost: terrain at the reference.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static LaunchDustSurface ProbeLaunchDustSurface(
            GhostPlaybackState state, double altitude, double groundRefAltitude,
            out Vector3 ground, out Vector3 up, out int colliderId)
        {
            ground = Vector3.zero;
            up = Vector3.up;
            colliderId = 0;
            if (state.ghost == null) return LaunchDustSurface.Terrain;

            Vector3 ghostPos = state.ghost.transform.position;
            if (!TryResolveLaunchDustSurfaceUp(state.lastInterpolatedBodyName, ghostPos, out up))
                up = state.ghost.transform.up;

            double agl = altitude - groundRefAltitude;
            float reach = (float)Math.Min(GhostPlaybackLogic.LaunchDustMaxAglMeters,
                double.IsNaN(agl) || agl < 0.0 ? 0.0 : agl) + 10f;
            if (Physics.Raycast(ghostPos + up, -up, out RaycastHit hit, reach + 1f,
                    GhostPlaybackLogic.LaunchDustSurfaceLayerMask))
            {
                Collider c = hit.collider;
                colliderId = c != null ? c.GetInstanceID() : 0;
                ground = hit.point + up * GhostPlaybackLogic.LaunchDustGroundLiftMeters;
                return GhostPlaybackLogic.ClassifyLaunchDustHit(
                    c != null && c.CompareTag(GhostPlaybackLogic.LaunchpadFxTag),
                    c != null && c.CompareTag(GhostPlaybackLogic.WheelPistonColliderTag));
            }
            ground = GhostPlaybackLogic.ComputeLaunchDustGroundPoint(ghostPos, up, altitude, groundRefAltitude);
            return LaunchDustSurface.Terrain;
        }

        /// <summary>
        /// Lays the dust emitter flat on the ground under the ghost. The emitter is a Circle shape,
        /// which emits in its local XY plane, so its local Z is turned onto the surface normal: the
        /// disc lies on the ground and its particles run outward across it, as stock's ground dust
        /// does. Re-placed every frame because the ghost climbs away from the spot while the dust
        /// stays on the ground.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PlaceLaunchDustOnGround(GhostPlaybackState state, Vector3 ground, Vector3 up)
        {
            GameObject dust = state.launchDustInfo?.dustObject;
            if (dust == null || state.ghost == null) return;
            dust.transform.SetPositionAndRotation(ground, Quaternion.FromToRotation(Vector3.forward, up));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryResolveLaunchDustSurfaceUp(
            string bodyName, Vector3 worldPosition, out Vector3 up)
        {
            up = Vector3.up;
            if (string.IsNullOrEmpty(bodyName)) return false;
            CelestialBody body;
            try { body = FlightGlobals.GetBodyByName(bodyName); }
            catch (Exception) { return false; }
            if (body == null) return false;
            Vector3 radial = worldPosition - (Vector3)body.position;
            if (radial.sqrMagnitude < 1e-6f) return false;
            up = radial.normalized;
            return true;
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
                state.ghost, state.heatInfos, recIdx, vesselName,
                GhostPlaybackLogic.CollectGhostOwnedMaterials(state));
            state.reentryFxInfo = built;
            GhostPlaybackLogic.InvalidateLightLensAndFlareWrites(state);
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
            info.aeroState = 0f;
            DriveReentryLayers(info, 0f, Vector3.zero, recIdx, bodyName, altitude, 0f, vesselName, state);
            info.lastIntensity = 0f;
        }

        // Per burning ghost per frame, under the ghost's cached key, with no capture: a
        // suppressed line allocates nothing.
        private struct ReentryIntensityLogState
        {
            public int recIdx;
            public float intensity, aeroState;
            public double altitude;
            public AeroFxInputs inputs;
        }

        private static readonly Func<ReentryIntensityLogState, string> FormatReentryIntensity = st =>
            $"ReentryFx: ghost #{st.recIdx} intensity={st.intensity:F2} state={st.aeroState:F2} alt={st.altitude:F0} "
                + GhostAeroFx.FormatInputs(st.inputs);

        private static void LogReentryIntensity(ReentryFxInfo info, int recIdx, float intensity,
            double altitude)
        {
            string key = GhostAeroFxLogKeys.CachedLogKey(ref info.intensityLogKey,
                ref info.intensityLogKeyIdx, recIdx, "Engine", "ghost-", "-intensity");
            ParsekLog.VerboseRateLimitedComposed("Engine", key, new ReentryIntensityLogState
            {
                recIdx = recIdx, intensity = intensity, aeroState = info.aeroState,
                altitude = altitude, inputs = info.lastInputs,
            }, FormatReentryIntensity);
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
                    $"ReentryFx: Activated for ghost #{recIdx} \"{vesselName}\" - intensity={intensity:F2}, state={info.aeroState:F2}, Mach={machNumber:F2}, speed={speed:F0} m/s, alt={altitude:F0} m, body={bodyName}");
            }
            else if (!isActive && wasActive)
            {
                float speed = surfaceVel.magnitude;
                ParsekLog.Verbose("Engine",
                    $"ReentryFx: Deactivated for ghost #{recIdx} — intensity dropped to 0 (speed={speed:F0} m/s, alt={altitude:F0} m)");
            }

            if (isActive)
            {
                if (ParsekLog.IsVerboseEnabled)
                    LogReentryIntensity(info, recIdx, intensity, altitude);
            }

            // Test-only isolation frame; None (and nothing hidden) unless a test armed it.
            ReentryIsolation isolation = ConsumeReentryIsolation(info, recIdx);
            bool isolateFire = ReentryIsolationHidesFireAndShell(isolation);
            if (isolateFire != info.fireHiddenForIsolation)
                SetReentryFireShownForIsolation(info, !isolateFire, recIdx);
            // The glow and char read the heat share of stock's effect: dark under the
            // condensation of a fast pass in dense air, as stock's hull stays.
            float heat = GhostAeroFx.ThermalShare(intensity, info.aeroState);
            float glowIntensity = ReentryIsolationGlowIntensity(heat, isolation);

            // Layer A: Heat glow (material emission). The material writes are Unity calls, so they
            // sit in a helper the JIT cannot inline back into this body, called only when there is
            // a material to write (the JIT compiles the helper, and whatever it inlines, on its
            // first call).
            if (info.glowMaterials != null && info.glowMaterials.Count > 0)
                ApplyReentryGlowLayer(info, glowIntensity);

            // Apply ablation char to heat shield parts
            if (state != null)
                GhostPlaybackLogic.ApplyColorChangerCharState(state, heat);

            // The sheath: stock's FX shaders on this ghost's meshes, drawn by the renderer this
            // frame unless stock's own effect is on, the ghost is out of its reach or an
            // isolation frame leaves it out.
            GhostAeroFxRenderer.OfferSheath(info, state?.ghost, intensity, info.aeroState,
                surfaceVel, recIdx, isolateFire);
        }

        /// <summary>
        /// Layer A: drives each glow material from cold to hot by <paramref name="glowIntensity"/>.
        /// Not inlined: <c>Material.SetColor</c> and the material null checks reach Unity
        /// internalcalls, and a Release JIT that inlined them into <c>DriveReentryLayers</c> would
        /// make that method fail to compile on the Windows .NET test host (SecurityException).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyReentryGlowLayer(ReentryFxInfo info, float glowIntensity)
        {
            if (info.glowMaterials != null)
            {
                for (int i = 0; i < info.glowMaterials.Count; i++)
                {
                    HeatMaterialState ms = info.glowMaterials[i];
                    if (ms.material == null) continue;

                    if (glowIntensity <= GhostVisualBuilder.ReentryLayerAThreshold)
                    {
                        if (!string.IsNullOrEmpty(ms.emissiveProperty))
                            ms.material.SetColor(ms.emissiveProperty, ms.coldEmission);
                        if (!string.IsNullOrEmpty(ms.colorProperty))
                            ms.material.SetColor(ms.colorProperty, ms.coldColor);
                    }
                    else
                    {
                        float glowFraction = Mathf.InverseLerp(GhostVisualBuilder.ReentryLayerAThreshold, 1f, glowIntensity);
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
        }

        /// <summary>
        /// Stock's sun and latitude temperature offset at a ghost, as
        /// <c>FlightIntegrator.CalculateSunBodyFlux</c> takes it for the active vessel: the
        /// normalized scaled-space vector to the sun, the up axis at the position and
        /// <c>CelestialBody.GetAtmoThermalStats</c> (body flux not needed). 0 when there is no
        /// sun or the body is the sun. Not inlined: every read here is a live game call.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadAtmosphereTemperatureOffset(CelestialBody body, GameObject ghost, double altitude)
        {
            try
            {
                if (ghost == null) return 0.0;
                Vector3d worldPosition = ghost.transform.position;
                CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
                if (sun == null || ReferenceEquals(sun, body) || sun.scaledBody == null) return 0.0;
                Vector3d scaledPosition = ScaledSpace.LocalToScaledSpace(worldPosition);
                Vector3d toSun = (Vector3d)sun.scaledBody.transform.position - scaledPosition;
                double distance = toSun.magnitude;
                if (distance == 0.0) distance = 1.0;
                Vector3d sunVector = toSun / distance;
                Vector3d up = FlightGlobals.getUpAxis(body, worldPosition);
                double sunDot = Vector3d.Dot(sunVector, up);
                body.GetAtmoThermalStats(false, sun, sunVector, sunDot, up, altitude,
                    out double offset, out double _, out double _);
                return double.IsNaN(offset) || double.IsInfinity(offset) ? 0.0 : offset;
            }
            catch (Exception e)
            {
                ParsekLog.WarnRateLimited("ReentryFx", "temp-offset-failed",
                    $"Ghost reentry temperature offset unavailable, standard atmosphere used: {e.GetType().Name}: {e.Message}", 30.0);
                return 0.0;
            }
        }

        /// <summary>
        /// Test-only: the next reentry drive of this ghost (one frame) offers no sheath (so no
        /// sheath and no ghost light that frame), or with
        /// <see cref="ReentryIsolation.FireShellAndGlow"/> also drives Layer A cold; the drive
        /// after it offers them again. This is the ghost half of the FXC isolation frames
        /// (design-part-ghost-parity.md G.2.3). Returns false (nothing armed) for a ghost with no
        /// reentry FX built yet.
        /// </summary>
        internal static bool ArmReentryIsolationForOneFrame(GhostPlaybackState state, ReentryIsolation mode)
        {
            ReentryFxInfo info = state?.reentryFxInfo;
            if (info == null)
            {
                ParsekLog.Verbose("ReentryFx",
                    $"Reentry isolation not armed for \"{state?.vesselName ?? "?"}\": no reentry FX built");
                return false;
            }
            info.isolationArmed = mode;
            ParsekLog.Verbose("ReentryFx",
                $"Reentry isolation armed for one frame: \"{state.vesselName}\" mode={mode}");
            return true;
        }

        /// <summary>The armed isolation mode, cleared so it applies to one drive only.</summary>
        internal static ReentryIsolation ConsumeReentryIsolation(ReentryFxInfo info, int recIdx)
        {
            if (info == null) return ReentryIsolation.None;
            ReentryIsolation mode = info.isolationArmed;
            if (mode == ReentryIsolation.None) return mode;
            info.isolationArmed = ReentryIsolation.None;
            // The rig arms this on its shot frames: the ghost's scalar inputs ride along, to read
            // beside stock's values at the same mark.
            ParsekLog.Verbose("ReentryFx",
                $"Reentry isolation frame applied to ghost #{recIdx}: mode={mode} fx={info.lastIntensity.ToString("F3", CultureInfo.InvariantCulture)} "
                + $"state={info.aeroState.ToString("F3", CultureInfo.InvariantCulture)} " + GhostAeroFx.FormatInputs(info.lastInputs));
            return mode;
        }

        internal static bool ReentryIsolationHidesFireAndShell(ReentryIsolation mode)
        {
            return mode == ReentryIsolation.FireAndShell || mode == ReentryIsolation.FireShellAndGlow;
        }

        /// <summary>The intensity Layer A reads: 0 (cold) under a glow isolation frame, else the drive's.</summary>
        internal static float ReentryIsolationGlowIntensity(float intensity, ReentryIsolation mode)
        {
            return mode == ReentryIsolation.FireShellAndGlow ? 0f : intensity;
        }

        private static void SetReentryFireShownForIsolation(ReentryFxInfo info, bool shown, int recIdx)
        {
            info.fireHiddenForIsolation = !shown;
            ParsekLog.Verbose("ReentryFx",
                $"Reentry sheath {(shown ? "restored" : "hidden")} for isolation on ghost #{recIdx}");
        }

        #endregion
    }
}
