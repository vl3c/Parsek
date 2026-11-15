using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Heat / reentry state, deployables, cargo bays and jettison panels, and lights.
    internal static partial class GhostPlaybackLogic
    {
        #region Heat / Reentry

        internal static bool ApplyHeatState(GhostPlaybackState state, PartEvent evt, HeatLevel level)
            => ApplyHeatStateWithOutcome(state, evt, level) == GhostPartEventOutcome.Applied;

        /// <summary>
        /// P8 step 1 outcome-reporting core. This family already had per-apply evidence
        /// (`Part pid=N: applied heat level ...`), which is KEPT: it names the level,
        /// which the family line does not, and existing readers pin it.
        /// </summary>
        /// <summary>P8 step 1 precondition classifier (pure).</summary>
        internal static GhostPartEventOutcome ClassifyHeatApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state == null || state.heatInfos == null) return GhostPartEventOutcome.NoFamilyState;
            if (!state.heatInfos.TryGetValue(partPersistentId, out HeatGhostInfo info) || info == null)
                return GhostPartEventOutcome.NoInfoForPart;
            if (info.transforms == null && info.materialStates == null)
                return GhostPartEventOutcome.NoResolvedVisual;
            return GhostPartEventOutcome.Applied;
        }

        internal static GhostPartEventOutcome ApplyHeatStateWithOutcome(
            GhostPlaybackState state, PartEvent evt, HeatLevel level)
        {
            GhostPartEventOutcome precondition = ClassifyHeatApply(state, evt.partPersistentId);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            HeatGhostInfo info = state.heatInfos[evt.partPersistentId];

            bool applied = false;

            if (info.transforms != null)
            {
                for (int i = 0; i < info.transforms.Count; i++)
                {
                    var ts = info.transforms[i];
                    if (ts.t == null) continue;

                    switch (level)
                    {
                        case HeatLevel.Hot:
                            ts.t.localPosition = ts.hotPos;
                            ts.t.localRotation = ts.hotRot;
                            ts.t.localScale = ts.hotScale;
                            break;
                        case HeatLevel.Medium:
                            ts.t.localPosition = ts.mediumPos;
                            ts.t.localRotation = ts.mediumRot;
                            ts.t.localScale = ts.mediumScale;
                            break;
                        default:
                            ts.t.localPosition = ts.coldPos;
                            ts.t.localRotation = ts.coldRot;
                            ts.t.localScale = ts.coldScale;
                            break;
                    }
                    applied = true;
                }
            }

            if (info.materialStates != null)
            {
                for (int i = 0; i < info.materialStates.Count; i++)
                {
                    HeatMaterialState materialState = info.materialStates[i];
                    if (materialState.material == null) continue;

                    Color color, emission;
                    switch (level)
                    {
                        case HeatLevel.Hot:
                            color = materialState.hotColor;
                            emission = materialState.hotEmission;
                            break;
                        case HeatLevel.Medium:
                            color = materialState.mediumColor;
                            emission = materialState.mediumEmission;
                            break;
                        default:
                            color = materialState.coldColor;
                            emission = materialState.coldEmission;
                            break;
                    }

                    if (!string.IsNullOrEmpty(materialState.colorProperty))
                        materialState.material.SetColor(materialState.colorProperty, color);

                    if (!string.IsNullOrEmpty(materialState.emissiveProperty))
                        materialState.material.SetColor(materialState.emissiveProperty, emission);

                    applied = true;
                }
            }

            if (applied)
                ParsekLog.VerboseRateLimited("Flight", $"heat-{evt.partPersistentId}",
                    $"Part pid={evt.partPersistentId}: applied heat level {level}", 5.0);

            return applied
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.NoResolvedVisual;
        }

        internal static void ResetReentryFx(GhostPlaybackState state, int recIdx)
        {
            var info = state.reentryFxInfo;
            if (info == null) return;

            info.lastIntensity = 0f;

            if (info.fireParticles != null)
            {
                info.fireParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                info.fireParticles.Clear(true);
            }

            if (info.glowMaterials != null)
            {
                for (int i = 0; i < info.glowMaterials.Count; i++)
                {
                    HeatMaterialState ms = info.glowMaterials[i];
                    if (ms.material == null) continue;
                    if (!string.IsNullOrEmpty(ms.emissiveProperty))
                        ms.material.SetColor(ms.emissiveProperty, ms.coldEmission);
                    if (!string.IsNullOrEmpty(ms.colorProperty))
                        ms.material.SetColor(ms.colorProperty, ms.coldColor);
                }
            }

        }

        #endregion

        #region Deployables / Jettison

        #region S2 — deployable / gear / bay / ladder interpolation

        /// <summary>Fallback clip length when the prefab animation's own length is unreadable.</summary>
        internal const float DefaultDeployableClipSeconds = 3f;
        internal const float MinDeployableClipSeconds = 0.25f;
        internal const float MaxDeployableClipSeconds = 30f;

        /// <summary>
        /// Clamps a prefab <c>AnimationState.length</c> into a plausible deployable-clip duration.
        /// A zero / NaN / negative length means the clip was unreadable (no Animation component on
        /// the model clone, a stripped state) and answers the 3 s default — which still ANIMATES,
        /// because the failure mode we are replacing is the snap, not a wrong duration.
        /// </summary>
        internal static float ClampDeployableClipSeconds(float rawLengthSeconds)
        {
            if (float.IsNaN(rawLengthSeconds) || float.IsInfinity(rawLengthSeconds)
                || rawLengthSeconds <= 0f)
            {
                return DefaultDeployableClipSeconds;
            }
            return Math.Min(Math.Max(rawLengthSeconds, MinDeployableClipSeconds), MaxDeployableClipSeconds);
        }

        /// <summary>
        /// Where along stowed(0) -> deployed(1) an in-flight transition is at <paramref name="currentUT"/>.
        ///
        /// PURE FUNCTION OF THE EVENT UT, which is the whole design: a prefix catch-up, a scrub, a
        /// warp change and a loop cycle all evaluate the same expression and land on the same pose,
        /// with no accumulated per-frame state to drift or to reset. Wall time would need all four
        /// to be special-cased.
        ///
        /// Duration scales with the DISTANCE travelled (<c>|target - start|</c>), so a reversal at
        /// mid-clip takes half a clip back rather than a full one — an animation played backwards
        /// from where it got to, not a fresh clip from the far end.
        /// </summary>
        internal static float ComputeDeployableTransitionFraction(
            double currentUT, double transitionStartUT,
            float startFraction, float targetFraction, float clipLengthSeconds,
            out bool complete)
        {
            complete = true;
            startFraction = Math.Min(Math.Max(startFraction, 0f), 1f);
            targetFraction = Math.Min(Math.Max(targetFraction, 0f), 1f);

            float span = Math.Abs(targetFraction - startFraction);
            if (span <= 1e-4f)
                return targetFraction;

            if (double.IsNaN(currentUT) || double.IsInfinity(currentUT)
                || double.IsNaN(transitionStartUT) || double.IsInfinity(transitionStartUT))
            {
                return targetFraction;
            }

            double duration = ClampDeployableClipSeconds(clipLengthSeconds) * span;
            double elapsed = currentUT - transitionStartUT;
            if (elapsed <= 0.0)
            {
                // Playback UT is at or before the event that started this transition (a backwards
                // scrub, or the same frame the event fired). Hold the start pose; the next frame
                // with a positive elapsed advances it.
                complete = false;
                return startFraction;
            }
            if (elapsed >= duration)
                return targetFraction;

            complete = false;
            return startFraction + (targetFraction - startFraction) * (float)(elapsed / duration);
        }

        /// <summary>
        /// Writes one interpolated pose into every transform of a deployable family. Endpoint
        /// fractions take the exact stored endpoint pose rather than a lerp result, so the snap
        /// path and the animated path agree bit-for-bit at 0 and 1.
        /// </summary>
        private static bool ApplyDeployableFraction(DeployableGhostInfo info, float fraction)
        {
            if (info?.transforms == null) return false;

            bool applied = false;
            bool atStowed = fraction <= 1e-4f;
            bool atDeployed = fraction >= 1f - 1e-4f;

            for (int i = 0; i < info.transforms.Count; i++)
            {
                var ts = info.transforms[i];
                if (ts.t == null) continue;
                applied = true;

                if (atStowed)
                {
                    ts.t.localPosition = ts.stowedPos;
                    ts.t.localRotation = ts.stowedRot;
                    ts.t.localScale = ts.stowedScale;
                }
                else if (atDeployed)
                {
                    ts.t.localPosition = ts.deployedPos;
                    ts.t.localRotation = ts.deployedRot;
                    ts.t.localScale = ts.deployedScale;
                }
                else
                {
                    ts.t.localPosition = Vector3.Lerp(ts.stowedPos, ts.deployedPos, fraction);
                    ts.t.localRotation = Quaternion.Slerp(ts.stowedRot, ts.deployedRot, fraction);
                    ts.t.localScale = Vector3.Lerp(ts.stowedScale, ts.deployedScale, fraction);
                }
            }

            if (applied)
                info.deployFraction = fraction;
            return applied;
        }

        /// <summary>
        /// Advances every in-flight deployable transition to <paramref name="currentUT"/> and drops
        /// the ones that finished. Called from <c>ApplyFrameVisuals</c> beside
        /// <c>UpdateActiveRobotics</c>. Cost is O(active transitions x transforms) and the active
        /// list is empty on every frame where nothing is opening or closing, which is almost all
        /// of them.
        /// </summary>
        internal static void UpdateActiveDeployables(GhostPlaybackState state, double currentUT)
        {
            List<DeployableGhostInfo> active = state?.activeDeployableTransitions;
            if (active == null || active.Count == 0) return;

            for (int i = active.Count - 1; i >= 0; i--)
            {
                DeployableGhostInfo info = active[i];
                if (info == null || !info.transitionActive)
                {
                    active.RemoveAt(i);
                    continue;
                }

                float fraction = ComputeDeployableTransitionFraction(
                    currentUT, info.transitionStartUT,
                    info.transitionStartFraction, info.transitionTargetFraction,
                    info.clipLengthSeconds, out bool complete);

                if (!ApplyDeployableFraction(info, fraction))
                {
                    // Every transform went null (part decoupled / destroyed mid-clip). Retire the
                    // entry rather than re-walking a dead list every frame.
                    info.transitionActive = false;
                    active.RemoveAt(i);
                    continue;
                }

                if (complete)
                {
                    info.transitionActive = false;
                    info.deployFraction = info.transitionTargetFraction;
                    info.currentDeployed = info.transitionTargetFraction >= 0.5f;
                    active.RemoveAt(i);
                }
            }
        }

        /// <summary>Drops every in-flight transition. Loop cycles re-stow and replay from their own UTs.</summary>
        internal static void ClearActiveDeployableTransitions(GhostPlaybackState state)
        {
            if (state?.deployableInfos != null)
            {
                foreach (var kvp in state.deployableInfos)
                {
                    if (kvp.Value == null) continue;
                    kvp.Value.transitionActive = false;
                }
            }
            state?.activeDeployableTransitions?.Clear();
        }

        #endregion

        /// <summary>
        /// The IMMEDIATE (snap) path, unchanged in behaviour and still what every baseline caller
        /// takes: <c>ApplySnapshotBaselines</c>, the split-seed stow, and loop-reset step 2. A pose
        /// that was APPLIED as a baseline must never animate — the ghost is being put into the
        /// state it spawned in, not shown opening a bay.
        /// </summary>
        internal static bool ApplyDeployableState(GhostPlaybackState state, PartEvent evt, bool deployed)
            => ApplyDeployableState(state, evt, deployed, immediate: true);

        /// <summary>
        /// S6: hides or re-shows the break subtree — the ghost-side equivalent of what stock does
        /// to a ModuleDeployablePart that goes BROKEN (<c>panelBreakTransform.gameObject
        /// .SetActive(false)</c>) and of what a repair undoes.
        ///
        /// Stock's own repair path (<c>DoRepair</c>) re-instantiates a fresh subtree off the part
        /// prefab rather than re-activating the old one, because the live break DETACHED the panel
        /// into its own physics object and flung it away. Our ghost never detached anything — it
        /// only ever hid its own clone — so a plain SetActive(true) is the faithful inverse, and
        /// the simpler one.
        ///
        /// Absolute-state and idempotent, which is what lets a snapshot baseline, a start-UT seed
        /// and a recorded event all target the same pid without fighting.
        ///
        /// Returns true when an info existed for the pid, whether or not it had a resolvable
        /// transform: the STATE flag is set either way, because "this panel is broken" still has
        /// to gate sun tracking on a part whose break transform could not be resolved.
        /// </summary>
        internal static bool ApplyDeployableBrokenState(
            GhostPlaybackState state, uint partPersistentId, bool broken)
            => ApplyDeployableBrokenStateWithOutcome(state, partPersistentId, broken)
                == GhostPartEventOutcome.Applied;

        /// <summary>
        /// P8 step 1 outcome-reporting core; the bool wrapper above is unchanged in
        /// behaviour (it was already "an info existed for the pid").
        /// </summary>
        /// <summary>
        /// P8 step 1 precondition classifier (pure; see <see cref="ClassifyEngineEmissionApply"/>
        /// for why every family has one). Applied whenever an info exists for the pid,
        /// with or without a resolvable break transform - the STATE flag is set either
        /// way, which is this handler's documented contract.
        /// </summary>
        internal static GhostPartEventOutcome ClassifyDeployableBrokenApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.deployableInfos == null) return GhostPartEventOutcome.NoFamilyState;
            DeployableGhostInfo info;
            if (!state.deployableInfos.TryGetValue(partPersistentId, out info) || info == null)
                return GhostPartEventOutcome.NoInfoForPart;
            return GhostPartEventOutcome.Applied;
        }

        internal static GhostPartEventOutcome ApplyDeployableBrokenStateWithOutcome(
            GhostPlaybackState state, uint partPersistentId, bool broken)
        {
            GhostPartEventOutcome precondition =
                ClassifyDeployableBrokenApply(state, partPersistentId);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            DeployableGhostInfo info = state.deployableInfos[partPersistentId];

            info.breakSubtreeHidden = broken;

            SetBreakSubtreeActive(info.breakSubtreeRoot, !broken);

            return GhostPartEventOutcome.Applied;
        }

        // The Unity null check and SetActive are ECalls. Kept out of the method above so the
        // optimizing JIT cannot inline them up into ApplySnapshotBaselines, which xUnit calls
        // (SecurityException on the .NET Framework test host).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void SetBreakSubtreeActive(Transform breakSubtreeRoot, bool active)
        {
            if (breakSubtreeRoot != null)
                breakSubtreeRoot.gameObject.SetActive(active);
        }

        /// <summary>
        /// Swaps a ghost wheel between its intact and busted meshes: the ghost-side equivalent of
        /// stock <c>ModuleWheelDamage.SetDamaged</c> (busted transform active, intact transform
        /// inactive while broken; the reverse after a repair).
        ///
        /// Absolute-state and idempotent, so a snapshot baseline, a start-UT seed and a recorded
        /// event can all target the same pid without fighting, and a loop cycle can reset every
        /// wheel to intact before the baseline re-applies.
        /// </summary>
        internal static bool ApplyWheelDamageState(
            GhostPlaybackState state, uint partPersistentId, bool damaged)
            => ApplyWheelDamageStateWithOutcome(state, partPersistentId, damaged)
                == GhostPartEventOutcome.Applied;

        /// <summary>
        /// Pure precondition classifier, the same shape as every other family's: Applied means
        /// only "the ghost carries a wheel damage swap for this part".
        /// </summary>
        internal static GhostPartEventOutcome ClassifyWheelDamageApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.wheelDamageInfos == null) return GhostPartEventOutcome.NoFamilyState;
            WheelDamageGhostInfo info;
            if (!state.wheelDamageInfos.TryGetValue(partPersistentId, out info) || info == null)
                return GhostPartEventOutcome.NoInfoForPart;
            return GhostPartEventOutcome.Applied;
        }

        internal static GhostPartEventOutcome ApplyWheelDamageStateWithOutcome(
            GhostPlaybackState state, uint partPersistentId, bool damaged)
        {
            GhostPartEventOutcome precondition = ClassifyWheelDamageApply(state, partPersistentId);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            WheelDamageGhostInfo info = state.wheelDamageInfos[partPersistentId];
            info.damaged = damaged;

            // The Count reads are managed: an info with no transforms (a headless test fixture)
            // never reaches the Unity calls.
            if ((info.damagedTransforms != null && info.damagedTransforms.Count > 0)
                || (info.undamagedTransforms != null && info.undamagedTransforms.Count > 0))
            {
                SetWheelDamageTransformsActive(info, damaged);
            }

            return GhostPartEventOutcome.Applied;
        }

        // Unity null checks and SetActive are ECalls; NoInlining keeps them out of the methods
        // xUnit calls (see SetBreakSubtreeActive).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void SetWheelDamageTransformsActive(WheelDamageGhostInfo info, bool damaged)
        {
            if (info.damagedTransforms != null)
            {
                for (int i = 0; i < info.damagedTransforms.Count; i++)
                {
                    Transform t = info.damagedTransforms[i];
                    if (t != null) t.gameObject.SetActive(damaged);
                }
            }
            if (info.undamagedTransforms != null)
            {
                for (int i = 0; i < info.undamagedTransforms.Count; i++)
                {
                    Transform t = info.undamagedTransforms[i];
                    if (t != null) t.gameObject.SetActive(!damaged);
                }
            }
        }

        /// <summary>
        /// Applies a deployable target pose, either immediately or as an interpolated transition
        /// keyed on <c>evt.ut</c> (the RECORDED event UT, never wall time — see
        /// <see cref="ComputeDeployableTransitionFraction"/>).
        /// </summary>
        internal static bool ApplyDeployableState(
            GhostPlaybackState state, PartEvent evt, bool deployed, bool immediate)
            => ApplyDeployableStateWithOutcome(state, evt, deployed, immediate)
                == GhostPartEventOutcome.Applied;

        /// <summary>
        /// P8 step 1 outcome-reporting core. Every historical `return false` keeps its
        /// exact condition and gains a named reason class; every historical `return
        /// true` maps to Applied, so the bool wrapper above is behaviourally identical.
        /// </summary>
        /// <summary>
        /// P8 step 1 precondition classifier (pure). Applied here means only "the ghost
        /// has a pose-carrying deployable info for this part"; whether the pose write
        /// itself reached a live transform is decided inside the handler.
        /// </summary>
        internal static GhostPartEventOutcome ClassifyDeployableApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.deployableInfos == null) return GhostPartEventOutcome.NoFamilyState;
            DeployableGhostInfo info;
            if (!state.deployableInfos.TryGetValue(partPersistentId, out info))
                return GhostPartEventOutcome.NoInfoForPart;
            if (info?.transforms == null) return GhostPartEventOutcome.NoResolvedVisual;
            return GhostPartEventOutcome.Applied;
        }

        internal static GhostPartEventOutcome ApplyDeployableStateWithOutcome(
            GhostPlaybackState state, PartEvent evt, bool deployed, bool immediate)
        {
            GhostPartEventOutcome precondition =
                ClassifyDeployableApply(state, evt.partPersistentId);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            DeployableGhostInfo info = state.deployableInfos[evt.partPersistentId];

            // S6: ANY extend/retract opinion on a panel currently rendered broken un-hides it
            // first. The recorder emits DeployableRetracted on a repair precisely so this fires,
            // and putting it here rather than only in the Retracted arm means a recording whose
            // repair was followed immediately by a re-deploy (so the tail's first deployable
            // event is Extended) still gets the panel back.
            //
            // AND THE UN-HIDE FORCES A SNAP, which is not a detail. Rule 1 deliberately leaves the
            // pivot at deployFraction = 1 when a panel breaks (the recorder does not retract it),
            // so an ANIMATED application here would re-show the panel fully EXTENDED and then fold
            // it politely shut over the clip length. Stock DoRepair lands on RETRACTED instantly,
            // with a freshly instantiated subtree - there is no fold to show. Reaching the target
            // pose in the same frame as the un-hide is the faithful rendering.
            if (info.breakSubtreeHidden)
            {
                ApplyDeployableBrokenState(state, evt.partPersistentId, broken: false);
                immediate = true;
            }

            float target = deployed ? 1f : 0f;

            if (immediate)
            {
                info.transitionActive = false;
                info.currentDeployed = deployed;
                bool snapped = ApplyDeployableFraction(info, target);
                if (snapped)
                    state.activeDeployableTransitions?.Remove(info);
                return snapped
                    ? GhostPartEventOutcome.Applied
                    : GhostPartEventOutcome.NoResolvedVisual;
            }

            // Already there and not mid-clip: nothing to animate, and re-arming would restart a
            // finished clip every time a duplicate event replays.
            if (!info.transitionActive && Math.Abs(info.deployFraction - target) <= 1e-4f)
            {
                info.currentDeployed = deployed;
                // Historically `return info.transforms.Count > 0`, preserved exactly: a
                // pose-carrying info already at the target counts as APPLIED (the ghost
                // is in the state the event asks for), and a pose-less one is a
                // resolution failure. That keeps the cargo-bay cascade below falling
                // through to jettison panels on precisely the same condition as before.
                return info.transforms.Count > 0
                    ? GhostPartEventOutcome.Applied
                    : GhostPartEventOutcome.NoResolvedVisual;
            }

            // A REVERSAL MID-CLIP must start from where the old transition had reached AT THIS
            // EVENT'S UT, not from info.deployFraction. In a one-batch prefix replay (a spawn or a
            // scrub into the window) deploy@t1 and retract@t2 are consumed back to back with no
            // UpdateActiveDeployables pass between them, so deployFraction is still the pose as of
            // t1; using it would arm a zero-span retract that completes instantly and show a
            // fully-stowed panel where a partly-retracted one belongs.
            float startFraction = info.transitionActive
                ? ComputeDeployableTransitionFraction(
                    evt.ut, info.transitionStartUT, info.transitionStartFraction,
                    info.transitionTargetFraction, info.clipLengthSeconds, out _)
                : info.deployFraction;

            info.transitionActive = true;
            info.transitionStartUT = evt.ut;
            info.transitionStartFraction = startFraction;
            info.transitionTargetFraction = target;
            info.currentDeployed = deployed;

            if (state.activeDeployableTransitions == null)
                state.activeDeployableTransitions = new List<DeployableGhostInfo>();
            if (!state.activeDeployableTransitions.Contains(info))
                state.activeDeployableTransitions.Add(info);

            // Apply the first frame straight away so a transition that starts on a frame where
            // UpdateActiveDeployables already ran is not a frame late.
            return ApplyDeployableFraction(info, startFraction)
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.NoResolvedVisual;
        }

        /// <summary>
        /// The cargo/service-bay cascade: most bays animate a DeployableGhostInfo, but
        /// some (e.g. bays whose doors are jettison-style panels) only have jettison
        /// transforms. Extracted so the M1 snapshot baseline and the CargoBayOpened /
        /// CargoBayClosed events take exactly the same path.
        /// </summary>
        internal static bool ApplyCargoBayState(GhostPlaybackState state, PartEvent evt, bool open)
            => ApplyCargoBayState(state, evt, open, immediate: true);

        /// <summary>
        /// S2: the jettison-panel arm of the cascade stays a SetActive snap — a panel that is a
        /// jettisoned object has no stowed/deployed pose pair to interpolate between.
        /// </summary>
        internal static bool ApplyCargoBayState(
            GhostPlaybackState state, PartEvent evt, bool open, bool immediate)
        {
            if (ApplyDeployableState(state, evt, deployed: open, immediate: immediate))
                return true;
            return ApplyJettisonPanelState(state, evt, jettisoned: open);
        }

        /// <summary>
        /// P8 step 1: the cascade reported as its TWO arms rather than one boolean.
        /// Which arm a bay took is the fact a reader cannot recover from the old
        /// aggregate line, and it is the difference between "the doors animated" and
        /// "a jettison panel was hidden".
        /// </summary>
        internal static void ApplyCargoBayStateWithOutcomes(
            GhostPlaybackState state,
            PartEvent evt,
            bool open,
            bool immediate,
            out GhostPartEventOutcome deployableOutcome,
            out GhostPartEventOutcome jettisonOutcome,
            out bool jettisonArmReached)
        {
            deployableOutcome = ApplyDeployableStateWithOutcome(
                state, evt, deployed: open, immediate: immediate);
            if (deployableOutcome == GhostPartEventOutcome.Applied)
            {
                jettisonOutcome = GhostPartEventOutcome.Applied;
                jettisonArmReached = false;
                return;
            }

            jettisonArmReached = true;
            // The classifier is consulted here as well as inside the writer - the SAME
            // function, so no predicate is duplicated - because it lets the cascade's
            // arm choice be driven headlessly up to the point where a live Transform is
            // genuinely required. Cost is one dictionary lookup on a bay event.
            jettisonOutcome = ClassifyJettisonPanelApply(state, evt.partPersistentId);
            if (jettisonOutcome == GhostPartEventOutcome.Applied)
                jettisonOutcome = ApplyJettisonPanelStateWithOutcome(state, evt, jettisoned: open);
        }

        internal static bool ApplyJettisonPanelState(GhostPlaybackState state, PartEvent evt, bool jettisoned)
            => ApplyJettisonPanelStateWithOutcome(state, evt, jettisoned)
                == GhostPartEventOutcome.Applied;

        /// <summary>
        /// P8 step 1 outcome-reporting core. The historical single `return false` guard
        /// covered three distinct facts (no jettison dictionary, no entry for the pid,
        /// an entry with an empty transform list); they are separated here because the
        /// third is a ghost-BUILD result and the first two are not.
        /// </summary>
        /// <summary>P8 step 1 precondition classifier (pure).</summary>
        internal static GhostPartEventOutcome ClassifyJettisonPanelApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.jettisonInfos == null) return GhostPartEventOutcome.NoFamilyState;
            JettisonGhostInfo jetInfo;
            if (!state.jettisonInfos.TryGetValue(partPersistentId, out jetInfo))
                return GhostPartEventOutcome.NoInfoForPart;
            if (jetInfo == null
                || jetInfo.jettisonTransforms == null
                || jetInfo.jettisonTransforms.Count == 0)
                return GhostPartEventOutcome.NoResolvedVisual;
            return GhostPartEventOutcome.Applied;
        }

        internal static GhostPartEventOutcome ApplyJettisonPanelStateWithOutcome(
            GhostPlaybackState state, PartEvent evt, bool jettisoned)
        {
            GhostPartEventOutcome precondition =
                ClassifyJettisonPanelApply(state, evt.partPersistentId);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            JettisonGhostInfo jetInfo = state.jettisonInfos[evt.partPersistentId];

            bool applied = false;
            for (int i = 0; i < jetInfo.jettisonTransforms.Count; i++)
            {
                Transform jettisonTransform = jetInfo.jettisonTransforms[i];
                if (jettisonTransform == null) continue;
                jettisonTransform.gameObject.SetActive(!jettisoned);
                applied = true;
            }

            return applied
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.NoResolvedVisual;
        }

        #endregion

        #region Lights

        internal static LightPlaybackState GetOrCreateLightPlaybackState(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.lightPlaybackStates == null)
                state.lightPlaybackStates = new Dictionary<uint, LightPlaybackState>();

            LightPlaybackState playbackState;
            if (!state.lightPlaybackStates.TryGetValue(partPersistentId, out playbackState))
            {
                playbackState = new LightPlaybackState();
                state.lightPlaybackStates[partPersistentId] = playbackState;
            }

            return playbackState;
        }

        internal static void ApplyLightPowerEvent(GhostPlaybackState state, uint partPersistentId, bool on)
        {
            ApplyLightPowerEventWithOutcomes(state, partPersistentId, on, out _, out _);
        }

        /// <summary>
        /// P8 step 1: a light event drives TWO independent ghost surfaces - Unity
        /// <c>Light</c> components and Pattern-A colour-changer emissive materials - and
        /// a part can carry either, both or neither. Reporting one boolean for the pair
        /// is what made SHOWCASE-COLORCHANGER-APPLY-UNOBSERVABLE unanswerable: a row
        /// that toggled a Light but resolved no cabin-light material was
        /// indistinguishable from one that toggled both. Both outcomes come out
        /// separately here and are logged as separate `surface=` lines.
        ///
        /// The ON-while-blinking case is NOT a failure: the visual write belongs to
        /// UpdateBlinkingLights, which runs every frame off the playback flag this
        /// event just set. It reports DeferredToDriver on both surfaces.
        /// </summary>
        internal static void ApplyLightPowerEventWithOutcomes(
            GhostPlaybackState state,
            uint partPersistentId,
            bool on,
            out GhostPartEventOutcome lightOutcome,
            out GhostPartEventOutcome colorChangerOutcome)
        {
            if (state == null)
            {
                lightOutcome = GhostPartEventOutcome.NoFamilyState;
                colorChangerOutcome = GhostPartEventOutcome.NoFamilyState;
                return;
            }

            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            playbackState.isOn = on;
            if (!on)
            {
                SetLightStateWithOutcomes(
                    state, partPersistentId, false, out lightOutcome, out colorChangerOutcome);
            }
            else if (!playbackState.blinkEnabled)
            {
                SetLightStateWithOutcomes(
                    state, partPersistentId, true, out lightOutcome, out colorChangerOutcome);
            }
            else
            {
                lightOutcome = GhostPartEventOutcome.DeferredToDriver;
                colorChangerOutcome = GhostPartEventOutcome.DeferredToDriver;
            }
        }

        internal static void ApplyLightBlinkModeEvent(
            GhostPlaybackState state, uint partPersistentId, bool enabled, float blinkRateHz)
        {
            ApplyLightBlinkModeEventWithOutcome(state, partPersistentId, enabled, blinkRateHz);
        }

        /// <summary>
        /// P8 step 1 outcome-reporting core. A blink event writes PLAYBACK STATE only -
        /// the visual is UpdateBlinkingLights' job every frame - so a successful write
        /// reports on the `blink-state` surface, never on `light`. Reporting it as
        /// Applied on the light surface would claim a lamp changed when none did.
        /// </summary>
        internal static GhostPartEventOutcome ApplyLightBlinkModeEventWithOutcome(
            GhostPlaybackState state, uint partPersistentId, bool enabled, float blinkRateHz)
        {
            if (state == null) return GhostPartEventOutcome.NoFamilyState;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            playbackState.blinkEnabled = enabled;
            if (blinkRateHz > 0f)
                playbackState.blinkRateHz = blinkRateHz;
            return GhostPartEventOutcome.Applied;
        }

        internal static void ApplyLightBlinkRateEvent(GhostPlaybackState state, uint partPersistentId, float blinkRateHz)
        {
            ApplyLightBlinkRateEventWithOutcome(state, partPersistentId, blinkRateHz);
        }

        /// <summary>
        /// P8 step 1 outcome-reporting core. A non-positive rate is DISCARDED by the
        /// handler (the recorder can emit a zero on a module whose rate field was not
        /// readable), so it reports AlreadyInState rather than claiming a write.
        /// </summary>
        internal static GhostPartEventOutcome ApplyLightBlinkRateEventWithOutcome(
            GhostPlaybackState state, uint partPersistentId, float blinkRateHz)
        {
            if (state == null) return GhostPartEventOutcome.NoFamilyState;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            if (blinkRateHz > 0f)
            {
                playbackState.blinkRateHz = blinkRateHz;
                return GhostPartEventOutcome.Applied;
            }
            return GhostPartEventOutcome.AlreadyInState;
        }

        internal static void UpdateBlinkingLights(GhostPlaybackState state, double currentUT)
        {
            if (state == null || state.lightPlaybackStates == null || state.lightPlaybackStates.Count == 0)
                return;

            foreach (var kv in state.lightPlaybackStates)
            {
                uint partPersistentId = kv.Key;
                LightPlaybackState playbackState = kv.Value;
                if (playbackState == null)
                    continue;

                bool shouldEnable = playbackState.isOn;
                if (shouldEnable && playbackState.blinkEnabled)
                {
                    float rateHz = playbackState.blinkRateHz > 0f ? playbackState.blinkRateHz : 1f;
                    double cycle = currentUT * rateHz;
                    double frac = cycle - Math.Floor(cycle);
                    shouldEnable = frac < 0.5;
                }

                SetLightState(state, partPersistentId, shouldEnable);
            }
        }

        /// <summary>
        /// THE DRIVER PATH, and it stays CLASSIFY-FREE on purpose.
        /// <see cref="UpdateBlinkingLights"/> calls this once per blinking light per
        /// ghost per frame, and that cost multiplies across every ghost in the scene.
        /// Routing it through the outcome variant would run the light classifier PLUS
        /// the nested colour-changer scan (a per-part list walk with a per-material
        /// walk inside it) on every one of those frames only to discard the answer.
        /// The blink driver has no event to attribute an outcome to, so it does the
        /// writes and nothing else - exactly the work the pre-P8 body did.
        /// </summary>
        internal static void SetLightState(GhostPlaybackState state, uint partPersistentId, bool on)
        {
            WriteUnityLightState(state, partPersistentId, on);
            WriteColorChangerLightState(state, partPersistentId, on);
        }

        /// <summary>
        /// P8 step 1 outcome-reporting variant for the two light surfaces, reached ONLY
        /// from a recorded light EVENT (<see cref="ApplyLightPowerEventWithOutcomes"/>),
        /// never from the per-frame blink driver above. Classifies each surface, then
        /// hands the write to the SAME single writer the driver path uses - one write
        /// loop per surface, not two.
        /// </summary>
        internal static void SetLightStateWithOutcomes(
            GhostPlaybackState state,
            uint partPersistentId,
            bool on,
            out GhostPartEventOutcome lightOutcome,
            out GhostPartEventOutcome colorChangerOutcome)
        {
            // Toggle Unity Light components (existing behavior)
            lightOutcome = ClassifyUnityLightApply(state, partPersistentId);
            if (lightOutcome == GhostPartEventOutcome.Applied)
                WriteUnityLightState(state, partPersistentId, on);

            // Toggle ColorChanger emissive materials (Pattern A: cabin lights)
            colorChangerOutcome = ApplyColorChangerLightStateWithOutcome(state, partPersistentId, on);
        }

        /// <summary>The single Unity-Light write loop, shared by both paths above.</summary>
        private static void WriteUnityLightState(
            GhostPlaybackState state, uint partPersistentId, bool on)
        {
            if (state.lightInfos == null) return;
            LightGhostInfo info;
            if (!state.lightInfos.TryGetValue(partPersistentId, out info)
                || info == null || info.lights == null)
                return;
            for (int i = 0; i < info.lights.Count; i++)
            {
                if (info.lights[i] != null)
                    info.lights[i].enabled = on;
            }
        }

        /// <summary>
        /// P8 step 1 precondition classifier for the Unity-Light half of a light event
        /// (pure). Applied means at least one non-null <c>Light</c> is present to write.
        /// </summary>
        internal static GhostPartEventOutcome ClassifyUnityLightApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.lightInfos == null) return GhostPartEventOutcome.NoFamilyState;
            LightGhostInfo info;
            if (!state.lightInfos.TryGetValue(partPersistentId, out info) || info == null
                || info.lights == null)
                return GhostPartEventOutcome.NoInfoForPart;
            for (int i = 0; i < info.lights.Count; i++)
            {
                if (info.lights[i] != null) return GhostPartEventOutcome.Applied;
            }
            return GhostPartEventOutcome.NoResolvedVisual;
        }

        /// <summary>
        /// P8 step 1 outcome-reporting variant, reached only from a recorded light
        /// EVENT. Classifies (the four distinct ghost-build facts are documented on
        /// <see cref="ClassifyColorChangerLightApply"/>) and then hands the write to
        /// the single writer below.
        /// </summary>
        internal static GhostPartEventOutcome ApplyColorChangerLightStateWithOutcome(
            GhostPlaybackState state, uint partPersistentId, bool on)
        {
            GhostPartEventOutcome precondition =
                ClassifyColorChangerLightApply(state, partPersistentId);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            WriteColorChangerLightState(state, partPersistentId, on);
            return GhostPartEventOutcome.Applied;
        }

        /// <summary>
        /// The single Pattern-A colour-changer write loop, shared by the driver path
        /// and the event path. Self-guarding, so the driver reaches it without a
        /// classifier pass.
        /// </summary>
        private static void WriteColorChangerLightState(
            GhostPlaybackState state, uint partPersistentId, bool on)
        {
            if (state.colorChangerInfos == null) return;

            List<ColorChangerGhostInfo> infos;
            if (!state.colorChangerInfos.TryGetValue(partPersistentId, out infos) || infos == null)
                return;

            for (int c = 0; c < infos.Count; c++)
            {
                var ccInfo = infos[c];
                if (ccInfo == null || !ccInfo.isCabinLight) continue; // Only Pattern A responds
                if (ccInfo.materials == null) continue;

                bool wrote = false;
                for (int i = 0; i < ccInfo.materials.Count; i++)
                {
                    if (ccInfo.materials[i].material != null)
                    {
                        ccInfo.materials[i].material.SetColor(
                            ccInfo.shaderProperty,
                            on ? ccInfo.materials[i].onColor : ccInfo.materials[i].offColor);
                        wrote = true;
                    }
                }

                if (wrote)
                    ParsekLog.VerboseRateLimited("Flight", $"cc-light-{partPersistentId}",
                        $"Part pid={partPersistentId}: applied color changer cabin light state={on}");
            }
        }

        /// <summary>
        /// P8 step 1 precondition classifier (pure). The three not-applied cases are
        /// distinct facts about the GHOST BUILD, and telling them apart is what closes
        /// SHOWCASE-COLORCHANGER-APPLY-UNOBSERVABLE:
        /// <list type="bullet">
        /// <item><description><c>no-family-state</c>: the ghost has no colour-changer
        /// dictionary at all (nothing on the craft uses ModuleColorChanger).</description></item>
        /// <item><description><c>no-info-for-part</c>: the dictionary exists but Pattern-A
        /// discovery resolved NOTHING for this part - hypothesis (a) in the S1.9
        /// reading, a genuine ghost-render gap.</description></item>
        /// <item><description><c>no-cabin-light-entry</c>: entries exist for the part but
        /// none is a cabin light, so a light event has nothing here to toggle -
        /// hypothesis (b), the part genuinely carries only Pattern-B (reentry char)
        /// colour changers.</description></item>
        /// <item><description><c>no-resolved-visual</c>: a cabin-light entry exists but
        /// every material in it resolved to null.</description></item>
        /// </list>
        /// </summary>
        internal static GhostPartEventOutcome ClassifyColorChangerLightApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.colorChangerInfos == null) return GhostPartEventOutcome.NoFamilyState;

            List<ColorChangerGhostInfo> infos;
            if (!state.colorChangerInfos.TryGetValue(partPersistentId, out infos) || infos == null)
                return GhostPartEventOutcome.NoInfoForPart;

            bool sawCabinLight = false;
            for (int c = 0; c < infos.Count; c++)
            {
                var ccInfo = infos[c];
                if (ccInfo == null || !ccInfo.isCabinLight) continue;

                sawCabinLight = true;
                if (ccInfo.materials == null) continue;
                for (int i = 0; i < ccInfo.materials.Count; i++)
                {
                    if (ccInfo.materials[i].material != null)
                        return GhostPartEventOutcome.Applied;
                }
            }

            return sawCabinLight
                ? GhostPartEventOutcome.NoResolvedVisual
                : GhostPartEventOutcome.NoCabinLightEntry;
        }

        /// <summary>
        /// Applies ablation char color to heat shield parts (Pattern B) based on reentry intensity.
        /// Called from DriveReentryLayers when reentry glow is active.
        /// </summary>
        internal static void ApplyColorChangerCharState(GhostPlaybackState state, float intensity)
        {
            if (state == null || state.colorChangerInfos == null) return;

            foreach (var kvp in state.colorChangerInfos)
            {
                var infos = kvp.Value;
                for (int c = 0; c < infos.Count; c++)
                {
                    var ccInfo = infos[c];
                    if (ccInfo.isCabinLight) continue; // Only Pattern B responds to reentry

                    // Char is permanent — only increase, never fade back
                    float fraction = Mathf.Clamp01(intensity);
                    if (fraction <= ccInfo.peakCharIntensity) continue;
                    ccInfo.peakCharIntensity = fraction;

                    for (int i = 0; i < ccInfo.materials.Count; i++)
                    {
                        if (ccInfo.materials[i].material != null)
                        {
                            Color lerped = Color.Lerp(
                                ccInfo.materials[i].offColor,
                                ccInfo.materials[i].onColor,
                                fraction);
                            ccInfo.materials[i].material.SetColor(ccInfo.shaderProperty, lerped);
                        }
                    }
                }
            }
        }

        #endregion
    }
}
