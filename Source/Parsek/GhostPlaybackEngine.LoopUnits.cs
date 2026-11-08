using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // Mission loop units: unit membership, unit-member playback, chain seam markers and the boundary-overlap secondary.
    internal partial class GhostPlaybackEngine
    {
        /// <summary>
        /// Pushes the host-detected chain-loop unit descriptors for the upcoming frame. Mirrors
        /// the <see cref="ResolveChainNextIndex"/> / <see cref="IsGhostHeld"/> host-injection
        /// pattern, but as a per-frame snapshot (not a recompute-on-demand delegate) so detection
        /// runs exactly once per frame and both schedulers (flight engine + tracking station)
        /// consume an identical frozen view (design D1). Called by the host once per frame, right
        /// before <see cref="UpdatePlayback"/>, alongside building the trajectory list. A null
        /// argument is normalized to <see cref="GhostPlaybackLogic.LoopUnitSet.Empty"/> so the
        /// feature stays dormant when the host has no units.
        /// </summary>
        internal void SetLoopUnits(GhostPlaybackLogic.LoopUnitSet units)
        {
            GhostPlaybackLogic.LoopUnitSet previous = currentLoopUnits;
            currentLoopUnits = units ?? GhostPlaybackLogic.LoopUnitSet.Empty;

            // A DIFFERENT set instance means the host rebuilt the units (a partner-journey link or
            // interval toggle, a commit, the loop turned off). Marker lists, member indices and even
            // the committed-index space they are keyed in can all have moved, so every seam cursor
            // and dedup key from the previous set is meaningless - carrying them across would
            // silently suppress whatever marker now sits at those indices for the rest of the loop
            // cycle. The host re-pushes the SAME reference every frame between rebuilds, so this is
            // one reference compare per frame and a clear only on an actual rebuild.
            if (!ReferenceEquals(previous, currentLoopUnits))
            {
                if (seamMarkerRuntime.TrackedMemberCount > 0)
                    ParsekLog.Verbose("SeamMarker",
                        "unit set replaced - reset seam cursors for "
                        + seamMarkerRuntime.TrackedMemberCount.ToString(CultureInfo.InvariantCulture)
                        + " member(s)");
                seamMarkerRuntime.Reset();
            }
        }

        /// <summary>
        /// True when the committed recording index is currently a member of some Mission loop
        /// unit. The watch controller uses this to detect when a loop-cycle-watched recording has
        /// been dropped from its mission (interval trim toggle, or the loop turned off) so it can
        /// exit watch instead of clinging to a dying ghost.
        /// </summary>
        internal bool IsLoopUnitMember(int recordingIndex)
        {
            return currentLoopUnits.IsMember(recordingIndex);
        }

        /// <summary>
        /// Resolves the through-line ROOT of the Mission loop unit containing
        /// <paramref name="memberIndex"/>: the unit's earliest member (<c>OwnerIndex</c>, the
        /// mission's first-rendered stage at the trimmed start). The watch controller uses this to
        /// RESTART the camera at the start of a fresh mission instance after the watched
        /// through-line's terminal explosion hold, instead of clinging to the just-exploded member's
        /// own primary cycle (the upper stage mid-flight, which can also be relative-anchor-retired
        /// at an invalid position). Returns false (rootIndex = memberIndex) when the index is not a
        /// unit member, so single-recording overlap watch keeps its existing primary retarget.
        /// </summary>
        internal bool TryGetUnitThroughLineRoot(int memberIndex, out int rootIndex)
        {
            if (currentLoopUnits.TryGetUnitForMember(memberIndex, out GhostPlaybackLogic.LoopUnit unit))
            {
                rootIndex = unit.OwnerIndex;
                return true;
            }
            rootIndex = memberIndex;
            return false;
        }

        /// <summary>
        /// Resolves the Mission span-clock loopUT for a committed recording index that is a
        /// member of a Mission loop unit. Watch entry must synchronize a watched member's ghost
        /// visuals at the SAME span loopUT the per-frame scheduler renders that member at in
        /// <see cref="UpdateUnitMemberPlayback"/>, NOT the raw Planetarium UT: a unit member does
        /// not carry its own per-recording LoopPlayback flag, so the watch path's
        /// ShouldLoopPlaybackForWatch fallback would otherwise feed the raw UT into
        /// ApplyPartEvents (monotonic), replaying every staging / decouple event past the member's
        /// window end and leaving only the never-jettisoned parts visible.
        ///
        /// Returns true and the shared span loopUT when <paramref name="recordingIndex"/> is a
        /// unit member; otherwise returns false with <paramref name="loopUT"/> set to
        /// <paramref name="currentUT"/> (no behavior change for non-members / non-looping watch).
        /// The span clock failing to resolve (before span start / degenerate span) also returns
        /// false with the raw UT, so the caller keeps its existing fallback path.
        /// </summary>
        internal bool TryResolveUnitMemberPlaybackUT(
            int recordingIndex, double currentUT, double memberStartUT, double memberEndUT, out double loopUT)
        {
            loopUT = currentUT;
            if (!currentLoopUnits.TryGetUnitForMember(recordingIndex, out GhostPlaybackLogic.LoopUnit unit))
                return false;

            // Interval-level start/end trim: track this member's trimmed render window (falls back
            // to the passed recording bounds when untrimmed), so the watch camera follows the same
            // trimmed playback the engine renders.
            memberStartUT = unit.MemberStartUT(recordingIndex, memberStartUT);
            memberEndUT = unit.MemberEndUT(recordingIndex, memberEndUT);

            double span = unit.SpanEndUT - unit.SpanStartUT;

            // Self-overlap: the mission relaunches every OverlapCadenceSeconds, so watch must pin the
            // NEWEST instance of this member (the primary ghost UpdateOverlapPlayback keeps pointed at
            // lastCycle). Resolve the member's playback UT in its newest overlap cycle via the SAME
            // per-recording overlap math the engine renders the member with: scheduleStartUT =
            // PhaseAnchorUT + (memberStart - spanStart), playbackStartUT = memberStart, duration =
            // memberEnd - memberStart, intervalSeconds = the mission overlap cadence. See
            // UpdateUnitMemberPlayback for the matching render-side reduction.
            if (span > 0 && unit.OverlapCadenceSeconds < span)
            {
                double memberScheduleStartUT = unit.PhaseAnchorUT + (memberStartUT - unit.SpanStartUT);
                double memberDuration = memberEndUT - memberStartUT;
                if (memberDuration > 0
                    && GhostPlaybackLogic.TryComputeNewestOverlapPlaybackUT(
                        currentUT,
                        unit.OverlapCadenceSeconds,
                        memberDuration,
                        memberStartUT,
                        memberScheduleStartUT,
                        out double overlapMemberUT,
                        out _))
                {
                    loopUT = overlapMemberUT;
                    return true;
                }
                return false;
            }

            // Forward the per-loop launch-alignment inputs so the watch clock is byte-identical to the
            // engine render clock (UpdateUnitMemberPlayback -> DecideUnitMemberRender at the render call
            // below). WITHOUT these, a launch-alignment-engaged re-aim member would resolve a different loopUT
            // than the engine renders: the borrow-at-launch / repay-at-SOI-exit model launches delta_N earlier
            // and inserts a delta_N coast hold at the SOI exit, so the resolved in-SOI loopUT and the SOI-exit
            // dwell differ from the plain clock by up to T_sid (~6 h of recorded trajectory). There is NO
            // pre-launch absence under borrow-repay (the launch is earlier, the repay is an in-window coast),
            // so the watch clock always resolves an in-span UT.
            if (!GhostPlaybackLogic.TryComputeSpanLoopUT(
                    currentUT,
                    unit.PhaseAnchorUT,
                    unit.SpanStartUT,
                    unit.SpanEndUT,
                    unit.CadenceSeconds,
                    out double spanLoopUT,
                    out _,
                    out _,
                    unit.RelaunchSchedule,
                    unit.LoiterCuts,
                    unit.ArrivalHoldSeconds,
                    unit.ArrivalHoldAtUT,
                    unit.ArrivalAlignPeriodSeconds,
                    unit.LaunchBodyRotationPeriodSeconds,
                    unit.LaunchHoldEngaged,
                    unit.RecordedSoiExitUT,
                    unit.ArrivalJointSecondaryPeriodSeconds,
                    unit.ArrivalJointSecondaryToleranceSeconds,
                    unit.ArrivalJointMaxWholeHoldPeriods))
            {
                return false;
            }

            loopUT = spanLoopUT;
            return true;
        }

        internal static bool HasLoopCycleChanged(GhostPlaybackState state, long cycleIndex)
        {
            return state == null || state.loopCycleIndex != cycleIndex;
        }

        /// <summary>
        /// True when a mission-loop member's slot holds a ghost that no loop path created: every
        /// unit / overlap spawn stamps its cycle (&gt;= 0) at creation, so a state still at the
        /// default -1 is the member's ORDINARY first-run ghost, left over from the frames before the
        /// unit's anchor (<see cref="GhostPlaybackLogic.LoopUnitSet.LiveAt"/>). Pure.
        /// </summary>
        internal static bool ShouldRetireOrdinaryStateAtUnitHandover(GhostPlaybackState state)
        {
            return state != null && state.loopCycleIndex < 0;
        }

        /// <summary>
        /// Loop unit follower dispatch. Routes a unit member onto the unit's SHARED mission clock
        /// instead of its own per-recording loop clock. Each member runs this independently per
        /// frame (no shared mutable cache, design D3): it computes the shared spanLoopUT and renders
        /// THIS member at that loopUT IFF the clock is inside THIS member's own [StartUT, EndUT]
        /// window; otherwise it hides / destroys its ghost. There is NO cross-member selection -
        /// multiple members render concurrently (debris alongside their parent), exactly like a
        /// rewind. During the inter-cycle tail-wait (cadence greater than span) every member hides.
        ///
        /// Re-checks anchor gating (edge 17) and warp suppression (edge 12) per-member so the unit
        /// only supplies the clock; the member still resolves its own anchor / surface at loopUT.
        /// On a unit cycle change the ghost is rebuilt for a clean per-cycle visual.
        /// <paramref name="state"/> / <paramref name="ghostActive"/> are by ref because the
        /// cycle-change path nulls state and the inner RenderInRangeGhost reassigns both.
        /// </summary>
        private void UpdateUnitMemberPlayback(
            int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f, FrameContext ctx,
            IReadOnlyList<IPlaybackTrajectory> trajectories, GhostPlaybackLogic.LoopUnit unit,
            bool suppressGhosts, bool suppressVisualFx,
            bool hasPointData, bool hasInterpolatedPoints,
            bool hasSurfaceData, bool hasOrbitData, ref GhostPlaybackState state, ref bool ghostActive)
        {
            string unitKey = "unit-" + unit.OwnerIndex.ToString(CultureInfo.InvariantCulture);

            // (1) Per-member anchor gating (edge 17): the unit only supplies the clock; each member
            // still short-circuits if its own anchor is configured but unloaded.
            if (traj.LoopAnchorVesselId != 0 && !loadedAnchorVessels.Contains(traj.LoopAnchorVesselId))
            {
                GhostRenderTrace.EmitGuardSkip(
                    traj, i, ctx.currentUT,
                    "unit-member-anchor-unloaded pid=" + traj.LoopAnchorVesselId.ToString(CultureInfo.InvariantCulture));
                if (ghostActive)
                {
                    DestroyGhost(i, traj, f, reason: $"unit member anchor {traj.LoopAnchorVesselId} unloaded");
                    DestroyAllOverlapGhosts(i);
                }
                CountFrameSkip(GhostPlaybackSkipReason.AnchorMissing);
                return;
            }

            // (1b) MISSION SELF-OVERLAP: when the mission's overlap cadence is SHORTER than the span,
            // the whole mission relaunches every OverlapCadenceSeconds, so several staggered instances
            // play concurrently. For THIS member that is EXACTLY a per-recording overlap loop:
            //   scheduleStartUT = PhaseAnchorUT + (memberStart - spanStart)  (when this instance launches)
            //   playbackStartUT = memberStart                                (where in the member to start)
            //   duration        = memberEnd - memberStart                    (the member's own length)
            //   intervalSeconds = OverlapCadenceSeconds                      (the mission launch cadence)
            // Route the member through the existing UpdateOverlapPlayback so overlapGhosts[i] holds the
            // staggered instances (the primary = newest instance, the camera-followed one). Do NOT clear
            // overlapGhosts[i] in this branch. The span-clock window/inter-cycle-tail decision below is
            // NOT used here (overlap has its own per-instance scheduling); the member-specific warp gate
            // is handled inside UpdateOverlapPlayback. When OverlapCadenceSeconds >= span we fall through
            // to the EXACT single-instance span-clock behavior (including the overlapGhosts[i] clear).
            double unitSpan = unit.SpanEndUT - unit.SpanStartUT;

            // Interval-level start/end trim: this member's effective render window (its own range,
            // unless the mission trimmed it - e.g. a pod shown only after the decouple). Both the
            // overlap and the span-clock branches use these instead of the raw recording bounds, so
            // an untrimmed mission keeps its full range (no behavior change).
            double memberStartUT = unit.MemberStartUT(i, traj.StartUT);
            double memberEndUT = unit.MemberEndUT(i, traj.EndUT);

            if (GhostPlaybackLogic.UnitMemberOverlaps(unit))
            {
                UpdateUnitMemberSelfOverlap(
                    i, traj, f, ctx, trajectories, unit, unitKey, unitSpan, memberStartUT, memberEndUT,
                    suppressGhosts, suppressVisualFx, ref state, ref ghostActive);
                return;
            }

            // (2) Shared mission clock + THIS member's own-window check, via the pure (xUnit-tested)
            // decision helper. No cross-member selection: the decision keys ONLY on whether the
            // shared spanLoopUT is in THIS member's [StartUT, EndUT].
            var decision = GhostPlaybackLogic.DecideUnitMemberRender(
                ctx.currentUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                memberStartUT, memberEndUT, out double spanLoopUT, out long unitCycle,
                out bool isInInterCycleTail, unit.RelaunchSchedule, unit.LoiterCuts,
                unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT, unit.ArrivalAlignPeriodSeconds,
                unit.LaunchBodyRotationPeriodSeconds, unit.LaunchHoldEngaged, unit.RecordedSoiExitUT,
                unit.ArrivalJointSecondaryPeriodSeconds, unit.ArrivalJointSecondaryToleranceSeconds,
                unit.ArrivalJointMaxWholeHoldPeriods);

            // Cycle-wrap / camera-handoff diagnostics + watch retarget: the first member of the unit
            // to run this frame observes the unit-wide transition and acts once (rate-limited per
            // unit). It computes the unit's live camera member (the in-window member with the highest
            // StartUT) by scanning all members against spanLoopUT.
            LogUnitTransitionIfChanged(
                unit, unitKey, trajectories, spanLoopUT, unitCycle, ctx.currentUT,
                isInInterCycleTail, ctx.protectedIndex, suppressWatchRetarget: false);

            if (decision == GhostPlaybackLogic.UnitMemberRenderDecision.SpanClockUnresolved)
            {
                GhostRenderTrace.EmitGuardSkip(traj, i, ctx.currentUT, "unit-span-clock-unresolved");
                if (ghostActive)
                {
                    DestroyGhost(i, traj, f, reason: "unit span clock unresolved");
                    DestroyAllOverlapGhosts(i);
                }
                CountFrameSkip(GhostPlaybackSkipReason.MissionLoopUnitInactive);
                return;
            }

            // (2b) LOOP SEAM MARKERS (design-dock-event-graph.md 7.3). Placed HERE, above every
            // hide/destroy branch, because the check is CLOCK-driven, not render-driven: R3 explains
            // a member's line ENDING, and the watched member is hidden-not-destroyed at exactly that
            // moment (edge case 17), so a check below the (3) block would miss the very case the
            // marker exists for. The span-clock-unresolved early return is above (no clock, no
            // marker) and the inter-cycle tail is excluded explicitly (edge case 19: nothing renders
            // during the wait, so nothing needs explaining). Uses the RAW span clock deliberately -
            // the descent-trigger / loiter-clamp overrides below re-anchor the render head, not
            // recorded time, and a seam is a recorded-time fact.
            if (!isInInterCycleTail)
                TryEmitSeamMarker(i, traj, f, unit, spanLoopUT, unitCycle);

            // DESCENT TRIGGER (re-aim looped arrival, docs/dev/plans/reaim-descent-trigger.md): the FLIGHT
            // engine must render a descent-set member EXACTLY like the map/TS resolver
            // (ResolveTrackingStationSampleUT) - detach it from the raw loop clock and re-anchor the whole
            // descent set to the first rotation-aligned moment after the icon reaches the parking-orbit deorbit
            // point. Without this the engine kept driving the member on the raw span clock (decision +
            // spanLoopUT from DecideUnitMemberRender), so in flight the descent member was HIDDEN the whole
            // time (its raw loop slot lands at the WRONG destination rotation = outside its own window) and the
            // ghost/icon never appeared. Re-resolve via the shared descent head and OVERRIDE the downstream
            // span clock: when the trigger says render, force decision=Render and drive the ghost at the
            // re-anchored descentHead; otherwise force a hide so the existing (3) teardown runs (the descent
            // member NEVER rides the raw loop clock). Byte-identical for every non-descent member /
            // non-re-aim unit (HasDescentTrigger false -> descentMember false -> this block is skipped).
            bool descentMember = unit.HasDescentTrigger && unit.IsDescentMember(i);
            if (descentMember)
            {
                ApplyDescentMemberRenderOverride(
                    i, unit, ctx, unitCycle, memberStartUT, memberEndUT, ref spanLoopUT, ref decision);
            }

            // DESCENT HANDOFF hide (the engine side of the ResolveTrackingStationSampleUT transfer-member
            // hide, via the SAME pure decision - ShouldHideNonDescentMemberForDescentHandoff - so flight and
            // map/TS can never disagree): once the unit-wide descent phase is Descent, the re-anchored
            // descent member's ghost is playing the approach clip, so a CURRENTLY-RENDERING non-descent
            // member (the transfer/loiter member circling the shifted parking conic) must HIDE its flight
            // 3D ghost - otherwise two vessels coexist at the handoff (the parking ghost + the descent
            // ghost), the engine twin of the TS double-icon bug (2026-06-20 13:34 log). Gated identically
            // to the TS site: HasDescentTrigger + decision==Render only, byte-identical everywhere else;
            // Inert / Loiter / Done phases keep rendering (the loiter ghost is correct until the trigger
            // fires). Checked BEFORE the loiter-gap clamp, matching the resolver's order (the handoff hide
            // wins over the loiter wrap). The forced HiddenOutsideWindow routes through the existing (3)
            // teardown, exactly like a descent member's own hide (watched-owner and boundary-secondary
            // handling included).
            bool hiddenForDescentHandoff = false;
            if (!descentMember
                && decision == GhostPlaybackLogic.UnitMemberRenderDecision.Render
                && unit.HasDescentTrigger)
            {
                ApplyDescentHandoffHide(
                    i, unit, ctx, unitCycle, spanLoopUT, ref decision, ref hiddenForDescentHandoff);
            }

            // Transfer-member loiter-gap clamp (the rogue-descent fix; the engine side of the
            // ResolveTrackingStationSampleUT clamp): the NON-descent destination transfer member's raw span clock
            // sweeps PAST the parking conic into the recorded deorbit -> descent arc while the LIVE descent trigger
            // is still ~|captureShift| away, so without this the engine drives the 3D ghost (and its projected map
            // mesh / non-proto marker) DOWN the recorded descent at the wrong time - the user's "rogue descending
            // trajectory". Hold the ghost on the parking conic by clamping the drive clock to the parking-conic end
            // through the loiter (matching the map-presence segment-lookup clamp + the orbit-line hold + the
            // marker/line/polyline resolver clamp); this also keeps the loop clock short of EndOfData, so the mesh
            // stays positioned and the map marker rides source=mesh on the parking conic instead of falling back to
            // the descending trajectory head. No-op (byte-identical) for descent members / ride-alongs / non-re-aim
            // units (IsDescentTransferMemberInLoiterGap false) and gated on decision==Render (a hidden member's
            // span clock is unused downstream). (Step 2: ClampTransferMemberHeadToLoiterGap WRAPS the head into the
            // last recorded parking period [P - LoiterPeriodSeconds, P) so the ghost CIRCLES the closed parking
            // conic through the wait instead of freezing at the deorbit point - continuous at engage and wrap-around.)
            if (!descentMember
                && decision == GhostPlaybackLogic.UnitMemberRenderDecision.Render)
            {
                bool wrapEngaged = GhostPlaybackLogic.IsDescentTransferMemberInLoiterGap(unit, i, spanLoopUT);
                spanLoopUT = GhostPlaybackLogic.ClampTransferMemberHeadToLoiterGap(unit, i, spanLoopUT);
                if (wrapEngaged)
                {
                    var lwdic = System.Globalization.CultureInfo.InvariantCulture;
                    ParsekLog.VerboseRateLimited("ReaimDescent", "loiter-wrap-" + i.ToString(lwdic),
                        "loiter-circle member=" + i.ToString(lwdic) + " head wrapped to " +
                        spanLoopUT.ToString("R", lwdic) + " within Tpark=" +
                        unit.LoiterPeriodSeconds.ToString("R", lwdic) +
                        " (parking conic circling, parkingConicEnd=" +
                        unit.ParkingConicEndUT.ToString("R", lwdic) + ")", 5.0);
                }
            }

            // BOUNDARY-OVERLAP secondary decision (docs/dev/plan-launch-boundary-overlap.md 3.1/3.2),
            // HOISTED above the (3) hide/destroy block (review H2). Resolve whether this member carries a
            // live boundary-overlap secondary (the early-launching NEXT instance N+1) BEFORE the primary's
            // out-of-window early returns below, because in a CHAIN launch member the primary (the continuing
            // instance N) is months downstream near the destination - OUTSIDE this member's window - while the
            // secondary is mid-ascent IN this member's window. If the decision stayed below the (3) block, the
            // primary's HiddenOutsideWindow / HiddenInterCycleTail early return tore down overlapGhosts[i] (the
            // secondary's 3D mesh) before it was ever dispatched. NoSecondary (every aligned loop /
            // non-launch-hold member / single-recording launch whose primary IS in-window) leaves the (3)
            // block byte-identical.
            var boundarySecondaryDecision = DecideUnitMemberBoundarySecondary(
                unit, ctx, memberStartUT, memberEndUT, descentMember,
                out double boundarySecondaryLoopUT, out long boundarySecondaryCycle);
            bool boundarySecondaryLive =
                boundarySecondaryDecision == GhostPlaybackLogic.BoundaryOverlapSecondaryDecision.Render;

            // (3) Not in THIS member's window (the shared clock is outside [StartUT, EndUT]), or in
            // the inter-cycle tail-wait (cadence greater than span): hide / destroy THIS member's ghost.
            if (decision != GhostPlaybackLogic.UnitMemberRenderDecision.Render)
            {
                HideUnitMemberOutsideWindow(
                    i, traj, f, ctx, unit, unitKey, decision, spanLoopUT, memberStartUT, memberEndUT,
                    hiddenForDescentHandoff, boundarySecondaryLive, boundarySecondaryLoopUT, boundarySecondaryCycle,
                    suppressGhosts, suppressVisualFx, state, ghostActive);
                return;
            }

            // (4a) Payload-activation gate (Fix 1): the member window uses raw StartUT/EndUT, but
            // RenderInRangeGhost positions/interpolates against the playable payload, which can begin
            // AFTER StartUT when ExplicitStartUT widened the semantic boundary below the first payload
            // sample. Mirror the loop-synced-debris gate: if the shared clock is in THIS member's
            // window but below its own payload start, hide it rather than render a stale pre-payload
            // pose. Contiguous members (StartUT == activationStartUT) are unaffected.
            if (TryHideUnitMemberBeforeActivation(i, traj, f, ctx, unit, unitKey, spanLoopUT, state, ghostActive))
                return;

            // (4b) This member renders. Warp suppression on the live member (edge 12): the unit
            // uses the same per-frame render path, so the same warp gate applies at spanLoopUT.
            if (suppressGhosts && GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(
                    ctx.warpRate, traj, spanLoopUT))
            {
                GhostRenderTrace.EmitGuardSkip(traj, i, ctx.currentUT, "unit-member-warp-hidden");
                if (ghostActive && state != null && state.ghost != null && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ResetGhostAppearanceTracking(state);
                }
                DestroyAllOverlapGhosts(i);
                CountFrameSkip(GhostPlaybackSkipReason.WarpHidden);
                return;
            }

            // (boundarySecondaryDecision / boundarySecondaryLive / boundarySecondaryLoopUT /
            // boundarySecondaryCycle are resolved ABOVE, hoisted over the (3) hide/destroy block - review H2.)

            // The shared clock never produces self-overlap cycles (one cycle live), so a unit member should never
            // carry SELF-overlap ghosts. Clear any stale ones left over from a prior standalone overlap-loop life
            // (e.g. the member was just toggled into the unit mid-flight). Mirror the standalone path (~1031):
            // Remove(i) after destroying so the (now empty) dict entry does not keep ContainsKey(i) true and rerun
            // this every frame. GATED on the boundary-overlap secondary (plan 3.2): when a live secondary occupies
            // overlapGhosts[i] this frame, do NOT tear it down (it would thrash every frame across the borrow
            // window); UpdateBoundaryOverlapSecondary below owns that storage. The secondary path NEVER calls
            // UpdateOverlapPlayback / the expiry block, so it never confuses the self-overlap machinery.
            if (!boundarySecondaryLive && overlapGhosts.ContainsKey(i))
            {
                DestroyAllOverlapGhosts(i);
                overlapGhosts.Remove(i);
            }

            // (5) Cycle change: rebuild the ghost for a clean per-cycle visual (same shape the
            // loop-synced-debris path uses) and clear the completed-event dedup so it can replay.
            if (state != null && state.loopCycleIndex != unitCycle)
            {
                DestroyGhost(i, traj, f, reason: "chain-loop unit cycle change");
                ghostActive = false;
                state = null;
                completedEventFired.Remove(i);
            }

            // (5c) M4b phasing knob: the per-launch body-fixed time shift this member is replaying
            // under (loiter cut < 0 / extension > 0; exactly 0 for knob-less schedules and uniform
            // units). The positioner derotates Absolute body-fixed point playback by it so vacuum
            // burn arcs land at their recorded INERTIAL positions instead of riding the planet's
            // extra rotation (the 2026-06-11 playtest's 46-degree teleports near the station).
            // A descent-set member's head is ALREADY rotation-aligned by the descent trigger (spanLoopUT was
            // overridden to descentHead above); the M4b phasing-knob body-fixed shift must NOT apply on top, or
            // the descent would be double-shifted off the recorded site. Gate it off for descent members (the
            // resolver path applies no such shift either, so this keeps engine/map parity).
            double bodyFixedShift = 0.0;
            if (!descentMember && unit.RelaunchSchedule != null && unit.RelaunchSchedule.HasPhasingKnob
                && unit.RelaunchSchedule.TryResolveActiveLaunch(
                    ctx.currentUT, out double knobLaunchUT, out _))
            {
                bodyFixedShift = GhostPlaybackLogic.ComputeScheduledBodyFixedShiftSeconds(
                    ctx.currentUT, knobLaunchUT, spanLoopUT, unit.SpanStartUT);
            }
            if (state != null)
                state.bodyFixedShiftSeconds = bodyFixedShift;

            // (6) Render THIS member at the shared loopUT via the normal in-range path. Override the
            // frame UT for positioning (same technique as TryUpdateLoopSyncedDebris).
            GhostPlaybackState stateBeforeRender = state;
            long cycleBeforeRender = state != null ? state.loopCycleIndex : -1;
            var syncCtx = ctx;
            syncCtx.currentUT = spanLoopUT;
            if (RenderInRangeGhost(i, traj, f, syncCtx, suppressVisualFx,
                    hasPointData, hasInterpolatedPoints, hasSurfaceData, hasOrbitData,
                    allowEarlyDestroyedDebrisCompletion: false,
                    ref state, ref ghostActive))
            {
                if (state != null)
                {
                    if (ReferenceEquals(state, stateBeforeRender) && cycleBeforeRender >= 0
                        && cycleBeforeRender != unitCycle)
                        EmitLoopCycleTrace(i, traj, state, cycleBeforeRender, unitCycle, "unit");
                    state.loopCycleIndex = unitCycle;
                    // A ghost spawned THIS frame got positioned before the stash above could reach
                    // its fresh state object; stamp it now so the next frame derotates.
                    state.bodyFixedShiftSeconds = bodyFixedShift;
                }
            }

            // (7) BOUNDARY-OVERLAP secondary (docs/dev/plan-launch-boundary-overlap.md 3.1/3.3): when this member
            // carries a live boundary-overlap secondary this frame (the early-launching NEXT instance N+1, during
            // the borrow window of a zero-slack re-aim launch loop), position ONE secondary ghost in
            // overlapGhosts[i] at its own loopUT via a dedicated positioner. The previous (primary) instance is far
            // downstream near the destination by now, so the two ghosts render at different places. When no live
            // secondary, tear any down (the borrow window ended). This is engaged ONLY on the residual-seam loops;
            // already-aligned loops never reach here with a live secondary (the gate guarantees byte-identical).
            if (boundarySecondaryLive)
            {
                UpdateBoundaryOverlapSecondary(
                    i, traj, f, syncCtx, suppressGhosts, suppressVisualFx,
                    boundarySecondaryLoopUT, boundarySecondaryCycle);
            }
            else if (overlapGhosts.ContainsKey(i))
            {
                // No live secondary this frame but a stale boundary-overlap ghost lingers (e.g. the borrow window
                // just ended, or warp suppression hid the primary above and returned early on a prior frame): tear
                // it down. Mirror the standalone clear so the empty entry does not rerun every frame.
                DestroyAllOverlapGhosts(i);
                overlapGhosts.Remove(i);
            }
        }

        /// <summary>
        /// The mission self-overlap branch of <see cref="UpdateUnitMemberPlayback"/> (step 1b): routes this
        /// member through <see cref="UpdateOverlapPlayback"/> on the mission launch cadence, after the
        /// first-run handover, the zero-duration guard and the overlap warp gate. The caller returns after it.
        /// </summary>
        internal void UpdateUnitMemberSelfOverlap(
            int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f, FrameContext ctx,
            IReadOnlyList<IPlaybackTrajectory> trajectories, in GhostPlaybackLogic.LoopUnit unit,
            string unitKey, double unitSpan, double memberStartUT, double memberEndUT,
            bool suppressGhosts, bool suppressVisualFx,
            ref GhostPlaybackState state, ref bool ghostActive)
        {
            // Handover from the first run: before its anchor the unit is not published
            // (LoopUnitSet.LiveAt), so the slot may still hold the member's ORDINARY first-run
            // ghost. UpdateOverlapPlayback would demote it as an overlap copy of a cycle the
            // loop schedule never has (-1) and replay it beside the new cycle-0 primary, so it
            // is retired here. (The span-clock branch retires it at its cycle-change rebuild.)
            if (ShouldRetireOrdinaryStateAtUnitHandover(state))
            {
                ParsekLog.Info("Engine",
                    "Loop first-run handover: #" + i.ToString(CultureInfo.InvariantCulture)
                    + " \"" + (traj.VesselName ?? "?") + "\" UT="
                    + ctx.currentUT.ToString("F2", CultureInfo.InvariantCulture)
                    + " anchor=" + unit.PhaseAnchorUT.ToString("F2", CultureInfo.InvariantCulture)
                    + " - retiring the first run's ordinary ghost before the self-overlap copies");
                DestroyGhost(i, traj, f, reason: "first run handed to the loop unit");
                state = null;
                ghostActive = false;
            }

            double memberDuration = memberEndUT - memberStartUT;
            if (memberDuration <= 0)
            {
                GhostRenderTrace.EmitGuardSkip(traj, i, ctx.currentUT, "unit-overlap-member-zero-duration");
                if (ghostActive)
                {
                    DestroyGhost(i, traj, f, reason: "unit overlap member zero duration");
                    DestroyAllOverlapGhosts(i);
                }
                CountFrameSkip(GhostPlaybackSkipReason.MissionLoopUnitInactive);
                return;
            }

            double memberScheduleStartUT = GhostPlaybackLogic.ComputeMemberOverlapScheduleStartUT(
                unit.PhaseAnchorUT, unit.SpanStartUT, memberStartUT);
            double memberPlaybackStartUT = memberStartUT;

            // Warp suppression mirrors the standalone overlap dispatch (~2659): hide the moving
            // overlap meshes at high warp but keep the stationary newest primary visible.
            bool suppressOverlapGhosts = false;
            if (suppressGhosts)
            {
                if (!GhostPlaybackLogic.TryComputeNewestOverlapPlaybackUT(
                        ctx.currentUT, unit.OverlapCadenceSeconds, memberDuration,
                        memberPlaybackStartUT, memberScheduleStartUT,
                        out double newestOverlapUT, out _)
                    || GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(
                        ctx.warpRate, traj, newestOverlapUT))
                {
                    GhostRenderTrace.EmitGuardSkip(
                        traj, i, ctx.currentUT, "unit-overlap-warp-hidden");
                    if (ghostActive && state != null && state.ghost != null && state.ghost.activeSelf)
                    {
                        state.ghost.SetActive(false);
                        ResetGhostAppearanceTracking(state);
                    }
                    DestroyAllOverlapGhosts(i);
                    CountFrameSkip(GhostPlaybackSkipReason.WarpHidden);
                    return;
                }
                suppressOverlapGhosts = true;
            }

            // Member handoff for the camera. By default the camera follows the NEWEST mission
            // instance (its span-progress loopUT picks the live member to hand off to). BUT when
            // the player is WATCHING an instance of THIS unit and that instance is still in
            // flight, drive the handoff off the WATCHED instance's clock instead, so watching an
            // overlapping mission tracks one launch all the way through its stages rather than
            // jumping to each new launch. When the watched instance ENDS (or before it launches)
            // we fall back to the newest instance, and the watch's own cycle-lost fallback then
            // snaps the camera onto the newest in-flight launch. isInInterCycleTail is false
            // (overlap relaunches with no gap). Computed deterministically per member (only the
            // first to run this frame acts, via the lastUnitSelection dedup).
            double handoffLoopUT;
            long handoffCycle;
            double watchedInstLoopUT = 0;
            bool watchingThisUnitInstance =
                ctx.protectedLoopCycleIndex >= 0
                && System.Array.IndexOf(unit.MemberIndices, ctx.protectedIndex) >= 0;
            bool watchPinnedToInstance = watchingThisUnitInstance
                && GhostPlaybackLogic.TryComputeMissionInstanceSpanLoopUT(
                    unit.PhaseAnchorUT, unit.SpanStartUT, unitSpan, unit.OverlapCadenceSeconds,
                    ctx.currentUT, ctx.protectedLoopCycleIndex, out watchedInstLoopUT);
            // When the watched instance has ENDED (it IS a member of this unit, but its own span
            // clock is past the span end), the through-line the player was following is done.
            // Suppress the handoff retarget so it does NOT fall back to the newest instance and
            // yank the camera onto a fresh launch's same-named member: that cross-instance jump
            // would preempt the watched member's own terminal explosion hold (which fires later
            // this frame in UpdateOverlapPlayback and only applies while watchedRecordingIndex
            // still equals the ending member). The post-hold retarget then restarts the camera
            // on the through-line root. Diagnostics below still use the newest clock.
            bool watchedInstanceEnded = watchingThisUnitInstance && !watchPinnedToInstance;
            if (watchPinnedToInstance)
            {
                handoffLoopUT = watchedInstLoopUT;
                handoffCycle = ctx.protectedLoopCycleIndex;
            }
            else
            {
                GhostPlaybackLogic.ComputeNewestMissionInstanceSpanLoopUT(
                    unit.PhaseAnchorUT, unit.SpanStartUT, unitSpan, unit.OverlapCadenceSeconds,
                    ctx.currentUT, out handoffLoopUT, out handoffCycle);
            }
            LogUnitTransitionIfChanged(
                unit, unitKey, trajectories, handoffLoopUT, handoffCycle, ctx.currentUT,
                isInInterCycleTail: false, watchedIndex: ctx.protectedIndex,
                suppressWatchRetarget: watchedInstanceEnded);

            ParsekLog.VerboseRateLimited(
                "Engine", unitKey + "-self-overlap-" + i.ToString(CultureInfo.InvariantCulture),
                "Mission self-overlap unit owner=" + unit.OwnerIndex.ToString(CultureInfo.InvariantCulture)
                    + " member #" + i.ToString(CultureInfo.InvariantCulture)
                    + " overlapCadence=" + unit.OverlapCadenceSeconds.ToString("F2", CultureInfo.InvariantCulture)
                    + " span=" + unitSpan.ToString("F2", CultureInfo.InvariantCulture)
                    + " memberDur=" + memberDuration.ToString("F2", CultureInfo.InvariantCulture)
                    + " schedStart=" + memberScheduleStartUT.ToString("F2", CultureInfo.InvariantCulture)
                    + " handoffLoopUT=" + handoffLoopUT.ToString("F2", CultureInfo.InvariantCulture)
                    + " handoffCycle=" + handoffCycle.ToString(CultureInfo.InvariantCulture)
                    + (watchPinnedToInstance ? " (watched-instance pin)" : " (newest)"),
                5.0);

            UpdateOverlapPlayback(
                i, traj, f, ctx, state,
                unit.OverlapCadenceSeconds, memberDuration,
                memberPlaybackStartUT, memberScheduleStartUT,
                suppressVisualFx, suppressOverlapGhosts);
        }

        /// <summary>
        /// The descent-member override of <see cref="UpdateUnitMemberPlayback"/>: re-anchors a descent-set
        /// member on the shared descent head (forcing Render) or forces the out-of-window hide. Called only
        /// for a descent-set member of a unit with a descent trigger.
        /// </summary>
        internal static void ApplyDescentMemberRenderOverride(
            int i, in GhostPlaybackLogic.LoopUnit unit, FrameContext ctx, long unitCycle,
            double memberStartUT, double memberEndUT,
            ref double spanLoopUT, ref GhostPlaybackLogic.UnitMemberRenderDecision decision)
        {
            GhostPlaybackLogic.DescentMemberEngineRender descentRender =
                GhostPlaybackLogic.ResolveDescentMemberEngineRender(
                    unit, i, ctx.currentUT, unitCycle, memberStartUT, memberEndUT);

            var dic = CultureInfo.InvariantCulture;
            ParsekLog.VerboseRateLimited(
                "ReaimDescent",
                // Bounded by mission x member x phase (NOT frame count), matching the resolver key shape.
                "engine-descent-head." + unit.PhaseAnchorUT.ToString("R", dic) + "."
                    + unit.SpanStartUT.ToString("R", dic) + "." + i.ToString(dic) + "." + descentRender.Phase,
                "engine descent trigger member=" + i.ToString(dic)
                    + " cycle=" + unitCycle.ToString(dic) + " phase=" + descentRender.Phase
                    + " render=" + descentRender.Render
                    + " liveUT=" + ctx.currentUT.ToString("R", dic)
                    + " head=" + descentRender.Head.ToString("R", dic)
                    + " win=[" + memberStartUT.ToString("R", dic) + "," + memberEndUT.ToString("R", dic) + "]"
                    + " deorbit=" + unit.RecordedDeorbitUT.ToString("R", dic)
                    + " end=" + unit.DescentEndUT.ToString("R", dic)
                    + " cs=" + unit.CaptureShiftSeconds.ToString("R", dic),
                5.0);

            // NOTE: the always-on per-cycle lifecycle trace (DescentRenderTrace.Note -> DESCENT
            // WINDOW/RENDERED/SKIPPED/REVERTED) is intentionally NOT emitted here. DescentRenderTrace holds
            // ONE per-unit state machine keyed by (PhaseAnchorUT.SpanStartUT); the map/TS resolver
            // (ResolveTrackingStationSampleUT, reached in FLIGHT every frame via the polyline Driver's
            // ResolveTrackingStationSampleFrame walk) already feeds it. A second feeder from this engine in
            // the SAME scene would interleave two currentUTs into one state and produce spurious
            // Reverted/Skipped events. The engine-specific render decision is surfaced by the distinct
            // "engine-descent-head" VerboseRateLimited line above (a non-colliding key).

            if (descentRender.Render)
            {
                // Drive the ghost at the re-anchored descent head: override the downstream span clock so
                // the warp gate, activation gate, cycle change, and RenderInRangeGhost all run against the
                // descentHead instead of the raw loopUT. FORCE decision=Render: the raw loop-clock decision
                // here is typically HiddenOutsideWindow (the descent member's raw slot lands at the wrong
                // rotation, out of window) — the trigger overrides it to render the re-anchored slice.
                spanLoopUT = descentRender.Head;
                decision = GhostPlaybackLogic.UnitMemberRenderDecision.Render;
            }
            else
            {
                // Inert / Loiter / Done / outside this member's slice: HIDE. Force the out-of-window hide
                // so the existing (3) teardown runs (which also keeps the watched-owner-alive invariant) -
                // the descent member never drives its ghost on the raw loop clock (the transfer member's
                // shifted conic carries the icon during the wait).
                decision = GhostPlaybackLogic.UnitMemberRenderDecision.HiddenOutsideWindow;
            }
        }

        /// <summary>
        /// The descent handoff hide of <see cref="UpdateUnitMemberPlayback"/>: a rendering non-descent member of
        /// a unit with a descent trigger is hidden (decision forced to HiddenOutsideWindow) once the unit-wide
        /// descent phase is Descent. Called only under that gate, so the closure its deferred log message
        /// captures is allocated only there, never on the per-member hot path.
        /// </summary>
        internal static void ApplyDescentHandoffHide(
            int i, in GhostPlaybackLogic.LoopUnit unit, FrameContext ctx, long unitCycle, double spanLoopUT,
            ref GhostPlaybackLogic.UnitMemberRenderDecision decision, ref bool hiddenForDescentHandoff)
        {
            bool hideForDescentHandoff = GhostPlaybackLogic.ShouldHideNonDescentMemberForDescentHandoff(
                unit, i, ctx.currentUT, unitCycle,
                out Parsek.Reaim.DescentTrigger.DescentHeadPhase unitDescentPhase);
            var hdic = CultureInfo.InvariantCulture;
            double handoffLiveUT = ctx.currentUT;
            double handoffLoopUT = spanLoopUT;
            ParsekLog.VerboseRateLimited(
                "ReaimDescent",
                // Bounded by mission x member x phase x action (NOT frame count), matching the resolver's
                // transfer-handoff key shape; "engine-" prefix keeps it distinct from the resolver line
                // (which the polyline Driver also reaches in FLIGHT) so the two surfaces correlate. The
                // MESSAGE goes through the deferring Func overload so the expensive ToString("R") body is
                // built only on the frames that actually emit (this line fires per rendering non-descent
                // member per frame; the key concat is unavoidable - it addresses the rate-limit state).
                "engine-transfer-handoff." + unit.PhaseAnchorUT.ToString("R", hdic) + "."
                    + unit.SpanStartUT.ToString("R", hdic) + "." + i.ToString(hdic) + "."
                    + unitDescentPhase + "." + hideForDescentHandoff,
                () => "engine transfer/loiter member=" + i.ToString(hdic)
                    + " cycle=" + unitCycle.ToString(hdic)
                    + " unitDescentPhase=" + unitDescentPhase
                    + " baseDecision=Render liveUT=" + handoffLiveUT.ToString("R", hdic)
                    + " loopUT=" + handoffLoopUT.ToString("R", hdic)
                    + " -> " + (hideForDescentHandoff
                        ? "HIDDEN (descent playing; hand off to descent member, no double ghost)"
                        : "kept (loiter/normal ghost)"),
                5.0);
            if (hideForDescentHandoff)
            {
                hiddenForDescentHandoff = true;
                decision = GhostPlaybackLogic.UnitMemberRenderDecision.HiddenOutsideWindow;
            }
        }

        /// <summary>
        /// The boundary-overlap secondary decision of <see cref="UpdateUnitMemberPlayback"/>: NoSecondary,
        /// with the secondary clock at the span start and cycle 0, unless the unit holds launch alignment and
        /// this is not a descent-set member. Pure.
        /// </summary>
        internal static GhostPlaybackLogic.BoundaryOverlapSecondaryDecision DecideUnitMemberBoundarySecondary(
            in GhostPlaybackLogic.LoopUnit unit, FrameContext ctx, double memberStartUT, double memberEndUT,
            bool descentMember, out double boundarySecondaryLoopUT, out long boundarySecondaryCycle)
        {
            var boundarySecondaryDecision = GhostPlaybackLogic.BoundaryOverlapSecondaryDecision.NoSecondary;
            boundarySecondaryLoopUT = unit.SpanStartUT;
            boundarySecondaryCycle = 0;
            // Descent trigger R6 (mirrors ResolveTrackingStationSampleFrame): a descent-set member is governed
            // ENTIRELY by the re-anchored descent head; it must NOT also emit a boundary-overlap SECONDARY (the
            // secondary path does not descent-remap, so it would draw the body-fixed clip at the raw loop UT =
            // the wrong rotation again). Suppress it for descent members.
            if (unit.LaunchHoldEngaged && !descentMember)
            {
                boundarySecondaryDecision = GhostPlaybackLogic.DecideBoundaryOverlapSecondaryRender(
                    ctx.currentUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                    memberStartUT, memberEndUT, out boundarySecondaryLoopUT, out boundarySecondaryCycle,
                    unit.RelaunchSchedule, unit.LoiterCuts,
                    unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT, unit.ArrivalAlignPeriodSeconds,
                    unit.LaunchBodyRotationPeriodSeconds, unit.LaunchHoldEngaged, unit.RecordedSoiExitUT,
                    unit.ArrivalJointSecondaryPeriodSeconds, unit.ArrivalJointSecondaryToleranceSeconds,
                    unit.ArrivalJointMaxWholeHoldPeriods);
            }
            return boundarySecondaryDecision;
        }

        /// <summary>
        /// Step 3 of <see cref="UpdateUnitMemberPlayback"/>: the member is not rendering this frame (outside
        /// its window, in the inter-cycle wait, or hidden for the descent handoff). Logs why, then drives a live
        /// boundary-overlap secondary, keeps a watched ghost alive hidden, or destroys the ghost. The caller
        /// returns after it.
        /// </summary>
        internal void HideUnitMemberOutsideWindow(
            int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f, FrameContext ctx,
            in GhostPlaybackLogic.LoopUnit unit, string unitKey,
            GhostPlaybackLogic.UnitMemberRenderDecision decision, double spanLoopUT,
            double memberStartUT, double memberEndUT, bool hiddenForDescentHandoff,
            bool boundarySecondaryLive, double boundarySecondaryLoopUT, long boundarySecondaryCycle,
            bool suppressGhosts, bool suppressVisualFx, GhostPlaybackState state, bool ghostActive)
        {
            if (decision == GhostPlaybackLogic.UnitMemberRenderDecision.HiddenInterCycleTail)
            {
                // The wait between cycles (cadence greater than span): render nothing this frame.
                ParsekLog.VerboseRateLimited(
                    "Engine", unitKey + "-tail",
                    "Chain-loop unit owner=" + unit.OwnerIndex.ToString(CultureInfo.InvariantCulture)
                        + " in inter-cycle wait at loopUT=" + spanLoopUT.ToString("F2", CultureInfo.InvariantCulture)
                        + " - all members hidden",
                    5.0);
            }
            else
            {
                // The descent-handoff hide reaches here with an IN-window loopUT (the member was
                // Render before the forced hide), so name the real reason instead of the misleading
                // "outside its window". Distinct key too, so a genuine out-of-window hide of the same
                // member is never deduped away behind a handoff line (or vice versa).
                ParsekLog.VerboseRateLimited(
                    "Engine",
                    unitKey + (hiddenForDescentHandoff ? "-handoff-hide-" : "-hide-")
                        + i.ToString(CultureInfo.InvariantCulture),
                    "Chain-loop unit owner=" + unit.OwnerIndex.ToString(CultureInfo.InvariantCulture)
                        + " member #" + i.ToString(CultureInfo.InvariantCulture)
                        + " hidden: loopUT=" + spanLoopUT.ToString("F2", CultureInfo.InvariantCulture)
                        + (hiddenForDescentHandoff
                            ? " descent handoff (unit descent playing; window ["
                            : " outside its window [")
                        + memberStartUT.ToString("F2", CultureInfo.InvariantCulture)
                        + "," + memberEndUT.ToString("F2", CultureInfo.InvariantCulture) + "]"
                        + (hiddenForDescentHandoff ? ")" : ""),
                    5.0);
            }
            GhostRenderTrace.EmitGuardSkip(traj, i, ctx.currentUT, "mission-loop-unit-inactive");

            // H2 (launch->escape seam render): a live boundary-overlap SECONDARY occupies
            // overlapGhosts[i] even though the PRIMARY is OUTSIDE this member's window - the CHAIN case
            // where the launch member spans two recordings and the continuing instance N is months
            // downstream near the destination while the early-launching N+1 is mid-ascent in this
            // member's window. Hide the primary mesh but DRIVE the secondary instead of tearing it down
            // with the rest of the overlap storage: the secondary dispatch (7) sits AFTER this early
            // return, so without this the secondary's 3D mesh is destroyed before it is ever positioned
            // (flight 3D shows icon + trajectory but no ghost mesh during the borrow window). The primary
            // is hidden, not destroyed, so a watching camera stays parented to a live (hidden) ghost
            // exactly like the keep-watched-owner-alive fallback below. NoSecondary (every aligned loop /
            // non-launch-hold member / single-recording launch whose primary IS in-window) skips this and
            // takes the unchanged teardown below.
            if (boundarySecondaryLive)
            {
                if (ghostActive && state != null && state.ghost != null && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ResetGhostAppearanceTracking(state);
                }
                UpdateBoundaryOverlapSecondary(
                    i, traj, f, ctx, suppressGhosts, suppressVisualFx,
                    boundarySecondaryLoopUT, boundarySecondaryCycle);
                CountFrameSkip(GhostPlaybackSkipReason.MissionLoopUnitInactive);
                return;
            }

            // Keep-watched-owner-alive fallback (design D5): if this hidden member is the one the
            // camera is watching, hide its ghost WITHOUT destroying it so the camera never ends
            // up parented to a deactivated/destroyed ghost even if the UnitHandoffRetarget to the
            // new live member is dropped or deferred a frame. Treats the watched member like the
            // IsGhostHeld case (hide, do not destroy).
            if (ghostActive && i == ctx.protectedIndex)
            {
                if (state != null && state.ghost != null && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ResetGhostAppearanceTracking(state);
                    ParsekLog.VerboseRateLimited(
                        "CameraFollow", unitKey + "-keep-owner-alive-" + i.ToString(CultureInfo.InvariantCulture),
                        "unit watch fallback: holding watched member #" + i.ToString(CultureInfo.InvariantCulture)
                            + " ghost alive (hidden) until camera transfers to the live member",
                        5.0);
                }
                DestroyAllOverlapGhosts(i);
                CountFrameSkip(GhostPlaybackSkipReason.MissionLoopUnitInactive);
                return;
            }
            if (ghostActive)
            {
                DestroyGhost(i, traj, f, reason: hiddenForDescentHandoff
                    ? "descent handoff hide (unit descent playing)"
                    : "chain-loop unit member outside its window");
                DestroyAllOverlapGhosts(i);
            }
            CountFrameSkip(GhostPlaybackSkipReason.MissionLoopUnitInactive);
        }

        /// <summary>
        /// Step 4a of <see cref="UpdateUnitMemberPlayback"/>: true (after hiding or destroying the ghost) when
        /// the shared clock is in this member's window but below its own payload start.
        /// </summary>
        internal bool TryHideUnitMemberBeforeActivation(
            int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f, FrameContext ctx,
            in GhostPlaybackLogic.LoopUnit unit, string unitKey, double spanLoopUT,
            GhostPlaybackState state, bool ghostActive)
        {
            double memberActivationStartUT = ResolveGhostActivationStartUT(traj);
            if (spanLoopUT < memberActivationStartUT)
            {
                ParsekLog.VerboseRateLimited(
                    "Engine", unitKey + "-pre-activation-" + i.ToString(CultureInfo.InvariantCulture),
                    "Chain-loop unit owner=" + unit.OwnerIndex.ToString(CultureInfo.InvariantCulture)
                        + " member #" + i.ToString(CultureInfo.InvariantCulture)
                        + " hidden: spanLoopUT=" + spanLoopUT.ToString("F2", CultureInfo.InvariantCulture)
                        + " below member activation UT=" + memberActivationStartUT.ToString("F2", CultureInfo.InvariantCulture),
                    5.0);
                GhostRenderTrace.EmitGuardSkip(traj, i, ctx.currentUT, "unit-member-before-activation");
                // Keep-watched-owner-alive guard (edge 10, design D5): mirror every other hide path
                // in this method. If the pre-activation member is the one the camera is watching,
                // hide its ghost WITHOUT destroying it so ValidateWatchedGhostStillActive never drops
                // watch and the camera stays anchored until the unit-handoff retarget lands. Without
                // this, an ExplicitStartUT-widened watched member would be destroyed here (the only
                // remaining unconditional DestroyGhost in the unit path), breaking the never-destroy-
                // the-watched-ghost invariant. Edge-of-an-edge: ExplicitStartUT widened StartUT below
                // the first payload sample AND that member is watched. Runtime coverage is the
                // existing in-game watch-retarget test; behavior for non-watched members is unchanged.
                if (ghostActive && i == ctx.protectedIndex)
                {
                    if (state != null && state.ghost != null && state.ghost.activeSelf)
                    {
                        state.ghost.SetActive(false);
                        ResetGhostAppearanceTracking(state);
                        ParsekLog.VerboseRateLimited(
                            "CameraFollow", unitKey + "-keep-owner-alive-pre-activation-" + i.ToString(CultureInfo.InvariantCulture),
                            "unit watch fallback: holding watched member #" + i.ToString(CultureInfo.InvariantCulture)
                                + " ghost alive (hidden) below activation UT until camera transfers to the live member",
                            5.0);
                    }
                    DestroyAllOverlapGhosts(i);
                    CountFrameSkip(GhostPlaybackSkipReason.MissionLoopUnitInactive);
                    return true;
                }
                if (ghostActive)
                {
                    DestroyGhost(i, traj, f, reason: "chain-loop unit member before activation UT");
                    DestroyAllOverlapGhosts(i);
                }
                CountFrameSkip(GhostPlaybackSkipReason.MissionLoopUnitInactive);
                return true;
            }
            return false;
        }

        /// <summary>
        /// One unit member's per-frame seam-marker check (design-dock-event-graph.md 7.3 / 14).
        /// Raises <see cref="OnSeamMarker"/> at most once per (marker, unit cycle) when the shared
        /// span clock enters a precomputed seam window for THIS member.
        ///
        /// <para><b>Budget.</b> A unit with no markers (every mission that never crossed a dock, and
        /// every unit built outside the flight scene) returns on the first null check - no
        /// dictionary touch, no allocation. A unit WITH markers costs one dictionary lookup plus the
        /// pure cursor's single double comparison; allocation happens only on an actual emission
        /// (the event object) and once per member on first use (the cursor state).</para>
        ///
        /// <para><b>Cycle change and unit swap.</b> The cursor and dedup reset when this member's
        /// tracked cycle moves OR when the unit's marker LIST is a different object (a signature-
        /// gated rebuild swaps it with no cycle change). Both rules live in
        /// <see cref="LoopSeamMarkerRuntime"/>; the cycle rule must live there rather than beside
        /// the completed-event dedup clear in the render path, because that clear sits below the
        /// hide/destroy block this check deliberately runs above.</para>
        ///
        /// <para><b>Not called from the self-overlap branch</b> (edge case 20): overlap instances
        /// already accept reduced fidelity, and one ScreenMessage per staggered instance would be
        /// noise. Documented limitation, not an oversight.</para>
        /// </summary>
        private void TryEmitSeamMarker(
            int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f,
            GhostPlaybackLogic.LoopUnit unit, double spanLoopUT, long unitCycle)
        {
            IReadOnlyList<GhostPlaybackLogic.LoopSeamMarker> markers = unit.SeamMarkers;
            if (markers == null || markers.Count == 0)
                return;

            int markerIndex = seamMarkerRuntime.TryTake(
                markers, i, spanLoopUT, unitCycle,
                out int skippedUnfired, out int firstSkippedIndex);

            // The clock stepped over a whole display window without firing - the deliberate
            // high-warp behavior (a line nobody could read is worse than none), logged so the
            // silent path still leaves evidence. Once per (marker, cycle) by key construction.
            if (skippedUnfired > 0 && firstSkippedIndex >= 0)
            {
                var sic = CultureInfo.InvariantCulture;
                GhostPlaybackLogic.LoopSeamMarker skipped = markers[firstSkippedIndex];
                ParsekLog.VerboseRateLimited(
                    "SeamMarker",
                    "skipped-at-warp-" + i.ToString(sic) + "-" + unitCycle.ToString(sic)
                        + "-" + firstSkippedIndex.ToString(sic),
                    "skipped-at-warp memberIdx=" + i.ToString(sic)
                        + " cycle=" + unitCycle.ToString(sic)
                        + " count=" + skippedUnfired.ToString(sic)
                        + " seamUT=" + skipped.SeamUT.ToString("R", sic)
                        + " windowEndUT=" + skipped.WindowEndUT.ToString("R", sic)
                        + " loopUT=" + spanLoopUT.ToString("R", sic)
                        + " (frame stepped past the whole window; no message posted)",
                    5.0);
            }

            if (markerIndex < 0)
                return;

            GhostPlaybackLogic.LoopSeamMarker marker = markers[markerIndex];
            ghostStates.TryGetValue(i, out GhostPlaybackState markerState);
            OnSeamMarker?.Invoke(new SeamMarkerEvent
            {
                Index = i,
                Trajectory = traj,
                State = markerState,
                Flags = f,
                Kind = marker.Kind,
                Text = marker.Text,
                SeamUT = marker.SeamUT,
                UnitCycle = unitCycle,
            });
        }

        /// <summary>
        /// Positions the BOUNDARY-OVERLAP secondary ghost (the early-launching NEXT instance N+1) for unit member
        /// <paramref name="index"/> at <paramref name="secondaryLoopUT"/> (docs/dev/plan-launch-boundary-overlap.md
        /// 3.3). A thin adapter that BORROWS the <c>overlapGhosts[index]</c> storage but is NEVER the self-overlap
        /// expiry path: it spawns exactly one secondary (rebuilt on a secondary-cycle change), stamps
        /// <see cref="GhostPlaybackState.isBoundaryOverlapSecondary"/> (so the watch camera excludes it), and
        /// positions it through the same in-range positioning pipeline the overlap primary uses
        /// (ResolvePlaybackDistance -> ApplyZoneRendering -> PositionLoopAtPlaybackUT -> EmitPostPositionUpdate ->
        /// ApplyFrameVisuals). It does NOT run the expiry block (no <see cref="OnOverlapCameraAction"/> /
        /// <see cref="OnOverlapExpired"/>): the secondary never expires that way - it is destroyed by HasSecondary
        /// going false at the boundary, where it has already become the next primary. The secondary's audio is muted
        /// (overlap ghosts get no audio) via the OverlapPrimaryEnter demoted-shell lifecycle. At high warp the moving
        /// secondary mesh is hidden (the same warp gate the moving overlap meshes use).
        /// </summary>
        private void UpdateBoundaryOverlapSecondary(
            int index, IPlaybackTrajectory traj, TrajectoryPlaybackFlags flags, FrameContext ctx,
            bool suppressGhosts, bool suppressVisualFx,
            double secondaryLoopUT, long secondaryCycle)
        {
            // Warp suppression: the secondary is the in-SOI launching ghost (moving), so hide its MESH at high warp
            // (mirrors the moving overlap-mesh gate). Destroy it so it re-spawns cleanly when warp drops.
            if (suppressGhosts && GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(ctx.warpRate, traj, secondaryLoopUT))
            {
                GhostRenderTrace.EmitGuardSkip(
                    traj, index, ctx.currentUT, "boundary-overlap-secondary-warp-hidden");
                DestroyAllOverlapGhosts(index);
                return;
            }

            if (!overlapGhosts.TryGetValue(index, out List<GhostPlaybackState> overlaps))
            {
                overlaps = new List<GhostPlaybackState>();
                overlapGhosts[index] = overlaps;
            }

            // Exactly one secondary, keyed by the secondary cycle. Rebuild on a cycle change (the borrow window's
            // instance advanced to the next N+1). Any leftover entry that is not the current secondary cycle is
            // cleared first so the storage holds at most one boundary-overlap ghost.
            GhostPlaybackState sec = null;
            if (overlaps.Count > 0 && overlaps[0] != null
                && overlaps[0].isBoundaryOverlapSecondary
                && overlaps[0].loopCycleIndex == secondaryCycle)
            {
                sec = overlaps[0];
            }
            if (sec == null)
            {
                DestroyAllOverlapGhosts(index);
                sec = SpawnBoundaryOverlapSecondary(index, traj, flags, secondaryLoopUT, secondaryCycle);
                if (sec == null)
                {
                    // Visual build failed / deferred this frame; nothing to position yet.
                    return;
                }
                overlaps.Add(sec);
            }

            PositionBoundaryOverlapSecondaryAt(index, traj, ctx, sec, secondaryLoopUT, suppressVisualFx);
        }

        /// <summary>
        /// Spawns the boundary-overlap secondary shell for unit member <paramref name="index"/> at
        /// <paramref name="secondaryLoopUT"/> (docs/dev/plan-launch-boundary-overlap.md 3.3). Reuses
        /// <see cref="CreatePendingSpawnState"/> with the OverlapPrimaryEnter demoted-shell lifecycle (no
        /// ghost-created / camera side effects), mutes the audio (overlap ghosts get no audio), stamps the
        /// <see cref="GhostPlaybackState.isBoundaryOverlapSecondary"/> flag, and loads the visuals. Returns the
        /// state, or null if the visual build failed / is still pending this frame (the caller positions next frame).
        /// </summary>
        private GhostPlaybackState SpawnBoundaryOverlapSecondary(
            int index, IPlaybackTrajectory traj, TrajectoryPlaybackFlags flags,
            double secondaryLoopUT, long secondaryCycle)
        {
            GhostPlaybackState sec = CreatePendingSpawnState(
                traj, secondaryLoopUT, PendingSpawnLifecycle.OverlapPrimaryEnter, flags);
            // Demote to a shell with no lifecycle side effects (mirror the demoted-overlap path): the secondary must
            // fire no ghost-created / camera-retarget events. CreatePendingSpawnState sets the lifecycle to
            // OverlapPrimaryEnter; null it so EnsureGhostVisualsLoaded does not fire a spawn event.
            sec.pendingSpawnLifecycle = PendingSpawnLifecycle.None;
            sec.pendingSpawnFlags = default(TrajectoryPlaybackFlags);
            sec.loopCycleIndex = secondaryCycle;
            sec.isBoundaryOverlapSecondary = true;

            GhostVisualLoadStatus status = EnsureGhostVisualsLoaded(
                index, traj, sec, secondaryLoopUT, "boundary-overlap secondary first spawn",
                resetCompletedEventDedup: false);
            if (sec.ghost != null)
                GhostPlaybackLogic.MuteAllAudio(sec); // boundary-overlap secondary gets no audio
            if (status == GhostVisualLoadStatus.Failed)
            {
                CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                DestroyOverlapGhostState(sec, index, traj, "boundary-overlap secondary build failed");
                ParsekLog.Warn("Engine",
                    $"Boundary-overlap secondary: SpawnGhost failed for #{index} cycle={secondaryCycle}");
                return null;
            }
            if (status == GhostVisualLoadStatus.Pending)
            {
                // Build deferred (time-sliced over frames); position next frame when ready. Keep the shell so the
                // caller's cycle-match check reuses it.
                ParsekLog.VerboseRateLimited(
                    "Engine", "boundary-overlap-secondary-pending-" + index.ToString(CultureInfo.InvariantCulture),
                    $"Boundary-overlap secondary build pending for #{index} cycle={secondaryCycle} - position deferred",
                    5.0);
                return sec;
            }
            ParsekLog.VerboseRateLimited(
                "Engine", "boundary-overlap-secondary-spawn-" + index.ToString(CultureInfo.InvariantCulture),
                $"Boundary-overlap secondary spawned for #{index} cycle={secondaryCycle} loopUT={secondaryLoopUT.ToString("F2", CultureInfo.InvariantCulture)} (audio muted)",
                5.0);
            return sec;
        }

        /// <summary>
        /// Positions the boundary-overlap secondary <paramref name="sec"/> at <paramref name="secondaryLoopUT"/>
        /// through the same in-range positioning pipeline the overlap primary uses (factored from the overlap
        /// primary positioning block), NEVER the expiry block. No <see cref="OnOverlapCameraAction"/> /
        /// <see cref="OnOverlapExpired"/> fire from this path (docs/dev/plan-launch-boundary-overlap.md 3.3).
        /// </summary>
        private void PositionBoundaryOverlapSecondaryAt(
            int index, IPlaybackTrajectory traj, FrameContext ctx, GhostPlaybackState sec,
            double secondaryLoopUT, bool suppressVisualFx)
        {
            if (sec == null)
                return;

            double distance = ResolvePlaybackDistance(index, traj, sec, secondaryLoopUT, ctx.activeVesselPos);
            double activeVesselDistance = ResolvePlaybackActiveVesselDistance(
                index, traj, sec, secondaryLoopUT, ctx.activeVesselPos);
            CachePlaybackDistances(sec, activeVesselDistance, distance);
            var zoneResult = positioner.ApplyZoneRendering(
                index, sec, traj, distance, secondaryLoopUT, ctx.protectedIndex);
            if (zoneResult.hiddenByZone)
            {
                GhostRenderTrace.EmitGuardSkip(
                    traj, index, ctx.currentUT,
                    "boundary-overlap-secondary-hidden-by-zone distance=" + FormatPlaybackDistanceForLog(distance));
                HandleHiddenGhostVisualState(
                    index, traj, sec, secondaryLoopUT, ctx.warpRate, distance, overlapGhost: true,
                    hiddenReason: $"boundary-overlap secondary hidden by distance LOD at {FormatPlaybackDistanceForLog(distance)}");
                return;
            }

            if (!HasLoadedGhostVisuals(sec))
            {
                if (!TryReserveSpawnSlot(index, "boundary-overlap-secondary-rehydrate"))
                    return;
                GhostVisualLoadStatus status = EnsureGhostVisualsLoaded(
                    index, traj, sec, secondaryLoopUT, "boundary-overlap secondary re-entered visible distance tier");
                if (status == GhostVisualLoadStatus.Failed || status == GhostVisualLoadStatus.Pending)
                {
                    if (status == GhostVisualLoadStatus.Failed)
                        CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                    return;
                }
            }

            GhostPlaybackLogic.ApplyDistanceLodFidelity(sec, zoneResult.reduceFidelity);
            bool effectiveSuppressVisualFx = suppressVisualFx || zoneResult.suppressVisualFx;
            sec.anchorRetiredThisFrame = false;
            GhostRenderTrace.BeginFrame(traj, index, ctx.currentUT, secondaryLoopUT, "boundary-overlap-secondary");
            bool usedBodyFixed = PositionLoopAtPlaybackUT(
                index, traj, sec, secondaryLoopUT, effectiveSuppressVisualFx,
                "GhostPlaybackEngine.PositionBoundaryOverlapSecondaryAt");
            bool retired = EmitPostPositionUpdate(
                traj, index, ctx.currentUT, secondaryLoopUT, sec, "boundary-overlap-secondary",
                usedBodyFixed, rawPlaybackUT: secondaryLoopUT);
            if (retired)
            {
                ApplyFrameVisuals(index, traj, sec, secondaryLoopUT, ctx.warpRate,
                    skipPartEvents: true, suppressVisualFx: true, allowTransientEffects: false);
            }
            else
            {
                bool activatedDeferredState = ActivateGhostVisualsIfNeeded(sec);
                ApplyFrameVisuals(index, traj, sec, secondaryLoopUT, ctx.warpRate,
                    zoneResult.skipPartEvents, effectiveSuppressVisualFx);
                if (ShouldRestoreDeferredRuntimeFxState(activatedDeferredState, effectiveSuppressVisualFx))
                    GhostPlaybackLogic.RestoreDeferredRuntimeFxState(sec);
                TrackGhostAppearance(index, traj, sec, secondaryLoopUT, "boundary-overlap-secondary");
            }
        }

        /// <summary>
        /// The line a loop unit's FIRST evaluation logs (flight engine and Space Center share it).
        /// Later changes log a hand-off or a cycle wrap against the stored selection; the first one
        /// has nothing to compare against, so it names the opening camera-live member (-1 = none
        /// in window). Pure.
        /// </summary>
        internal static string FormatUnitFirstEvaluation(
            string unitLabel, int ownerIndex, int liveMemberIdx, double spanLoopUT, long cycle)
        {
            return unitLabel + " unit owner=" + ownerIndex.ToString(CultureInfo.InvariantCulture)
                + " first evaluation: camera-live member #" + liveMemberIdx.ToString(CultureInfo.InvariantCulture)
                + " at loopUT=" + spanLoopUT.ToString("F2", CultureInfo.InvariantCulture)
                + " cycle=" + cycle.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Logs the unit's cycle wrap and camera-handoff once per transition (rate-limited per unit
        /// owner) and fires the watch retarget. Called by every member of the unit; only the first
        /// member to run this frame sees a CHANGE versus the last recorded state and acts. Computes
        /// the unit's live CAMERA member = the in-window member with the highest StartUT (the newest
        /// segment) by scanning all members against <paramref name="spanLoopUT"/>; -1 = nothing live
        /// (inter-cycle wait / before span). Records the new state so siblings later in the same
        /// frame see no change.
        /// </summary>
        private void LogUnitTransitionIfChanged(
            GhostPlaybackLogic.LoopUnit unit, string unitKey,
            IReadOnlyList<IPlaybackTrajectory> trajectories,
            double spanLoopUT, long cycle, double currentUT, bool isInInterCycleTail, int watchedIndex,
            bool suppressWatchRetarget)
        {
            // Live camera member = the in-window member to point the watch camera at. -1 in the
            // inter-cycle wait or before the span. Selection is two-tier so a structural fork
            // (e.g. a crew EVA peeling off at the same UT the parent vessel continues) does not
            // steal the camera from the through-line the player is watching:
            //   tier 1 - a member that is the WATCHED member's vessel always beats a non-match
            //            (keeps the camera on the continuing craft L->cont, not L->EVA-kerbal
            //            which shares the same StartUT). Same vessel = same launch guid, with the
            //            VesselName only as the fallback when a guid is unknown
            //            (GhostPlaybackLogic.IsSameWatchedVessel): a name is not an identity;
            //   tier 2 - within the same tier, the newest segment (highest StartUT) wins.
            // Both watched keys are null when nothing is watched, so unwatched playback keeps the
            // pure highest-StartUT behavior.
            string watchedVesselName = null;
            string watchedVesselGuid = null;
            bool hasWatched = false;
            if (watchedIndex >= 0 && watchedIndex < trajectories.Count
                && trajectories[watchedIndex] != null)
            {
                hasWatched = true;
                watchedVesselName = trajectories[watchedIndex].VesselName;
                watchedVesselGuid = trajectories[watchedIndex].RecordedVesselGuid;
            }

            int liveMemberIdx = -1;
            double liveStartUT = double.NegativeInfinity;
            bool liveMatchesWatched = false;
            if (!isInInterCycleTail)
            {
                int[] members = unit.MemberIndices;
                if (members != null)
                {
                    for (int m = 0; m < members.Length; m++)
                    {
                        int idx = members[m];
                        if (idx < 0 || idx >= trajectories.Count || trajectories[idx] == null)
                            continue;
                        var member = trajectories[idx];
                        // Use the unit's TRIMMED member window (interval start/end trim), matching
                        // DecideUnitMemberRender. The raw [StartUT,EndUT] would treat a start/end-
                        // trimmed member as live while the engine is hiding it, so the watch camera
                        // could hand off to (or stay on) a member that is not actually rendered.
                        double mStart = unit.MemberStartUT(idx, member.StartUT);
                        double mEnd = unit.MemberEndUT(idx, member.EndUT);
                        if (!GhostPlaybackLogic.IsLoopUTInMemberWindow(spanLoopUT, mStart, mEnd))
                            continue;

                        bool matchesWatched = hasWatched
                            && GhostPlaybackLogic.IsSameWatchedVessel(
                                watchedVesselGuid, watchedVesselName,
                                member.RecordedVesselGuid, member.VesselName);

                        if (GhostPlaybackLogic.IsBetterUnitCameraLiveMember(
                                matchesWatched, mStart, liveMatchesWatched, liveStartUT))
                        {
                            liveStartUT = mStart;
                            liveMemberIdx = idx;
                            liveMatchesWatched = matchesWatched;
                        }
                    }
                }
            }

            // Is the watched member rendering this frame (for the retarget transition)? Use the
            // TRIMMED member window so this matches the actual render decision (a trimmed watched
            // member is "not rendering" once the shared clock leaves its trimmed window, even if it
            // is still inside the raw recording range).
            bool watchedIsRendering = false;
            if (watchedIndex >= 0 && watchedIndex < trajectories.Count
                && trajectories[watchedIndex] != null && !isInInterCycleTail)
            {
                var w = trajectories[watchedIndex];
                watchedIsRendering = GhostPlaybackLogic.IsLoopUTInMemberWindow(
                    spanLoopUT,
                    unit.MemberStartUT(watchedIndex, w.StartUT),
                    unit.MemberEndUT(watchedIndex, w.EndUT));
            }

            if (!lastUnitSelection.TryGetValue(unit.OwnerIndex, out var prev))
            {
                // First evaluation of this unit (playback start, a reset, or the frame the unit
                // goes live at its anchor after the first run played as an ordinary recording):
                // there is no prior member to hand off from, so name the opening selection.
                ParsekLog.VerboseRateLimited(
                    "Engine", unitKey + "-first",
                    FormatUnitFirstEvaluation("Chain-loop", unit.OwnerIndex, liveMemberIdx, spanLoopUT, cycle),
                    5.0);
                lastUnitSelection[unit.OwnerIndex] = (cycle, liveMemberIdx, watchedIsRendering);
                return;
            }
            bool watchedWasRendering = prev.watchedRendering;
            if (prev.cycle == cycle && prev.liveMemberIdx == liveMemberIdx
                && watchedWasRendering == watchedIsRendering)
                return;

            if (cycle != prev.cycle)
            {
                ParsekLog.VerboseRateLimited(
                    "Engine", unitKey + "-wrap",
                    "Chain-loop unit owner=" + unit.OwnerIndex.ToString(CultureInfo.InvariantCulture)
                        + " wrapped cycle " + prev.cycle.ToString(CultureInfo.InvariantCulture)
                        + "->" + cycle.ToString(CultureInfo.InvariantCulture)
                        + " at UT=" + currentUT.ToString("F2", CultureInfo.InvariantCulture),
                    5.0);
            }
            else if (liveMemberIdx != prev.liveMemberIdx && liveMemberIdx >= 0 && prev.liveMemberIdx >= 0)
            {
                ParsekLog.VerboseRateLimited(
                    "Engine", unitKey + "-handoff",
                    "Chain-loop unit owner=" + unit.OwnerIndex.ToString(CultureInfo.InvariantCulture)
                        + " camera-live member #" + prev.liveMemberIdx.ToString(CultureInfo.InvariantCulture)
                        + "->#" + liveMemberIdx.ToString(CultureInfo.InvariantCulture)
                        + " at loopUT=" + spanLoopUT.ToString("F2", CultureInfo.InvariantCulture),
                    5.0);
            }

            // Watch retarget: when the watched member stops rendering (its window ended) and a
            // different live member exists, transfer the camera to it. The pure decision
            // (ShouldRetargetWatchOnUnitHandoff) gates on the watched index belonging to this unit
            // and the render-state transition; firing here keys off the SAME single per-frame
            // transition detector, so the event fires once per boundary, not every frame.
            //
            // Re-fire while pending: the host transfer can DEFER when the target member's ghost is
            // not spawned yet (unit-member respawns are time-sliced over several frames). A
            // fire-and-forget single shot would leave the camera stuck on the old (now hidden)
            // member because the steady-state early-return above suppresses any re-fire once the
            // state advances. To make the retarget self-healing we DO NOT advance the
            // watchedRendering edge of lastUnitSelection until the watch camera has actually
            // landed on liveMemberIdx (the engine reads the live watched index as `watchedIndex`,
            // i.e. ctx.protectedIndex == WatchModeController.WatchedRecordingIndex). While the
            // transfer is pending we keep watchedRendering=true, so next frame the gate sees the
            // same wasRendering=true -> isRendering=false transition and re-fires the event. Once
            // the transfer lands (watchedIndex == liveMemberIdx) we store the real watchedIsRendering
            // value and re-firing stops. cycle / liveMemberIdx always advance as-is so we never emit
            // duplicate "wrapped cycle" diagnostics.
            // Only hand the watch camera off to a SAME-VESSEL continuation of the through-line the
            // player is watching (liveMatchesWatched). When the watched member ends and the only
            // in-window member is a different-vessel sibling (e.g. a kerbal who went EVA, or any
            // peeled-off piece), DO NOT retarget: leaving the camera on the ending member lets its
            // own terminal end take over (a terminal=Destroyed member fires ExplosionHoldStart from
            // UpdateOverlapPlayback later in the SAME frame, which HandleOverlapCameraAction applies
            // only while watchedRecordingIndex still equals that member). Retargeting to the sibling
            // first moves watchedRecordingIndex off the member, so HandleOverlapCameraAction's
            // `watchedRecordingIndex != evt.Index` guard silently drops the explosion hold and the
            // camera jumps to the sibling at the moment of impact instead of holding the explosion.
            // hasWatched is false when nothing is watched, so liveMatchesWatched is false and
            // retargetMemberIdx is -1; the retarget already cannot fire unwatched, so this is inert
            // there.
            // The watched through-line's END suppresses the handoff in two ways: (a) the watched
            // INSTANCE has ended (suppressWatchRetarget) - the whole instance is done, so the camera
            // must NOT jump to a fresh launch's same-named member (cross-instance); (b) within a live
            // instance, the only remaining in-window member is a DIFFERENT vessel (a peeled EVA kerbal
            // / booster) - ResolveUnitHandoffRetargetMember returns -1. Either way the camera holds on
            // the ending member so its terminal explosion hold applies and the post-hold retarget
            // restarts on the through-line root.
            int retargetMemberIdx = suppressWatchRetarget
                ? -1
                : GhostPlaybackLogic.ResolveUnitHandoffRetargetMember(liveMemberIdx, liveMatchesWatched);
            if (suppressWatchRetarget && watchedIndex >= 0)
                ParsekLog.VerboseRateLimited(
                    "CameraFollow", unitKey + "-handoff-instance-ended",
                    "unit watch handoff suppressed owner="
                        + unit.OwnerIndex.ToString(CultureInfo.InvariantCulture)
                        + " watched #" + watchedIndex.ToString(CultureInfo.InvariantCulture)
                        + " instance ended - holding for its own terminal end / post-hold restart"
                        + " at loopUT=" + spanLoopUT.ToString("F2", CultureInfo.InvariantCulture),
                    5.0);
            else if (retargetMemberIdx < 0 && watchedIndex >= 0 && watchedWasRendering
                && !watchedIsRendering && liveMemberIdx >= 0)
                ParsekLog.VerboseRateLimited(
                    "CameraFollow", unitKey + "-handoff-noncontinuation",
                    "unit watch handoff suppressed owner="
                        + unit.OwnerIndex.ToString(CultureInfo.InvariantCulture)
                        + " watched #" + watchedIndex.ToString(CultureInfo.InvariantCulture)
                        + " ended; only live member #" + liveMemberIdx.ToString(CultureInfo.InvariantCulture)
                        + " is a different vessel - holding for the watched member's own end"
                        + " at loopUT=" + spanLoopUT.ToString("F2", CultureInfo.InvariantCulture),
                    5.0);

            bool retargetFired = GhostPlaybackLogic.ShouldRetargetWatchOnUnitHandoff(
                watchedIndex, watchedWasRendering, watchedIsRendering, retargetMemberIdx, unit);
            if (retargetFired)
            {
                ParsekLog.Info("CameraFollow",
                    "unit watch retarget owner=" + unit.OwnerIndex.ToString(CultureInfo.InvariantCulture)
                        + " member #" + watchedIndex.ToString(CultureInfo.InvariantCulture)
                        + "->#" + retargetMemberIdx.ToString(CultureInfo.InvariantCulture)
                        + " at loopUT=" + spanLoopUT.ToString("F2", CultureInfo.InvariantCulture));
                OnLoopCameraAction?.Invoke(new CameraActionEvent
                {
                    Index = retargetMemberIdx,
                    Action = CameraActionType.UnitHandoffRetarget,
                });
                // If the watch camera did not transfer this frame (still on the old member because
                // the target ghost is mid-build), the pure helper preserves the rendering edge so the
                // event re-fires next frame; this rate-limited line records that pending state.
                if (watchedIndex != retargetMemberIdx)
                    ParsekLog.VerboseRateLimited(
                        "CameraFollow", "unit-handoff-retry",
                        "unit watch retarget pending owner="
                            + unit.OwnerIndex.ToString(CultureInfo.InvariantCulture)
                            + " target #" + retargetMemberIdx.ToString(CultureInfo.InvariantCulture)
                            + " (watch still #" + watchedIndex.ToString(CultureInfo.InvariantCulture)
                            + ") - re-firing until ghost spawns",
                        5.0);
            }

            // Self-healing edge: while the retarget is pending (target ghost not yet spawned) keep
            // the rendering edge true so the steady-state early-return does not suppress the re-fire.
            // Once the transfer lands (watchedIndex == retargetMemberIdx) the real value is stored and
            // re-firing stops. cycle / liveMemberIdx always advance as-is (no duplicate wrap logs).
            bool storedWatchedRendering = GhostPlaybackLogic.ResolveUnitHandoffStoredRenderingEdge(
                retargetFired, watchedIndex, retargetMemberIdx, watchedIsRendering);

            lastUnitSelection[unit.OwnerIndex] = (cycle, liveMemberIdx, storedWatchedRendering);
        }
    }
}
