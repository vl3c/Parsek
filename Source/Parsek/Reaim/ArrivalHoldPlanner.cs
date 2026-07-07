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
        /// leaving the span clock byte-identical): an unsupported destination (2+ moons Jool-class, or
        /// the still-deferred D8 station-bearing duals station+moon / moon-orbiting-station - those
        /// also carry <see cref="ArrivalHoldResult.AmberReason"/>), an orbit-only arrival with no
        /// station, a landing with alignment OFF (Drop mode - the mode gates ONLY the rotation hold:
        /// it is the transited-body ROTATION alignment A/B, and the station hold is a fully automatic
        /// alignment with no toggle per design D4), or a degenerate align period all return None. The
        /// D8 landing+station dual routes through the JOINT hold
        /// (<see cref="ComputeJointArrivalHold"/>, the post-M4c SolveArrivalWindow wiring; the
        /// windowSpacing / launchBody / loopSlack parameters feed it and are unused by every other
        /// shape). Otherwise the hold W is the minimal forward delay that lands the SOI entry at the
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
            // Unsupported destination: no hold. A station-bearing failure (the still-deferred D8
            // duals: station+moon, a moon-orbiting station, a station-bearing Jool-class
            // destination) carries the reason as the arrival amber so the UI can explain WHY the
            // alignment stays faithful; the pre-existing no-station Jool-class path stays silent
            // (byte-identical).
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
        private static ArrivalHoldResult ComputeJointArrivalHold(
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
            // substitutes one full station period so the clock's hold>0 gate stays engaged - the
            // station stays exact (whole period) and the extension search still runs.
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
    }
}
