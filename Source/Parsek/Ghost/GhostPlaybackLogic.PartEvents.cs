using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Part event replay: decouple / destroy / parachute / inventory / flag events and fake canopies.
    internal static partial class GhostPlaybackLogic
    {
        #region Part Events

        internal static void ApplyPartEvents(
            int recIdx, IPlaybackTrajectory rec, double currentUT, GhostPlaybackState state,
            bool allowTransientEffects = true)
        {
            if (rec.PartEvents == null || rec.PartEvents.Count == 0) return;
            if (state.ghost == null)
            {
                ParsekLog.VerboseRateLimited("Flight", $"apply-part-events-null-ghost-{recIdx}",
                    $"ApplyPartEvents: ghost is null for recording #{recIdx}");
                return;
            }

            int evtIdx = state.partEventIndex;
            var tree = state.partTree;
            var ghost = state.ghost;
            var logicalPartIds = state.logicalPartIds;
            bool visibilityChanged = false;
            bool needsReentryMeshRebuild = false;
            bool audioPowerTouched = false;
            HashSet<uint> placedTargetPartIds = null;
            // P8 step 1: allocated lazily on the first CONSUMED event, so the common
            // frame (cursor already caught up, loop body never entered) pays nothing.
            GhostPartEventApplyTally tally = null;

            while (evtIdx < rec.PartEvents.Count && rec.PartEvents[evtIdx].ut <= currentUT)
            {
                var evt = rec.PartEvents[evtIdx];
                if (tally == null) tally = new GhostPartEventApplyTally();
                ApplyOnePartEvent(
                    tally,
                    state,
                    ghost,
                    logicalPartIds,
                    tree,
                    evt,
                    currentUT,
                    allowTransientEffects,
                    ref visibilityChanged,
                    ref needsReentryMeshRebuild,
                    ref audioPowerTouched,
                    ref placedTargetPartIds);
                evtIdx++;
            }

            FinishPartEventWalk(recIdx, state, evtIdx, audioPowerTouched, tally);
            if (visibilityChanged)
                RefreshGhostAfterPartVisibilityChange(
                    state, ghost, placedTargetPartIds, ref needsReentryMeshRebuild);
            UpdateBlinkingLights(state, currentUT);
            // Retained so every caller (flight preview, the KSC-scene ghost paths) keeps driving
            // continuous robotic motion exactly as before. The flight engine ALSO calls this from
            // ApplyFrameVisuals, because this method early-returns when a recording has no
            // PartEvents at all and wheel spin must not depend on that. Calling it twice in one
            // frame is a no-op: each pass stamps info.lastUpdateUT = currentUT, so the second sees
            // deltaSeconds == 0 and skips.
            //
            // DELIBERATE ASYMMETRY, do not "fix" by adding a second call to the other scenes: only
            // the flight engine drives wheel spin unconditionally. A KSC-scene ghost never spins its
            // wheels at all (ParsekKSC never calls SetInterpolated, so the surface velocity
            // source stays None and TryResolveWheelGroundSpeedInputs declines), and the flight PREVIEW
            // ghost spins only for a recording that carries at least one part event, because a
            // zero-event recording early-returns above before reaching here. Both are acceptable:
            // KSC is a static parked-craft display, and preview is a scrubbing aid, not the replay.
            UpdateActiveRobotics(state, currentUT, rec.TrackSections);
            // S2, and retained here for exactly the reason UpdateActiveRobotics is: the flight
            // engine ALSO calls this from ApplyFrameVisuals, but the KSC-scene and flight-PREVIEW
            // ghost paths reach ApplyPartEvents and nothing else. Without this call a panel whose
            // deploy event just fired on one of those paths would arm a transition and then sit at
            // its stowed pose forever, which is worse than the snap S2 replaces. Calling it twice
            // in one frame is a no-op: the progress is a pure function of the event UT, so the
            // second pass recomputes the same fraction and writes the same pose.
            UpdateActiveDeployables(state, currentUT);
        }

        /// <summary>
        /// One consumed part event: routes it to its family's applier and tallies every surface
        /// the applier reports on. The flags and the placed-target set are the walk's own and
        /// accumulate across the events of one frame.
        /// </summary>
        internal static void ApplyOnePartEvent(
            GhostPartEventApplyTally tally,
            GhostPlaybackState state,
            GameObject ghost,
            HashSet<uint> logicalPartIds,
            Dictionary<uint, List<uint>> tree,
            PartEvent evt,
            double currentUT,
            bool allowTransientEffects,
            ref bool visibilityChanged,
            ref bool needsReentryMeshRebuild,
            ref bool audioPowerTouched,
            ref HashSet<uint> placedTargetPartIds)
        {
            switch (evt.eventType)
            {
                case PartEventType.Decoupled:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Visibility,
                        evt.partPersistentId,
                        ApplyDecoupledPartEvent(
                            state,
                            ghost,
                            logicalPartIds,
                            tree,
                            evt,
                            allowTransientEffects,
                            ref visibilityChanged,
                            ref needsReentryMeshRebuild));
                    break;
                case PartEventType.Destroyed:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Visibility,
                        evt.partPersistentId,
                        ApplyDestroyedPartEvent(
                            state,
                            ghost,
                            logicalPartIds,
                            evt,
                            allowTransientEffects,
                            ref visibilityChanged,
                            ref needsReentryMeshRebuild));
                    break;
                case PartEventType.ParachuteCut:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Parachute,
                        evt.partPersistentId,
                        ApplyParachuteCutEvent(state, evt.partPersistentId));
                    break;
                case PartEventType.ParachuteRepacked:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Parachute,
                        evt.partPersistentId,
                        ApplyParachuteRepackedEvent(state, evt.partPersistentId));
                    break;
                case PartEventType.ShroudJettisoned:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.JettisonPanel,
                        evt.partPersistentId,
                        ApplyJettisonPanelStateWithOutcome(state, evt, jettisoned: true));
                    break;
                case PartEventType.ParachuteDestroyed:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Visibility,
                        evt.partPersistentId,
                        ApplyParachuteDestroyedEvent(
                            state,
                            ghost,
                            logicalPartIds,
                            evt.partPersistentId,
                            ref visibilityChanged));
                    break;
                case PartEventType.ParachuteSemiDeployed:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Parachute,
                        evt.partPersistentId,
                        ApplyParachuteSemiDeployedEvent(state, evt.partPersistentId));
                    break;
                case PartEventType.ParachuteDeployed:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Parachute,
                        evt.partPersistentId,
                        ApplyParachuteDeployedEvent(state, ghost, evt.partPersistentId));
                    break;
                case PartEventType.EngineIgnited:
                    // Producers never emit a zero-throttle ignition (#165,
                    // PartStateSeeder.ShouldSkipZeroThrottleEngineSeed).
                    RecordEngineEvent(tally, state, evt, evt.value, ref audioPowerTouched);
                    break;
                case PartEventType.EngineShutdown:
                    RecordEngineEvent(tally, state, evt, 0f, ref audioPowerTouched);
                    break;
                case PartEventType.EngineThrottle:
                    RecordEngineEvent(tally, state, evt, evt.value, ref audioPowerTouched);
                    break;
                // S2: the four deployable-family event pairs take the ANIMATED path
                // (immediate: false). Every BASELINE caller keeps the snap overload; the
                // distinction is "the recording says this moved now" vs "put the ghost into
                // the state it spawned in".
                case PartEventType.DeployableExtended:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Deployable,
                        evt.partPersistentId,
                        ApplyDeployableStateWithOutcome(
                            state, evt, deployed: true, immediate: false));
                    break;
                case PartEventType.DeployableRetracted:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Deployable,
                        evt.partPersistentId,
                        ApplyDeployableStateWithOutcome(
                            state, evt, deployed: false, immediate: false));
                    break;
                // S6: a break is a SNAP, never an animation. The panel is gone in the frame it
                // broke (stock flings the subtree off as debris), so there is no pose to
                // interpolate toward and no `immediate` distinction to make.
                case PartEventType.DeployableBroken:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Deployable,
                        evt.partPersistentId,
                        ApplyDeployableBrokenStateWithOutcome(
                            state, evt.partPersistentId, broken: true));
                    break;
                // A wheel break / repair is a mesh swap, a snap in the frame it happened.
                case PartEventType.WheelBroken:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.WheelDamage,
                        evt.partPersistentId,
                        ApplyWheelDamageStateWithOutcome(
                            state, evt.partPersistentId, damaged: true));
                    break;
                case PartEventType.WheelRepaired:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.WheelDamage,
                        evt.partPersistentId,
                        ApplyWheelDamageStateWithOutcome(
                            state, evt.partPersistentId, damaged: false));
                    break;
                // S7: evt.ut is the loop's phase origin, which is what makes a scrubbed or
                // looping replay land on the SAME pose for the same recorded moment.
                case PartEventType.ConverterActivated:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.ConverterLoop,
                        evt.partPersistentId,
                        ApplyConverterLoopStateWithOutcome(
                            state, evt.partPersistentId, active: true, activeSinceUT: evt.ut));
                    break;
                case PartEventType.ConverterDeactivated:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.ConverterLoop,
                        evt.partPersistentId,
                        ApplyConverterLoopStateWithOutcome(
                            state, evt.partPersistentId, active: false, activeSinceUT: evt.ut));
                    break;
                // S4: all six EVA members share one applier so the plume gate lives in one
                // place. The RAGDOLL pair is recorded and applied but renders no POSE - it
                // gates the plume and marks the timeline (see PartEventType.EvaRagdollStarted).
                case PartEventType.EvaJetpackDeployed:
                case PartEventType.EvaJetpackStowed:
                case PartEventType.EvaJetpackThrustStarted:
                case PartEventType.EvaJetpackThrustStopped:
                case PartEventType.EvaRagdollStarted:
                case PartEventType.EvaRagdollEnded:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Eva,
                        evt.partPersistentId,
                        ApplyEvaStateWithOutcome(state, evt.eventType));
                    break;
                case PartEventType.ThermalAnimationHot:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Heat,
                        evt.partPersistentId,
                        ApplyHeatStateWithOutcome(state, evt, HeatLevel.Hot));
                    break;
                case PartEventType.ThermalAnimationMedium:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Heat,
                        evt.partPersistentId,
                        ApplyHeatStateWithOutcome(state, evt, HeatLevel.Medium));
                    break;
                case PartEventType.ThermalAnimationCold:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Heat,
                        evt.partPersistentId,
                        ApplyHeatStateWithOutcome(state, evt, HeatLevel.Cold));
                    break;
                case PartEventType.LightOn:
                    RecordLightPowerEvent(tally, state, evt, on: true);
                    break;
                case PartEventType.LightOff:
                    RecordLightPowerEvent(tally, state, evt, on: false);
                    break;
                case PartEventType.LightBlinkEnabled:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.BlinkState,
                        evt.partPersistentId,
                        ApplyLightBlinkModeEventWithOutcome(
                            state, evt.partPersistentId, enabled: true, evt.value));
                    break;
                case PartEventType.LightBlinkDisabled:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.BlinkState,
                        evt.partPersistentId,
                        ApplyLightBlinkModeEventWithOutcome(
                            state, evt.partPersistentId, enabled: false, evt.value));
                    break;
                case PartEventType.LightBlinkRate:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.BlinkState,
                        evt.partPersistentId,
                        ApplyLightBlinkRateEventWithOutcome(
                            state, evt.partPersistentId, evt.value));
                    break;
                case PartEventType.GearDeployed:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Deployable,
                        evt.partPersistentId,
                        ApplyDeployableStateWithOutcome(
                            state, evt, deployed: true, immediate: false));
                    break;
                case PartEventType.GearRetracted:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Deployable,
                        evt.partPersistentId,
                        ApplyDeployableStateWithOutcome(
                            state, evt, deployed: false, immediate: false));
                    break;
                case PartEventType.CargoBayOpened:
                    RecordCargoBayEvent(tally, state, evt, open: true);
                    break;
                case PartEventType.CargoBayClosed:
                    RecordCargoBayEvent(tally, state, evt, open: false);
                    break;
                case PartEventType.FairingJettisoned:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Fairing,
                        evt.partPersistentId,
                        ApplyFairingJettisonedState(state, evt.partPersistentId));
                    break;
                case PartEventType.RCSActivated:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.RcsFx,
                        evt.partPersistentId,
                        SetRcsEmissionWithOutcome(state, evt, evt.value));
                    break;
                case PartEventType.RCSStopped:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.RcsFx,
                        evt.partPersistentId,
                        SetRcsEmissionWithOutcome(state, evt, 0f));
                    break;
                case PartEventType.RCSThrottle:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.RcsFx,
                        evt.partPersistentId,
                        SetRcsEmissionWithOutcome(state, evt, evt.value));
                    break;
                case PartEventType.RoboticMotionStarted:
                case PartEventType.RoboticPositionSample:
                case PartEventType.RoboticMotionStopped:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Robotic,
                        evt.partPersistentId,
                        ApplyRoboticEvent(state, evt, currentUT));
                    break;
                case PartEventType.InventoryPartPlaced:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Inventory,
                        evt.partPersistentId,
                        ApplyInventoryPartPlacedEvent(
                            state,
                            logicalPartIds,
                            evt.partPersistentId,
                            ref placedTargetPartIds,
                            ref visibilityChanged));
                    break;
                case PartEventType.InventoryPartRemoved:
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Inventory,
                        evt.partPersistentId,
                        ApplyInventoryPartRemovedEvent(
                            state,
                            logicalPartIds,
                            evt.partPersistentId,
                            ref visibilityChanged));
                    break;
                default:
                    // Docked / Undocked are chain-segment boundary markers with no ghost
                    // pose, and an older build reading a NEWER sidecar can materialise an
                    // undefined member here (the binary reader raw-casts). Both land in
                    // this arm; naming them in the log is the whole point of the
                    // unhandled-event-type reason class.
                    tally.Record(
                        evt.eventType,
                        GhostPartEventSurface.Visibility,
                        evt.partPersistentId,
                        GhostPartEventOutcome.UnhandledEventType);
                    break;
            }
        }

        /// <summary>
        /// After the walk: advances the cursor, enforces the looped-audio cap when an engine
        /// event touched audio power, and writes the aggregate line and the per-family lines.
        /// </summary>
        internal static void FinishPartEventWalk(
            int recIdx,
            GhostPlaybackState state,
            int evtIdx,
            bool audioPowerTouched,
            GhostPartEventApplyTally tally)
        {
            int appliedCount = evtIdx - state.partEventIndex;
            state.partEventIndex = evtIdx;
            if (audioPowerTouched)
                EnforceLoopedAudioPlaybackCapWithTestingOverride(state);
            if (appliedCount > 0)
                ParsekLog.VerboseRateLimited("Flight", $"part-events-{recIdx}",
                    $"Applied {appliedCount} part events for ghost #{recIdx} (evtIdx now {evtIdx})");
            // P8 step 1: the per-family breakdown the aggregate line above cannot carry.
            // One rate-limited line per (recording, family, surface) actually touched.
            if (tally != null) tally.Flush(recIdx);
        }

        /// <summary>
        /// After a walk that hid or showed a part: re-derives compound-part visibility, restores
        /// the compound parts of placed targets, rebuilds the reentry meshes when anything moved,
        /// and recentres the camera pivot on the parts still shown.
        /// </summary>
        internal static void RefreshGhostAfterPartVisibilityChange(
            GhostPlaybackState state,
            GameObject ghost,
            HashSet<uint> placedTargetPartIds,
            ref bool needsReentryMeshRebuild)
        {
            if (RefreshCompoundPartVisibility(state))
                needsReentryMeshRebuild = true;
            if (RestoreCompoundPartsForPlacedTargets(state, placedTargetPartIds))
                needsReentryMeshRebuild = true;
            if (needsReentryMeshRebuild)
                GhostVisualBuilder.RebuildReentryMeshes(ghost, state.reentryFxInfo);
            RecalculateCameraPivot(state);
        }

        /// <summary>
        /// P8 step 1: the engine arms drive TWO surfaces (particle FX and the ghost's
        /// looped audio) and the audio half already had its own boolean, which the
        /// switch consumed for the playback-cap enforcement and then threw away. Both
        /// are tallied so a silent-but-visible plume (or the reverse) is readable.
        /// </summary>
        private static void RecordEngineEvent(
            GhostPartEventApplyTally tally,
            GhostPlaybackState state,
            PartEvent evt,
            float power,
            ref bool audioPowerTouched)
        {
            tally.Record(
                evt.eventType,
                GhostPartEventSurface.EngineFx,
                evt.partPersistentId,
                SetEngineEmissionWithOutcome(state, evt, power));

            // The classifier mirrors SetEngineAudio's only two declining paths, so the
            // recorded outcome and the boolean below cannot disagree - it is there to
            // name WHICH of the two, which the boolean cannot.
            GhostPartEventOutcome audioOutcome = ClassifyEngineAudioApply(state, evt);
            if (SetEngineAudio(state, evt, power, enforcePlaybackCap: false))
                audioPowerTouched = true;
            tally.Record(
                evt.eventType,
                GhostPartEventSurface.EngineAudio,
                evt.partPersistentId,
                audioOutcome);
        }

        /// <summary>
        /// P8 step 1: a light event's two independent surfaces, tallied separately.
        /// See <see cref="ApplyLightPowerEventWithOutcomes"/> for why one boolean for
        /// the pair was the thing that made the colour-changer half unmeasurable.
        /// </summary>
        private static void RecordLightPowerEvent(
            GhostPartEventApplyTally tally,
            GhostPlaybackState state,
            PartEvent evt,
            bool on)
        {
            ApplyLightPowerEventWithOutcomes(
                state,
                evt.partPersistentId,
                on,
                out GhostPartEventOutcome lightOutcome,
                out GhostPartEventOutcome colorChangerOutcome);
            tally.Record(
                evt.eventType, GhostPartEventSurface.Light, evt.partPersistentId, lightOutcome);
            tally.Record(
                evt.eventType, GhostPartEventSurface.ColorChanger, evt.partPersistentId,
                colorChangerOutcome);
        }

        /// <summary>
        /// P8 step 1: the cargo-bay CASCADE. The jettison arm is tallied only when the
        /// cascade actually reached it, so a bay whose doors animated does not also
        /// report a phantom jettison skip.
        /// </summary>
        private static void RecordCargoBayEvent(
            GhostPartEventApplyTally tally,
            GhostPlaybackState state,
            PartEvent evt,
            bool open)
        {
            ApplyCargoBayStateWithOutcomes(
                state,
                evt,
                open,
                immediate: false,
                out GhostPartEventOutcome deployableOutcome,
                out GhostPartEventOutcome jettisonOutcome,
                out bool jettisonArmReached);
            tally.Record(
                evt.eventType, GhostPartEventSurface.Deployable, evt.partPersistentId,
                deployableOutcome);
            if (jettisonArmReached)
                tally.Record(
                    evt.eventType, GhostPartEventSurface.JettisonPanel, evt.partPersistentId,
                    jettisonOutcome);
        }

        /// <summary>
        /// P8 step 1: the FairingJettisoned arm, lifted out of the switch so its three
        /// distinct early-outs become named reason classes instead of one silent
        /// nested-if fall-through.
        /// </summary>
        internal static GhostPartEventOutcome ApplyFairingJettisonedState(
            GhostPlaybackState state, uint partPersistentId)
        {
            GhostPartEventOutcome precondition = ClassifyFairingJettisonApply(state, partPersistentId);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            state.fairingInfos[partPersistentId].fairingMeshObject.SetActive(false);
            return GhostPartEventOutcome.Applied;
        }

        /// <summary>P8 step 1 precondition classifier (pure).</summary>
        internal static GhostPartEventOutcome ClassifyFairingJettisonApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.fairingInfos == null) return GhostPartEventOutcome.NoFamilyState;
            FairingGhostInfo fInfo;
            if (!state.fairingInfos.TryGetValue(partPersistentId, out fInfo) || fInfo == null)
                return GhostPartEventOutcome.NoInfoForPart;
            if (fInfo.fairingMeshObject == null)
                return GhostPartEventOutcome.NoResolvedVisual;
            return GhostPartEventOutcome.Applied;
        }

        /// <summary>
        /// P8 step 1: unconditional - the subtree is hidden and dropped from logical
        /// presence on every path - so Applied on the VISIBILITY surface is always the
        /// honest report. There is no ghost lookup here that can miss.
        /// </summary>
        private static GhostPartEventOutcome ApplyDecoupledPartEvent(
            GhostPlaybackState state,
            GameObject ghost,
            HashSet<uint> logicalPartIds,
            Dictionary<uint, List<uint>> tree,
            PartEvent evt,
            bool allowTransientEffects,
            ref bool visibilityChanged,
            ref bool needsReentryMeshRebuild)
        {
            // The decoupled subtree's parts are about to become a separate debris
            // recording with its own AudioSources. The parent ghost's per-pid
            // AudioGhostInfo / EngineGhostInfo / RcsGhostInfo entries for those parts
            // would otherwise keep playing, because audio sources get reanchored to the
            // ghost's cameraPivot at spawn (AttachGhostAudioToWatchPivot) — hiding the
            // part visual no longer takes the audio with it. Walk the whole subtree.
            StopFxAndAudioForSubtree(state, evt.partPersistentId, tree);
            ApplyHeatState(state, evt, HeatLevel.Cold);
            if (allowTransientEffects)
                SpawnPartPuffAtPart(ghost, evt.partPersistentId);
            if (tree != null)
            {
                HidePartSubtree(ghost, evt.partPersistentId, tree);
                RemovePartSubtreeFromLogicalPresence(logicalPartIds, evt.partPersistentId, tree);
            }
            else
            {
                HideGhostPart(ghost, evt.partPersistentId);
                RemovePartSubtreeFromLogicalPresence(logicalPartIds, evt.partPersistentId, null);
            }
            visibilityChanged = true;
            needsReentryMeshRebuild = true;
            return GhostPartEventOutcome.Applied;
        }

        /// <summary>
        /// Walks the part-tree subtree rooted at <paramref name="rootPid"/> and returns
        /// every pid reachable through it (root + descendants). Pure logic: no Unity
        /// dependencies, safe to call from xUnit. If <paramref name="tree"/> is null
        /// only the root pid is returned, matching HidePartSubtree's null-tree
        /// fallback.
        /// </summary>
        internal static List<uint> CollectSubtreePids(
            uint rootPid, Dictionary<uint, List<uint>> tree)
        {
            var result = new List<uint>();
            var stack = new Stack<uint>();
            stack.Push(rootPid);
            while (stack.Count > 0)
            {
                uint pid = stack.Pop();
                result.Add(pid);
                if (tree == null) continue;
                List<uint> children;
                if (tree.TryGetValue(pid, out children))
                {
                    for (int i = 0; i < children.Count; i++)
                        stack.Push(children[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// Walks the part-tree subtree rooted at <paramref name="rootPid"/> and stops
        /// engine FX, RCS FX, and ghost audio for every pid in the subtree. Used by
        /// Decoupled events so the parent ghost's per-pid FX/audio entries for parts
        /// that are physically gone (and now belong to a separate debris recording)
        /// stop emitting on the parent. The single-pid Stop helpers were not enough:
        /// HidePartSubtree hides every descendant of the decoupler, but FX and audio
        /// dictionaries are keyed by part pid, so a child engine's plume/audio survived
        /// the decouple and kept playing under the parent's cameraPivot until the
        /// parent ghost itself was destroyed.
        ///
        /// Touches Unity APIs through the per-pid Stop helpers so it cannot be
        /// invoked from xUnit (`System.Security.SecurityException : ECall methods
        /// must be packaged into a system module.`). The pure-walk logic is exposed
        /// via <see cref="CollectSubtreePids"/> for unit testing; the integrated
        /// audio-stop behavior is covered by in-game tests.
        /// </summary>
        internal static void StopFxAndAudioForSubtree(
            GhostPlaybackState state, uint rootPid, Dictionary<uint, List<uint>> tree)
        {
            if (state == null) return;
            var pids = CollectSubtreePids(rootPid, tree);
            for (int i = 0; i < pids.Count; i++)
            {
                StopEngineFxForPart(state, pids[i]);
                StopRcsFxForPart(state, pids[i]);
                StopAudioForPart(state, pids[i]);
            }
            if (pids.Count > 1)
                ParsekLog.VerboseRateLimited("GhostAudio", $"subtree-fx-stop-{rootPid}",
                    $"Stopped FX/audio for decoupled subtree rooted at pid={rootPid}: {pids.Count} pid(s)",
                    5.0);
        }

        /// <summary>
        /// P8 step 1: unconditional, like the decouple sibling - Applied on the
        /// VISIBILITY surface.
        /// </summary>
        private static GhostPartEventOutcome ApplyDestroyedPartEvent(
            GhostPlaybackState state,
            GameObject ghost,
            HashSet<uint> logicalPartIds,
            PartEvent evt,
            bool allowTransientEffects,
            ref bool visibilityChanged,
            ref bool needsReentryMeshRebuild)
        {
            StopEngineFxForPart(state, evt.partPersistentId);
            StopRcsFxForPart(state, evt.partPersistentId);
            StopAudioForPart(state, evt.partPersistentId);
            if (allowTransientEffects)
                PlayPartDestroyedFxAtPart(
                    ghost,
                    evt.partPersistentId,
                    evt.partName,
                    state.audioPaused,
                    state.atmosphereFactor,
                    ResolveAudioPriorityDistance(state));
            ApplyHeatState(state, evt, HeatLevel.Cold);
            HideGhostPart(ghost, evt.partPersistentId);
            RemovePartSubtreeFromLogicalPresence(logicalPartIds, evt.partPersistentId, null);
            visibilityChanged = true;
            needsReentryMeshRebuild = true;
            return GhostPartEventOutcome.Applied;
        }

        /// <summary>
        /// Spawn a stock-style explosion (visual + audio) at an individual destroyed part's world
        /// position. The flight scene path delegates to <see cref="FXMonger.Explode(Part, Vector3d, double)"/>
        /// with the part's recorded `explosionPotential` so KSP picks the size-appropriate clip
        /// from its `explosionSounds[]` array — decompiled `Part.cs:11610` calls
        /// `FXMonger.Explode(this, partTransform.position, explosionPotential + speedOffset)` and
        /// most stock parts default to `explosionPotential = 0.5f` (decompiled `Part.cs:591`),
        /// which lands on slot 1 (`sound_explosion_debris2`) for the 3-element stock array.
        /// We omit the speedOffset (0/0.12/0.25 by surface speed) because ghost playback doesn't
        /// know the live ship speed at destruction time; that produces audio one bucket lower
        /// than stock for fast-moving destruction events but the alternative would require
        /// recording the speed in every PartEvent.
        ///
        /// Replaces the old `PlayOneShotAtGhost(Destroyed)` path which always played
        /// <c>sound_explosion_large</c> through the ghost's per-vessel cameraPivot-anchored
        /// AudioSource regardless of part size and reserved a global "audio gate" for the full
        /// clip duration that then suppressed every subsequent terminal-vessel
        /// <see cref="FXMonger.Explode"/> call inside the same window, leaving multi-debris
        /// breakups nearly silent in watch mode (only the first explosion played sound).
        ///
        /// While the stock pause menu is open (<paramref name="audioPaused"/>=true), the FXMonger
        /// path is skipped — `FXMonger.Explode` would queue a SHIP_VOLUME `PlayOneShot` on a fresh
        /// AudioSource that does NOT respect the engine-side pause, so a destroyed-part event
        /// applied in the same frame the player opens Esc would punch through the pause and
        /// regress the flight pause-audio fix. The puff fallback runs instead so the destruction
        /// still produces a visual cue without queuing audio.
        ///
        /// FXMonger isn't loaded outside the flight scene; KSC playback (and any other scene
        /// where the singleton is not live) falls back to the small Parsek particle puff PLUS
        /// an independent positional explosion AudioSource via
        /// <see cref="TryPlayIndependentExplosionOneShot"/> so destroyed-part events at KSC stay
        /// audible (pre-fix the per-vessel `oneShotAudio.audioSource.PlayOneShot` covered this;
        /// removing that plumbing without an independent-audio fallback silenced KSC per-part
        /// destruction). The independent path is also the rescue route when FXMonger is live but
        /// `Explode` returned false (empty prefab array, threw, etc.) so the user still hears
        /// destruction audio.
        /// </summary>
        internal static void PlayPartDestroyedFxAtPart(
            GameObject ghost,
            uint persistentId,
            string partName,
            bool audioPaused,
            float atmosphereFactor,
            double audioPriorityDistanceMeters)
        {
            if (ghost == null) return;
            if (ShouldSuppressVisualFx(TimeWarp.CurrentRate)) return;

            var t = GhostVisualBuilder.FindGhostPartTransform(ghost, persistentId);
            if (t == null || !t.gameObject.activeSelf) return;

            double power = ResolvePartExplosionPower(partName);

            // Flight scene: FXMonger handles audio + visual + spatial coalescing in one call.
            // We only take this branch when audio isn't paused (FXMonger.Explode would queue a
            // SHIP_VOLUME PlayOneShot on a fresh AudioSource that the per-source PauseAllAudio
            // doesn't reach mid-flight; PauseFxMongerExplosionAudioSources covers the in-flight
            // case but new spawns during the pause should not happen at all).
            if (!audioPaused && GhostVisualBuilder.IsFxMongerLive())
            {
                if (GhostVisualBuilder.TryTriggerStockExplosionFx(t.position, power, out string failure))
                {
                    ParsekLog.VerboseRateLimited("ExplosionFx", $"part-destroyed-fxmonger-{persistentId}",
                        $"FXMonger.Explode queued for destroyed part pid={persistentId} part='{partName ?? "?"}' " +
                        $"at ({t.position.x:F1},{t.position.y:F1},{t.position.z:F1}) " +
                        $"power={power.ToString("F2", CultureInfo.InvariantCulture)}",
                        5.0);
                    return;
                }

                ParsekLog.VerboseRateLimited("ExplosionFx", $"part-destroyed-fxmonger-failed-{persistentId}",
                    $"FXMonger.Explode failed for destroyed part pid={persistentId}: {failure}; " +
                    $"falling back to puff + independent audio",
                    10.0);
            }
            else if (audioPaused)
            {
                ParsekLog.VerboseRateLimited("ExplosionFx", $"part-destroyed-paused-{persistentId}",
                    $"Pause menu open: skipping FXMonger.Explode for destroyed part pid={persistentId}, " +
                    $"falling back to particle puff (visual only)",
                    5.0);
            }

            // Fallback path — runs when (a) FXMonger isn't loaded (KSC scene and other non-flight
            // scenes), (b) FXMonger.Explode itself failed, or (c) the stock pause menu is open.
            // The puff is always spawned as the visual cue. Audio is queued via the independent
            // explosion one-shot path UNLESS audio is paused — then we deliberately keep the
            // event silent so destroyed-part events applied during pause don't punch through.
            SpawnPartPuffAtPart(ghost, persistentId);
            if (!audioPaused && atmosphereFactor > 0.001f)
            {
                TryPlayIndependentExplosionOneShot(
                    t.position,
                    atmosphereFactor,
                    audioPriorityDistanceMeters,
                    power,
                    $"destroyed part pid={persistentId} part='{partName ?? "?"}'");
            }
            else
            {
                ParsekLog.VerboseRateLimited("ExplosionFx", $"part-destroyed-puff-only-{persistentId}",
                    $"Particle puff spawned for destroyed part pid={persistentId} " +
                    $"(audio suppressed: paused={audioPaused} atmosphereFactor={atmosphereFactor.ToString("F2", CultureInfo.InvariantCulture)})",
                    5.0);
            }
        }

        /// <summary>
        /// Resolves the FXMonger power bucket for a destroyed part by looking up the part prefab's
        /// `explosionPotential` (matching stock `Part.explode()` which calls
        /// <c>FXMonger.Explode(this, pos, explosionPotential + speedOffset)</c>). Returns
        /// <see cref="GhostAudioPresets.DefaultPartExplosionPotential"/> (0.5 — stock default) when
        /// the part name is missing or doesn't resolve to a loaded `AvailablePart`. Power is
        /// clamped to [0,1] so a custom-cfg part with `explosionPotential` outside that range still
        /// produces a valid index pick. Pure helper with optional <paramref name="lookup"/> seam
        /// for unit tests; production callers leave it null and use PartLoader.
        /// </summary>
        internal static double ResolvePartExplosionPower(
            string partName, ExplosionPotentialLookup lookup = null)
        {
            ExplosionPotentialLookup resolve = lookup ?? DefaultExplosionPotentialLookup;
            float? potential = resolve(partName);
            if (!potential.HasValue)
                return GhostAudioPresets.DefaultPartExplosionPotential;
            return Mathf.Clamp01(potential.Value);
        }

        internal delegate float? ExplosionPotentialLookup(string partName);

        private static float? DefaultExplosionPotentialLookup(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return null;
            try
            {
                AvailablePart info = PartLoader.getPartInfoByName(partName);
                return info?.partPrefab?.explosionPotential;
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("ExplosionFx", $"explosion-potential-lookup-failed-{partName}",
                    $"PartLoader.getPartInfoByName('{partName}') threw {ex.GetType().Name}: {ex.Message}; " +
                    $"using default explosionPotential",
                    30.0);
                return null;
            }
        }

        /// <summary>
        /// Applies the semi-deployed (drogue-stage) canopy pose. Extracted from the
        /// ApplyPartEvents switch so the M1 snapshot baseline can reach the same code —
        /// a pack whose snapshot says SEMIDEPLOYED must render half-open at spawn, not
        /// stowed. No-ops unless the build sampled a semi-deployed pose for this pack.
        /// </summary>
        private static GhostPartEventOutcome ApplyParachuteSemiDeployedEvent(
            GhostPlaybackState state,
            uint partPersistentId)
        {
            if (state?.parachuteInfos == null) return GhostPartEventOutcome.NoFamilyState;

            ParachuteGhostInfo semiInfo;
            if (!state.parachuteInfos.TryGetValue(partPersistentId, out semiInfo)
                || semiInfo == null)
                return GhostPartEventOutcome.NoInfoForPart;
            if (semiInfo.canopyTransform == null)
                return GhostPartEventOutcome.NoResolvedVisual;
            // The semi-deployed (drogue) pose is optional at build time; a pack whose
            // prefab exposed no streamer stage has nothing to interpolate toward, which
            // is a BUILD fact and not an apply failure.
            if (!semiInfo.semiDeployedSampled)
                return GhostPartEventOutcome.PoseNotSampled;

            semiInfo.canopyTransform.localScale = semiInfo.semiDeployedCanopyScale;
            semiInfo.canopyTransform.localPosition = semiInfo.semiDeployedCanopyPos;
            semiInfo.canopyTransform.localRotation = semiInfo.semiDeployedCanopyRot;
            if (semiInfo.capTransform != null)
                semiInfo.capTransform.gameObject.SetActive(false);
            return GhostPartEventOutcome.Applied;
        }

        /// <summary>
        /// The cap-visibility half of every parachute event's ghost pose, pulled out of the
        /// Transform-touching handlers so it is assertable without a Unity scene. Returns false for
        /// event types that say nothing about a cap.
        ///
        /// The cap is the whole bug this table exists to pin down. A stock chute's cap is the nose
        /// cone that covers the packed canopy: DEPLOY blows it off, and stock <c>Repack()</c> puts it
        /// back (<c>cap.gameObject.SetActive(true)</c>). Playback had a cut hide the cap with nothing
        /// that ever re-showed it, so once the recorder mistook a repack for a cut the chute rendered
        /// as an empty can for the rest of the recording — visually permanent, and faithfully
        /// reproduced on every replay.
        /// </summary>
        internal static bool TryResolveParachuteCapActive(PartEventType type, out bool capActive)
        {
            switch (type)
            {
                case PartEventType.ParachuteSemiDeployed:
                case PartEventType.ParachuteDeployed:
                case PartEventType.ParachuteCut:
                case PartEventType.ParachuteDestroyed:
                    capActive = false;
                    return true;
                case PartEventType.ParachuteRepacked:
                    // The only parachute event that puts the cap BACK.
                    capActive = true;
                    return true;
                default:
                    capActive = false;
                    return false;
            }
        }

        private static GhostPartEventOutcome ApplyParachuteCutEvent(
            GhostPlaybackState state,
            uint partPersistentId)
        {
            GhostPartEventOutcome outcome = GhostPartEventOutcome.NoFamilyState;
            if (state.parachuteInfos != null)
            {
                ParachuteGhostInfo cutInfo;
                if (!state.parachuteInfos.TryGetValue(partPersistentId, out cutInfo))
                {
                    outcome = GhostPartEventOutcome.NoInfoForPart;
                }
                else
                {
                    bool touched = false;
                    if (cutInfo.canopyTransform != null)
                    {
                        cutInfo.canopyTransform.localScale = Vector3.zero;
                        touched = true;
                    }
                    if (cutInfo.capTransform != null &&
                        TryResolveParachuteCapActive(PartEventType.ParachuteCut, out bool cutCapActive))
                    {
                        cutInfo.capTransform.gameObject.SetActive(cutCapActive);
                        touched = true;
                    }
                    outcome = touched
                        ? GhostPartEventOutcome.Applied
                        : GhostPartEventOutcome.NoResolvedVisual;
                }
            }
            DestroyFakeCanopy(state, partPersistentId);
            return outcome;
        }

        /// <summary>
        /// Applies a ParachuteRepacked event: the exact inverse of
        /// <see cref="ApplyParachuteCutEvent"/>, and a faithful mirror of what stock
        /// <c>ModuleParachute.Repack()</c> does to the real part
        /// (<c>cap.gameObject.SetActive(true)</c> + <c>canopy.gameObject.SetActive(false)</c>).
        ///
        /// Canopy back to its captured stowed pose (hidden), and — the part that matters — the CAP
        /// RESTORED. The cut hid the cap permanently, which is why a repack replayed as a cut left
        /// the chute rendering as an empty can for the rest of the recording.
        ///
        /// The fake-canopy destroy is kept: a stowed chute has no canopy, so if the deploy fell back
        /// to a fake canopy sphere it must be gone here too. DestroyFakeCanopy is idempotent, so
        /// this is safe whether the preceding cut already removed it or the repack arrives without
        /// one (the ordinary real-canopy path).
        /// </summary>
        private static GhostPartEventOutcome ApplyParachuteRepackedEvent(
            GhostPlaybackState state,
            uint partPersistentId)
        {
            GhostPartEventOutcome outcome = GhostPartEventOutcome.NoFamilyState;
            if (state.parachuteInfos != null)
            {
                ParachuteGhostInfo repackInfo;
                if (!state.parachuteInfos.TryGetValue(partPersistentId, out repackInfo))
                {
                    outcome = GhostPartEventOutcome.NoInfoForPart;
                }
                else
                {
                    bool touched = false;
                    if (repackInfo.canopyTransform != null)
                    {
                        repackInfo.canopyTransform.localScale = repackInfo.stowedCanopyScale;
                        repackInfo.canopyTransform.localPosition = repackInfo.stowedCanopyPos;
                        repackInfo.canopyTransform.localRotation = repackInfo.stowedCanopyRot;
                        touched = true;
                    }
                    if (repackInfo.capTransform != null &&
                        TryResolveParachuteCapActive(PartEventType.ParachuteRepacked, out bool repackCapActive))
                    {
                        repackInfo.capTransform.gameObject.SetActive(repackCapActive);
                        touched = true;
                    }
                    outcome = touched
                        ? GhostPartEventOutcome.Applied
                        : GhostPartEventOutcome.NoResolvedVisual;
                }
            }
            DestroyFakeCanopy(state, partPersistentId);
            return outcome;
        }

        /// <summary>
        /// P8 step 1: unconditional, so it reports Applied on the VISIBILITY surface -
        /// the part is hidden and dropped from logical presence whatever the ghost
        /// carried for it. The canopy cleanup above is best-effort tidying of a visual
        /// that is about to be hidden anyway, not the outcome.
        /// </summary>
        private static GhostPartEventOutcome ApplyParachuteDestroyedEvent(
            GhostPlaybackState state,
            GameObject ghost,
            HashSet<uint> logicalPartIds,
            uint partPersistentId,
            ref bool visibilityChanged)
        {
            // Clean up canopy visuals before hiding the part
            if (state.parachuteInfos != null)
            {
                ParachuteGhostInfo destroyedInfo;
                if (state.parachuteInfos.TryGetValue(partPersistentId, out destroyedInfo))
                {
                    if (destroyedInfo.canopyTransform != null)
                        destroyedInfo.canopyTransform.localScale = Vector3.zero;
                }
            }
            DestroyFakeCanopy(state, partPersistentId);
            HideGhostPart(ghost, partPersistentId);
            RemovePartSubtreeFromLogicalPresence(logicalPartIds, partPersistentId, null);
            visibilityChanged = true;
            return GhostPartEventOutcome.Applied;
        }

        /// <summary>
        /// The bookkeeping (logical presence, placed-target set) is unconditional, but the
        /// REPORTED outcome follows the ghost: <c>applied</c> only when the ghost carries a
        /// transform for the pid, otherwise <c>no-info-for-part</c>. The distinction is
        /// real rather than defensive: stock places an EVA ground-science part as its OWN
        /// new vessel (<c>ModuleInventoryPart.DeployGroundPart</c> -> <c>AddVessel</c>), so
        /// the pid the recorder captures on the kerbal's recording is never a part of the
        /// kerbal's ghost, and an unconditional <c>applied</c> claimed a visual write that
        /// did not happen (coverage wave 10, todo EVA-GROUND-SCIENCE-PLACED-PART-PID-NOT-ON-VESSEL).
        /// </summary>
        private static GhostPartEventOutcome ApplyInventoryPartPlacedEvent(
            GhostPlaybackState state,
            HashSet<uint> logicalPartIds,
            uint partPersistentId,
            ref HashSet<uint> placedTargetPartIds,
            ref bool visibilityChanged)
        {
            bool carried = GhostCarriesPart(state, partPersistentId);
            SetGhostPartActive(state, partPersistentId, true);
            if (logicalPartIds != null)
                logicalPartIds.Add(partPersistentId);
            if (placedTargetPartIds == null)
                placedTargetPartIds = new HashSet<uint>();
            placedTargetPartIds.Add(partPersistentId);
            visibilityChanged = true;
            return InventoryApplyOutcome(carried);
        }

        private static GhostPartEventOutcome ApplyInventoryPartRemovedEvent(
            GhostPlaybackState state,
            HashSet<uint> logicalPartIds,
            uint partPersistentId,
            ref bool visibilityChanged)
        {
            bool carried = GhostCarriesPart(state, partPersistentId);
            SetGhostPartActive(state, partPersistentId, false);
            RemovePartSubtreeFromLogicalPresence(logicalPartIds, partPersistentId, null);
            visibilityChanged = true;
            return InventoryApplyOutcome(carried);
        }

        /// <summary>The inventory surface's reported outcome. Pure.</summary>
        internal static GhostPartEventOutcome InventoryApplyOutcome(bool ghostCarriesPart)
            => ghostCarriesPart ? GhostPartEventOutcome.Applied : GhostPartEventOutcome.NoInfoForPart;

        private static bool GhostCarriesPart(GhostPlaybackState state, uint persistentId)
            => state != null && state.ghost != null
               && GhostVisualBuilder.FindGhostPartTransform(state.ghost, persistentId) != null;

        /// <summary>
        /// Spawns a small smoke puff + spark FX at a ghost part's world position.
        /// Called before hiding the part on Decoupled/Destroyed events.
        /// </summary>
        internal static void SpawnPartPuffAtPart(GameObject ghost, uint persistentId)
        {
            if (ghost == null) return;
            if (ShouldSuppressVisualFx(TimeWarp.CurrentRate)) return;
            var t = GhostVisualBuilder.FindGhostPartTransform(ghost, persistentId);
            if (t == null)
            {
                return;
            }
            if (!t.gameObject.activeSelf)
            {
                return;
            }

            // Estimate part scale from its renderer bounds
            float partScale = 1f;
            var renderer = t.GetComponentInChildren<Renderer>();
            if (renderer != null)
                partScale = renderer.bounds.size.magnitude * 0.5f;

            var pos = t.position;
            GhostVisualBuilder.SpawnPartPuffFx(pos, partScale);
        }

        internal static void HideGhostPart(GameObject ghost, uint persistentId)
        {
            var t = GhostVisualBuilder.FindGhostPartTransform(ghost, persistentId);
            if (t != null) t.gameObject.SetActive(false);
        }

        internal static void SetGhostPartActive(GameObject ghost, uint persistentId, bool active)
        {
            if (ghost == null) return;
            var t = GhostVisualBuilder.FindGhostPartTransform(ghost, persistentId);
            if (t != null) t.gameObject.SetActive(active);
        }

        internal static void SetGhostPartActive(GhostPlaybackState state, uint persistentId, bool active)
        {
            if (state == null)
                return;

            SetGhostPartActive(state.ghost, persistentId, active);

            if (state.audioInfos == null)
                return;

            var restores = active ? new List<(int moduleIndex, float power)>() : null;
            foreach (var info in state.audioInfos.Values)
            {
                if (info == null || info.partPersistentId != persistentId || info.audioSource == null)
                    continue;

                if (!active && info.audioSource.isPlaying)
                    StopLoopedGhostAudio(info, "part-inactive");

                info.audioSource.gameObject.SetActive(active);

                if (active && info.currentPower > 0f)
                    restores.Add((info.moduleIndex, info.currentPower));
            }

            if (restores == null)
                return;

            bool audioPowerTouched = false;
            for (int i = 0; i < restores.Count; i++)
            {
                if (SetEngineAudio(state, new PartEvent
                    {
                        partPersistentId = persistentId,
                        moduleIndex = restores[i].moduleIndex
                    }, restores[i].power, enforcePlaybackCap: false))
                    audioPowerTouched = true;
            }
            if (audioPowerTouched)
                EnforceLoopedAudioPlaybackCapWithTestingOverride(state);
        }

        internal static void InitializeInventoryPlacementVisibility(
            IPlaybackTrajectory rec, GhostPlaybackState state)
        {
            if (rec == null || rec.PartEvents == null || rec.PartEvents.Count == 0) return;
            if (state == null || state.ghost == null) return;

            // If a part's first placement-related event is "placed", start hidden so it
            // visibly appears only when the event fires.
            var initialized = new HashSet<uint>();
            int hidden = 0;
            for (int i = 0; i < rec.PartEvents.Count; i++)
            {
                var evt = rec.PartEvents[i];
                if (initialized.Contains(evt.partPersistentId)) continue;

                if (evt.eventType == PartEventType.InventoryPartPlaced)
                {
                    SetGhostPartActive(state, evt.partPersistentId, false);
                    initialized.Add(evt.partPersistentId);
                    hidden++;
                }
                else if (evt.eventType == PartEventType.InventoryPartRemoved)
                {
                    SetGhostPartActive(state, evt.partPersistentId, true);
                    initialized.Add(evt.partPersistentId);
                }
            }
        }

        /// <summary>
        /// Initializes flag ghost visibility — all flags start hidden and appear when their event fires.
        /// </summary>
        internal static void InitializeFlagVisibility(IPlaybackTrajectory rec, GhostPlaybackState state)
        {
            if (rec == null || rec.FlagEvents == null || rec.FlagEvents.Count == 0) return;
            if (state == null) return;
            state.flagEventIndex = 0;
        }

        /// <summary>
        /// Spawns flag vessels when their UT is reached. Flags are permanent world objects —
        /// they are never destroyed by Parsek. Duplicate check prevents re-spawning on loop wrap.
        /// The FlagEvent in the recording tracks which flag was planted (name, position, texture, plaque).
        /// </summary>
        internal static void ApplyFlagEvents(GhostPlaybackState state, IPlaybackTrajectory rec, double currentUT)
        {
            if (rec == null) return;

            // Recorded pick-ups first: a flag taken down by the time of this walk is never
            // planted by it (its tombstone blocks the plant below), so a catch-up walk past
            // both does not plant a flag only to take it down again.
            ApplyFlagPickUps(rec, currentUT);

            if (rec.FlagEvents == null || rec.FlagEvents.Count == 0) return;

            if (state != null)
            {
                // Fast path: cursor-driven walk advances the per-state index monotonically.
                while (state.flagEventIndex < rec.FlagEvents.Count)
                {
                    var evt = rec.FlagEvents[state.flagEventIndex];
                    if (evt.ut > currentUT) break;

                    TrySpawnFlagVessel(evt);
                    state.flagEventIndex++;
                }
                return;
            }

            // Bug #414: state-less fallback. Reached when the caller cannot yet produce a
            // GhostPlaybackState (e.g. first-spawn visual build is throttled for a frame) but
            // we still want flag vessels — which are independent permanent world objects — to
            // be placed on schedule. `FlagExistsAtPosition` dedups, so a follow-up state-aware
            // walk starting from `flagEventIndex = 0` on the next frame is cheap and correct.
            SpawnFlagVesselsUpToUT(rec, currentUT);
        }

        /// <summary>
        /// Replays all flag events whose UT is in the past for callers that do not carry
        /// a per-recording flag cursor (for example deferred spawn flushes at warp end).
        /// Returns how many flag events were eligible at the requested UT, and how many
        /// actually spawned new flag vessels after dedup, were already present, or failed.
        /// </summary>
        internal static (int eligibleCount, int spawnedCount, int alreadyPresentCount, int failedCount,
            int removedByPlayerCount)
            SpawnFlagVesselsUpToUT(
            IPlaybackTrajectory rec, double currentUT)
        {
            if (rec == null)
                return (0, 0, 0, 0, 0);
            ApplyFlagPickUps(rec, currentUT);
            if (rec.FlagEvents == null || rec.FlagEvents.Count == 0)
                return (0, 0, 0, 0, 0);

            int eligibleCount = 0;
            int spawnedCount = 0;
            int alreadyPresentCount = 0;
            int failedCount = 0;
            int removedByPlayerCount = 0;
            for (int i = 0; i < rec.FlagEvents.Count; i++)
            {
                var evt = rec.FlagEvents[i];
                if (evt.ut > currentUT)
                    break;

                eligibleCount++;
                switch (TrySpawnFlagVessel(evt))
                {
                    case FlagReplayOutcome.Spawned:
                        spawnedCount++;
                        break;
                    case FlagReplayOutcome.AlreadyPresent:
                        alreadyPresentCount++;
                        break;
                    case FlagReplayOutcome.RemovedByPlayer:
                        removedByPlayerCount++;
                        break;
                    default:
                        failedCount++;
                        break;
                }
            }

            return (eligibleCount, spawnedCount, alreadyPresentCount, failedCount, removedByPlayerCount);
        }

        /// <summary>
        /// Pure: the UT a ghost's flag walk runs to when its timeline completes. Every flag plant
        /// and recorded pick-up the recording holds is history once its replay is over, so this is
        /// the later of the clock and the recording's last flag event. A pick-up made in the frame
        /// before an EVA kerbal boards lands after the last trajectory sample (the recording's end),
        /// and a walk bounded by the clock at the end frame would skip it for good.
        /// </summary>
        internal static double ResolveTimelineEndFlagWalkUT(IPlaybackTrajectory rec, double currentUT)
        {
            double ut = currentUT;
            if (rec == null)
                return ut;
            List<FlagEvent> flags = rec.FlagEvents;
            if (flags != null)
            {
                for (int i = 0; i < flags.Count; i++)
                {
                    if (flags[i].ut > ut)
                        ut = flags[i].ut;
                }
            }
            List<SegmentEvent> segs = (rec as Recording)?.SegmentEvents;
            if (segs != null)
            {
                for (int i = 0; i < segs.Count; i++)
                {
                    if (segs[i].type == SegmentEventType.FlagPickedUp && segs[i].ut > ut)
                        ut = segs[i].ut;
                }
            }
            return ut;
        }

        /// <summary>
        /// Runs the flag walk at a ghost's timeline end (see
        /// <see cref="ResolveTimelineEndFlagWalkUT"/>). Logs when the walk reaches past the clock.
        /// </summary>
        internal static void ApplyFlagEventsAtTimelineEnd(GhostPlaybackState state, IPlaybackTrajectory rec,
            double currentUT, string host)
        {
            if (rec == null)
                return;
            double walkUT = ResolveTimelineEndFlagWalkUT(rec, currentUT);
            if (walkUT > currentUT)
            {
                ParsekLog.Verbose(RemovedReplayFlags.Tag, string.Format(CultureInfo.InvariantCulture,
                    "Flag events at timeline end: rec={0} host={1} walkUT={2} currentUT={3} endUT={4}",
                    rec.RecordingId ?? "(none)", host ?? "?",
                    walkUT.ToString("R", CultureInfo.InvariantCulture),
                    currentUT.ToString("R", CultureInfo.InvariantCulture),
                    rec.EndUT.ToString("R", CultureInfo.InvariantCulture)));
            }
            ApplyFlagEvents(state, rec, walkUT);
        }

        internal static void SetBodyRadiusOverrideForTesting(Func<string, double> lookup)
        {
            bodyRadiusOverrideForTesting = lookup;
        }

        internal static void SetRemoveStandingFlagOverrideForTesting(Func<RemovedReplayFlags.Tombstone, int> remover)
        {
            removeStandingFlagOverrideForTesting = remover;
        }

        /// <summary>
        /// Replays the recorded flag pick-ups (<see cref="SegmentEventType.FlagPickedUp"/>) whose
        /// UT has been reached: each leaves a tombstone at its own UT (so no plant of that flag
        /// runs again) and takes down a flag standing there. A pick-up already in the tombstones
        /// (applied before, or the live pick-up itself) does nothing, so loop cycles and repeated
        /// walks are free of side effects. Returns how many were applied by this call.
        /// </summary>
        internal static int ApplyFlagPickUps(IPlaybackTrajectory rec, double currentUT)
        {
            List<SegmentEvent> segs = (rec as Recording)?.SegmentEvents;
            if (segs == null || segs.Count == 0)
                return 0;

            int applied = 0;
            for (int i = 0; i < segs.Count; i++)
            {
                SegmentEvent se = segs[i];
                if (se.type != SegmentEventType.FlagPickedUp || se.ut > currentUT)
                    continue;
                if (!FlagPickUp.TryParseDetails(se.details, out string body, out double lat, out double lon,
                        out double alt, out string name))
                {
                    ParsekLog.VerboseRateLimited(RemovedReplayFlags.Tag, "pickup-unreadable-" + i,
                        $"Flag pick-up not replayed: unreadable details '{se.details ?? "(null)"}'");
                    continue;
                }
                double radius = ResolveBodyRadius(body);
                if (double.IsNaN(radius) || radius <= 0.0)
                {
                    ParsekLog.VerboseRateLimited(RemovedReplayFlags.Tag, "pickup-no-body-" + body,
                        $"Flag pick-up not replayed: body '{body}' not found");
                    continue;
                }

                var t = new RemovedReplayFlags.Tombstone
                {
                    BodyName = body,
                    BodyRadius = radius,
                    Latitude = lat,
                    Longitude = lon,
                    Altitude = alt,
                    RemovedUT = se.ut,
                    FlagName = name,
                    Source = "replayed-pickup",
                };
                if (RemovedReplayFlags.IsAlreadyRecorded(t))
                    continue;

                RemovedReplayFlags.RecordRemoval(t);
                int removed;
                using (RemovedReplayFlags.BeginParsekRemoval())
                {
                    removed = removeStandingFlagOverrideForTesting != null
                        ? removeStandingFlagOverrideForTesting(t)
                        : RemoveStandingFlagsLive(t);
                }
                applied++;
                ParsekLog.Info(RemovedReplayFlags.Tag, string.Format(CultureInfo.InvariantCulture,
                    "Flag pick-up replayed: '{0}' on {1} ut={2} rec={3} took down {4} standing flag(s)",
                    name ?? "", body, se.ut.ToString("R", CultureInfo.InvariantCulture),
                    rec.RecordingId ?? "(none)", removed));
            }
            return applied;
        }

        private static double ResolveBodyRadius(string bodyName)
        {
            if (bodyRadiusOverrideForTesting != null)
                return bodyRadiusOverrideForTesting(bodyName);
            return ResolveBodyRadiusLive(bodyName);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static double ResolveBodyRadiusLive(string bodyName)
        {
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            return body != null ? body.Radius : double.NaN;
        }

        /// <summary>
        /// Takes down every real flag vessel standing where <paramref name="t"/> says (same body,
        /// within <see cref="RemovedReplayFlags.MatchToleranceMeters"/>), loaded or not, and drops
        /// its flight-state entry so the next save does not carry it. Ghost map vessels are skipped.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int RemoveStandingFlagsLive(RemovedReplayFlags.Tombstone t)
        {
            if (FlightGlobals.Vessels == null)
                return 0;
            var targets = new List<Vessel>();
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel v = FlightGlobals.Vessels[i];
                if (v == null || v.vesselType != VesselType.Flag) continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                if (v.mainBody == null || v.mainBody.name != t.BodyName) continue;
                double d = RemovedReplayFlags.SurfaceDistanceMeters(
                    t.Latitude, t.Longitude, t.Altitude, v.latitude, v.longitude, v.altitude, t.BodyRadius);
                if (d <= RemovedReplayFlags.MatchToleranceMeters)
                    targets.Add(v);
            }

            int removed = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                Vessel v = targets[i];
                uint pid = v.persistentId;
                Guid id = v.id;
                ProtoVessel proto = v.protoVessel;
                try
                {
                    v.Die();
                    var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
                    LiveClaimedVesselRemovalTarget.RemoveFlightStateEntries(protos, proto, pid, id);
                    removed++;
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(RemovedReplayFlags.Tag,
                        $"Flag pick-up replay: taking down '{v.vesselName}' pid={pid} threw {ex.GetType().Name}: {ex.Message}");
                }
            }
            return removed;
        }

        private enum FlagReplayOutcome
        {
            Spawned,
            AlreadyPresent,
            /// <summary>The player removed this flag after it was planted (<see cref="RemovedReplayFlags"/>).</summary>
            RemovedByPlayer,
            Failed
        }

        private static FlagReplayOutcome TrySpawnFlagVessel(FlagEvent evt)
        {
            // A flag is planted once, at its moment; once the player removed it, no later walk
            // over the event (a loop cycle, an overlap ghost, a fresh playback state after a
            // scene change, a deferred flush) plants it again. Checked before the world scan:
            // it is pure and the common blocked case then costs no vessel walk.
            int blocking = RemovedReplayFlags.FindBlocking(evt);
            if (blocking >= 0)
            {
                ParsekLog.VerboseRateLimited(RemovedReplayFlags.Tag,
                    "replant-skipped-" + (evt.bodyName ?? "") + "-" + (evt.flagSiteName ?? ""),
                    string.Format(CultureInfo.InvariantCulture,
                        "Flag replay skipped: '{0}' on {1} (event ut={2}) was removed by the player at ut={3}",
                        evt.flagSiteName ?? "", evt.bodyName ?? "?",
                        evt.ut.ToString("R", CultureInfo.InvariantCulture),
                        RemovedReplayFlags.Tombstones[blocking].RemovedUT.ToString("R", CultureInfo.InvariantCulture)));
                return FlagReplayOutcome.RemovedByPlayer;
            }

            if (FlagExistsAtPosition(evt))
                return FlagReplayOutcome.AlreadyPresent;

            if (spawnFlagOverrideForTesting != null)
                return spawnFlagOverrideForTesting(evt)
                    ? FlagReplayOutcome.Spawned
                    : FlagReplayOutcome.Failed;

            return GhostVisualBuilder.SpawnFlagVessel(evt) != null
                ? FlagReplayOutcome.Spawned
                : FlagReplayOutcome.Failed;
        }

        /// <summary>
        /// Checks if a flag vessel already exists within 1m of the event position (prevents duplicates on loop).
        /// Uses world-space 3D distance rather than lat/lon to handle high-latitude and small-body cases correctly.
        /// </summary>
        private static bool FlagExistsAtPosition(FlagEvent evt)
        {
            if (flagExistsOverrideForTesting != null)
                return flagExistsOverrideForTesting(evt);

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == evt.bodyName);
            if (body == null || FlightGlobals.Vessels == null) return false;

            Vector3d eventPos = body.GetWorldSurfacePosition(evt.latitude, evt.longitude, evt.altitude);

            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel v = FlightGlobals.Vessels[i];
                if (v == null || v.vesselType != VesselType.Flag) continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                if (v.mainBody != body) continue;

                Vector3d flagPos = body.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude);
                double dx = flagPos.x - eventPos.x;
                double dy = flagPos.y - eventPos.y;
                double dz = flagPos.z - eventPos.z;
                if (dx * dx + dy * dy + dz * dz < 1.0) // within 1m
                    return true;
            }
            return false;
        }

        internal static void HidePartSubtree(GameObject ghost, uint rootPid, Dictionary<uint, List<uint>> tree)
        {
            int hidden = 0;
            int notFound = 0;
            var stack = new Stack<uint>();
            stack.Push(rootPid);
            while (stack.Count > 0)
            {
                uint pid = stack.Pop();
                var t = GhostVisualBuilder.FindGhostPartTransform(ghost, pid);
                if (t != null)
                {
                    t.gameObject.SetActive(false);
                    hidden++;
                }
                else
                    notFound++;
                List<uint> children;
                if (tree.TryGetValue(pid, out children))
                    for (int c = 0; c < children.Count; c++)
                        stack.Push(children[c]);
            }
        }

        /// <summary>
        /// Recalculate cameraPivot position after a visibility change (decouple/destroy).
        /// Sets localPosition to midpoint of remaining active parts' bounding extent.
        /// </summary>
        internal static void RecalculateCameraPivot(GhostPlaybackState state)
        {
            if (state.ghost == null || state.cameraPivot == null) return;
            var ghostTransform = state.ghost.transform;
            var partContainer = GhostVisualBuilder.GetGhostPartContainer(ghostTransform);
            if (partContainer == null)
            {
                state.cameraPivot.localPosition = Vector3.zero;
                return;
            }
            int count = 0;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            for (int i = 0; i < partContainer.childCount; i++)
            {
                var child = partContainer.GetChild(i);
                if (!child.gameObject.activeSelf || !child.name.StartsWith("ghost_part_"))
                    continue;
                var pos = ghostTransform.InverseTransformPoint(child.position);
                if (count == 0) { min = max = pos; }
                else { min = Vector3.Min(min, pos); max = Vector3.Max(max, pos); }
                count++;
            }
            state.cameraPivot.localPosition = count > 0 ? (min + max) * 0.5f : Vector3.zero;
            ParsekLog.VerboseRateLimited("CameraFollow", $"pivot-{state.ghost.name}",
                $"Camera pivot recalculated: localPos=({state.cameraPivot.localPosition.x:F2},{state.cameraPivot.localPosition.y:F2},{state.cameraPivot.localPosition.z:F2})" +
                $" activeParts={count}", 1.0);
        }

        #endregion

        #region Canopy Management

        /// <summary>
        /// Applies a ParachuteDeployed event: sets the real canopy to deployed pose if available,
        /// otherwise creates a fake canopy sphere as fallback. Hides the cap in both cases.
        /// </summary>
        /// <summary>
        /// P8 step 1: this family has a FALLBACK rather than a skip - a pack whose ghost
        /// carries no canopy transform gets a fabricated sphere canopy - so the only
        /// non-applied outcome is "neither the real canopy nor the fake one resolved".
        /// </summary>
        private static GhostPartEventOutcome ApplyParachuteDeployedEvent(
            GhostPlaybackState state, GameObject ghost, uint partPersistentId)
        {
            bool usedRealCanopy = false;

            if (state.parachuteInfos != null)
            {
                ParachuteGhostInfo info;
                if (state.parachuteInfos.TryGetValue(partPersistentId, out info) && info.canopyTransform != null)
                {
                    info.canopyTransform.localScale = info.deployedCanopyScale;
                    info.canopyTransform.localPosition = info.deployedCanopyPos;
                    info.canopyTransform.localRotation = info.deployedCanopyRot;
                    if (info.capTransform != null)
                        info.capTransform.gameObject.SetActive(false);
                    usedRealCanopy = true;
                }
            }

            if (!usedRealCanopy)
            {
                var canopy = GhostVisualBuilder.CreateFakeCanopy(ghost, partPersistentId);
                if (canopy != null)
                {
                    TrackFakeCanopy(state, partPersistentId, canopy);
                    return GhostPartEventOutcome.Applied;
                }
                return GhostPartEventOutcome.NoResolvedVisual;
            }

            return GhostPartEventOutcome.Applied;
        }

        internal static void TrackFakeCanopy(GhostPlaybackState state, uint partPid, GameObject canopy)
        {
            if (state.fakeCanopies == null)
                state.fakeCanopies = new Dictionary<uint, GameObject>();
            // Destroy previous canopy for this part if one exists (prevents leak)
            GameObject existing;
            if (state.fakeCanopies.TryGetValue(partPid, out existing) && existing != null)
                DestroyCanopyAndMaterial(existing);
            state.fakeCanopies[partPid] = canopy;
        }

        internal static void DestroyFakeCanopy(GhostPlaybackState state, uint partPid)
        {
            if (state.fakeCanopies == null) return;
            GameObject canopy;
            if (state.fakeCanopies.TryGetValue(partPid, out canopy) && canopy != null)
                DestroyCanopyAndMaterial(canopy);
            state.fakeCanopies.Remove(partPid);
        }

        internal static void DestroyAllFakeCanopies(GhostPlaybackState state)
        {
            if (state.fakeCanopies == null) return;
            foreach (var kv in state.fakeCanopies)
                if (kv.Value != null) DestroyCanopyAndMaterial(kv.Value);
            state.fakeCanopies = null;
        }

        internal static void DestroyCanopyAndMaterial(GameObject canopy)
        {
            var renderer = canopy.GetComponent<Renderer>();
            if (renderer != null && renderer.material != null)
                UnityEngine.Object.Destroy(renderer.material);
            UnityEngine.Object.Destroy(canopy);
        }

        #endregion
    }
}
