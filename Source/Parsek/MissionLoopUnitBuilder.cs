using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Reaim;

namespace Parsek
{
    // Pure adapter: turns every looping Mission's selection into a span-clock LoopUnitSet
    // (GhostPlaybackLogic.LoopUnit / LoopUnitSet), one unit per looping Mission. Multiple
    // Missions loop concurrently, at most one per tree (enforced by MissionStore), so their
    // committed indices are disjoint and each owns its own span clock. Each unit carries TWO
    // cadences from the same user input: a span-clock cadence (never shorter than the span,
    // so a single span instance always plays in full - used by KSC, the Tracking Station,
    // and the flight no-overlap branch) and an overlap cadence (the TRUE launch-to-launch
    // period, Auto = the global auto-loop interval, cap-clamped to MaxOverlapMissionInstances).
    // When the overlap cadence is shorter than the span the flight engine relaunches the
    // whole mission on that cadence so several staggered instances play concurrently, exactly
    // like a single recording with period < duration. Pure: no Unity calls, no shared mutable
    // state, no recording mutation.
    internal static class MissionLoopUnitBuilder
    {
        // Set true in tests to silence the single per-build Verbose summary.
        internal static bool SuppressLogging;

        /// <summary>
        /// Builds the LoopUnitSet for every looping Mission (one unit per Mission). Multiple Missions
        /// loop concurrently - at most one per tree (enforced by MissionStore), so their committed
        /// indices are disjoint and each Mission owns its own span clock. Returns
        /// <see cref="GhostPlaybackLogic.LoopUnitSet.Empty"/> when nothing loops or no looping Mission
        /// maps to any committed member. Member indices are committed-list indices (the alignment
        /// invariant the engine consumes). Collisions (only possible if the one-per-tree invariant is
        /// violated upstream): a unit whose owner index is already taken is dropped wholesale with a
        /// warn (the realistic same-tree case, since same-tree variants share the trunk root and so
        /// the same earliest/owner index); a stray shared member index is kept on its FIRST claimant
        /// in OwnerByIndex, so the engine's per-index dispatch always routes a shared index to one
        /// unit.
        /// </summary>
        internal static GhostPlaybackLogic.LoopUnitSet Build(
            IReadOnlyList<Mission> missions,
            IReadOnlyList<RecordingTree> trees,
            IReadOnlyList<Recording> committed,
            double autoLoopIntervalSeconds,
            IBodyInfo bodyInfo = null,
            TransitedBodyRotationMode transitedBodyRotationMode = TransitedBodyRotationMode.Tight)
        {
            if (missions == null)
                return GhostPlaybackLogic.LoopUnitSet.Empty;

            Dictionary<string, int> indexById = BuildIndexById(committed);

            var unitsByOwner = new Dictionary<int, GhostPlaybackLogic.LoopUnit>();
            var ownerByIndex = new Dictionary<int, int>();
            int builtUnits = 0;

            for (int mi = 0; mi < missions.Count; mi++)
            {
                Mission mission = missions[mi];
                if (mission == null || !mission.LoopPlayback)
                    continue;

                if (!TryBuildMissionUnit(
                        mission, trees, committed, indexById, autoLoopIntervalSeconds, bodyInfo,
                        transitedBodyRotationMode,
                        out GhostPlaybackLogic.LoopUnit unit, out int[] memberArray))
                    continue;

                // Owner-index collision across units: only reachable if two looping Missions share a
                // tree (one-per-tree violated upstream). The earlier unit wins its owner slot.
                if (unitsByOwner.ContainsKey(unit.OwnerIndex))
                {
                    ParsekLog.Warn("Mission",
                        $"MissionLoopUnit: mission='{mission.Name}' tree={mission.TreeId} owner index " +
                        $"{unit.OwnerIndex} already owned by another looping unit; skipping (expected " +
                        "one loop per tree)");
                    continue;
                }

                // Member-index collision: keep the first claimant so OwnerByIndex and the unit's
                // MemberIndices never disagree. Defensive - disjoint trees never collide.
                int claimedConflicts = 0;
                for (int k = 0; k < memberArray.Length; k++)
                {
                    int idx = memberArray[k];
                    if (ownerByIndex.ContainsKey(idx))
                        claimedConflicts++;
                    else
                        ownerByIndex[idx] = unit.OwnerIndex;
                }
                if (claimedConflicts > 0)
                    ParsekLog.Warn("Mission",
                        $"MissionLoopUnit: mission='{mission.Name}' tree={mission.TreeId} had " +
                        $"{claimedConflicts} member index(es) already claimed by another looping unit; " +
                        "kept the first claimant (expected one loop per tree)");

                unitsByOwner[unit.OwnerIndex] = unit;
                builtUnits++;
            }

            if (builtUnits == 0)
                return GhostPlaybackLogic.LoopUnitSet.Empty;

            return new GhostPlaybackLogic.LoopUnitSet(unitsByOwner, ownerByIndex);
        }

        /// <summary>
        /// (M-MIS-11 item 1) First-class SINGLE-selection entry point for the
        /// Missions -&gt; Logistics loop-unit seam: builds the one
        /// <see cref="GhostPlaybackLogic.LoopUnit"/> for <paramref name="mission"/>
        /// without the <see cref="GhostPlaybackLogic.LoopUnitSet"/> wrapper.
        /// Byte-identical to <c>Build(new List&lt;Mission&gt; { mission }, ...)</c>
        /// followed by extracting the single unit: it runs the SAME
        /// <c>BuildIndexById</c> + <c>TryBuildMissionUnit</c> pipeline (the
        /// cross-mission owner/member collision handling in <see cref="Build"/>
        /// is unreachable for a one-element list, so skipping it changes
        /// nothing). Returns false (unit = default) when the mission is null,
        /// not looping, its tree is missing, or its selection maps to no
        /// committed members - exactly the cases where the one-element
        /// <see cref="Build"/> returns Empty. The builder's INTERNAL logic is
        /// untouched; this is only a named door for consumers (the route
        /// delivery clock's <c>RouteOrchestrator.ResolveLoopUnit</c>) that were
        /// synthesizing a throwaway one-element list to get one unit out.
        /// Member indices are committed-list indices (the alignment invariant),
        /// so callers must pass the SAME committed snapshot the render seams
        /// use. Pure.
        /// </summary>
        internal static bool TryBuildLoopUnitForSelection(
            Mission mission,
            IReadOnlyList<RecordingTree> trees,
            IReadOnlyList<Recording> committed,
            double autoLoopIntervalSeconds,
            IBodyInfo bodyInfo,
            TransitedBodyRotationMode transitedBodyRotationMode,
            out GhostPlaybackLogic.LoopUnit unit)
        {
            unit = default;
            if (mission == null || !mission.LoopPlayback)
                return false;
            Dictionary<string, int> indexById = BuildIndexById(committed);
            return TryBuildMissionUnit(
                mission, trees, committed, indexById, autoLoopIntervalSeconds, bodyInfo,
                transitedBodyRotationMode, out unit, out int[] _);
        }

        /// <summary>
        /// Builds one span-clock <see cref="GhostPlaybackLogic.LoopUnit"/> for a single looping
        /// Mission. Returns false (and logs why at Verbose) when the tree is missing or the
        /// selection maps to no committed members. <paramref name="indexById"/> is the shared
        /// committed id -> index map (built once by <see cref="Build"/>).
        /// </summary>
        private static bool TryBuildMissionUnit(
            Mission mission,
            IReadOnlyList<RecordingTree> trees,
            IReadOnlyList<Recording> committed,
            Dictionary<string, int> indexById,
            double autoLoopIntervalSeconds,
            IBodyInfo bodyInfo,
            TransitedBodyRotationMode transitedBodyRotationMode,
            out GhostPlaybackLogic.LoopUnit unit,
            out int[] memberArray)
        {
            unit = default;
            memberArray = System.Array.Empty<int>();

            // 1. Resolve its tree by TreeId.
            RecordingTree tree = FindTree(trees, mission.TreeId);
            if (tree == null)
            {
                ParsekLog.Verbose("Mission",
                    $"MissionLoopUnit: mission='{mission.Name}' treeId={mission.TreeId ?? "<null>"} " +
                    "tree not found; no unit");
                return false;
            }

            // 2. Structure + through-line view + composition tree (the intervals the interval-level
            //    start/end-trim selection toggles).
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);
            List<MissionCompositionNode> compRoots = MissionCompositionBuilder.Build(structure);

            // 3-5. Per-vessel render windows from the interval-level selection, mapped to committed
            //      indices + their TRIMMED render window (the vessel window intersected with the
            //      member's own [StartUT, EndUT]; a member entirely outside the window is dropped).
            //      Extracted into ComputeTrimmedMemberWindows so the Missions UI (period-cell span
            //      display + watch target) consumes the IDENTICAL member set + windows this builder
            //      uses - otherwise the UI (which keyed off the legacy ExcludedThroughLineHeadIds)
            //      would ignore the interval-level trims the loop actually applies. With no
            //      exclusions every window spans the whole vessel (no behavior change).
            var memberWindowByIndex = ComputeTrimmedMemberWindows(
                view, compRoots, committed, mission.ExcludedIntervalKeys, indexById,
                out int vesselWindowCount, out int skippedNotCommitted);
            var memberIndices = new List<int>(memberWindowByIndex.Keys);
            if (memberIndices.Count == 0)
            {
                ParsekLog.Verbose("Mission",
                    $"MissionLoopUnit: mission='{mission.Name}' tree={tree.Id} " +
                    $"vessels={vesselWindowCount} no committed members in window; no unit");
                return false;
            }

            // Sort by the TRIMMED render start (the member's effective start), tiebreak by index.
            memberIndices.Sort((a, b) =>
            {
                int cmp = memberWindowByIndex[a].StartUT.CompareTo(memberWindowByIndex[b].StartUT);
                if (cmp != 0)
                    return cmp;
                return a.CompareTo(b);
            });

            // 5. Span = [min trimmed start, max trimmed end] over the members.
            double spanStartUT = double.PositiveInfinity;
            double spanEndUT = double.NegativeInfinity;
            for (int i = 0; i < memberIndices.Count; i++)
            {
                GhostPlaybackLogic.LoopUnit.MemberWindow w = memberWindowByIndex[memberIndices[i]];
                if (w.StartUT < spanStartUT) spanStartUT = w.StartUT;
                if (w.EndUT > spanEndUT) spanEndUT = w.EndUT;
            }

            // 6. Two cadences from the same user input:
            //
            //    (a) Span-clock cadence: never shorter than the span so a SINGLE span instance never
            //        truncates. Auto = span; an explicit period is raised to the span when shorter,
            //        both floored at MinCycleDuration. Consumed by the single-instance scenes (KSC,
            //        Tracking Station) and the flight engine's no-overlap branch.
            //
            //    (b) Overlap cadence: the TRUE launch-to-launch period (Auto = the GLOBAL auto-loop
            //        interval, same as single recordings - NOT the span; an explicit period kept
            //        as-is). Floored at MinCycleDuration, then cap-clamped so ceil(span / cadence)
            //        stays within MaxOverlapMissionInstances (mirrors the per-recording
            //        ComputeEffectiveLaunchCadence cap, but over the SPAN at mission granularity).
            //        When this is shorter than the span the flight engine overlaps the whole mission
            //        with itself; when >= span it falls back to the single span instance.
            double span = spanEndUT - spanStartUT;
            double cadence = mission.LoopTimeUnit == LoopTimeUnit.Auto
                ? Math.Max(span, LoopTiming.MinCycleDuration)
                : Math.Max(Math.Max(mission.LoopIntervalSeconds, span), LoopTiming.MinCycleDuration);

            double rawOverlapPeriod = mission.LoopTimeUnit == LoopTimeUnit.Auto
                ? autoLoopIntervalSeconds
                : mission.LoopIntervalSeconds;
            // ComputeEffectiveLaunchCadence floors at MinCycleDuration and raises only as far as the
            // cap needs; pass the SPAN as the per-instance "duration" so ceil(span/cadence) is the
            // live mission-instance count.
            double overlapCadence = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                rawOverlapPeriod, span, GhostPlayback.MaxOverlapMissionInstances);

            // 7. Owner = earliest-start member (first after the StartUT sort).
            int ownerIndex = memberIndices[0];

            // 7b. Phase anchor: today's behavior is "the UT the loop was enabled at" (the span clock
            //     measures phase from this so re-enabling restarts from the recording's start; an
            //     unset NaN anchor falls back to spanStartUT, the old absolute-phase behavior).
            double baseAnchorUT = double.IsNaN(mission.LoopAnchorUT)
                ? spanStartUT
                : mission.LoopAnchorUT;

            // 7b-i. First-play floor: a looped mission must NEVER relaunch before its first real play
            //       completes - the original recording, which runs [spanStart, spanEnd] and spawns a
            //       real vessel at spanEnd. The span clock (TryComputeSpanLoopUT /
            //       ComputeNewestMissionInstanceSpanLoopUT) never produces an instance before
            //       phaseAnchorUT, so clamping the anchor (and the phase-lock window-search reference
            //       below) to at least spanEndUT is sufficient to guarantee it. In the normal flow
            //       (loop enabled after the recording finished) LoopAnchorUT is already > spanEndUT,
            //       so this is a no-op; it only bites a NaN anchor or a future-dated recording (e.g.
            //       after a career rewind, whose faithful window can otherwise fall before the launch),
            //       where it stops the loop from playing the mission before it ever actually flew.
            double firstPlayEndUT = spanEndUT;
            baseAnchorUT = Math.Max(baseAnchorUT, firstPlayEndUT);

