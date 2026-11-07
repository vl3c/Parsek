using System.Collections.Generic;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The periodicity cells: the mission periodicity display, the T-minus text and its amber tint, the next relaunch UT and the period / basis / scheduled interval text.
    internal partial class MissionsWindowUI
    {
        // ===================== Mission periodicity display (Phase 3 UI) =====================

        // The computed Phase-1 periodicity solution for one looping mission, plus the dominant
        // constraint's kind/body (for the period-cell basis label). Computed once per mission per
        // frame by ComputeMissionPeriodicity and shared by the period cell + the T- cell. The
        // default value (Solved == false) means "not computed / not looping" -> blank cells.
        private struct MissionPeriodicityDisplay
        {
            public bool Solved;                  // false on the default (not looping / no data)
            public PeriodicitySolution Solution;
            public double NowUT;                 // the reference UT used for the live countdown
            public ConstraintKind DominantKind;  // valid only when IsPhaseLockedConstrained
            public string DominantBodyName;      // valid only when IsPhaseLockedConstrained

            // The engine's ACTUAL next relaunch UT for this mission, read off the REAL LoopUnitSet
            // (PhaseAnchorUT + n*relaunchCadence). This is what the T- countdown targets - NOT the
            // periodicity solution's NextWindowUT (the next faithful P-window), which the engine
            // SKIPS whenever the relaunch cadence is a multiple m*P with m>=2 (the recording span or
            // the user period exceeds P). The two coincide only when the relaunch cadence == P.
            // NaN when no real unit was built for this mission (see UnitBuilt).
            public double NextRelaunchUT;

            // True when MissionLoopUnitBuilder built a real unit for this mission (the mission maps
            // to at least one committed loop member). When false the engine relaunches nothing, so
            // the T- cell reads "not aligned" - the same word an unsupported config gets, since both
            // mean "this mission is not on a faithful launch schedule".
            public bool UnitBuilt;

            // True when the resolved unit carries a zero-drift relaunch schedule (a drifting
            // multi-constraint config). The cadence is then NON-UNIFORM, so the period cell shows a
            // "varies" RANGE off ScheduledMinIntervalSeconds / ScheduledMaxIntervalSeconds instead of
            // the fixed Solution.P.
            public bool IsScheduled;

            // The scheduled unit's MIN and MAX relaunch interval over the schedule's eager prefix -
            // the two ends of the "varies" period-cell RANGE ("~13d-1mo"). Read off the schedule's
            // MinIntervalSeconds / MaxIntervalSeconds. NaN when not scheduled. The range collapses to
            // a single value when the two ends are within ~5% (a tight cadence after the loiter knob
            // engages).
            public double ScheduledMinIntervalSeconds;
            public double ScheduledMaxIntervalSeconds;

            // R3: the SCHEDULE's own worst-launch tolerance flag - true iff every resolved scheduled
            // launch found a within-tolerance window, false once any fell to bounded-best (a genuinely
            // over-tolerance launch). The T- amber tint reads THIS for a scheduled unit, not the fixed
            // m*P-fit Solution.WithinTolerance (which is false for the in-tolerance stock Mun). True (the
            // benign default) when not scheduled, so the non-scheduled amber path is unchanged. See
            // docs/dev/plans/zero-drift-reschedule-hardening.md section 6 R2/R3.
            public bool ScheduleAllLaunchesWithinTolerance;

            // Supported + constrained: the loop is phase-locked to a real period P (not the free
            // MinCycleDuration). This is the state where the period cell shows P + basis label and
            // the T- cell shows a live countdown.
            public bool IsPhaseLockedConstrained =>
                Solved && Solution.ShouldPhaseLock
                && !double.IsNaN(Solution.P) && Solution.P > LoopTiming.MinCycleDuration + 1e-6;

            // D3 drift amber (M4a): set when an emitted VesselOrbital constraint's LIVE anchor
            // period has drifted past tolerance from the recorded rendezvous-time orbit. Display
            // only - tints the T- cell amber with the reason as tooltip; never affects Support.
            public string DriftAmberReason;

            // M4c arrival amber (design D8): set when the resolved re-aim unit's destination-side
            // alignment failed closed with a station involved (landing + station, station +
            // constrained moon, moon-orbiting station, station-bearing Jool-class). Read off the
            // REAL unit (not extraction) so it appears exactly when a built re-aim unit refused
            // the hold - a mission whose re-aim declines entirely shows no misleading amber.
            // Display only - tints the T- cell with the reason as tooltip.
            public string ArrivalAmberReason;

            // True when the resolved unit is a re-aim interplanetary loop (cross-parent single-hop). The
            // faithful periodicity Solve reports cross-parent UnsupportedCrossParent (ShouldPhaseLock
            // false), so without this the period / T- cells would read "not aligned" even though the
            // engine relaunches the re-aimed transfer every synodic window. When set, the period cell
            // shows the synodic cadence + a "(<target> transfer)" basis and the T- cell counts down to
            // the next window (NextRelaunchUT is already the uniform synodic anchor + n*cadence).
            public bool IsReaim;
            public double ReaimSynodicSeconds;   // the relaunch cadence (CadenceSeconds = smallest whole
                                                 // synodic multiple >= span; NaN when not re-aim). Matches
                                                 // the T-minus countdown so the two cells agree.
            public string ReaimTargetBody;        // the interplanetary destination (null when not re-aim)
        }

        // Computes the Phase-1 (Tier-1) periodicity solution for one looping mission, mirroring the
        // loop builder's Extract+Solve, but referenced to the LIVE clock (Planetarium UT) so the T-
        // countdown is "the next window from NOW". Reads RecordingStore.CommittedRecordings + the
        // mission's interval trim + the live bodies via FlightGlobalsBodyInfo. The per-call Verbose
        // summaries in ExtractConstraints/Solve are suppressed (the UI runs every frame); a single
        // rate-limited summary is logged here instead.
        // [ERS-exempt] reason: this reads the RAW committed list to match MissionLoopUnitBuilder
        // (which keys loop members by committed RecordingId); display-only, file allowlisted.
        private MissionPeriodicityDisplay ComputeMissionPeriodicity(
            Mission mission, MissionThroughLineView view)
        {
            var result = new MissionPeriodicityDisplay { Solved = false };
            if (mission == null || view == null)
                return result;
            RecordingTree tree = FindTree(RecordingStore.CommittedTrees, mission.TreeId);
            if (tree == null)
                return result;

            var committed = RecordingStore.CommittedRecordings;
            var compRoots = GetCompositionRoots(tree);
            double nowUT = Planetarium.GetUniversalTime();

            // Suppress the per-call Verbose lines (every frame) and emit one rate-limited summary.
            // MissionCompositionBuilder is gated too for parity (defensive - compRoots above is
            // already built through the suppressed GetCompositionRoots cache, and
            // ExtractConstraints emits no composition lines today).
            bool prevSuppress = MissionPeriodicity.SuppressLogging;
            bool prevCompSuppress = MissionCompositionBuilder.SuppressLogging;
            MissionPeriodicity.SuppressLogging = true;
            MissionCompositionBuilder.SuppressLogging = true;
            ConstraintExtraction extraction;
            PeriodicitySolution solution;
            try
            {
                extraction = MissionPeriodicity.ExtractConstraints(
                    view, compRoots, committed, mission.ExcludedIntervalKeys,
                    FlightGlobalsBodyInfo.Instance);
                solution = MissionPeriodicity.Solve(
                    extraction.Constraints, extraction.Support, extraction.UT0, nowUT,
                    FlightGlobalsBodyInfo.Instance);
            }
            finally
            {
                MissionPeriodicity.SuppressLogging = prevSuppress;
                MissionCompositionBuilder.SuppressLogging = prevCompSuppress;
            }

            result.Solved = true;
            result.Solution = solution;
            result.NowUT = nowUT;
            result.DriftAmberReason = extraction.DriftAmberReason;

            // Dominant constraint (kind/body) for the period-cell basis label, picked the same way
            // the solver picks the constraint whose period sets P.
            if (solution.ShouldPhaseLock && extraction.Constraints != null
                && extraction.Constraints.Count > 0)
            {
                int di = MissionPeriodicity.SelectDominantConstraintIndex(extraction.Constraints);
                PhaseConstraint dom = extraction.Constraints[di];
                result.DominantKind = dom.Kind;
                result.DominantBodyName = dom.BodyName;
            }

            // The ENGINE's actual next relaunch UT for this mission. The countdown must point here,
            // NOT at solution.NextWindowUT: the engine relaunches every effectiveOverlapCadence /
            // effectiveCadence (a MULTIPLE m*P of the period when the span / user period exceeds P),
            // so it SKIPS the in-between P-windows. Read the REAL unit (the same one the scene
            // drivers build) for this mission and derive the next relaunch from its schedule, so the
            // displayed countdown can never drift away from when a ghost actually launches.
            result.UnitBuilt = TryResolveMissionUnit(
                tree, committed, mission.ExcludedIntervalKeys, out GhostPlaybackLogic.LoopUnit unit);
            result.NextRelaunchUT = result.UnitBuilt
                ? ComputeNextRelaunchUT(unit, nowUT)
                : double.NaN;
            // Zero-drift: a scheduled (drifting) unit's cadence is non-uniform -> the period cell
            // shows a "varies" RANGE off the schedule's min / max gap, not the fixed Solution.P.
            result.IsScheduled = result.UnitBuilt && unit.RelaunchSchedule != null;
            // Range ends for the "varies" period cell: the schedule's min / max gap over its eager
            // prefix. Shown as "~min-max (basis, varies)" (collapsing to a single value when tight).
            result.ScheduledMinIntervalSeconds = result.IsScheduled
                ? unit.RelaunchSchedule.MinIntervalSeconds
                : double.NaN;
            result.ScheduledMaxIntervalSeconds = result.IsScheduled
                ? unit.RelaunchSchedule.MaxIntervalSeconds
                : double.NaN;
            // R3: surface the schedule's OWN worst-launch tolerance for the amber tint. Benign default
            // true when not scheduled (the non-scheduled amber path then reads Solution.WithinTolerance,
            // unchanged). For a scheduled unit this reflects whether any cached launch fell to bounded-best.
            result.ScheduleAllLaunchesWithinTolerance = !result.IsScheduled
                || unit.RelaunchSchedule.AllLaunchesWithinTolerance;

            // Re-aim (cross-parent interplanetary): the unit carries a synodic schedule the faithful
            // periodicity Solve cannot represent (it reports UnsupportedCrossParent). Surface it so the
            // cells show the ~synodic cadence + a window countdown instead of "not aligned".
            result.IsReaim = result.UnitBuilt && unit.IsReaim;
            // Show the ACTUAL relaunch cadence (CadenceSeconds = the smallest whole synodic multiple >=
            // span), NOT the bare 1*synodic period: the cadence is what the engine relaunches on and what
            // ComputeNextRelaunchUT counts the T-minus down to, so pointing the period cell at the synodic
            // period (1.05y) while the countdown uses the cadence (e.g. 2.1y for span>synodic) would make
            // the two cells DISAGREE by the multiple. For a normal mission cadence == synodic, so the cell
            // is unchanged.
            result.ReaimSynodicSeconds = result.IsReaim
                ? unit.CadenceSeconds
                : double.NaN;
            result.ReaimTargetBody = result.IsReaim ? unit.ReaimPlan.Value.TargetBody : null;
            // M4c arrival amber: carried by the unit the builder produced (same inputs as the
            // scene drivers, so the UI deterministically sees the same verdict).
            result.ArrivalAmberReason = result.UnitBuilt ? unit.ArrivalAmberReason : null;

            ParsekLog.VerboseRateLimited("MissionPeriodicity", "missions-ui-solve",
                $"Missions UI: mission='{mission.Name}' tree={tree.Id} " +
                $"support={solution.Support} P={solution.P.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"lock={(solution.ShouldPhaseLock ? "yes" : "no")} " +
                $"nextWindow={solution.NextWindowUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"unitBuilt={(result.UnitBuilt ? "yes" : "no")} " +
                $"nextRelaunch={result.NextRelaunchUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"now={nowUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}",
                3.0);

            // SEAM-RENDER OBSERVABILITY 4 (docs/dev/design-reaim-launch-hold-seam.md): the displayed T-minus
            // countdown target, so the next playtest can confirm the countdown targets the SAME advanced launch
            // UT as ComputeNextRelaunchUT / the warp (ruling out a countdown-vs-warp mismatch as the source of
            // the few-minutes offset). targetUT is result.NextRelaunchUT (the same value BuildTMinusCellText
            // counts down to, == L_N - ComputeBoundaryOverlapAdvanceSeconds for an engaged loop). Gated on the
            // launch-hold unit (the seam regime) and keyed per mission identity (phaseAnchorUT + spanStartUT,
            // matching the clock's per-loop keys) so the key set stays bounded by mission count. Logging-only.
            if (result.UnitBuilt && unit.LaunchHoldEngaged)
            {
                var mic = System.Globalization.CultureInfo.InvariantCulture;
                double tMinusSec = result.NextRelaunchUT - nowUT;
                ParsekLog.VerboseRateLimited(
                    "Mission",
                    $"missions-tminus-cell.{unit.PhaseAnchorUT.ToString("R", mic)}.{unit.SpanStartUT.ToString("R", mic)}",
                    $"missions T-minus cell: targetUT={result.NextRelaunchUT.ToString("R", mic)} " +
                    $"now={nowUT.ToString("R", mic)} tMinusSec={tMinusSec.ToString("R", mic)}",
                    3.0);
            }

            return result;
        }

        // Resolves the REAL LoopUnit the engine built for this mission, by mapping the mission's
        // trimmed loop members to committed indices (the SAME ComputeTrimmedMemberWindows the builder
        // keys members on) and looking any one of them up in the per-frame LoopUnitSet. Returns false
        // (unit = default) when the mission has no committed members or no unit was built for it (an
        // unsupported config / phase-lock skip still builds a unit; "no unit" means the mission maps
        // to no live loop member at all - e.g. every member was trimmed off). Display-only.
        // [ERS-exempt] reason: maps members via the RAW committed list to match MissionLoopUnitBuilder
        // (keyed by committed RecordingId); display-only, file allowlisted.
        private bool TryResolveMissionUnit(
            RecordingTree tree, IReadOnlyList<Recording> committed,
            ICollection<string> excludedIntervalKeys, out GhostPlaybackLogic.LoopUnit unit)
        {
            unit = default;
            if (tree == null || committed == null)
                return false;

            var (_, view) = GetMissionView(tree);
            var windows = MissionLoopUnitBuilder.ComputeTrimmedMemberWindows(
                view, GetCompositionRoots(tree), committed, excludedIntervalKeys,
                null, out _, out _);

            GhostPlaybackLogic.LoopUnitSet units = GetLoopUnitSet();
            foreach (int idx in windows.Keys)
                if (units.TryGetUnitForMember(idx, out unit))
                    return true;
            return false;
        }

        // ----- Pure display helpers (unit-tested; the IMGUI layout above is playtest-verified) -----

        /// <summary>
        /// Whether the next-launch countdown carries a WARNING (R3, see
        /// docs/dev/plans/zero-drift-reschedule-hardening.md section 6). Until Missions Model 1 this
        /// tinted the "Next launch" cell amber; the column is gone, and the warning now rides the
        /// mission summary's tooltip (NextLaunchWarningText). "Amber" below means that warning. It
        /// is set when the
        /// loop's ACTUAL relaunch alignment misses its physics tolerance - the user may want to re-trim.
        /// The amber source DIFFERS between scheduled and non-scheduled units:
        /// - A SCHEDULED (zero-drift) unit relaunches at NON-UNIFORM, within-tolerance-when-reachable
        ///   windows; its real tolerance is the SCHEDULE's own worst launch
        ///   (<paramref name="scheduleAllLaunchesWithinTolerance"/>), NOT the fixed m*P-fit
        ///   <paramref name="fixedFitWithinTolerance"/>. The fixed fit is FALSE for the stock Mun (~993 s
        ///   residual at m=9) even though every ACTUAL scheduled launch is within tolerance by construction,
        ///   so tinting off the fixed fit wrongly ambers a faithful unit. Tinting off the schedule flag
        ///   correctly ambers only a genuinely over-tolerance (bounded-best) schedule and stays green for
        ///   the in-tolerance stock Mun.
        /// - A NON-scheduled (fixed-cadence) unit has no schedule; its tolerance IS the fixed m*P fit, so it
        ///   tints off <paramref name="fixedFitWithinTolerance"/> exactly as before (no behavior change).
        /// For the TOLERANCE ambers the unit must be phase-locked + constrained
        /// (<paramref name="isPhaseLockedConstrained"/>) to tint at all; continuous / not-aligned / blank
        /// states read plain. NOT tinted unconditionally off <paramref name="isScheduled"/> - that would
        /// hide a real over-tolerance schedule. A non-null <paramref name="driftAmberReason"/> (the M4a
        /// D3 station-drift surface) also tints a phase-locked cell, independent of the tolerance flags
        /// (alignment is still to the LIVE orbit; the amber says the recorded approach may seam).
        /// Two M4c additions bypass the phase-lock gate, which is false for every re-aim mission:
        /// a non-null <paramref name="arrivalAmberReason"/> (the D8 dual-constraint refusal, carried by
        /// the built re-aim unit) always tints, and <paramref name="driftAmberReason"/> also tints when
        /// <paramref name="isReaimUnit"/> (a drifted cross-parent depot - M4c makes drift amber reachable
        /// on re-aim missions for the first time; tooltip-without-tint there would repeat the exact
        /// inconsistency the arrival amber fixes). Pure (no Unity); unit-tested.
        /// </summary>
        internal static bool ShouldTintTMinusAmber(
            bool isPhaseLockedConstrained,
            bool isScheduled,
            bool scheduleAllLaunchesWithinTolerance,
            bool fixedFitWithinTolerance,
            string driftAmberReason = null,
            string arrivalAmberReason = null,
            bool isReaimUnit = false)
        {
            // M4c arrival amber (D8): the destination-side alignment failed closed on a built
            // re-aim unit - tints regardless of phase-lock (re-aim is never phase-locked).
            if (!string.IsNullOrEmpty(arrivalAmberReason))
                return true;
            // D3 drift amber: display-only, set when the live station orbit drifted past tolerance
            // from the recorded rendezvous-time orbit. Phase-locked cells (M4a) and re-aim units
            // (M4c cross-parent depots) both tint.
            if (driftAmberReason != null && (isPhaseLockedConstrained || isReaimUnit))
                return true;
            if (!isPhaseLockedConstrained)
                return false;
            // Scheduled: amber iff some scheduled launch was over tolerance (bounded-best).
            // Non-scheduled: amber iff the fixed m*P fit missed tolerance (unchanged behavior).
            return isScheduled
                ? !scheduleAllLaunchesWithinTolerance
                : !fixedFitWithinTolerance;
        }

        /// <summary>The T- cell tooltip: the D3 drift and M4c arrival amber reasons joined with
        /// "; " when both are set, the lone one otherwise, null when neither. Pure.</summary>
        internal static string JoinAmberReasons(string driftAmberReason, string arrivalAmberReason)
        {
            bool hasDrift = !string.IsNullOrEmpty(driftAmberReason);
            bool hasArrival = !string.IsNullOrEmpty(arrivalAmberReason);
            if (hasDrift && hasArrival)
                return driftAmberReason + "; " + arrivalAmberReason;
            if (hasDrift)
                return driftAmberReason;
            return hasArrival ? arrivalAmberReason : null;
        }

        /// <summary>
        /// The engine's ACTUAL next relaunch UT for a built loop unit, derived from the SAME schedule
        /// <see cref="MissionLoopUnitBuilder.TryBuildMissionUnit"/> produces, so the T- countdown can
        /// never drift away from when a ghost really launches. The engine relaunches the whole mission
        /// every <see cref="GhostPlaybackLogic.LoopUnit.OverlapCadenceSeconds"/> when that overlaps the
        /// span (it is &lt; the span duration), else once per <see cref="GhostPlaybackLogic.LoopUnit.CadenceSeconds"/>
        /// (the single span instance). Either cadence is already quantized to a multiple m*P of the
        /// faithful period P, so the relaunches land on every m-th P-window - the in-between windows
        /// are SKIPPED. Next relaunch from now = <c>PhaseAnchorUT + n * interval</c> with
        /// <c>n = max(0, ceil((now - PhaseAnchorUT) / interval))</c> (n == 0 while the loop is still
        /// parked before its forward-snapped anchor). Pure; a degenerate interval (&lt;= 0 / NaN /
        /// infinity) falls back to the anchor itself.
        /// </summary>
        internal static double ComputeNextRelaunchUT(GhostPlaybackLogic.LoopUnit unit, double nowUT)
        {
            // Zero-drift: a scheduled unit relaunches at NON-UNIFORM UTs, so the next relaunch is the
            // schedule's next launch strictly after now (NaN if the safety cap was reached - the cell
            // then reads "not aligned"). The uniform phaseAnchor + n*cadence below would be wrong for a
            // non-uniform schedule.
            if (unit.RelaunchSchedule != null)
                return unit.RelaunchSchedule.NextLaunchAfter(nowUT);

            double anchor = unit.PhaseAnchorUT;
            if (double.IsNaN(anchor) || double.IsInfinity(anchor))
                return double.NaN;

            double span = unit.SpanEndUT - unit.SpanStartUT;
            // The engine overlaps the whole mission with itself when the overlap cadence is shorter
            // than the span (relaunch on the overlap cadence); otherwise there is a single span
            // instance that relaunches once per span-clock cadence. Mirror that exact choice.
            double interval = unit.OverlapCadenceSeconds < span
                ? unit.OverlapCadenceSeconds
                : unit.CadenceSeconds;
            if (double.IsNaN(interval) || double.IsInfinity(interval) || interval <= 0.0)
                return anchor; // degenerate cadence: the anchor is the only launch we can name

            // n = ceil((now - anchor) / interval), clamped at 0 so a future-snapped anchor (loop
            // parked, now < anchor) reports the anchor itself rather than a negative cycle.
            double n = System.Math.Ceiling((nowUT - anchor) / interval - 1e-9);
            if (n < 0.0)
                n = 0.0;
            double next = anchor + n * interval;
            // Floating-point guard: keep the result at or after now (and not a whole interval past).
            if (next < nowUT - 1e-6)
                next += interval;

            // Per-loop launch alignment (borrow-at-launch / repay-at-SOI-exit + boundary-overlap launch render):
            // when the unit's launch alignment is engaged the ghost launches delta_N EARLIER than the nominal
            // window L_N (the launch-body rotates back under the recorded path), so the actual launch instant the
            // engine RENDERS is L_N - ComputeBoundaryOverlapAdvanceSeconds(...): on an already-aligned (slack>0)
            // loop this equals the OLD capped advance (no change); on a zero-slack residual-seam loop the boundary
            // overlap uses the FULL raw delta (uncapped) - the launch realigns fully and the secondary renders, so
            // the navigable warp-to must point at the EARLIER (uncapped) launch the ghost actually lifts off at.
            // Using the capped advance here would point a few hours too LATE on the engaged loops (the cap-truncated
            // launch the engine no longer uses). This returns the ACTUAL launch instant L_N - boundaryOverlapAdvance:
            // subtract the advance for window n; if that advanced launch already passed, fall forward to window n+1's
            // advanced launch. The 15 s warp lead is NOT applied here - it is applied ONCE at the warp site by
            // TimeJumpManager.ApplyJumpLead(relaunchUT, ...) in ShowMissionWarpToWindowConfirmation, so the warp lands
            // the player on the pad just before lift-off (applying it here too would double-lead the warp ~30 s
            // early). No-op when the launch alignment is not engaged (a degenerate T_sid yields delta_N == 0).
            // PhaseAnchorUT / cadence / the window index are untouched (targeting is unchanged) - only the displayed
            // launch time shifts.
            if (unit.LaunchHoldEngaged)
            {
                long win = (long)System.Math.Round((next - anchor) / interval);
                double delta = GhostPlaybackLogic.ComputeBoundaryOverlapAdvanceSeconds(
                    anchor, unit.SpanStartUT, unit.SpanEndUT, interval, win,
                    unit.LaunchBodyRotationPeriodSeconds, unit.LoiterCuts,
                    unit.ArrivalHoldSeconds, unit.ArrivalAlignPeriodSeconds,
                    unit.ArrivalHoldAtUT, unit.ArrivalJointSecondaryPeriodSeconds,
                    unit.ArrivalJointSecondaryToleranceSeconds, unit.ArrivalJointMaxWholeHoldPeriods);
                next = next - delta;
                if (next < nowUT - 1e-6)
                {
                    double deltaNext = GhostPlaybackLogic.ComputeBoundaryOverlapAdvanceSeconds(
                        anchor, unit.SpanStartUT, unit.SpanEndUT, interval, win + 1,
                        unit.LaunchBodyRotationPeriodSeconds, unit.LoiterCuts,
                        unit.ArrivalHoldSeconds, unit.ArrivalAlignPeriodSeconds,
                        unit.ArrivalHoldAtUT, unit.ArrivalJointSecondaryPeriodSeconds,
                        unit.ArrivalJointSecondaryToleranceSeconds, unit.ArrivalJointMaxWholeHoldPeriods);
                    next = anchor + (win + 1) * interval - deltaNext;
                }
            }
            return next;
        }

        /// <summary>
        /// The next-launch text for the four states (design doc UX); the summary line shows it when
        /// it is a countdown (MissionPresentation.SummaryNextLaunchText) and its tooltip explains
        /// the two state words:
        /// - not looping / not solved -> "" (blank);
        /// - unsupported (cross-parent / rendezvous; the no-lock sentinel, ShouldPhaseLock==false)
        ///   OR no engine unit built for this mission -> "not aligned";
        /// - unconstrained (P == MinCycleDuration) -> "continuous";
        /// - supported + constrained -> "T-" + a compact countdown to the ENGINE's next relaunch.
        /// The countdown targets <paramref name="nextRelaunchUT"/> (the engine's actual next
        /// relaunch, PhaseAnchorUT + n*relaunchCadence), NOT the periodicity solution's next faithful
        /// P-window: when the relaunch cadence is m*P (m&gt;=2) the engine launches only every m-th
        /// window, so a P-window countdown would tick to "T- 0s" with no launch. Pure (no Unity).
        /// </summary>
        internal static string BuildTMinusCellText(
            bool looping, bool solved, bool shouldPhaseLock, bool unitBuilt,
            double p, double nextRelaunchUT, double nowUT, bool isReaim = false)
        {
            if (!looping || !solved)
                return "";
            // Re-aim: the cross-parent transfer IS aligned to a synodic launch window, even though the
            // faithful Solve reports it unsupported. Count down to the next window (NextRelaunchUT is the
            // uniform synodic anchor + n*cadence).
            if (isReaim && unitBuilt)
            {
                if (double.IsNaN(nextRelaunchUT))
                    return "not aligned";   // defensive: re-aim with no resolvable window
                double dr = nextRelaunchUT - nowUT;
                if (dr < 0.0)
                    dr = 0.0;
                return ParsekTimeFormat.FormatCountdown(dr);
            }
            if (!shouldPhaseLock || !unitBuilt)
                // Unsupported (cross-parent / rendezvous, the no-lock sentinel) or no live loop
                // member maps to a unit: this mission is not on a faithful launch schedule.
                return "not aligned";
            if (double.IsNaN(p) || p <= LoopTiming.MinCycleDuration + 1e-6)
                return "continuous";    // unconstrained free loop (nothing to line up)
            if (double.IsNaN(nextRelaunchUT))
                return "continuous";    // defensive: a locked P with no relaunch resolves as free
            double delta = nextRelaunchUT - nowUT;
            if (delta < 0.0)
                delta = 0.0;            // relaunch is at/behind now -> launching now
            return ParsekTimeFormat.FormatCountdown(delta);
        }

        /// <summary>
        /// A single-unit "~" approximate period: "~6h", "~1.6d", "~36m", "~9s", "~2.1y". Picks the
        /// largest unit at which the value is &gt;= 1 and shows one decimal (dropping a trailing
        /// ".0"). Respects the player's day length. Pure; non-positive / NaN -&gt; "~0s".
        /// </summary>
        internal static string FormatPeriodCompact(double seconds)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0.0)
                return "~0s";
            double secsPerDay = ParsekTimeFormat.SecsPerDay;
            double secsPerYear = ParsekTimeFormat.SecsPerYear;
            double value;
            string unit;
            if (seconds >= secsPerYear) { value = seconds / secsPerYear; unit = "y"; }
            else if (seconds >= secsPerDay) { value = seconds / secsPerDay; unit = "d"; }
            else if (seconds >= 3600.0) { value = seconds / 3600.0; unit = "h"; }
            else if (seconds >= 60.0) { value = seconds / 60.0; unit = "m"; }
            else { value = seconds; unit = "s"; }
            string num = value.ToString("0.0", ic);
            if (num.EndsWith(".0"))
                num = num.Substring(0, num.Length - 2);
            return "~" + num + unit;
        }

        /// <summary>
        /// The basis label for a phase-locked period cell: "(Kerbin rot)" for a Rotation constraint
        /// (the launch/landing body's rotation realigns the orbit over the site), "(Mun window)"
        /// for an Orbital constraint (the next intercept window), or "(station window)" for a
        /// VesselOrbital constraint (M4a: the period is the ANCHOR VESSEL's orbit, not a celestial
        /// event of bodyName, which holds the orbited body). Pure; empty body -> empty label.
        /// </summary>
        internal static string BuildPeriodBasisLabel(ConstraintKind kind, string bodyName)
        {
            if (kind == ConstraintKind.VesselOrbital)
                return "(station window)";
            if (string.IsNullOrEmpty(bodyName))
                return "";
            return kind == ConstraintKind.Rotation
                ? "(" + bodyName + " rot)"
                : "(" + bodyName + " window)";
        }

        /// <summary>
        /// The phase-locked period-cell display: the faithful period P + its basis label, e.g.
        /// "~6h (Kerbin rot)" or "~1.6d (Mun window)". Pure. A null/empty body (no dominant
        /// constraint) drops the basis suffix and shows just the period.
        /// </summary>
        internal static string BuildPeriodCellDisplay(double p, ConstraintKind kind, string bodyName)
        {
            string period = FormatPeriodCompact(p);
            string basis = BuildPeriodBasisLabel(kind, bodyName);
            return string.IsNullOrEmpty(basis) ? period : period + " " + basis;
        }

        /// <summary>
        /// The period-cell display for a RE-AIM interplanetary loop: the synodic relaunch cadence + a
        /// "(&lt;target&gt; transfer)" basis, e.g. "~2.1y (Duna transfer)". Pure. A null/empty target
        /// drops the basis suffix.
        /// </summary>
        internal static string BuildReaimPeriodCellDisplay(double synodicSeconds, string targetBody)
        {
            string period = FormatPeriodCompact(synodicSeconds);
            return string.IsNullOrEmpty(targetBody) ? period : period + " (" + targetBody + " transfer)";
        }

        /// <summary>
        /// The period-cell display for a ZERO-DRIFT scheduled unit: the cadence is non-uniform, so we
        /// show the RANGE of relaunch intervals (min-max over the schedule's eager prefix) plus a
        /// "varies" marker, e.g. "~13d-1mo (Mun window, varies)". Pure. A null/empty body shows
        /// "~min-max (varies)". When the two ends are within ~5% (a tight cadence, e.g. after the
        /// loiter knob engages on the destination-body parking orbit) the range collapses to a single
        /// value with no dash: "~13d (Mun window, varies)".
        ///
        /// Note the period and the basis describe DIFFERENT things, intentionally (review N2): the
        /// interval is the actual relaunch cadence (anchored on the tightest constraint - the launch
        /// pad - and throttled by the player), while the basis label
        /// (<paramref name="kind"/>/<paramref name="bodyName"/>) names the DOMINANT celestial event
        /// each window targets (the Mun intercept). So "~13d-1mo (Mun window, varies)" reads as "a
        /// varying ~13-day-to-1-month cadence, each launch hitting a Mun window".
        /// </summary>
        internal static string BuildScheduledPeriodCellDisplay(
            double minIntervalSeconds, double maxIntervalSeconds,
            ConstraintKind kind, string bodyName)
        {
            string period = FormatScheduledIntervalRange(minIntervalSeconds, maxIntervalSeconds);
            string basis = BuildPeriodBasisLabel(kind, bodyName);
            return string.IsNullOrEmpty(basis)
                ? period + " (varies)"
                : period + " " + basis.Substring(0, basis.Length - 1) + ", varies)";
        }

        /// <summary>
        /// Formats a relaunch-interval RANGE for the scheduled period cell. Each end goes through
        /// <see cref="FormatPeriodCompact"/> ("~13d", "~1mo"-style). When the two ends are within ~5%
        /// (relative to the min) OR render to the same string, the range collapses to a single value
        /// (no dash). Otherwise they join as "min-max" with the leading "~" dropped from the max so the
        /// cell reads "~13d-30d", not "~13d-~30d". Pure; a non-positive / NaN min uses the max (and
        /// vice-versa).
        /// </summary>
        internal static string FormatScheduledIntervalRange(double minSeconds, double maxSeconds)
        {
            bool minOk = !double.IsNaN(minSeconds) && !double.IsInfinity(minSeconds) && minSeconds > 0.0;
            bool maxOk = !double.IsNaN(maxSeconds) && !double.IsInfinity(maxSeconds) && maxSeconds > 0.0;
            if (!minOk && !maxOk)
                return FormatPeriodCompact(double.NaN); // "~0s"
            if (!minOk)
                return FormatPeriodCompact(maxSeconds);
            if (!maxOk)
                return FormatPeriodCompact(minSeconds);

            double lo = System.Math.Min(minSeconds, maxSeconds);
            double hi = System.Math.Max(minSeconds, maxSeconds);
            string loStr = FormatPeriodCompact(lo);
            string hiStr = FormatPeriodCompact(hi);

            // Collapse: within ~5% of the low end, or the two ends format identically.
            if (hi <= lo * 1.05 + 1e-9 || string.Equals(loStr, hiStr, System.StringComparison.Ordinal))
                return loStr;

            // "min-max": strip the leading "~" from the high end so only one tilde shows.
            string hiTail = hiStr.StartsWith("~", System.StringComparison.Ordinal)
                ? hiStr.Substring(1)
                : hiStr;
            return loStr + "-" + hiTail;
        }
    }
}
