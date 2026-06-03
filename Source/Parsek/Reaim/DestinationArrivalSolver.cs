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
        /// phase). <paramref name="mode"/> follows the existing Off/Loose/Precise = Drop/Loose/Tight
        /// ladder: Drop pre-filters the transited-body (destination) rotation constraint out here (its
        /// body-fixed landing self-anchors); the MoonConfig (Orbital) constraint is NEVER dropped. Pure.
        /// </summary>
        internal static DestinationArrivalSolve SolveArrivalWindow(
            double windowSpacingSeconds,
            IReadOnlyList<PhaseConstraint> destConstraints,
            IBodyInfo bodyInfo,
            string launchBodyName,
            TransitedBodyRotationMode mode,
            long kStart,
            int lookaheadWindows)
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

            // The arrival window grid is synodic-spaced; choose which window k so the destination phases
            // (sampled at k*synodic) recur to recorded within tolerance. anchorPeriod = synodic; the
            // destination constraints are the "others".
            MissionPeriodicity.TryFindNextScheduleK(
                windowSpacingSeconds, periods, tolerances, kStart, lookaheadWindows,
                out long foundK, out double residual, out bool within);

            result.ChosenWindowK = foundK;
            result.ResidualSeconds = residual;
            result.WithinTolerance = within;
            result.EffectiveConstraintCount = CountEffectiveConstraints(active, PeriodEqualityRelTolerance);
            result.Method = ClassifyMethod(active.Count, result.EffectiveConstraintCount, within);

            LogSolve(destConstraints, result, mode, lookaheadWindows, droppedRotation, skippedDegenerate);
            return result;
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
        private static string ClassifyMethod(int activeCount, int effectiveCount, bool within)
        {
            string topology;
            if (effectiveCount <= 1)
                topology = activeCount > effectiveCount ? "tidal-collapse" : "single-constraint";
            else
                topology = "joint-best-fit";
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
