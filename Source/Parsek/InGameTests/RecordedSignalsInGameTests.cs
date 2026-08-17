using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Category <c>RecordedSignals</c> — the live half of the 2026-08-09 part-action recording
    /// audit fixes (parachute repack, wheel spin derived from ground speed). Every claim these
    /// cells make is one the headless suite CANNOT make:
    ///
    ///   1. <see cref="WheelRollForwardMatchesUnityHandedness"/> — THE STAR CELL, and the one
    ///      genuinely unverifiable step in the whole wheel fix. The absolute visual spin direction
    ///      rests on Unity's left-handed <c>Quaternion.AngleAxis</c> identity, and
    ///      <c>Quaternion.AngleAxis</c> is a native call that throws outside a Unity runtime, so no
    ///      xUnit cell can pin it. This one can.
    ///   2. <see cref="ParachuteRepackRestoresTheCapAtTransformLevel"/> — the repack visual is a
    ///      <c>GameObject.SetActive</c> on a transform cloned out of a real PartLoader prefab. The
    ///      headless suite proves the ENCODING (cut vs repack) and the DECISION
    ///      (<c>TryResolveParachuteCapActive</c>); only a live scene proves the transform moved.
    ///   3. <see cref="WheelSpinNeedsGroundContactAndGroundMotion"/> — the spin is applied by
    ///      <c>Transform.localRotation *= AngleAxis(...)</c> against a real wheel prefab's own spin
    ///      axis and radius, through the live body-rotation subtraction
    ///      (<c>CelestialBody.getRFrmVel</c>). None of that exists headless.
    ///
    /// House rules honoured here: a cell that cannot self-set-up calls
    /// <see cref="InGameAssert.Skip"/> naming the context it needed (never asserts against an
    /// assumed one), and any assertion measured in METRES sizes its tolerance from
    /// <see cref="InGameFixtureMath.SceneFloatGridToleranceMeters"/> rather than a fixed epsilon.
    /// Both fixtures are built from PartLoader prefabs discovered at run time, not from hardcoded
    /// part names, so the batch does not depend on which craft the save happens to be flying.
    /// </summary>
    public class RecordedSignalsInGameTests
    {
        private readonly InGameTestRunner runner;
        public RecordedSignalsInGameTests(InGameTestRunner runner) { this.runner = runner; }

        /// <summary>Ghost part pid for the single-part fixtures. Mirrors
        /// <c>VesselSnapshotBuilder.AddPart</c>'s first slot (100000 + 0 * 1111), which is the pid a
        /// single-part showcase ghost's events must carry or playback lookup silently misses.</summary>
        private const uint FixturePartPid = 100000u;

        // ──────────────────────────────────────────────────────────────────────
        //  1. The handedness proof
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// THE ONE THING HEADLESS CANNOT VERIFY. <c>ComputeWheelRollForward</c> returns
        /// <c>Cross(spinAxis, up)</c> as the direction a wheel travels when spun POSITIVELY about
        /// its axis, and <c>UpdateActiveRobotics</c> feeds the resulting signed degrees straight into
        /// <c>Quaternion.AngleAxis(deltaDegrees, axis)</c>. The derivation is only right if Unity's
        /// convention is that <c>AngleAxis(+t, a)</c> moves a point at offset <c>v</c> toward
        /// <c>Cross(a, v)</c> — stated in the doc comment as
        /// <c>AngleAxis(90, up) * forward == right</c>. <c>Quaternion.AngleAxis</c> is a native ECall
        /// that throws outside a Unity runtime, so xUnit can prove the magnitude, the sign relative
        /// to the roll direction, the stationary threshold and the sideways-slide case, but never
        /// this. Hence this cell.
        ///
        /// IF THIS CELL FAILS, the documented fix is to negate the single cross product in
        /// <c>GhostPlaybackLogic.ComputeWheelRollForward</c> — <c>Cross(up, axis)</c> instead of
        /// <c>Cross(axis, up)</c> — and nothing else. Do not "fix" it by flipping the sign inside
        /// <c>ComputeWheelSpinDeltaDegrees</c>: that would also invert the reverse-is-negative
        /// contract the headless cells pin.
        /// </summary>
        [InGameTest(Category = "RecordedSignals", Scene = InGameTestAttribute.AnyScene,
            Description = "Unity's AngleAxis handedness matches the Cross(axis, up) wheel-roll derivation")]
        public void WheelRollForwardMatchesUnityHandedness()
        {
            // (1) The bare identity the derivation quotes. Unit vectors, so a direction epsilon is
            //     the right instrument here — the metre-scaled grid tolerance applies to the
            //     displacement assertions in step (3), where the quantity really is in metres.
            Vector3 rotatedForward = Quaternion.AngleAxis(90f, Vector3.up) * Vector3.forward;
            InGameAssert.ApproxEqual(Vector3.right, rotatedForward, 1e-3f,
                "Unity's AngleAxis convention changed: AngleAxis(90, up) * forward must be right. " +
                "The whole Cross(axis, up) wheel-roll derivation rests on this identity - see " +
                "GhostPlaybackLogic.ComputeWheelRollForward.");

            // (2) The composed direction, for the canonical wheel: spin axis +x, surface normal +y.
            //     Cross(right, up) == forward, so a wheel whose axis points +x travels +z when spun
            //     positively about it.
            Vector3 axis = Vector3.right;
            Vector3 up = Vector3.up;
            Vector3 rollForward = GhostPlaybackLogic.ComputeWheelRollForward(axis, up);
            InGameAssert.ApproxEqual(Vector3.forward, rollForward, 1e-3f,
                "ComputeWheelRollForward(right, up) must be forward");

            // (3) THE COMPOSED PROOF, and the part that actually rules out a mirrored spin. Take the
            //     rotation UpdateActiveRobotics would apply for forward motion and check what it does
            //     to the wheel's rim. Rolling without slip means the CONTACT point (at -up * R from
            //     the hub) is momentarily stationary against the ground while the hub advances along
            //     rollForward - so under that rotation the TOP of the wheel must move ALONG
            //     rollForward and the contact patch AGAINST it. A negated cross product reverses
            //     both signs, which is exactly the in-game symptom ("wheels spin backwards").
            const float radiusMeters = 1f;
            float deltaDegrees = GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(
                rollForward * 10f, rollForward, radiusMeters, 0.02);
            InGameAssert.IsGreaterThan(deltaDegrees, 0.0,
                "Driving ALONG rollForward must produce a positive spin delta; the sign convention " +
                "below is stated against that.");

            Quaternion spin = Quaternion.AngleAxis(deltaDegrees, axis);
            Vector3 topBefore = up * radiusMeters;
            Vector3 contactBefore = -up * radiusMeters;
            float topTravel = Vector3.Dot(spin * topBefore - topBefore, rollForward);
            float contactTravel = Vector3.Dot(spin * contactBefore - contactBefore, rollForward);

            // Metres, so the tolerance is grid-derived. These offsets are LOCAL (a 1 m rim, not a
            // scene position), so the grid step is tiny and the floor dominates - which is the
            // honest reading, and ToleranceResolvesSignal is what refuses the assertion if the
            // travel is too small to out-resolve it rather than passing vacuously.
            double tolerance = InGameFixtureMath.SceneFloatGridToleranceMeters(radiusMeters);
            if (!InGameFixtureMath.ToleranceResolvesSignal(tolerance, Mathf.Abs(topTravel)))
            {
                InGameAssert.Skip(
                    "rim travel " + topTravel.ToString("R") + " m over one 0.02 s frame does not " +
                    "out-resolve the " + tolerance.ToString("R") + " m float tolerance; raise the " +
                    "fixture speed or the frame length");
                return;
            }

            InGameAssert.IsGreaterThan(topTravel, tolerance,
                "Positive spin about the wheel's own axis must carry the TOP of the wheel ALONG " +
                "rollForward. It carried it " + topTravel.ToString("R") + " m. If this is negative, " +
                "negate the single Vector3.Cross in GhostPlaybackLogic.ComputeWheelRollForward " +
                "(Cross(up, axis) instead of Cross(axis, up)) and nothing else.");
            InGameAssert.IsLessThan(contactTravel, -tolerance,
                "...and the CONTACT patch AGAINST it (rolling without slip). Measured " +
                contactTravel.ToString("R") + " m.");

            ParsekLog.Info("TestRunner",
                "RecordedSignals handedness proof: AngleAxis(90,up)*forward=" + rotatedForward +
                " rollForward=" + rollForward + " deltaDeg=" + deltaDegrees.ToString("R") +
                " topTravel=" + topTravel.ToString("R") + "m contactTravel=" +
                contactTravel.ToString("R") + "m tol=" + tolerance.ToString("R") + "m");
        }

        // ──────────────────────────────────────────────────────────────────────
        //  2. Parachute repack, at transform level
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The headline parachute fix, proven where it is visible. A repack recorded as a cut left
        /// the cap hidden with nothing that ever restored it, so the chute rendered as an empty can
        /// for the rest of the recording. Headless cells pin the ENCODING (Cut=3 is distinct from
        /// Stowed=0, CUT -> STOWED classifies as <c>ParachuteRepacked</c>) and the DECISION
        /// (<c>TryResolveParachuteCapActive</c> answers <c>true</c> only for the repack). Neither can
        /// touch a <c>GameObject</c>: the cap is a transform cloned out of a real PartLoader prefab
        /// and the restore is a <c>SetActive(true)</c> on it.
        ///
        /// The CUT step is not scene-setting, it is the contrast that makes the cell a proof: assert
        /// the cap is off after the cut and back on after the repack, so a handler that simply left
        /// the cap alone could not pass.
        /// </summary>
        [InGameTest(Category = "RecordedSignals", Scene = GameScenes.FLIGHT,
            Description = "Deploy -> Cut -> Repacked restores the parachute cap GameObject and the stowed canopy pose")]
        public void ParachuteRepackRestoresTheCapAtTransformLevel()
        {
            string partName = TryFindParachutePartNameWithCap();
            if (partName == null)
            {
                InGameAssert.Skip(
                    "no loaded part carries a ModuleParachute whose canopyName AND capName both " +
                    "resolve to model transforms on the prefab; needs a stock-parachute part in the " +
                    "install (e.g. parachuteSingle) for the cap restore to be observable");
                return;
            }

            double t0 = Planetarium.GetUniversalTime();
            Recording rec = BuildSinglePartFixture(partName, "parsektest-chute-repack", t0);
            rec.PartEvents.Add(ChuteEvent(t0 + 1.0, PartEventType.ParachuteDeployed, partName));
            rec.PartEvents.Add(ChuteEvent(t0 + 2.0, PartEventType.ParachuteCut, partName));
            rec.PartEvents.Add(ChuteEvent(t0 + 3.0, PartEventType.ParachuteRepacked, partName));

            GhostBuildResult build = null;
            try
            {
                build = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                    rec, "ParsekTest_ChuteRepack");
                if (build == null || build.root == null)
                {
                    InGameAssert.Skip("BuildTimelineGhostFromSnapshot produced no ghost for '" +
                        partName + "'");
                    return;
                }

                var state = new GhostPlaybackState { ghost = build.root, vesselName = rec.VesselName };
                GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, build);

                if (state.parachuteInfos == null ||
                    !state.parachuteInfos.TryGetValue(FixturePartPid, out ParachuteGhostInfo info) ||
                    info == null || info.capTransform == null || info.canopyTransform == null)
                {
                    InGameAssert.Skip("the ghost built for '" + partName + "' carries no " +
                        "ParachuteGhostInfo with BOTH a cap and a canopy transform at pid " +
                        FixturePartPid + " (single-part fixtures must use that pid - see the " +
                        "ghost-event/snapshot PID rule)");
                    return;
                }

                // Baseline: a freshly built ghost is stowed - cap on, canopy at zero scale.
                InGameAssert.IsTrue(info.capTransform.gameObject.activeSelf,
                    "a freshly built (stowed) ghost chute must start with its cap ON");

                // Cut: cap OFF. This is the contrast - without it, a no-op repack handler passes.
                GhostPlaybackLogic.ApplyPartEvents(-1, rec, t0 + 2.5, state);
                InGameAssert.IsFalse(info.capTransform.gameObject.activeSelf,
                    "after ParachuteCut the cap must be hidden (this is the state a repack " +
                    "recorded as a cut used to leave behind permanently)");

                // Repack: cap BACK ON, canopy returned to the captured stowed pose.
                GhostPlaybackLogic.ApplyPartEvents(-1, rec, t0 + 3.5, state);
                InGameAssert.IsTrue(info.capTransform.gameObject.activeSelf,
                    "THE FIX: after ParachuteRepacked the cap GameObject must be active again, " +
                    "mirroring stock ModuleParachute.Repack's cap.gameObject.SetActive(true). A " +
                    "hidden cap here is the 'repacked chute renders as an empty can' bug.");
                InGameAssert.ApproxEqual(info.stowedCanopyScale, info.canopyTransform.localScale, 1e-4f,
                    "the canopy must be back at its captured stowed scale (stowed = invisible)");
                InGameAssert.ApproxEqual(info.stowedCanopyPos, info.canopyTransform.localPosition, 1e-3f,
                    "the canopy must be back at its captured stowed position");
                InGameAssert.IsLessThan(
                    Quaternion.Angle(info.stowedCanopyRot, info.canopyTransform.localRotation), 0.5,
                    "the canopy must be back at its captured stowed rotation");

                ParsekLog.Info("TestRunner",
                    "RecordedSignals chute repack: part='" + partName + "' capActive=" +
                    info.capTransform.gameObject.activeSelf + " canopyScale=" +
                    info.canopyTransform.localScale + " stowedScale=" + info.stowedCanopyScale);
            }
            finally
            {
                if (build != null && build.root != null)
                    Object.Destroy(build.root);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  3. Wheel spin: ground motion turns it, nothing else does
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The wheel derivation end to end against a real wheel prefab, including the two things
        /// only a live scene supplies: the wheel's own spin axis and radius read off the prefab, and
        /// the body-rotation subtraction through <c>CelestialBody.getRFrmVel</c>.
        ///
        /// Three arms, and the third is the D1 regression:
        ///   * SURFACE section + ground motion -> the servo transform's rotation CHANGES.
        ///   * SURFACE section + zero velocity -> it does NOT (a parked rover holds still).
        ///   * NON-SURFACE section + orbital velocity -> it does NOT. Ungated, a rover riding a
        ///     launch vehicle spun its wheels at ~2100 m/s for the whole ascent and coast; the old
        ///     recorded signal was accidentally right about that case, so an ungated derivation was
        ///     a NEW artifact.
        ///
        /// The fixture velocity is BUILT from the live inputs (body rotation at the ghost's position
        /// plus a known ground speed along the wheel's own roll direction) rather than assumed, so
        /// the cell measures the same regardless of which craft the batch is flying or where.
        /// </summary>
        [InGameTest(Category = "RecordedSignals", Scene = GameScenes.FLIGHT,
            Description = "Wheel spin follows ground motion, holds still when parked, and holds still off the ground")]
        public void WheelSpinNeedsGroundContactAndGroundMotion()
        {
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null || active.mainBody == null)
            {
                InGameAssert.Skip("no active vessel / main body to anchor the wheel fixture against");
                return;
            }

            string partName = TryFindWheelMotorPartName();
            if (partName == null)
            {
                InGameAssert.Skip(
                    "no loaded part carries a ModuleWheelMotor / ModuleWheelMotorSteering module; " +
                    "needs a stock rover wheel in the install for the spin derivation to be " +
                    "observable");
                return;
            }

            CelestialBody body = active.mainBody;
            double t0 = Planetarium.GetUniversalTime();
            Recording rec = BuildSinglePartFixture(partName, "parsektest-wheel-spin", t0);

            GhostBuildResult build = null;
            try
            {
                build = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                    rec, "ParsekTest_WheelSpin");
                if (build == null || build.root == null)
                {
                    InGameAssert.Skip("BuildTimelineGhostFromSnapshot produced no ghost for '" +
                        partName + "'");
                    return;
                }

                // Anchor the ghost on the live body so `up` and getRFrmVel are real.
                build.root.transform.position = active.transform.position;

                var state = new GhostPlaybackState
                {
                    ghost = build.root,
                    vesselName = rec.VesselName,
                    recordingId = rec.RecordingId
                };
                GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, build);

                RoboticGhostInfo wheel = FindWheelGroundSpeedInfo(state);
                if (wheel == null || wheel.servoTransform == null)
                {
                    InGameAssert.Skip("the ghost built for '" + partName + "' carries no " +
                        "RoboticGhostInfo in WheelGroundSpeed mode with a servo transform " +
                        "(GhostVisualBuilder could not resolve the wheel's spin transform)");
                    return;
                }
                InGameAssert.IsGreaterThan(wheel.wheelRadius,
                    GhostPlaybackLogic.MinWheelRadiusMeters,
                    "the wheel radius resolved off the prefab must be usable, got " +
                    wheel.wheelRadius.ToString("R"));

                // Build a velocity that is unambiguously GROUND motion: the body's own rotation at
                // this position (which the derivation must subtract) plus 10 m/s along this wheel's
                // own roll direction (which it must keep).
                Vector3d ghostPos = build.root.transform.position;
                Vector3 up = (Vector3)(ghostPos - body.position);
                if (up.sqrMagnitude <= 1e-8f)
                {
                    InGameAssert.Skip("ghost sits at the body centre; no surface normal to roll on");
                    return;
                }
                Vector3 spinAxisWorld = wheel.servoTransform.TransformDirection(
                    wheel.axisLocal.sqrMagnitude > 0.0001f ? wheel.axisLocal.normalized : Vector3.right);
                Vector3 rollForward = GhostPlaybackLogic.ComputeWheelRollForward(spinAxisWorld, up);
                if (rollForward.sqrMagnitude <= 1e-8f)
                {
                    InGameAssert.Skip("this wheel's spin axis is parallel to the local surface " +
                        "normal in the current scene orientation, so it has no rolling direction");
                    return;
                }
                Vector3 bodyFrameVelocity = (Vector3)body.getRFrmVel(ghostPos);
                Vector3 drivingVelocity = bodyFrameVelocity + rollForward * 10f;

                state.lastInterpolatedBodyName = body.name;

                // The frame is short on purpose. Quaternion.Angle answers in [0, 180], so a frame
                // long enough to spin the wheel past half a turn wraps and stops being a readable
                // measure of "how far did it turn". At 10 m/s over a sub-metre wheel, 0.05 s is
                // ~80-160 deg - unmistakable, and nowhere near the wrap.
                const double frameSeconds = 0.05;
                double expectedDegrees =
                    10.0 / wheel.wheelRadius * Mathf.Rad2Deg * frameSeconds;

                // ARM 1 — surface section + ground motion: the wheel must turn.
                var surfaceSections = new List<TrackSection>
                {
                    FixtureSection(SegmentEnvironment.SurfaceMobile, t0, t0 + 100.0)
                };
                state.lastInterpolatedVelocity = drivingVelocity;
                double drivenDegrees = SpinOneFrame(
                    state, wheel, surfaceSections, t0 + 10.0, frameSeconds, out _, out _);
                InGameAssert.IsGreaterThan(drivenDegrees, 1.0,
                    "a wheel on the ground moving at 10 m/s along its own roll direction must " +
                    "rotate; it moved " + drivenDegrees.ToString("R") + " deg over " +
                    frameSeconds.ToString("R") + " s (expected ~" + expectedDegrees.ToString("R") +
                    ", radius " + wheel.wheelRadius.ToString("R") + " m)");

                // ARM 2 — surface section + parked: the wheel must hold still.
                //
                // Asserted as an UNTOUCHED rotation rather than a small angle, and arm 3 needs the
                // same treatment for a sharper reason: Quaternion.Angle wraps into [0, 180], so a
                // broken gate spinning the wheel by thousands of degrees could land near zero by
                // coincidence and read as "held still". A closed gate does not write
                // servoTransform.localRotation at all. NOTE Unity's Quaternion.operator== is a
                // dot-product threshold (~0.16 deg), not bitwise equality, so a runaway spin
                // landing within ~0.16 deg of a full-turn multiple could still slip through -
                // a ~0.1% residual blind spot per arm, categorically tighter than an
                // angle-epsilon assert but not the exact no-write statement.
                state.lastInterpolatedVelocity = bodyFrameVelocity;
                SpinOneFrame(state, wheel, surfaceSections, t0 + 20.0, frameSeconds,
                    out Quaternion parkedBefore, out Quaternion parkedAfter);
                InGameAssert.IsTrue(parkedBefore == parkedAfter,
                    "a parked ghost (moving only with the body's own rotation) must not creep; its " +
                    "wheel rotation moved from " + parkedBefore + " to " + parkedAfter);

                // ARM 3 — THE D1 REGRESSION. Off the ground, at orbital speed, the wheel must hold
                // still even though the speed is enormous.
                //
                // The span is DELIBERATELY disjoint from the surface span above, and that is a
                // fixture requirement rather than a stylistic one: the ground-contact answer is
                // memoised over the resolved section's own [startUT, endUT), so reusing [t0, t0+100)
                // with a different environment would be answered from the cache and this arm would
                // measure the previous arm. Stepping outside the window forces the re-resolve, which
                // is exactly what a real section change does.
                var orbitSections = new List<TrackSection>
                {
                    FixtureSection(SegmentEnvironment.ExoBallistic, t0 + 200.0, t0 + 300.0)
                };
                state.lastInterpolatedVelocity = bodyFrameVelocity + rollForward * 2100f;
                SpinOneFrame(state, wheel, orbitSections, t0 + 210.0, frameSeconds,
                    out Quaternion orbitBefore, out Quaternion orbitAfter);
                InGameAssert.IsTrue(orbitBefore == orbitAfter,
                    "GROUND-CONTACT GATE (review D1): a rover carried to orbit keeps the " +
                    "recording's ~2100 m/s orbital velocity, and speed/radius turns that into " +
                    "thousands of degrees per frame. Off a Surface TrackSection the wheels must not " +
                    "be touched at all; this one moved from " + orbitBefore + " to " + orbitAfter +
                    ". Check GhostPlaybackLogic.ResolveWheelGroundContact.");

                ParsekLog.Info("TestRunner",
                    "RecordedSignals wheel spin: part='" + partName + "' radius=" +
                    wheel.wheelRadius.ToString("R") + "m driven=" + drivenDegrees.ToString("R") +
                    "deg (expected ~" + expectedDegrees.ToString("R") + ") parkedUntouched=" +
                    (parkedBefore == parkedAfter) + " offGroundUntouched=" +
                    (orbitBefore == orbitAfter));
            }
            finally
            {
                if (build != null && build.root != null)
                    Object.Destroy(build.root);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Fixture helpers
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Drives one <c>UpdateActiveRobotics</c> frame of length <paramref name="deltaSeconds"/>,
        /// returns how far the servo transform rotated (degrees, via <c>Quaternion.Angle</c>, so it
        /// wraps into [0, 180]) and hands back the before / after rotations so a caller asserting
        /// "held still" can test them for equality instead of trusting a wrapped angle.
        /// <c>info.lastUpdateUT</c> is seeded explicitly because the first pass over a fresh info
        /// only stamps the clock (its <c>lastUpdateUT</c> starts NaN by design).
        /// </summary>
        private static double SpinOneFrame(
            GhostPlaybackState state, RoboticGhostInfo wheel,
            List<TrackSection> sections, double frameStartUT, double deltaSeconds,
            out Quaternion before, out Quaternion after)
        {
            wheel.lastUpdateUT = frameStartUT;
            before = wheel.servoTransform.localRotation;
            GhostPlaybackLogic.UpdateActiveRobotics(state, frameStartUT + deltaSeconds, sections);
            after = wheel.servoTransform.localRotation;
            return Quaternion.Angle(before, after);
        }

        private static RoboticGhostInfo FindWheelGroundSpeedInfo(GhostPlaybackState state)
        {
            if (state.roboticInfos == null)
                return null;
            foreach (var kv in state.roboticInfos)
            {
                if (kv.Value != null && kv.Value.visualMode == RoboticVisualMode.WheelGroundSpeed)
                    return kv.Value;
            }
            return null;
        }

        private static TrackSection FixtureSection(
            SegmentEnvironment env, double startUT, double endUT)
        {
            return new TrackSection
            {
                environment = env,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            };
        }

        /// <summary>
        /// A one-part recording whose ghost snapshot names <paramref name="partName"/> at
        /// <see cref="FixturePartPid"/>. The pid is not arbitrary: ghost part GameObjects are named
        /// by persistentId for O(1) playback lookup, and a single-part showcase fixture must use
        /// 100000 (the first slot <c>VesselSnapshotBuilder.AddPart</c> assigns) or every event
        /// silently misses.
        /// </summary>
        private static Recording BuildSinglePartFixture(string partName, string idPrefix, double t0)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("name", idPrefix);
            ConfigNode partNode = snapshot.AddNode("PART");
            partNode.AddValue("name", partName);
            partNode.AddValue("persistentId", FixturePartPid.ToString());

            return new Recording
            {
                RecordingId = idPrefix + "-" + System.Guid.NewGuid().ToString("N").Substring(0, 8),
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                VesselName = idPrefix,
                VesselPersistentId = FixturePartPid,
                ExplicitStartUT = t0,
                ExplicitEndUT = t0 + 100.0,
                GhostVisualSnapshot = snapshot.CreateCopy(),
                VesselSnapshot = snapshot.CreateCopy()
            };
        }

        private static PartEvent ChuteEvent(double ut, PartEventType type, string partName)
        {
            return new PartEvent
            {
                ut = ut,
                partPersistentId = FixturePartPid,
                eventType = type,
                partName = partName,
                value = 0f,
                moduleIndex = 0
            };
        }

        /// <summary>
        /// First loaded part whose prefab carries a <c>ModuleParachute</c> with BOTH a canopy and a
        /// cap transform resolvable on the prefab — the only shape that can prove a cap restore.
        /// Discovered rather than hardcoded so the cell does not depend on a specific part surviving
        /// in the install; stock <c>parachuteSingle</c> is preferred when present purely so the log
        /// line is stable across runs.
        /// </summary>
        private static string TryFindParachutePartNameWithCap()
        {
            List<AvailablePart> parts = PartLoader.LoadedPartsList;
            if (parts == null)
                return null;

            string fallback = null;
            for (int i = 0; i < parts.Count; i++)
            {
                AvailablePart ap = parts[i];
                Part prefab = ap?.partPrefab;
                if (prefab == null)
                    continue;
                ModuleParachute chute = prefab.FindModuleImplementing<ModuleParachute>();
                if (chute == null ||
                    string.IsNullOrEmpty(chute.canopyName) || string.IsNullOrEmpty(chute.capName))
                    continue;
                if (prefab.FindModelTransform(chute.canopyName) == null ||
                    prefab.FindModelTransform(chute.capName) == null)
                    continue;

                if (ap.name == "parachuteSingle")
                    return ap.name;
                if (fallback == null)
                    fallback = ap.name;
            }
            return fallback;
        }

        /// <summary>
        /// First loaded part whose prefab carries a wheel MOTOR module, by the same predicate the
        /// recorder's emission gate and the ghost builder's visual-mode switch use
        /// (<c>FlightRecorder.IsWheelMotorSpinModuleName</c>), so the fixture and the production
        /// gate can never disagree about what counts as a driven wheel.
        /// </summary>
        private static string TryFindWheelMotorPartName()
        {
            List<AvailablePart> parts = PartLoader.LoadedPartsList;
            if (parts == null)
                return null;

            for (int i = 0; i < parts.Count; i++)
            {
                AvailablePart ap = parts[i];
                Part prefab = ap?.partPrefab;
                if (prefab?.Modules == null)
                    continue;
                for (int m = 0; m < prefab.Modules.Count; m++)
                {
                    PartModule module = prefab.Modules[m];
                    if (module != null &&
                        FlightRecorder.IsWheelMotorSpinModuleName(module.moduleName))
                    {
                        return ap.name;
                    }
                }
            }
            return null;
        }
    }
}
