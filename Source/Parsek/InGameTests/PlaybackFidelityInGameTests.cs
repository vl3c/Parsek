using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Category <c>PlaybackFidelity</c> — the live half of P5/P6 (plume magnitude S1, deployable
    /// interpolation S2, synthesized motion S3). Every claim here is one the headless suite cannot
    /// make, because it needs a real PartLoader prefab, a real cloned <c>KSPParticleEmitter</c>, a
    /// real <c>Transform</c>, or Unity's native rotation math:
    ///
    ///   * S1 is a RATIO OF A CAPTURED BASELINE, and the baseline only exists after a real FX clone
    ///     has been instantiated, size-boosted and velocity-floored. Headless cells pin the ratio
    ///     ARITHMETIC; only a live scene proves a captured baseline exists at all and that the write
    ///     lands on the emitter. The suppress/restore cell additionally covers
    ///     <c>RestoreAllRcsEmissions</c>, the ONE production path that re-enables emitters without
    ///     going through <c>SetRcsEmission</c> — the whole reason the mechanism had to be persistent
    ///     fields rather than a per-event computation.
    ///   * S2's interpolation writes <c>Transform.localPosition/localRotation/localScale</c> between
    ///     two sampled animation poses. Headless can prove the PROGRESS; only a scene proves the
    ///     pose is strictly between the endpoints, and that a BASELINE application still snaps.
    ///   * S3 reads the ghost's APPLIED WORLD ROTATION off the transform and writes deflections
    ///     through <c>Quaternion.AngleAxis</c>, a native ECall. None of that exists headless.
    ///
    /// House rules honoured: every cell builds its own single-part fixture from a prefab DISCOVERED
    /// at run time (never a hardcoded part name), and a cell that cannot self-set-up calls
    /// <see cref="InGameAssert.Skip"/> naming the context it needed instead of asserting against an
    /// assumed one. Each cell destroys its ghost in a finally block.
    /// </summary>
    public class PlaybackFidelityInGameTests
    {
        private readonly InGameTestRunner runner;
        public PlaybackFidelityInGameTests(InGameTestRunner runner) { this.runner = runner; }

        /// <summary>Ghost part pid for the single-part fixtures — the first slot
        /// <c>VesselSnapshotBuilder.AddPart</c> assigns (100000 + 0 * 1111). A single-part showcase
        /// fixture must use it or every event's playback lookup silently misses.</summary>
        private const uint FixturePartPid = 100000u;

        /// <summary>How many candidate prefabs a cell will actually BUILD before giving up. Ghost
        /// building is the expensive step, so the candidate lists below are pre-filtered by cheap
        /// module tests and only the first few survivors are built.</summary>
        private const int MaxFixtureBuildAttempts = 4;

        // ──────────────────────────────────────────────────────────────────────
        //  S1 — plume magnitude
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A lit engine's plume must be SMALLER at part throttle than at full, and EXACTLY the
        /// captured baseline at full — that second half is what proves the change is a ratio rather
        /// than a rewrite, i.e. that a 100%-throttle ghost still looks like the pre-S1 build.
        ///
        /// Measured on the ghost's own particle systems and (when the emitter fields were readable)
        /// on the cloned KSPParticleEmitter, because those are the two surfaces
        /// <c>ApplyFxMagnitudeScale</c> writes. If this reds while the headless ratio cells stay
        /// green, the arithmetic is fine and the failure is in CAPTURE — check that
        /// <c>CaptureFxMagnitudeBaselines</c> still runs after the FX build in
        /// <c>EngineFxBuilder.TryBuildEngineFX</c>.
        /// </summary>
        [InGameTest(Category = "PlaybackFidelity", Scene = GameScenes.FLIGHT,
            Description = "Engine plume magnitude scales down with throttle and returns exactly to the captured baseline at full")]
        public void EnginePlumeMagnitudeScalesWithThrottle()
        {
            GhostBuildResult build = null;
            try
            {
                EngineGhostInfo engine = null;
                GhostPlaybackState state = null;
                string usedPart = null;

                foreach (string partName in CandidateEngineParts())
                {
                    build = BuildGhost(partName, "parsektest-plume-magnitude", out state);
                    if (build != null)
                    {
                        engine = FindScalableEngine(state);
                        if (engine != null) { usedPart = partName; break; }
                    }
                    DestroyBuild(ref build);
                }

                if (engine == null)
                {
                    InGameAssert.Skip(
                        "no loaded engine part produced a ghost with both particle systems and a " +
                        "captured magnitude baseline; needs a stock engine whose EFFECTS (or legacy " +
                        "fx_* children) clone into the ghost for the plume magnitude to be observable");
                    return;
                }

                var evt = new PartEvent
                {
                    partPersistentId = engine.partPersistentId,
                    moduleIndex = engine.moduleIndex
                };

                GhostPlaybackLogic.ResetFxMagnitudeWriteFailureCountForTesting();

                GhostPlaybackLogic.SetEngineEmission(state, evt, 1f);
                float fullSpeed = SumStartSpeed(engine.particleSystems);
                float fullEmission = SumEmitterMaxEmission(engine.kspEmitters);

                GhostPlaybackLogic.SetEngineEmission(state, evt, 0.3f);
                float lowSpeed = SumStartSpeed(engine.particleSystems);
                float lowEmission = SumEmitterMaxEmission(engine.kspEmitters);

                GhostPlaybackLogic.SetEngineEmission(state, evt, 1f);
                float backToFullSpeed = SumStartSpeed(engine.particleSystems);

                // THE 2026-08-11 REGRESSION GUARD. Every emitter write threw
                // ("Object of type 'System.Single' cannot be converted to type 'System.Int32'")
                // and S1 was a total no-op in game, while the ParticleSystem arm below still passed
                // and the log line that reported it was rate-limited to one per minute per module.
                // A hard zero on the counter is the assertion that failure mode cannot come back
                // quietly, and quoting the count makes the next diagnosis a single read.
                int writeFailures = GhostPlaybackLogic.FxMagnitudeWriteFailureCount;
                InGameAssert.AreEqual(0, writeFailures,
                    "S1: " + writeFailures + " FX magnitude field write(s) were refused or threw " +
                    "while driving '" + usedPart + "' through 1.0 -> 0.3 -> 1.0. Any nonzero count " +
                    "means part of the plume is silently stuck at its baseline; grep " +
                    "'FX magnitude write failed' in KSP.log for the field name and its declared " +
                    "type (KSPParticleEmitter.minEmission / maxEmission are int, localVelocity is " +
                    "Vector3 — a reflective write must convert to the FieldInfo's own FieldType).");

                InGameAssert.IsTrue(fullSpeed > 0f,
                    "fixture is vacuous: '" + usedPart + "' produced a ghost whose particle systems " +
                    "have zero total startSpeedMultiplier at full throttle, so a 'smaller at 30%' " +
                    "assertion could not fail");
                InGameAssert.IsLessThan(lowSpeed, fullSpeed,
                    "S1: at 30% throttle the plume speed/size must be strictly smaller than at " +
                    "full. '" + usedPart + "' measured " + lowSpeed + " vs " + fullSpeed +
                    ". Check GhostPlaybackLogic.ApplyFxMagnitudeScale and the captured baselines.");
                InGameAssert.ApproxEqual(fullSpeed, backToFullSpeed, fullSpeed * 0.001f + 0.0001f,
                    "S1 IDEMPOTENCE: returning to full throttle must restore the captured baseline " +
                    "EXACTLY (ratio 1.0), not drift. Drift here means the applier is multiplying the " +
                    "current value instead of the stored baseline — the bug ratio-of-baseline exists " +
                    "to prevent.");

                // The emitter's rate fields are INTEGER, and an integer baseline of 1 quantises back
                // to 1 at every ratio (the nonzero floor is deliberate — a lit engine never goes to
                // zero particles). So a strict inequality is only sound when at least one emitter
                // has room to move; a rig of all-1 baselines would red on perfectly correct code.
                // One emitter with baseline >= 2 is exactly enough, because the others can only
                // stay equal and the sum is then strictly smaller.
                if (AnyEmitterBaselineAtLeast(engine.kspEmitters, 2f))
                {
                    InGameAssert.IsLessThan(lowEmission, fullEmission,
                        "S1: the cloned KSPParticleEmitter's own emission rate must fall with " +
                        "throttle too — it is the ONLY particle creation source on a ghost " +
                        "(Unity's emission module is permanently disabled, bug #105), so scaling " +
                        "the ParticleSystem alone would leave the particle COUNT unchanged. " +
                        "Measured " + lowEmission + " at 0.3 against " + fullEmission + " at full.");
                }

                ParsekLog.Info("TestRunner",
                    "PlaybackFidelity engine plume: part='" + usedPart + "' fullSpeed=" +
                    fullSpeed.ToString("R") + " lowSpeed=" + lowSpeed.ToString("R") +
                    " fullEmission=" + fullEmission.ToString("R") + " lowEmission=" +
                    lowEmission.ToString("R") + " restored=" + backToFullSpeed.ToString("R") +
                    " writeFailures=" + writeFailures);
            }
            finally
            {
                DestroyBuild(ref build);
            }
        }

        /// <summary>
        /// The suppress/restore round trip. <c>RestoreAllRcsEmissions</c> re-enables emitters
        /// DIRECTLY rather than going back through <c>SetRcsEmission</c>, which is exactly why S1
        /// mutates persistent emitter fields instead of computing magnitude per event: if the scale
        /// lived in the event path, every high-warp suppression would restore a full-magnitude plume
        /// on a quarter-throttle thruster. This cell is that path's only proof.
        /// </summary>
        [InGameTest(Category = "PlaybackFidelity", Scene = GameScenes.FLIGHT,
            Description = "RCS plume magnitude survives the suppress/restore round trip that bypasses SetRcsEmission")]
        public void RcsPlumeMagnitudeSurvivesSuppressAndRestore()
        {
            GhostBuildResult build = null;
            try
            {
                RcsGhostInfo rcs = null;
                GhostPlaybackState state = null;
                string usedPart = null;

                foreach (string partName in CandidateRcsParts())
                {
                    build = BuildGhost(partName, "parsektest-rcs-magnitude", out state);
                    if (build != null)
                    {
                        rcs = FindScalableRcs(state);
                        if (rcs != null) { usedPart = partName; break; }
                    }
                    DestroyBuild(ref build);
                }

                if (rcs == null)
                {
                    InGameAssert.Skip(
                        "no loaded RCS part produced a ghost with both particle systems and a " +
                        "captured magnitude baseline; needs a stock RCS block whose running EFFECTS " +
                        "group clones into the ghost");
                    return;
                }

                var evt = new PartEvent
                {
                    partPersistentId = rcs.partPersistentId,
                    moduleIndex = rcs.moduleIndex
                };

                // THE BASELINE, read BEFORE anything is scaled. Without it this cell was VACUOUS:
                // the H36 flight of 2026-08-11 passed it green while every emitter write was
                // throwing, because "unchanged baseline in, unchanged baseline out" satisfies a
                // round trip perfectly. The round trip only means something once the value going
                // into it is PROVABLY not the baseline.
                GhostPlaybackLogic.ResetFxMagnitudeWriteFailureCountForTesting();
                float baselineSpeed = SumBaselineStartSpeed(rcs.particleBaselines);
                float baselineEmission = SumBaselineMaxEmission(rcs.kspEmitters);

                GhostPlaybackLogic.SetRcsEmission(state, evt, 0.25f);
                float scaledSpeed = SumStartSpeed(rcs.particleSystems);
                float scaledEmission = SumEmitterMaxEmission(rcs.kspEmitters);

                GhostPlaybackLogic.StopAllRcsEmissions(state);
                GhostPlaybackLogic.RestoreAllRcsEmissions(state);

                float afterSpeed = SumStartSpeed(rcs.particleSystems);
                float afterEmission = SumEmitterMaxEmission(rcs.kspEmitters);

                int writeFailures = GhostPlaybackLogic.FxMagnitudeWriteFailureCount;
                InGameAssert.AreEqual(0, writeFailures,
                    "S1: " + writeFailures + " FX magnitude field write(s) were refused or threw " +
                    "while driving '" + usedPart + "' to 25% power. Any nonzero count means part " +
                    "of the plume is silently stuck at its baseline; grep 'FX magnitude write " +
                    "failed' in KSP.log for the field name and its declared type.");

                InGameAssert.IsTrue(scaledSpeed > 0f,
                    "fixture is vacuous: '" + usedPart + "' produced zero total startSpeedMultiplier " +
                    "at 25% power, so the round-trip assertion could not fail");
                InGameAssert.IsTrue(baselineSpeed > 0f,
                    "fixture is vacuous: '" + usedPart + "' captured a zero total baseline " +
                    "startSpeedMultiplier, so 'scaled differs from baseline' could not fail");

                // NON-VACUITY, arm 1: the value the round trip is about must actually BE a scaled
                // one. This is a HARD assertion rather than a skip, and that is grounded in a
                // measurement of the fixture population rather than in hope: every stock RCS
                // MODEL_MULTI_PARTICLE in GameData/Squad declares `speed = 0.0 0.8` / `speed = 1.0
                // 1.0` (RV-105 and its small variant, the linear port, the Vernor, the Mk1-3 pod),
                // so a 0.25-power write reads ~0.85 of baseline; and a part whose curve failed to
                // scan falls back to `power * 10f`, i.e. 0.25 of baseline. Neither can land on 1.0.
                // The showcase floors cannot lift it either — they are gated on a `parsek-rcs-...`
                // vessel-name prefix this fixture does not use, and RcsShowcaseSpeedScale is 1f.
                InGameAssert.IsLessThan(scaledSpeed, baselineSpeed * 0.999f,
                    "S1 NON-VACUITY: at 25% power the plume must be strictly BELOW the captured " +
                    "baseline before the suppress/restore round trip is worth asserting. '" +
                    usedPart + "' scaled to " + scaledSpeed + " against a baseline of " +
                    baselineSpeed + " — an equal reading means nothing was scaled at all, which is " +
                    "exactly the state that passed this cell vacuously in the H36 flight.");

                // NON-VACUITY, arm 2: the same on the emitter's own rate field, which is where the
                // write actually threw. Same integer-quantisation guard as the engine cell.
                if (AnyEmitterBaselineAtLeast(rcs.kspEmitters, 2f))
                {
                    InGameAssert.IsLessThan(scaledEmission, baselineEmission * 0.999f,
                        "S1 NON-VACUITY: the cloned KSPParticleEmitter's own emission rate must be " +
                        "below its captured baseline at 25% power. '" + usedPart + "' read " +
                        scaledEmission + " against a baseline of " + baselineEmission + ".");
                }

                // ...and only now does the round trip mean "the SCALED value survived", not "the
                // baseline survived".
                InGameAssert.ApproxEqual(scaledSpeed, afterSpeed, scaledSpeed * 0.001f + 0.0001f,
                    "S1 SUPPRESS/RESTORE: RestoreAllRcsEmissions re-enables emitters without calling " +
                    "SetRcsEmission, so the scaled magnitude must still be sitting in the persistent " +
                    "fields. '" + usedPart + "' came back at " + afterSpeed + " instead of " +
                    scaledSpeed + ".");
                InGameAssert.ApproxEqual(scaledEmission, afterEmission,
                    scaledEmission * 0.001f + 0.0001f,
                    "S1 SUPPRESS/RESTORE: the emitter's own emission rate must survive the round " +
                    "trip for the same reason.");

                ParsekLog.Info("TestRunner",
                    "PlaybackFidelity RCS plume: part='" + usedPart + "' baselineSpeed=" +
                    baselineSpeed.ToString("R") + " scaledSpeed=" + scaledSpeed.ToString("R") +
                    " afterRestore=" + afterSpeed.ToString("R") + " baselineEmission=" +
                    baselineEmission.ToString("R") + " scaledEmission=" +
                    scaledEmission.ToString("R") + " afterEmission=" + afterEmission.ToString("R") +
                    " writeFailures=" + writeFailures);
            }
            finally
            {
                DestroyBuild(ref build);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  S2 — deployable interpolation
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The mid-clip pose must be STRICTLY BETWEEN the stowed and deployed poses, and a BASELINE
        /// application of the same target must still SNAP. The two halves are inseparable: a panel
        /// that animates at spawn is the M1 regression (the ghost is being put into the state it
        /// spawned in, not shown opening), and a panel that snaps on its event is the bug S2 fixes.
        /// </summary>
        [InGameTest(Category = "PlaybackFidelity", Scene = GameScenes.FLIGHT,
            Description = "A deployable event animates through a strictly interior pose while a baseline application still snaps")]
        public void DeployableAnimatesOnEventAndSnapsOnBaseline()
        {
            GhostBuildResult build = null;
            try
            {
                DeployableGhostInfo deployable = null;
                GhostPlaybackState state = null;
                string usedPart = null;

                foreach (string partName in CandidateDeployableParts())
                {
                    build = BuildGhost(partName, "parsektest-deployable-lerp", out state);
                    if (build != null)
                    {
                        deployable = FindMovingDeployable(state);
                        if (deployable != null) { usedPart = partName; break; }
                    }
                    DestroyBuild(ref build);
                }

                if (deployable == null)
                {
                    InGameAssert.Skip(
                        "no loaded deployable part produced a ghost whose sampled stowed and " +
                        "deployed poses actually DIFFER; needs a stock solar panel / gear / bay " +
                        "whose animation samples on this install for interpolation to be observable");
                    return;
                }

                DeployableTransformState moving = FindMovingTransform(deployable);
                Vector3 stowedPos = moving.stowedPos;
                Vector3 deployedPos = moving.deployedPos;
                double t0 = Planetarium.GetUniversalTime();

                // ARM 1 — the ANIMATED path. Half a clip in, the transform must be interior.
                var extend = new PartEvent
                {
                    ut = t0,
                    partPersistentId = deployable.partPersistentId,
                    eventType = PartEventType.DeployableExtended
                };
                GhostPlaybackLogic.ApplyDeployableState(state, extend, deployed: true, immediate: false);
                GhostPlaybackLogic.UpdateActiveDeployables(
                    state, t0 + deployable.clipLengthSeconds * 0.5);

                Vector3 midPos = moving.t.localPosition;
                float toStowed = (midPos - stowedPos).magnitude;
                float toDeployed = (midPos - deployedPos).magnitude;
                float span = (deployedPos - stowedPos).magnitude;

                InGameAssert.IsTrue(span > 0f, "fixture is vacuous: stowed and deployed poses coincide");
                InGameAssert.IsGreaterThan(toStowed, span * 0.1f,
                    "S2: half a clip after the event the panel must have LEFT the stowed pose. " +
                    "'" + usedPart + "' is still " + toStowed + "m from stowed over a " + span +
                    "m span — the transition is not being advanced by UpdateActiveDeployables.");
                InGameAssert.IsGreaterThan(toDeployed, span * 0.1f,
                    "S2: half a clip after the event the panel must NOT have arrived yet. " +
                    "'" + usedPart + "' is already " + toDeployed + "m from deployed over a " + span +
                    "m span — this is the snap S2 exists to remove.");
                InGameAssert.IsTrue(deployable.transitionActive,
                    "S2: a half-finished transition must still be marked active so the next frame " +
                    "advances it");

                // ARM 2 — the BASELINE path. Same target, immediate, must land exactly on the pose.
                var baselineEvt = new PartEvent { partPersistentId = deployable.partPersistentId };
                GhostPlaybackLogic.ApplyDeployableState(state, baselineEvt, deployed: true);

                InGameAssert.IsFalse(deployable.transitionActive,
                    "S2: an immediate (baseline) application must CANCEL any in-flight transition, " +
                    "not leave one running that would drag the pose back");
                InGameAssert.ApproxEqual(deployedPos, moving.t.localPosition,
                    (float)InGameFixtureMath.SceneFloatGridToleranceMeters(deployedPos.magnitude) + 1e-4f,
                    "S2: a baseline application must SNAP to the endpoint pose. A ghost spawning " +
                    "with its panels already out must not be shown opening them.");

                ParsekLog.Info("TestRunner",
                    "PlaybackFidelity deployable: part='" + usedPart + "' clip=" +
                    deployable.clipLengthSeconds.ToString("R") + "s span=" + span.ToString("R") +
                    "m midToStowed=" + toStowed.ToString("R") + " midToDeployed=" +
                    toDeployed.ToString("R"));
            }
            finally
            {
                DestroyBuild(ref build);
            }
        }

        /// <summary>
        /// THE M4 CELL. A loop cycle that lands mid-clip must re-stow the panel and drop the
        /// transition, not resume it against a rewound clock. Both halves of the reset are exercised
        /// in the production order: <c>ResetForLoopCycle</c> (the numbers, Unity-free) then
        /// <c>ReapplySpawnTimeModuleBaselinesForLoopCycle</c> (the transforms).
        /// </summary>
        [InGameTest(Category = "PlaybackFidelity", Scene = GameScenes.FLIGHT,
            Description = "A loop cycle taken mid-clip re-stows the deployable and clears the transition")]
        public void LoopCycleResetReStowsAMidTransitionDeployable()
        {
            GhostBuildResult build = null;
            try
            {
                DeployableGhostInfo deployable = null;
                GhostPlaybackState state = null;
                Recording rec = null;
                string usedPart = null;

                foreach (string partName in CandidateDeployableParts())
                {
                    build = BuildGhost(partName, "parsektest-deployable-loop", out state, out rec);
                    if (build != null)
                    {
                        deployable = FindMovingDeployable(state);
                        if (deployable != null) { usedPart = partName; break; }
                    }
                    DestroyBuild(ref build);
                }

                if (deployable == null)
                {
                    InGameAssert.Skip(
                        "no loaded deployable part produced a ghost with distinguishable stowed and " +
                        "deployed poses; same context the interpolation cell needs");
                    return;
                }

                DeployableTransformState moving = FindMovingTransform(deployable);
                double t0 = Planetarium.GetUniversalTime();

                var extend = new PartEvent
                {
                    ut = t0,
                    partPersistentId = deployable.partPersistentId,
                    eventType = PartEventType.DeployableExtended
                };
                GhostPlaybackLogic.ApplyDeployableState(state, extend, deployed: true, immediate: false);
                GhostPlaybackLogic.UpdateActiveDeployables(
                    state, t0 + deployable.clipLengthSeconds * 0.5);
                InGameAssert.IsTrue(deployable.transitionActive,
                    "fixture precondition: the panel must be mid-transition before the loop cycle");

                GhostPlaybackLogic.ResetForLoopCycle(state, state.loopCycleIndex + 1);
                InGameAssert.IsFalse(deployable.transitionActive,
                    "M4 CLASS: ResetForLoopCycle must drop every in-flight transition. Resuming one " +
                    "against a rewound clock runs the clip backwards.");
                InGameAssert.IsTrue(
                    state.activeDeployableTransitions == null
                    || state.activeDeployableTransitions.Count == 0,
                    "M4 CLASS: the active-transition list must be empty after a loop reset");

                GhostPlaybackLogic.ReapplySpawnTimeModuleBaselinesForLoopCycle(state, rec);
                InGameAssert.ApproxEqual(moving.stowedPos, moving.t.localPosition,
                    (float)InGameFixtureMath.SceneFloatGridToleranceMeters(moving.stowedPos.magnitude) + 1e-4f,
                    "M4 CLASS: the TRANSFORM must be back at the stowed pose after the loop " +
                    "re-apply. ResetForLoopCycle can only zero numbers; if the mesh stays half-open " +
                    "while the numbers claim stowed, that is exactly the carry-over bug.");

                ParsekLog.Info("TestRunner",
                    "PlaybackFidelity loop reset: part='" + usedPart + "' re-stowed to " +
                    moving.t.localPosition + " (stowed " + moving.stowedPos + ")");
            }
            finally
            {
                DestroyBuild(ref build);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  S3 — synthesized motion
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A gimbal must respond to the ghost's own attitude change and must never exceed the
        /// module's declared range. The attitude derivative is read off <c>ghost.transform.rotation</c>
        /// — the APPLIED world rotation — which is the only reading that is frame-correct on both
        /// Absolute and RELATIVE track sections; this cell drives it by rotating the real transform.
        /// The clamp arm injects a deliberately absurd rate so a missing clamp would show up as a
        /// nozzle swinging far past its authority.
        ///
        /// It ALSO carries the sign/handedness pin for the hand-rolled quaternion product
        /// (<see cref="AssertAngularVelocityAgreesWithUnity"/>), run first so it holds even on a rig
        /// with no gimballing engine to build. That check cannot live headless: the only independent
        /// authority on the convention is Unity's own native rotation math.
        /// </summary>
        [InGameTest(Category = "PlaybackFidelity", Scene = GameScenes.FLIGHT,
            Description = "A gimbal deflects with the ghost's applied attitude change and clamps to the module's range, and the attitude derivative agrees with Unity's own quaternion convention in sign and axis")]
        public void GimbalDeflectsWithAttitudeAndClampsToRange()
        {
            // Convention pin FIRST: it needs no fixture, so it still runs (and still reds on a
            // mirrored product) on a scene where no engine prefab yields a gimbal transform.
            AssertAngularVelocityAgreesWithUnity();

            GhostBuildResult build = null;
            try
            {
                GimbalGhostInfo gimbal = null;
                GhostPlaybackState state = null;
                string usedPart = null;

                foreach (string partName in CandidateGimbalParts())
                {
                    build = BuildGhost(partName, "parsektest-gimbal", out state);
                    if (build != null && state.synthesizedMotionInfos?.gimbals != null
                        && state.synthesizedMotionInfos.gimbals.Count > 0)
                    {
                        gimbal = state.synthesizedMotionInfos.gimbals[0];
                        if (gimbal.gimbalTransforms.Count > 0) { usedPart = partName; break; }
                        gimbal = null;
                    }
                    DestroyBuild(ref build);
                }

                if (gimbal == null)
                {
                    InGameAssert.Skip(
                        "no loaded engine part produced a ghost with a resolvable ModuleGimbal " +
                        "transform; needs a stock gimballing engine whose gimbalTransformName " +
                        "resolves on the cloned model");
                    return;
                }

                // A lit engine is a precondition: a cold engine's bell hangs neutral by design.
                LightEveryEngine(state);

                Transform ring = gimbal.gimbalTransforms[0];
                Quaternion neutral = gimbal.neutralRotations[0];
                double t0 = Planetarium.GetUniversalTime();

                // Seed the derivative, then rotate the ghost hard between synthesis frames.
                state.ghost.transform.rotation = Quaternion.identity;
                GhostPlaybackLogic.UpdateSynthesizedMotion(state, t0);

                for (int i = 1; i <= 12; i++)
                {
                    state.ghost.transform.rotation =
                        Quaternion.AngleAxis(20f * i, state.ghost.transform.right);
                    GhostPlaybackLogic.UpdateSynthesizedMotion(state, t0 + i * 0.2);
                }

                float deflected = Quaternion.Angle(neutral, ring.localRotation);
                InGameAssert.IsGreaterThan(deflected, 0.05f,
                    "S3: a lit engine on a ghost pitching at ~100 deg/s must gimbal. '" + usedPart +
                    "' stayed within " + deflected + " degrees of neutral — check that " +
                    "UpdateSynthesizedMotion is reading ghost.transform.rotation and that the " +
                    "engine's currentPower gate is open.");
                InGameAssert.IsLessThan(deflected, gimbal.gimbalRangeDegrees * 2f + 0.5f,
                    "S3 CLAMP: the deflection must stay inside the module's own authority even " +
                    "under an absurd rate. '" + usedPart + "' reached " + deflected +
                    " degrees against a declared range of " + gimbal.gimbalRangeDegrees +
                    " (the composed two-axis bound is 2x the per-axis range).");

                // And it must ease back to neutral once the ghost stops rotating.
                for (int i = 13; i <= 40; i++)
                    GhostPlaybackLogic.UpdateSynthesizedMotion(state, t0 + i * 0.2);
                float settled = Quaternion.Angle(neutral, ring.localRotation);
                InGameAssert.IsLessThan(settled, deflected * 0.5f + 0.05f,
                    "S3: with the ghost holding attitude the deflection must decay back toward " +
                    "neutral (the deadband answers exactly zero); '" + usedPart + "' held at " +
                    settled + " degrees.");

                ParsekLog.Info("TestRunner",
                    "PlaybackFidelity gimbal: part='" + usedPart + "' range=" +
                    gimbal.gimbalRangeDegrees.ToString("R") + " peak=" + deflected.ToString("R") +
                    " settled=" + settled.ToString("R"));
            }
            finally
            {
                DestroyBuild(ref build);
            }
        }

        /// <summary>
        /// THE CONVENTION PIN. <c>GhostPlaybackLogic.ComputeLocalAngularVelocityDegPerSec</c>
        /// multiplies quaternions BY HAND (Unity's <c>Quaternion.Inverse</c> / <c>operator*</c> /
        /// <c>ToAngleAxis</c> are native ECalls that throw headless), and a hand-rolled Hamilton
        /// product is exactly the kind of code that can be self-consistently MIRRORED: flip the sign
        /// of the vector part, or swap the operand order, and every magnitude in the system stays
        /// identical while every gimbal and control surface deflects the wrong way.
        ///
        /// The headless cells cannot catch that — they build their inputs from raw components in the
        /// same convention the product assumes, which is circular — and the gimbal arm above only
        /// reads <c>Quaternion.Angle</c>, an UNSIGNED magnitude. So this is the one assertion that
        /// compares the product against an INDEPENDENT authority: Unity's own
        /// <c>Inverse(previous) * current</c> → <c>ToAngleAxis</c>, which is the definition the
        /// production math is a hand transcription of.
        ///
        /// The fixture pair is deliberately NON-COMMUTING (a yaw, then a pitch about the rotated
        /// frame). For a single-axis pair the local delta <c>conj(p)*c</c> and the world delta
        /// <c>c*conj(p)</c> share an axis, so an operand-order mirror would slip through; here the
        /// world-order axis is tilted ~50 degrees off the local-order one and the dot test rejects it.
        /// A pure negation is rejected by the same test at dot = -1.
        /// </summary>
        private static void AssertAngularVelocityAgreesWithUnity()
        {
            const double dt = 0.5;
            Quaternion previous = Quaternion.AngleAxis(50f, Vector3.up);
            Quaternion current = previous * Quaternion.AngleAxis(30f, Vector3.right);

            Vector3 product = GhostPlaybackLogic.ComputeLocalAngularVelocityDegPerSec(
                previous, current, dt);

            // Ground truth, in native Unity math the production code is not allowed to call.
            Quaternion delta = Quaternion.Inverse(previous) * current;
            delta.ToAngleAxis(out float truthAngle, out Vector3 truthAxis);
            if (truthAngle > 180f)
            {
                // ToAngleAxis reports in [0, 360]; the production math takes the shortest arc.
                truthAngle = 360f - truthAngle;
                truthAxis = -truthAxis;
            }
            Vector3 truth = truthAxis.normalized * (float)(truthAngle / dt);

            InGameAssert.IsGreaterThan(product.magnitude, 1e-3f,
                "S3 CONVENTION: the product answered a zero rate for a 30-degree rotation over " +
                dt + " s; expected ~" + truth.magnitude + " deg/s about " + truthAxis + ".");

            float dot = Vector3.Dot(product.normalized, truth.normalized);
            InGameAssert.IsGreaterThan(dot, 0.999f,
                "S3 CONVENTION (SIGN / HANDEDNESS): the hand-rolled quaternion product's axis must " +
                "match Unity's Inverse(previous)*current -> ToAngleAxis. Got " + product +
                " against ground truth " + truth + " (dot=" + dot + "). A dot near -1 means the " +
                "vector part is NEGATED — every gimbal and control surface would deflect the wrong " +
                "way at identical magnitude. A dot near " + Mathf.Cos(50f * Mathf.Deg2Rad) +
                " means the operands are the wrong way round (world delta c*conj(p) instead of the " +
                "local delta conj(p)*c).");

            float error = (product - truth).magnitude;
            InGameAssert.IsLessThan(error, truth.magnitude * 0.01f + 0.05f,
                "S3 CONVENTION: the product must equal Unity's ground truth componentwise, not just " +
                "in direction. Got " + product + " against " + truth + " (error " + error +
                " deg/s).");

            ParsekLog.Info("TestRunner",
                "PlaybackFidelity attitude-derivative convention: product=" + product +
                " unityTruth=" + truth + " dot=" + dot.ToString("R") + " err=" + error.ToString("R"));
        }

        /// <summary>
        /// Wheel steering calipers must park STRAIGHT across a loop cycle. The angle is per-cycle
        /// state that <c>ResetForLoopCycle</c> zeroes, but only
        /// <c>ApplyRoboticSpawnBaseline</c>'s WheelSteeringHeading branch can put the TRANSFORM
        /// back — and <c>ApplyRoboticPose</c> deliberately writes nothing for this mode, so without
        /// that branch a caliper left turned 30 degrees stays turned while the numbers claim zero.
        /// That is the M4 carry-over class, on a transform a player looks straight at.
        /// </summary>
        [InGameTest(Category = "PlaybackFidelity", Scene = GameScenes.FLIGHT,
            Description = "A steered wheel maps to WheelSteeringHeading, ignores recorded steering scalars, and parks straight on a loop cycle")]
        public void WheelSteeringIsDerivedAndParksOnLoopCycle()
        {
            GhostBuildResult build = null;
            try
            {
                RoboticGhostInfo steering = null;
                GhostPlaybackState state = null;
                string usedPart = null;

                foreach (string partName in CandidateSteeringParts())
                {
                    build = BuildGhost(partName, "parsektest-wheel-steering", out state);
                    if (build != null)
                    {
                        steering = FindSteeringInfo(state);
                        if (steering != null && steering.servoTransform != null)
                        {
                            usedPart = partName;
                            break;
                        }
                        steering = null;
                    }
                    DestroyBuild(ref build);
                }

                if (steering == null)
                {
                    InGameAssert.Skip(
                        "no loaded part produced a ghost carrying a ModuleWheelSteering whose " +
                        "caliperTransformName resolves; needs a stock steered rover wheel");
                    return;
                }

                Quaternion parked = steering.stowedRot;

                // ARM 1 — a recorded steering scalar must be IGNORED. It was an unsigned steering
                // INPUT, not a caliper angle; consuming it would resurrect the wheel-motor bug.
                steering.servoTransform.localRotation = parked;
                double eventUT = Planetarium.GetUniversalTime();
                var scalarRec = BuildSinglePartFixture(usedPart, "parsektest-steering-scalar", eventUT);
                scalarRec.PartEvents.Add(new PartEvent
                {
                    ut = eventUT,
                    partPersistentId = steering.partPersistentId,
                    moduleIndex = steering.moduleIndex,
                    eventType = PartEventType.RoboticPositionSample,
                    partName = usedPart,
                    value = 25f
                });
                state.partEventIndex = 0;
                GhostPlaybackLogic.ApplyPartEvents(0, scalarRec, eventUT + 1.0, state);
                InGameAssert.IsTrue(steering.servoTransform.localRotation == parked,
                    "S3: a recorded ModuleWheelSteering scalar must not move the caliper — the " +
                    "visual is derived from the ghost's ground-track heading. '" + usedPart +
                    "' moved to " + steering.servoTransform.localRotation + ".");

                // ARM 2 — an applied steering angle must be undone by the loop-cycle park.
                steering.steeringAngleDegrees = 27f;
                steering.servoTransform.localRotation =
                    parked * Quaternion.AngleAxis(27f, Vector3.up);
                InGameAssert.IsGreaterThan(
                    Quaternion.Angle(parked, steering.servoTransform.localRotation), 1f,
                    "fixture precondition: the caliper must actually be turned before the loop reset");

                GhostPlaybackLogic.ResetForLoopCycle(state, state.loopCycleIndex + 1);
                InGameAssert.AreEqual(0f, steering.steeringAngleDegrees,
                    "M4 CLASS: ResetForLoopCycle must zero the eased caliper angle");
                GhostPlaybackLogic.RestoreRoboticSpawnBaselines(state);
                InGameAssert.IsLessThan(
                    Quaternion.Angle(parked, steering.servoTransform.localRotation), 0.05f,
                    "M4 CLASS: the caliper TRANSFORM must be back at its parked rotation. " +
                    "ApplyRoboticPose writes nothing for WheelSteeringHeading, so the dedicated " +
                    "branch in ApplyRoboticSpawnBaseline is the only thing that can park it.");

                ParsekLog.Info("TestRunner",
                    "PlaybackFidelity wheel steering: part='" + usedPart + "' mode=" +
                    steering.visualMode + " parked=" + steering.servoTransform.localRotation);
            }
            finally
            {
                DestroyBuild(ref build);
            }
        }

        /// <summary>
        /// A tracking solar pivot must aim at the Sun once the panel is FULLY DEPLOYED, and must
        /// stay at its neutral pose while stowed or mid-transition (risk 4's precedence: transition
        /// beats tracking). A panel that tracks while folded is geometry rotating inside its own
        /// housing.
        /// </summary>
        [InGameTest(Category = "PlaybackFidelity", Scene = GameScenes.FLIGHT,
            Description = "A tracking solar pivot aims at the Sun only once fully deployed and holds neutral while stowed")]
        public void SunTrackingPivotAimsOnlyWhenFullyDeployed()
        {
            if (Planetarium.fetch?.Sun == null)
            {
                InGameAssert.Skip("no Planetarium.fetch.Sun in this scene to aim at");
                return;
            }

            GhostBuildResult build = null;
            try
            {
                SunTrackingGhostInfo tracker = null;
                GhostPlaybackState state = null;
                string usedPart = null;

                foreach (string partName in CandidateTrackingPanelParts())
                {
                    build = BuildGhost(partName, "parsektest-sun-tracking", out state);
                    if (build != null && state.synthesizedMotionInfos?.sunTrackers != null
                        && state.synthesizedMotionInfos.sunTrackers.Count > 0)
                    {
                        tracker = state.synthesizedMotionInfos.sunTrackers[0];
                        if (tracker.pivotTransform != null) { usedPart = partName; break; }
                        tracker = null;
                    }
                    DestroyBuild(ref build);
                }

                if (tracker == null)
                {
                    InGameAssert.Skip(
                        "no loaded part produced a ghost with a resolvable TRACKING " +
                        "ModuleDeployablePart pivot; needs a stock tracking solar panel " +
                        "(isTracking = true with a pivotName that resolves on the cloned model)");
                    return;
                }

                double t0 = Planetarium.GetUniversalTime();
                Quaternion neutral = tracker.neutralRotation;
                tracker.pivotTransform.localRotation = neutral;

                // ARM 1 — STOWED. Even with the derivative seeded, the pivot must not move.
                SetDeployedState(state, tracker.partPersistentId, deployed: false);
                GhostPlaybackLogic.UpdateSynthesizedMotion(state, t0);
                for (int i = 1; i <= 10; i++)
                    GhostPlaybackLogic.UpdateSynthesizedMotion(state, t0 + i * 0.2);
                float stowedDrift = Quaternion.Angle(neutral, tracker.pivotTransform.localRotation);
                InGameAssert.IsLessThan(stowedDrift, 0.05f,
                    "S3 PRECEDENCE: a stowed panel must not track. '" + usedPart + "' drifted " +
                    stowedDrift + " degrees from neutral — check " +
                    "IsDeployablePoseFullyDeployedForPart.");

                // ARM 2 — DEPLOYED. Now it must aim, and hold once aimed.
                SetDeployedState(state, tracker.partPersistentId, deployed: true);
                for (int i = 11; i <= 60; i++)
                    GhostPlaybackLogic.UpdateSynthesizedMotion(state, t0 + i * 0.2);

                InGameAssert.IsTrue(tracker.hasAimed,
                    "S3: a fully deployed tracking panel must resolve an aim angle. '" + usedPart +
                    "' never did — either the Sun projects onto the pivot axis (hold, not a bug) " +
                    "or the deployed gate is closed.");

                float aimed = Quaternion.Angle(neutral, tracker.pivotTransform.localRotation);
                Quaternion settledRotation = tracker.pivotTransform.localRotation;
                for (int i = 61; i <= 70; i++)
                    GhostPlaybackLogic.UpdateSynthesizedMotion(state, t0 + i * 0.2);
                float holdDelta = Quaternion.Angle(
                    settledRotation, tracker.pivotTransform.localRotation);

                InGameAssert.IsLessThan(holdDelta, 1.5f,
                    "S3: once aimed, a tracking pivot must HOLD (the Sun moves arcseconds per " +
                    "second, and the slew is rate-limited). '" + usedPart + "' moved " + holdDelta +
                    " degrees in 2 seconds of settled tracking, which reads as hunting.");

                ParsekLog.Info("TestRunner",
                    "PlaybackFidelity sun tracking: part='" + usedPart + "' stowedDrift=" +
                    stowedDrift.ToString("R") + " aimed=" + aimed.ToString("R") + " holdDelta=" +
                    holdDelta.ToString("R"));
            }
            finally
            {
                DestroyBuild(ref build);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Fixture helpers
        // ──────────────────────────────────────────────────────────────────────

        private static GhostBuildResult BuildGhost(
            string partName, string idPrefix, out GhostPlaybackState state)
            => BuildGhost(partName, idPrefix, out state, out _);

        /// <summary>
        /// Builds a one-part ghost from a live PartLoader prefab and populates the playback state
        /// exactly the way a real spawn does, so every cell below drives PRODUCTION code paths
        /// rather than hand-assembled info objects.
        /// </summary>
        private static GhostBuildResult BuildGhost(
            string partName, string idPrefix, out GhostPlaybackState state, out Recording rec)
        {
            state = null;
            rec = BuildSinglePartFixture(partName, idPrefix, Planetarium.GetUniversalTime());

            GhostBuildResult build = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                rec, "ParsekTest_" + idPrefix);
            if (build == null || build.root == null)
                return null;

            state = new GhostPlaybackState
            {
                vesselName = idPrefix,
                recordingId = rec.RecordingId,
                ghost = build.root
            };
            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, build, rec);
            return build;
        }

        private static void DestroyBuild(ref GhostBuildResult build)
        {
            if (build != null && build.root != null)
                Object.Destroy(build.root);
            build = null;
        }

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

        private static float SumStartSpeed(List<ParticleSystem> systems)
        {
            if (systems == null) return 0f;
            float total = 0f;
            for (int i = 0; i < systems.Count; i++)
            {
                if (systems[i] == null) continue;
                var main = systems[i].main;
                total += main.startSpeedMultiplier + main.startSizeMultiplier;
            }
            return total;
        }

        private static float SumEmitterMaxEmission(List<KspEmitterRef> emitters)
        {
            if (emitters == null) return 0f;
            float total = 0f;
            for (int i = 0; i < emitters.Count; i++)
            {
                KspEmitterRef r = emitters[i];
                if (!r.magnitudeBaselineCaptured || r.emitter == null || r.maxEmissionField == null)
                    continue;
                try
                {
                    total += System.Convert.ToSingle(r.maxEmissionField.GetValue(r.emitter));
                }
                catch (System.Exception)
                {
                    // A field we captured but can no longer read is not a test failure — the
                    // production applier degrades the same way. The ParticleSystem arm still holds.
                }
            }
            return total;
        }

        /// <summary>The CAPTURED build-time magnitude, summed the same way
        /// <see cref="SumStartSpeed"/> sums the live one — so the two are directly comparable and a
        /// "scaled differs from baseline" assertion has a real reference to stand on.</summary>
        private static float SumBaselineStartSpeed(List<GhostFxMagnitudeBaseline> baselines)
        {
            if (baselines == null) return 0f;
            float total = 0f;
            for (int i = 0; i < baselines.Count; i++)
            {
                GhostFxMagnitudeBaseline b = baselines[i];
                if (!b.captured) continue;
                total += b.startSpeedMultiplier + b.startSizeMultiplier;
            }
            return total;
        }

        /// <summary>True when at least one captured emitter's baseline rate has room to quantise
        /// DOWN — the precondition for a strict "scaled &lt; baseline" assertion on an integer
        /// field. See the call sites for why one such emitter is enough.</summary>
        private static bool AnyEmitterBaselineAtLeast(List<KspEmitterRef> emitters, float threshold)
        {
            if (emitters == null) return false;
            for (int i = 0; i < emitters.Count; i++)
            {
                KspEmitterRef r = emitters[i];
                if (!r.magnitudeBaselineCaptured || r.maxEmissionField == null) continue;
                if (r.baselineMaxEmission >= threshold) return true;
            }
            return false;
        }

        private static float SumBaselineMaxEmission(List<KspEmitterRef> emitters)
        {
            if (emitters == null) return 0f;
            float total = 0f;
            for (int i = 0; i < emitters.Count; i++)
            {
                KspEmitterRef r = emitters[i];
                if (!r.magnitudeBaselineCaptured || r.maxEmissionField == null) continue;
                total += r.baselineMaxEmission;
            }
            return total;
        }

        private static EngineGhostInfo FindScalableEngine(GhostPlaybackState state)
        {
            if (state?.engineInfos == null) return null;
            foreach (var kv in state.engineInfos)
            {
                EngineGhostInfo info = kv.Value;
                if (info == null || info.particleSystems.Count == 0) continue;
                for (int i = 0; i < info.particleBaselines.Count; i++)
                    if (info.particleBaselines[i].captured) return info;
            }
            return null;
        }

        private static RcsGhostInfo FindScalableRcs(GhostPlaybackState state)
        {
            if (state?.rcsInfos == null) return null;
            foreach (var kv in state.rcsInfos)
            {
                RcsGhostInfo info = kv.Value;
                if (info == null || info.particleSystems.Count == 0) continue;
                for (int i = 0; i < info.particleBaselines.Count; i++)
                    if (info.particleBaselines[i].captured) return info;
            }
            return null;
        }

        /// <summary>A deployable whose stowed and deployed poses actually differ — the only shape
        /// an interpolation assertion can be made against.</summary>
        private static DeployableGhostInfo FindMovingDeployable(GhostPlaybackState state)
        {
            if (state?.deployableInfos == null) return null;
            foreach (var kv in state.deployableInfos)
            {
                DeployableGhostInfo info = kv.Value;
                if (info?.transforms == null) continue;
                for (int i = 0; i < info.transforms.Count; i++)
                {
                    DeployableTransformState ts = info.transforms[i];
                    if (ts.t != null && (ts.deployedPos - ts.stowedPos).sqrMagnitude > 1e-6f)
                        return info;
                }
            }
            return null;
        }

        private static DeployableTransformState FindMovingTransform(DeployableGhostInfo info)
        {
            for (int i = 0; i < info.transforms.Count; i++)
            {
                DeployableTransformState ts = info.transforms[i];
                if (ts.t != null && (ts.deployedPos - ts.stowedPos).sqrMagnitude > 1e-6f)
                    return ts;
            }
            return info.transforms[0];
        }

        private static RoboticGhostInfo FindSteeringInfo(GhostPlaybackState state)
        {
            if (state?.roboticInfos == null) return null;
            foreach (var kv in state.roboticInfos)
            {
                if (kv.Value != null
                    && kv.Value.visualMode == RoboticVisualMode.WheelSteeringHeading)
                {
                    return kv.Value;
                }
            }
            return null;
        }

        private static void LightEveryEngine(GhostPlaybackState state)
        {
            if (state?.engineInfos == null) return;
            foreach (var kv in state.engineInfos)
                if (kv.Value != null) kv.Value.currentPower = 1f;
        }

        private static void SetDeployedState(
            GhostPlaybackState state, uint partPersistentId, bool deployed)
        {
            if (state?.deployableInfos == null) return;
            if (!state.deployableInfos.TryGetValue(partPersistentId, out DeployableGhostInfo info)
                || info == null)
            {
                return;
            }
            GhostPlaybackLogic.ApplyDeployableState(
                state, new PartEvent { partPersistentId = partPersistentId }, deployed);
        }

        // ---- prefab discovery (never hardcoded part names) -------------------

        private static List<string> CandidateEngineParts()
        {
            return DiscoverParts(prefab =>
                prefab.FindModuleImplementing<ModuleEngines>() != null
                && prefab.partInfo?.partConfig != null);
        }

        private static List<string> CandidateRcsParts()
        {
            return DiscoverParts(prefab =>
                prefab.FindModuleImplementing<ModuleRCS>() != null
                && prefab.partInfo?.partConfig != null);
        }

        private static List<string> CandidateDeployableParts()
        {
            return DiscoverParts(prefab =>
                prefab.FindModuleImplementing<ModuleDeployablePart>() != null);
        }

        private static List<string> CandidateGimbalParts()
        {
            return DiscoverParts(prefab => HasModuleNamed(prefab, "ModuleGimbal"));
        }

        private static List<string> CandidateSteeringParts()
        {
            return DiscoverParts(prefab => HasModuleNamed(prefab, "ModuleWheelSteering"));
        }

        private static List<string> CandidateTrackingPanelParts()
        {
            return DiscoverParts(prefab =>
            {
                ModuleDeployablePart deployable = prefab.FindModuleImplementing<ModuleDeployablePart>();
                return deployable != null && deployable.isTracking
                    && !string.IsNullOrEmpty(deployable.pivotName);
            });
        }

        private static bool HasModuleNamed(Part prefab, string moduleName)
        {
            if (prefab?.Modules == null) return false;
            for (int i = 0; i < prefab.Modules.Count; i++)
            {
                PartModule m = prefab.Modules[i];
                if (m != null && string.Equals(m.moduleName, moduleName, System.StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Up to <see cref="MaxFixtureBuildAttempts"/> part names matching a cheap prefab predicate.
        /// The cap matters: building a ghost is the expensive step and a cell that walked every
        /// loaded part would dominate the batch's wall time on a modded install.
        /// </summary>
        private static List<string> DiscoverParts(System.Func<Part, bool> predicate)
        {
            var result = new List<string>();
            List<AvailablePart> parts = PartLoader.LoadedPartsList;
            if (parts == null) return result;

            for (int i = 0; i < parts.Count && result.Count < MaxFixtureBuildAttempts; i++)
            {
                AvailablePart ap = parts[i];
                Part prefab = ap?.partPrefab;
                if (prefab == null || string.IsNullOrEmpty(ap.name)) continue;
                bool matches;
                try { matches = predicate(prefab); }
                catch (System.Exception) { continue; }
                if (matches) result.Add(ap.name);
            }
            return result;
        }
    }
}
