using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Category <c>PartEventFidelity</c> — the live half of P8, the wave that recorded the three
    /// signals the 2026-08-09 part-action audit found MISSING rather than wrong. Every claim here is
    /// one the headless suite cannot make, because each needs a real PartLoader prefab, a real
    /// cloned <c>Transform</c> subtree, or a real Unity <c>ParticleSystem</c>:
    ///
    ///   * S6 (BROKEN deployable) hides a mesh subtree. Headless cells pin the recorder's truth
    ///     table, the seeds and the snapshot parse; only a scene can prove the break transform
    ///     RESOLVED onto a ghost and that hiding it, un-hiding it on repair, and re-showing it on a
    ///     loop cycle all actually move a GameObject's active state. The applier is not even
    ///     CALLABLE headless — comparing a Transform against null routes through a
    ///     UnityEngine.Object ECall xUnit cannot host.
    ///   * S7 (converter running loop) samples a real animation clip at twelve phases off a temp
    ///     model clone. Headless pins the phase arithmetic; only a scene proves a stock clip yields
    ///     moving transforms at all and that driving it writes them.
    ///   * S4 (EVA jetpack) builds a Parsek-owned ParticleSystem lazily. Headless pins the gate;
    ///     only a scene proves the system builds and plays.
    ///   * The GOO CELL is the wave's one VERIFICATION cell rather than a new-feature one. It pins
    ///     the §2 CORRECTION: the science-experiment deploy visual was ALREADY recorded before P8,
    ///     through the ModuleAnimateGeneric path, which is why P8 deliberately adds no science
    ///     events. That claim had never been showcased, and an unshowcased claim is the kind that
    ///     rots — so it is asserted here instead of asserted in a doc.
    ///
    /// House rules honoured, following <see cref="PlaybackFidelityInGameTests"/> verbatim: every
    /// cell builds its own single-part fixture from a prefab DISCOVERED at run time (never a
    /// hardcoded part name), a cell that cannot self-set-up calls <see cref="InGameAssert.Skip"/>
    /// naming the context it needed rather than asserting against an assumed one, and each cell
    /// destroys its ghost in a finally block.
    /// </summary>
    public class PartEventFidelityInGameTests
    {
        private readonly InGameTestRunner runner;
        public PartEventFidelityInGameTests(InGameTestRunner runner) { this.runner = runner; }

        /// <summary>Ghost part pid for the single-part fixtures — the first slot
        /// <c>VesselSnapshotBuilder.AddPart</c> assigns (100000 + 0 * 1111). A single-part fixture
        /// must use it or every event's playback lookup silently misses.</summary>
        private const uint FixturePartPid = 100000u;

        /// <summary>How many candidate prefabs a cell will actually BUILD before giving up. Ghost
        /// building is the expensive step, so the candidate lists are pre-filtered by cheap module
        /// tests and only the first few survivors are built.</summary>
        private const int MaxFixtureBuildAttempts = 4;

        // ──────────────────────────────────────────────────────────────────────
        //  S6 — the BROKEN deployable
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The whole S6 visual round trip on a real panel: break HIDES the subtree, repair
        /// (DeployableRetracted) RESTORES it, and a loop cycle restores it again.
        ///
        /// If this reds while the headless S6 cells stay green, the recorder and the seeds are fine
        /// and the failure is in RESOLUTION — check
        /// <c>GhostVisualBuilder.TryResolveDeployableBreakSubtreeRoot</c>, and in particular that it
        /// still falls back from <c>breakName</c> to <c>pivotName</c>. Almost no stock part sets
        /// breakName (stock applies that default in OnStart, which a prefab has not run), so losing
        /// the fallback makes the whole feature a silent no-op rather than an error.
        /// </summary>
        [InGameTest(Category = "PartEventFidelity", Scene = GameScenes.FLIGHT,
            Description = "A broken deployable hides its break subtree, a repair restores it, and a loop cycle restores it")]
        public void BrokenDeployableHidesItsSubtreeAndRepairRestoresIt()
        {
            GhostBuildResult build = null;
            try
            {
                DeployableGhostInfo info = null;
                GhostPlaybackState state = null;
                Recording rec = null;
                string usedPart = null;

                foreach (string partName in CandidateDeployableParts())
                {
                    build = BuildGhost(partName, "parsektest-broken-panel", out state, out rec);
                    if (build != null)
                    {
                        info = FindBreakableDeployable(state);
                        if (info != null) { usedPart = partName; break; }
                    }
                    DestroyBuild(ref build);
                }

                if (info == null)
                {
                    InGameAssert.Skip(
                        "no loaded ModuleDeployablePart produced a ghost with a resolvable break " +
                        "subtree; needs a stock solar panel / antenna / radiator whose breakName (or " +
                        "its pivotName fallback) names a transform that clones into the ghost");
                    return;
                }

                Transform breakRoot = info.breakSubtreeRoot;
                InGameAssert.IsTrue(breakRoot.gameObject.activeSelf,
                    "S6: '" + usedPart + "' break subtree '" + breakRoot.name + "' must start ACTIVE — " +
                    "a freshly built ghost has an intact panel, and anything else means the builder " +
                    "hid it at build time");

                // 1. THE BREAK.
                bool appliedBreak = GhostPlaybackLogic.ApplyDeployableBrokenState(
                    state, FixturePartPid, broken: true);
                InGameAssert.IsTrue(appliedBreak,
                    "S6: ApplyDeployableBrokenState found no DeployableGhostInfo for pid " +
                    FixturePartPid + " on '" + usedPart + "' — the fixture pid and the ghost's pid " +
                    "have diverged, so no P8 event could ever reach this part");
                InGameAssert.IsFalse(breakRoot.gameObject.activeSelf,
                    "S6: '" + usedPart + "' break subtree '" + breakRoot.name + "' is still ACTIVE " +
                    "after DeployableBroken — the panel would replay intact, which is the exact bug " +
                    "S6 exists to fix");
                InGameAssert.IsTrue(info.breakSubtreeHidden,
                    "S6: the breakSubtreeHidden FLAG did not follow the hide. The flag is what closes " +
                    "the sun-tracking gate, so a hidden panel with a false flag would still slew " +
                    "its pivot toward the Sun for the rest of the recording");

                // 2. THE REPAIR, driven through the event the recorder actually emits. Stock
                //    DoRepair lands on RETRACTED, and DeployableRetracted is what the recorder
                //    writes — so this is the production path, not a direct un-hide call.
                var retractEvt = new PartEvent
                {
                    partPersistentId = FixturePartPid,
                    eventType = PartEventType.DeployableRetracted,
                    ut = Planetarium.GetUniversalTime()
                };
                GhostPlaybackLogic.ApplyDeployableState(state, retractEvt, deployed: false, immediate: false);
                InGameAssert.IsTrue(breakRoot.gameObject.activeSelf,
                    "S6: '" + usedPart + "' break subtree is still hidden after a DeployableRetracted " +
                    "(the event a repair emits) — a repaired panel would replay as still missing");
                InGameAssert.IsFalse(info.breakSubtreeHidden,
                    "S6: breakSubtreeHidden stayed true through the repair");

                // THE REPAIR MUST SNAP, not animate. Rule 1 leaves the pivot at deployFraction = 1
                // when the panel breaks (the recorder does not retract it), so an ANIMATED
                // application here would re-show the panel fully EXTENDED and fold it politely shut
                // over the clip length — while stock DoRepair lands on RETRACTED instantly with a
                // freshly instantiated subtree. Caught on review: the applier was passing
                // immediate: false, so the un-hide branch now forces the snap.
                InGameAssert.IsFalse(info.transitionActive,
                    "S6: '" + usedPart + "' armed an ANIMATED transition on repair. The panel would " +
                    "reappear fully extended and fold shut over " + info.clipLengthSeconds +
                    "s; stock DoRepair is instant, so the un-hide must snap");
                InGameAssert.IsTrue(info.deployFraction < 1e-3f,
                    "S6: '" + usedPart + "' repaired to deployFraction=" + info.deployFraction +
                    " instead of the stowed 0 — the panel came back still extended");

                // 3. THE LOOP CYCLE. Break it again, then run the real loop-restart pair. Without
                //    the explicit re-show, cycle 2 of a looping replay starts with the panel already
                //    gone — the break would be shown once and then permanent.
                GhostPlaybackLogic.ApplyDeployableBrokenState(state, FixturePartPid, broken: true);
                InGameAssert.IsFalse(breakRoot.gameObject.activeSelf,
                    "S6: re-breaking the panel did not hide it again (the applier is meant to be " +
                    "absolute-state and idempotent)");

                GhostPlaybackLogic.ResetForLoopCycle(state, newCycleIndex: 1);
                GhostPlaybackLogic.ReapplySpawnTimeModuleBaselinesForLoopCycle(state, rec);

                InGameAssert.IsTrue(breakRoot.gameObject.activeSelf,
                    "S6: '" + usedPart + "' break subtree is still hidden after a loop cycle. Every " +
                    "cycle after the first would start with the panel already broken, before the " +
                    "recording's own DeployableBroken had replayed");
                InGameAssert.IsFalse(info.breakSubtreeHidden,
                    "S6: breakSubtreeHidden survived the loop-cycle reset");

                ParsekLog.Info("InGameTest",
                    "S6 break/repair/loop round trip on '" + usedPart + "': subtree='" +
                    breakRoot.name + "' transforms=" + (info.transforms?.Count ?? 0));
            }
            finally
            {
                DestroyBuild(ref build);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  S7 — the converter running loop
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A real running clip, sampled and driven: the loop must MOVE its transforms while active,
        /// land back on the same pose one whole clip later (the cyclic property), and STOP dead when
        /// the converter is deactivated.
        ///
        /// The "one clip later is the same pose" assertion is the one that catches a broken wrap. A
        /// sampler that stored a duplicate endpoint, or a blend that ran to phaseCount-1 instead of
        /// wrapping to 0, both still produce motion — they just stutter once per revolution, which
        /// is exactly the kind of defect that survives a human glance at a spinning drill.
        /// </summary>
        [InGameTest(Category = "PartEventFidelity", Scene = GameScenes.FLIGHT,
            Description = "A converter running loop moves its transforms while active, wraps cyclically, and stops on deactivate")]
        public void ConverterLoopAdvancesWhileActiveAndStopsOnDeactivate()
        {
            GhostBuildResult build = null;
            try
            {
                ConverterLoopGhostInfo loop = null;
                GhostPlaybackState state = null;
                string usedPart = null;

                foreach (string partName in CandidateRunningAnimationParts())
                {
                    build = BuildGhost(partName, "parsektest-converter-loop", out state);
                    if (build != null)
                    {
                        loop = FindConverterLoop(state);
                        if (loop != null) { usedPart = partName; break; }
                    }
                    DestroyBuild(ref build);
                }

                if (loop == null)
                {
                    InGameAssert.Skip(
                        "no loaded ModuleAnimationGroup with a non-empty activeAnimationName produced " +
                        "a ghost with sampled looping transforms; needs a stock drill / ISRU whose " +
                        "running clip moves geometry that clones into the ghost");
                    return;
                }

                ConverterLoopTransformState ts = loop.transforms[0];
                double t0 = Planetarium.GetUniversalTime();

                // Activate through the production applier, which is what a ConverterActivated event
                // calls, so the phase origin is the recorded UT exactly as in a real replay.
                bool applied = GhostPlaybackLogic.ApplyConverterLoopState(
                    state, FixturePartPid, active: true, activeSinceUT: t0);
                InGameAssert.IsTrue(applied,
                    "S7: ApplyConverterLoopState found no loop for pid " + FixturePartPid +
                    " on '" + usedPart + "' — the fixture pid and the built loop's pid have diverged");

                float clip = loop.clipLengthSeconds;
                InGameAssert.IsTrue(clip > 0f,
                    "S7: '" + usedPart + "' running clip length is " + clip +
                    "s; a non-positive length parks the loop at phase 0 forever");

                GhostPlaybackLogic.UpdateSynthesizedMotion(state, t0);
                Quaternion atStart = ts.t.localRotation;
                Vector3 atStartPos = ts.t.localPosition;

                // A QUARTER of the way round: far enough that any real clip has moved.
                GhostPlaybackLogic.UpdateSynthesizedMotion(state, t0 + clip * 0.25);
                Quaternion atQuarter = ts.t.localRotation;
                Vector3 atQuarterPos = ts.t.localPosition;

                float rotMoved = Quaternion.Angle(atStart, atQuarter);
                float posMoved = (atQuarterPos - atStartPos).magnitude;
                InGameAssert.IsTrue(rotMoved > 0.01f || posMoved > 1e-4f,
                    "S7: '" + usedPart + "' running loop did not move a quarter of the way through " +
                    "its clip (rotDelta=" + rotMoved + " deg posDelta=" + posMoved + " m). A drill " +
                    "that 'runs' by standing still is the pre-P8 behaviour this cell exists to reject");

                // THE CYCLIC PROPERTY: exactly one clip later is the same pose.
                GhostPlaybackLogic.UpdateSynthesizedMotion(state, t0 + clip);
                float wrapRotDelta = Quaternion.Angle(atStart, ts.t.localRotation);
                float wrapPosDelta = (ts.t.localPosition - atStartPos).magnitude;
                InGameAssert.IsTrue(wrapRotDelta < 0.5f && wrapPosDelta < 1e-3f,
                    "S7: '" + usedPart + "' loop did not WRAP — one full clip after the start the " +
                    "pose differs by rot=" + wrapRotDelta + " deg pos=" + wrapPosDelta + " m. The " +
                    "clip is cyclic, so a mismatch means the phase wrap or the blend pair is wrong " +
                    "and the loop stutters once per revolution");

                // THE STOP. A deactivated loop must be left exactly where it stopped — parked
                // mid-stroke is what a switched-off drill looks like, not snapped to a home pose.
                GhostPlaybackLogic.UpdateSynthesizedMotion(state, t0 + clip * 0.4);
                Quaternion whenStopped = ts.t.localRotation;
                GhostPlaybackLogic.ApplyConverterLoopState(
                    state, FixturePartPid, active: false, activeSinceUT: t0 + clip * 0.4);
                InGameAssert.IsFalse(loop.active, "S7: the loop is still marked active after deactivate");

                GhostPlaybackLogic.UpdateSynthesizedMotion(state, t0 + clip * 0.9);
                float driftAfterStop = Quaternion.Angle(whenStopped, ts.t.localRotation);
                InGameAssert.IsTrue(driftAfterStop < 0.5f,
                    "S7: '" + usedPart + "' kept turning after ConverterDeactivated (drift=" +
                    driftAfterStop + " deg) — a switched-off drill would spin forever");

                ParsekLog.Info("InGameTest",
                    "S7 loop on '" + usedPart + "': transforms=" + loop.transforms.Count +
                    " clip=" + clip + "s quarterRot=" + rotMoved + "deg wrapRot=" + wrapRotDelta +
                    "deg driftAfterStop=" + driftAfterStop + "deg");
            }
            finally
            {
                DestroyBuild(ref build);
            }
        }

        /// <summary>
        /// The EMPTY-DEPLOY-NAME case, which is a separate cell rather than a branch of the one
        /// above so that it can skip independently.
        ///
        /// The large ISRU and the orbital scanner both ship a ModuleAnimationGroup with an EMPTY
        /// <c>deployAnimationName</c> and only a running one (<c>ProcessorLarge_running</c> /
        /// <c>miniscanner</c>). Anything that resolved the running clip via
        /// <c>TryGetAnimationGroupDeployAnimation</c> would skip them, because that helper requires a
        /// non-empty deploy name — and the part it would skip is the biggest ISRU in the game. This
        /// cell is the live proof that the loop builder is independent of the deploy name.
        /// </summary>
        [InGameTest(Category = "PartEventFidelity", Scene = GameScenes.FLIGHT,
            Description = "A running loop resolves even when the part's ModuleAnimationGroup has an empty deployAnimationName (the large ISRU shape)")]
        public void ConverterLoopResolvesForAPartWithNoDeployAnimation()
        {
            GhostBuildResult build = null;
            try
            {
                ConverterLoopGhostInfo loop = null;
                GhostPlaybackState state = null;
                string usedPart = null;

                foreach (string partName in CandidateRunningOnlyAnimationParts())
                {
                    build = BuildGhost(partName, "parsektest-loop-nodeploy", out state);
                    if (build != null)
                    {
                        loop = FindConverterLoop(state);
                        if (loop != null) { usedPart = partName; break; }
                    }
                    DestroyBuild(ref build);
                }

                if (loop == null)
                {
                    InGameAssert.Skip(
                        "no loaded part has a ModuleAnimationGroup with an EMPTY deployAnimationName " +
                        "AND a running clip that yields ghost transforms; stock supplies this shape " +
                        "through the large ISRU (ProcessorLarge_running) and the orbital scanner " +
                        "(miniscanner), so this skip means neither is installed or neither's clip " +
                        "moves cloned geometry");
                    return;
                }

                InGameAssert.IsTrue(loop.transforms.Count > 0,
                    "S7: '" + usedPart + "' resolved a loop with no transforms");
                InGameAssert.IsTrue(loop.clipLengthSeconds > 0f,
                    "S7: '" + usedPart + "' loop has a non-positive clip length");

                ParsekLog.Info("InGameTest",
                    "S7 empty-deploy-name loop resolved on '" + usedPart + "': transforms=" +
                    loop.transforms.Count + " clip=" + loop.clipLengthSeconds + "s");
            }
            finally
            {
                DestroyBuild(ref build);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  S4 — the EVA jetpack plume
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The jetpack plume's lazy build and its three-flag gate, on a real kerbalEVA ghost.
        ///
        /// The gate is the point. Headless pins the truth table of
        /// <c>ShouldEmitEvaJetpackPlume</c>; this proves the particle system is actually BUILT when
        /// the gate first opens (it is built lazily, so an EVA ghost that never fires its pack
        /// allocates nothing), that it plays, and that a ragdoll shuts it off — the one place the
        /// ragdoll events earn their keep visually, given the ragdoll POSE is deliberately never
        /// replayed.
        /// </summary>
        [InGameTest(Category = "PartEventFidelity", Scene = GameScenes.FLIGHT,
            Description = "The EVA jetpack plume builds lazily when the gate opens, plays while thrusting, and is suppressed by a ragdoll")]
        public void EvaJetpackPlumeBuildsOnDemandAndGatesOnThrustAndRagdoll()
        {
            GhostBuildResult build = null;
            try
            {
                GhostPlaybackState state = null;
                string usedPart = null;

                foreach (string partName in CandidateKerbalParts())
                {
                    build = BuildGhost(partName, "parsektest-eva-plume", out state);
                    if (build != null) { usedPart = partName; break; }
                    DestroyBuild(ref build);
                }

                if (state == null)
                {
                    InGameAssert.Skip(
                        "no loaded KerbalEVA prefab produced a ghost; needs a stock kerbalEVA part " +
                        "for the plume to have a ghost root to parent itself to");
                    return;
                }

                // Nothing yet: no flags set, so the gate is shut and nothing should be allocated.
                GhostPlaybackLogic.ReconcileEvaJetpackPlume(state);
                InGameAssert.IsTrue(state.evaJetpackPlumeInfo == null,
                    "S4: a plume was built on '" + usedPart + "' before any EVA event — the build is " +
                    "meant to be lazy, so a kerbal who never fires his pack allocates no particle " +
                    "system at all");

                // Pack out, but idle: still shut.
                GhostPlaybackLogic.ApplyEvaState(state, PartEventType.EvaJetpackDeployed);
                InGameAssert.IsTrue(state.evaJetpackPlumeInfo == null,
                    "S4: deploying the pack alone built a plume — an idle jetpack emits nothing");

                // ── THE INACTIVE-HIERARCHY CASE, and it is here because the 2026-08-12 flight
                //    found it the hard way. Unity's ParticleSystem.Play() on a ghost that is not
                //    activeInHierarchy is a SILENT no-op: no throw, isPlaying stays false. That is
                //    the state the spawn-time prefix replay is ALWAYS in, so a ghost spawning
                //    mid-burst used to consume its thrust event into nothing and stay dark for the
                //    whole burst while the log claimed it was emitting.
                //
                //    Deactivating deliberately reproduces it, and asserts the two properties that
                //    make it safe now: the reconcile DEFERS (allocating nothing rather than building
                //    a system it cannot start), and the per-frame self-heal picks it up afterwards.
                state.ghost.SetActive(false);
                GhostPlaybackLogic.ApplyEvaState(state, PartEventType.EvaJetpackThrustStarted);
                InGameAssert.IsTrue(state.evaJetpackPlumeInfo == null,
                    "S4: a plume was BUILT while the ghost was inactive in the hierarchy. Play() " +
                    "cannot take there, so building is pure allocation for a system that would sit " +
                    "idle — the reconcile is supposed to defer and retry");
                InGameAssert.IsFalse(state.evaJetpackPlumeUnavailable,
                    "S4: the inactive hierarchy was recorded as a PERMANENT build failure. It is a " +
                    "retry-later, and marking it unavailable means the plume never appears for this " +
                    "ghost even once it is shown");

                // THE SELF-HEAL: activate the ghost the way production does, then drive the frame
                // hook. This is the exact production sequence a ghost spawning mid-burst follows.
                GhostPlaybackEngine.ActivateGhostVisualsIfNeededForTesting(state);
                GhostPlaybackLogic.UpdateEvaJetpackPlumeForFrame(state);

                if (state.evaJetpackPlumeUnavailable)
                {
                    InGameAssert.Skip(
                        "the additive particle shader (KSP/Particles/Additive) is unavailable in this " +
                        "install, which is the same condition that makes launch dust and reentry FX " +
                        "unbuildable; the gate logic is covered headless");
                    return;
                }

                InGameAssert.IsTrue(state.evaJetpackPlumeInfo != null,
                    "S4: the per-frame self-heal did not build a plume on '" + usedPart + "' after " +
                    "the ghost became active, with the gate still open (deployed + thrusting, no " +
                    "ragdoll) — a ghost that spawned mid-burst would stay dark for the whole burst");
                ParticleSystem ps = state.evaJetpackPlumeInfo.particles;
                InGameAssert.IsTrue(ps != null, "S4: the built plume carries no ParticleSystem");
                InGameAssert.IsTrue(ps.isPlaying,
                    "S4: the plume was built but is not playing while the recording says the kerbal " +
                    "is thrusting. If the ghost is active and this still fails, Play() is being " +
                    "refused for a reason nothing models — check the Warn line the reconcile now " +
                    "emits for exactly this case");

                // RAGDOLL suppresses it, even though thrust is still recorded as on.
                GhostPlaybackLogic.ApplyEvaState(state, PartEventType.EvaRagdollStarted);
                InGameAssert.IsFalse(ps.isPlaying,
                    "S4: the plume is still playing after EvaRagdollStarted. A tumbling kerbal is not " +
                    "flying — stock cuts thrust when the FSM enters ragdoll, and the recorder reads " +
                    "the two flags independently so this combination does occur on disk");

                // Back on his feet, still thrusting: it relights.
                GhostPlaybackLogic.ApplyEvaState(state, PartEventType.EvaRagdollEnded);
                InGameAssert.IsTrue(ps.isPlaying,
                    "S4: the plume did not relight after the ragdoll ended while thrust was still on");

                // And a stow shuts it off with thrust still nominally set — the physically
                // impossible pair the gate exists to resolve in favour of "no plume".
                GhostPlaybackLogic.ApplyEvaState(state, PartEventType.EvaJetpackStowed);
                InGameAssert.IsFalse(ps.isPlaying,
                    "S4: the plume is still playing with the pack STOWED — a stowed pack cannot thrust");

                ParsekLog.Info("InGameTest",
                    "S4 plume gate on '" + usedPart + "': built=" +
                    (state.evaJetpackPlumeInfo != null) + " emissionRate=" +
                    GhostVisualBuilder.EvaJetpackPlumeEmissionRate);
            }
            finally
            {
                DestroyBuild(ref build);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  The §2 correction — a VERIFICATION cell, not a new feature
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// THE GOO CELL. This exists to pin a NEGATIVE decision: P8 deliberately records no science
        /// events, because the science-experiment deploy visual was already recorded before P8 — and
        /// the audit's §2 wording ("<c>Deployed</c> ... gates the deploy animation") was imprecise
        /// enough to make that verdict look wrong.
        ///
        /// The mechanism, read off the shipped configs: the Goo canister and Science Jr each carry a
        /// SEPARATE <c>ModuleAnimateGeneric</c> named <c>Deploy</c>, wired to the experiment through
        /// <c>FxModules = 0</c>. <c>ModuleScienceExperiment</c> is NOT in
        /// <c>FlightRecorder.HasDedicatedAnimateHandler</c>'s list, so <c>CheckAnimateGenericState</c>
        /// polls that animation like any other — and playback animates it through the ordinary
        /// deployable family.
        ///
        /// So the cell asserts both halves of the claim on a live prefab: the recorder does NOT skip
        /// the part, and the ghost's deployable family really does move when the resulting event is
        /// applied. If it ever reds, the §2 verdict has stopped being true and the science timeline
        /// is owed a real look — which is precisely the signal a doc sentence could not give.
        /// </summary>
        [InGameTest(Category = "PartEventFidelity", Scene = GameScenes.FLIGHT,
            Description = "A science experiment's deploy animation is already recorded via the AnimateGeneric path and animates on a ghost (the P8 science-timeline WON'T verdict)")]
        public void ScienceExperimentDeployVisualIsAlreadyCoveredByTheAnimateGenericPath()
        {
            GhostBuildResult build = null;
            try
            {
                DeployableGhostInfo info = null;
                GhostPlaybackState state = null;
                string usedPart = null;
                Part usedPrefab = null;

                foreach (string partName in CandidateScienceAnimationParts())
                {
                    Part prefab = ResolvePrefab(partName);
                    if (prefab == null) continue;

                    // HALF ONE, and it needs no ghost: the recorder's AnimateGeneric poller skips
                    // any part with a dedicated animate handler. A science canister must NOT be
                    // skipped, or its deploy animation is recorded by nothing at all.
                    if (FlightRecorder.HasDedicatedAnimateHandler(prefab))
                    {
                        ParsekLog.Info("InGameTest",
                            "Goo cell: '" + partName + "' has a dedicated animate handler, so its " +
                            "AnimateGeneric is polled elsewhere; trying the next candidate");
                        continue;
                    }

                    build = BuildGhost(partName, "parsektest-science-deploy", out state);
                    if (build != null)
                    {
                        info = FindMovingDeployable(state);
                        if (info != null) { usedPart = partName; usedPrefab = prefab; break; }
                    }
                    DestroyBuild(ref build);
                }

                if (info == null)
                {
                    InGameAssert.Skip(
                        "no loaded ModuleScienceExperiment part paired with a ModuleAnimateGeneric " +
                        "produced a ghost whose sampled stowed and deployed poses differ in ANY " +
                        "component (position, rotation or scale); stock supplies this shape through " +
                        "the Goo canister and Science Jr (both animationName = Deploy, " +
                        "FxModules = 0), whose doors SWING - so a rotation-blind precondition here " +
                        "is a fixture bug, not an install property");
                    return;
                }

                InGameAssert.IsFalse(FlightRecorder.HasDedicatedAnimateHandler(usedPrefab),
                    "§2 CORRECTION BROKEN: '" + usedPart + "' now reports a dedicated animate " +
                    "handler, so CheckAnimateGenericState SKIPS it and its deploy animation is no " +
                    "longer recorded by anything. P8's WON'T verdict for the science timeline rests " +
                    "on this being false — revisit the verdict, do not silence this cell");

                // HALF TWO: the recorded event really does animate the ghost. Measured on ALL
                // THREE pose components, because a science canister's Deploy clip is a door SWING -
                // rotation only, with the positions identical at both ends. The first version of
                // this cell measured position alone and skipped itself into vacuity.
                DeployableTransformState ts = FindMovingTransform(info);
                MeasurePoseSpan(ts, out float posSpan, out float rotSpanDeg, out float scaleSpan);

                GhostPlaybackLogic.ApplyDeployableState(
                    state, new PartEvent { partPersistentId = FixturePartPid }, deployed: false);
                MeasurePoseError(ts, toDeployed: false,
                    out float stowedPosErr, out float stowedRotErr, out float stowedScaleErr);

                GhostPlaybackLogic.ApplyDeployableState(
                    state, new PartEvent { partPersistentId = FixturePartPid }, deployed: true);
                MeasurePoseError(ts, toDeployed: true,
                    out float deployedPosErr, out float deployedRotErr, out float deployedScaleErr);

                // Each component is judged against ITS OWN span with a small absolute floor, so a
                // component the clip does not move at all (span 0) is satisfied by staying put
                // rather than being held to an unreachable relative tolerance.
                bool stowedOk =
                    stowedPosErr <= posSpan * 0.1f + 1e-4f
                    && stowedRotErr <= rotSpanDeg * 0.1f + 0.05f
                    && stowedScaleErr <= scaleSpan * 0.1f + 1e-4f;
                bool deployedOk =
                    deployedPosErr <= posSpan * 0.1f + 1e-4f
                    && deployedRotErr <= rotSpanDeg * 0.1f + 0.05f
                    && deployedScaleErr <= scaleSpan * 0.1f + 1e-4f;

                string spans = "span(pos=" + posSpan + "m rot=" + rotSpanDeg + "deg scale=" + scaleSpan + ")";

                InGameAssert.IsTrue(stowedOk,
                    "§2: '" + usedPart + "' did not reach its STOWED pose — off by pos=" +
                    stowedPosErr + "m rot=" + stowedRotErr + "deg scale=" + stowedScaleErr +
                    " against " + spans);
                InGameAssert.IsTrue(deployedOk,
                    "§2: '" + usedPart + "' did not reach its DEPLOYED pose — off by pos=" +
                    deployedPosErr + "m rot=" + deployedRotErr + "deg scale=" + deployedScaleErr +
                    " against " + spans + ". The science deploy visual does NOT round-trip, so the " +
                    "claim that it is already covered by the AnimateGeneric path is false");

                ParsekLog.Info("InGameTest",
                    "Goo verification on '" + usedPart + "': dedicatedHandler=false " + spans +
                    " transforms=" + info.transforms.Count +
                    " stowedErr(pos=" + stowedPosErr + " rot=" + stowedRotErr + " scale=" + stowedScaleErr + ")" +
                    " deployedErr(pos=" + deployedPosErr + " rot=" + deployedRotErr + " scale=" + deployedScaleErr + ")");
            }
            finally
            {
                DestroyBuild(ref build);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Fixture helpers (shape lifted from PlaybackFidelityInGameTests)
        // ──────────────────────────────────────────────────────────────────────

        private static GhostBuildResult BuildGhost(
            string partName, string idPrefix, out GhostPlaybackState state)
            => BuildGhost(partName, idPrefix, out state, out _);

        /// <summary>
        /// Builds a one-part ghost from a live PartLoader prefab and populates the playback state
        /// exactly the way a real spawn does, so every cell drives PRODUCTION code paths rather than
        /// hand-assembled info objects.
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

            // ACTIVATE THE GHOST THE WAY A REAL SPAWN DOES, through the production seam rather than
            // a bare SetActive. BuildTimelineGhostFromSnapshot deliberately ends with
            // root.SetActive(false) (GhostVisualBuilder: the ghost must not flash at the origin
            // before it is positioned), and every production path then activates it through
            // GhostPlaybackEngine.ActivateGhostVisualsIfNeeded - which is exactly what this seam
            // exposes. In this case the production step IS just the SetActive(true) plus clearing
            // deferVisibilityUntilPlaybackSync, so routing through it costs nothing and cannot drift
            // from production if that step ever grows.
            //
            // WHY IT MATTERS HERE AND NOT IN THE H36 CELLS: those read activeSelf flags and sampled
            // poses, both readable on an inactive hierarchy. Anything Unity refuses to do while
            // inactive is invisible to them. ParticleSystem.Play() is exactly such a thing - a
            // silent no-op, no throw, isPlaying stays false - so the EVA plume cell measured a lie
            // until the fixture matched production. (The same no-op is reachable in PRODUCTION
            // during the spawn prefix replay; GhostPlaybackLogic.UpdateEvaJetpackPlumeForFrame is
            // the fix for that half, and this is the fix for the fixture half.)
            GhostPlaybackEngine.ActivateGhostVisualsIfNeededForTesting(state);
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

        /// <summary>A deployable whose break subtree actually resolved onto the ghost — the only
        /// shape an S6 hide assertion can be made against.</summary>
        private static DeployableGhostInfo FindBreakableDeployable(GhostPlaybackState state)
        {
            if (state?.deployableInfos == null) return null;
            foreach (var kv in state.deployableInfos)
            {
                DeployableGhostInfo info = kv.Value;
                if (info?.breakSubtreeRoot != null) return info;
            }
            return null;
        }

        /// <summary>A converter loop with at least one resolved, sampled transform.</summary>
        private static ConverterLoopGhostInfo FindConverterLoop(GhostPlaybackState state)
        {
            List<ConverterLoopGhostInfo> loops = state?.synthesizedMotionInfos?.converterLoops;
            if (loops == null) return null;
            for (int i = 0; i < loops.Count; i++)
            {
                ConverterLoopGhostInfo loop = loops[i];
                if (loop?.transforms == null || loop.transforms.Count == 0) continue;
                ConverterLoopTransformState ts = loop.transforms[0];
                if (ts?.t != null && ts.phases != null && ts.phases.Length > 1) return loop;
            }
            return null;
        }

        // ---- pose-change detection -------------------------------------------
        //
        // THESE THRESHOLDS ARE THE SAMPLER'S OWN, and copying them rather than inventing a
        // tolerance is the whole fix for the 2026-08-12 vacuous skip. The first version of the Goo
        // cell asked "do stowed and deployed POSITIONS differ?" - and a science canister's `Deploy`
        // clip SWINGS ITS DOORS, i.e. it is pure ROTATION. GhostVisualBuilder.CollectTransformDeltas
        // keeps a transform when position OR ROTATION OR scale moved (sqrMag > 0.0001, angle > 0.01
        // deg, sqrMag > 0.0001), so the builder correctly sampled 6 animated transforms while the
        // cell's position-only predicate matched none of them and skipped on its own precondition -
        // asserting nothing about the verdict it exists to pin.
        private const float PoseMoveSqrMagThreshold = 0.0001f;
        private const float PoseMoveAngleDegThreshold = 0.01f;

        /// <summary>True when this transform's stowed and deployed poses differ in ANY component,
        /// by the same test the builder used to decide the transform was animated at all.</summary>
        private static bool PoseChanges(DeployableTransformState ts)
        {
            if (ts.t == null) return false;
            return (ts.deployedPos - ts.stowedPos).sqrMagnitude > PoseMoveSqrMagThreshold
                || Quaternion.Angle(ts.deployedRot, ts.stowedRot) > PoseMoveAngleDegThreshold
                || (ts.deployedScale - ts.stowedScale).sqrMagnitude > PoseMoveSqrMagThreshold;
        }

        /// <summary>A deployable at least one of whose transforms actually changes pose.</summary>
        private static DeployableGhostInfo FindMovingDeployable(GhostPlaybackState state)
        {
            if (state?.deployableInfos == null) return null;
            foreach (var kv in state.deployableInfos)
            {
                DeployableGhostInfo info = kv.Value;
                if (info?.transforms == null) continue;
                for (int i = 0; i < info.transforms.Count; i++)
                    if (PoseChanges(info.transforms[i])) return info;
            }
            return null;
        }

        private static DeployableTransformState FindMovingTransform(DeployableGhostInfo info)
        {
            for (int i = 0; i < info.transforms.Count; i++)
                if (PoseChanges(info.transforms[i])) return info.transforms[i];
            return info.transforms[0];
        }

        /// <summary>How far a transform currently sits from a target pose, per component. Reported
        /// as three numbers rather than one blended score because the components have different
        /// units, and a failure message that says "12 degrees off" is actionable where a mixed
        /// scalar is not.</summary>
        private static void MeasurePoseError(
            DeployableTransformState ts, bool toDeployed,
            out float posErr, out float rotErrDeg, out float scaleErr)
        {
            Vector3 targetPos = toDeployed ? ts.deployedPos : ts.stowedPos;
            Quaternion targetRot = toDeployed ? ts.deployedRot : ts.stowedRot;
            Vector3 targetScale = toDeployed ? ts.deployedScale : ts.stowedScale;

            posErr = (ts.t.localPosition - targetPos).magnitude;
            rotErrDeg = Quaternion.Angle(ts.t.localRotation, targetRot);
            scaleErr = (ts.t.localScale - targetScale).magnitude;
        }

        /// <summary>The size of the stowed -> deployed move, per component: the reference each error
        /// above is judged against, so a clip that barely moves is not held to an absolute
        /// tolerance it could never meet.</summary>
        private static void MeasurePoseSpan(
            DeployableTransformState ts, out float posSpan, out float rotSpanDeg, out float scaleSpan)
        {
            posSpan = (ts.deployedPos - ts.stowedPos).magnitude;
            rotSpanDeg = Quaternion.Angle(ts.deployedRot, ts.stowedRot);
            scaleSpan = (ts.deployedScale - ts.stowedScale).magnitude;
        }

        // ---- prefab discovery (never hardcoded part names) -------------------

        private static List<string> CandidateDeployableParts()
        {
            return DiscoverParts(prefab =>
                prefab.FindModuleImplementing<ModuleDeployablePart>() != null);
        }

        /// <summary>Parts whose ModuleAnimationGroup names a RUNNING animation, whatever their
        /// deploy name is.</summary>
        private static List<string> CandidateRunningAnimationParts()
        {
            return DiscoverParts(prefab => HasRunningAnimationGroup(prefab, requireEmptyDeploy: false));
        }

        /// <summary>The large-ISRU shape: a running animation and NO deploy animation.</summary>
        private static List<string> CandidateRunningOnlyAnimationParts()
        {
            return DiscoverParts(prefab => HasRunningAnimationGroup(prefab, requireEmptyDeploy: true));
        }

        private static bool HasRunningAnimationGroup(Part prefab, bool requireEmptyDeploy)
        {
            if (prefab?.Modules == null) return false;
            for (int i = 0; i < prefab.Modules.Count; i++)
            {
                PartModule m = prefab.Modules[i];
                if (m == null) continue;
                if (!string.Equals(m.moduleName, "ModuleAnimationGroup", System.StringComparison.Ordinal))
                    continue;

                string active = ReadStringField(m, "activeAnimationName");
                if (string.IsNullOrEmpty(active)) continue;
                if (!requireEmptyDeploy) return true;
                if (string.IsNullOrEmpty(ReadStringField(m, "deployAnimationName"))) return true;
            }
            return false;
        }

        private static List<string> CandidateKerbalParts()
        {
            return DiscoverParts(prefab => prefab.FindModuleImplementing<KerbalEVA>() != null);
        }

        /// <summary>A science experiment paired with its own ModuleAnimateGeneric — the Goo /
        /// Science Jr shape.</summary>
        private static List<string> CandidateScienceAnimationParts()
        {
            return DiscoverParts(prefab =>
                prefab.FindModuleImplementing<ModuleScienceExperiment>() != null
                && prefab.FindModuleImplementing<ModuleAnimateGeneric>() != null);
        }

        /// <summary>Reads a string field off a live PartModule through its KSPField collection,
        /// which is how the recorder reads modules whose types it does not reference.</summary>
        private static string ReadStringField(PartModule module, string fieldName)
        {
            try
            {
                BaseField f = module.Fields[fieldName];
                object v = f?.GetValue(module);
                return v as string;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private static Part ResolvePrefab(string partName)
        {
            AvailablePart ap = PartLoader.getPartInfoByName(partName);
            return ap?.partPrefab;
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
