using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // Parachute canopy airflow aim: an open ghost canopy points the way stock points the real one.
    internal static partial class GhostPlaybackLogic
    {
        #region Canopy airflow aim

        /// <summary>
        /// Below this surface speed (m/s) the ghost's velocity says nothing about the airflow
        /// (a hold, a landed ghost, a sample at rest), so the aim falls back to gravity.
        /// </summary>
        internal const float MinCanopyAimAirspeedMps = 0.5f;

        /// <summary>Stock's cap on a symmetry group's canopy spread (ModuleParachute.FixedUpdate).</summary>
        internal const float MaxCanopySpreadDeg = 45f;

        internal enum CanopyAimStep
        {
            /// <summary>Set the aim outright (first aim of a stage, a scrub, a long gap).</summary>
            Snap,
            /// <summary>Same UT as the last aim: re-apply the last world rotation.</summary>
            Hold,
            /// <summary>Turn toward the aim at the module's rate (stock's RotateTowards).</summary>
            Step
        }

        /// <summary>
        /// Stock's <c>ModuleParachute.symmetryCount</c> from the number of symmetry counterparts
        /// (the snapshot PART node's <c>sym</c> values): counterparts + 1 when there are any, else 0.
        /// </summary>
        internal static int StockParachuteSymmetryCount(int counterparts)
            => counterparts > 0 ? counterparts + 1 : 0;

        /// <summary>
        /// The spread tilt stock adds for a symmetry group (rotation about the canopy's own X):
        /// <c>Clamp(spreadAngle * symmetryCount, 0, 45)</c>, a third of it while semi-deployed.
        /// Stock eases the full-deploy third away over the deploy clip; the ghost poses the
        /// stage's end, so it takes the stage's steady value. Zero without symmetry or spread.
        /// </summary>
        internal static float ResolveCanopySpreadDeg(float spreadAngleDeg, int symmetryCount, CanopyPoseStage stage)
        {
            if (symmetryCount <= 0 || !(spreadAngleDeg > 0f) || float.IsInfinity(spreadAngleDeg))
                return 0f;
            if (stage != CanopyPoseStage.SemiDeployed && stage != CanopyPoseStage.Deployed)
                return 0f;
            float spread = Mathf.Clamp(spreadAngleDeg * symmetryCount, 0f, MaxCanopySpreadDeg);
            return stage == CanopyPoseStage.SemiDeployed ? spread / 3f : spread;
        }

        /// <summary>
        /// The direction the canopy's forward axis points, per stock
        /// (<c>ModuleParachute.FixedUpdate</c>: <c>LookRotation((invertCanopy ? -1 : 1) * dragVectorDir, up)</c>,
        /// where <c>dragVectorDir</c> is the part's air-relative velocity direction). The ghost's
        /// stand-in for <c>dragVectorDir</c>, in order: its own surface-relative velocity when known
        /// and at least <see cref="MinCanopyAimAirspeedMps"/>; else straight down the body's gravity
        /// (a chute falling vertically); else along the part's own -Y (an upright falling part).
        /// All vectors are world-space. Never returns a zero vector for unit inputs.
        /// </summary>
        internal static CanopyAimSource ResolveCanopyAimForward(
            Vector3 surfaceVelocity, bool surfaceVelocityKnown,
            Vector3 gravityUp, bool gravityUpKnown,
            Vector3 partUp, bool invertCanopy,
            out Vector3 aimForward)
        {
            float sign = invertCanopy ? -1f : 1f;
            if (surfaceVelocityKnown && IsFinite(surfaceVelocity))
            {
                float speed = Magnitude(surfaceVelocity);
                if (speed >= MinCanopyAimAirspeedMps)
                {
                    aimForward = surfaceVelocity * (sign / speed);
                    return CanopyAimSource.Airflow;
                }
            }
            if (gravityUpKnown && IsFinite(gravityUp))
            {
                float g = Magnitude(gravityUp);
                if (g > 1e-6f)
                {
                    // Falling straight down: dragVectorDir = -up.
                    aimForward = gravityUp * (-sign / g);
                    return CanopyAimSource.Gravity;
                }
            }
            float p = Magnitude(partUp);
            Vector3 up = p > 1e-6f && IsFinite(partUp) ? partUp * (1f / p) : new Vector3(0f, 1f, 0f);
            aimForward = up * -sign;
            return CanopyAimSource.PartUp;
        }

        /// <summary>
        /// Stock's LookRotation up hint: the part's own forward (+Z), or for a symmetry group the
        /// direction from the part toward the vessel's centre (stock reads <c>vessel.CurrentCoM</c>;
        /// the ghost reads its root). Falls back to the part's forward when the two coincide.
        /// </summary>
        internal static Vector3 ResolveCanopyAimUpHint(
            Vector3 partForward, Vector3 partPosition, Vector3 vesselCentre, int symmetryCount)
        {
            if (symmetryCount > 0)
            {
                Vector3 d = vesselCentre - partPosition;
                float m = Magnitude(d);
                if (m > 1e-4f && IsFinite(d))
                    return d * (1f / m);
            }
            return partForward;
        }

        /// <summary>
        /// How this frame's aim moves the canopy: snap on the first aim of a stage, a scrub back,
        /// a gap over <see cref="MaxSynthSampleSeconds"/> or an unknown UT; hold on the same UT;
        /// otherwise step toward the aim by at most <c>rate * dt</c> degrees, the way stock's
        /// <c>RotateTowards(lastRot, aim, rotationSpeedDPS * dt)</c> does.
        /// </summary>
        internal static CanopyAimStep ResolveCanopyAimStep(
            bool seeded, double lastUT, double ut, float rateDegPerSec, out float maxStepDeg)
        {
            maxStepDeg = 0f;
            if (!seeded || double.IsNaN(lastUT) || double.IsNaN(ut)
                || double.IsInfinity(lastUT) || double.IsInfinity(ut))
                return CanopyAimStep.Snap;
            double dt = ut - lastUT;
            if (dt == 0.0)
                return CanopyAimStep.Hold;
            if (dt < 0.0 || dt > MaxSynthSampleSeconds || !(rateDegPerSec > 0f) || float.IsInfinity(rateDegPerSec))
                return CanopyAimStep.Snap;
            maxStepDeg = (float)(rateDegPerSec * dt);
            return CanopyAimStep.Step;
        }

        private static bool IsFinite(Vector3 v)
            => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
               && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        private static float Magnitude(Vector3 v)
            => (float)Math.Sqrt((double)v.x * v.x + (double)v.y * v.y + (double)v.z * v.z);

        /// <summary>
        /// Starts (or restarts) the airflow aim of a canopy that was just posed for
        /// <paramref name="stage"/>, and aims it at once so every caller of the event appliers
        /// sees the aimed pose. No-op for a canopy stock does not aim (EVA chutes).
        /// </summary>
        internal static void BeginCanopyAim(GhostPlaybackState state, ParachuteGhostInfo info, CanopyPoseStage stage)
        {
            if (info == null) return;
            if (!info.aimCanopy || info.canopyTransform == null)
            {
                info.canopyAimStage = CanopyPoseStage.Stowed;
                return;
            }
            info.canopyAimStage = stage;
            info.canopyAimSeeded = false;
            info.canopyAimUT = double.NaN;
            CanopyAimSource source = AimCanopyCore(state, info, double.NaN);
            ParsekLog.Verbose("GhostVisual",
                $"Canopy aim armed: pid={info.partPersistentId} stage={stage} source={source} " +
                $"invert={info.invertCanopy} symmetry={info.symmetryCount.ToString(CultureInfo.InvariantCulture)} " +
                $"spread={ResolveCanopySpreadDeg(info.spreadAngleDeg, info.symmetryCount, stage).ToString("0.0", CultureInfo.InvariantCulture)}deg " +
                $"vessel='{state?.vesselName ?? "unknown"}'");
        }

        /// <summary>Stops a canopy's aim (cut, repack, destroyed, loop-cycle re-stow).</summary>
        internal static void StopCanopyAim(ParachuteGhostInfo info)
        {
            if (info == null) return;
            info.canopyAimStage = CanopyPoseStage.Stowed;
            info.canopyAimSeeded = false;
            info.canopyAimUT = double.NaN;
        }

        /// <summary>
        /// The per-frame canopy aim, beside the other continuous part drivers: every open canopy
        /// turns toward the airflow at its module's rate. A ghost with no open canopy pays one
        /// null check and a walk of its (few) parachute infos.
        /// </summary>
        internal static void UpdateParachuteCanopyAim(GhostPlaybackState state, double ut)
        {
            if (state?.parachuteInfos == null || state.ghost == null) return;
            int aimed = 0, airflow = 0, gravity = 0, partUp = 0;
            foreach (var kvp in state.parachuteInfos)
            {
                ParachuteGhostInfo info = kvp.Value;
                if (info == null || info.canopyAimStage == CanopyPoseStage.Stowed || info.canopyTransform == null)
                    continue;
                CanopyAimSource source = AimCanopyCore(state, info, ut);
                aimed++;
                if (source == CanopyAimSource.Airflow) airflow++;
                else if (source == CanopyAimSource.Gravity) gravity++;
                else partUp++;
            }
            // Gated before the string is built: this runs every frame for every ghost with an open canopy.
            if (aimed > 0 && ParsekLog.IsVerboseEnabled)
                ParsekLog.VerboseRateLimited("GhostVisual", "canopy-aim",
                    $"Canopy aim: {aimed} open canopy(ies) aimed (airflow={airflow} gravity={gravity} partUp={partUp}) " +
                    $"vessel='{state.vesselName ?? "unknown"}'", 5.0);
        }

        /// <summary>
        /// Aims one canopy (Unity calls kept out of the methods a headless test reaches). Returns
        /// the source of the aim direction.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static CanopyAimSource AimCanopyCore(GhostPlaybackState state, ParachuteGhostInfo info, double ut)
        {
            Transform canopy = info.canopyTransform;
            CanopyAimStep step = ResolveCanopyAimStep(info.canopyAimSeeded, info.canopyAimUT, ut,
                info.rotationSpeedDegPerSec, out float maxStepDeg);
            if (step == CanopyAimStep.Hold)
            {
                // Same UT (the second driver call of a frame): keep the world aim, skip the solve.
                canopy.rotation = info.canopyAimWorldRot;
                return info.canopyAimLastSource;
            }
            Transform part = info.partTransform != null ? info.partTransform : canopy.parent;
            Vector3 partUp = part != null ? part.up : Vector3.up;
            Vector3 partForward = part != null ? part.forward : Vector3.forward;
            Vector3 partPos = part != null ? part.position : canopy.position;

            bool velocityKnown = state != null
                && state.lastInterpolatedSurfaceVelocitySource != GhostSurfaceVelocitySource.None;
            Vector3 velocity = state != null ? state.lastInterpolatedSurfaceVelocity : Vector3.zero;
            bool gravityKnown = TryReadGravityUp(state, canopy.position, out Vector3 gravityUp);

            CanopyAimSource source = ResolveCanopyAimForward(velocity, velocityKnown,
                gravityUp, gravityKnown, partUp, info.invertCanopy, out Vector3 aim);
            Vector3 upHint = ResolveCanopyAimUpHint(partForward, partPos,
                state?.ghost != null ? state.ghost.transform.position : partPos, info.symmetryCount);
            Quaternion target = Quaternion.LookRotation(aim, upHint);
            float spread = ResolveCanopySpreadDeg(info.spreadAngleDeg, info.symmetryCount, info.canopyAimStage);
            if (spread > 0f)
                target = target * Quaternion.Euler(spread, 0f, 0f);

            Quaternion applied;
            if (step == CanopyAimStep.Step) applied = Quaternion.RotateTowards(info.canopyAimWorldRot, target, maxStepDeg);
            else applied = target;

            canopy.rotation = applied;
            info.canopyAimWorldRot = applied;
            info.canopyAimSeeded = true;
            info.canopyAimUT = ut;
            info.canopyAimLastSource = source;
            return source;
        }

        /// <summary>
        /// The body's up at <paramref name="position"/>: the ghost's own body when its playback
        /// named one, else the scene's main body. False when neither is known (KSC-only scenes
        /// without a flight body), so the aim falls back to the part's own axis.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadGravityUp(GhostPlaybackState state, Vector3 position, out Vector3 up)
        {
            up = Vector3.zero;
            CelestialBody body = ResolveCachedGhostBody(state);
            if (body == null)
                body = FlightGlobals.currentMainBody;
            if (body == null)
                return false;
            Vector3d d = (Vector3d)position - body.position;
            if (d.sqrMagnitude < 1e-6)
                return false;
            up = (Vector3)d.normalized;
            return true;
        }

        #endregion
    }
}
