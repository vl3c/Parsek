using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Contracts;

namespace Parsek
{
    // Rewind read-back divergence guard run at the top of PatchAll.
    internal static partial class KspStatePatcher
    {
        // ================================================================
        // Rewind read-back divergence guard (audit rec #1,
        // docs/dev/ledger-state-reconstruction-audit.md §8). Reports the case where a
        // Rewind-to-Separation recalc writes an economy BELOW the real career floor.
        // Two-witness floor, downward-only, realized-vs-realized. Warn-and-proceed with no
        // production abort switch: the flag is a reporting signal, not a gate, because a
        // Step-3b resurrected-recovery retirement puts the target legitimately below both
        // witnesses. The abort branch exists only under
        // RewindReadbackGuard.ForceAbortForTesting.
        // ================================================================

        /// <summary>
        /// Verdict from <see cref="ResolveRewindDivergence"/>.
        /// <list type="bullet">
        /// <item><see cref="NotEvaluated"/>: no finite witness for the resource — cannot
        /// build a floor, so no judgement.</item>
        /// <item><see cref="WithinExpectedRange"/>: the recalc target is at or above the
        /// floor (within tolerance) — the normal "career restores / sticks" direction.</item>
        /// <item><see cref="FlaggedDivergence"/>: the recalc target dropped below the floor
        /// past tolerance, OR the target itself is NaN / Infinity.</item>
        /// </list>
        /// </summary>
        internal enum RewindReadbackVerdict { NotEvaluated, WithinExpectedRange, FlaggedDivergence }

        // Absolute downward tolerances for the read-back floor comparison. Set a touch
        // looser than the per-resource patch no-op thresholds (funds 0.01, science 0.001,
        // rep 0.01) so float-rounding / curve-application noise between the realized
        // running value and the witnessed live value never trips a false divergence,
        // while still flagging any economically meaningful drop. Funds/science floors are
        // large pools so 1.0 / 0.1 are still tight; rep is small-magnitude so 0.1 stays
        // sensitive.
        internal const double RewindReadbackFundsTolerance = 1.0;
        internal const double RewindReadbackScienceTolerance = 0.1;
        internal const double RewindReadbackReputationTolerance = 0.1;

