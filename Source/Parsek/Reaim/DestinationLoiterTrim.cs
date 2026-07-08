using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Reaim
{
    // Re-aim Phase 4 (cross-parent destination-SOI arrival alignment), implementation Phase 4 (P4):
    // the DESTINATION-LOITER PRE-LANDING TRIM (the "keepRevs re-timer"). See
    // docs/dev/plans/reaim-destination-loiter-pretrim.md and the parent plan
    // docs/dev/plans/reaim-destination-arrival-alignment.md sections 1, 3, 4.
    //
    // The shipped arrival hold (ArrivalHoldPlanner.ComputeArrivalHold) FAILS CLOSED (returns None,
    // byte-identical clock) for a landing that recorded a destination PARKING LOITER: an
    // entry-referenced hold cannot align the deorbit once a destination loiter cut excises whole
    // periods between SOI entry and deorbit (ArrivalHoldPlanner.cs:116-127). P4 replaces that refusal
    // with a JOINT solve of two bidirectional knobs, anchored at the DEORBIT (the recorded
    // surface-arrival UT) instead of the SOI entry:
    //   - keepRevs trim (EARLIER): the destination loiter cut excises (WholeRevs - r) whole parking
    //     periods from the run START (preserving the recorded exit phase, ReaimLoiterCompressor), which
    //     steps the live deorbit earlier in whole-period jumps;
    //   - continuous arrival hold W in [0, T_rot) (LATER): the shipped
    //     GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds forward minimal shift.
    // Because the continuous hold reaches any rotation phase, the deorbit ALWAYS aligns; the trim only
    // shapes how much of the alignment is paid as a frozen-ghost WAIT (W) versus replayed loiter. So
    // the objective is to MINIMIZE W (minimize the unnatural frozen hold at SOI arrival): default to
    // keepRevs = 1 (today's maximal compression) and keep MORE recorded revolutions only when doing so
    // reduces the frozen hold by more than the alignment tolerance.
    //
    // PURE: no Unity; all live-body data comes through the IBodyInfo seam, all timeline data through
    // the LoiterRun / LoopCut structs. UNWIRED this phase; the builder wiring (run partition by
    // EndUT, the same-launch gather, the final cut assembly) is P4.2.
    internal static class DestinationLoiterTrim
    {
        /// <summary>The default cap on kept destination-loiter revolutions (plan section 8 decision 2):
        /// the trim search keeps at most this many recorded revolutions, so a long parking loiter is
        /// always compressed (it never replays dozens of revs just to shave the frozen hold).</summary>
        internal const int DefaultMaxKeepRevs = 10;

        /// <summary>Epsilon (seconds) by which a candidate run's <c>EndUT</c> may exceed the recorded
        /// deorbit UT and still count as "the loiter immediately preceding the deorbit" (the parking
        /// run ends AT the deorbit, so its EndUT lands at / a hair past the recorded surface UT).</summary>
        internal const double DeorbitSelectionEpsilonSeconds = 1.0;

        /// <summary>The joint trim + hold decision. Pure value; nothing persisted.</summary>
        internal struct DestinationLoiterTrimResult
        {
            /// <summary>False =&gt; the caller takes the existing ComputeArrivalHold path (fail closed,
            /// byte-identical). True =&gt; the caller assembles <see cref="DestinationCut"/> into the
            /// loiter cuts and applies <see cref="HoldSeconds"/> as the arrival hold.</summary>
            public bool Applied;

            /// <summary>The chosen kept-revolution count for the destination loiter run (&gt;= 1).</summary>
            public int DestinationKeepRevs;

            /// <summary>The recorded whole-revolution count of the selected destination loiter run
            /// (for the summary log: keepRevs=R/WholeRevs).</summary>
            public long DestinationWholeRevs;

            /// <summary>The destination loiter cut at the chosen keepRevs. Valid only when
            /// <see cref="HasDestinationCut"/>.</summary>
            public GhostPlaybackLogic.LoopCut DestinationCut;

            /// <summary>False when keepRevs == WholeRevs (the full loiter is kept, no excision).</summary>
            public bool HasDestinationCut;

            /// <summary>The arrival hold W_0 (seconds, in [0, T_rot)) at the chosen keepRevs, anchored
            /// at the deorbit. 0 means already aligned by the trim alone.</summary>
            public double HoldSeconds;

            /// <summary>The hold INSERTION boundary = the SOI-entry UT (recordedArrivalUT). The deorbit
            /// lies past it, so the hold defers the whole in-SOI replay including the deorbit.</summary>
            public double HoldAtUT;

            /// <summary>T_rot (the destination rotation period the hold aligns).</summary>
            public double AlignPeriodSeconds;

            internal static DestinationLoiterTrimResult None =>
                new DestinationLoiterTrimResult
                {
                    Applied = false,
                    DestinationKeepRevs = 1,
                    DestinationWholeRevs = 0,
                    DestinationCut = default(GhostPlaybackLogic.LoopCut),
                    HasDestinationCut = false,
                    HoldSeconds = 0.0,
                    HoldAtUT = double.NaN,
                    AlignPeriodSeconds = double.NaN,
                };
        }

        /// <summary>
        /// The joint destination-loiter trim + arrival hold for a re-aim LANDING that recorded a
        /// destination parking loiter, or <see cref="DestinationLoiterTrimResult.None"/> (fail closed to
        /// faithful, leaving the span clock byte-identical) when P4 does not apply.
        ///
        /// GATES that return None (each preserving the byte-identical-off invariant): an unsupported /
        /// station-bearing / orbit-only / Drop-mode / degenerate destination (the shipped
        /// ComputeArrivalHold owns those), no destination loiter run, or a selected run with fewer than
        /// two whole revolutions (a 0/1-rev run produces no cut and the shipped hold already aligns it).
        ///
        /// When it applies, the destination cut excises whole periods from the chosen loiter run and the
        /// hold defers the in-SOI replay so the deorbit lands on the recorded destination rotation phase.
        /// Pure.
        /// </summary>
        /// <param name="allRuns">The loiter runs detected across the re-aim source member AND every
        /// member sharing its launch identity (P4.2 gathers these; the destination parking may live in a
        /// same-launch chain continuation, not the classified transfer member - see plan B1).</param>
        /// <param name="launchSideCuts">The launch-side cuts (runs ending at/before recordedArrivalUT,
        /// at keepRevs=1), already byte-identical to today for those runs.</param>
        /// <param name="destSet">The destination constraint set (the Supported / HasStation /
        /// HasLandingRotation gates).</param>
        /// <param name="destRotation">The target-body Rotation constraint (its period + the schedule
        /// tolerance dispatch).</param>
        internal static DestinationLoiterTrimResult SolveTrimAndHold(
            IReadOnlyList<ReaimLoiterCompressor.LoiterRun> allRuns,
            IReadOnlyList<GhostPlaybackLogic.LoopCut> launchSideCuts,
            DestinationConstraintExtractor.DestinationConstraintSet destSet,
            PhaseConstraint destRotation,
            string launchBodyName,
            string targetBody,
            double recordedArrivalUT,
            double recordedDestSurfaceUT,
            double rotationPeriod,
            double phaseAnchorUT,
            double spanStartUT,
            double spanSeconds,
            TransitedBodyRotationMode mode,
            int maxKeepRevs,
            IBodyInfo bodyInfo)
        {
            // --- Gates (fail closed; the shipped ComputeArrivalHold owns each of these shapes) -------
            if (bodyInfo == null || string.IsNullOrEmpty(targetBody))
                return Fail("no-bodyinfo");
            if (double.IsNaN(recordedArrivalUT) || double.IsNaN(recordedDestSurfaceUT)
                || double.IsNaN(phaseAnchorUT) || double.IsNaN(spanStartUT))
                return Fail("nan-input");
            // P4 is the rotation/landing pre-landing trim ONLY. Unsupported, station, orbit-only and
            // Drop-mode destinations stay on the shipped path (station holds, orbit-only None, the
            // Drop A/B rotation gate) - byte-identical. A multi-moon (2+ constrained moons) shape is
            // ALSO excluded (M-MIS-6: it is Supported now, but a rotation-only trim would misalign
            // the moon configuration; the config hold owns it, and its L8 guard ambers destination
            // cuts) - the same outcome the pre-M-MIS-6 Unsupported flag produced here.
            if (!destSet.Supported || destSet.HasStation || !destSet.HasLandingRotation
                || destSet.ConstrainedMoonCount >= 2
                || mode == TransitedBodyRotationMode.Drop)
                return Fail("not-a-trimmable-landing");
            if (double.IsNaN(rotationPeriod) || double.IsInfinity(rotationPeriod) || rotationPeriod <= 0.0)
                return Fail("degenerate-rotation-period");
            // The deorbit must lie AFTER the SOI entry (the hold is inserted at entry and must defer the
            // deorbit by the full W). A degenerate ordering means no rigid in-SOI tail to re-time.
            if (!(recordedDestSurfaceUT > recordedArrivalUT))
                return Fail("deorbit-not-after-entry");

            // --- Select the destination loiter run (the parking immediately before the deorbit) -------
            if (!TrySelectDestinationRun(allRuns, targetBody, recordedArrivalUT, recordedDestSurfaceUT,
                    out ReaimLoiterCompressor.LoiterRun destRun))
                return Fail("no-destination-loiter");
            // A 0/1-rev run produces no cut even at keepRevs=1, so the shipped hold already aligns it
            // (no refusal). P4 only matters when the run WOULD produce a destination cut (>= 2 revs).
            if (destRun.WholeRevs < 2
                || double.IsNaN(destRun.PeriodSeconds) || destRun.PeriodSeconds <= 0.0)
                return Fail("destination-loiter-too-short");

            // --- Joint solve: pick keepRevs minimizing the frozen hold W, default r=1 ----------------
            int cap = maxKeepRevs <= 0 ? DefaultMaxKeepRevs : maxKeepRevs;
            int rMax = (int)Math.Min(destRun.WholeRevs, (long)cap);
            // The meaningful-improvement threshold: keep extra recorded revolutions only when they
            // reduce the frozen hold by more than the rotation alignment tolerance (respects the
            // Off/Loose/Precise ladder). A sub-tolerance hold saving is not worth replaying an extra
            // parking revolution. Floored to a tiny positive value so a degenerate tolerance still picks
            // the strict minimum.
            double tol = MissionPeriodicity.ScheduleToleranceSecondsFor(
                destRotation, bodyInfo, launchBodyName, mode);
            if (double.IsNaN(tol) || double.IsInfinity(tol) || tol < 0.0)
                tol = 0.0;

            int bestR = -1;
            double bestW = double.PositiveInfinity;
            for (int r = 1; r <= rMax; r++)
            {
                // The cut at keepRevs=r excises (WholeRevs - r) whole periods from the run start; r ==
                // WholeRevs keeps the full loiter (no cut). r is capped <= WholeRevs by rMax.
                bool hasCut = r < destRun.WholeRevs;
                GhostPlaybackLogic.LoopCut cut = default(GhostPlaybackLogic.LoopCut);
                var trialCuts = new List<GhostPlaybackLogic.LoopCut>();
                if (launchSideCuts != null)
                    trialCuts.AddRange(launchSideCuts);
                if (hasCut)
                {
                    cut = new GhostPlaybackLogic.LoopCut
                    {
                        StartUT = destRun.StartUT,
                        LengthSeconds = (destRun.WholeRevs - r) * destRun.PeriodSeconds,
                    };
                    trialCuts.Add(cut);
                }
                // Span guard (matches the builder's TotalCutLength < span gate): a larger r only shrinks
                // the destination cut, so this is monotone - r=1 is the largest cut. Skip any candidate
                // whose total excision would not leave a positive compressed span.
                if (GhostPlaybackLogic.TotalCutLength(trialCuts) >= spanSeconds)
                    continue;

                double liveSurface = phaseAnchorUT
                    + (GhostPlaybackLogic.CompressSpanUT(recordedDestSurfaceUT, trialCuts) - spanStartUT);
                double w = GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(
                    recordedDestSurfaceUT, liveSurface, rotationPeriod);
                if (double.IsNaN(w) || double.IsInfinity(w))
                    continue;

                if (bestR < 0)
                {
                    // First valid candidate (the smallest valid r, usually r=1) is the baseline: the
                    // most-compressed option.
                    bestR = r;
                    bestW = w;
                }
                else if (w < bestW - tol)
                {
                    // Keep more recorded revolutions ONLY when it cuts the frozen hold meaningfully.
                    bestR = r;
                    bestW = w;
                }
            }

            if (bestR < 0)
                return Fail("no-valid-keeprevs"); // every candidate failed the span guard

            bool keepHasCut = bestR < destRun.WholeRevs;
            var result = new DestinationLoiterTrimResult
            {
                Applied = true,
                DestinationKeepRevs = bestR,
                DestinationWholeRevs = destRun.WholeRevs,
                DestinationCut = keepHasCut
                    ? new GhostPlaybackLogic.LoopCut
                    {
                        StartUT = destRun.StartUT,
                        LengthSeconds = (destRun.WholeRevs - bestR) * destRun.PeriodSeconds,
                    }
                    : default(GhostPlaybackLogic.LoopCut),
                HasDestinationCut = keepHasCut,
                HoldSeconds = bestW,
                HoldAtUT = recordedArrivalUT,
                AlignPeriodSeconds = rotationPeriod,
            };
            LogSolve(targetBody, result, mode);
            return result;
        }

        /// <summary>
        /// Selects the destination parking loiter run = the loiter on <paramref name="targetBody"/>
        /// whose EndUT is the latest among those at or before the recorded deorbit
        /// (<paramref name="recordedDestSurfaceUT"/>, within
        /// <see cref="DeorbitSelectionEpsilonSeconds"/>) and strictly after the SOI entry
        /// (<paramref name="recordedArrivalUT"/>). This excludes a same-target launch-side run (a depot
        /// parked before departure) and, for a capture-then-deorbit-prep two-run shape, picks the run
        /// immediately preceding the deorbit. Falls back to the earliest post-arrival target run when
        /// none ends at/before the deorbit (an unusual shape). Pure.
        /// </summary>
        internal static bool TrySelectDestinationRun(
            IReadOnlyList<ReaimLoiterCompressor.LoiterRun> allRuns,
            string targetBody,
            double recordedArrivalUT,
            double recordedDestSurfaceUT,
            out ReaimLoiterCompressor.LoiterRun destRun)
        {
            destRun = default(ReaimLoiterCompressor.LoiterRun);
            if (allRuns == null || string.IsNullOrEmpty(targetBody))
                return false;

            bool found = false;
            bool foundAtOrBefore = false;
            double bestEndBefore = double.NegativeInfinity; // latest EndUT <= deorbit
            double bestEndAfter = double.PositiveInfinity;   // earliest EndUT > deorbit (fallback)
            for (int i = 0; i < allRuns.Count; i++)
            {
                ReaimLoiterCompressor.LoiterRun run = allRuns[i];
                if (run.BodyName != targetBody)
                    continue;
                if (!(run.EndUT > recordedArrivalUT))
                    continue; // launch-side / pre-entry run on the same body

                if (run.EndUT <= recordedDestSurfaceUT + DeorbitSelectionEpsilonSeconds)
                {
                    if (!foundAtOrBefore || run.EndUT > bestEndBefore)
                    {
                        bestEndBefore = run.EndUT;
                        destRun = run;
                        foundAtOrBefore = true;
                        found = true;
                    }
                }
                else if (!foundAtOrBefore && run.EndUT < bestEndAfter)
                {
                    bestEndAfter = run.EndUT;
                    destRun = run;
                    found = true;
                }
            }
            return found;
        }

        private static DestinationLoiterTrimResult Fail(string reason)
        {
            // The fail-closed reasons are diagnostic only (each defers to the shipped path); keep them
            // out of the steady-state log unless verbose tracing is wanted. No-op placeholder so the
            // reason strings are self-documenting at the call sites above.
            _ = reason;
            return DestinationLoiterTrimResult.None;
        }

        private static void LogSolve(
            string targetBody, DestinationLoiterTrimResult r, TransitedBodyRotationMode mode)
        {
            if (MissionPeriodicity.SuppressLogging)
                return;
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose("Reaim",
                $"dest-trim dest={targetBody} keepRevs={r.DestinationKeepRevs.ToString(ic)}/" +
                $"{r.DestinationWholeRevs.ToString(ic)} " +
                $"cutLen={(r.HasDestinationCut ? r.DestinationCut.LengthSeconds.ToString("F0", ic) : "0")}s " +
                $"W0={r.HoldSeconds.ToString("F1", ic)}s Talign={r.AlignPeriodSeconds.ToString("F0", ic)}s " +
                $"mode={mode}");
        }
    }
}