            // 7c. Mission periodicity (Phase 1, Tier 1): when the included config's constraints can
            //      be phase-locked, SNAP the anchor to the next faithful launch (UT0 + k*P at or
            //      after the loop-enable UT) and quantize the cadence to a multiple of P. This is a
            //      STRICT SUPERSET of today's behavior: with no body-info (tests / not yet wired) or
            //      an unsupported config (cross-parent / rendezvous / no constraints to lock) the
            //      solver returns the no-lock sentinel and we keep baseAnchorUT + the raw cadences
            //      EXACTLY as before (byte-identical to the merged #958 looping). Affects ONLY
            //      looping Missions; non-looping ghosts and per-recording auto-loop are untouched.
            double phaseAnchorUT = baseAnchorUT;
            double effectiveCadence = cadence;
            double effectiveOverlapCadence = overlapCadence;
            bool phaseLocked = false;
            MissionRelaunchSchedule relaunchSchedule = null;
            bool scheduleRejectedForOverlap = false;
            PeriodicitySolution solution = default;
            ReaimMissionPlan? reaimPlan = null;
            ReaimWindowPlanner.ReaimWindowSchedule? reaimSchedule = null;
            IReadOnlyList<GhostPlaybackLogic.LoopCut> loiterCuts = null;
            ArrivalHoldPlanner.ArrivalHoldResult arrivalHold = ArrivalHoldPlanner.ArrivalHoldResult.None;
            // Per-loop launch alignment (docs/dev/design-reaim-launch-hold-seam.md, borrow-at-launch /
            // repay-at-SOI-exit): launch delta_N EARLIER so the in-SOI replay runs at the recorded launch-body
            // rotation (the launch->escape seam closes), and repay delta_N as a coast hold at the SOI-exit
            // boundary so everything from the SOI exit onward is UNCHANGED vs baseline. Engaged only inside the
            // re-aim block (re-aim engaged && !pad.Applied && plan.Supported && a valid SOI-exit boundary);
            // NaN period + not-engaged here keeps the unit byte-identical for every other shape, so
            // ComputePerLoopLaunchAdvanceSeconds returns 0 and the span clock is unchanged.
            bool launchHoldEngaged = false;
            double launchHoldRotationPeriod = double.NaN;
            double launchHoldSoiExitUT = double.NaN;
            // Descent trigger (re-aim looped LANDING, docs/dev/plans/reaim-descent-trigger.md): detach the
            // deorbit->reentry->landing clip from the loop clock and re-anchor it to the first rotation-aligned
            // moment after the icon reaches the parking-orbit deorbit point. Engaged only when the destination
            // loiter trim resolves a landing rotation alignment AND the re-aimed transfer arrives early
            // (captureShift < 0, the conic-shift gap). Sentinels keep every other unit byte-identical.
            int[] descentMemberIndices = null;
            double descentRecordedDeorbitUT = double.NaN;
            double descentEndUT = double.NaN;
            double descentRotationPeriod = double.NaN;
            double descentLoiterPeriod = double.NaN;
            double descentCaptureShift = double.NaN;
            // The transfer member's SHIFTED PARKING-conic end (= the destination loiter run end + captureShift =
            // the deorbit point = the start of the first deorbit-transition segment, in the re-aimed display
            // frame). The map-presence segment-lookup clamp boundary, in the SAME frame as conicEnd and the
            // effective-segment lookup effUT, distinct from descentRecordedDeorbitUT (the LAST target-body segment
            // end / descent re-anchor). NaN keeps every non-descent unit byte-identical.
            double descentParkingConicEndUT = double.NaN;
            // The committed index of the member whose OWN segments classified Supported = the DESTINATION transfer
            // member (the one that loiters at the target and owns descentRun / seamUT). The loiter-gap predicate +
            // line-hold gate EXACTLY on this member so a ride-along (e.g. a launch-body-orbit probe in a
            // DIFFERENT/unshifted frame) never fires the clamp. -1 until the supported plan is captured; stays -1
            // on every non-re-aim / declined unit (keeping the predicate byte-identical-off).
            int transferMemberIndex = -1;
            // The renderer's FIRST deorbit-arc polyline leg startUT for the transfer member (UNSHIFTED, same
            // frame as descentRecordedDeorbitUT). Computed below by calling the SAME leg builder the map
            // Driver calls, so the C1 icon-ride gate engages exactly when that leg first draws. NaN keeps
            // every non-descent unit byte-identical.
            double descentFirstDeorbitLegStartUT = double.NaN;
            if (bodyInfo != null)
            {
                ConstraintExtraction extraction = MissionPeriodicity.ExtractConstraints(
                    view, compRoots, committed, mission.ExcludedIntervalKeys, bodyInfo);
                // Reference time for "the next window at or after the loop was enabled": the
                // loop-enable UT (LoopAnchorUT), so the snap is stable across frames (it does not
                // drift with the live clock). NaN anchor -> reference the span start (cycle-0 lands
                // on UT0 itself).
                double referenceUT = double.IsNaN(mission.LoopAnchorUT)
                    ? extraction.UT0
                    : mission.LoopAnchorUT;
                // Same first-play floor as baseAnchorUT: never snap to a faithful window before the
                // first play. NextWindow returns the smallest UT0 + k*P >= referenceUT, so clamping
                // the reference keeps the result a faithful window AND >= spanEndUT.
                referenceUT = Math.Max(referenceUT, firstPlayEndUT);
                solution = MissionPeriodicity.Solve(
                    extraction.Constraints, extraction.Support, extraction.UT0, referenceUT, bodyInfo);
                if (solution.ShouldPhaseLock
                    && !double.IsNaN(solution.NextWindowUT)
                    && !double.IsNaN(solution.P) && solution.P > 0.0)
                {
                    phaseAnchorUT = solution.NextWindowUT;
                    effectiveCadence = QuantizeCadenceToMultipleOfP(cadence, solution.P);
                    effectiveOverlapCadence = QuantizeCadenceToMultipleOfP(overlapCadence, solution.P);
                    phaseLocked = true;

                    // 7d. Zero-drift per-window reschedule (docs/dev/plans/zero-drift-reschedule.md):
                    //     for a DRIFTING multi-constraint incommensurate config, replace the fixed
                    //     cadence with a NON-UNIFORM schedule so each relaunch is independently within
                    //     tolerance instead of accumulating drift. The schedule anchors on the TIGHTEST
                    //     constraint (the pad) for the densest attainable cadence, then THROTTLES down to
                    //     the player's requested relaunch period (minSpacing) - the player picks how
                    //     often to launch, never faster than physics allows. TryBuildRelaunchSchedule
                    //     returns no schedule for single-constraint / tidal-collapse / unsupported
                    //     configs (they keep the fixed cadence above, byte-identical).
                    // minSpacing = the throttle = `cadence` (the player's requested relaunch period,
                    // already raised to at least the span and floored at MinCycleDuration). FLOORING AT
                    // THE SPAN is the non-overlap guarantee: the schedule places consecutive launches
                    // >= minSpacing apart, and minSpacing >= span, so EVERY interval is >= span. That
                    // makes a scheduled unit non-overlapping BY CONSTRUCTION (UnitMemberOverlaps false),
                    // independent of the MinIntervalSeconds probe - which only sampled the first few
                    // launches and could otherwise miss a later short gap (review S1). Auto throttles to
                    // one mission instance per span (faithful windows >= span apart); an explicit period
                    // launches no more often than that. The realistic faithful gap (days for an
                    // inter-body mission) is >> span, so this floor only bites a pathological short-gap
                    // config (where it correctly merges to one-at-a-time single-instance playback).
                    double minSpacing = cadence;
                    // M4b phasing-loiter knob (docs/dev/plans/mission-loiter-knob.md): builder-side
                    // engagement rules 2 and 5 (a phasing run exists on the owner's own segments
                    // before the rendezvous/SOI guard; the extraction UT0 coincides with the span
                    // start). Rules 3/4 (anchor placement, shiftable partition) are checked inside
                    // TryBuildRelaunchSchedule on the same effective constraint list the schedule
                    // uses. A null input simply builds the schedule exactly as before (fail closed).
                    PhasingKnobInput knobInput = BuildPhasingKnobInput(
                        committed, memberIndices, ownerIndex, extraction, spanStartUT, spanEndUT,
                        bodyInfo, mission.Name);
                    if (MissionPeriodicity.TryBuildRelaunchSchedule(
                            extraction.Constraints, extraction.Support, extraction.UT0, referenceUT,
                            bodyInfo, out MissionRelaunchSchedule sched, minSpacing,
                            extraction.LaunchBodyName, transitedBodyRotationMode, knobInput))
                    {
                        // minSpacing >= span guarantees sched.MinIntervalSeconds >= span; this gate is a
                        // defensive belt-and-suspenders that always passes for a built schedule.
                        if (sched.MinIntervalSeconds >= span)
                        {
                            relaunchSchedule = sched;
                            phaseAnchorUT = sched.FirstLaunchUT;             // L_0 (>= the first-play floor)
                            effectiveCadence = Math.Max(span, sched.MinIntervalSeconds);
                            effectiveOverlapCadence = effectiveCadence;       // >= span -> never overlaps
                        }
                        else
                        {
                            scheduleRejectedForOverlap = true;                // (unreachable with the span floor)
                        }
                    }
                }

                // 7e. Re-aim (cross-parent interplanetary): the faithful path reports cross-parent
                //      targets UnsupportedCrossParent (their faithful celestial geometry recurs on the
                //      order of ~1000 Kerbin years - useless for logistics), so phaseLocked stays
                //      false. When that happens, try the re-aim model: classify the recorded SOI chain
                //      as a single-hop interplanetary transfer and, if eligible, REPLACE the recorded
                //      heliocentric geometry with a per-window re-aimed transfer that relaunches every
                //      SYNODIC window (~2 Kerbin years for Kerbin->Duna). The classifier + planner both
                //      fail closed: a same-parent target, a missing heliocentric leg, a multi-hop chain,
                //      or degenerate geometry all leave the mission on today's faithful path (the raw
                //      cadence above), never half-applied. Only the per-window transfer ORIENTATION
                //      varies between windows; the Hohmann tof (hence the recorded span) is constant.
                if (!phaseLocked)
                {
                    ApplyReaim(
                        mission, committed, bodyInfo, memberIndices, memberWindowByIndex,
                        transitedBodyRotationMode, spanStartUT, spanEndUT, referenceUT, extraction, span,
                        ref phaseAnchorUT, ref effectiveCadence, ref effectiveOverlapCadence,
                        ref relaunchSchedule, ref reaimPlan, ref reaimSchedule, ref loiterCuts,
                        ref arrivalHold, ref launchHoldEngaged, ref launchHoldRotationPeriod,
                        ref launchHoldSoiExitUT, ref descentMemberIndices, ref descentRecordedDeorbitUT,
                        ref descentEndUT, ref descentRotationPeriod, ref descentLoiterPeriod,
                        ref descentCaptureShift, ref descentParkingConicEndUT, ref transferMemberIndex,
                        ref descentFirstDeorbitLegStartUT);
                }
            }

            // M4c arrival-amber transition (set/clear once per change): evaluated on EVERY build,
            // not just inside the re-aim branch, so the amber also CLEARS when re-aim disengages,
            // the station vanishes, or the dual constraint resolves (arrivalHold defaults to None
            // with a null AmberReason on all those paths).
            LogArrivalAmberTransition(tree.Id, mission.Name, arrivalHold.AmberReason);

