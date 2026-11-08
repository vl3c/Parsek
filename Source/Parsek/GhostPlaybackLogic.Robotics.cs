using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Robotic part poses, wheel spin from ground speed and wheel steering.
    internal static partial class GhostPlaybackLogic
    {
        #region Robotic

        internal static float ComputeRotorDeltaDegrees(float rpm, double deltaSeconds)
        {
            if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds <= 0)
                return 0f;
            if (float.IsNaN(rpm) || float.IsInfinity(rpm) || Mathf.Abs(rpm) <= 0.0001f)
                return 0f;

            // RPM * 360deg / 60s
            return rpm * 6f * (float)deltaSeconds;
        }

        #region Wheel spin derived from ground speed

        /// <summary>
        /// Fallback rolling radius used when <c>ModuleWheelBase.radius</c> cannot be read off the
        /// prefab. 0.35 m sits in the middle of the stock rover-wheel range (RoveMax S2 is smaller,
        /// XL3 larger) so a wheel that misses its radius spins at a plausible rate rather than a
        /// wild one. Only the visual rate depends on it — nothing physical.
        /// </summary>
        internal const float DefaultWheelRadiusMeters = 0.35f;

        /// <summary>Radii at or below this are treated as unusable (divide-by-zero guard).</summary>
        internal const float MinWheelRadiusMeters = 0.01f;

        /// <summary>
        /// Ground speeds below this (m/s) count as stopped, so a parked ghost's wheels hold still
        /// instead of creeping on interpolation noise.
        /// </summary>
        internal const float WheelStationarySpeedMetersPerSecond = 0.05f;

        /// <summary>
        /// Strips the radial (climb/sink) component out of a ghost's surface velocity, leaving
        /// velocity along the local surface - i.e. ground speed as a vector. Removing the
        /// component along <paramref name="up"/> drops vertical motion, so a falling or hovering
        /// craft does not spin its wheels.
        ///
        /// The input is the position-derived surface velocity
        /// (<see cref="GhostPlaybackState.lastInterpolatedSurfaceVelocity"/>), never the recorded
        /// <c>TrajectoryPoint.velocity</c>: that vector is ALREADY surface-relative for a landed
        /// or low vessel (the world co-rotates below the body's inverse-rotation threshold), so
        /// subtracting the body's rotating-frame velocity from it would invent ~175 m/s of ground
        /// speed on a rover parked at KSC.
        /// </summary>
        internal static Vector3 ComputeSurfaceHorizontalVelocity(Vector3 surfaceVelocity, Vector3 up)
        {
            if (up.sqrMagnitude <= 1e-8f)
                return surfaceVelocity;

            Vector3 upUnit = up.normalized;
            return surfaceVelocity - upUnit * Vector3.Dot(surfaceVelocity, upUnit);
        }

        /// <summary>
        /// The world direction a wheel rolls toward when it spins POSITIVELY about
        /// <paramref name="wheelSpinAxisWorld"/>, given the local surface normal
        /// <paramref name="up"/>. Returns <see cref="Vector3.zero"/> when the axis is degenerate or
        /// parallel to up (a wheel lying flat has no rolling direction).
        ///
        /// Derivation, in Unity's left-handed convention where
        /// <c>Quaternion.AngleAxis(+t, a)</c> moves a point at offset <c>v</c> toward
        /// <c>Cross(a, v)</c> (check: <c>AngleAxis(90, up) * forward == right</c>):
        /// rolling without slip means the contact point, at offset <c>-up * R</c> from the hub, is
        /// stationary against the ground, so the hub velocity is
        /// <c>V = t' * Cross(a, up * R) = t' * R * Cross(axis, up)</c>. Hence the positive-spin
        /// travel direction is <c>Cross(axis, up)</c>.
        ///
        /// Taking the sign from the wheel's OWN axis rather than a guessed vessel forward is what
        /// makes reverse read as reverse, and it stays correct for wheels mirrored onto opposite
        /// sides of a rover: their local axes point opposite ways, and both must counter-rotate to
        /// travel the same way, which is exactly what the cross product yields.
        ///
        /// UNVERIFIED STEP — the one thing here that unit tests cannot settle. Everything else about
        /// this path is proven headless (rate = speed/radius, reverse is the negative of forward,
        /// sub-threshold speed holds still, sideways slide does not roll), but the ABSOLUTE visual
        /// direction depends on the Unity handedness identity quoted above, and
        /// <c>Quaternion.AngleAxis</c> is a native call that throws outside a Unity runtime, so the
        /// identity cannot be pinned by a test. If wheels visibly spin BACKWARDS while a rover drives
        /// forwards in game, the fix is to negate this one cross product —
        /// <c>Cross(up, axis)</c> instead of <c>Cross(axis, up)</c> — and nothing else.
        /// </summary>
        internal static Vector3 ComputeWheelRollForward(Vector3 wheelSpinAxisWorld, Vector3 up)
        {
            if (wheelSpinAxisWorld.sqrMagnitude <= 1e-8f || up.sqrMagnitude <= 1e-8f)
                return Vector3.zero;

            Vector3 rollForward = Vector3.Cross(wheelSpinAxisWorld.normalized, up.normalized);
            if (rollForward.sqrMagnitude <= 1e-8f)
                return Vector3.zero;

            return rollForward.normalized;
        }

        /// <summary>
        /// Signed wheel rotation for this frame, in degrees, from horizontal ground velocity.
        /// Magnitude is <c>speed / radius</c> (rad/s) converted to degrees; sign is the projection
        /// of the velocity onto <paramref name="rollForward"/>, so driving backwards rotates
        /// backwards. Returns 0 for a zero/invalid dt, an unusable radius, a degenerate roll
        /// direction, or a speed under <see cref="WheelStationarySpeedMetersPerSecond"/> — the
        /// stationary case, where a coasting-or-parked wheel must simply hold still.
        ///
        /// Replaces the recorded-<c>driveOutput</c> path, which produced the exact opposite
        /// behaviour: zero (stationary wheels) whenever the rover was coasting with no motor input,
        /// and an unsigned torque percent when it was not.
        /// </summary>
        internal static float ComputeWheelSpinDeltaDegrees(
            Vector3 horizontalSurfaceVelocity, Vector3 rollForward, float wheelRadius, double deltaSeconds)
        {
            if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds <= 0)
                return 0f;
            if (float.IsNaN(wheelRadius) || float.IsInfinity(wheelRadius) ||
                wheelRadius <= MinWheelRadiusMeters)
                return 0f;
            if (rollForward.sqrMagnitude <= 1e-8f)
                return 0f;

            float signedSpeed = Vector3.Dot(horizontalSurfaceVelocity, rollForward);
            if (float.IsNaN(signedSpeed) || float.IsInfinity(signedSpeed))
                return 0f;
            if (Mathf.Abs(signedSpeed) < WheelStationarySpeedMetersPerSecond)
                return 0f;

            // rad/s = v / r  ->  deg for this frame.
            return signedSpeed / wheelRadius * Mathf.Rad2Deg * (float)deltaSeconds;
        }

        /// <summary>
        /// Ground-contact test for a single recorded environment classification: only the two
        /// SURFACE classes mean the wheels are touching something they can roll on.
        ///
        /// Why this is the whole test, and why the altitude classes are not on the list: the
        /// recorder's own classifier (<see cref="EnvironmentDetector.Classify"/>) resolves
        /// LANDED / SPLASHED / PRELAUNCH straight to
        /// <see cref="SegmentEnvironment.SurfaceMobile"/> / <see cref="SegmentEnvironment.SurfaceStationary"/>
        /// before it ever looks at altitude, and it debounces the jitter (#246). So a rover driving,
        /// a plane on its takeoff roll, and a plane that has just touched down are all Surface; the
        /// instant the wheels leave the ground KSP reports FLYING and the section becomes
        /// Atmospheric. That boundary IS the wheels-on-ground boundary, already measured by the
        /// recorder, at no per-frame cost here.
        ///
        /// <see cref="SegmentEnvironment.Atmospheric"/> is deliberately NOT ground contact even
        /// though a wheel would keep freewheeling for a second after liftoff: holding still is the
        /// honest answer when nothing in the recording says the wheel is loaded, and it is the answer
        /// that keeps a rover riding a launch vehicle from spinning its wheels up the whole ascent.
        /// <see cref="SegmentEnvironment.Approach"/> is likewise excluded — on an airless body the
        /// classifier already promotes anything under 100 m AGL to Surface, so Approach means "not
        /// near the ground yet".
        /// </summary>
        internal static bool IsWheelSpinGroundContactEnvironment(SegmentEnvironment env)
        {
            return env == SegmentEnvironment.SurfaceMobile
                || env == SegmentEnvironment.SurfaceStationary;
        }

        /// <summary>
        /// Resolves whether a ghost's wheels are on the ground at <paramref name="playbackUT"/>, via
        /// the recorded <see cref="TrackSection"/> environment covering that UT, memoised in
        /// <paramref name="memo"/> for the resolved section's own span.
        ///
        /// FAILS CLOSED (returns false, wheels hold still) when no section covers the UT. That
        /// covers every "we do not actually know" shape and each of them is a case where spinning
        /// would be wrong, not right: a BG on-rails recording emits no env-classified per-frame
        /// sections at all (it is in orbit); a re-aimed trajectory presents an empty section list by
        /// contract (it is on a heliocentric transfer); an endpoint / loop-pause hold sits outside
        /// the recorded span (and zeroes the interpolated velocity anyway).
        ///
        /// The memo window is the resolved section's [startUT, endUT). That is only a correct cache
        /// because sections partition the recorded timeline without overlapping — each section's
        /// endUT is the next one's startUT — so no other section can be the answer inside the window.
        /// A malformed overlapping list could return the wrong cached class for the overlap; nothing
        /// produces that shape, and <see cref="TrajectoryMath.FindTrackSectionForUT"/> would already
        /// resolve it first-match-wins.
        /// </summary>
        internal static bool ResolveWheelGroundContact(
            List<TrackSection> sections, double playbackUT, ref WheelGroundContactMemo memo)
        {
            if (memo.hasValue && playbackUT >= memo.startUT && playbackUT < memo.endUT)
                return memo.onGround;

            int idx = TrajectoryMath.FindTrackSectionForUT(sections, playbackUT);
            if (idx < 0)
            {
                memo.hasValue = false;
                return false;
            }

            TrackSection section = sections[idx];
            bool onGround = IsWheelSpinGroundContactEnvironment(section.environment);
            memo.hasValue = true;
            memo.startUT = section.startUT;
            memo.endUT = section.endUT;
            memo.onGround = onGround;
            return onGround;
        }

        #endregion

        private static void ApplyRoboticPose(RoboticGhostInfo info, float value)
        {
            if (info == null || info.servoTransform == null)
                return;

            Vector3 axis = info.axisLocal.sqrMagnitude > 0.0001f
                ? info.axisLocal.normalized
                : Vector3.up;

            if (info.visualMode == RoboticVisualMode.Linear)
            {
                info.servoTransform.localPosition = info.stowedPos + (axis * value);
            }
            else if (info.visualMode == RoboticVisualMode.Rotational)
            {
                info.servoTransform.localRotation =
                    info.stowedRot * Quaternion.AngleAxis(value, axis);
            }
        }

        private static GhostPartEventOutcome ApplyRoboticEvent(
            GhostPlaybackState state, PartEvent evt, double currentUT)
        {
            if (state == null || state.roboticInfos == null)
                return GhostPartEventOutcome.NoFamilyState;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            if (!state.roboticInfos.TryGetValue(key, out RoboticGhostInfo info) || info == null)
                return GhostPartEventOutcome.NoInfoForPart;

            info.currentValue = evt.value;

            if (info.visualMode == RoboticVisualMode.RotorRpm)
            {
                info.active = evt.eventType != PartEventType.RoboticMotionStopped &&
                    Mathf.Abs(evt.value) > 0.0001f;
                info.lastUpdateUT = currentUT;
            }
            else
            {
                ApplyRoboticPose(info, evt.value);
                info.active = evt.eventType != PartEventType.RoboticMotionStopped;
                info.lastUpdateUT = currentUT;
            }

            return GhostPartEventOutcome.Applied;
        }

        /// <param name="trackSections">
        /// The trajectory's recorded track sections, used ONLY by the wheel-spin ground-contact gate
        /// (<see cref="ResolveWheelGroundContact"/>). Null / empty means "no proof of ground contact",
        /// which holds the wheels still; every other robotic visual mode ignores it.
        /// </param>
        internal static void UpdateActiveRobotics(
            GhostPlaybackState state, double currentUT, List<TrackSection> trackSections)
        {
            if (state == null || state.roboticInfos == null || state.roboticInfos.Count == 0)
                return;

            // Wheel-spin inputs are resolved ONCE per ghost per frame, not once per wheel: the
            // ground-contact gate, the body lookup, the surface normal and the ground-speed vector
            // are identical for every wheel on the craft. Only the per-wheel roll direction and
            // radius vary below. Resolution is also lazy — a ghost with no WheelGroundSpeed wheels
            // pays nothing, and a ghost whose wheels are off the ground pays only the memoised
            // section lookup (two double compares on a steady-state frame).
            bool wheelInputsResolved = false;
            bool wheelInputsUsable = false;
            Vector3 wheelUp = Vector3.zero;
            Vector3 wheelHorizontalVelocity = Vector3.zero;
            // S3 wheel steering shares those same once-per-ghost inputs; the heading rate on top of
            // them is also once-per-ghost (every steered wheel on a craft turns off one heading).
            bool steeringRateResolved = false;
            float steeringHeadingRate = 0f;

            foreach (var kv in state.roboticInfos)
            {
                RoboticGhostInfo info = kv.Value;
                if (info == null || info.servoTransform == null)
                    continue;

                if (double.IsNaN(info.lastUpdateUT) || double.IsInfinity(info.lastUpdateUT))
                {
                    info.lastUpdateUT = currentUT;
                    continue;
                }

                double deltaSeconds = currentUT - info.lastUpdateUT;
                if (deltaSeconds <= 0)
                {
                    info.lastUpdateUT = currentUT;
                    continue;
                }

                // Timeline jumps/loop boundaries rebuild ghosts, but guard large UT gaps anyway.
                deltaSeconds = Math.Min(deltaSeconds, 1.0);

                if (info.visualMode == RoboticVisualMode.RotorRpm && info.active)
                {
                    float deltaDegrees = ComputeRotorDeltaDegrees(info.currentValue, deltaSeconds);
                    if (Mathf.Abs(deltaDegrees) > 0.0001f)
                    {
                        Vector3 axis = info.axisLocal.sqrMagnitude > 0.0001f
                            ? info.axisLocal.normalized
                            : Vector3.up;
                        info.servoTransform.localRotation =
                            info.servoTransform.localRotation * Quaternion.AngleAxis(deltaDegrees, axis);
                    }
                }
                else if (info.visualMode == RoboticVisualMode.WheelGroundSpeed)
                {
                    if (!wheelInputsResolved)
                    {
                        wheelInputsResolved = true;
                        // GROUND-CONTACT GATE first, before the body lookup and ground-speed work. A
                        // rover carried to orbit by a launch vehicle keeps its recorded orbital
                        // velocity (~2100 m/s on a Kerbin parking orbit); ungated, speed/radius
                        // turns that into thousands of degrees per frame and the wheels strobe for
                        // the whole ascent and coast. The old recorded signal never showed this
                        // because driveOutput was 0 with no motor input — it was right here by
                        // accident, which makes an ungated derivation a NEW artifact, on old
                        // recordings too. Nothing below the gate is reached off the ground.
                        bool onGround = ResolveWheelGroundContact(
                            trackSections, currentUT, ref state.wheelGroundContact);
                        wheelInputsUsable = onGround
                            && TryResolveWheelGroundSpeedInputs(
                                state, out wheelUp, out wheelHorizontalVelocity);
                        LogWheelGroundContactDecision(state, currentUT, onGround);
                    }

                    // A broken wheel does not roll (stock puts it on the wheel's inoperable list), and
                    // its busted mesh may sit under the spin transform, so the ghost does not spin it.
                    if (wheelInputsUsable && !IsGhostWheelDamaged(state, info.partPersistentId))
                    {
                        Vector3 spinAxisWorld = info.servoTransform.TransformDirection(
                            info.axisLocal.sqrMagnitude > 0.0001f ? info.axisLocal.normalized : Vector3.right);
                        Vector3 rollForward = ComputeWheelRollForward(spinAxisWorld, wheelUp);
                        float deltaDegrees = ComputeWheelSpinDeltaDegrees(
                            wheelHorizontalVelocity, rollForward, info.wheelRadius, deltaSeconds);
                        if (Mathf.Abs(deltaDegrees) > 0.0001f)
                        {
                            Vector3 axis = info.axisLocal.sqrMagnitude > 0.0001f
                                ? info.axisLocal.normalized
                                : Vector3.right;
                            info.servoTransform.localRotation =
                                info.servoTransform.localRotation * Quaternion.AngleAxis(deltaDegrees, axis);
                        }
                    }
                }
                else if (info.visualMode == RoboticVisualMode.WheelSteeringHeading)
                {
                    if (!wheelInputsResolved)
                    {
                        wheelInputsResolved = true;
                        bool onGroundForSteering = ResolveWheelGroundContact(
                            trackSections, currentUT, ref state.wheelGroundContact);
                        wheelInputsUsable = onGroundForSteering
                            && TryResolveWheelGroundSpeedInputs(
                                state, out wheelUp, out wheelHorizontalVelocity);
                        LogWheelGroundContactDecision(state, currentUT, onGroundForSteering);
                    }

                    if (!steeringRateResolved)
                    {
                        steeringRateResolved = true;
                        steeringHeadingRate = ResolveGhostHeadingRateDegPerSec(
                            state, currentUT, wheelInputsUsable, wheelUp, wheelHorizontalVelocity);
                    }

                    // Off the ground, parked, or below the crawl threshold: heading rate is 0 and
                    // this eases the calipers back to straight rather than freezing them mid-turn.
                    //
                    // The rate is negated on the way IN because ComputeSynthDeflectionDegrees
                    // inverts (a control deflection OPPOSES the body rate it produced). Steering is
                    // the one family where the deflection and the rate share a sign — the wheels
                    // point INTO the turn — so the two negations cancel. Shared clamp/deadband/NaN
                    // handling is worth the one confusing sign.
                    float targetSteering = ComputeTargetWheelSteeringDegrees(steeringHeadingRate);
                    info.steeringAngleDegrees = SlewTowardDegrees(
                        info.steeringAngleDegrees, targetSteering,
                        WheelSteeringSlewDegPerSec, deltaSeconds);

                    Vector3 steerAxis = info.axisLocal.sqrMagnitude > 0.0001f
                        ? info.axisLocal.normalized
                        : Vector3.up;
                    info.servoTransform.localRotation =
                        info.stowedRot * Quaternion.AngleAxis(info.steeringAngleDegrees, steerAxis);
                }

                info.lastUpdateUT = currentUT;
            }
        }

        /// <summary>
        /// The wheel-caliper target angle for a ground-track heading rate. Extracted from the
        /// ghost wheel-steering drive so the CALLER-SIDE negate is testable without a servo
        /// transform: the rate is negated on the way in because
        /// <see cref="ComputeSynthDeflectionDegrees"/> inverts (a control deflection OPPOSES the
        /// body rate it produced), and steering is the one family where deflection and rate share
        /// a sign - the wheels point INTO the turn - so the two negations cancel. Byte-identical
        /// to the inline expression it replaces.
        /// </summary>
        internal static float ComputeTargetWheelSteeringDegrees(float headingRateDegPerSec)
        {
            return ComputeSynthDeflectionDegrees(
                -headingRateDegPerSec, WheelSteeringGainDegPerDegPerSec, MaxWheelSteeringDegrees);
        }

        /// <summary>Degrees of caliper angle per deg/s of ground-track heading change.</summary>
        internal const float WheelSteeringGainDegPerDegPerSec = 1.5f;
        /// <summary>Stock rover calipers top out near 30 degrees; clamp there.</summary>
        internal const float MaxWheelSteeringDegrees = 30f;
        internal const float WheelSteeringSlewDegPerSec = 60f;
        /// <summary>Below this ground speed the heading of a velocity vector is noise, not a direction.</summary>
        internal const float MinWheelSteeringGroundSpeed = 0.2f;
        internal const float WheelSteeringHeadingEmaAlpha = 0.3f;

        /// <summary>
        /// Once-per-ghost-per-frame heading rate for wheel steering, EMA-smoothed. Returns 0 (wheels
        /// straight) whenever the wheel inputs are unusable or the ghost is below a crawl, and
        /// re-seeds the heading memo so the next moving frame differences against a fresh sample
        /// rather than one from before a stop.
        /// </summary>
        private static float ResolveGhostHeadingRateDegPerSec(
            GhostPlaybackState state, double currentUT,
            bool inputsUsable, Vector3 up, Vector3 horizontalVelocity)
        {
            if (!inputsUsable || horizontalVelocity.magnitude < MinWheelSteeringGroundSpeed)
            {
                state.prevGroundHeadingUT = double.NaN;
                state.prevGroundHeading = Vector3.zero;
                state.smoothedHeadingRateDegPerSec = 0f;
                return 0f;
            }

            Vector3 heading = horizontalVelocity.normalized;
            if (double.IsNaN(state.prevGroundHeadingUT) || double.IsInfinity(state.prevGroundHeadingUT))
            {
                state.prevGroundHeading = heading;
                state.prevGroundHeadingUT = currentUT;
                return 0f;
            }

            double dt = currentUT - state.prevGroundHeadingUT;
            if (dt <= 0.0 || dt > MaxSynthSampleSeconds)
            {
                // Same reasoning as UpdateSynthesizedMotion: re-seed rather than cap the
                // denominator, so a warp gap cannot manufacture a huge heading rate and lock the
                // calipers at full lock on a rover that is barely turning.
                //
                // But DECAY the smoothed rate instead of returning it verbatim. Under sustained warp
                // every frame lands in this branch, and a held rate would hold the calipers at
                // whatever angle the last pre-warp turn commanded — a rover frozen mid-turn for as
                // long as the warp lasts, which is the one state a player is most likely to see.
                // Decaying by the same EMA weight a real zero sample would carry lets the existing
                // slew ease them straight, and the first sub-second frame re-acquires the true rate.
                state.prevGroundHeading = heading;
                state.prevGroundHeadingUT = currentUT;
                state.smoothedHeadingRateDegPerSec = DecayRateTowardZero(
                    state.smoothedHeadingRateDegPerSec, WheelSteeringHeadingEmaAlpha);
                return state.smoothedHeadingRateDegPerSec;
            }

            float sample = ComputeHeadingRateDegPerSec(state.prevGroundHeading, heading, up, dt);
            state.smoothedHeadingRateDegPerSec =
                state.smoothedHeadingRateDegPerSec * (1f - WheelSteeringHeadingEmaAlpha)
                + sample * WheelSteeringHeadingEmaAlpha;
            state.prevGroundHeading = heading;
            state.prevGroundHeadingUT = currentUT;
            return state.smoothedHeadingRateDegPerSec;
        }

        /// <summary>
        /// Resolves the two per-ghost inputs a wheel spin needs: the local surface normal and the
        /// ghost's horizontal ground velocity. Returns false (wheels hold still) when the ghost has
        /// no position, no resolved body, or no interpolated velocity yet.
        ///
        /// The body comes from <c>state.lastInterpolatedBodyName</c> through the same
        /// name-keyed cache the audio and watch-mode camera paths already use, so an SOI change
        /// re-resolves and steady flight costs a string compare rather than a scan of
        /// <c>FlightGlobals.Bodies</c>. The steady-state path allocates nothing; only a cache MISS
        /// pays for the <c>Find</c> predicate closure, and a miss happens once per body per ghost
        /// (first frame and SOI changes), not per frame. This is the same trade
        /// <c>ComputeAtmosphereFactor</c> already makes against the same cache fields.
        ///
        /// The ground speed is <see cref="GhostPlaybackState.lastInterpolatedSurfaceVelocity"/>,
        /// derived from body-fixed positions. An unknown surface velocity (source None: single
        /// sample, long gap, a RELATIVE section without body-fixed frames) lands on the honest
        /// answer here: no known ground speed means stationary wheels.
        /// </summary>
        /// <summary>
        /// True while the ghost shows this part's wheel as broken (a WheelBroken event, seed or
        /// snapshot baseline applied and no repair since). Pure dictionary read.
        /// </summary>
        internal static bool IsGhostWheelDamaged(GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.wheelDamageInfos == null) return false;
            WheelDamageGhostInfo info;
            return state.wheelDamageInfos.TryGetValue(partPersistentId, out info)
                && info != null && info.damaged;
        }

        private static bool TryResolveWheelGroundSpeedInputs(
            GhostPlaybackState state, out Vector3 up, out Vector3 horizontalVelocity)
        {
            up = Vector3.zero;
            horizontalVelocity = Vector3.zero;

            if (state.ghost == null)
                return false;

            string bodyName = state.lastInterpolatedBodyName;
            if (string.IsNullOrEmpty(bodyName))
                return false;

            CelestialBody body = state.cachedAudioBody;
            if (body == null || state.cachedAudioBodyName != bodyName)
            {
                body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                state.cachedAudioBody = body;
                state.cachedAudioBodyName = bodyName;
            }
            if (body == null)
                return false;

            bool usable = TryComputeWheelGroundInputs(
                state.ghost.transform.position, body.position,
                state.lastInterpolatedSurfaceVelocity, state.lastInterpolatedSurfaceVelocitySource,
                out up, out horizontalVelocity);
            LogWheelGroundSpeed(state, usable, horizontalVelocity);
            return usable;
        }

        /// <summary>
        /// Pure core of <see cref="TryResolveWheelGroundSpeedInputs"/>: the local surface normal
        /// and the horizontal ground velocity from the ghost and body world positions and the
        /// ghost's surface velocity. False (wheels hold still) when the surface velocity is
        /// unknown or the ghost sits at the body centre. A known zero surface velocity (a parked
        /// rover) returns true with a zero ground velocity, so the wheels hold still for a
        /// MEASURED reason.
        /// </summary>
        internal static bool TryComputeWheelGroundInputs(
            Vector3d ghostWorldPos, Vector3d bodyWorldPos,
            Vector3 surfaceVelocity, GhostSurfaceVelocitySource source,
            out Vector3 up, out Vector3 horizontalVelocity)
        {
            up = Vector3.zero;
            horizontalVelocity = Vector3.zero;
            if (source == GhostSurfaceVelocitySource.None)
                return false;

            up = (Vector3)(ghostWorldPos - bodyWorldPos);
            if (up.sqrMagnitude <= 1e-8f)
                return false;

            horizontalVelocity = ComputeSurfaceHorizontalVelocity(surfaceVelocity, up);
            return true;
        }

        /// <summary>
        /// Rate-limited per ghost: the ground speed the wheels are driven at and where it came
        /// from, so a parked rover's ghost can be shown to read ~0 m/s from KSP.log. The key
        /// carries the source, so a change of source prints at once.
        /// </summary>
        private static void LogWheelGroundSpeed(
            GhostPlaybackState state, bool usable, Vector3 horizontalVelocity)
        {
            if (!ParsekLog.IsVerboseEnabled)
                return;
            string id = !string.IsNullOrEmpty(state.recordingId)
                ? state.recordingId
                : state.vesselName ?? "?";
            string source = usable
                ? GhostSurfaceVelocity.SourceToken(state.lastInterpolatedSurfaceVelocitySource)
                : "none";
            ParsekLog.VerboseRateLimited("Flight",
                "wheel-ground-speed-" + id + "-" + source,
                FormatWheelGroundSpeedLine(state.vesselName, id, horizontalVelocity.magnitude, source),
                30.0);
        }

        /// <summary>The wheel ground-speed diagnostic line; invariant formatting.</summary>
        internal static string FormatWheelGroundSpeedLine(
            string vesselName, string recordingId, float speedMetersPerSecond, string source)
        {
            return "Wheel ground speed for \"" + (vesselName ?? "?") + "\" rec=" + (recordingId ?? "?")
                + " speed=" + speedMetersPerSecond.ToString("F2", CultureInfo.InvariantCulture)
                + " source=" + (source ?? "none");
        }

        /// <summary>
        /// One Verbose line per ghost per ground-contact ANSWER, so a "wheels spun in orbit" or
        /// "wheels never turned" report is decidable from KSP.log. The rate-limit key carries the
        /// decision, so a flip emits immediately (new key, no prior timestamp) and a steady answer
        /// re-affirms at most once a minute rather than every frame.
        /// </summary>
        private static void LogWheelGroundContactDecision(
            GhostPlaybackState state, double currentUT, bool onGround)
        {
            string id = !string.IsNullOrEmpty(state.recordingId)
                ? state.recordingId
                : state.vesselName ?? "?";
            ParsekLog.VerboseRateLimited("Flight",
                $"wheel-ground-contact-{id}-{(onGround ? 1 : 0)}",
                $"Wheel spin ground-contact gate {(onGround ? "OPEN" : "CLOSED")} for \"{state.vesselName}\" " +
                $"rec={id} ut={currentUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"section={(state.wheelGroundContact.hasValue ? "resolved" : "none")}" +
                (onGround ? "" : "; wheels hold still"),
                60.0);
        }

        #endregion
    }
}
