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
                    if (MissionPeriodicity.TryBuildRelaunchSchedule(
                            extraction.Constraints, extraction.Support, extraction.UT0, referenceUT,
                            bodyInfo, out MissionRelaunchSchedule sched, minSpacing,
                            extraction.LaunchBodyName, transitedBodyRotationMode))
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
                        if (mp.Supported && transferSegments == null)
                        {
                            plan = mp;
                            transferSegments = msegs;
                            // keep scanning only to finish the gatheredCount tally for the diagnostic
                        }
                    }

                    // Diagnostic: dump the transfer member's segments + the classifier's transfer
                    // measurement so a save reload reveals the recorded structure (which segment is the
                    // transfer, where any loiter is, what the loiter detector + classifier see). Verbose,
                    // one-shot per build.
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
                                  $" ancestor={plan.CommonAncestor}"
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
                            // Apply only when the cuts leave a POSITIVE compressed span. keepRevs >= 1
                            // already guarantees each cutLength < its run duration (so totalCut < span),
                            // but gate on it explicitly to match TryComputeSpanLoopUT's effectiveSpan
                            // check: that gate falls back to the identity remap when totalCut >= span, so
                            // applying the anchor shift here without the same gate would produce a
                            // shifted-but-uncompressed clock in the (unreachable) degenerate case.
                            // Diagnostic: dump each cut (cross-reference start with the seg# dump above to
                            // see which run was excised, and whether the transfer arc was wrongly cut).
                            if (!SuppressLogging)
                            {
                                var cic = CultureInfo.InvariantCulture;
                                for (int ci = 0; ci < cuts.Count && ci < 30; ci++)
                                    ParsekLog.Verbose("ReaimDiag",
                                        $"  cut#{ci} start={cuts[ci].StartUT.ToString("F0", cic)}" +
                                        $" len={cuts[ci].LengthSeconds.ToString("F0", cic)}" +
                                        $" end={cuts[ci].EndUT.ToString("F0", cic)}");
                            }

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
            }

            // 8. Build the unit (carrying the per-member trimmed render windows + the optional
            //    zero-drift schedule; null schedule => the existing uniform-cadence span clock; the
            //    optional re-aim plan + synodic schedule drive the flight engine's per-window transfer
            //    substitution).
            memberArray = memberIndices.ToArray();
            unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex, memberArray, spanStartUT, spanEndUT, effectiveCadence, phaseAnchorUT,
                effectiveOverlapCadence, memberWindowByIndex, relaunchSchedule, reaimPlan, reaimSchedule,
                loiterCuts);

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

                // Phase-lock applied vs skipped (design Diagnostic Logging): Info so a misbehaving
                // config is never a silent branch.
                if (phaseLocked)
                {
                    string scheduleNote = relaunchSchedule != null
                        ? $" zeroDrift=yes firstLaunch={relaunchSchedule.FirstLaunchUT.ToString("R", ic)} " +
                          $"minInterval={relaunchSchedule.MinIntervalSeconds.ToString("R", ic)}"
                        : (scheduleRejectedForOverlap
                            ? " zeroDrift=rejected-would-overlap-keeping-fixed-cadence"
                            : " zeroDrift=no");
                    ParsekLog.Info("MissionPeriodicity",
                        $"PhaseLock APPLIED: mission='{mission.Name}' tree={tree.Id} " +
                        $"anchor {baseAnchorUT.ToString("R", ic)}->{phaseAnchorUT.ToString("R", ic)} " +
                        $"P={solution.P.ToString("R", ic)} method={solution.Method} " +
                        $"cadence {cadence.ToString("R", ic)}->{effectiveCadence.ToString("R", ic)} " +
                        $"residual={solution.ResidualSeconds.ToString("R", ic)} " +
                        $"withinTol={(solution.WithinTolerance ? "yes" : "no")}" + scheduleNote);
                }
                else if (bodyInfo != null)
                {
                    ParsekLog.Info("MissionPeriodicity",
                        $"PhaseLock SKIPPED: mission='{mission.Name}' tree={tree.Id} " +
                        $"support={solution.Support} keeping anchor={phaseAnchorUT.ToString("R", ic)} " +
                        "(today's behavior)");
                }
            }

            return true;
        }

        /// <summary>
        /// Gathers the non-predicted OrbitSegments of all loop members into one startUT-ordered list
        /// for re-aim classification. The classifier picks the launch body (earliest segment), the
        /// heliocentric (common-ancestor) leg, and the arrival, so interleaved debris segments are
        /// harmless. Pure (reads committed recordings' segment lists). Returns an empty list when no
        /// member has orbit segments.
        /// </summary>
        internal static List<OrbitSegment> GatherMemberOrbitSegments(
            IReadOnlyList<Recording> committed, List<int> memberIndices)
        {
            var segs = new List<OrbitSegment>();
            if (committed == null || memberIndices == null)
                return segs;
            for (int i = 0; i < memberIndices.Count; i++)
            {
                int idx = memberIndices[i];
                if (idx < 0 || idx >= committed.Count)
                    continue;
                Recording rec = committed[idx];
                if (rec == null || rec.OrbitSegments == null)
                    continue;
                for (int s = 0; s < rec.OrbitSegments.Count; s++)
                    segs.Add(rec.OrbitSegments[s]);
            }
            segs.Sort((a, b) => a.startUT.CompareTo(b.startUT));
            return segs;
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
                        AppendTransitedBodyDigest(sb, loopTree, bodyInfo, ic);
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

        private static RecordingTree FindTree(IReadOnlyList<RecordingTree> trees, string treeId)
        {
            if (trees == null || string.IsNullOrEmpty(treeId))
                return null;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null && trees[i].Id == treeId)
                    return trees[i];
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
    }
}