        /// <summary>
        /// Pure read-back divergence predicate. Builds a per-resource floor from the two
        /// economy witnesses (<paramref name="eBefore"/> = pre-rewind live career,
        /// <paramref name="eRp"/> = loaded quicksave / OLD at-RP economy) using only the
        /// FINITE witnesses, then judges the recalc's realized running target against it:
        /// <list type="bullet">
        /// <item>A NaN / Infinity target is itself corruption → <see cref="RewindReadbackVerdict.FlaggedDivergence"/>
        /// (floor / delta returned as NaN).</item>
        /// <item>No finite witness → cannot judge → <see cref="RewindReadbackVerdict.NotEvaluated"/>.</item>
        /// <item><paramref name="recalcRunningTarget"/> &lt; floor − tolerance → downward
        /// clobber → <see cref="RewindReadbackVerdict.FlaggedDivergence"/>; otherwise
        /// <see cref="RewindReadbackVerdict.WithinExpectedRange"/> (upward / restore never flags).</item>
        /// </list>
        /// The min-floor structurally encodes legit drawdown: post-RP spending lowers
        /// <paramref name="eBefore"/>, lowering the floor, and the older quicksave
        /// <paramref name="eRp"/> absorbs uncommitted-in-flight effects — so the guard does
        /// NOT gate on <c>authoritativeReduction</c>. Pure: no KSP singletons, fully
        /// unit-testable. <paramref name="resource"/> is logging-only (keeps it pure).
        /// </summary>
        internal static RewindReadbackVerdict ResolveRewindDivergence(
            string resource,
            double? eBefore, double? eRp,
            double recalcRunningTarget,
            double tolerance,
            out double floor, out double delta)
        {
            // A corrupt target value is divergence by definition — no floor needed.
            if (double.IsNaN(recalcRunningTarget) || double.IsInfinity(recalcRunningTarget))
            {
                floor = double.NaN;
                delta = double.NaN;
                return RewindReadbackVerdict.FlaggedDivergence;
            }

            bool beforeFinite = eBefore.HasValue && IsFinite(eBefore.Value);
            bool rpFinite = eRp.HasValue && IsFinite(eRp.Value);

            if (beforeFinite && rpFinite)
                floor = Math.Min(eBefore.Value, eRp.Value);
            else if (beforeFinite)
                floor = eBefore.Value;
            else if (rpFinite)
                floor = eRp.Value;
            else
            {
                floor = double.NaN;
                delta = double.NaN;
                return RewindReadbackVerdict.NotEvaluated;
            }

            delta = recalcRunningTarget - floor;

            // Downward-only: at or above (floor - tolerance) is the normal restore / stick
            // direction. Strictly below it is the silent-clobber signature.
            if (recalcRunningTarget >= floor - tolerance)
                return RewindReadbackVerdict.WithinExpectedRange;

            return RewindReadbackVerdict.FlaggedDivergence;
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        /// <summary>
        /// Runs the rewind read-back guard over the three economy modules at the rewind
        /// apply boundary. For each resource it computes the SAME realized running value the
        /// matching <c>Patch*</c> writes (funds <c>GetRunningBalance()</c>, science the
        /// pending-adjusted <see cref="ComputePendingAdjustedRunningScience"/>, rep
        /// <c>GetRunningRep()</c>), resolves the verdict against the two witnesses, and logs
        /// every branch. Returns <c>true</c> iff (any resource flagged divergence) AND the
        /// abort test seam is forced - i.e. PatchAll should abort. The production path is
        /// warn-and-proceed and always returns <c>false</c>. Early-returns <c>false</c> when
        /// the guard is not armed, so it is inert on ordinary (non-rewind) recalc patches.
        /// </summary>
        internal static bool RunRewindReadbackGuard(
            ScienceModule science, FundsModule funds, ReputationModule reputation,
            bool authoritativeReduction)
        {
            if (!RewindReadbackGuard.Armed)
                return false;

            bool anyFlagged = false;

            anyFlagged |= EvaluateReadbackResource(
                "funds",
                RewindReadbackGuard.Before.Funds,
                RewindReadbackGuard.Loaded.Funds,
                funds != null ? (double?)funds.GetRunningBalance() : null,
                RewindReadbackFundsTolerance,
                authoritativeReduction);

            anyFlagged |= EvaluateReadbackResource(
                "science",
                RewindReadbackGuard.Before.Science,
                RewindReadbackGuard.Loaded.Science,
                science != null
                    ? (double?)ComputePendingAdjustedRunningScience(science)
                    : null,
                RewindReadbackScienceTolerance,
                authoritativeReduction);

            anyFlagged |= EvaluateReadbackResource(
                "reputation",
                RewindReadbackGuard.Before.Reputation.HasValue
                    ? (double?)RewindReadbackGuard.Before.Reputation.Value : null,
                RewindReadbackGuard.Loaded.Reputation.HasValue
                    ? (double?)RewindReadbackGuard.Loaded.Reputation.Value : null,
                reputation != null ? (double?)reputation.GetRunningRep() : null,
                RewindReadbackReputationTolerance,
                authoritativeReduction);

            return anyFlagged && RewindReadbackGuard.ForceAbortForTesting;
        }

        // Evaluates one resource and logs its branch. Returns true iff flagged divergence.
        private static bool EvaluateReadbackResource(
            string resource, double? eBefore, double? eRp, double? recalcRunningTarget,
            double tolerance, bool authoritativeReduction)
        {
            // No module / no target to read — record the skip and bail.
            if (!recalcRunningTarget.HasValue)
            {
                ParsekLog.Verbose(RewindReadbackTag,
                    $"skipped resource={resource} no recalc target (module null)");
                return false;
            }

            RewindReadbackVerdict verdict = ResolveRewindDivergence(
                resource, eBefore, eRp, recalcRunningTarget.Value, tolerance,
                out double floor, out double delta);

            string target = recalcRunningTarget.Value.ToString("R", IC);

            switch (verdict)
            {
                case RewindReadbackVerdict.NotEvaluated:
                    ParsekLog.Verbose(RewindReadbackTag,
                        $"skipped resource={resource} no finite witness " +
                        $"(eBefore={FmtNullable(eBefore)} eRp={FmtNullable(eRp)} target={target})");
                    return false;

                case RewindReadbackVerdict.WithinExpectedRange:
                    ParsekLog.Verbose(RewindReadbackTag,
                        $"within-expected-range resource={resource} " +
                        $"eBefore={FmtNullable(eBefore)} eRp={FmtNullable(eRp)} " +
                        $"floor={floor.ToString("R", IC)} target={target} " +
                        $"delta={delta.ToString("R", IC)} tolerance={tolerance.ToString("R", IC)}");
                    return false;

                case RewindReadbackVerdict.FlaggedDivergence:
                    ParsekLog.Warn(RewindReadbackTag,
                        $"FLAGGED DIVERGENCE resource={resource} " +
                        $"eBefore={FmtNullable(eBefore)} eRp={FmtNullable(eRp)} " +
                        $"floor={floor.ToString("R", IC)} target={target} " +
                        $"delta={delta.ToString("R", IC)} tolerance={tolerance.ToString("R", IC)} " +
                        $"authoritativeReduction={authoritativeReduction}: the recalc target is below " +
                        "the pre-rewind / rewind-point floor and is written anyway under the " +
                        "authoritative rewind. Two known legitimate causes: a Step-3b " +
                        "resurrected-recovery retirement after post-RP spending, and an earning " +
                        "channel the ledger does not model (strategy conversion, mod grant)");
                    return true;

                default:
                    return false;
            }
        }

        private const string RewindReadbackTag = "RewindReadback";

        private static string FmtNullable(double? v)
        {
            return v.HasValue ? v.Value.ToString("R", IC) : "none";
        }
    }
}
