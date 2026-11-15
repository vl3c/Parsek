using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Synthesized motion (gimbals, control surfaces, sun tracking, converters, EVA jetpack) and launch dust.
    internal static partial class GhostPlaybackLogic
    {
        #region S3 — synthesized motion (gimbal, control surfaces, sun tracking)

        /// <summary>
        /// The longest UT gap a synthesis derivative will differentiate across. A longer gap is
        /// not clamped - it is DISCARDED and the sample re-seeded. Clamping a derivative's
        /// denominator inflates the answer instead of bounding it, which under time warp reads as
        /// a violent manoeuvre and pins every synthesized surface at its clamp.
        /// </summary>
        internal const double MaxSynthSampleSeconds = 1.0;

        /// <summary>EMA weight on the newest angular-velocity sample. Low = smooth, laggy.</summary>
        internal const float SynthAngularEmaAlpha = 0.25f;
        /// <summary>Angular rates below this are noise, not manoeuvring; everything decays to neutral.</summary>
        internal const float SynthAngularDeadbandDegPerSec = 0.5f;
        /// <summary>Degrees of gimbal per deg/s of body rate. A 5 deg/s pitch buys a 5 deg gimbal.</summary>
        internal const float GimbalGainDegPerDegPerSec = 1.0f;
        /// <summary>Degrees of control-surface deflection per deg/s of body rate.</summary>
        internal const float ControlSurfaceGainDegPerDegPerSec = 2.0f;
        /// <summary>Fallback surface range when the module carries no readable authority.</summary>
        internal const float DefaultControlSurfaceRangeDegrees = 15f;
        internal const float DefaultGimbalRangeDegrees = 4f;
        /// <summary>How fast a synthesized deflection is allowed to move toward its target.</summary>
        internal const float SynthDeflectionSlewDegPerSec = 90f;
        /// <summary>Stock tracking panels slew slowly; this is the visual cap, deg/s.</summary>
        internal const float SunTrackingSlewDegPerSec = 20f;

        /// <summary>
        /// The ghost's body-frame angular velocity in deg/s, from two APPLIED world rotations.
        ///
        /// Every quaternion operation here is written out by hand. <c>Quaternion.Inverse</c>,
        /// <c>operator*</c> and <c>ToAngleAxis</c> are all Unity native calls that throw outside a
        /// Unity runtime, and this decision has to be provable in a headless xUnit process. The
        /// struct itself is safe — only its static math is native.
        ///
        /// The inputs are APPLIED WORLD ROTATIONS, read off the ghost transform after positioning.
        /// They are never <c>TrajectoryPoint.rotation</c> values pulled from a flat Points list: in
        /// a RELATIVE track section that field holds an ANCHOR-LOCAL rotation rather than
        /// srfRelRotation, so differencing two of them across a section boundary mixes frames and
        /// invents a rotation that never happened.
        /// </summary>
        internal static Vector3 ComputeLocalAngularVelocityDegPerSec(
            Quaternion previous, Quaternion current, double deltaSeconds)
        {
            if (deltaSeconds <= 0.0 || double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds))
                return Vector3.zero;

            // delta = conjugate(previous) * current, i.e. the rotation applied in previous's frame.
            float px = -previous.x, py = -previous.y, pz = -previous.z, pw = previous.w;
            float dx = pw * current.x + px * current.w + py * current.z - pz * current.y;
            float dy = pw * current.y - px * current.z + py * current.w + pz * current.x;
            float dz = pw * current.z + px * current.y - py * current.x + pz * current.w;
            float dw = pw * current.w - px * current.x - py * current.y - pz * current.z;

            // Shortest arc: q and -q are the same rotation, and only one of them has a small angle.
            if (dw < 0f) { dx = -dx; dy = -dy; dz = -dz; dw = -dw; }

            double sinHalf = Math.Sqrt((double)dx * dx + (double)dy * dy + (double)dz * dz);
            if (sinHalf <= 1e-7)
                return Vector3.zero;
            if (dw > 1f) dw = 1f;

            double angleDeg = 2.0 * Math.Atan2(sinHalf, dw) * (180.0 / Math.PI);
            double scale = angleDeg / deltaSeconds / sinHalf;
            if (double.IsNaN(scale) || double.IsInfinity(scale))
                return Vector3.zero;

            return new Vector3((float)(dx * scale), (float)(dy * scale), (float)(dz * scale));
        }

        /// <summary>
        /// One gap-frame decay of a smoothed scalar rate toward zero, weighted exactly as an EMA
        /// against a zero sample would be. Used where a frame carries no differentiable sample (a
        /// warp gap) but holding the last rate would freeze a visual: decaying answers "we no longer
        /// know that this is still happening" instead of "it is still happening". Snaps to exactly 0
        /// once the residue is below a ten-thousandth of a degree per second so the value settles
        /// rather than asymptoting forever.
        /// </summary>
        internal static float DecayRateTowardZero(float rate, float alpha)
        {
            if (float.IsNaN(rate) || float.IsInfinity(rate)) return 0f;
            if (float.IsNaN(alpha) || alpha <= 0f) return rate;
            if (alpha >= 1f) return 0f;

            float decayed = rate * (1f - alpha);
            return Math.Abs(decayed) < 1e-4f ? 0f : decayed;
        }

        /// <summary>Exponential moving average of a vector. Pure; <c>Vector3.Lerp</c> is managed but this is explicit.</summary>
        internal static Vector3 ComputeEmaVector(Vector3 previous, Vector3 sample, float alpha)
        {
            if (float.IsNaN(alpha) || alpha <= 0f) return previous;
            if (alpha >= 1f) return sample;
            float keep = 1f - alpha;
            return new Vector3(
                previous.x * keep + sample.x * alpha,
                previous.y * keep + sample.y * alpha,
                previous.z * keep + sample.z * alpha);
        }

        /// <summary>
        /// Maps a body rate (deg/s) to a control deflection (deg): opposite sign (a control surface
        /// or gimbal that produces a nose-up rate is itself deflected the other way), gain-scaled,
        /// deadbanded, and hard-clamped to the module's own authority. Rates below the deadband
        /// answer exactly 0 so the surface settles instead of shimmering on sampling noise.
        /// </summary>
        internal static float ComputeSynthDeflectionDegrees(
            float bodyRateDegPerSec, float gain, float rangeDegrees)
        {
            if (float.IsNaN(bodyRateDegPerSec) || float.IsInfinity(bodyRateDegPerSec))
                return 0f;
            if (Math.Abs(bodyRateDegPerSec) < SynthAngularDeadbandDegPerSec)
                return 0f;

            float range = float.IsNaN(rangeDegrees) || rangeDegrees <= 0f
                ? DefaultControlSurfaceRangeDegrees
                : rangeDegrees;
            float raw = -bodyRateDegPerSec * gain;
            return Math.Min(Math.Max(raw, -range), range);
        }

        /// <summary>
        /// Rate-limited move of a synthesized angle toward its target. Pure. Without this a
        /// deflection would jump the full clamp width on the first frame a manoeuvre starts, and a
        /// derivative spike at a trajectory sample boundary would show up as a visible twitch.
        /// </summary>
        internal static float SlewTowardDegrees(
            float currentDegrees, float targetDegrees, float maxRateDegPerSec, double deltaSeconds)
        {
            if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds <= 0.0)
                return currentDegrees;
            if (float.IsNaN(targetDegrees) || float.IsInfinity(targetDegrees))
                return currentDegrees;

            float maxStep = (float)(maxRateDegPerSec * Math.Min(deltaSeconds, 1.0));
            float delta = targetDegrees - currentDegrees;
            if (Math.Abs(delta) <= maxStep)
                return targetDegrees;
            return currentDegrees + (delta > 0f ? maxStep : -maxStep);
        }

        /// <summary>
        /// Signed rate of change of a ground-track heading, degrees per second, measured about the
        /// local up. Pure: <c>Vector3.Dot</c> / <c>Vector3.Cross</c> and the struct's own magnitude
        /// are managed C#, not native calls.
        /// </summary>
        internal static float ComputeHeadingRateDegPerSec(
            Vector3 previousHeading, Vector3 currentHeading, Vector3 up, double deltaSeconds)
        {
            if (deltaSeconds <= 0.0 || double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds))
                return 0f;
            if (previousHeading.sqrMagnitude <= 1e-8f || currentHeading.sqrMagnitude <= 1e-8f
                || up.sqrMagnitude <= 1e-8f)
            {
                return 0f;
            }

            Vector3 axis = up.normalized;
            Vector3 a = (previousHeading - axis * Vector3.Dot(previousHeading, axis)).normalized;
            Vector3 b = (currentHeading - axis * Vector3.Dot(currentHeading, axis)).normalized;
            if (a.sqrMagnitude <= 1e-8f || b.sqrMagnitude <= 1e-8f)
                return 0f;

            double cos = Math.Min(Math.Max(Vector3.Dot(a, b), -1f), 1f);
            double sin = Vector3.Dot(Vector3.Cross(a, b), axis);
            double angleDeg = Math.Atan2(sin, cos) * (180.0 / Math.PI);
            return (float)(angleDeg / deltaSeconds);
        }

        /// <summary>
        /// The angle (degrees, about <paramref name="axis"/>) that would point
        /// <paramref name="referenceForward"/> at <paramref name="towardTarget"/>, both projected
        /// onto the plane perpendicular to the axis. Returns false when either projection collapses
        /// (target along the axis, or an unusable axis) — the caller HOLDS its current angle rather
        /// than snapping to an arbitrary one. Pure.
        /// </summary>
        internal static bool TryComputeAimAngleDegrees(
            Vector3 towardTarget, Vector3 axis, Vector3 referenceForward, out float angleDegrees)
            => TryComputeAimAngleDegrees(
                towardTarget, axis, referenceForward, out angleDegrees, out _, out _);

        /// <summary>
        /// The diagnosing overload. <paramref name="targetPerpendicular"/> and
        /// <paramref name="referencePerpendicular"/> are the two projections into the aim plane, and
        /// they are what tells a "the Sun is along the axis" hold (target ~ 0, legitimate) apart
        /// from a frame bug (reference ~ 0). The H36 flight had no way to distinguish them and the
        /// cell's failure message could only list both possibilities.
        /// </summary>
        internal static bool TryComputeAimAngleDegrees(
            Vector3 towardTarget, Vector3 axis, Vector3 referenceForward,
            out float angleDegrees, out float targetPerpendicular, out float referencePerpendicular)
        {
            angleDegrees = 0f;
            targetPerpendicular = 0f;
            referencePerpendicular = 0f;
            if (towardTarget.sqrMagnitude <= 1e-8f || axis.sqrMagnitude <= 1e-8f
                || referenceForward.sqrMagnitude <= 1e-8f)
            {
                return false;
            }

            Vector3 n = axis.normalized;
            Vector3 target = towardTarget - n * Vector3.Dot(towardTarget, n);
            Vector3 reference = referenceForward - n * Vector3.Dot(referenceForward, n);
            targetPerpendicular = target.magnitude;
            referencePerpendicular = reference.magnitude;
            if (target.sqrMagnitude <= 1e-8f || reference.sqrMagnitude <= 1e-8f)
                return false;

            target = target.normalized;
            reference = reference.normalized;
            double cos = Math.Min(Math.Max(Vector3.Dot(reference, target), -1f), 1f);
            double sin = Vector3.Dot(Vector3.Cross(reference, target), n);
            angleDegrees = (float)(Math.Atan2(sin, cos) * (180.0 / Math.PI));
            return true;
        }

        /// <summary>
        /// Restores every synthesized transform to the neutral pose captured at build time. Called
        /// on a loop cycle (step 3g) — <see cref="ResetForLoopCycle"/> can zero the numbers but not
        /// touch a Transform, and a pivot left aimed at last orbit's Sun is the M4 bug class.
        /// </summary>
        internal static int RestoreSynthesizedMotionNeutralPoses(GhostPlaybackState state)
        {
            SynthesizedMotionGhostInfos synth = state?.synthesizedMotionInfos;
            if (synth == null) return 0;

            int restored = 0;

            if (synth.gimbals != null)
            {
                for (int i = 0; i < synth.gimbals.Count; i++)
                {
                    GimbalGhostInfo g = synth.gimbals[i];
                    if (g?.gimbalTransforms == null) continue;
                    g.currentDeflection = Vector2.zero;
                    for (int t = 0; t < g.gimbalTransforms.Count && t < g.neutralRotations.Count; t++)
                    {
                        if (g.gimbalTransforms[t] == null) continue;
                        g.gimbalTransforms[t].localRotation = g.neutralRotations[t];
                        restored++;
                    }
                }
            }

            if (synth.controlSurfaces != null)
            {
                for (int i = 0; i < synth.controlSurfaces.Count; i++)
                {
                    ControlSurfaceGhostInfo c = synth.controlSurfaces[i];
                    if (c?.surfaceTransforms == null) continue;
                    c.currentDeflection = 0f;
                    for (int t = 0; t < c.surfaceTransforms.Count && t < c.neutralRotations.Count; t++)
                    {
                        if (c.surfaceTransforms[t] == null) continue;
                        c.surfaceTransforms[t].localRotation = c.neutralRotations[t];
                        restored++;
                    }
                }
            }

            if (synth.sunTrackers != null)
            {
                for (int i = 0; i < synth.sunTrackers.Count; i++)
                {
                    SunTrackingGhostInfo s = synth.sunTrackers[i];
                    if (s?.pivotTransform == null) continue;
                    s.currentAngleDegrees = 0f;
                    s.hasAimed = false;
                    s.pivotTransform.localRotation = s.neutralRotation;
                    restored++;
                }
            }

            // S7: STOP every running loop and put its transforms back to phase 0. Without the stop,
            // a drill switched on during the prior cycle would keep turning through the start of
            // the next one - before the recording's own ConverterActivated has replayed - so the
            // second cycle of a looping mining replay would show the drill already running.
            if (synth.converterLoops != null)
            {
                for (int i = 0; i < synth.converterLoops.Count; i++)
                {
                    ConverterLoopGhostInfo loop = synth.converterLoops[i];
                    if (loop == null) continue;
                    loop.active = false;
                    loop.activeSinceUT = 0.0;
                    if (loop.transforms == null) continue;
                    for (int t = 0; t < loop.transforms.Count; t++)
                    {
                        ConverterLoopTransformState ts = loop.transforms[t];
                        if (ts?.t == null || ts.phases == null || ts.phases.Length == 0) continue;
                        ts.t.localPosition = ts.phases[0].pos;
                        ts.t.localRotation = ts.phases[0].rot;
                        ts.t.localScale = ts.phases[0].scale;
                        restored++;
                    }
                }
            }

            if (restored > 0)
                ParsekLog.VerboseRateLimited("GhostVisual", "loop-synth-restore",
                    $"Loop cycle: restored {restored} synthesized transform(s) to neutral " +
                    $"(vessel='{state.vesselName ?? "unknown"}')", 1.0);
            return restored;
        }

        /// <summary>
        /// The single S3 per-frame entry, called from <c>ApplyFrameVisuals</c> beside
        /// <c>UpdateActiveRobotics</c>. Everything below the first two guards is gated: a ghost with
        /// no gimbal, no control surface and no tracking pivot pays one null check and one
        /// <c>IsEmpty</c> check per frame.
        /// </summary>
        internal static void UpdateSynthesizedMotion(GhostPlaybackState state, double currentUT)
        {
            if (state?.ghost == null) return;
            SynthesizedMotionGhostInfos synth = state.synthesizedMotionInfos;
            if (synth == null || synth.IsEmpty) return;

            // S7 runs FIRST and OUTSIDE the delta-time machinery below, deliberately. The other
            // three families are attitude-DERIVATIVE driven, so they need a usable dt and re-seed
            // (skipping the frame) when they cannot get one. A running loop needs no derivative at
            // all: its phase is a pure function of (currentUT - activeSinceUT). Putting it after
            // the re-seed guard would freeze every drill in the scene for the whole of a sustained
            // time warp — precisely the situation a mining base is usually watched in.
            DriveConverterLoops(state, synth, currentUT);

            // The APPLIED world rotation — post-positioning, post-frame-resolution, correct on
            // Absolute and RELATIVE sections alike because it is the transform, not stored data.
            Quaternion currentRotation = state.ghost.transform.rotation;

            if (double.IsNaN(state.prevSynthUT) || double.IsInfinity(state.prevSynthUT))
            {
                state.prevSynthRotation = currentRotation;
                state.prevSynthUT = currentUT;
                return;
            }

            double deltaSeconds = currentUT - state.prevSynthUT;
            if (deltaSeconds <= 0.0 || deltaSeconds > MaxSynthSampleSeconds)
            {
                // Paused, same-frame double call, a backwards scrub, or a gap too long to
                // differentiate across (high warp, a hitch, a stall). RE-SEED AND SKIP THE SAMPLE
                // rather than clamping the denominator: dividing a full-interval angle by a capped
                // dt does not GUARD the rate, it INFLATES it - a 10 s gap over-reports by 10x, so
                // a slowly-coasting ghost under warp would sit pinned at full deflection while its
                // true rate is under the deadband. A negative dt would invert the sign outright.
                //
                // Accepted: under SUSTAINED warp every frame re-seeds here, so a gimbal or surface
                // holds its last deflection instead of easing to neutral. Left as-is deliberately —
                // the hold is bounded by the clamp, invisible at warp's own visual scale, and
                // self-correcting on the first sub-second frame. Wheel steering does NOT get the same
                // pass (see ResolveGhostHeadingRateDegPerSec): its held state is a caliper angle a
                // player reads directly off a parked rover, so that one decays.
                state.prevSynthRotation = currentRotation;
                state.prevSynthUT = currentUT;
                return;
            }

            Vector3 sample = ComputeLocalAngularVelocityDegPerSec(
                state.prevSynthRotation, currentRotation, deltaSeconds);
            state.smoothedAngularVelocity =
                ComputeEmaVector(state.smoothedAngularVelocity, sample, SynthAngularEmaAlpha);
            state.prevSynthRotation = currentRotation;
            state.prevSynthUT = currentUT;

            Vector3 rate = state.smoothedAngularVelocity;

            DriveSynthesizedGimbals(state, synth, rate, deltaSeconds);
            DriveSynthesizedControlSurfaces(state, synth, rate, deltaSeconds);
            DriveSunTracking(state, synth, deltaSeconds);
        }

        /// <summary>
        /// S7: the running-loop phase for one loop at one recorded UT, wrapped into [0,1).
        ///
        /// Pure, so the cyclic arithmetic — the part most likely to be wrong and least likely to be
        /// noticed — is directly testable. A non-finite or non-positive clip length parks the loop
        /// at phase 0 rather than dividing by it.
        /// </summary>
        internal static float ComputeConverterLoopPhase(
            double currentUT, double activeSinceUT, float clipLengthSeconds)
        {
            if (clipLengthSeconds <= 0f || float.IsNaN(clipLengthSeconds) || float.IsInfinity(clipLengthSeconds))
                return 0f;
            double elapsed = currentUT - activeSinceUT;
            if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed <= 0.0)
                return 0f;

            double cycles = elapsed / clipLengthSeconds;
            double phase = cycles - Math.Floor(cycles);
            if (phase < 0.0) phase += 1.0;
            // Guard the boundary: floating point can land phase at exactly 1.0 for a large
            // `cycles`, and a caller indexing phases[(int)(phase * N)] would then run off the end.
            if (phase >= 1.0) phase = 0.0;
            return (float)phase;
        }

        /// <summary>
        /// S7: resolves a wrapped phase onto a pair of adjacent sampled poses and the blend between
        /// them. The pair WRAPS — phase 11 of 12 blends toward phase 0, not toward a 13th slot —
        /// because the clip is cyclic and the sampler deliberately did not store a duplicate
        /// endpoint.
        /// </summary>
        internal static void ResolveConverterLoopBlend(
            float phase, int phaseCount, out int fromIndex, out int toIndex, out float blend)
        {
            fromIndex = 0;
            toIndex = 0;
            blend = 0f;
            if (phaseCount <= 0) return;
            if (phaseCount == 1) return;

            float scaled = Mathf.Clamp01(phase) * phaseCount;
            fromIndex = (int)scaled;
            if (fromIndex >= phaseCount) fromIndex = phaseCount - 1;
            toIndex = (fromIndex + 1) % phaseCount;
            blend = Mathf.Clamp01(scaled - fromIndex);
        }

        /// <summary>
        /// S7: advances every ACTIVE running loop on this ghost to the pose its recorded UT implies.
        /// Inactive loops are left exactly where they stopped, which is what a switched-off drill
        /// looks like — parked mid-stroke, not snapped to a home pose.
        /// </summary>
        private static void DriveConverterLoops(
            GhostPlaybackState state, SynthesizedMotionGhostInfos synth, double currentUT)
        {
            if (synth.converterLoops == null || synth.converterLoops.Count == 0) return;

            int driven = 0;
            for (int i = 0; i < synth.converterLoops.Count; i++)
            {
                ConverterLoopGhostInfo loop = synth.converterLoops[i];
                if (loop == null || !loop.active) continue;
                if (loop.transforms == null || loop.transforms.Count == 0) continue;

                float phase = ComputeConverterLoopPhase(
                    currentUT, loop.activeSinceUT, loop.clipLengthSeconds);

                for (int t = 0; t < loop.transforms.Count; t++)
                {
                    ConverterLoopTransformState ts = loop.transforms[t];
                    if (ts?.t == null || ts.phases == null || ts.phases.Length == 0) continue;

                    ResolveConverterLoopBlend(
                        phase, ts.phases.Length, out int from, out int to, out float blend);

                    ConverterLoopPose a = ts.phases[from];
                    ConverterLoopPose b = ts.phases[to];
                    ts.t.localPosition = Vector3.Lerp(a.pos, b.pos, blend);
                    ts.t.localRotation = Quaternion.Slerp(a.rot, b.rot, blend);
                    ts.t.localScale = Vector3.Lerp(a.scale, b.scale, blend);
                }
                driven++;
            }

            if (driven > 0)
                ParsekLog.VerboseRateLimited("GhostVisual", "converter-loop-drive",
                    $"Converter loops driven: {driven} (vessel='{state.vesselName ?? "unknown"}')", 5.0);
        }

        /// <summary>
        /// S7: starts or stops one part's running loop. Called by the ConverterActivated /
        /// ConverterDeactivated events, by the snapshot-start seed and by the loop-cycle reset.
        ///
        /// <paramref name="activeSinceUT"/> is the RECORDED event UT, which is what makes the whole
        /// thing scrub-safe: replaying the same recorded moment always lands on the same phase, so
        /// a rewound or looping replay is not merely close but identical.
        /// </summary>
        internal static bool ApplyConverterLoopState(
            GhostPlaybackState state, uint partPersistentId, bool active, double activeSinceUT)
        {
            GhostPartEventOutcome outcome = ApplyConverterLoopStateWithOutcome(
                state, partPersistentId, active, activeSinceUT);
            // Historical contract preserved exactly: the bool was true whenever a loop
            // for this pid was REACHED, including the deliberate ignore of a duplicate
            // activation. The outcome enum is what splits those two apart.
            return outcome == GhostPartEventOutcome.Applied
                || outcome == GhostPartEventOutcome.AlreadyInState;
        }

        /// <summary>
        /// P8 step 1 outcome-reporting core. Reports AlreadyInState when every loop
        /// matched for this pid was already active and the duplicate-activation ignore
        /// fired - the one case where "the handler ran and changed nothing" is
        /// deliberate rather than a resolution failure.
        /// </summary>
        internal static GhostPartEventOutcome ApplyConverterLoopStateWithOutcome(
            GhostPlaybackState state, uint partPersistentId, bool active, double activeSinceUT)
        {
            var synth = state?.synthesizedMotionInfos;
            if (synth?.converterLoops == null) return GhostPartEventOutcome.NoFamilyState;

            bool matched = false;
            bool changed = false;
            for (int i = 0; i < synth.converterLoops.Count; i++)
            {
                ConverterLoopGhostInfo loop = synth.converterLoops[i];
                if (loop == null || loop.partPersistentId != partPersistentId) continue;

                // Re-arming an ALREADY-active loop would restart it from phase 0 and produce a
                // visible hitch. A duplicate ConverterActivated (a snapshot seed followed by a
                // start-UT seed for the same part) is exactly that case, so it is ignored.
                if (active && loop.active) { matched = true; continue; }

                loop.active = active;
                if (active) loop.activeSinceUT = activeSinceUT;
                matched = true;
                changed = true;
            }

            if (!matched) return GhostPartEventOutcome.NoInfoForPart;
            return changed
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.AlreadyInState;
        }

        /// <summary>
        /// S4: the jetpack plume gate. Pure, because it is the one place the three recorded EVA
        /// flags combine and the combination is the whole honesty of the feature.
        ///
        /// All three conditions are load-bearing:
        /// <list type="bullet">
        /// <item><description>THRUSTING is the signal itself, already debounced by the recorder so
        /// a single tap never reaches here.</description></item>
        /// <item><description>JETPACK DEPLOYED, because a stowed pack cannot thrust. The recorder
        /// reads the two flags independently and KSP recomputes JetpackIsThrusting from fuel flow,
        /// so the pair CAN disagree for a frame around a stow - and a plume from a stowed pack is
        /// the more visible of the two possible errors.</description></item>
        /// <item><description>NOT RAGDOLL. A tumbling kerbal is not flying, and stock cuts thrust
        /// when the FSM enters ragdoll. This is also where the ragdoll events earn their keep on the
        /// VISUAL side, given that the ragdoll POSE is deliberately not replayed.</description></item>
        /// </list>
        /// </summary>
        internal static bool ShouldEmitEvaJetpackPlume(bool deployed, bool thrusting, bool ragdoll)
            => thrusting && deployed && !ragdoll;

        /// <summary>
        /// S4: records one EVA flag change and reconciles the plume to the resulting gate.
        ///
        /// All six EVA event types route through here rather than each toggling the particle system
        /// itself, so the gate is evaluated in exactly one place and cannot drift between call sites.
        /// </summary>
        internal static void ApplyEvaState(GhostPlaybackState state, PartEventType type)
        {
            ApplyEvaStateWithOutcome(state, type);
        }

        /// <summary>
        /// P8 step 1 outcome-reporting core. The only non-apply here is an event type
        /// the flag reducer does not model (a null state, or a member routed to this
        /// arm by mistake), which is exactly UnhandledEventType.
        /// </summary>
        internal static GhostPartEventOutcome ApplyEvaStateWithOutcome(
            GhostPlaybackState state, PartEventType type)
        {
            if (!TryUpdateEvaFlags(state, type)) return GhostPartEventOutcome.UnhandledEventType;
            ReconcileEvaJetpackPlume(state);
            return GhostPartEventOutcome.Applied;
        }

        /// <summary>
        /// S4: folds one EVA event into the three playback flags. Returns false for an event type
        /// that is not an EVA one, so the caller skips the reconcile.
        ///
        /// Split out from <see cref="ApplyEvaState"/> so the BOOKKEEPING is headless-testable: the
        /// reconcile below has to compare a GameObject against null, which routes through a
        /// UnityEngine.Object ECall xUnit cannot host. Same pure-decision / Unity-applier division
        /// the rest of this file uses.
        /// </summary>
        internal static bool TryUpdateEvaFlags(GhostPlaybackState state, PartEventType type)
        {
            if (state == null) return false;

            switch (type)
            {
                case PartEventType.EvaJetpackDeployed: state.evaJetpackDeployed = true; return true;
                case PartEventType.EvaJetpackStowed: state.evaJetpackDeployed = false; return true;
                case PartEventType.EvaJetpackThrustStarted: state.evaJetpackThrusting = true; return true;
                case PartEventType.EvaJetpackThrustStopped: state.evaJetpackThrusting = false; return true;
                case PartEventType.EvaRagdollStarted: state.evaRagdoll = true; return true;
                case PartEventType.EvaRagdollEnded: state.evaRagdoll = false; return true;
                default: return false;
            }
        }

        /// <summary>
        /// S4: what one plume reconcile should DO, as a pure function of the gate plus the facts
        /// only Unity can answer. Split out from <see cref="ReconcileEvaJetpackPlume"/> so the
        /// interesting decision - in particular the inactive-hierarchy case, which is the one that
        /// shipped broken - is headless-testable; the Unity reads and the build / Play / Stop calls
        /// stay in the impure wrapper.
        /// </summary>
        internal enum EvaPlumeReconcileAction
        {
            /// <summary>Gate shut and nothing playing: no work.</summary>
            None,
            /// <summary>Gate shut but the system is still emitting: stop it.</summary>
            Stop,
            /// <summary>Gate open, hierarchy active, no system yet: build one, then play it.</summary>
            Build,
            /// <summary>Gate open, hierarchy active, system exists and is idle: start it.</summary>
            Play,
            /// <summary>Gate open and the system is already emitting: no work.</summary>
            AlreadyPlaying,
            /// <summary>
            /// Gate open but the ghost is NOT active in the hierarchy, so neither building nor
            /// playing would take effect. Do nothing THIS call; the per-frame self-heal retries once
            /// the ghost is shown.
            /// </summary>
            DeferInactiveHierarchy,
            /// <summary>A previous build failed (no additive shader): never retry.</summary>
            Unavailable,
        }

        /// <summary>
        /// THE INACTIVE-HIERARCHY CASE IS THE WHOLE REASON THIS IS A NAMED FUNCTION. Unity's
        /// <c>ParticleSystem.Play()</c> on a system that is not <c>activeInHierarchy</c> is a SILENT
        /// no-op - it neither throws nor sets <c>isPlaying</c> - and a ghost spends a genuinely
        /// reachable part of its life inactive: <c>BuildTimelineGhostFromSnapshot</c> ends with
        /// <c>root.SetActive(false)</c>, and the spawn-time prefix replay inside
        /// <c>ApplyPartEvents</c> runs BEFORE <c>ActivateGhostVisualsIfNeeded</c>. So a ghost whose
        /// playback cursor lands inside a thrust burst consumed its <c>EvaJetpackThrustStarted</c>
        /// against an inactive hierarchy, the Play no-opped, and - because this reconcile was
        /// EVENT-driven only - nothing ever tried again. The plume stayed dark for the whole burst
        /// while the log claimed it was emitting.
        ///
        /// Deferring the BUILD as well (rather than building and failing to play) keeps the laziness
        /// claim honest: a ghost that is never shown allocates nothing at all.
        /// </summary>
        internal static EvaPlumeReconcileAction ClassifyEvaPlumeReconcile(
            bool wanted, bool hasPlume, bool unavailable, bool hierarchyActive, bool isPlaying)
        {
            if (!wanted)
                return hasPlume && isPlaying ? EvaPlumeReconcileAction.Stop : EvaPlumeReconcileAction.None;

            // Checked BEFORE `unavailable` and before the build: an inactive ghost is a
            // RETRY-LATER, not a permanent failure, and conflating the two would either mark a
            // perfectly good ghost permanently unavailable or allocate for one never shown.
            if (!hierarchyActive)
                return EvaPlumeReconcileAction.DeferInactiveHierarchy;

            if (unavailable) return EvaPlumeReconcileAction.Unavailable;
            if (!hasPlume) return EvaPlumeReconcileAction.Build;
            return isPlaying ? EvaPlumeReconcileAction.AlreadyPlaying : EvaPlumeReconcileAction.Play;
        }

        /// <summary>
        /// S4: brings the plume into line with the flags. Builds it LAZILY on the first moment it is
        /// wanted AND showable, so an EVA ghost that never fires its pack - or is never shown -
        /// allocates no particle system, and a non-EVA ghost never reaches here at all.
        ///
        /// Called BOTH from the six EVA events (immediate response) and once per rendered frame
        /// while the recording says the pack is firing
        /// (<see cref="UpdateEvaJetpackPlumeForFrame"/>). The per-frame call is what makes it
        /// SELF-HEALING, exactly as launch dust already is: whatever the hierarchy was doing when
        /// the event arrived, the first frame the ghost is actually visible starts the plume.
        ///
        /// Logging is DECISION-VS-TRUTH, per the named anomaly class the render tracers use: the
        /// success line is emitted only after reading <c>isPlaying</c> BACK and finding it true. It
        /// used to be emitted on the strength of having called Play, which is exactly how a total
        /// no-op logged as a success.
        /// </summary>
        internal static void ReconcileEvaJetpackPlume(GhostPlaybackState state)
        {
            if (state == null) return;

            bool wanted = ShouldEmitEvaJetpackPlume(
                state.evaJetpackDeployed, state.evaJetpackThrusting, state.evaRagdoll);

            // Every Unity read happens ONCE, here, and is handed to the pure classifier. A PLAIN
            // reference check guards the info object first: comparing a null ParticleSystem against
            // null would still route through UnityEngine.Object's overloaded operator.
            bool hasPlume = state.evaJetpackPlumeInfo != null;
            ParticleSystem ps = hasPlume ? state.evaJetpackPlumeInfo.particles : null;
            bool isPlaying = hasPlume && ps != null && ps.isPlaying;
            bool hierarchyActive = state.ghost != null && state.ghost.activeInHierarchy;

            EvaPlumeReconcileAction action = ClassifyEvaPlumeReconcile(
                wanted, hasPlume, state.evaJetpackPlumeUnavailable, hierarchyActive, isPlaying);

            switch (action)
            {
                case EvaPlumeReconcileAction.None:
                case EvaPlumeReconcileAction.AlreadyPlaying:
                case EvaPlumeReconcileAction.Unavailable:
                    return;

                case EvaPlumeReconcileAction.Stop:
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    return;

                case EvaPlumeReconcileAction.DeferInactiveHierarchy:
                    // Not a failure, and deliberately not silent: this is the state the spawn prefix
                    // replay is ALWAYS in, so it has to be greppable when a plume is reported
                    // missing.
                    ParsekLog.VerboseRateLimited("GhostVisual", "eva-plume-deferred",
                        "EVA jetpack plume deferred (vessel='" + (state.vesselName ?? "unknown") +
                        "'): ghost not active in hierarchy, so Play() would silently no-op; " +
                        "retrying on the first rendered frame", 5.0);
                    return;

                case EvaPlumeReconcileAction.Build:
                    state.evaJetpackPlumeInfo = GhostVisualBuilder.TryBuildEvaJetpackPlume(
                        state.ghost, state.vesselName);
                    if (state.evaJetpackPlumeInfo == null)
                    {
                        state.evaJetpackPlumeUnavailable = true;
                        return;
                    }
                    ps = state.evaJetpackPlumeInfo.particles;
                    goto case EvaPlumeReconcileAction.Play;

                case EvaPlumeReconcileAction.Play:
                    if (ps == null) return;
                    ps.Play();

                    // TRUTH, read back rather than assumed.
                    if (ps.isPlaying)
                    {
                        ParsekLog.VerboseRateLimited("GhostVisual", "eva-plume",
                            "EVA jetpack plume emitting (vessel='" + (state.vesselName ?? "unknown") +
                            "' deployed=" + state.evaJetpackDeployed +
                            " thrusting=" + state.evaJetpackThrusting +
                            " ragdoll=" + state.evaRagdoll + ")", 5.0);
                    }
                    else
                    {
                        // WARN, not Verbose: the hierarchy WAS active, so the deferral branch did not
                        // apply and Play() should have taken. Anything reaching here is an unmodelled
                        // refusal, and the render tracers' convention is that a decision-vs-truth
                        // mismatch nobody predicted is loud.
                        ParsekLog.Warn("GhostVisual",
                            "EVA jetpack plume did NOT start after Play() (vessel='" +
                            (state.vesselName ?? "unknown") + "' hierarchyActive=true deployed=" +
                            state.evaJetpackDeployed + " thrusting=" + state.evaJetpackThrusting +
                            " ragdoll=" + state.evaRagdoll +
                            ") - the gate opened but the system stayed idle");
                    }
                    return;
            }
        }

        /// <summary>
        /// S4 per-frame self-heal, called from <c>ApplyFrameVisuals</c> beside the launch-dust drive.
        /// Gated on the ONE flag that can want a plume, so a non-EVA ghost - i.e. essentially every
        /// ghost - pays a single bool field read per frame and nothing more.
        ///
        /// It exists because the event-driven reconcile alone is not enough: see
        /// <see cref="ClassifyEvaPlumeReconcile"/> for the inactive-hierarchy no-op it repairs.
        /// Launch dust gets the same property for free by re-calling Play every frame; this plume is
        /// event-driven, so it needs the retry stated explicitly.
        /// </summary>
        internal static void UpdateEvaJetpackPlumeForFrame(GhostPlaybackState state)
        {
            if (state == null || !state.evaJetpackThrusting) return;
            ReconcileEvaJetpackPlume(state);
        }

        private static void DriveSynthesizedGimbals(
            GhostPlaybackState state, SynthesizedMotionGhostInfos synth,
            Vector3 rate, double deltaSeconds)
        {
            if (synth.gimbals == null || synth.gimbals.Count == 0) return;

            for (int i = 0; i < synth.gimbals.Count; i++)
            {
                GimbalGhostInfo g = synth.gimbals[i];
                if (g?.gimbalTransforms == null || g.gimbalTransforms.Count == 0) continue;

                // A gimbal only steers while its engine is LIT. A cold engine's bell hangs neutral;
                // easing there rather than snapping keeps a shutdown from popping the nozzle.
                bool lit = IsAnyEnginePowerOnPart(state, g.partPersistentId);
                float targetX = lit
                    ? ComputeSynthDeflectionDegrees(rate.x, GimbalGainDegPerDegPerSec, g.gimbalRangeDegrees)
                    : 0f;
                float targetY = lit
                    ? ComputeSynthDeflectionDegrees(rate.y, GimbalGainDegPerDegPerSec, g.gimbalRangeDegrees)
                    : 0f;

                g.currentDeflection = new Vector2(
                    SlewTowardDegrees(g.currentDeflection.x, targetX, SynthDeflectionSlewDegPerSec, deltaSeconds),
                    SlewTowardDegrees(g.currentDeflection.y, targetY, SynthDeflectionSlewDegPerSec, deltaSeconds));

                Quaternion offset =
                    Quaternion.AngleAxis(g.currentDeflection.x, Vector3.right)
                    * Quaternion.AngleAxis(g.currentDeflection.y, Vector3.up);

                for (int t = 0; t < g.gimbalTransforms.Count && t < g.neutralRotations.Count; t++)
                {
                    Transform tr = g.gimbalTransforms[t];
                    if (tr == null) continue;
                    tr.localRotation = g.neutralRotations[t] * offset;
                }
            }
        }

        private static void DriveSynthesizedControlSurfaces(
            GhostPlaybackState state, SynthesizedMotionGhostInfos synth,
            Vector3 rate, double deltaSeconds)
        {
            if (synth.controlSurfaces == null || synth.controlSurfaces.Count == 0) return;

            // Aero surfaces only work in air. Vacuum (or an unresolvable body) drives them to
            // neutral rather than leaving them cocked from the last atmospheric frame.
            bool inAtmosphere = IsGhostInAtmosphere(state);

            for (int i = 0; i < synth.controlSurfaces.Count; i++)
            {
                ControlSurfaceGhostInfo c = synth.controlSurfaces[i];
                if (c?.surfaceTransforms == null || c.surfaceTransforms.Count == 0) continue;

                // PRECEDENCE (risk 4): a DEPLOYED deployable pose on the same part owns this
                // transform. An extended airbrake is a brake pose, and S2 is mid-clip or holding
                // it; deflecting on top would fight the transition.
                if (IsDeployablePoseHeldForPart(state, c.partPersistentId))
                    continue;

                float bodyRate = 0f;
                if (!c.ignorePitch) bodyRate += rate.x;
                if (!c.ignoreYaw) bodyRate += rate.y;
                if (!c.ignoreRoll) bodyRate += rate.z;

                float target = inAtmosphere
                    ? ComputeSynthDeflectionDegrees(
                        bodyRate, ControlSurfaceGainDegPerDegPerSec, c.rangeDegrees)
                    : 0f;
                c.currentDeflection = SlewTowardDegrees(
                    c.currentDeflection, target, SynthDeflectionSlewDegPerSec, deltaSeconds);

                Quaternion offset = Quaternion.AngleAxis(c.currentDeflection, Vector3.right);
                for (int t = 0; t < c.surfaceTransforms.Count && t < c.neutralRotations.Count; t++)
                {
                    Transform tr = c.surfaceTransforms[t];
                    if (tr == null) continue;
                    tr.localRotation = c.neutralRotations[t] * offset;
                }
            }
        }

        private static void DriveSunTracking(
            GhostPlaybackState state, SynthesizedMotionGhostInfos synth, double deltaSeconds)
        {
            if (synth.sunTrackers == null || synth.sunTrackers.Count == 0) return;

            CelestialBody sun = Planetarium.fetch?.Sun;
            if (sun == null) return;
            Vector3d sunPosition = sun.position;

            for (int i = 0; i < synth.sunTrackers.Count; i++)
            {
                SunTrackingGhostInfo s = synth.sunTrackers[i];
                if (s?.pivotTransform == null) continue;

                // PRECEDENCE (risk 4): transition > tracking. A panel only tracks once it is fully
                // deployed and nothing is animating it.
                if (!IsDeployablePoseFullyDeployedForPart(state, s.partPersistentId))
                {
                    // DISCRIMINATOR 1 of 2. A tracking panel that never aims has exactly two
                    // possible reasons and they need opposite fixes; this line says which, so the
                    // next red is one read rather than a re-flight. (The other is at the aim site.)
                    // IsVerboseEnabled-guarded because the rate limiter cannot stop the CALLER from
                    // building the string: this runs every frame for every stowed panel on every
                    // ghost, and a stowed panel is the common case.
                    if (ParsekLog.IsVerboseEnabled)
                    {
                        ParsekLog.VerboseRateLimited("GhostVisual",
                            $"sun-track-gate-{s.partPersistentId}",
                            $"Sun tracking held (gate closed) pid={s.partPersistentId}: " +
                            DescribeDeployableGateForPart(state, s.partPersistentId), 30.0);
                    }

                    // TRANSITION > TRACKING, in its strong form: while a clip is actually RUNNING
                    // we must not write the pivot AT ALL. On many tracking panels the pivot is one
                    // of the transforms the deploy animation itself moves, so easing it toward
                    // neutral here would overwrite UpdateActiveDeployables' write from earlier in
                    // the same frame and make the retract judder. Drop the bookkeeping and let S2
                    // own the transform; the ease-back below is only for a panel that is stowed
                    // and static, where nothing else is writing it.
                    if (IsDeployableTransitionRunningForPart(state, s.partPersistentId))
                    {
                        s.currentAngleDegrees = 0f;
                        s.hasAimed = false;
                        continue;
                    }
                    if (s.hasAimed)
                    {
                        s.currentAngleDegrees = SlewTowardDegrees(
                            s.currentAngleDegrees, 0f, SunTrackingSlewDegPerSec, deltaSeconds);
                        ApplySunTrackingAngle(s);
                        if (Math.Abs(s.currentAngleDegrees) <= 0.01f) s.hasAimed = false;
                    }
                    continue;
                }

                Transform pivot = s.pivotTransform;

                // BOTH the axis and the reference are read in the PIVOT'S OWN NEUTRAL FRAME, and
                // that is the whole of the 2026-08-11 H36 fix. ApplySunTrackingAngle POST-multiplies
                // (neutralRotation * AngleAxis(angle, axisLocal)), so the world axis the applied
                // angle actually turns about is parentRotation * neutralRotation * axisLocal — not
                // the parent's own up, which is what this used to measure against. Two consequences,
                // both real:
                //   * the measured angle was expressed about a different axis than the one it was
                //     applied about whenever the pivot's neutral rotation was not identity, and
                //   * the reference direction (the pivot's neutral forward) could land PARALLEL to
                //     the parent's up, at which point its projection into the aim plane vanished,
                //     TryComputeAimAngleDegrees answered false every frame, and the panel never
                //     tracked at all. That is exactly what solarPanelOX10C did in the H36 flight.
                // In the pivot's own frame the reference is perpendicular to the axis BY
                // CONSTRUCTION (see ResolveSunTrackingReferenceLocal), so the only remaining "hold"
                // is the legitimate one: the Sun lying along the rotation axis.
                //
                // This is also what stock does. ModuleDeployablePart tracks with
                // `Atan2(pivot.InverseTransformPoint(sun).x, ....z)` applied as
                // `pivot.rotation * Euler(0, y, 0)` — the pivot's own local +Y as the axis and its
                // own local +Z as the zero-angle reference.
                Quaternion parentRotation =
                    pivot.parent != null ? pivot.parent.rotation : Quaternion.identity;
                Quaternion neutralWorld = parentRotation * s.neutralRotation;
                Vector3 axisWorld = neutralWorld * ResolveSunTrackingAxisLocal(s.axisLocal);
                Vector3 referenceForward = neutralWorld * ResolveSunTrackingReferenceLocal(s.axisLocal);
                Vector3 toSun = (Vector3)(sunPosition - (Vector3d)pivot.position);

                if (!TryComputeAimAngleDegrees(
                        toSun, axisWorld, referenceForward,
                        out float aim, out float targetPerp, out float referencePerp))
                {
                    // DISCRIMINATOR 2 of 2. The gate is OPEN here, so this is the geometric hold —
                    // and the two projection magnitudes say which of the three degeneracies it is.
                    // Same per-frame string-building guard as the gate line above: a hold persists
                    // for as long as the geometry does, so this would run every frame.
                    if (ParsekLog.IsVerboseEnabled)
                    {
                        ParsekLog.VerboseRateLimited("GhostVisual",
                            $"sun-track-aim-{s.partPersistentId}",
                            $"Sun tracking held (aim unresolved) pid={s.partPersistentId}: " +
                            $"targetPerp={targetPerp.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"referencePerp={referencePerp.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"toSun={toSun.magnitude.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"axisWorld={axisWorld} — a near-zero targetPerp is the Sun lying along " +
                            "the pivot axis (a legitimate hold); a near-zero referencePerp is a " +
                            "frame bug", 30.0);
                    }
                    continue;
                }

                s.currentAngleDegrees = SlewTowardDegrees(
                    s.currentAngleDegrees, aim, SunTrackingSlewDegPerSec, deltaSeconds);
                s.hasAimed = true;
                ApplySunTrackingAngle(s);
            }
        }

        private static void ApplySunTrackingAngle(SunTrackingGhostInfo s)
        {
            if (s?.pivotTransform == null) return;
            // Same resolver the aim used, so the angle is applied about the axis it was measured
            // about. These two used to drift apart and that was half the H36 sun-tracking defect.
            s.pivotTransform.localRotation = s.neutralRotation
                * Quaternion.AngleAxis(s.currentAngleDegrees, ResolveSunTrackingAxisLocal(s.axisLocal));
        }

        /// <summary>
        /// The pivot's rotation axis IN ITS OWN LOCAL FRAME, normalised, with the stock default
        /// (local up / +Y, what <c>ModuleDeployablePart</c> rotates about) as the fallback for an
        /// unusable stored axis. Pure Vector3 math — no native call — so it is headless-testable.
        /// </summary>
        internal static Vector3 ResolveSunTrackingAxisLocal(Vector3 axisLocal)
            => axisLocal.sqrMagnitude > 0.0001f ? axisLocal.normalized : Vector3.up;

        /// <summary>
        /// The ZERO-ANGLE REFERENCE direction in the pivot's own local frame: the direction that
        /// ends up pointing at the Sun when the tracking angle is applied.
        ///
        /// Local +Z, matching stock (<c>Atan2(sunLocal.x, sunLocal.z)</c> is measured from the
        /// pivot's own +Z), except that it is explicitly ORTHOGONALISED against the axis and
        /// swapped for +X if the axis happens to be +Z. That guarantee is the point: a reference
        /// with no component in the aim plane makes the aim unresolvable, and a panel whose
        /// reference silently degenerated is a panel that never tracks — the H36 red.
        /// </summary>
        internal static Vector3 ResolveSunTrackingReferenceLocal(Vector3 axisLocal)
        {
            Vector3 axis = ResolveSunTrackingAxisLocal(axisLocal);
            Vector3 candidate = Vector3.forward;
            if (Math.Abs(Vector3.Dot(candidate, axis)) > 0.9f) candidate = Vector3.right;

            Vector3 perpendicular = candidate - axis * Vector3.Dot(candidate, axis);
            return perpendicular.sqrMagnitude > 1e-6f ? perpendicular.normalized : Vector3.forward;
        }

        /// <summary>The deployable gate's own state for one part, as a grep-stable one-liner. Shared
        /// by the runtime hold log and the in-game cell's failure message so both read alike.</summary>
        internal static string DescribeDeployableGateForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.deployableInfos == null)
                return "deployable=no-dictionary (treated as deployed)";
            if (!state.deployableInfos.TryGetValue(partPersistentId, out DeployableGhostInfo d)
                || d == null)
            {
                return "deployable=absent (treated as deployed)";
            }
            return "deployable=present"
                + $" breakSubtreeHidden={d.breakSubtreeHidden}"
                + $" currentDeployed={d.currentDeployed}"
                + $" transitionActive={d.transitionActive}"
                + $" deployFraction={d.deployFraction.ToString("R", CultureInfo.InvariantCulture)}"
                + $" transforms={d.transforms?.Count ?? 0}";
        }

        /// <summary>
        /// The FULL sun-tracking decision for one tracker: the deployed gate AND the two aim-plane
        /// projections, in one string. The in-game cell pastes this straight into its failure
        /// message so a red names its own cause instead of listing candidates. Reads Transforms, so
        /// this is scene-side, not headless.
        /// </summary>
        internal static string DescribeSunTrackingState(
            GhostPlaybackState state, SunTrackingGhostInfo s)
        {
            if (s == null) return "tracker=null";
            string gate = "gate="
                + (IsDeployablePoseFullyDeployedForPart(state, s.partPersistentId) ? "open" : "closed")
                + " " + DescribeDeployableGateForPart(state, s.partPersistentId);

            Transform pivot = s.pivotTransform;
            if (pivot == null) return gate + " pivot=null";

            CelestialBody sun = Planetarium.fetch?.Sun;
            if (sun == null) return gate + " sun=absent";

            Quaternion parentRotation =
                pivot.parent != null ? pivot.parent.rotation : Quaternion.identity;
            Quaternion neutralWorld = parentRotation * s.neutralRotation;
            Vector3 axisWorld = neutralWorld * ResolveSunTrackingAxisLocal(s.axisLocal);
            Vector3 referenceForward = neutralWorld * ResolveSunTrackingReferenceLocal(s.axisLocal);
            Vector3 toSun = (Vector3)(sun.position - (Vector3d)pivot.position);

            bool resolved = TryComputeAimAngleDegrees(
                toSun, axisWorld, referenceForward,
                out float aim, out float targetPerp, out float referencePerp);

            return gate
                + $" aimResolved={resolved}"
                + $" aim={aim.ToString("R", CultureInfo.InvariantCulture)}"
                + $" targetPerp={targetPerp.ToString("R", CultureInfo.InvariantCulture)}"
                + $" referencePerp={referencePerp.ToString("R", CultureInfo.InvariantCulture)}"
                + $" currentAngle={s.currentAngleDegrees.ToString("R", CultureInfo.InvariantCulture)}"
                + $" hasAimed={s.hasAimed}";
        }

        /// <summary>True when any engine module on the part is currently commanded above zero.</summary>
        private static bool IsAnyEnginePowerOnPart(GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.engineInfos == null) return false;
            foreach (var kvp in state.engineInfos)
            {
                EngineGhostInfo info = kvp.Value;
                if (info != null && info.partPersistentId == partPersistentId && info.currentPower > 0f)
                    return true;
            }
            return false;
        }

        /// <summary>True when the part has a deployable clip actually RUNNING this frame.</summary>
        private static bool IsDeployableTransitionRunningForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.deployableInfos == null) return false;
            return state.deployableInfos.TryGetValue(partPersistentId, out DeployableGhostInfo d)
                && d != null && d.transitionActive;
        }

        /// <summary>
        /// True when the part carries a deployable family that is deployed or mid-transition, i.e.
        /// S2 owns its transforms this frame.
        /// </summary>
        private static bool IsDeployablePoseHeldForPart(GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.deployableInfos == null) return false;
            if (!state.deployableInfos.TryGetValue(partPersistentId, out DeployableGhostInfo d) || d == null)
                return false;
            return d.transitionActive || d.currentDeployed;
        }

        /// <summary>
        /// True when the part is safe for sun tracking: fully deployed with nothing animating. A
        /// part with NO deployable family at all counts as deployed — a fixed tracking panel has no
        /// stow pose to be caught in.
        /// </summary>
        private static bool IsDeployablePoseFullyDeployedForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.deployableInfos == null) return true;
            if (!state.deployableInfos.TryGetValue(partPersistentId, out DeployableGhostInfo d) || d == null)
                return true;
            // S6: a BROKEN panel closes the gate. The pivot may still carry a fully-deployed pose
            // from before the break (the recorder does not retract it - that is rule 1), so without
            // this the ghost would slew the pivot of a panel it has just hidden toward the Sun for
            // the rest of the recording.
            //
            // SCOPE, stated precisely because the obvious stronger claim is false: this closes the
            // gate for any part that HAS a DeployableGhostInfo, including one whose break transform
            // did not resolve (ApplyDeployableBrokenState still sets the flag). It does NOT cover a
            // part with no info at all - the TryGetValue miss above answers "treated as deployed" -
            // but that combination needs a part with no deploy animation AND an unresolvable break
            // transform AND isTracking, which no stock part is: every tracking panel has a deploy
            // animation, so it always has an info.
            if (d.breakSubtreeHidden) return false;
            return d.currentDeployed && !d.transitionActive && d.deployFraction >= 1f - 1e-4f;
        }

        /// <summary>
        /// Resolves the ghost's current body through the same name-keyed cache the audio, wheel and
        /// watch-camera paths use (a string compare on a steady frame; a Find only on an SOI change).
        /// </summary>
        internal static CelestialBody ResolveCachedGhostBody(GhostPlaybackState state)
        {
            if (state == null) return null;
            string bodyName = state.lastInterpolatedBodyName;
            if (string.IsNullOrEmpty(bodyName)) return null;

            if (state.cachedAudioBody == null || state.cachedAudioBodyName != bodyName)
            {
                state.cachedAudioBody = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                state.cachedAudioBodyName = bodyName;
            }
            return state.cachedAudioBody;
        }

        private static bool IsGhostInAtmosphere(GhostPlaybackState state)
        {
            CelestialBody body = ResolveCachedGhostBody(state);
            if (body == null || !body.atmosphere) return false;
            double altitude = state.lastInterpolatedAltitude;
            if (double.IsNaN(altitude) || double.IsInfinity(altitude)) return false;
            return altitude < body.atmosphereDepth;
        }

        #endregion

        #region S3 — launch dust

        /// <summary>Above this height above ground level no dust is raised at all.</summary>
        internal const float LaunchDustMaxAglMeters = 40f;
        internal const float LaunchDustEmissionMax = 260f;
        internal const float LaunchDustSizeMin = 1.5f;
        internal const float LaunchDustSizeMax = 5f;
        /// <summary>Below this the plume is not moving enough air to be worth a particle system.</summary>
        internal const float LaunchDustMinIntensity = 0.02f;
        /// <summary>How far into the recording to look for a usable ground reference before giving up.</summary>
        internal const int LaunchDustGroundLatchScanCap = 2000;

        /// <summary>
        /// Sum of every engine module's commanded power on a ghost. Used as the dust driver's
        /// cheapest-first gate: no lit engine, no dust, and nothing below this is evaluated.
        /// </summary>
        internal static float SumEnginePower(GhostPlaybackState state)
        {
            if (state?.engineInfos == null) return 0f;
            float total = 0f;
            foreach (var kvp in state.engineInfos)
            {
                EngineGhostInfo info = kvp.Value;
                if (info == null || info.currentPower <= 0f) continue;
                total += info.currentPower;
            }
            return total;
        }

        /// <summary>
        /// Latches the sea-level altitude of the ground under the recording's launch site, from the
        /// first trajectory point that carries a finite <c>recordedGroundClearance</c>.
        ///
        /// SCALARS ONLY, and the RECORDER GATE is what makes reading them off a flat
        /// <c>Recording.Points</c> list safe. <c>FlightRecorder.ShouldEmitSurfaceClearance</c> only
        /// populates <c>recordedGroundClearance</c> on an ABSOLUTE, surface-environment, PQS-backed
        /// sample, so the finite-clearance test below is simultaneously the "this point's
        /// <c>altitude</c> is a real altitude-above-datum" test: a RELATIVE-section point, whose
        /// altitude field holds anchor-local METRES, carries NaN clearance and is skipped. That is
        /// the invariant to preserve if the gate ever widens — it is not true a priori that these two
        /// fields mean the same thing in every frame. <c>latitude</c> / <c>longitude</c> are NEVER
        /// read here at all, for the same reason in its sharper form: treating anchor-local metres as
        /// a body-fixed position puts the answer inside the planet.
        ///
        /// NaN clearance everywhere answers false, and the caller emits no dust. That is the honest
        /// degradation: an invented ground reference would put a dust cloud around a ghost in orbit.
        /// </summary>
        internal static bool TryLatchLaunchDustGroundReference(
            List<TrajectoryPoint> points, out double groundRefAltitude)
        {
            groundRefAltitude = double.NaN;
            if (points == null || points.Count == 0) return false;

            int scanned = Math.Min(points.Count, LaunchDustGroundLatchScanCap);
            for (int i = 0; i < scanned; i++)
            {
                double clearance = points[i].recordedGroundClearance;
                double altitude = points[i].altitude;
                if (double.IsNaN(clearance) || double.IsInfinity(clearance)) continue;
                if (double.IsNaN(altitude) || double.IsInfinity(altitude)) continue;
                groundRefAltitude = altitude - clearance;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The dust intensity (0..1) for one frame. Gate order is cheapest-first by design: engine
        /// power (a dictionary walk over a handful of entries), then the latched ground reference
        /// (one NaN test), then the AGL window.
        /// </summary>
        internal static bool TryComputeLaunchDustIntensity(
            float totalEnginePower, double altitude, double groundRefAltitude, out float intensity)
        {
            intensity = 0f;
            if (float.IsNaN(totalEnginePower) || totalEnginePower <= 0f) return false;
            if (double.IsNaN(groundRefAltitude) || double.IsInfinity(groundRefAltitude)) return false;
            if (double.IsNaN(altitude) || double.IsInfinity(altitude)) return false;

            double agl = altitude - groundRefAltitude;
            // A modest negative AGL is terrain-model disagreement between record and playback, not
            // a ghost underground; a large one means the reference belongs to somewhere else.
            if (agl < -50.0 || agl >= LaunchDustMaxAglMeters) return false;
            if (agl < 0.0) agl = 0.0;

            float proximity = (float)(1.0 - agl / LaunchDustMaxAglMeters);
            intensity = Math.Min(1f, totalEnginePower * proximity);
            return intensity > LaunchDustMinIntensity;
        }

        #endregion
    }
}
