using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Reaim
{
    // Re-aim Phase 4 (cross-parent destination-SOI arrival-UT alignment), implementation Phase 1:
    // the PURE arrival-window solver. See docs/dev/plans/reaim-destination-arrival-alignment.md
    // sections 1-3.
    //
    // The problem: a looped re-aimed interplanetary LANDING pad-aligns only the LAUNCH body; nothing
    // aligns the DESTINATION body's rotation phase (or an inner moon's orbital phase) across the loop
    // shift, so the inertial arrival orbit diverges from the body-fixed landing. The fix decouples the
    // two ends: the launch is already pad-aligned per window, and this step SELECTS which of the fixed
    // synodic arrival windows best matches the destination configuration.
    //
    // The recurrence math is the SAME zero-drift primitive the launch side uses
    // (MissionPeriodicity.TryFindNextScheduleK / CircularPhaseError), with one deliberate relabel: the
    // ANCHOR period is the SYNODIC window spacing (the quantum between successive arrival windows), and
    // the destination phase constraints (DestRotation, and 0/1 MoonConfig) are the "others" sampled at
    // k*synodic. The synodic grid is externally fixed by the transfer geometry (we cannot arrive at an
    // arbitrary instant), so we do NOT pick the anchor among the constraints - the window step IS the
    // anchor. Because the whole recorded in-SOI timeline shifts RIGIDLY by k*synodic here (the integer
    // loiter re-timer that breaks that rigidity is a later phase), both the surface-arrival and
    // SOI-entry references shift by the same k*synodic, so their in-mission phase offsets cancel and
    // the residual at window k is an ABSOLUTE function of k - it never accumulates.
    //
    // Window-index-PURE: no resolver / loiter / render / Unity references; all live-body data comes
    // through the IBodyInfo seam. The candidate surface-arrival UTs (the loiter-re-timed variants) are
    // a LATER phase; here the timeline shift is rigid, so the constraint periods alone drive selection.
    internal static class DestinationArrivalSolver
    {
        // Two constraint periods within this relative tolerance are ONE effective constraint
        // (tidal-collapse): e.g. tidally-locked Ike's orbit period equals Duna's rotation period to ~6
        // significant figures, so an arrival that matches Duna's rotation auto-matches Ike. This mirrors
        // MissionPeriodicity.PeriodEqualityRelTolerance (private there); it is replicated locally ONLY
        // for the honest effective-constraint COUNT in the summary log. The residual math needs no
        // pre-pass: TryFindNextScheduleK's worst-of objective already collapses coincident periods for
        // free (each near-identical period yields the same ~0 phase error at the aligned k).
        internal const double PeriodEqualityRelTolerance = 1e-6;

        // The joint-hold budget in whole hold-lattice periods (the M-MIS-4 post-M4c wiring for the D8
        // landing+station dual): the per-loop arrival hold aligns the STATION period exactly and may
        // extend by up to this many whole station periods so the landing ROTATION also lands within its
        // mode tolerance. A constant, not a setting (the MaxExtraLoiterRevs precedent): 64 station
        // orbits bounds the worst per-loop dead time to ~(64+1)*T_station (a few Kerbin days for an
        // LDO-class station), negligible against a multi-year synodic cadence but a real frozen-ghost
        // cost, so geometry needing more fails closed (amber) instead of holding for weeks.
        internal const int MaxJointHoldWholePeriods = 64;

        // Sample horizon for the joint-hold lattice feasibility scan (PlanJointHoldLattice): how many
        // consecutive hold-lattice points the run-length check walks. Consecutive-miss runs of a circle
        // rotation take only a few distinct lengths (three-distance-theorem structure), so a horizon
        // this many times the budget observes every run length that occurs; an exactly commensurate
        // ratio is periodic well within it. Build-time only, one pass, never in the clock hot path.
        internal const int JointLatticeScanHorizon = 8192;

        /// <summary>
        /// The result of selecting an arrival window. Pure value: the chosen window index, the worst
        /// destination-constraint phase error at that window, whether it was within tolerance (vs the
        /// bounded-best fallback), the count of DISTINCT (tidal-collapsed) constraints, and a method tag.
        /// </summary>
        internal struct DestinationArrivalSolve
        {
            public long ChosenWindowK;
            public double ResidualSeconds;
            public bool WithinTolerance;
            public int EffectiveConstraintCount;
            public string Method;

            /// <summary>Hold-aware sampling only: the whole-hold-period extension i chosen at the
            /// accepted window (the first-loop preview of the clock's per-loop i_N search). 0 when
            /// hold-aware sampling is off or no extension was needed.</summary>
            public long ChosenHoldWholePeriods;
        }

        /// <summary>
        /// Feasibility verdict for the joint-hold lattice (the D8 landing+station dual): can EVERY
        /// loop's arrival, station-exact by construction, be extended by at most
        /// <see cref="MaxHoldWholePeriods"/> whole station periods so the rotation phase error is
        /// within tolerance? Pure value from <see cref="PlanJointHoldLattice"/>.
        /// </summary>
        internal struct JointHoldLatticePlan
        {
            /// <summary>True when every scanned lattice offset reaches an in-tolerance rotation phase
            /// within the whole-period budget - the all-loops guarantee the joint hold needs.</summary>
            public bool Feasible;

            /// <summary>The longest observed run of consecutive OUT-of-tolerance lattice points (the
            /// worst whole-period extension any loop would need, minus the in-tolerance endpoint).</summary>
            public int WorstMissRun;

            /// <summary>The whole-period budget the scan tested against.</summary>
            public int MaxHoldWholePeriods;

            /// <summary>Upper bound on the per-loop hold under this plan:
            /// (budget + 1) * holdPeriod (station-lattice base &lt; one period, plus the extension).</summary>
            public double MaxHoldSeconds;
        }

        /// <summary>
        /// Select the arrival window index k in [<paramref name="kStart"/>,
        /// kStart + <paramref name="lookaheadWindows"/>) whose destination configuration best recurs to
        /// the recorded values. Returns the FIRST window where every destination constraint is within
        /// its tolerance, else the BOUNDED-BEST (min worst-residual) window in the horizon (amber).
        ///
        /// <paramref name="windowSpacingSeconds"/> is the synodic spacing between arrival windows (the
        /// anchor period). <paramref name="destConstraints"/> are the destination-side phase constraints
        /// (a DestRotation Rotation constraint and at most one MoonConfig Orbital constraint in this
        /// phase, plus - under hold-aware sampling - the destination STATION's VesselOrbital constraint).
        /// <paramref name="mode"/> follows the existing Off/Loose/Precise = Drop/Loose/Tight
        /// ladder: Drop pre-filters the transited-body (destination) rotation constraint out here (its
        /// body-fixed landing self-anchors); the MoonConfig (Orbital) constraint is NEVER dropped.
        ///
        /// HOLD-AWARE SAMPLING (the M-MIS-4 post-M4c joint wiring): when
        /// <paramref name="holdAlignPeriodSeconds"/> is a valid period, the per-window residual is no
        /// longer sampled at the raw k*synodic offset. The per-loop arrival hold first snaps the
        /// arrival FORWARD onto the hold lattice (the station period - that constraint becomes EXACT,
        /// residual 0), then may extend by up to <paramref name="maxWholeHoldPeriods"/> whole hold
        /// periods; the remaining constraints are sampled at the extended point and the smallest
        /// in-tolerance extension i is chosen per window (mirroring the clock's per-loop i_N search).
        /// Constraints whose period matches the hold period within
        /// <see cref="PeriodEqualityRelTolerance"/> (the station itself, a tidally-collapsed twin)
        /// contribute residual 0. Omitting the parameters preserves the raw-grid sampling
        /// byte-identically. Pure.
        /// </summary>
        internal static DestinationArrivalSolve SolveArrivalWindow(
            double windowSpacingSeconds,
            IReadOnlyList<PhaseConstraint> destConstraints,
            IBodyInfo bodyInfo,
            string launchBodyName,
            TransitedBodyRotationMode mode,
            long kStart,
            int lookaheadWindows,
            double holdAlignPeriodSeconds = double.NaN,
            int maxWholeHoldPeriods = 0)
        {
            var result = new DestinationArrivalSolve
            {
                ChosenWindowK = kStart,
                ResidualSeconds = 0.0,
                WithinTolerance = true,
                EffectiveConstraintCount = 0,
                Method = "no-constraint"
            };

            // Filter: under Drop, the transited (destination) Rotation constraint is removed - its
            // body-fixed landing self-anchors and the moon/SOI tolerance governs (the same semantics
            // ScheduleToleranceSecondsFor documents: Drop never reaches the tolerance dispatch).
            // Orbital (MoonConfig) is never dropped. Degenerate-period constraints are skipped.
            var active = new List<PhaseConstraint>();
            int rawCount = destConstraints?.Count ?? 0;
            int droppedRotation = 0;
            int skippedDegenerate = 0;
            for (int i = 0; i < rawCount; i++)
            {
                PhaseConstraint c = destConstraints[i];
                double p = c.PeriodSeconds;
                if (double.IsNaN(p) || double.IsInfinity(p) || p <= 0.0)
                {
                    skippedDegenerate++;
                    continue;
                }
                bool transitedRotation = c.Kind == ConstraintKind.Rotation
                    && !string.IsNullOrEmpty(launchBodyName)
                    && c.BodyName != launchBodyName;
                if (mode == TransitedBodyRotationMode.Drop && transitedRotation)
                {
                    droppedRotation++;
                    continue;
                }
                active.Add(c);
            }

            if (active.Count == 0)
            {
                // Nothing to align (Off with no constrained moon, or all-degenerate): every window is
                // faithful by construction; arrive at the first candidate window.
                LogSolve(destConstraints, result, mode, lookaheadWindows, droppedRotation, skippedDegenerate);
                return result;
            }

            if (double.IsNaN(windowSpacingSeconds) || double.IsInfinity(windowSpacingSeconds)
                || windowSpacingSeconds <= 0.0 || lookaheadWindows <= 0)
            {
                // A degenerate window spacing or an empty horizon cannot be solved: fail closed to the
                // un-aligned (faithful) window EXPLICITLY, rather than letting TryFindNextScheduleK return
                // a k=0 / NaN-residual sentinel. Callers (Phase 3) must pass a valid synodic spacing.
                result.ChosenWindowK = kStart;
                result.ResidualSeconds = 0.0;
                result.WithinTolerance = false;
                result.EffectiveConstraintCount = CountEffectiveConstraints(active, PeriodEqualityRelTolerance);
                result.Method = "degenerate-input";
                LogSolve(destConstraints, result, mode, lookaheadWindows, droppedRotation, skippedDegenerate);
                return result;
            }

            // Per-constraint schedule tolerances, honoring the Loose band for the transited DestRotation
            // and the SoiRadius/OrbitalVelocity band for the MoonConfig (Orbital, mode-independent).
            var periods = new double[active.Count];
            var tolerances = new double[active.Count];
            for (int i = 0; i < active.Count; i++)
            {
                periods[i] = active[i].PeriodSeconds;
                tolerances[i] = MissionPeriodicity.ScheduleToleranceSecondsFor(
                    active[i], bodyInfo, launchBodyName, mode);
            }

            bool holdAware = !double.IsNaN(holdAlignPeriodSeconds)
                && !double.IsInfinity(holdAlignPeriodSeconds)
                && holdAlignPeriodSeconds > 0.0;

            long foundK;
            double residual;
            bool within;
            long chosenHoldWholePeriods = 0;
            if (holdAware)
            {
                ScanHoldAwareWindows(
                    windowSpacingSeconds, periods, tolerances,
                    holdAlignPeriodSeconds, maxWholeHoldPeriods, kStart, lookaheadWindows,
                    out foundK, out chosenHoldWholePeriods, out residual, out within);
            }
            else
            {
                // The arrival window grid is synodic-spaced; choose which window k so the destination
                // phases (sampled at k*synodic) recur to recorded within tolerance. anchorPeriod =
                // synodic; the destination constraints are the "others".
                MissionPeriodicity.TryFindNextScheduleK(
                    windowSpacingSeconds, periods, tolerances, kStart, lookaheadWindows,
                    out foundK, out residual, out within);
            }

            result.ChosenWindowK = foundK;
            result.ResidualSeconds = residual;
            result.WithinTolerance = within;
            result.EffectiveConstraintCount = CountEffectiveConstraints(active, PeriodEqualityRelTolerance);
            result.Method = ClassifyMethod(active.Count, result.EffectiveConstraintCount, within, holdAware);
            result.ChosenHoldWholePeriods = chosenHoldWholePeriods;

            LogSolve(destConstraints, result, mode, lookaheadWindows, droppedRotation, skippedDegenerate);
            return result;
        }

        // The hold-aware per-window scan (the joint-hold twin of TryFindNextScheduleK's base scan,
        // reusing CircularPhaseError and the same accept-first / bounded-best shape). Per window k the
        // pre-hold offset is delta = k*windowSpacing; the hold snaps it FORWARD to the hold lattice
        // (wBase = (-delta) mod holdPeriod, so the held constraint is exact) and may extend by i whole
        // hold periods; the non-held constraints are evaluated at delta + wBase + i*holdPeriod and the
        // smallest in-tolerance i wins. Held-period constraints (period == holdPeriod within
        // PeriodEqualityRelTolerance) contribute 0 by construction and are skipped.
        private static void ScanHoldAwareWindows(
            double windowSpacingSeconds,
            double[] periods, double[] tolerances,
            double holdPeriodSeconds, int maxWholeHoldPeriods,
            long kStart, int lookaheadWindows,
            out long foundK, out long foundHoldWholePeriods,
            out double residualSeconds, out bool withinTolerance)
        {
            long bestK = kStart;
            long bestI = 0;
            double bestResidual = double.PositiveInfinity;
            if (maxWholeHoldPeriods < 0)
                maxWholeHoldPeriods = 0;

            for (int step = 0; step < lookaheadWindows; step++)
            {
                long k = kStart + step;
                double delta = k * windowSpacingSeconds;
                // Forward snap onto the hold lattice: the smallest W >= 0 with (delta + W) a whole
                // multiple of the hold period. Same normalization as ComputeArrivalAlignHoldSeconds.
                double wBase = (-delta) % holdPeriodSeconds;
                if (wBase < 0.0)
                    wBase += holdPeriodSeconds;

                long chosenI = 0;
                double worstAtChosen = double.PositiveInfinity;
                bool iWithin = false;
                for (long i = 0; i <= maxWholeHoldPeriods && !iWithin; i++)
                {
                    double sample = delta + wBase + i * holdPeriodSeconds;
                    double worst = 0.0;
                    bool allWithin = true;
                    for (int j = 0; j < periods.Length; j++)
                    {
                        if (IsHeldPeriod(periods[j], holdPeriodSeconds))
                            continue; // exact by the hold, residual 0
                        double err = MissionPeriodicity.CircularPhaseError(sample, periods[j]);
                        if (err > worst)
                            worst = err;
                        if (err > tolerances[j])
                            allWithin = false;
                    }
                    if (allWithin)
                    {
                        chosenI = i;
                        worstAtChosen = worst;
                        iWithin = true;
                        break;
                    }
                    if (worst < worstAtChosen)
                    {
                        worstAtChosen = worst;
                        chosenI = i;
                    }
                }

                if (iWithin)
                {
                    foundK = k;
                    foundHoldWholePeriods = chosenI;
                    residualSeconds = worstAtChosen;
                    withinTolerance = true;
                    return;
                }
                if (worstAtChosen < bestResidual)
                {
                    bestResidual = worstAtChosen;
                    bestK = k;
                    bestI = chosenI;
                }
            }

            // No window in the horizon admits an in-tolerance joint arrival under the budget: the
            // bounded-best (min worst-residual) window, mirroring TryFindNextScheduleK's fallback.
            foundK = bestK;
            foundHoldWholePeriods = bestI;
            residualSeconds = double.IsPositiveInfinity(bestResidual) ? 0.0 : bestResidual;
            withinTolerance = false;
        }

        private static bool IsHeldPeriod(double period, double holdPeriodSeconds)
        {
            return Math.Abs(period - holdPeriodSeconds)
                <= PeriodEqualityRelTolerance * Math.Max(1.0, holdPeriodSeconds);
        }

        /// <summary>
        /// Feasibility of the joint-hold lattice for the D8 landing+station dual: per replayed loop the
        /// arrival hold lands the offset on SOME whole multiple m of the station period (station phase
        /// exact), then extends by i whole station periods (i &lt;= <paramref name="maxWholeHoldPeriods"/>)
        /// until the rotation phase error is within <paramref name="secondaryToleranceSeconds"/>. The
        /// loop index makes m effectively arbitrary, so the all-loops guarantee is a RUN-LENGTH bound on
        /// the lattice orbit <c>m*holdPeriod mod secondaryPeriod</c>: every run of consecutive
        /// out-of-tolerance lattice points observed over <see cref="JointLatticeScanHorizon"/> samples
        /// must fit inside the whole-period budget. Reuses <see cref="MissionPeriodicity.CircularPhaseError"/>
        /// per sample (the near-coincidence residual metric); this is the coverage twin of the
        /// zero-drift near-coincidence scan, not new window math. Degenerate inputs are infeasible.
        /// Pure; build-time only.
        /// </summary>
        internal static JointHoldLatticePlan PlanJointHoldLattice(
            double holdPeriodSeconds,
            double secondaryPeriodSeconds,
            double secondaryToleranceSeconds,
            int maxWholeHoldPeriods)
        {
            var plan = new JointHoldLatticePlan
            {
                Feasible = false,
                WorstMissRun = int.MaxValue,
                MaxHoldWholePeriods = maxWholeHoldPeriods,
                MaxHoldSeconds = double.NaN,
            };
            if (double.IsNaN(holdPeriodSeconds) || double.IsInfinity(holdPeriodSeconds)
                || holdPeriodSeconds <= 0.0)
                return plan;
            if (double.IsNaN(secondaryPeriodSeconds) || double.IsInfinity(secondaryPeriodSeconds)
                || secondaryPeriodSeconds <= 0.0)
                return plan;
            if (double.IsNaN(secondaryToleranceSeconds) || secondaryToleranceSeconds < 0.0
                || maxWholeHoldPeriods < 0)
                return plan;

            // Walk the lattice orbit and record the longest run of consecutive misses. The run that is
            // OPEN at the horizon end is counted too (conservative: a longer real run can only start
            // inside the horizon with this many misses already seen).
            int worstRun = 0;
            int currentRun = 0;
            for (int m = 0; m < JointLatticeScanHorizon; m++)
            {
                double err = MissionPeriodicity.CircularPhaseError(
                    m * holdPeriodSeconds, secondaryPeriodSeconds);
                if (err > secondaryToleranceSeconds)
                {
                    currentRun++;
                    if (currentRun > worstRun)
                        worstRun = currentRun;
                }
                else
                {
                    currentRun = 0;
                }
            }

            plan.WorstMissRun = worstRun;
            // A loop landing at the START of the worst miss-run needs `worstRun` whole-period steps to
            // reach the in-tolerance point just past it, so the budget must cover worstRun steps.
            plan.Feasible = worstRun <= maxWholeHoldPeriods;
            plan.MaxHoldSeconds = (maxWholeHoldPeriods + 1) * holdPeriodSeconds;
            return plan;
        }

        /// <summary>
        /// The number of DISTINCT effective constraints after collapsing periods within
        /// <paramref name="relTolerance"/> (relative). Coincident periods (e.g. tidally-locked Ike's
        /// orbit period equal to Duna's rotation period) count once; degenerate periods are ignored.
        /// Used only for the honest summary count - the residual already treats coincident periods as one.
        /// Pure.
        /// </summary>
        internal static int CountEffectiveConstraints(
            IReadOnlyList<PhaseConstraint> constraints, double relTolerance)
        {
            int n = constraints?.Count ?? 0;
            var distinct = new List<double>();
            for (int i = 0; i < n; i++)
            {
                double p = constraints[i].PeriodSeconds;
                if (double.IsNaN(p) || double.IsInfinity(p) || p <= 0.0)
                    continue;
                bool merged = false;
                for (int j = 0; j < distinct.Count; j++)
                {
                    if (Math.Abs(p - distinct[j]) <= relTolerance * Math.Max(1.0, distinct[j]))
                    {
                        merged = true;
                        break;
                    }
                }
                if (!merged)
                    distinct.Add(p);
            }
            return distinct.Count;
        }

        // Topology label for the summary log. The within/bounded-best outcome is appended so a single
        // tag carries both the constraint shape and whether the chosen window was actually in band.
        // Hold-aware sampling relabels the genuine multi-constraint case "joint-hold" (the station-exact
        // + whole-period-extension model); the collapsed cases keep their existing labels.
        private static string ClassifyMethod(int activeCount, int effectiveCount, bool within, bool holdAware = false)
        {
            string topology;
            if (effectiveCount <= 1)
                topology = activeCount > effectiveCount ? "tidal-collapse" : "single-constraint";
            else
                topology = holdAware ? "joint-hold" : "joint-best-fit";
            return within ? topology : topology + "/bounded-best";
        }

        private static void LogSolve(
            IReadOnlyList<PhaseConstraint> rawConstraints,
            DestinationArrivalSolve r,
            TransitedBodyRotationMode mode,
            int lookaheadWindows,
            int droppedRotation,
            int skippedDegenerate)
        {
            if (MissionPeriodicity.SuppressLogging)
                return;
            var ic = CultureInfo.InvariantCulture;
            string destBody = DestinationLabel(rawConstraints);
            ParsekLog.Verbose("ReaimArrival",
                $"arrival-solve dest={destBody} k={r.ChosenWindowK} " +
                $"residual={r.ResidualSeconds.ToString("R", ic)}s within={r.WithinTolerance} " +
                $"mode={mode} effConstraints={r.EffectiveConstraintCount} method={r.Method} " +
                $"holdI={r.ChosenHoldWholePeriods.ToString(ic)} " +
                $"scanned={lookaheadWindows} droppedRot={droppedRotation} skippedDegenerate={skippedDegenerate}");
        }

        // The destination body for the log line = the Rotation constraint's body if present (the
        // landing body), else the first constraint's body, else a sentinel.
        private static string DestinationLabel(IReadOnlyList<PhaseConstraint> constraints)
        {
            int n = constraints?.Count ?? 0;
            for (int i = 0; i < n; i++)
                if (constraints[i].Kind == ConstraintKind.Rotation && !string.IsNullOrEmpty(constraints[i].BodyName))
                    return constraints[i].BodyName;
            for (int i = 0; i < n; i++)
                if (!string.IsNullOrEmpty(constraints[i].BodyName))
                    return constraints[i].BodyName;
            return "?";
        }
    }
}