            // 8. Build the unit (carrying the per-member trimmed render windows + the optional
            //    zero-drift schedule; null schedule => the existing uniform-cadence span clock; the
            //    optional re-aim plan + synodic schedule drive the flight engine's per-window transfer
            //    substitution).
            memberArray = memberIndices.ToArray();
            unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex, memberArray, spanStartUT, spanEndUT, effectiveCadence, phaseAnchorUT,
                effectiveOverlapCadence, memberWindowByIndex, relaunchSchedule, reaimPlan, reaimSchedule,
                loiterCuts, arrivalHold.HoldSeconds, arrivalHold.HoldAtUT,
                arrivalHold.AlignPeriodSeconds, arrivalHold.AmberReason,
                launchHoldRotationPeriod, launchHoldEngaged, launchHoldSoiExitUT,
                descentMemberIndices, descentRecordedDeorbitUT, descentEndUT,
                descentRotationPeriod, descentLoiterPeriod, descentCaptureShift,
                descentParkingConicEndUT, transferMemberIndex, descentFirstDeorbitLegStartUT,
                arrivalHold.IsJointHold ? arrivalHold.JointSecondaryPeriodSeconds : double.NaN,
                arrivalHold.IsJointHold ? arrivalHold.JointSecondaryToleranceSeconds : double.NaN,
                arrivalHold.IsJointHold ? arrivalHold.JointMaxWholeHoldPeriods : 0);

            LogMissionUnitSummary(
                mission, tree, memberArray, skippedNotCommitted, spanStartUT, spanEndUT, span,
                effectiveCadence, effectiveOverlapCadence, ownerIndex, relaunchSchedule, phaseAnchorUT,
                phaseLocked, scheduleRejectedForOverlap, baseAnchorUT, solution, cadence, bodyInfo);

            return true;
        }

        /// <summary>
        /// Re-aim (cross-parent interplanetary) — phase extract of the
        /// <c>if (!phaseLocked)</c> body in <see cref="TryBuildMissionUnit"/>. The faithful path
        /// reports cross-parent targets UnsupportedCrossParent (their faithful celestial geometry
        /// recurs on the order of ~1000 Kerbin years - useless for logistics), so phaseLocked stays
        /// false. When that happens, try the re-aim model: classify the recorded SOI chain as a
        /// single-hop interplanetary transfer and, if eligible, REPLACE the recorded heliocentric
        /// geometry with a per-window re-aimed transfer that relaunches every SYNODIC window (~2
        /// Kerbin years for Kerbin->Duna). The classifier + planner both fail closed: a same-parent
        /// target, a missing heliocentric leg, a multi-hop chain, or degenerate geometry all leave
        /// the mission on today's faithful path (the raw cadence above), never half-applied. Only the
        /// per-window transfer ORIENTATION varies between windows; the Hohmann tof (hence the
        /// recorded span) is constant. Single contiguous block, no internal reordering; mutates
        /// anchor / cadence / schedule / plan / hold / launch-hold / descent-trigger state via
        /// <c>ref</c>.
        /// </summary>
        private static void ApplyReaim(
            Mission mission,
            IReadOnlyList<Recording> committed,
            IBodyInfo bodyInfo,
            List<int> memberIndices,
            Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow> memberWindowByIndex,
            TransitedBodyRotationMode transitedBodyRotationMode,
            double spanStartUT,
            double spanEndUT,
            double referenceUT,
            ConstraintExtraction extraction,
            double span,
            ref double phaseAnchorUT,
            ref double effectiveCadence,
            ref double effectiveOverlapCadence,
            ref MissionRelaunchSchedule relaunchSchedule,
            ref ReaimMissionPlan? reaimPlan,
            ref ReaimWindowPlanner.ReaimWindowSchedule? reaimSchedule,
            ref IReadOnlyList<GhostPlaybackLogic.LoopCut> loiterCuts,
            ref ArrivalHoldPlanner.ArrivalHoldResult arrivalHold,
            ref bool launchHoldEngaged,
            ref double launchHoldRotationPeriod,
            ref double launchHoldSoiExitUT,
            ref int[] descentMemberIndices,
            ref double descentRecordedDeorbitUT,
            ref double descentEndUT,
            ref double descentRotationPeriod,
            ref double descentLoiterPeriod,
            ref double descentCaptureShift,
            ref double descentParkingConicEndUT,
            ref int transferMemberIndex,
            ref double descentFirstDeorbitLegStartUT)
        {
            // Classify across ALL members' segments (the mission's combined SOI chain): a real
            // interplanetary mission is usually a CHAIN (launch leg / transfer leg / arrival
            // leg / debris as separate recordings), so the heliocentric transfer lives in a
            // non-owner member. The per-window substitution then re-aims ONLY each member's
            // heliocentric leg(s) (ReaimPlaybackResolver / ReaimSegmentAssembler.
            // ReplaceHeliocentricLeg) and leaves body-relative legs faithful, so classifying
            // across the whole chain and substituting per-member is consistent (no half-apply).
            // Classify PER-MEMBER, not the flattened multi-member gather. A member parked in orbit
            // DURING the transfer (a station, a tug, a jettisoned stage left in LKO) interleaves
            // its orbit segments with the transfer member's heliocentric coast in the flattened
            // startUT-sorted list. That broke both algorithms in playtest: the classifier's
            // backward walk from the target SOI coast hit an interleaved launch-body segment and
            // stopped (transfer = the last coast only -> a too-short tof -> a bogus re-aimed
            // geometry), and the loiter compressor cut the interleaved launch-body loiter whose UT
            // range OVERLAPS the transfer (excising the transfer itself). The transfer is ONE
            // member's continuous heliocentric arc, so classify each member's own segments; the
            // member that yields a supported plan is the re-aim source and ITS segments drive the
            // loiter cuts (only its parking is compressed, never another member's overlapping
            // loiter). Members with no heliocentric leg (the parked station) classify as
            // unsupported and loop faithfully alongside.
            ReaimMissionPlan plan = ReaimMissionPlan.Unsupported(null, "no member yields a re-aim transfer");
            List<OrbitSegment> transferSegments = null;
            int gatheredCount = 0;
            for (int mi = 0; mi < memberIndices.Count; mi++)
            {
                int midx = memberIndices[mi];
                if (midx < 0 || midx >= committed.Count)
                    continue;
                Recording mrec = committed[midx];
                if (mrec == null || mrec.OrbitSegments == null || mrec.OrbitSegments.Count == 0)
                    continue;
                gatheredCount += mrec.OrbitSegments.Count;
                var msegs = new List<OrbitSegment>(mrec.OrbitSegments);
                msegs.Sort((a, b) => a.startUT.CompareTo(b.startUT));
                ReaimMissionPlan mp = ReaimClassifier.Classify(msegs, bodyInfo);
                // Per-member classify verdict: the classifier returns a per-member reason but the
                // gather kept only the chosen-supported member's details, so a "why
                // transferMemberSegs=0" diagnosis had to be reverse-engineered from the recording.
                // Log each member's verdict (member count is bounded) so the decline reason is
                // visible in KSP.log directly.
                if (!SuppressLogging)
                    ParsekLog.Verbose("ReaimDiag",
                        $"mission='{mission.Name}' member#{mi} segs={msegs.Count} " +
                        $"startBody={(msegs.Count > 0 ? msegs[0].bodyName : "none")} " +
                        $"supported={mp.Supported}" +
                        (mp.Supported
                            ? $" target={mp.TargetBody} parking={mp.DepartedFromHeliocentricPark}"
                            : $" reason='{mp.Reason}'"));
                if (mp.Supported && transferSegments == null)
                {
                    plan = mp;
                    transferSegments = msegs;
                    // The destination transfer member's committed index: the member whose OWN segments
                    // classified Supported. This is the canonical transfer-member identity - the SAME
                    // member that drives transferSegments / loiterRuns / descentRun / seamUT below - so
                    // the loiter-gap clamp can gate on it exactly (excluding the ride-along probe).
                    transferMemberIndex = midx;
                    // keep scanning only to finish the gatheredCount tally for the diagnostic
                }
            }

            // Diagnostic: dump the transfer member's segments + the classifier's transfer
            // measurement so a save reload reveals the recorded structure (which segment is the
            // transfer, where any loiter is, what the loiter detector + classifier see). Verbose,
            // one-shot per build.
            LogReaimDiagDump(mission, plan, transferSegments, gatheredCount, bodyInfo);

            if (plan.Supported)
            {
                // Congruent-window schedule: the windows are RecordedDepartureUT + k*synodic
                // (the bodies' relative configuration recurs every synodic period), and each
                // window re-solves the transfer for the target's actual position using the
                // RECORDED tof - so the transfer stays congruent to the recorded one and fits
                // the recorded span exactly. Needs only the two solar orbital periods.
                double pOrigin = bodyInfo.OrbitPeriod(plan.LaunchBody);
                double pTarget = bodyInfo.OrbitPeriod(plan.TargetBody);

                ReaimWindowPlanner.ReaimWindowSchedule sched = ReaimWindowPlanner.Plan(
                    pOrigin, pTarget, plan.RecordedDepartureUT, plan.RecordedTransferTofSeconds,
                    spanStartUT, spanEndUT, referenceUT);

                if (sched.Valid)
                {
                    reaimPlan = plan;
                    reaimSchedule = sched;
                    // The synodic period dwarfs the recorded span, so the cadence is synodic and
                    // the mission is single-instance per window (overlap cadence >= span => never
                    // overlaps). The faithful zero-drift schedule does not apply to re-aim.
                    phaseAnchorUT = sched.PhaseAnchorUT;
                    effectiveCadence = Math.Max(span, sched.CadenceSeconds);
                    effectiveOverlapCadence = effectiveCadence;
                    relaunchSchedule = null;

                    // Loiter compression (docs/dev/plans/reaim-loiter-compression.md): the
                    // recorded mission usually parks for a year or more waiting for the transfer
                    // window. For a SUPPLY-ROUTE loop that loiter must not replay, so excise every
                    // repeated parking orbit down to ~1 revolution. The cuts feed the shared span
                    // clock (TryComputeSpanLoopUT remaps loopUT to skip them); the phase anchor
                    // shifts LATER by the cut excised before the transfer departure so the launch
                    // still lands ~1 orbit before the (unchanged, absolute) synodic window. Empty
                    // cuts (no compressible loiter) leave the clock byte-identical to faithful.
                    List<GhostPlaybackLogic.LoopCut> cuts =
                        ReaimLoiterCompressor.ComputeCuts(transferSegments, bodyInfo.GravParameter);
                    // M-MIS-2 P4 (destination-loiter pre-landing trim): partition the transfer
                    // member's loiter runs so a recorded DESTINATION parking loiter can be
                    // re-timed jointly with the arrival hold (DestinationLoiterTrim) instead of
                    // disabling alignment (the shipped ArrivalHoldPlanner refusal). The
                    // launch-side cuts (runs ending at/before the SOI entry) are built at
                    // keepRevs=1 - byte-identical to today's launch-parking compression - and
                    // `cuts` (the full keepRevs=1 set) remains the byte-identical fallback when
                    // P4 does not apply. Scoped to a destination loiter in the classified
                    // transfer member; a same-launch chain continuation that records the parking
                    // in a SEPARATE member is the documented follow-up (plan B1: gather across
                    // VesselLaunchIdentity.RecordingsShareLaunch members, the M4b pattern).
                    List<ReaimLoiterCompressor.LoiterRun> loiterRuns =
                        ReaimLoiterCompressor.DetectRuns(transferSegments, bodyInfo.GravParameter);
                    List<GhostPlaybackLogic.LoopCut> launchSideCuts =
                        BuildLaunchSideKeepOneCuts(loiterRuns, plan.RecordedArrivalUT);
                    // Apply only when the cuts leave a POSITIVE compressed span. keepRevs >= 1
                    // already guarantees each cutLength < its run duration (so totalCut < span),
                    // but gate on it explicitly to match TryComputeSpanLoopUT's effectiveSpan
                    // check: that gate falls back to the identity remap when totalCut >= span, so
                    // applying the anchor shift here without the same gate would produce a
                    // shifted-but-uncompressed clock in the (unreachable) degenerate case.
                    // Diagnostic: dump each cut (cross-reference start with the seg# dump above to
                    // see which run was excised, and whether the transfer arc was wrongly cut).
                    LogReaimPerCutDump(cuts);

                    if (cuts.Count > 0 && GhostPlaybackLogic.TotalCutLength(cuts) < span)
                    {
                        double cutBeforeDeparture = plan.RecordedDepartureUT
                            - GhostPlaybackLogic.CompressSpanUT(plan.RecordedDepartureUT, cuts);
                        phaseAnchorUT = sched.PhaseAnchorUT + cutBeforeDeparture;
                        loiterCuts = cuts;
                    }

                    // Launch-pad alignment (option 1, departure side). Snap the (now loiter-shifted)
                    // launch to the launch body's recorded rotation phase so the body-fixed ascent
                    // replays from the pad EXACTLY as recorded and feeds the recorded inertial parking
                    // orbit + escape with no seam (the atmo-exit -> circular-orbit misalignment the
                    // playtest flagged). Quantizes the cadence + the schedule's window spacing to the
                    // same sidereal day so EVERY relaunch stays aligned, and moves the departure offset
                    // by the same delta so the window-index <-> launch mapping the resolver uses is
                    // preserved. Identity for a non-rotating launch body.
                    double launchRotationPeriod = bodyInfo.RotationPeriod(plan.LaunchBody);
                    ReaimWindowPlanner.PadAlignResult pad = ReaimWindowPlanner.PadAlignLaunch(
                        phaseAnchorUT, effectiveCadence,
                        sched.FirstDepartureUT, sched.SynodicPeriodSeconds,
                        spanStartUT, launchRotationPeriod, referenceUT);
                    if (pad.Applied)
                    {
                        phaseAnchorUT = pad.PhaseAnchorUT;
                        effectiveCadence = pad.CadenceSeconds;
                        effectiveOverlapCadence = effectiveCadence;
                        sched.FirstDepartureUT = pad.FirstDepartureUT;
                        sched.SynodicPeriodSeconds = pad.SynodicPeriodSeconds;
                        sched.CadenceSeconds = pad.CadenceSeconds;
                        reaimSchedule = sched; // re-store the pad-aligned schedule the resolver reads
                        if (!SuppressLogging)
                        {
                            var pic = CultureInfo.InvariantCulture;
                            ParsekLog.Info("Reaim",
                                $"MissionLoopUnit: mission='{mission.Name}' PAD-ALIGN launch to " +
                                $"{plan.LaunchBody} rotation: siderealDay={launchRotationPeriod.ToString("F1", pic)}s " +
                                $"launchShift={pad.DeltaSeconds.ToString("F1", pic)}s " +
                                $"phaseAnchor={phaseAnchorUT.ToString("R", pic)} " +
                                $"cadence={effectiveCadence.ToString("R", pic)} " +
                                $"(launch-recLaunch)/day=" +
                                $"{((phaseAnchorUT - spanStartUT) / launchRotationPeriod).ToString("F3", pic)}");
                        }
                    }

                    // Per-loop LAUNCH ALIGNMENT gate (docs/dev/design-reaim-launch-hold-seam.md 6.3):
                    // engage the borrow-at-launch / repay-at-SOI-exit shift that closes the
                    // launch->escape render seam ONLY when PadAlignLaunch declined (cadence != synodic,
                    // the span>=synodic regime PadAlignLaunch bails on). When pad.Applied (cadence ==
                    // synodic) the pad is already globally aligned, so it must stay off (no
                    // double-correction). plan.Supported (a re-aim mission with a recorded launch-body
                    // parking orbit preceding the heliocentric leg) is the body-fixed-launch-leg
                    // precondition: a member starting already in orbit or a chained continuation with no
                    // ascent never classifies Supported with a launch-body ParkingOrbit at spanStart, so
                    // it never engages a no-op shift. The SOI-exit boundary (plan.RecordedSoiExitUT, the
                    // launch-body->heliocentric handoff) must be finite and strictly inside the span
                    // before the arrival, since the delta_N repay coast hold is inserted there. T_sid is
                    // the same rotation period PadAlignLaunch consumed; a degenerate (non-rotating)
                    // period makes ComputePerLoopLaunchAdvanceSeconds return 0, so the gate ordering is
                    // safe either way.
                    bool soiExitValid = !double.IsNaN(plan.RecordedSoiExitUT)
                        && !double.IsInfinity(plan.RecordedSoiExitUT)
                        && plan.RecordedSoiExitUT > spanStartUT
                        && plan.RecordedSoiExitUT < plan.RecordedArrivalUT;
                    if (!pad.Applied && plan.Supported && soiExitValid)
                    {
                        launchHoldEngaged = true;
                        launchHoldRotationPeriod = launchRotationPeriod;
                        launchHoldSoiExitUT = plan.RecordedSoiExitUT;
                        if (!SuppressLogging)
                        {
                            var lic = CultureInfo.InvariantCulture;
                            ParsekLog.Info("Reaim",
                                $"MissionLoopUnit: mission='{mission.Name}' LAUNCH HOLD engaged to " +
                                $"{plan.LaunchBody} rotation (PadAlignLaunch declined -> per-loop launch advance / SOI-exit repay; " +
                                $"the boundary-overlap launch render closes the launch->escape seam on EVERY loop, including the " +
                                $"zero-slack loops the cap previously left open, via a secondary in-SOI ghost): " +
                                $"siderealDay={launchRotationPeriod.ToString("F1", lic)}s " +
                                $"soiExit={launchHoldSoiExitUT.ToString("R", lic)} " +
                                $"phaseAnchor={phaseAnchorUT.ToString("R", lic)} " +
                                $"cadence={effectiveCadence.ToString("R", lic)}");
                        }
                    }
                    else if (!pad.Applied && plan.Supported && !SuppressLogging)
                    {
                        var lic = CultureInfo.InvariantCulture;
                        ParsekLog.Verbose("Reaim",
                            $"MissionLoopUnit: mission='{mission.Name}' LAUNCH HOLD declined " +
                            $"(SOI-exit boundary invalid): soiExit={plan.RecordedSoiExitUT.ToString("R", lic)} " +
                            $"spanStart={spanStartUT.ToString("R", lic)} arrival={plan.RecordedArrivalUT.ToString("R", lic)} " +
                            "- staying on faithful in-SOI render (seam remains)");
                    }

                    if (!SuppressLogging)
                    {
                        var ric = CultureInfo.InvariantCulture;
                        double totalCut = GhostPlaybackLogic.TotalCutLength(loiterCuts);
                        double recordedSpan = spanEndUT - spanStartUT;
                        ParsekLog.Info("Reaim",
                            $"MissionLoopUnit: mission='{mission.Name}' ENGAGED re-aim " +
                            $"{plan.LaunchBody}->{plan.TargetBody} via {plan.CommonAncestor}; " +
                            $"{ReaimWindowPlanner.Describe(sched)} " +
                            $"phaseAnchor={phaseAnchorUT.ToString("R", ric)} " +
                            $"cadence={effectiveCadence.ToString("R", ric)} " +
                            $"loiterCuts={(loiterCuts?.Count ?? 0).ToString(ric)} " +
                            $"cutSeconds={totalCut.ToString("F0", ric)} " +
                            $"compressedSpan={(recordedSpan - totalCut).ToString("F0", ric)}" +
                            $"/{recordedSpan.ToString("F0", ric)}");
                    }

                    // Destination-SOI arrival alignment (re-aim cross-parent arrival): the loop-clock
                    // arrival HOLD that defers the in-SOI replay so the destination-side phase recurs
                    // to recorded - the destination's ROTATION at the deorbit for a landing, or the
                    // destination STATION's orbital phase at the SOI entry for an orbit-rendezvous
                    // (M4c Tier 2, T_station for T_rot). None (hold 0) for rotation alignment off /
                    // an unsupported destination (incl. the D8 dual-constraint shapes, which carry
                    // the amber reason) / an orbit-only no-station arrival, leaving the span clock
                    // byte-identical. extraction is in scope; phaseAnchorUT + loiterCuts are final
                    // (post pad-align).
                    // M-MIS-2 P4: a Supported landing whose destination parking loiter would
                    // otherwise trip the ArrivalHoldPlanner refusal takes the joint trim+hold
                    // path - re-time the destination loiter (keepRevs) so the deorbit aligns,
                    // and assemble the final cuts = launch-side cuts + the re-timed destination
                    // cut. EVERY other shape (no destination loiter, station, orbit-only, Drop,
                    // unsupported, degenerate) returns None and falls through to the shipped
                    // ComputeArrivalHold with `loiterCuts == cuts`, byte-identical to today.
                    DestinationLoiterTrim.DestinationLoiterTrimResult destTrim =
                        TrySolveDestinationLoiterTrim(
                            extraction, plan, loiterRuns, launchSideCuts, phaseAnchorUT,
                            spanStartUT, span, transitedBodyRotationMode, bodyInfo);
                    if (destTrim.Applied)
                    {
                        // Reassigning loiterCuts here is consistent with the cutBeforeDeparture
                        // phase-anchor shift already computed from the full `cuts`: every cut that
                        // differs between `cuts` and `p4Cuts` (the dropped post-arrival keepRevs=1
                        // dest cut, the re-timed dest cut) starts at/after the SOI entry, hence
                        // after the departure, so it contributes 0 to CompressSpanUT(departureUT)
                        // and the shift is identical either way.
                        var p4Cuts = new List<GhostPlaybackLogic.LoopCut>(launchSideCuts);
                        if (destTrim.HasDestinationCut)
                            p4Cuts.Add(destTrim.DestinationCut);
                        loiterCuts = p4Cuts;
                        // None-based construction so the joint-hold fields keep their documented
                        // NaN/0 "not joint" sentinels (a bare object initializer would zero them).
                        arrivalHold = ArrivalHoldPlanner.ArrivalHoldResult.None;
                        arrivalHold.HoldSeconds = destTrim.HoldSeconds;
                        arrivalHold.HoldAtUT = destTrim.HoldAtUT;
                        arrivalHold.AlignPeriodSeconds = destTrim.AlignPeriodSeconds;
                        arrivalHold.Applied = true;
                        if (!SuppressLogging)
                        {
                            var aic = CultureInfo.InvariantCulture;
                            ParsekLog.Info("Reaim",
                                $"MissionLoopUnit: mission='{mission.Name}' ARRIVAL HOLD dest={plan.TargetBody} " +
                                $"kind=rotation keepRevs={destTrim.DestinationKeepRevs.ToString(aic)}/" +
                                $"{destTrim.DestinationWholeRevs.ToString(aic)} " +
                                $"cutLen={(destTrim.HasDestinationCut ? destTrim.DestinationCut.LengthSeconds.ToString("F0", aic) : "0")}s " +
                                $"Talign={destTrim.AlignPeriodSeconds.ToString("R", aic)}s " +
                                $"hold={destTrim.HoldSeconds.ToString("R", aic)}s at " +
                                $"recordedArrivalUT={plan.RecordedArrivalUT.ToString("R", aic)} " +
                                $"(re-timed destination loiter, mode={transitedBodyRotationMode})");
                        }
                    }
                    else
                    {
                        // Loop slack budget for the JOINT hold (D8 landing+station): the idle gap the
                        // per-loop hold can spend without the clock's defensive clamp truncating it.
                        double jointSlackSeconds = effectiveCadence
                            - (span - GhostPlaybackLogic.TotalCutLength(loiterCuts));
                        arrivalHold = ArrivalHoldPlanner.ComputeArrivalHold(
                            extraction.Constraints, plan.TargetBody, plan.RecordedArrivalUT,
                            transitedBodyRotationMode, phaseAnchorUT, spanStartUT, loiterCuts, bodyInfo,
                            windowSpacingSeconds: sched.SynodicPeriodSeconds,
                            launchBodyName: plan.LaunchBody,
                            loopSlackSeconds: jointSlackSeconds);
                        if (arrivalHold.Applied && !SuppressLogging)
                        {
                            var aic = CultureInfo.InvariantCulture;
                            string kind = arrivalHold.IsConfigHold
                                ? "config"
                                : arrivalHold.IsJointHold
                                    ? "joint" : (arrivalHold.IsStationHold ? "station" : "rotation");
                            int configMoons = 0;
                            if (arrivalHold.IsConfigHold)
                            {
                                for (int ci = 0; ci < extraction.Constraints.Count; ci++)
                                {
                                    PhaseConstraint cc = extraction.Constraints[ci];
                                    if (cc.Kind == ConstraintKind.Orbital
                                        && bodyInfo?.ReferenceBodyName(cc.BodyName) == plan.TargetBody)
                                        configMoons++;
                                }
                            }
                            ParsekLog.Info("Reaim",
                                $"MissionLoopUnit: mission='{mission.Name}' ARRIVAL HOLD dest={plan.TargetBody} " +
                                $"kind={kind} " +
                                (arrivalHold.IsStationHold
                                    ? $"pid={arrivalHold.AlignAnchorPid.ToString(aic)} "
                                    : "") +
                                $"Talign={arrivalHold.AlignPeriodSeconds.ToString("R", aic)}s " +
                                (arrivalHold.IsJointHold
                                    ? $"Trot={arrivalHold.JointSecondaryPeriodSeconds.ToString("R", aic)}s " +
                                      $"rotTol={arrivalHold.JointSecondaryToleranceSeconds.ToString("F1", aic)}s " +
                                      $"budget={arrivalHold.JointMaxWholeHoldPeriods.ToString(aic)} " +
                                      $"k={arrivalHold.JointChosenWindowK.ToString(aic)} " +
                                      $"residual={arrivalHold.JointResidualSeconds.ToString("F1", aic)}s "
                                    : "") +
                                (arrivalHold.IsConfigHold
                                    ? $"moons={configMoons.ToString(aic)} " +
                                      $"alignedWindows={arrivalHold.ConfigAlignedWindowHorizon.ToString(aic)} " +
                                      $"window1Residual={arrivalHold.ConfigFirstWindowResidualSeconds.ToString("F1", aic)}s "
                                    : "") +
                                $"hold={arrivalHold.HoldSeconds.ToString("R", aic)}s at " +
                                $"recordedArrivalUT={plan.RecordedArrivalUT.ToString("R", aic)} " +
                                $"(aligns the {(arrivalHold.IsConfigHold ? "multi-moon configuration" : arrivalHold.IsJointHold ? "station orbital + landing rotation" : arrivalHold.IsStationHold ? "station orbital" : "deorbit rotation")} " +
                                $"phase{(arrivalHold.IsJointHold || arrivalHold.IsConfigHold ? "s" : "")}, mode={transitedBodyRotationMode})");
                        }
                    }

                    // DESCENT TRIGGER engagement (docs/dev/plans/reaim-descent-trigger.md). A re-aim
                    // looped landing whose re-aimed transfer arrives EARLIER than recorded
                    // (captureShift < 0) shifts the destination parking/capture conics ~|captureShift|
                    // earlier (PR #1177) to meet the early transfer, opening a gap before the body-fixed
                    // descent - the icon used to sit frozen across it while the descent drew at the wrong
                    // rotation. Engage the trigger so the descent member's head detaches from the loop
                    // clock and re-anchors to the first rotation-aligned moment after the icon reaches the
                    // parking-orbit deorbit point. Gated DIRECTLY on the destination parking loiter run,
                    // NOT on the arrival-hold path: a landing whose recording extracts no destination
                    // ROTATION constraint still mis-renders, and that path then never fires (the live log
                    // showed zero ARRIVAL HOLD lines for the failing subject). captureShift is the
                    // build-time equivalent of the per-window resolver value (newArrival - recordedArrival
                    // ≈ HohmannTof - recordedTof), ~constant across loops. Sentinels (-1 / NaN) keep every
                    // other unit byte-identical.
                    ReaimLoiterCompressor.LoiterRun descentRun = default(ReaimLoiterCompressor.LoiterRun);
                    bool foundDescentRun = false;
                    for (int dr = 0; dr < loiterRuns.Count; dr++)
                    {
                        ReaimLoiterCompressor.LoiterRun run = loiterRuns[dr];
                        // The destination parking loiter = the target-body run after arrival with the most
                        // recorded revolutions (the deorbit follows it). A launch-side depot run ends
                        // before arrival and is excluded.
                        if (run.BodyName == plan.TargetBody && run.EndUT > plan.RecordedArrivalUT
                            && run.WholeRevs >= 1
                            && !double.IsNaN(run.PeriodSeconds) && run.PeriodSeconds > 0.0
                            && (!foundDescentRun || run.WholeRevs > descentRun.WholeRevs))
                        {
                            descentRun = run;
                            foundDescentRun = true;
                        }
                    }
                    double descTrot = bodyInfo.RotationPeriod(plan.TargetBody);
                    double descGeomTof = TransferWindowMath.HohmannTransferTimeSeconds(
                        ReaimClassifier.HeliocentricSemiMajorAxis(plan.LaunchBody, plan.CommonAncestor, bodyInfo),
                        ReaimClassifier.HeliocentricSemiMajorAxis(plan.TargetBody, plan.CommonAncestor, bodyInfo),
                        bodyInfo.GravParameter(plan.CommonAncestor));
                    double descCaptureShift = double.IsNaN(descGeomTof)
                        ? double.NaN : descGeomTof - plan.RecordedTransferTofSeconds;

                    // SEAM = the transfer member's last (max-endUT) non-predicted target-body OrbitSegment
                    // end. This is where the in-orbit capture/parking/deorbit-transition conics end and the
                    // separate body-fixed approach members begin; it is the descent clip's re-anchor point
                    // (RecordedDeorbitUT). NOT descentRun.EndUT (the PARKING-loiter end, which is earlier and
                    // mid-conic - the deorbit-transition orbits seg#13-17 continue past it). conicEnd =
                    // RecordedDeorbitUT + captureShift then lands on the SHIFTED conic's end (PR #1177).
                    double seamUT = double.NaN;
                    if (transferSegments != null)
                    {
                        for (int s = 0; s < transferSegments.Count; s++)
                        {
                            OrbitSegment seg = transferSegments[s];
                            if (!seg.isPredicted && seg.bodyName == plan.TargetBody
                                && (double.IsNaN(seamUT) || seg.endUT > seamUT))
                                seamUT = seg.endUT;
                        }
                    }

                    // DESCENT MEMBER SET = the post-parking body-fixed approach members (the chain tail on
                    // the target body starting at/after the seam). A re-aim looped arrival continues PAST the
                    // destination arrival as one or more SEPARATE committed recordings (each a member); they
                    // all share ONE re-anchored clip and ONE trigger, each rendering only its own window
                    // slice. Identification is pure (DescentTrigger.SelectDescentMemberIndices); the EPS
                    // tolerates sub-second seam jitter.
                    const double descentSeamEpsSeconds = 1.0;
                    var descentArrivalInfos =
                        new List<Parsek.Reaim.DescentTrigger.MemberArrivalInfo>(memberIndices.Count);
                    for (int mi2 = 0; mi2 < memberIndices.Count; mi2++)
                    {
                        int midx = memberIndices[mi2];
                        if (midx < 0 || midx >= committed.Count)
                            continue;
                        double mStart = memberWindowByIndex.TryGetValue(
                                midx, out GhostPlaybackLogic.LoopUnit.MemberWindow mw)
                            ? mw.StartUT
                            : committed[midx].StartUT;
                        descentArrivalInfos.Add(new Parsek.Reaim.DescentTrigger.MemberArrivalInfo(
                            midx, mStart, MemberStartBody(committed[midx])));
                    }
                    int[] descentSet = Parsek.Reaim.DescentTrigger.SelectDescentMemberIndices(
                        descentArrivalInfos, seamUT, plan.TargetBody, descentSeamEpsSeconds);

                    // The descent set's per-member windows (sorted by start) drive DescentEndUT and the
                    // contiguity / seam guards. DescentEndUT = the LAST descent member's recorded EndUT
                    // (NOT spanEndUT, which can include route-excluded intervals).
                    var descentWindows =
                        new List<GhostPlaybackLogic.LoopUnit.MemberWindow>(descentSet.Length);
                    for (int k = 0; k < descentSet.Length; k++)
                        if (memberWindowByIndex.TryGetValue(
                                descentSet[k], out GhostPlaybackLogic.LoopUnit.MemberWindow mw2))
                            descentWindows.Add(mw2);
                    descentWindows.Sort((a, b) => a.StartUT.CompareTo(b.StartUT));

                    // Build-time engage/decline decision (contiguity / seam-match / conic-region safety
                    // gates) extracted to the pure, xUnit-tested DescentTrigger.EvaluateEngage so a
                    // regression in any guard is caught by CI. descentWindows is ascending by StartUT.
                    Parsek.Reaim.DescentTrigger.DescentEngageDecision descentDecision =
                        Parsek.Reaim.DescentTrigger.EvaluateEngage(
                            foundDescentRun, descentWindows, descentSet.Length, seamUT,
                            descCaptureShift, descTrot, plan.RecordedSoiExitUT, descentSeamEpsSeconds);
                    double descentSetEndUT = descentDecision.SetEndUT;
                    double descentSetMinStartUT = descentDecision.SetMinStartUT;
                    double conicEndRecorded = seamUT + descCaptureShift; // for the engage log line below
                    bool descentEngage = descentDecision.Engage;
                    if (descentEngage)
                    {
                        descentMemberIndices = descentSet;
                        descentRecordedDeorbitUT = seamUT;
                        descentEndUT = descentSetEndUT;
                        descentRotationPeriod = descTrot;
                        descentLoiterPeriod = descentRun.PeriodSeconds;
                        descentCaptureShift = descCaptureShift;
                        // PARKING-conic end (Layer A of the loiter-gap render fix): the SHIFTED
                        // destination loiter run end. A loiter run (ReaimLoiterCompressor.DetectRuns)
                        // ends at the first > 5% sma step, so descentRun.EndUT is the parking conic's
                        // last sample = the deorbit point = the start of the first deorbit-transition
                        // OrbitSegment - but descentRun comes from the transfer member's RAW recorded
                        // segments, so descentRun.EndUT is in the UNSHIFTED recorded frame. The
                        // map-presence segment lookup runs against the RE-AIMED (captureShift-SHIFTED)
                        // effective segments at the loop-shifted sample UT, so the clamp boundary MUST be
                        // in the SHIFTED frame too: descentRun.EndUT + descCaptureShift. This is the SAME
                        // frame as conicEnd (= seamUT + descCaptureShift, the deorbit-arc end) and the
                        // same frame the effective-segment lookup effUT runs in; it lands on the SHIFTED
                        // parking-conic end (one segment EARLIER than conicEnd's deorbit-arc end), so the
                        // deorbit arc no longer leaks as the loiter orbit. Distinct from
                        // descentRecordedDeorbitUT = seamUT (the LAST target-body segment end, where the
                        // deorbit-transition conics finish - too late). descCaptureShift is the
                        // build-time captureShift (= descentCaptureShift above), so the shift sign/magnitude
                        // matches conicEnd exactly.
                        descentParkingConicEndUT = descentRun.EndUT + descCaptureShift;
                        // Frame-mismatch guard (the invariant behind two failed loiter fixes): the
                        // parking-conic end and conicEnd are BOTH in the shifted frame, and the parking
                        // conic must end BEFORE the deorbit-arc end (parkingConicEnd < conicEnd). If a
                        // future edit drops the captureShift from parkingConicEnd it lands in the unshifted
                        // ~2570 frame and EXCEEDS the shifted conicEnd, which this catches. Warn-only; it
                        // can only fire on a genuine frame regression (would have caught commit 0ba10f594).
                        if (!double.IsNaN(descentParkingConicEndUT) && !double.IsNaN(conicEndRecorded)
                            && descentParkingConicEndUT >= conicEndRecorded)
                        {
                            ParsekLog.Warn("ReaimDescent", string.Format(CultureInfo.InvariantCulture,
                                "MissionLoopUnit: mission='{0}' parking-conic-end FRAME MISMATCH: "
                                + "parkingConicEnd={1:R} >= conicEnd={2:R} (must be EARLIER, in the same "
                                + "shifted frame; likely a missing captureShift on parkingConicEnd).",
                                mission.Name, descentParkingConicEndUT, conicEndRecorded));
                        }

                        // C1 engage bound (loiter-orbit-gap fix): the recorded UT of the FIRST
                        // deorbit-arc polyline leg the MAP renderer draws for this transfer member.
                        // Source it from the renderer's OWN leg builder over the SAME recording so it
                        // equals the Driver's leg.startUT to the UT — sourcing it from the first
                        // below-surface OrbitSegment.startUT instead is the WRONG quantity (the
                        // renderer's leg.startUT is the first non-orbital SAMPLE UT, not a segment
                        // boundary). A bodyInfo.Radius-backed surface provider reproduces
                        // IsOrbitSegmentBelowSurface byte-for-byte (same CelestialBody.Radius); a NULL
                        // gap sampler is safe (FillFramelessGapsFromConics only inserts points INTERIOR
                        // to a gap, never before the first sample, so leg.startUT is identical). Select
                        // the FIRST (min-startUT) leg matching the renderer's deorbit-tail predicate
                        // (leg.bodyName == TargetBody && leg.endUT > seam + captureShift &&
                        // leg.endUT <= seam + 1s) — the exact window the renderer pairs to the
                        // re-anchored deorbit head.
                        if (transferMemberIndex >= 0 && transferMemberIndex < committed.Count
                            && committed[transferMemberIndex] != null)
                        {
                            string targetBody = plan.TargetBody;
                            Parsek.Display.GhostTrajectoryPolylineRenderer.BodySurfaceProvider surf =
                                (string bn, out Parsek.Display.GhostTrajectoryPolylineRenderer.BodySurfaceInfo bi) =>
                                {
                                    bi = default(Parsek.Display.GhostTrajectoryPolylineRenderer.BodySurfaceInfo);
                                    double r = bodyInfo.Radius(bn);
                                    if (double.IsNaN(r) || double.IsInfinity(r) || r <= 0.0)
                                        return false;
                                    bi.radius = r;
                                    return true;
                                };
                            var deorbitLegs = Parsek.Display.GhostTrajectoryPolylineRenderer
                                .BuildLegsForRecording(committed[transferMemberIndex], surf, null);
                            const double deorbitTailEpsSeconds = 1.0; // matches ResolveTransferLegHeadUT eps
                            // The deorbit-arc leg the renderer rides as the descent tail is the one ENDING
                            // AT the seam (the transfer member's recorded trajectory terminates there, where
                            // the descent set takes over). Select it by MAX endUT <= seam+eps — NOT the
                            // min-startUT leg in a wide (seam+captureShift, seam] window, which grabbed an
                            // earlier ~12-parking-period approach leg and engaged C1 ~51k s too early (the
                            // loiter-line regression: the icon left the parking conic across most of the
                            // loiter, killing the parking line while no deorbit leg had drawn yet).
                            if (deorbitLegs != null)
                                descentFirstDeorbitLegStartUT = SelectDeorbitTailLegStartUT(
                                    deorbitLegs, targetBody, seamUT, deorbitTailEpsSeconds);
                            // Frame sanity (warn-only): the leg start must sit at or before the seam in
                            // the same UNSHIFTED frame; a value past the seam means a frame mix-up.
                            if (!double.IsNaN(descentFirstDeorbitLegStartUT)
                                && descentFirstDeorbitLegStartUT > seamUT + deorbitTailEpsSeconds)
                            {
                                ParsekLog.Warn("ReaimDescent", string.Format(CultureInfo.InvariantCulture,
                                    "MissionLoopUnit: mission='{0}' firstDeorbitLegStart={1:R} > seam={2:R} "
                                    + "(must be <= seam in the unshifted frame); ignoring (C1 falls back).",
                                    mission.Name, descentFirstDeorbitLegStartUT, seamUT));
                                descentFirstDeorbitLegStartUT = double.NaN;
                            }
                        }

                        if (!SuppressLogging)
                        {
                            var dic = CultureInfo.InvariantCulture;
                            ParsekLog.Info("ReaimDescent",
                                $"MissionLoopUnit: mission='{mission.Name}' DESCENT TRIGGER engaged " +
                                $"dest={plan.TargetBody} members=[{string.Join(",", descentSet)}] " +
                                $"transferMember={transferMemberIndex.ToString(dic)} " +
                                $"deorbit(seam)={descentRecordedDeorbitUT.ToString("R", dic)} " +
                                $"setMinStart={descentSetMinStartUT.ToString("R", dic)} " +
                                $"descentEnd={descentEndUT.ToString("R", dic)} " +
                                $"Trot={descentRotationPeriod.ToString("R", dic)}s " +
                                $"Tpark={descentLoiterPeriod.ToString("R", dic)}s " +
                                $"parkRevs={descentRun.WholeRevs.ToString(dic)} " +
                                $"parkingConicEnd={descentParkingConicEndUT.ToString("R", dic)} " +
                                $"firstDeorbitLegStart={descentFirstDeorbitLegStartUT.ToString("R", dic)} " +
                                $"captureShift={descentCaptureShift.ToString("R", dic)}s " +
                                $"conicEnd={conicEndRecorded.ToString("R", dic)} " +
                                $"(geomTof={descGeomTof.ToString("F0", dic)} recordedTof={plan.RecordedTransferTofSeconds.ToString("F0", dic)})");
                            // Co-engagement note (review m4): the descent trigger and the per-loop arrival
                            // hold both rotation-align the destination via different mechanisms. The math is
                            // disjoint (conicEnd sits pre-arrival, so the hold never perturbs entryUT), but
                            // log the combination so an in-game capture proves which governs the approach.
                            if (arrivalHold.Applied)
                                ParsekLog.Info("ReaimDescent",
                                    $"MissionLoopUnit: mission='{mission.Name}' descent trigger co-engaged with " +
                                    $"ARRIVAL HOLD (hold={arrivalHold.HoldSeconds.ToString("R", dic)}s at " +
                                    $"{arrivalHold.HoldAtUT.ToString("R", dic)}); descent governs the post-parking " +
                                    "approach head, the hold only the transfer/parking icon.");
                        }
                    }
                    else if (!SuppressLogging)
                    {
                        var dic = CultureInfo.InvariantCulture;
                        ParsekLog.Verbose("ReaimDescent",
                            $"MissionLoopUnit: mission='{mission.Name}' descent trigger NOT engaged " +
                            $"(foundRun={foundDescentRun} captureShift={descCaptureShift.ToString("R", dic)} " +
                            $"Trot={descTrot.ToString("R", dic)} seam={seamUT.ToString("R", dic)} " +
                            $"descentSet=[{string.Join(",", descentSet)}] setEnd={descentSetEndUT.ToString("R", dic)} " +
                            $"contiguous={descentDecision.Contiguous} startMatchesSeam={descentDecision.StartMatchesSeam} " +
                            $"conicInRegion={descentDecision.ConicInRegion} setMinStart={descentSetMinStartUT.ToString("R", dic)})");
                    }
                }
                else if (!SuppressLogging)
                {
                    ParsekLog.Verbose("Reaim",
                        $"MissionLoopUnit: mission='{mission.Name}' re-aim eligible " +
                        $"({plan.LaunchBody}->{plan.TargetBody}) but window plan invalid " +
                        $"({sched.Reason}); staying faithful");
                }
            }
            else if (!SuppressLogging)
            {
                ParsekLog.Verbose("Reaim",
                    $"MissionLoopUnit: mission='{mission.Name}' not re-aim ({plan.Reason}); faithful");
            }
        }

        /// <summary>
        /// Re-aim diagnostic dump (phase extract of <see cref="TryBuildMissionUnit"/>): dumps the
        /// transfer member's segments + the classifier's transfer measurement, Verbose, one-shot per
        /// build. Verbatim — same gate, same lines.
        /// </summary>
        private static void LogReaimDiagDump(
            Mission mission,
            ReaimMissionPlan plan,
            List<OrbitSegment> transferSegments,
            int gatheredCount,
            IBodyInfo bodyInfo)
        {
            if (!SuppressLogging && bodyInfo != null)
            {
                var dic = CultureInfo.InvariantCulture;
                List<OrbitSegment> dumpSegs = transferSegments ?? new List<OrbitSegment>();
                ParsekLog.Verbose("ReaimDiag",
                    $"mission='{mission.Name}' gatheredSegs={gatheredCount} transferMemberSegs={dumpSegs.Count} " +
                    $"plan.Supported={plan.Supported} reason='{plan.Reason}'" +
                    (plan.Supported
                        ? $" departUT={plan.RecordedDepartureUT.ToString("F0", dic)}" +
                          $" arrivalUT={plan.RecordedArrivalUT.ToString("F0", dic)}" +
                          $" tof={plan.RecordedTransferTofSeconds.ToString("F0", dic)}" +
                          $" ancestor={plan.CommonAncestor}" +
                          $" parking={plan.DepartedFromHeliocentricPark}"
                        : ""));
                int logged = 0;
                for (int si = 0; si < dumpSegs.Count && logged < 60; si++, logged++)
                {
                    OrbitSegment s = dumpSegs[si];
                    double per = ReaimLoiterCompressor.OrbitalPeriod(
                        s.semiMajorAxis, bodyInfo.GravParameter(s.bodyName));
                    double dur = s.endUT - s.startUT;
                    ParsekLog.Verbose("ReaimDiag",
                        $"  seg#{si} {s.bodyName} [{s.startUT.ToString("F0", dic)},{s.endUT.ToString("F0", dic)}]" +
                        $" dur={dur.ToString("F0", dic)} sma={s.semiMajorAxis.ToString("R", dic)}" +
                        $" period={(double.IsNaN(per) ? "NaN" : per.ToString("F0", dic))}" +
                        $" revs={(double.IsNaN(per) || per <= 0.0 ? "-" : (dur / per).ToString("F2", dic))}" +
                        $" pred={s.isPredicted}");
                }
            }
        }

        /// <summary>
        /// Re-aim per-cut diagnostic dump (phase extract of <see cref="TryBuildMissionUnit"/>): dumps
        /// each loiter cut, Verbose. Verbatim — same gate, same lines.
        /// </summary>
        private static void LogReaimPerCutDump(List<GhostPlaybackLogic.LoopCut> cuts)
        {
            if (!SuppressLogging)
            {
                var cic = CultureInfo.InvariantCulture;
                for (int ci = 0; ci < cuts.Count && ci < 30; ci++)
                    ParsekLog.Verbose("ReaimDiag",
                        $"  cut#{ci} start={cuts[ci].StartUT.ToString("F0", cic)}" +
                        $" len={cuts[ci].LengthSeconds.ToString("F0", cic)}" +
                        $" end={cuts[ci].EndUT.ToString("F0", cic)}");
            }
        }

        /// <summary>
        /// Unit summary + PhaseLock APPLIED/SKIPPED log (phase extract of
        /// <see cref="TryBuildMissionUnit"/>). Verbatim — same gate, same lines.
        /// </summary>
        private static void LogMissionUnitSummary(
            Mission mission,
            RecordingTree tree,
            int[] memberArray,
            int skippedNotCommitted,
            double spanStartUT,
            double spanEndUT,
            double span,
            double effectiveCadence,
            double effectiveOverlapCadence,
            int ownerIndex,
            MissionRelaunchSchedule relaunchSchedule,
            double phaseAnchorUT,
            bool phaseLocked,
            bool scheduleRejectedForOverlap,
            double baseAnchorUT,
            PeriodicitySolution solution,
            double cadence,
            IBodyInfo bodyInfo)
        {
            if (!SuppressLogging)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Verbose("Mission",
                    $"MissionLoopUnit: mission='{mission.Name}' tree={tree.Id} " +
                    $"members={memberArray.Length} skipped={skippedNotCommitted} " +
                    $"span=[{spanStartUT.ToString("R", ic)},{spanEndUT.ToString("R", ic)}] " +
                    $"spanDur={span.ToString("R", ic)} unit={mission.LoopTimeUnit} " +
                    $"cadence={effectiveCadence.ToString("R", ic)} " +
                    $"overlapCadence={effectiveOverlapCadence.ToString("R", ic)} " +
                    $"overlaps={(effectiveOverlapCadence < span ? "yes" : "no")} owner={ownerIndex} " +
                    $"scheduled={(relaunchSchedule != null ? "yes" : "no")} " +
                    $"phaseAnchor={phaseAnchorUT.ToString("R", ic)}");

                // Phase-lock applied vs skipped (design Diagnostic Logging). APPLIED stays Info
                // (a genuine, noteworthy lock). SKIPPED is VerboseRateLimited per-tree: it is a
                // STATIC "unsupported config, keeping today's behavior" verdict that recurs on
                // every rebuild, so it must never be Info (that spams even with verbose OFF) nor
                // fire on every rebuild - rate-limited so verbose-on debugging still sees it.
                if (phaseLocked)
                {
                    // When a zero-drift schedule is attached it is AUTHORITATIVE: the headline
                    // fixedCadence* values below are the FIXED-CADENCE fallback verdict (what Solve()
                    // returns as the diagnostic), which the per-window reschedule supersedes. Surface
                    // the schedule's OWN aggregate tolerance (worst residual + all-within flag folded
                    // across the cached launches) so the log reflects the windows actually flown, not
                    // the unused fallback. A drifting same-parent Mun config typically shows
                    // fixedCadenceWithinTol=no but scheduleWithinTol=yes - the schedule, not the
                    // fallback, runs.
                    string scheduleNote = relaunchSchedule != null
                        ? $" zeroDrift=yes firstLaunch={relaunchSchedule.FirstLaunchUT.ToString("R", ic)} " +
                          $"minInterval={relaunchSchedule.MinIntervalSeconds.ToString("R", ic)} " +
                          $"scheduleWithinTol={(relaunchSchedule.AllLaunchesWithinTolerance ? "yes" : "no")} " +
                          $"scheduleWorstResidual={relaunchSchedule.WorstResidualSeconds.ToString("R", ic)}"
                        : (scheduleRejectedForOverlap
                            ? " zeroDrift=rejected-would-overlap-keeping-fixed-cadence"
                            : " zeroDrift=no");
                    ParsekLog.Info("MissionPeriodicity",
                        $"PhaseLock APPLIED: mission='{mission.Name}' tree={tree.Id} " +
                        $"anchor {baseAnchorUT.ToString("R", ic)}->{phaseAnchorUT.ToString("R", ic)} " +
                        $"P={solution.P.ToString("R", ic)} method={solution.Method} " +
                        $"cadence {cadence.ToString("R", ic)}->{effectiveCadence.ToString("R", ic)} " +
                        $"fixedCadenceResidual={solution.ResidualSeconds.ToString("R", ic)} " +
                        $"fixedCadenceWithinTol={(solution.WithinTolerance ? "yes" : "no")}" + scheduleNote);
                }
                else if (bodyInfo != null)
                {
                    ParsekLog.VerboseRateLimited("MissionPeriodicity",
                        "phaselock-skipped-" + (tree.Id ?? "<null>"),
                        $"PhaseLock SKIPPED: mission='{mission.Name}' tree={tree.Id} " +
                        $"support={solution.Support} keeping anchor={phaseAnchorUT.ToString("R", ic)} " +
                        "(today's behavior)", 10.0);
                }
            }
        }

        /// <summary>
        /// Selects the deorbit-arc leg the map renderer rides as the descent tail: the leg ENDING AT the
        /// seam (the transfer member's recorded trajectory terminates there, where the descent set takes
        /// over) — i.e. the leg with the MAXIMUM <c>endUT</c> that is at/below <paramref name="seamUT"/> +
        /// <paramref name="epsSeconds"/>, on <paramref name="targetBody"/>. Returns its <c>startUT</c>, or
        /// NaN when no such leg exists. This is the C1 icon-engage bound
        /// (<see cref="GhostPlaybackLogic.LoopUnit.FirstDeorbitLegStartUT"/>): selecting by max-endUT (the
        /// seam-terminating leg) instead of min-startUT in the wide (seam+captureShift, seam] window is what
        /// keeps C1 from engaging ~a dozen parking periods early on an earlier approach leg — the loiter-line
        /// regression where the icon left the parking conic across most of the loiter and killed the parking
        /// line while no deorbit leg had drawn yet. Pure; xUnit-testable without Unity.
        /// </summary>
        internal static double SelectDeorbitTailLegStartUT(
            IReadOnlyList<Parsek.Display.GhostTrajectoryPolylineRenderer.LegPolyline> legs,
            string targetBody, double seamUT, double epsSeconds)
        {
            if (legs == null)
                return double.NaN;
            double bestStart = double.NaN;
            double bestEnd = double.NaN;
            for (int i = 0; i < legs.Count; i++)
            {
                var lg = legs[i];
                if (!string.Equals(lg.bodyName, targetBody, StringComparison.Ordinal))
                    continue;
                if (lg.endUT > seamUT + epsSeconds)
                    continue; // ends after the seam (+eps) -> not the deorbit tail
                if (double.IsNaN(bestEnd) || lg.endUT > bestEnd)
                {
                    bestEnd = lg.endUT;   // the leg ending closest to (== at) the seam
                    bestStart = lg.startUT;
                }
            }
            return bestStart;
        }

        /// <summary>
        /// Quantizes a cadence to the nearest multiple of <paramref name="p"/> at or ABOVE the
        /// cadence (which is already floored at MinCycleDuration / the overlap cap). Rounds UP so
        /// the quantized cadence never drops below the existing floor, keeping the instance-count
        /// cap intact. A degenerate P (&lt;= 0 / NaN) leaves the cadence unchanged. Pure.
        /// </summary>
        internal static double QuantizeCadenceToMultipleOfP(double cadence, double p)
        {
            if (double.IsNaN(p) || double.IsInfinity(p) || p <= 0.0)
                return cadence;
            if (double.IsNaN(cadence) || double.IsInfinity(cadence) || cadence <= 0.0)
                return p;
            // Smallest k >= 1 with k*P >= cadence.
            double k = Math.Ceiling(cadence / p - 1e-9);
            if (k < 1.0)
                k = 1.0;
            return k * p;
        }

        /// <summary>
        /// Maps a Mission's interval-level selection (<see cref="Mission.ExcludedIntervalKeys"/>) to
        /// its committed loop members + their TRIMMED render windows, keyed by committed index. This
        /// is the single source of truth for "which recordings does this looped mission include, and
        /// over what time window" - <see cref="TryBuildMissionUnit"/> builds the actual span clock
        /// from it, and the Missions UI (period-cell effective-cadence display + watch target) reads
        /// it so the displayed cadence and watch candidates match the looped reality exactly (rather
        /// than the legacy through-line-head selection, which ignored interval trims).
        ///
        /// Each included vessel's render window (from <see cref="MissionIntervalSelection.ComputeRenderWindows"/>)
        /// is intersected with each member recording's own [StartUT, EndUT]; a member entirely
        /// outside the window is dropped, and the first claimant wins on duplicate RecordingIds.
        /// <paramref name="indexById"/> is the committed RecordingId -> index map (pass the shared one
        /// from <see cref="Build"/>, or null to build it here). Empty exclusions => every window spans
        /// the whole vessel, so each member keeps its full range. Pure: no Unity, no shared state.
        /// </summary>
        internal static Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow> ComputeTrimmedMemberWindows(
            MissionThroughLineView view,
            List<MissionCompositionNode> compRoots,
            IReadOnlyList<Recording> committed,
            ICollection<string> excludedIntervalKeys,
            Dictionary<string, int> indexById,
            out int vesselWindowCount,
            out int skippedNotCommitted)
        {
            var memberWindowByIndex = new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>();
            vesselWindowCount = 0;
            skippedNotCommitted = 0;
            if (view == null || compRoots == null || committed == null)
                return memberWindowByIndex;
            if (indexById == null)
                indexById = BuildIndexById(committed);

            Dictionary<string, MissionIntervalSelection.RenderWindow> vesselWindows =
                MissionIntervalSelection.ComputeRenderWindows(compRoots, excludedIntervalKeys);
            vesselWindowCount = vesselWindows.Count;
            foreach (var vw in vesselWindows)
            {
                if (!view.ByHeadId.TryGetValue(vw.Key, out MissionThroughLine tl))
                    continue;
                double winStart = vw.Value.StartUT;
                double winEnd = vw.Value.EndUT;
                var members = tl.MemberLegIds;
                for (int i = 0; i < members.Count; i++)
                {
                    string id = members[i];
                    if (string.IsNullOrEmpty(id))
                        continue;
                    if (!indexById.TryGetValue(id, out int idx))
                    {
                        skippedNotCommitted++;
                        continue;
                    }
                    if (memberWindowByIndex.ContainsKey(idx))
                        continue; // first wins on duplicate ids
                    Recording rec = committed[idx];
                    double rStart = Math.Max(winStart, rec.StartUT);
                    double rEnd = Math.Min(winEnd, rec.EndUT);
                    if (rEnd <= rStart)
                        continue; // member entirely outside the vessel's render window (trimmed off)
                    memberWindowByIndex[idx] =
                        new GhostPlaybackLogic.LoopUnit.MemberWindow(rStart, rEnd);
                }
            }
            return memberWindowByIndex;
        }

        /// <summary>
        /// Cheap change-detection signature over the inputs that shape the Mission
        /// <see cref="GhostPlaybackLogic.LoopUnitSet"/>. Shared by every scene driver (flight engine,
        /// KSC, tracking station) so the allocating, Verbose-logging <see cref="Build"/> only fires on
        /// an actual input change while the cached set is pushed every frame. Mirrors Build's "every
        /// looping mission, in list order" rule: for EACH looping mission it folds in Id, TreeId,
        /// LoopIntervalSeconds, LoopTimeUnit, LoopAnchorUT, sorted ExcludedThroughLineHeadIds, and its
        /// tree's BranchPoints.Count + Recordings.Count; then the global
        /// <paramref name="autoLoopIntervalSeconds"/> (which sets an Auto mission's overlap cadence),
        /// the committed-list count, and a rolling RecordingId hash. When <paramref name="bodyInfo"/>
        /// is non-null (the phase-lock wiring), it ALSO folds in each looping tree's transited-body
        /// set + their rotation/orbit periods, so a body-geometry change (e.g. a planet pack / a
        /// different save's universe) re-derives P and rebuilds the unit. Constant "none:" prefix
        /// when no mission loops, so toggling looping off still rebuilds to Empty exactly once.
        /// Pure: no Unity calls, no shared mutable state.
        /// </summary>
        internal static string BuildSignature(
            IReadOnlyList<Mission> missions,
            IReadOnlyList<RecordingTree> trees,
            IReadOnlyList<Recording> committed,
            double autoLoopIntervalSeconds,
            IBodyInfo bodyInfo = null,
            TransitedBodyRotationMode transitedBodyRotationMode = TransitedBodyRotationMode.Tight)
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder(128);
            // The transited-body rotation mode (player A/B setting) changes the zero-drift schedule
            // (drops / loosens a transited-body landing constraint), so flipping it must rebuild the
            // cached LoopUnitSet. Fold it in once (only matters when bodyInfo is supplied / phase-lock
            // wiring active; without it the schedule path is inert and the mode is irrelevant).
            if (bodyInfo != null)
                sb.Append("TBR:").Append(transitedBodyRotationMode.ToString()).Append('|');

            int loopingCount = 0;
            if (missions != null)
            {
                for (int mi = 0; mi < missions.Count; mi++)
                {
                    Mission m = missions[mi];
                    if (m == null || !m.LoopPlayback)
                        continue;
                    loopingCount++;
                    sb.Append(m.Id ?? "<noid>").Append('|');
                    sb.Append(m.TreeId ?? "<notree>").Append('|');
                    sb.Append(m.LoopIntervalSeconds.ToString("R", ic)).Append('|');
                    sb.Append(m.LoopTimeUnit.ToString()).Append('|');
                    // Phase anchor: re-enabling the loop re-anchors the span clock, so a changed
                    // anchor must force a rebuild even when nothing else about the mission moved.
                    sb.Append(m.LoopAnchorUT.ToString("R", ic)).Append('|');
                    // Sorted + joined so set order never perturbs the signature. The interval-level
                    // trim keys (ExcludedIntervalKeys) now drive the unit's members + span, so fold
                    // those in (a start/end-trim change must rebuild). The legacy through-line-head
                    // set is folded too so a mixed/old save still rebuilds when it changes.
                    var excluded = new List<string>(m.ExcludedThroughLineHeadIds);
                    excluded.Sort(StringComparer.Ordinal);
                    for (int e = 0; e < excluded.Count; e++)
                        sb.Append(excluded[e] ?? "").Append(',');
                    sb.Append('~');
                    var excludedIntervals = new List<string>(m.ExcludedIntervalKeys);
                    excludedIntervals.Sort(StringComparer.Ordinal);
                    for (int e = 0; e < excludedIntervals.Count; e++)
                        sb.Append(excludedIntervals[e] ?? "").Append(',');
                    sb.Append(';');
                    // Tree topology: a mid-session merge / re-parent can change the unit's
                    // members or span without adding/renaming any committed RecordingId, so the
                    // committed hash below would not move. Fold this looping tree's branch +
                    // recording counts in so a topology change still forces a rebuild.
                    RecordingTree loopTree = FindTree(trees, m.TreeId);
                    sb.Append((loopTree?.BranchPoints?.Count ?? 0).ToString(ic)).Append('/');
                    sb.Append((loopTree?.Recordings?.Count ?? 0).ToString(ic)).Append('#');

                    // Phase-lock constraint inputs: the looping tree's transited-body set + their
                    // live rotation/orbit periods. A body-geometry change (planet pack / different
                    // universe) moves these even when the recordings + selection are unchanged, so
                    // P re-derives and the unit rebuilds. Only computed when bodyInfo is supplied
                    // (the phase-lock wiring); without it the signature is byte-identical to before.
                    if (bodyInfo != null)
                    {
                        AppendTransitedBodyDigest(sb, loopTree, bodyInfo, ic);
                        // M4a/M4c anchor orbit identity (design doc section 8): the rendezvous
                        // anchor's LIVE orbit feeds the VesselOrbital constraint (schedule on
                        // same-parent shapes, arrival hold on cross-parent shapes), so a boosted
                        // or vanished station must move the signature or the cached unit keeps a
                        // stale T_station.
                        AppendStationAnchorDigest(sb, loopTree, committed, bodyInfo, ic);
                    }
                }
            }

            if (loopingCount == 0)
                sb.Append("none:");
            else
                // Auto missions take their overlap cadence from the GLOBAL auto-loop interval, so a
                // change to that setting (with any Auto mission looping) must rebuild even when nothing
                // about the missions moved. Folding it in once unconditionally is cheap.
                sb.Append(autoLoopIntervalSeconds.ToString("R", ic)).Append('|');

            // Committed-list identity: count + a rolling hash of RecordingIds (member indices are
            // committed-list indices, so any add/remove/reorder must invalidate the cached set).
            int count = committed?.Count ?? 0;
            sb.Append(count.ToString(ic)).Append('|');
            int rollingHash = 17;
            for (int i = 0; i < count; i++)
            {
                string id = committed[i]?.RecordingId ?? "";
                unchecked { rollingHash = rollingHash * 31 + StringComparer.Ordinal.GetHashCode(id); }
            }
            sb.Append(rollingHash.ToString(ic));
            return sb.ToString();
        }

        /// <summary>
        /// Appends a compact digest of a looping tree's transited bodies + their live rotation/orbit
        /// periods to the signature, so a body-geometry change rebuilds the cached unit. Scans only
        /// this one tree's recordings (bounded), gathering the distinct body names from each
        /// recording's OrbitSegments + OrbitalCheckpoint checkpoints + the recording-level
        /// Start/Segment body fields. Sorted so enumeration order never perturbs the digest.
        /// </summary>
        private static void AppendTransitedBodyDigest(
            System.Text.StringBuilder sb, RecordingTree loopTree, IBodyInfo bodyInfo, CultureInfo ic)
        {
            sb.Append("B:");
            if (loopTree == null || loopTree.Recordings == null)
                return;
            var bodies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rec in loopTree.Recordings.Values)
            {
                if (rec == null)
                    continue;
                if (rec.OrbitSegments != null)
                    for (int i = 0; i < rec.OrbitSegments.Count; i++)
                        if (!string.IsNullOrEmpty(rec.OrbitSegments[i].bodyName))
                            bodies.Add(rec.OrbitSegments[i].bodyName);
                if (rec.TrackSections != null)
                {
                    for (int i = 0; i < rec.TrackSections.Count; i++)
                    {
                        TrackSection sec = rec.TrackSections[i];
                        if (sec.checkpoints != null)
                            for (int c = 0; c < sec.checkpoints.Count; c++)
                                if (!string.IsNullOrEmpty(sec.checkpoints[c].bodyName))
                                    bodies.Add(sec.checkpoints[c].bodyName);
                    }
                }
                if (!string.IsNullOrEmpty(rec.SegmentBodyName))
                    bodies.Add(rec.SegmentBodyName);
                if (!string.IsNullOrEmpty(rec.StartBodyName))
                    bodies.Add(rec.StartBodyName);
            }
            var sorted = new List<string>(bodies);
            sorted.Sort(StringComparer.Ordinal);
            for (int i = 0; i < sorted.Count; i++)
            {
                string b = sorted[i];
                sb.Append(b).Append('=')
                  .Append(bodyInfo.RotationPeriod(b).ToString("R", ic)).Append(',')
                  .Append(bodyInfo.OrbitPeriod(b).ToString("R", ic)).Append(',')
                  .Append(bodyInfo.ReferenceBodyName(b) ?? "-").Append(',')
                  // SoiRadius + OrbitalVelocity feed the orbital tolerance (SoiRadius/OrbitalVelocity),
                  // which determines the zero-drift schedule's within-tolerance windows. Fold them in
                  // so a planet pack that changes a body's SOI (without changing its orbit period)
                  // still re-derives the schedule.
                  .Append(bodyInfo.SoiRadius(b).ToString("R", ic)).Append(',')
                  .Append(bodyInfo.OrbitalVelocity(b).ToString("R", ic)).Append(';');
            }
            sb.Append('@');
        }

        /// <summary>
        /// Appends the looping tree's rendezvous-anchor LIVE orbit identities to the signature
        /// (M4a/M4c, design doc section 8): the distinct vessel anchors of the tree's Relative
        /// TrackSections - each resolved (anchorVesselId directly; anchorRecordingId through the
        /// committed recording's pid + launch guid) PLUS the OWNING recording's identity for any
        /// member carrying a vessel-anchored section (the dock merge's mutual anchoring means a
        /// partner-side section's rendezvous target is the OWNER, per the classifier's
        /// self-partition rule) - probed once each via
        /// <see cref="IBodyInfo.TryGetVesselOrbit"/>. A boosted station (period change), a
        /// vanished/recovered one (found flip), or a re-orbited one (body change) moves the
        /// digest and rebuilds the cached unit. The period is quantized to whole seconds: a real
        /// boost moves it by far more (the station tolerance is ~20s at 3 degrees), while a
        /// LOADED vessel's per-frame numeric orbit noise stays below the quantum - the residual
        /// accepted churn is a live vessel matching an anchor pid under ACTIVE THRUST sweeping
        /// integer-second boundaries for the burn duration (transient, bounded, only while that
        /// tree loops). Sorted by pid so enumeration order never perturbs the digest.
        /// </summary>
        private static void AppendStationAnchorDigest(
            System.Text.StringBuilder sb,
            RecordingTree loopTree,
            IReadOnlyList<Recording> committed,
            IBodyInfo bodyInfo,
            CultureInfo ic)
        {
            sb.Append("S:");
            if (loopTree == null || loopTree.Recordings == null)
                return;
            // pid -> guid (null until some identity supplies one; a guid-less direct-pid anchor
            // keeps null, matching the classifier's pid-only fallback).
            var anchorGuids = new Dictionary<uint, string>();
            foreach (var rec in loopTree.Recordings.Values)
            {
                if (rec == null || rec.TrackSections == null)
                    continue;
                if (!string.IsNullOrEmpty(rec.ParentAnchorRecordingId))
                    continue; // debris/decoupled children anchor their own parent, never a rendezvous
                bool ownsVesselAnchoredSection = false;
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    TrackSection sec = rec.TrackSections[i];
                    if (sec.referenceFrame != ReferenceFrame.Relative)
                        continue;
                    if (sec.anchorVesselId != 0)
                    {
                        ownsVesselAnchoredSection = true;
                        AddAnchorIdentity(anchorGuids, sec.anchorVesselId, null);
                    }
                    else if (!string.IsNullOrEmpty(sec.anchorRecordingId))
                    {
                        ownsVesselAnchoredSection = true;
                        Recording anchorRec = MissionPeriodicity.FindCommittedRecordingById(
                            committed, sec.anchorRecordingId);
                        if (anchorRec != null && anchorRec.VesselPersistentId != 0)
                            AddAnchorIdentity(anchorGuids,
                                anchorRec.VesselPersistentId, anchorRec.RecordedVesselGuid);
                    }
                }
                // The dock merge's MUTUAL anchoring: a PARTNER member's sections anchor the
                // mission's own craft, and the classifier reattributes them to the OWNER (the
                // partner = the station). The owning recording's identity is therefore part of
                // the anchor universe too - without it, a tree whose only Relative sections are
                // partner-side would feed the station's live orbit into the constraint while the
                // signature never probed the station. This (and the early-set of
                // ownsVesselAnchoredSection before the anchor-recording-id resolves) makes the
                // digest a strict SUPERSET of the classifier's emitted-anchor set: it may probe
                // a self-anchoring or unresolvable-recording owner the classifier emits no
                // constraint for. Harmless - the digest only gates cache invalidation, so
                // over-inclusion can at worst cost an occasional extra rebuild, never a missed
                // change or a wrong constraint (the constraint set is built independently). The
                // ParentAnchorRecordingId guard above still keeps debris/decoupled children out.
                if (ownsVesselAnchoredSection && rec.VesselPersistentId != 0)
                    AddAnchorIdentity(anchorGuids, rec.VesselPersistentId, rec.RecordedVesselGuid);
            }
            if (anchorGuids.Count == 0)
                return;
            var pids = new List<uint>(anchorGuids.Keys);
            pids.Sort();
            // (AddAnchorIdentity keeps the merge deterministic regardless of the recordings
            // dictionary's enumeration order, so the digest never flips between builds or across
            // save/load for identical inputs.)
            for (int i = 0; i < pids.Count; i++)
            {
                uint pid = pids[i];
                bool found = bodyInfo.TryGetVesselOrbit(
                    pid, anchorGuids[pid], out double period, out string body);
                sb.Append(pid.ToString(ic)).Append('=')
                  .Append(found ? "1" : "0").Append(',')
                  .Append(found ? System.Math.Floor(period).ToString("F0", ic) : "-").Append(',')
                  .Append(found ? (body ?? "-") : "-").Append(';');
            }
            sb.Append('@');
        }

        /// <summary>Order-independent identity merge for the station-anchor digest: a non-null
        /// guid always beats null, and between two DIFFERENT non-null guids for one pid (the
        /// craft-baked pid-collision shape - rejected by the classifier as distinct launches,
        /// but the digest must still be stable) the ordinal-smaller guid wins, so the digest is
        /// identical regardless of dictionary enumeration order. Net effect: "ordinal-min of all
        /// non-null offers for this pid, else null". internal for direct unit testing.</summary>
        internal static void AddAnchorIdentity(Dictionary<uint, string> anchorGuids, uint pid, string guid)
        {
            if (!anchorGuids.TryGetValue(pid, out string existing))
            {
                anchorGuids[pid] = guid;
                return;
            }
            if (guid == null || existing == guid)
                return;
            if (existing == null || string.CompareOrdinal(guid, existing) < 0)
                anchorGuids[pid] = guid;
        }

        // M4c arrival-amber transition log: last reason per TREE ID (mission names are
        // player-renamable via MissionGroupLink, so a name key would leak entries and fire
        // spurious transitions on rename), so the set/clear/changed Info line fires once per
        // transition, not per build. Suppressed builds (the UI rebuilds with SuppressLogging
        // set) neither log nor consume the transition, so the next unsuppressed build still
        // reports it - the M4a LogDriftAmberTransition pattern.
        private static readonly Dictionary<string, string> lastArrivalAmberReasonByTree =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal static void ResetArrivalAmberLogForTesting()
        {
            lastArrivalAmberReasonByTree.Clear();
        }

        // === M-MIS-2 P4 helpers (destination-loiter pre-landing trim) =========================

        // Launch-side loiter cuts at keepRevs=1 (runs ending at/before the SOI entry), matching
        // ReaimLoiterCompressor.ComputeCuts for those runs so the launch-parking compression stays
        // byte-identical to today. Destination-side runs (EndUT > the SOI entry) are excluded here;
        // their re-timing is decided jointly with the arrival hold in DestinationLoiterTrim.
        internal static List<GhostPlaybackLogic.LoopCut> BuildLaunchSideKeepOneCuts(
            IReadOnlyList<ReaimLoiterCompressor.LoiterRun> runs, double recordedArrivalUT)
        {
            var cuts = new List<GhostPlaybackLogic.LoopCut>();
            if (runs == null)
                return cuts;
            for (int i = 0; i < runs.Count; i++)
            {
                ReaimLoiterCompressor.LoiterRun run = runs[i];
                if (run.EndUT <= recordedArrivalUT
                    && run.WholeRevs > ReaimLoiterCompressor.DefaultKeepRevs
                    && !double.IsNaN(run.PeriodSeconds) && run.PeriodSeconds > 0.0)
                {
                    cuts.Add(new GhostPlaybackLogic.LoopCut
                    {
                        StartUT = run.StartUT,
                        LengthSeconds =
                            (run.WholeRevs - ReaimLoiterCompressor.DefaultKeepRevs) * run.PeriodSeconds,
                    });
                }
            }
            return cuts;
        }

        // Try the M-MIS-2 P4 joint destination-loiter trim + arrival hold for a re-aim landing.
        // Resolves the destination rotation constraint (its phase offset carries the recorded deorbit
        // anchor) and the destination constraint set, then defers to the pure DestinationLoiterTrim
        // solver. Returns None (the caller falls through to the shipped ComputeArrivalHold) when the
        // target has no landing rotation constraint or the solver declines.
        private static DestinationLoiterTrim.DestinationLoiterTrimResult TrySolveDestinationLoiterTrim(
            ConstraintExtraction extraction, ReaimMissionPlan plan,
            IReadOnlyList<ReaimLoiterCompressor.LoiterRun> loiterRuns,
            IReadOnlyList<GhostPlaybackLogic.LoopCut> launchSideCuts,
            double phaseAnchorUT, double spanStartUT, double spanSeconds,
            TransitedBodyRotationMode mode, IBodyInfo bodyInfo)
        {
            if (bodyInfo == null || string.IsNullOrEmpty(plan.TargetBody))
                return DestinationLoiterTrim.DestinationLoiterTrimResult.None;
            if (!TryFindTargetRotationConstraint(
                    extraction.Constraints, plan.TargetBody, out PhaseConstraint destRotation))
                return DestinationLoiterTrim.DestinationLoiterTrimResult.None;
            DestinationConstraintExtractor.DestinationConstraintSet destSet =
                DestinationConstraintExtractor.ExtractDestinationConstraints(
                    extraction.Constraints, plan.TargetBody, bodyInfo);
            // The deorbit anchor: the destination rotation constraint's phase offset is the earliest
            // target-body surface-section start relative to UT0. INVARIANT: UT0 == spanStartUT - both
            // derive from the same ComputeTrimmedMemberWindows span (extraction.UT0 is hard-asserted
            // within 1s of spanStartUT in BuildPhasingKnobInput rule 5), so adding spanStartUT recovers
            // the recorded surface UT.
            double recordedDestSurfaceUT = spanStartUT + destRotation.PhaseOffsetSeconds;
            return DestinationLoiterTrim.SolveTrimAndHold(
                loiterRuns, launchSideCuts, destSet, destRotation, extraction.LaunchBodyName,
                plan.TargetBody, plan.RecordedArrivalUT, recordedDestSurfaceUT,
                bodyInfo.RotationPeriod(plan.TargetBody), phaseAnchorUT, spanStartUT, spanSeconds,
                mode, DestinationLoiterTrim.DefaultMaxKeepRevs, bodyInfo);
        }

        // The Rotation constraint on the target body (the landing rotation), or false. Mirrors the
        // DestRotation selection DestinationConstraintExtractor performs.
        private static bool TryFindTargetRotationConstraint(
            IReadOnlyList<PhaseConstraint> constraints, string targetBody, out PhaseConstraint found)
        {
            found = default(PhaseConstraint);
            if (constraints == null || string.IsNullOrEmpty(targetBody))
                return false;
            for (int i = 0; i < constraints.Count; i++)
            {
                if (constraints[i].Kind == ConstraintKind.Rotation
                    && constraints[i].BodyName == targetBody)
                {
                    found = constraints[i];
                    return true;
                }
            }
            return false;
        }

        private static void LogArrivalAmberTransition(string treeId, string missionName, string reason)
        {
            if (SuppressLogging)
                return;
            string key = treeId ?? "?";
            lastArrivalAmberReasonByTree.TryGetValue(key, out string prev);
            if (string.Equals(prev, reason, StringComparison.Ordinal))
                return;
            lastArrivalAmberReasonByTree[key] = reason;
            ParsekLog.Info("Reaim",
                reason != null
                    ? $"Arrival amber SET: tree={key} mission='{missionName}' {reason}"
                    : $"Arrival amber CLEARED: tree={key} mission='{missionName}'");
        }

        private static RecordingTree FindTree(IReadOnlyList<RecordingTree> trees, string treeId)
        {
            if (trees == null || string.IsNullOrEmpty(treeId))
                return null;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null && trees[i].Id == treeId)
                    return trees[i];
            return null;
        }

        // The body a member's trajectory STARTS on, for descent-set identification: StartBodyName when set,
        // else the first recorded point's body, else the first OrbitSegment's body (a body-fixed approach
        // member may have no OrbitSegments, so Points is the reliable fallback). Null when indeterminable.
        private static string MemberStartBody(Recording rec)
        {
            if (rec == null)
                return null;
            if (!string.IsNullOrEmpty(rec.StartBodyName))
                return rec.StartBodyName;
            if (rec.Points != null && rec.Points.Count > 0 && !string.IsNullOrEmpty(rec.Points[0].bodyName))
                return rec.Points[0].bodyName;
            if (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
                return rec.OrbitSegments[0].bodyName;
            return null;
        }

        private static Dictionary<string, int> BuildIndexById(IReadOnlyList<Recording> committed)
        {
            var indexById = new Dictionary<string, int>();
            if (committed == null)
                return indexById;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording rec = committed[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!indexById.ContainsKey(rec.RecordingId))
                    indexById[rec.RecordingId] = i; // first wins on duplicate ids
            }
            return indexById;
        }

        /// <summary>
        /// Builder-side half of the M4b phasing-knob engagement
        /// (docs/dev/plans/mission-loiter-knob.md sections 3.2/5): detects the phasing run on the
        /// mission's SELF-LINE segments - every member sharing the OWNER's launch identity
        /// (pid + guid via <see cref="VesselLaunchIdentity.RecordingsShareLaunch"/>), so a CHAIN
        /// mission whose parking orbit lives in a continuation segment still engages (the
        /// 2026-06-11 playtest miss: the chain ROOT carries no OrbitSegments at all). Same-launch
        /// chain segments are ONE vessel's sequential timeline, so flattening them is safe; the
        /// per-member discipline of the re-aim branch guards against interleaving OTHER vessels'
        /// segments, and the identity gate excludes exactly those (the dock-merged partner, debris,
        /// probes - a partner's parked orbit is a loiter but never OUR phasing instrument).
        /// Computes the rendezvous/SOI guard UT and packages the run + the static cuts for earlier
        /// compressible runs. Returns null (knob disengaged, schedule built as today) when no
        /// compressible self-line loiter lies in-span before the guard, when the extraction UT0
        /// and span start do not coincide (rule 5 - the residual derivation assumes the schedule's
        /// launch grid and the clock's phase origin share an origin), or when inputs are degenerate.
        /// Rules 3/4 (anchor placement, shiftable partition) live in TryBuildRelaunchSchedule.
        /// Pure apart from gated Verbose logging.
        /// </summary>
        internal static PhasingKnobInput BuildPhasingKnobInput(
            IReadOnlyList<Recording> committed,
            IReadOnlyList<int> memberIndices,
            int ownerIndex,
            ConstraintExtraction extraction,
            double spanStartUT,
            double spanEndUT,
            IBodyInfo bodyInfo,
            string missionName)
        {
            var ic = CultureInfo.InvariantCulture;
            if (bodyInfo == null || committed == null
                || ownerIndex < 0 || ownerIndex >= committed.Count)
                return null;

            // Rule 5 (defensive origin coincidence). One second of slack: both values come from the
            // same member-window scan today, so any real divergence is a builder routing bug.
            if (double.IsNaN(extraction.UT0) || Math.Abs(extraction.UT0 - spanStartUT) > 1.0)
            {
                if (!SuppressLogging)
                    ParsekLog.Verbose("Mission",
                        $"phasing knob disengaged: mission='{missionName}' UT0=" +
                        $"{extraction.UT0.ToString("F0", ic)} spanStart={spanStartUT.ToString("F0", ic)} " +
                        "do not coincide (rule 5)");
                return null;
            }

            Recording owner = committed[ownerIndex];
            if (owner == null)
                return null;

            // Self-line segment gather: the owner plus every member of the SAME launch (chain
            // continuation segments share the launch pid + guid; the partner / debris / probes do
            // not). Sequential segments of one timeline sort cleanly into one list.
            var segs = new List<OrbitSegment>();
            int selfMembers = 0;
            for (int mi = 0; mi < (memberIndices?.Count ?? 0); mi++)
            {
                int idx = memberIndices[mi];
                if (idx < 0 || idx >= committed.Count)
                    continue;
                Recording rec = committed[idx];
                if (rec == null)
                    continue;
                bool isSelf = idx == ownerIndex
                    || VesselLaunchIdentity.RecordingsShareLaunch(owner, rec);
                if (!isSelf)
                    continue;
                selfMembers++;
                if (rec.OrbitSegments != null)
                    segs.AddRange(rec.OrbitSegments);
            }
            if (segs.Count == 0)
                return null; // no loiter source on the self line: quietly no knob (common case)

            segs.Sort((a, b) => a.startUT.CompareTo(b.startUT));
            List<ReaimLoiterCompressor.LoiterRun> runs =
                ReaimLoiterCompressor.DetectRuns(segs, bodyInfo.GravParameter);
            if (runs.Count == 0)
                return null; // no closed-orbit loiter at all: quietly no knob

            // Guard UT (rule 2 / the 4.3 cut-placement rule): never cut at or past the first
            // vessel rendezvous, never across a GENUINE third-body SOI boundary.
            double guardUT = spanEndUT;
            var constraints = extraction.Constraints;
            // Rendezvous bodies = the body each VesselOrbital station ORBITS (BodyName, set to the
            // orbited body by ClassifyVesselOrbitalConstraint). The phasing parking loiter that aligns
            // a destination-body dock (e.g. a Mun-station rendezvous) is AROUND that body, AFTER the
            // SOI entry into it. Treating the SOI entry into a rendezvous body as a guard would
            // structurally exclude that loiter; only a genuine THIRD body (neither launch nor a
            // rendezvous body, e.g. Minmus on a Kerbin->Mun->Minmus hop) must clamp the guard.
            var rendezvousBodies = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < (constraints?.Count ?? 0); i++)
            {
                if (constraints[i].Kind != ConstraintKind.VesselOrbital)
                    continue;
                double rendezvousUT = extraction.UT0 + constraints[i].PhaseOffsetSeconds;
                if (rendezvousUT < guardUT)
                    guardUT = rendezvousUT;
                if (!string.IsNullOrEmpty(constraints[i].BodyName))
                    rendezvousBodies.Add(constraints[i].BodyName);
            }
            string launchBody = extraction.LaunchBodyName;
            if (!string.IsNullOrEmpty(launchBody))
            {
                for (int i = 0; i < segs.Count; i++)
                {
                    if (segs[i].isPredicted || string.IsNullOrEmpty(segs[i].bodyName))
                        continue;
                    // Skip the launch body AND any rendezvous body: their orbit segments (the pad
                    // ascent, the destination-body parking loiter) are legitimate phasing-run terrain.
                    // Clamp + stop only at the first segment whose body is NEITHER - a real SOI entry
                    // into a third body the schedule cannot phase against.
                    if (segs[i].bodyName == launchBody || rendezvousBodies.Contains(segs[i].bodyName))
                        continue;
                    if (segs[i].startUT < guardUT)
                        guardUT = segs[i].startUT;
                    break;
                }
            }

            // Phasing run = the LAST launch-body run ending before the guard (the parking orbit the
            // player phase-matched with). Earlier compressible runs get static keepRevs=1 cuts.
            // IN-SPAN REQUIREMENT (review finding): the owner's OrbitSegments are NOT clipped to
            // the unit span, and a trimmed mission (interval exclusions) can start its render
            // window mid-recording - a run (or part of one) BEFORE spanStartUT would produce cuts
            // referencing out-of-span UTs, which the span clock's effSpan/Decompress composition
            // cannot represent (the cut shortens effSpan but never maps back for in-span samples).
            // A run not entirely inside [spanStartUT, guardUT] is therefore never the phasing run
            // and never a static cut; if that excludes every candidate, the knob fails closed.
            // (EndUT <= guardUT <= spanEndUT already bounds the upper edge; the start needs the
            // explicit spanStartUT check.)
            int phasingIdx = -1;
            for (int i = 0; i < runs.Count; i++)
            {
                if (runs[i].StartUT < spanStartUT - 1e-6)
                    continue;
                if (runs[i].EndUT > guardUT + 1e-6)
                    continue;
                if (!IsPhasingRunBodyAccepted(runs[i].BodyName, launchBody, rendezvousBodies))
                    continue;
                phasingIdx = i;
            }
            if (phasingIdx < 0)
            {
                if (!SuppressLogging)
                    ParsekLog.Verbose("Mission",
                        $"phasing knob disengaged: mission='{missionName}' " +
                        $"runs={runs.Count.ToString(ic)} none lie in-span before guardUT=" +
                        $"{guardUT.ToString("F0", ic)} on body '{launchBody ?? "?"}' (rule 2; " +
                        $"spanStart={spanStartUT.ToString("F0", ic)})");
                return null;
            }

            var staticCuts = new List<GhostPlaybackLogic.LoopCut>();
            for (int i = 0; i < phasingIdx; i++)
            {
                if (runs[i].StartUT < spanStartUT - 1e-6)
                    continue;
                if (runs[i].EndUT > guardUT + 1e-6)
                    continue;
                if (!IsPhasingRunBodyAccepted(runs[i].BodyName, launchBody, rendezvousBodies))
                    continue;
                if (runs[i].WholeRevs > 1)
                {
                    staticCuts.Add(new GhostPlaybackLogic.LoopCut
                    {
                        StartUT = runs[i].StartUT,
                        LengthSeconds = (runs[i].WholeRevs - 1) * runs[i].PeriodSeconds,
                    });
                }
            }

            ReaimLoiterCompressor.LoiterRun phasing = runs[phasingIdx];
            if (!SuppressLogging)
            {
                ParsekLog.Verbose("Mission",
                    $"phasing knob candidate: mission='{missionName}' run=[" +
                    $"{phasing.StartUT.ToString("F0", ic)},{phasing.EndUT.ToString("F0", ic)}] " +
                    $"T={phasing.PeriodSeconds.ToString("F1", ic)}s R={phasing.WholeRevs.ToString(ic)} " +
                    $"staticCuts={staticCuts.Count.ToString(ic)} guardUT={guardUT.ToString("F0", ic)} " +
                    $"selfMembers={selfMembers.ToString(ic)} selfSegs={segs.Count.ToString(ic)}");
                // The destination-body case: the selected phasing run is a parking loiter AROUND a
                // rendezvous body (e.g. a Mun-station dock), not the launch body. Name the body so the
                // log shows the knob engaged past the SOI entry into the destination, not on the pad.
                if (!string.IsNullOrEmpty(launchBody) && phasing.BodyName != launchBody)
                    ParsekLog.Verbose("Mission",
                        $"phasing knob rendezvous-body loiter: mission='{missionName}' " +
                        $"body='{phasing.BodyName ?? "?"}' (launch body '{launchBody}') run=[" +
                        $"{phasing.StartUT.ToString("F0", ic)},{phasing.EndUT.ToString("F0", ic)}] " +
                        $"R={phasing.WholeRevs.ToString(ic)}");
            }
            return new PhasingKnobInput
            {
                RunStartUT = phasing.StartUT,
                RunEndUT = phasing.EndUT,
                PeriodSeconds = phasing.PeriodSeconds,
                RecordedRevs = phasing.WholeRevs,
                StaticCuts = staticCuts,
                SpanSeconds = spanEndUT - spanStartUT,
            };
        }

        /// <summary>
        /// A loiter run is eligible to be the phasing run (or a static keepRevs=1 cut) when it sits on
        /// the LAUNCH body (the pad parking orbit the player phase-matched against) OR on a RENDEZVOUS
        /// body (the body a VesselOrbital station orbits - a destination-body dock parks around it,
        /// after the SOI entry). A run on any third body (a genuine SOI fly-by the schedule cannot
        /// phase against) is excluded. An empty <paramref name="launchBody"/> accepts every run (the
        /// degenerate no-launch-body fallback, unchanged). Pure.
        /// </summary>
        internal static bool IsPhasingRunBodyAccepted(
            string runBodyName, string launchBody, HashSet<string> rendezvousBodies)
        {
            if (string.IsNullOrEmpty(launchBody))
                return true;
            if (runBodyName == launchBody)
                return true;
            return rendezvousBodies != null
                && !string.IsNullOrEmpty(runBodyName)
                && rendezvousBodies.Contains(runBodyName);
        }
    }
}
