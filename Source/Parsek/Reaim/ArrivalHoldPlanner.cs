using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Reaim
{
    // Re-aim Phase 4 (cross-parent destination-SOI arrival alignment), implementation Phase 3b: the PURE
    // planner that decides the loop-clock arrival HOLD for a re-aim arrival. The hold defers the in-SOI
    // replay so the destination-side phase at the handoff recurs to its recorded value; it is the inverse
    // of a loiter cut, applied inside the shared loop clock so both render paths inherit it. Three
    // alignment targets: a LANDING aligns the destination's ROTATION (T_rot at the deorbit); a
    // destination STATION rendezvous aligns the station's ORBIT (T_station at the SOI entry, rigid
    // through to the rendezvous); and the D8 landing+station DUAL (the post-M4c SolveArrivalWindow
    // wiring) takes the JOINT hold - station-lattice-exact plus up to
    // DestinationArrivalSolver.MaxJointHoldWholePeriods whole station periods so the rotation also lands
    // within its mode tolerance, engage-gated by the hold-aware window solve + the lattice feasibility
    // scan and failing closed (amber, reason naming the tolerance miss) otherwise. The remaining
    // station-bearing duals (station+moon, a moon-orbiting station) still fail closed upstream in the
    // extractor. Pure; no Unity. See docs/dev/design-mission-phasing-alignment.md section 6 (D8) and
    // docs/dev/plans/mission-station-arrival-hold.md.
    internal static class ArrivalHoldPlanner
    {
        // Windows the joint solve scans for the pick. With a feasible lattice every window admits an
        // in-tolerance joint arrival (the lattice run-length bound is window-independent), so the pick
        // is normally k = 1 (the cadence stays synodic - the HARD cadence requirement of
        // docs/dev/plans/reaim-destination-arrival-alignment.md); the horizon exists for the solver
        // contract and the bounded-best residual report on decline.
        internal const int JointSolveLookaheadWindows = 8;

        // M-MIS-6 (docs/dev/design-mission-multimoon-alignment.md D1): how many whole anchor
        // periods the joint-configuration recurrence scan walks when searching T_config = k *
        // P_anchor. The stock resonant inner three accept at k = 2 (Vall anchor); an
        // incommensurate pack (Bop/Pol) finds nothing and fails closed. 64 mirrors the
        // MaxJointHoldWholePeriods bound (a hold near 64 anchor periods is already a multi-day
        // frozen-ghost cost); the loop slack clamps it further below.
        internal const int ConfigAnchorLookaheadMultiples = 64;

        // M-MIS-6 (design D6): how many synodic windows the aligned-horizon REPORT counts (the
        // count of consecutive in-tolerance windows from k=1, logged on engage - the finite
        // aligned horizon the design accepts; ~40 for the stock inner three under Loose).
        // Reporting only, never a gate.
        internal const int ConfigHorizonReportWindows = 64;

        /// <summary>The arrival hold decision. Pure value.</summary>
        internal struct ArrivalHoldResult
        {
            public double HoldSeconds;          // 0 when no hold
            public double HoldAtUT;             // NaN when no hold
            public double AlignPeriodSeconds;   // T_rot (landing) or T_station (station / joint); NaN when no hold
            public bool Applied;
            public bool IsStationHold;          // true when AlignPeriodSeconds is the station period
            public uint AlignAnchorPid;         // the station's anchor vessel pid (logging); 0 for rotation
            public string AmberReason;          // D8 fail-closed surface (UI arrival amber); null when none

            // JOINT hold (D8 landing+station, the post-M4c SolveArrivalWindow wiring). When
            // IsJointHold is true the loop clock's per-loop hold stays station-lattice-exact
            // (AlignPeriodSeconds = T_station) and extends by whole station periods (bounded by
            // JointMaxWholeHoldPeriods) until the destination ROTATION phase error is within
            // JointSecondaryToleranceSeconds. All NaN/0/false on every other result so the clock's
            // single-period path stays byte-identical.
            public bool IsJointHold;
            public double JointSecondaryPeriodSeconds;      // T_rot; NaN when not joint
            public double JointSecondaryToleranceSeconds;   // mode tolerance for T_rot; NaN when not joint
            public int JointMaxWholeHoldPeriods;            // whole-period budget; 0 when not joint
            public long JointChosenWindowK;                 // the solve's pick (logging); 0 when not joint
            public double JointResidualSeconds;             // the solve's residual (logging); NaN when not joint

            // MULTI-MOON configuration hold (M-MIS-6). When IsConfigHold is true,
            // AlignPeriodSeconds is the joint configuration period T_config (a whole number of
            // anchor-moon periods) and the clock runs the SHIPPED single-period per-loop path -
            // no new clock fields. The horizon/residual fields are build-time reporting only
            // (design D6: the finite aligned horizon is computed and surfaced, never silent).
            public bool IsConfigHold;
            public int ConfigAlignedWindowHorizon;          // consecutive in-tolerance windows from k=1 (capped report)
            public double ConfigFirstWindowResidualSeconds; // worst constraint error at window 1; NaN when not config

            internal static ArrivalHoldResult None =>
                new ArrivalHoldResult
                {
                    HoldSeconds = 0.0,
                    HoldAtUT = double.NaN,
                    AlignPeriodSeconds = double.NaN,
                    Applied = false,
                    IsStationHold = false,
                    AlignAnchorPid = 0,
                    AmberReason = null,
                    IsJointHold = false,
                    JointSecondaryPeriodSeconds = double.NaN,
                    JointSecondaryToleranceSeconds = double.NaN,
                    JointMaxWholeHoldPeriods = 0,
                    JointChosenWindowK = 0,
                    JointResidualSeconds = double.NaN,
                    IsConfigHold = false,
                    ConfigAlignedWindowHorizon = 0,
                    ConfigFirstWindowResidualSeconds = double.NaN,
                };

            internal static ArrivalHoldResult NoneWithAmber(string reason)
            {
                ArrivalHoldResult r = None;
                r.AmberReason = reason;
                return r;
            }
        }

        /// <summary>
        /// The loop-clock arrival hold for a re-aim arrival, or None. GATED (fail closed to faithful,
        /// leaving the span clock byte-identical): an unsupported destination (the still-deferred D8
        /// station-bearing duals station+moon / moon-orbiting-station, incl. station-bearing
        /// Jool-class - those carry <see cref="ArrivalHoldResult.AmberReason"/>), an orbit-only
        /// arrival with no station, a landing with alignment OFF (Drop mode - the mode gates ONLY the
        /// rotation hold: it is the transited-body ROTATION alignment A/B, and the station hold is a
        /// fully automatic alignment with no toggle per design D4), or a degenerate align period all
        /// return None. The D8 landing+station dual routes through the JOINT hold
        /// (<see cref="ComputeJointArrivalHold"/>, the post-M4c SolveArrivalWindow wiring) and the
        /// M-MIS-6 multi-moon (2+ constrained moons) shape through the CONFIGURATION hold
        /// (<see cref="ComputeMultiMoonConfigHold"/>); the windowSpacing / launchBody / loopSlack
        /// parameters feed those two branches and are unused by every other
        /// shape. Otherwise the hold W is the minimal forward delay that lands the SOI entry at the
        /// recorded destination-side phase (rotation phase for a landing; station orbital phase for a
        /// destination station - the in-SOI sequence is rigid, so aligning the entry aligns the
        /// deorbit / the rendezvous). The live (unshifted) SOI entry replays at
        /// <c>phaseAnchorUT + (CompressSpanUT(recordedArrivalUT, loiterCuts) - spanStartUT)</c> (the loop
        /// clock's recorded-span -> live map, accounting for launch-side loiter compression). The hold is
        /// inserted at <c>recordedArrivalUT</c> (the heliocentric-&gt;capture boundary). Pure.
        /// </summary>
        internal static ArrivalHoldResult ComputeArrivalHold(
            IReadOnlyList<PhaseConstraint> allConstraints,
            string targetBody,
            double recordedArrivalUT,
            TransitedBodyRotationMode mode,
            double phaseAnchorUT,
            double spanStartUT,
            IReadOnlyList<GhostPlaybackLogic.LoopCut> loiterCuts,
            IBodyInfo bodyInfo,
            double windowSpacingSeconds = double.NaN,
            string launchBodyName = null,
            double loopSlackSeconds = double.NaN)
        {
            if (bodyInfo == null || string.IsNullOrEmpty(targetBody))
                return ArrivalHoldResult.None;
            if (double.IsNaN(recordedArrivalUT) || double.IsNaN(phaseAnchorUT) || double.IsNaN(spanStartUT))
                return ArrivalHoldResult.None;

            DestinationConstraintExtractor.DestinationConstraintSet destSet =
                DestinationConstraintExtractor.ExtractDestinationConstraints(allConstraints, targetBody, bodyInfo);
            // Unsupported destination: no hold. Every Unsupported shape is station-bearing since
            // M-MIS-6 (the no-station Jool-class shape now emits and routes to the multi-moon
            // config hold below), and carries the reason as the arrival amber so the UI can
            // explain WHY the alignment stays faithful; the no-station arm is defensive.
            if (!destSet.Supported)
            {
                return destSet.HasStation
                    ? ArrivalHoldResult.NoneWithAmber(destSet.Reason)
                    : ArrivalHoldResult.None;
            }

            // D8 shape (a) landing+station: the JOINT hold (the post-M4c SolveArrivalWindow wiring).
            // Routed before the single-period pick; its own gates fail closed with an amber reason.
            if (destSet.IsJointLandingStation)
            {
                return ComputeJointArrivalHold(
                    destSet, targetBody, recordedArrivalUT, mode, phaseAnchorUT, spanStartUT,
                    loiterCuts, bodyInfo, windowSpacingSeconds, launchBodyName, loopSlackSeconds);
            }

            // M-MIS-6 multi-moon (Jool-class) shape: 2+ constrained moons, never station-bearing
            // (the extractor's station+moon reject already returned above). One per-loop hold on
            // the joint configuration period T_config; its own gates fail closed with amber
            // (docs/dev/design-mission-multimoon-alignment.md D1/D5/D6).
            if (destSet.ConstrainedMoonCount >= 2)
            {
                return ComputeMultiMoonConfigHold(
                    destSet, targetBody, recordedArrivalUT, mode, phaseAnchorUT, spanStartUT,
                    loiterCuts, bodyInfo, windowSpacingSeconds, launchBodyName, loopSlackSeconds);
            }

            // Pick the alignment target period. Station first: a Supported set never carries both
            // (the joint branch above consumed the dual), so the order only matters defensively.
            // The Drop-mode gate applies ONLY to the rotation hold (see the summary); a station
            // hold computes under every mode.
            double tAlign;
            bool isStationHold;
            if (destSet.HasStation)
            {
                tAlign = destSet.StationPeriodSeconds;
                isStationHold = true;
            }
            else if (destSet.HasLandingRotation && mode != TransitedBodyRotationMode.Drop)
            {
                tAlign = bodyInfo.RotationPeriod(targetBody);
                isStationHold = false;
            }
            else
            {
                // Orbit-only arrival with no station, or a landing with alignment off (Drop):
                // nothing to align, the clock stays faithful.
                return ArrivalHoldResult.None;
            }

            // A destination-side loiter cut (a recorded parking orbit excised AFTER SOI entry) compresses the
            // in-SOI duration by whole orbital periods, breaking the rigidity that lets an entry-referenced
            // hold align the deorbit / the rendezvous (the alignment would land off by cut mod T_align). That
            // is the deferred pre-landing-trim (L8) case: fail closed to faithful (no hold) here rather than
            // apply a partially-aligned hold; the M-MIS-2 P4 re-timer replaces this refusal later. Launch-side
            // cuts (before SOI entry) are fine - CompressSpanUT already folds them into liveEntryUT below.
            if (loiterCuts != null)
            {
                for (int i = 0; i < loiterCuts.Count; i++)
                    if (loiterCuts[i].EndUT > recordedArrivalUT)
                        return ArrivalHoldResult.None;
            }

            if (double.IsNaN(tAlign) || double.IsInfinity(tAlign) || tAlign <= 0.0)
                return ArrivalHoldResult.None;

            double liveEntryUT = phaseAnchorUT
                + (GhostPlaybackLogic.CompressSpanUT(recordedArrivalUT, loiterCuts) - spanStartUT);
            double w = GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(recordedArrivalUT, liveEntryUT, tAlign);
            if (!(w > 0.0) || double.IsInfinity(w))
                return ArrivalHoldResult.None;  // already aligned (w == 0) or degenerate: no hold

            ArrivalHoldResult single = ArrivalHoldResult.None;
            single.HoldSeconds = w;
            single.HoldAtUT = recordedArrivalUT;
            single.AlignPeriodSeconds = tAlign;
            single.Applied = true;
            single.IsStationHold = isStationHold;
            single.AlignAnchorPid = isStationHold ? destSet.StationAnchorPid : 0;
            return single;
        }

        /// <summary>
        /// The JOINT arrival hold for the D8 landing+station dual (the post-M4c SolveArrivalWindow
        /// wiring). Model: the per-loop hold snaps the arrival FORWARD onto the STATION lattice (the
        /// station's orbital phase recurs exactly, as the shipped station hold does), then extends by
        /// whole station periods - bounded by the slack-clamped
        /// <see cref="DestinationArrivalSolver.MaxJointHoldWholePeriods"/> budget - until the
        /// destination ROTATION phase error is within its
        /// <see cref="MissionPeriodicity.ScheduleToleranceSecondsFor"/> mode tolerance (the same
        /// acceptance band the same-parent landing schedule ships). Engage requires BOTH the
        /// hold-aware <see cref="DestinationArrivalSolver.SolveArrivalWindow"/> pick to land within
        /// tolerance in the window horizon AND the lattice feasibility scan
        /// (<see cref="DestinationArrivalSolver.PlanJointHoldLattice"/>, the all-loops run-length
        /// guarantee) to pass; any miss fails closed to faithful with an amber reason naming the
        /// tolerance miss - never a silent partial alignment. Under Drop the solver's own filter
        /// removes the rotation constraint (the sanctioned mode-aware resolution of the M4c
        /// deferral), so the dual degrades to the plain station hold; a tidally-degenerate pair
        /// (T_station ~= T_rot) collapses the same way because aligning one aligns the other. Pure.
        /// </summary>
        internal static ArrivalHoldResult ComputeJointArrivalHold(
            DestinationConstraintExtractor.DestinationConstraintSet destSet,
            string targetBody,
            double recordedArrivalUT,
            TransitedBodyRotationMode mode,
            double phaseAnchorUT,
            double spanStartUT,
            IReadOnlyList<GhostPlaybackLogic.LoopCut> loiterCuts,
            IBodyInfo bodyInfo,
            double windowSpacingSeconds,
            string launchBodyName,
            double loopSlackSeconds)
        {
            var ic = CultureInfo.InvariantCulture;
            double tSta = destSet.StationPeriodSeconds;
            if (double.IsNaN(tSta) || double.IsInfinity(tSta) || tSta <= 0.0
                || destSet.StationConstraint == null)
                return ArrivalHoldResult.NoneWithAmber(
                    "landing rotation + station rendezvous at '" + (targetBody ?? "?") +
                    "': degenerate station period, joint alignment impossible");

            // The destination rotation constraint: the extractor put it (alone) into Constraints for
            // this shape. Defensive find keeps the branch safe against synthetic inputs. T_rot comes
            // from the CONSTRAINT (the live rotation period extraction stamped - the same value
            // bodyInfo.RotationPeriod(targetBody) returns in a production build), so the period the
            // lattice gate checks is exactly the period the solve + the clock consume.
            PhaseConstraint? rotConstraint = null;
            for (int i = 0; i < destSet.Constraints.Count; i++)
            {
                if (destSet.Constraints[i].Kind == ConstraintKind.Rotation)
                {
                    rotConstraint = destSet.Constraints[i];
                    break;
                }
            }
            if (rotConstraint == null)
                return ArrivalHoldResult.NoneWithAmber(
                    "landing rotation + station rendezvous at '" + (targetBody ?? "?") +
                    "': rotation constraint missing from the destination set");
            double tRot = rotConstraint.Value.PeriodSeconds;
            if (double.IsNaN(tRot) || double.IsInfinity(tRot) || tRot <= 0.0)
                return ArrivalHoldResult.NoneWithAmber(
                    "landing rotation + station rendezvous at '" + (targetBody ?? "?") +
                    "': degenerate rotation period, joint alignment impossible");

            // Destination-side loiter-cut rigidity guard (the shipped L8 refusal, same reasoning): a
            // cut excised AFTER the SOI entry breaks the entry-referenced rigidity the joint hold
            // relies on. Amber (unlike the silent single-period None) because a station is involved.
            if (loiterCuts != null)
            {
                for (int i = 0; i < loiterCuts.Count; i++)
                    if (loiterCuts[i].EndUT > recordedArrivalUT)
                        return ArrivalHoldResult.NoneWithAmber(
                            "landing rotation + station rendezvous at '" + (targetBody ?? "?") +
                            "': a destination-side loiter cut breaks the entry-referenced joint " +
                            "hold (deferred to the M-MIS-2 P4 re-timer)");
            }

            // Drop / tidal degenerate FIRST (before the budget gate: neither needs a whole-period
            // budget). Under Drop the solver's own contract removes the transited rotation (the
            // sanctioned mode-aware resolution of the M4c deferral) and under tidal lock
            // (T_rot ~= T_station) aligning the station aligns the rotation for free - either way
            // the plain station hold is the honest outcome. No joint fields, so the clock runs the
            // shipped single-period path.
            // Drop is launch-body-gated exactly like the solver's own filter (and
            // ScheduleToleranceSecondsFor's Loose branch): only a TRANSITED (non-launch) body's
            // rotation is dropped, so a null launch body keeps the rotation constraint active.
            bool rotationDropped = mode == TransitedBodyRotationMode.Drop
                && MissionPeriodicity.IsTransitedBodyRotation(rotConstraint.Value, launchBodyName);
            bool tidal = System.Math.Abs(tRot - tSta)
                <= DestinationArrivalSolver.PeriodEqualityRelTolerance * System.Math.Max(1.0, tSta);
            if (rotationDropped || tidal)
            {
                double wSta = GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(
                    recordedArrivalUT,
                    phaseAnchorUT + (GhostPlaybackLogic.CompressSpanUT(recordedArrivalUT, loiterCuts) - spanStartUT),
                    tSta);
                if (!(wSta > 0.0) || double.IsInfinity(wSta))
                    return ArrivalHoldResult.None; // already aligned or degenerate: no hold
                ArrivalHoldResult degen = ArrivalHoldResult.None;
                degen.HoldSeconds = wSta;
                degen.HoldAtUT = recordedArrivalUT;
                degen.AlignPeriodSeconds = tSta;
                degen.Applied = true;
                degen.IsStationHold = true;
                degen.AlignAnchorPid = destSet.StationAnchorPid;
                if (!MissionPeriodicity.SuppressLogging)
                {
                    ParsekLog.Verbose("ReaimArrival",
                        "joint-hold degenerate to station hold: dest='" + (targetBody ?? "?") +
                        "' reason=" + (rotationDropped ? "rotation-dropped(Drop)" : "tidal-collapse") +
                        " Tsta=" + tSta.ToString("R", ic) + "s Trot=" + tRot.ToString("R", ic) + "s");
                }
                return degen;
            }

            // Whole-period budget: the constant cap, clamped down by the loop slack the builder
            // measured (cadence - compressed span) so the clock's defensive hold clamp can never
            // silently truncate a within-budget joint hold. Base hold < tSta, extension <=
            // maxWhole*tSta => worst hold < (maxWhole+1)*tSta must fit in the slack.
            int maxWhole = DestinationArrivalSolver.MaxJointHoldWholePeriods;
            if (!double.IsNaN(loopSlackSeconds) && !double.IsInfinity(loopSlackSeconds))
            {
                long slackPeriods = (long)System.Math.Floor(loopSlackSeconds / tSta) - 1;
                if (slackPeriods < maxWhole)
                    maxWhole = (int)System.Math.Max(0, slackPeriods);
            }
            if (maxWhole <= 0)
                return ArrivalHoldResult.NoneWithAmber(
                    "landing rotation + station rendezvous at '" + (targetBody ?? "?") +
                    "': loop slack " + (double.IsNaN(loopSlackSeconds) ? "unknown" : loopSlackSeconds.ToString("F0", ic) + "s") +
                    " leaves no whole-station-period joint hold budget");

            // The rotation tolerance: the same mode-aware band the same-parent schedule uses
            // (Loose = TransitedBodyLooseRotationDegrees, Tight = the physics tolerance).
            double tolRot = MissionPeriodicity.ScheduleToleranceSecondsFor(
                rotConstraint.Value, bodyInfo, launchBodyName, mode);

            // The hold-aware window solve: the consumed pick (which synodic window k admits an
            // in-tolerance joint arrival under the hold budget, and the first-window residual).
            var jointConstraints = new List<PhaseConstraint>(2)
            {
                rotConstraint.Value,
                destSet.StationConstraint.Value,
            };
            DestinationArrivalSolver.DestinationArrivalSolve solve =
                DestinationArrivalSolver.SolveArrivalWindow(
                    windowSpacingSeconds, jointConstraints, bodyInfo, launchBodyName, mode,
                    kStart: 1, lookaheadWindows: JointSolveLookaheadWindows,
                    holdAlignPeriodSeconds: tSta, maxWholeHoldPeriods: maxWhole);

            // All-loops feasibility: the lattice run-length bound. The window solve proves the
            // scanned windows; this proves EVERY loop offset (the loop index walks the whole
            // lattice), so a pass here is the zero-drift guarantee and a miss is the honest
            // incommensurate / tolerance-edge decline.
            DestinationArrivalSolver.JointHoldLatticePlan lattice =
                DestinationArrivalSolver.PlanJointHoldLattice(tSta, tRot, tolRot, maxWhole);
            if (!lattice.Feasible)
            {
                return ArrivalHoldResult.NoneWithAmber(
                    "landing rotation + station rendezvous at '" + (targetBody ?? "?") +
                    "': joint arrival alignment misses tolerance - the station/rotation lattice " +
                    "needs " + lattice.WorstMissRun.ToString(ic) + " whole station periods to reach " +
                    "the " + tolRot.ToString("F0", ic) + "s rotation tolerance (budget " +
                    maxWhole.ToString(ic) + "); faithful");
            }
            if (!solve.WithinTolerance)
            {
                // Defensive: with a feasible lattice every window passes, so this fires only on a
                // degenerate window spacing (e.g. a caller that did not supply the synodic period).
                return ArrivalHoldResult.NoneWithAmber(
                    "landing rotation + station rendezvous at '" + (targetBody ?? "?") +
                    "': joint arrival window solve declined (method=" + solve.Method +
                    " residual=" + solve.ResidualSeconds.ToString("F0", ic) + "s vs rotation tolerance " +
                    tolRot.ToString("F0", ic) + "s over " +
                    JointSolveLookaheadWindows.ToString(ic) + " windows); faithful");
            }

            // ENGAGE. The base hold is the station-lattice snap for the FIRST loop; the clock's
            // per-loop dispatch re-derives the snap and the whole-period extension for every loop
            // (ComputePerLoopJointArrivalHoldSeconds). A zero base (entry already station-aligned)
            // substitutes one full station period so the clock's hold>0 gate stays engaged; this
            // inserts NO spurious dead time - the per-loop formula mods the substituted w0 back to
            // the true 0 base ((tSta - N*(C mod tSta)) mod tSta == 0 at N=0) and only the
            // whole-period extension search runs on top of it.
            double liveEntryUT = phaseAnchorUT
                + (GhostPlaybackLogic.CompressSpanUT(recordedArrivalUT, loiterCuts) - spanStartUT);
            double w0 = GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(recordedArrivalUT, liveEntryUT, tSta);
            if (!(w0 > 0.0) || double.IsInfinity(w0))
                w0 = tSta;

            ArrivalHoldResult joint = ArrivalHoldResult.None;
            joint.HoldSeconds = w0;
            joint.HoldAtUT = recordedArrivalUT;
            joint.AlignPeriodSeconds = tSta;
            joint.Applied = true;
            joint.IsStationHold = true;
            joint.AlignAnchorPid = destSet.StationAnchorPid;
            joint.IsJointHold = true;
            joint.JointSecondaryPeriodSeconds = tRot;
            joint.JointSecondaryToleranceSeconds = tolRot;
            joint.JointMaxWholeHoldPeriods = maxWhole;
            joint.JointChosenWindowK = solve.ChosenWindowK;
            joint.JointResidualSeconds = solve.ResidualSeconds;
            return joint;
        }

        /// <summary>
        /// The MULTI-MOON configuration hold for a 2+-constrained-moon (Jool-class) destination
        /// (M-MIS-6, docs/dev/design-mission-multimoon-alignment.md). Model: all recorded moon
        /// encounters shift TOGETHER under one arrival hold, so one hold aligns the tour iff the
        /// moons' JOINT CONFIGURATION recurs - T_config = k * P_anchor found by the shipped
        /// near-coincidence scan (<see cref="MissionPeriodicity.TryFindNextScheduleK"/>, anchor =
        /// the smallest-duty participant per <see cref="MissionPeriodicity.SelectAnchorConstraintIndex"/>,
        /// design D1/D2). Participants are the moon Orbital constraints (SOI tolerance,
        /// mode-independent, never dropped) plus the constrained moons' landing rotations and the
        /// target's DestRotation (mode ladder; Drop removes them; a tidally locked moon's rotation
        /// equals its orbital period and collapses for free - design D3). Engage requires the scan
        /// to accept within the slack-clamped anchor budget AND the hold-aware
        /// <see cref="DestinationArrivalSolver.SolveArrivalWindow"/> pick (holdAlignPeriodSeconds =
        /// T_config) to land window k=1 within tolerance; ANY miss fails closed to faithful with an
        /// amber reason naming the shape, periods and residuals - the pre-M-MIS-6 silent Jool-class
        /// decline is gone (design D4/D6, never silent). On engage the clock runs the SHIPPED
        /// single-period per-loop hold with AlignPeriodSeconds = T_config (design D8, zero clock
        /// change); the finite aligned-window horizon (drift accumulates across loops, design
        /// section 2) is counted and logged, reporting only. Pure.
        /// </summary>
        internal static ArrivalHoldResult ComputeMultiMoonConfigHold(
            DestinationConstraintExtractor.DestinationConstraintSet destSet,
            string targetBody,
            double recordedArrivalUT,
            TransitedBodyRotationMode mode,
            double phaseAnchorUT,
            double spanStartUT,
            IReadOnlyList<GhostPlaybackLogic.LoopCut> loiterCuts,
            IBodyInfo bodyInfo,
            double windowSpacingSeconds,
            string launchBodyName,
            double loopSlackSeconds)
        {
            var ic = CultureInfo.InvariantCulture;
            string shape = destSet.ConstrainedMoonCount.ToString(ic) + " constrained moons of '"
                + (targetBody ?? "?") + "'";

            // Destination-side loiter-cut rigidity guard (the shipped L8 refusal): a cut excised
            // AFTER the SOI entry breaks the entry-referenced rigidity that lets ONE hold align
            // every encounter. Amber (design D6: never silent).
            if (loiterCuts != null)
            {
                for (int i = 0; i < loiterCuts.Count; i++)
                    if (loiterCuts[i].EndUT > recordedArrivalUT)
                        return ArrivalHoldResult.NoneWithAmber(
                            shape + ": a destination-side loiter cut breaks the entry-referenced " +
                            "configuration hold (deferred to the M-MIS-2 P4 re-timer); faithful");
            }

            // Participants (design D3): moon Orbitals + DestRotation (in Constraints) + the
            // constrained moons' landing rotations (MoonRotations). Drop removes TRANSITED
            // rotations (the same filter SolveArrivalWindow applies); degenerate periods skipped.
            var participants = new List<PhaseConstraint>();
            CollectConfigParticipants(destSet.Constraints, mode, launchBodyName, participants);
            CollectConfigParticipants(destSet.MoonRotations, mode, launchBodyName, participants);
            if (participants.Count == 0)
                return ArrivalHoldResult.None; // defensive: >=2 valid moon periods always remain

            var tolerances = new double[participants.Count];
            for (int i = 0; i < participants.Count; i++)
            {
                tolerances[i] = MissionPeriodicity.ScheduleToleranceSecondsFor(
                    participants[i], bodyInfo, launchBodyName, mode);
            }

            // The recurrence lattice anchor: the smallest-duty participant (design D2 - pinning
            // the tightest band exactly wastes the least tolerance and maximizes the aligned
            // horizon; Vall for the stock inner three).
            int anchorIdx = MissionPeriodicity.SelectAnchorConstraintIndex(
                participants, bodyInfo, launchBodyName, mode);
            double pAnchor = participants[anchorIdx].PeriodSeconds;

            // Anchor-multiple budget: the constant cap, clamped by the loop slack so the worst
            // hold (< T_config = k * pAnchor) always fits the idle gap - the clock's defensive
            // hold clamp must never silently truncate the configuration hold.
            int maxK = ConfigAnchorLookaheadMultiples;
            if (!double.IsNaN(loopSlackSeconds) && !double.IsInfinity(loopSlackSeconds))
            {
                long slackK = (long)System.Math.Floor(loopSlackSeconds / pAnchor);
                if (slackK < maxK)
                    maxK = (int)System.Math.Max(0, slackK);
            }
            if (maxK < 1)
            {
                return ArrivalHoldResult.NoneWithAmber(
                    shape + ": loop slack " +
                    (double.IsNaN(loopSlackSeconds) ? "unknown" : loopSlackSeconds.ToString("F0", ic) + "s") +
                    " leaves no whole configuration-anchor-period hold budget (anchor '" +
                    (participants[anchorIdx].BodyName ?? "?") + "' P=" + pAnchor.ToString("F0", ic) +
                    "s); faithful");
            }

            // T_config = k * pAnchor via the shipped near-coincidence scan (design D1): the first
            // whole anchor multiple where EVERY other participant is within its own tolerance.
            var otherPeriods = new List<double>(participants.Count - 1);
            var otherTolerances = new List<double>(participants.Count - 1);
            for (int i = 0; i < participants.Count; i++)
            {
                if (i == anchorIdx)
                    continue;
                otherPeriods.Add(participants[i].PeriodSeconds);
                otherTolerances.Add(tolerances[i]);
            }
            MissionPeriodicity.TryFindNextScheduleK(
                pAnchor, otherPeriods, otherTolerances, 1, maxK,
                out long foundK, out double scanResidual, out bool scanWithin);
            if (!scanWithin)
            {
                return ArrivalHoldResult.NoneWithAmber(
                    shape + ": joint configuration (" + DescribeParticipants(participants) +
                    ") does not recur within tolerance in k*P_anchor (anchor '" +
                    (participants[anchorIdx].BodyName ?? "?") + "', k<=" + maxK.ToString(ic) +
                    "): worst residual " + scanResidual.ToString("F0", ic) + "s at " +
                    WorstViolatorLabel(participants, tolerances, anchorIdx, foundK * pAnchor) +
                    "; faithful");
            }
            double tConfig = foundK * pAnchor;

            // The hold-aware window pick (the M-MIS-4 SolveArrivalWindow wiring, reused): window
            // k=1 sampled at the T_config-lattice snap must land within every tolerance. Errors
            // grow with the lattice index, so a window-1 miss means every window misses.
            DestinationArrivalSolver.DestinationArrivalSolve solve =
                DestinationArrivalSolver.SolveArrivalWindow(
                    windowSpacingSeconds, participants, bodyInfo, launchBodyName, mode,
                    kStart: 1, lookaheadWindows: JointSolveLookaheadWindows,
                    holdAlignPeriodSeconds: tConfig, maxWholeHoldPeriods: 0);
            if (!solve.WithinTolerance || solve.ChosenWindowK != 1)
            {
                return ArrivalHoldResult.NoneWithAmber(
                    shape + ": configuration window solve declined (method=" + solve.Method +
                    " k=" + solve.ChosenWindowK.ToString(ic) +
                    " residual=" + solve.ResidualSeconds.ToString("F0", ic) +
                    "s Tconfig=" + tConfig.ToString("F0", ic) + "s over " +
                    JointSolveLookaheadWindows.ToString(ic) + " windows); faithful");
            }

            // The finite aligned horizon (design D6): consecutive in-tolerance windows from k=1,
            // capped for reporting. Loops past it keep the lattice hold (bounded, slowly-growing
            // configuration error); the count is logged so the degradation is never silent.
            int horizon = DestinationArrivalSolver.CountAlignedWindowPrefix(
                windowSpacingSeconds, ExtractPeriods(participants), tolerances,
                tConfig, ConfigHorizonReportWindows);

            // ENGAGE. A zero base hold (entry already configuration-aligned) substitutes one full
            // T_config to keep the clock's hold>0 gate engaged (the shipped joint-hold trick; the
            // per-loop formula mods it back to the true zero base, no spurious dead time).
            double liveEntryUT = phaseAnchorUT
                + (GhostPlaybackLogic.CompressSpanUT(recordedArrivalUT, loiterCuts) - spanStartUT);
            double w0 = GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(
                recordedArrivalUT, liveEntryUT, tConfig);
            if (!(w0 > 0.0) || double.IsInfinity(w0))
                w0 = tConfig;

            if (!MissionPeriodicity.SuppressLogging)
            {
                ParsekLog.Verbose("ReaimArrival",
                    "config-hold engage dest=" + (targetBody ?? "?") +
                    " participants=" + DescribeParticipants(participants) +
                    " anchor=" + (participants[anchorIdx].BodyName ?? "?") +
                    " k=" + foundK.ToString(ic) +
                    " Tconfig=" + tConfig.ToString("R", ic) + "s" +
                    " scanResidual=" + scanResidual.ToString("F1", ic) + "s" +
                    " window1Residual=" + solve.ResidualSeconds.ToString("F1", ic) + "s" +
                    " alignedWindows" + (horizon >= ConfigHorizonReportWindows ? ">=" : "=") +
                    horizon.ToString(ic) +
                    " mode=" + mode);
            }

            ArrivalHoldResult config = ArrivalHoldResult.None;
            config.HoldSeconds = w0;
            config.HoldAtUT = recordedArrivalUT;
            config.AlignPeriodSeconds = tConfig;
            config.Applied = true;
            config.IsStationHold = false;
            config.AlignAnchorPid = 0;
            config.IsConfigHold = true;
            config.ConfigAlignedWindowHorizon = horizon;
            config.ConfigFirstWindowResidualSeconds = solve.ResidualSeconds;
            return config;
        }

        // Appends the valid configuration participants from one source list: Drop removes
        // TRANSITED rotations (MissionPeriodicity.IsTransitedBodyRotation, the SolveArrivalWindow
        // filter semantics); degenerate periods are skipped.
        private static void CollectConfigParticipants(
            IReadOnlyList<PhaseConstraint> source, TransitedBodyRotationMode mode,
            string launchBodyName, List<PhaseConstraint> into)
        {
            int n = source?.Count ?? 0;
            for (int i = 0; i < n; i++)
            {
                PhaseConstraint c = source[i];
                double p = c.PeriodSeconds;
                if (double.IsNaN(p) || double.IsInfinity(p) || p <= 0.0)
                    continue;
                if (mode == TransitedBodyRotationMode.Drop
                    && MissionPeriodicity.IsTransitedBodyRotation(c, launchBodyName))
                    continue;
                into.Add(c);
            }
        }

        // Compact participant summary for logs/ambers: "Laythe/Vall/Tylo/rot:Laythe".
        private static string DescribeParticipants(IReadOnlyList<PhaseConstraint> participants)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < participants.Count; i++)
            {
                if (i > 0)
                    sb.Append('/');
                if (participants[i].Kind == ConstraintKind.Rotation)
                    sb.Append("rot:");
                sb.Append(participants[i].BodyName ?? "?");
            }
            return sb.ToString();
        }

        // The participant with the largest phase error at the given lattice offset, preferring
        // those OUT of tolerance (for the honest decline amber).
        private static string WorstViolatorLabel(
            IReadOnlyList<PhaseConstraint> participants, double[] tolerances,
            int anchorIdx, double latticeOffsetSeconds)
        {
            var ic = CultureInfo.InvariantCulture;
            int worstIdx = -1;
            double worstErr = -1.0;
            bool worstViolates = false;
            for (int i = 0; i < participants.Count; i++)
            {
                if (i == anchorIdx)
                    continue;
                double err = MissionPeriodicity.CircularPhaseError(
                    latticeOffsetSeconds, participants[i].PeriodSeconds);
                bool violates = err > tolerances[i];
                if (worstIdx < 0
                    || (violates && !worstViolates)
                    || (violates == worstViolates && err > worstErr))
                {
                    worstIdx = i;
                    worstErr = err;
                    worstViolates = violates;
                }
            }
            if (worstIdx < 0)
                return "?";
            PhaseConstraint w = participants[worstIdx];
            return (w.Kind == ConstraintKind.Rotation ? "rot:" : "") + (w.BodyName ?? "?")
                + " (tol " + tolerances[worstIdx].ToString("F0", ic) + "s)";
        }

        private static double[] ExtractPeriods(IReadOnlyList<PhaseConstraint> participants)
        {
            var periods = new double[participants.Count];
            for (int i = 0; i < participants.Count; i++)
                periods[i] = participants[i].PeriodSeconds;
            return periods;
        }
    }
}
