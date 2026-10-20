using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Design section 2.12 Rule 2: which of the two loss-swap variants a Re-Fly merge
    /// writes. Persisted on the merge journal (key <c>lossSwapVariant</c>, absent = A).
    /// </summary>
    public enum LossSwapVariant
    {
        /// <summary>The replaced flight's loss rows are retired; the re-fly's rows count.</summary>
        A = 0,

        /// <summary>As A, except the replaced flight's ReputationPenalty loss rows are kept
        /// and the re-fly's own ReputationPenalty loss rows are tombstoned by the merge.</summary>
        B = 1,
    }

    /// <summary>What <see cref="ReFlyMergeDominance"/> decided for one merge.</summary>
    internal sealed class ReFlyMergeDominanceVerdict
    {
        /// <summary>False when no Re-Fly session is live: the check is a no-op and the
        /// merge goes ahead exactly as an ordinary tree commit.</summary>
        internal bool Applicable;
        internal bool Pass = true;
        internal string SessionId;
        internal LossSwapVariant Variant = LossSwapVariant.A;
        internal double RepLossOld;
        internal double RepLossNew;

        /// <summary>On refuse: the first failing pool (<c>funds</c>, <c>science</c>,
        /// <c>reputation</c>, or <c>sealing</c> for a Rule 1 row); on pass: the pool with
        /// the smallest margin, or <c>none</c>.</summary>
        internal string Pool = "none";
        internal double UT = double.NaN;
        internal double Before;
        internal double After;
        /// <summary>A failing spending row (<c>Type:ActionId</c>), when the refusal is an
        /// affordability one.</summary>
        internal string SpendingRow;
        /// <summary><c>kerbal</c> or <c>facility</c> for the world-object clause.</summary>
        internal string WorldKind;
        internal string WorldId;
        /// <summary>The Rule 1 re-check's first sealing row (<c>Type:ActionId</c>).</summary>
        internal string SealingRow;

        internal int AttemptRowCount;
        internal int RetireSetCount;

        internal static ReFlyMergeDominanceVerdict NotApplicable()
            => new ReFlyMergeDominanceVerdict { Applicable = false, Pass = true };
    }

    /// <summary>
    /// Design section 2.12 Rules 2, 4 and 5 and Invariant LS: the dominance check a Re-Fly
    /// merge must pass before its tree commits. <c>L</c> is the Effective Ledger Set with
    /// the attempt's rows left out; <c>L'</c> is what the merge leaves: <c>L</c> minus the
    /// retire set (Variant B keeps the replaced flight's ReputationPenalty loss rows), plus
    /// the attempt's rows (those already in the ledger and the build step's rows not filed
    /// yet; Variant B leaves out the attempt's own ReputationPenalty loss rows). Both are
    /// walked on cloned resource modules. The merge passes when, at every UT, funds,
    /// science and reputation over <c>L'</c> are at least their value over <c>L</c>, every
    /// spending row affordable in <c>L</c> stays affordable in <c>L'</c>, and no committed
    /// row after the rewind UT uses a kerbal or facility that <c>L'</c> leaves dead or
    /// destroyed at that UT where <c>L</c> did not.
    /// </summary>
    internal static class ReFlyMergeDominance
    {
        private const string Tag = "LedgerSwap";

        /// <summary>Tolerance for the balance comparison (float rewards and rounding in
        /// the module walks).</summary>
        internal const double BalanceEpsilon = 0.01;

        /// <summary>The pure check's inputs.</summary>
        internal sealed class Inputs
        {
            internal string SessionId;
            /// <summary>The Effective Ledger Set now (tombstoned rows already out).</summary>
            internal IReadOnlyList<GameAction> Els;
            /// <summary>The recordings of the attempt.</summary>
            internal ICollection<string> AttemptRecordingIds;
            /// <summary>The build step's rows for the pending tree (not in the ledger yet).</summary>
            internal IReadOnlyList<GameAction> NewRows;
            /// <summary>The rows the merge would tombstone (Phase 1 retire set).</summary>
            internal IReadOnlyList<GameAction> RetireSet;
            /// <summary>True when the merge will retire nothing on the replaced side
            /// (<see cref="SupersedeCommit.MergeWouldRetireNothing"/>): <c>L'</c> keeps the
            /// retire set. The Rule 1 re-check still reads <see cref="RetireSet"/>.</summary>
            internal bool RetiresNothing;
            /// <summary>The rewind point's UT: the world clause reads committed rows after it.</summary>
            internal double RewindUT = double.NaN;
            /// <summary>Stock respawn delay for a kerbal who died on a recording, or NaN when
            /// the death is permanent.</summary>
            internal Func<string, double> RespawnSecondsForRecording;
            /// <summary>The live session clock both walks judge contract deadlines against
            /// (<see cref="ResolveDeadlineClockUT"/>); NaN when unknown.</summary>
            internal double DeadlineClockUT = double.NaN;
        }

        /// <summary>The walk the check runs (seam for tests).</summary>
        internal static Func<List<GameAction>, RecalculationEngine.IsolatedBalanceWalk> WalkerForTesting;

        /// <summary>The pending tree's row build the live wrapper runs (seam for tests).</summary>
        internal static Func<RecordingTree, LedgerOrchestrator.TreeCommitRowPreview> PreviewBuilderForTesting;

        internal static void ResetForTesting()
        {
            WalkerForTesting = null;
            PreviewBuilderForTesting = null;
        }

        private static RecalculationEngine.IsolatedBalanceWalk Walk(
            List<GameAction> actions, double deadlineClockUT)
        {
            var walker = WalkerForTesting;
            return walker != null
                ? walker(actions)
                : RecalculationEngine.WalkBalancesOnClones(actions, deadlineClockUT);
        }

        // ------------------------------------------------------------------
        // Pure check
        // ------------------------------------------------------------------

        /// <summary>
        /// The one instant both walks judge contract deadlines against, so <c>L</c> and
        /// <c>L'</c> cannot differ by a synthetic ContractFail that only the list with the
        /// later last row synthesizes: <paramref name="sessionClockUT"/> when it is a real
        /// clock (finite, positive, not the walk-everything sentinel), else the latest row
        /// UT of both lists. Pure.
        /// </summary>
        internal static double ResolveDeadlineClockUT(
            double sessionClockUT,
            IReadOnlyList<GameAction> baseline,
            IReadOnlyList<GameAction> attemptRows)
        {
            if (!double.IsNaN(sessionClockUT) && !double.IsInfinity(sessionClockUT)
                && sessionClockUT > 0.0 && !KerbalsModule.IsNoTimeFilterCutoff(sessionClockUT))
                return sessionClockUT;
            double latest = 0.0;
            if (baseline != null)
                for (int i = 0; i < baseline.Count; i++)
                    if (baseline[i] != null && baseline[i].UT > latest) latest = baseline[i].UT;
            if (attemptRows != null)
                for (int i = 0; i < attemptRows.Count; i++)
                    if (attemptRows[i] != null && attemptRows[i].UT > latest) latest = attemptRows[i].UT;
            return latest;
        }

        /// <summary>
        /// The check over explicit inputs. Logs nothing (the live wrapper writes the two
        /// grep-stable lines).
        /// </summary>
        internal static ReFlyMergeDominanceVerdict Evaluate(Inputs inputs)
        {
            var v = new ReFlyMergeDominanceVerdict { Applicable = true, SessionId = inputs?.SessionId };
            if (inputs == null)
                return v;

            var attemptIds = inputs.AttemptRecordingIds ?? (ICollection<string>)new HashSet<string>();
            var els = inputs.Els ?? (IReadOnlyList<GameAction>)new List<GameAction>();
            var retire = inputs.RetireSet ?? (IReadOnlyList<GameAction>)new List<GameAction>();
            var newRows = inputs.NewRows ?? (IReadOnlyList<GameAction>)new List<GameAction>();

            // Rule 1 re-check: the retire set holds only loss rows.
            var sealingRows = new List<GameAction>();
            for (int i = 0; i < retire.Count; i++)
            {
                if (retire[i] != null && !SupersedeCommit.IsLossRow(retire[i]))
                    sealingRows.Add(retire[i]);
            }
            if (sealingRows.Count > 0)
            {
                GameAction named = SupersedeCommit.PickNamedSealingRow(sealingRows);
                v.RetireSetCount = retire.Count;
                v.Pass = false;
                v.Pool = "sealing";
                v.UT = named.UT;
                v.SealingRow = SupersedeCommit.FormatRetireSetRowSummary(named);
                return v;
            }
            if (inputs.RetiresNothing)
                retire = new List<GameAction>();
            v.RetireSetCount = retire.Count;

            // L and the attempt's rows.
            var baseline = new List<GameAction>(els.Count);
            var attemptRows = new List<GameAction>();
            for (int i = 0; i < els.Count; i++)
            {
                var a = els[i];
                if (a == null) continue;
                if (IsAttemptRow(a, attemptIds)) attemptRows.Add(a);
                else baseline.Add(a);
            }
            // A build-step row of a recording outside the attempt (the replaced flight's own
            // rows when the ledger never filed them) is read as if it were already filed: it
            // sits on both sides unless the retire set takes it out of L'.
            for (int i = 0; i < newRows.Count; i++)
            {
                var a = newRows[i];
                if (a == null) continue;
                if (IsAttemptRow(a, attemptIds)) attemptRows.Add(a);
                else baseline.Add(a);
            }
            v.AttemptRowCount = attemptRows.Count;

            var retireIds = new HashSet<string>(StringComparer.Ordinal);
            var retireRepLossIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < retire.Count; i++)
            {
                var r = retire[i];
                if (r == null || string.IsNullOrEmpty(r.ActionId)) continue;
                retireIds.Add(r.ActionId);
                if (SupersedeCommit.IsReputationLossRow(r))
                {
                    retireRepLossIds.Add(r.ActionId);
                    v.RepLossOld += r.NominalPenalty;
                }
            }
            for (int i = 0; i < attemptRows.Count; i++)
            {
                var a = attemptRows[i];
                if (IsAttemptRow(a, attemptIds) && SupersedeCommit.IsReputationLossRow(a))
                    v.RepLossNew += a.NominalPenalty;
            }

            double clock = ResolveDeadlineClockUT(inputs.DeadlineClockUT, baseline, attemptRows);
            List<GameAction> afterA = ComposeAfter(baseline, attemptRows, retireIds, null, null);
            var walkBefore = Walk(baseline, clock);
            var walkA = Walk(afterA, clock);

            // Rule 2: A when reputation over L'(A) never drops below L.
            BalanceSeries seriesBefore = BalanceSeries.From(walkBefore);
            BalanceSeries seriesA = BalanceSeries.From(walkA);
            Comparison repA = Compare(seriesBefore, seriesA, onlyReputation: true);

            List<GameAction> after = afterA;
            var walkAfter = walkA;
            BalanceSeries seriesAfter = seriesA;
            if (repA.Failed)
            {
                v.Variant = LossSwapVariant.B;
                after = ComposeAfter(baseline, attemptRows, retireIds, retireRepLossIds, attemptIds);
                walkAfter = Walk(after, clock);
                seriesAfter = BalanceSeries.From(walkAfter);
            }

            // Invariant LS: balances at every UT.
            Comparison all = Compare(seriesBefore, seriesAfter, onlyReputation: false);
            if (all.Failed)
            {
                v.Pass = false;
                v.Pool = all.Pool;
                v.UT = all.UT;
                v.Before = all.Before;
                v.After = all.After;
                return v;
            }
            v.Pool = all.TightestPool ?? "none";
            v.UT = all.TightestUT;
            v.Before = all.TightestBefore;
            v.After = all.TightestAfter;

            // Invariant LS: every spending row affordable in L stays affordable in L'.
            var afterIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < after.Count; i++)
                if (!string.IsNullOrEmpty(after[i].ActionId)) afterIds.Add(after[i].ActionId);
            var spendsByUt = new List<GameAction>();
            for (int i = 0; i < baseline.Count; i++)
            {
                var s = baseline[i];
                if (string.IsNullOrEmpty(s.ActionId) || !RecalculationEngine.IsSpendingType(s.Type))
                    continue;
                if (!afterIds.Contains(s.ActionId)) continue;
                bool affordableBefore;
                if (!walkBefore.AffordableByActionId.TryGetValue(s.ActionId, out affordableBefore)
                    || !affordableBefore)
                    continue;
                bool affordableAfter;
                if (walkAfter.AffordableByActionId.TryGetValue(s.ActionId, out affordableAfter)
                    && affordableAfter)
                    continue;
                spendsByUt.Add(s);
            }
            if (spendsByUt.Count > 0)
            {
                spendsByUt.Sort((x, y) => x.UT.CompareTo(y.UT));
                var s = spendsByUt[0];
                string pool = SpendingPool(s.Type);
                v.Pass = false;
                v.Pool = pool;
                v.UT = s.UT;
                v.Before = seriesBefore.ValueAt(pool, s.UT);
                v.After = seriesAfter.ValueAt(pool, s.UT);
                v.SpendingRow = SupersedeCommit.FormatRetireSetRowSummary(s);
                return v;
            }

            // Invariant LS: the world-object clause.
            string worldKind, worldId;
            double worldUT;
            if (TryFindWorldConflict(baseline, after, attemptIds, inputs.RewindUT,
                    inputs.RespawnSecondsForRecording, out worldKind, out worldId, out worldUT))
            {
                v.Pass = false;
                v.WorldKind = worldKind;
                v.WorldId = worldId;
                v.UT = worldUT;
                v.Pool = "world";
                v.Before = 0;
                v.After = 0;
                return v;
            }

            return v;
        }

        private static bool IsAttemptRow(GameAction a, ICollection<string> attemptIds)
        {
            return a != null && !string.IsNullOrEmpty(a.RecordingId) && attemptIds.Contains(a.RecordingId);
        }

        /// <summary>
        /// <c>L'</c>: <paramref name="baseline"/> minus the retire set (minus
        /// <paramref name="keptRetireIds"/>, Variant B's kept ReputationPenalty loss rows),
        /// plus <paramref name="attemptRows"/> (minus the attempt's own ReputationPenalty
        /// loss rows when <paramref name="attemptIdsForDroppedRepLoss"/> is set, Variant B).
        /// </summary>
        internal static List<GameAction> ComposeAfter(
            IReadOnlyList<GameAction> baseline,
            IReadOnlyList<GameAction> attemptRows,
            ICollection<string> retireIds,
            ICollection<string> keptRetireIds,
            ICollection<string> attemptIdsForDroppedRepLoss)
        {
            var after = new List<GameAction>(baseline.Count + attemptRows.Count);
            for (int i = 0; i < baseline.Count; i++)
            {
                var a = baseline[i];
                if (a == null) continue;
                if (!string.IsNullOrEmpty(a.ActionId) && retireIds.Contains(a.ActionId)
                    && (keptRetireIds == null || !keptRetireIds.Contains(a.ActionId)))
                    continue;
                after.Add(a);
            }
            for (int i = 0; i < attemptRows.Count; i++)
            {
                var a = attemptRows[i];
                if (a == null) continue;
                if (attemptIdsForDroppedRepLoss != null
                    && IsAttemptRow(a, attemptIdsForDroppedRepLoss)
                    && SupersedeCommit.IsReputationLossRow(a))
                    continue;
                after.Add(a);
            }
            return after;
        }

        private static string SpendingPool(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.ScienceSpending:
                case GameActionType.StrategyScienceDebit:
                    return "science";
                default:
                    return "funds";
            }
        }

        // ------------------------------------------------------------------
        // Balance series
        // ------------------------------------------------------------------

        /// <summary>A walk's balances as a step function of UT: the value after every
        /// action at a UT has been applied.</summary>
        internal sealed class BalanceSeries
        {
            internal readonly List<double> UTs = new List<double>();
            internal readonly List<double> Funds = new List<double>();
            internal readonly List<double> Science = new List<double>();
            internal readonly List<double> Reputation = new List<double>();

            internal static BalanceSeries From(RecalculationEngine.IsolatedBalanceWalk walk)
            {
                var s = new BalanceSeries();
                if (walk == null) return s;
                var samples = walk.Samples;
                for (int i = 0; i < samples.Count; i++)
                {
                    var x = samples[i];
                    int n = s.UTs.Count;
                    if (n > 0 && s.UTs[n - 1] == x.UT)
                    {
                        s.Funds[n - 1] = x.Funds;
                        s.Science[n - 1] = x.Science;
                        s.Reputation[n - 1] = x.Reputation;
                        continue;
                    }
                    s.UTs.Add(x.UT);
                    s.Funds.Add(x.Funds);
                    s.Science.Add(x.Science);
                    s.Reputation.Add(x.Reputation);
                }
                return s;
            }

            internal double ValueAt(string pool, double ut)
            {
                int idx = -1;
                for (int i = 0; i < UTs.Count; i++)
                {
                    if (UTs[i] <= ut) idx = i;
                    else break;
                }
                if (idx < 0) return 0.0;
                switch (pool)
                {
                    case "funds": return Funds[idx];
                    case "science": return Science[idx];
                    default: return Reputation[idx];
                }
            }
        }

        private struct Comparison
        {
            internal bool Failed;
            internal string Pool;
            internal double UT;
            internal double Before;
            internal double After;
            internal string TightestPool;
            internal double TightestUT;
            internal double TightestBefore;
            internal double TightestAfter;
        }

        private static readonly string[] AllPools = { "funds", "science", "reputation" };
        private static readonly string[] ReputationOnly = { "reputation" };

        /// <summary>
        /// Compares <paramref name="after"/> against <paramref name="before"/> at every UT
        /// either series changes; the first UT (and, at one UT, the first pool in
        /// funds / science / reputation order) where after falls below before by more than
        /// <see cref="BalanceEpsilon"/> fails.
        /// </summary>
        private static Comparison Compare(BalanceSeries before, BalanceSeries after, bool onlyReputation)
        {
            var c = new Comparison { TightestUT = double.NaN };
            var uts = new SortedSet<double>(before.UTs);
            uts.UnionWith(after.UTs);
            double tightestMargin = double.PositiveInfinity;
            string[] pools = onlyReputation ? ReputationOnly : AllPools;
            foreach (double ut in uts)
            {
                for (int p = 0; p < pools.Length; p++)
                {
                    double b = before.ValueAt(pools[p], ut);
                    double a = after.ValueAt(pools[p], ut);
                    double margin = a - b;
                    if (margin < -BalanceEpsilon)
                    {
                        c.Failed = true;
                        c.Pool = pools[p];
                        c.UT = ut;
                        c.Before = b;
                        c.After = a;
                        return c;
                    }
                    if (margin < tightestMargin)
                    {
                        tightestMargin = margin;
                        c.TightestPool = pools[p];
                        c.TightestUT = ut;
                        c.TightestBefore = b;
                        c.TightestAfter = a;
                    }
                }
            }
            return c;
        }

        // ------------------------------------------------------------------
        // World-object clause
        // ------------------------------------------------------------------

        private static double DeathUT(GameAction a)
        {
            return !float.IsNaN(a.EndUT) ? a.EndUT : a.UT;
        }

        private static double RespawnUT(GameAction death, Func<string, double> respawnSeconds)
        {
            double deathUT = DeathUT(death);
            double secs = respawnSeconds != null ? respawnSeconds(death.RecordingId) : double.NaN;
            if (double.IsNaN(secs) || double.IsInfinity(secs) || secs < 0.0)
                return double.PositiveInfinity;
            return deathUT + secs;
        }

        private static bool DeadAt(IReadOnlyList<GameAction> rows, string kerbal, double ut,
            Func<string, double> respawnSeconds)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var b = rows[i];
                if (b == null || b.Type != GameActionType.KerbalAssignment) continue;
                if (b.KerbalEndStateField != KerbalEndState.Dead) continue;
                if (!string.Equals(b.KerbalName, kerbal, StringComparison.Ordinal)) continue;
                if (DeathUT(b) <= ut && ut < RespawnUT(b, respawnSeconds))
                    return true;
            }
            return false;
        }

        private static bool DestroyedAt(IReadOnlyList<GameAction> rows, string facility, double ut)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var d = rows[i];
                if (d == null || d.Type != GameActionType.FacilityDestruction) continue;
                if (!string.Equals(d.FacilityId, facility, StringComparison.Ordinal)) continue;
                if (d.UT <= ut && ut < RepairUT(rows, facility, d.UT))
                    return true;
            }
            return false;
        }

        private static double RepairUT(IReadOnlyList<GameAction> rows, string facility, double destroyedUT)
        {
            double repair = double.PositiveInfinity;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r == null || r.Type != GameActionType.FacilityRepair) continue;
                if (!string.Equals(r.FacilityId, facility, StringComparison.Ordinal)) continue;
                if (r.UT > destroyedUT && r.UT < repair) repair = r.UT;
            }
            return repair;
        }

        /// <summary>
        /// The world-object clause of Invariant LS over <paramref name="before"/>
        /// (<c>L</c>) and <paramref name="after"/> (<c>L'</c>): a kerbal a row of the attempt
        /// (tagged to <paramref name="attemptIds"/>) leaves dead in <c>L'</c>, until the stock
        /// respawn <paramref name="respawnSeconds"/> names or for good, that another row of
        /// <c>L'</c> (a committed <see cref="GameActionType.KerbalAssignment"/> starting at or
        /// after <paramref name="rewindUT"/>) flies inside that dead window where <c>L</c> did
        /// not already have him dead; or a facility the attempt destroys that a committed
        /// <see cref="GameActionType.FacilityUpgrade"/> of <c>L'</c> after the rewind UT
        /// upgrades before any repair, where <c>L</c> did not already have it destroyed. The
        /// earliest conflict wins. Pure.
        /// </summary>
        internal static bool TryFindWorldConflict(
            IReadOnlyList<GameAction> before,
            IReadOnlyList<GameAction> after,
            ICollection<string> attemptIds,
            double rewindUT,
            Func<string, double> respawnSeconds,
            out string kind,
            out string id,
            out double ut)
        {
            kind = null;
            id = null;
            ut = double.PositiveInfinity;
            double floor = double.IsNaN(rewindUT) ? double.NegativeInfinity : rewindUT;
            if (before == null || after == null || attemptIds == null)
                return false;

            for (int i = 0; i < after.Count; i++)
            {
                var death = after[i];
                if (!IsAttemptRow(death, attemptIds)) continue;
                if (death.Type != GameActionType.KerbalAssignment) continue;
                if (death.KerbalEndStateField != KerbalEndState.Dead) continue;
                if (string.IsNullOrEmpty(death.KerbalName)) continue;
                double deadFrom = DeathUT(death);
                double deadUntil = RespawnUT(death, respawnSeconds);
                for (int j = 0; j < after.Count; j++)
                {
                    var use = after[j];
                    if (use == null || IsAttemptRow(use, attemptIds)) continue;
                    if (use.Type != GameActionType.KerbalAssignment) continue;
                    if (!string.Equals(use.KerbalName, death.KerbalName, StringComparison.Ordinal)) continue;
                    double start = use.StartUT;
                    if (start < floor || start < deadFrom || start >= deadUntil) continue;
                    if (DeadAt(before, death.KerbalName, start, respawnSeconds)) continue;
                    if (start < ut)
                    {
                        ut = start;
                        kind = "kerbal";
                        id = death.KerbalName;
                    }
                }
            }

            for (int i = 0; i < after.Count; i++)
            {
                var destroyed = after[i];
                if (!IsAttemptRow(destroyed, attemptIds)) continue;
                if (destroyed.Type != GameActionType.FacilityDestruction) continue;
                if (string.IsNullOrEmpty(destroyed.FacilityId)) continue;
                double repaired = RepairUT(after, destroyed.FacilityId, destroyed.UT);
                for (int j = 0; j < after.Count; j++)
                {
                    var use = after[j];
                    if (use == null || IsAttemptRow(use, attemptIds)) continue;
                    if (use.Type != GameActionType.FacilityUpgrade) continue;
                    if (!string.Equals(use.FacilityId, destroyed.FacilityId, StringComparison.Ordinal)) continue;
                    if (use.UT < floor || use.UT < destroyed.UT || use.UT >= repaired) continue;
                    if (DestroyedAt(before, destroyed.FacilityId, use.UT)) continue;
                    if (use.UT < ut)
                    {
                        ut = use.UT;
                        kind = "facility";
                        id = destroyed.FacilityId;
                    }
                }
            }

            return kind != null;
        }

        // ------------------------------------------------------------------
        // Live wrapper
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs the check for the merge of <paramref name="tree"/> against the live career:
        /// a no-op (<see cref="ReFlyMergeDominanceVerdict.Applicable"/> false) when no Re-Fly
        /// session is live. Builds the tree's rows with the pure commit row build, reads the
        /// retire set the merge would tombstone, and logs
        /// <c>Merge dominance check</c> and <c>Loss swap</c>. <paramref name="context"/>
        /// names the caller (dialog open or the Merge click). <paramref name="attemptRows"/>
        /// returns the build step's rows of the attempt's recordings (the dialog's auto-seal
        /// preview reads them).
        /// </summary>
        internal static ReFlyMergeDominanceVerdict EvaluateForMerge(
            RecordingTree tree, string context, out List<GameAction> attemptRows,
            bool treeAwaitsFinalize = false)
        {
            attemptRows = new List<GameAction>();
            var scenario = ParsekScenario.Instance;
            ReFlySessionMarker marker = !object.ReferenceEquals(null, scenario)
                ? scenario.ActiveReFlySessionMarker
                : null;
            if (marker == null || tree == null)
            {
                ParsekLog.Verbose(Tag,
                    $"Merge dominance check skipped ({context ?? "<none>"}): " +
                    (marker == null ? "no Re-Fly session" : "no tree") + " - ordinary tree commit");
                return ReFlyMergeDominanceVerdict.NotApplicable();
            }

            if (scenario.ActiveMergeJournal != null)
            {
                // A merge already began (its variant is on the journal); the finisher
                // owns it, and the ledger is mid-merge, so there is no L to compare.
                ParsekLog.Verbose(Tag,
                    $"Merge dominance check skipped ({context ?? "<none>"}): merge journal active " +
                    $"sess={marker.SessionId ?? "<no-id>"} phase={scenario.ActiveMergeJournal.Phase ?? "<none>"}");
                return ReFlyMergeDominanceVerdict.NotApplicable();
            }

            // The whole check runs inside one try: a throw in the row build, the retire set
            // or the closure walk fails closed to "refuse, Discard only" instead of aborting
            // the merge dialog (which would offer no Discard at all).
            ReFlyMergeDominanceVerdict v;
            int closureCount = 0;
            try
            {
                v = EvaluateLiveCore(tree, marker, context, attemptRows, out closureCount, treeAwaitsFinalize);
            }
            catch (Exception ex)
            {
                // Fail closed: a check that cannot run cannot prove the merge safe. Discard
                // stays offered.
                attemptRows.Clear();
                ParsekLog.Warn(Tag,
                    $"Merge dominance check threw ({context ?? "<none>"}) sess={marker.SessionId ?? "<no-id>"}: " +
                    $"{ex.GetType().Name}: {ex.Message} - refusing the merge");
                v = new ReFlyMergeDominanceVerdict
                {
                    Applicable = true,
                    Pass = false,
                    SessionId = marker.SessionId,
                    Pool = "error",
                };
            }

            LogVerdict(v, context, closureCount);
            return v;
        }

        /// <summary>The live inputs and the pure check, for <see cref="EvaluateForMerge"/>
        /// (which owns the fail-closed catch). Fills <paramref name="attemptRows"/>.</summary>
        private static ReFlyMergeDominanceVerdict EvaluateLiveCore(
            RecordingTree tree, ReFlySessionMarker marker, string context,
            List<GameAction> attemptRows, out int closureCount, bool treeAwaitsFinalize)
        {
            var attemptIds = CollectAttemptRecordingIds(tree, marker);
            LedgerOrchestrator.Initialize();
            var previewBuilder = PreviewBuilderForTesting;
            LedgerOrchestrator.TreeCommitRowPreview preview = previewBuilder != null
                ? previewBuilder(tree)
                : LedgerOrchestrator.BuildTreeCommitRowsPreview(tree);
            for (int i = 0; i < preview.Rows.Count; i++)
            {
                var r = preview.Rows[i];
                if (r != null && !string.IsNullOrEmpty(r.RecordingId) && attemptIds.Contains(r.RecordingId))
                    attemptRows.Add(r);
            }

            // The merge retires nothing when AppendRelations will refuse the attempt; the
            // check then keeps the replaced side in L' instead of assuming it retired.
            List<GameAction> retire = SupersedeCommit.BuildMergeRetireSet(
                marker, attemptIds, out closureCount, preview.Rows);
            string noRetireReason;
            bool retiresNothing = SupersedeCommit.MergeWouldRetireNothing(
                marker, tree, out noRetireReason, terminalPendingFinalize: treeAwaitsFinalize);
            if (retiresNothing)
            {
                ParsekLog.Info(Tag,
                    $"Merge dominance check ({context ?? "<none>"}) sess={marker.SessionId ?? "<no-id>"}: " +
                    $"merge would retire nothing ({noRetireReason}) - L' keeps the replaced flight's " +
                    $"{retire.Count.ToString(CultureInfo.InvariantCulture)} retire-set row(s)");
            }

            var crewSources = preview.CrewSources;
            Func<string, double> respawn = recordingId =>
            {
                Recording rec = null;
                if (!string.IsNullOrEmpty(recordingId))
                {
                    if (!crewSources.TryGetValue(recordingId, out rec) || rec == null)
                        rec = EffectiveState.FindCommittedRecordingByIdRaw(recordingId);
                }
                if (rec == null || rec.CrewDeathRespawns != true) return double.NaN;
                return rec.CrewDeathRespawnSeconds;
            };

            var inputs = new Inputs
            {
                SessionId = marker.SessionId,
                Els = EffectiveState.ComputeELS(),
                AttemptRecordingIds = attemptIds,
                NewRows = preview.Rows,
                RetireSet = retire,
                RetiresNothing = retiresNothing,
                RewindUT = SupersedeCommit.ComputeTombstoneRewindCutoffUT(marker),
                RespawnSecondsForRecording = respawn,
                DeadlineClockUT = ParsekScenario.GetCurrentTimelineUTForLedgerRecalc(),
            };

            return Evaluate(inputs);
        }

        /// <summary>
        /// The recordings of the attempt in the dialog's tree: every recording the session
        /// created (<see cref="MergeDialog.IsCreatedByReFlySession"/>) plus the marker's
        /// active re-fly recording.
        /// </summary>
        internal static HashSet<string> CollectAttemptRecordingIds(RecordingTree tree, ReFlySessionMarker marker)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (marker == null) return ids;
            if (!string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                ids.Add(marker.ActiveReFlyRecordingId);
            if (tree?.Recordings != null)
            {
                foreach (var rec in tree.Recordings.Values)
                {
                    if (rec != null && !string.IsNullOrEmpty(rec.RecordingId)
                        && MergeDialog.IsCreatedByReFlySession(rec, marker))
                        ids.Add(rec.RecordingId);
                }
            }
            return ids;
        }

        private static string F(double value)
        {
            if (double.IsNaN(value)) return "<none>";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>The log's name of a pool (design section 12.6):
        /// <c>Funds</c> / <c>Science</c> / <c>Reputation</c>, or <c>-</c>.</summary>
        internal static string PoolLogName(string pool)
        {
            switch (pool)
            {
                case "funds": return "Funds";
                case "science": return "Science";
                case "reputation": return "Reputation";
                default: return "-";
            }
        }

        /// <summary>
        /// Writes the two grep-stable lines (design sections 2.12 and 12.6). The pool
        /// fields are <c>pool=&lt;Funds|Science|Reputation|-&gt; ut= before= after=</c>; a
        /// world-object refusal replaces them with <c>world=&lt;kerbal|facility&gt;:&lt;id&gt;
        /// ut=</c>, a sealing row in the retire set with
        /// <c>reason=recordingAction:&lt;Type&gt;:&lt;ActionId&gt;</c>.
        /// </summary>
        internal static void LogVerdict(ReFlyMergeDominanceVerdict v, string context, int closureCount)
        {
            if (v == null || !v.Applicable) return;
            var ic = CultureInfo.InvariantCulture;
            string sess = v.SessionId ?? "<no-id>";
            string fields;
            if (!v.Pass && v.WorldKind != null)
                fields = "world=" + v.WorldKind + ":" + (v.WorldId ?? "<none>") + " ut=" + F(v.UT);
            else if (!v.Pass && v.Pool == "sealing")
                fields = "reason=recordingAction:" + (v.SealingRow ?? "<none>") + " ut=" + F(v.UT);
            else if (!v.Pass && v.Pool == "error")
                fields = "reason=error";
            else
                fields = "pool=" + PoolLogName(v.Pool) + " ut=" + F(v.UT)
                    + " before=" + F(v.Before) + " after=" + F(v.After);
            if (v.SpendingRow != null) fields += " spend=" + v.SpendingRow;

            if (v.Pool != "sealing" && v.Pool != "error")
            {
                ParsekLog.Info(Tag,
                    $"Loss swap sess={sess} variant={v.Variant} " +
                    $"repLossOld={F(v.RepLossOld)} repLossNew={F(v.RepLossNew)}");
            }
            ParsekLog.Info(Tag,
                $"Merge dominance check sess={sess} result={(v.Pass ? "pass" : "refuse")} {fields} " +
                $"context={context ?? "<none>"} attemptRows={v.AttemptRowCount.ToString(ic)} " +
                $"retireSet={v.RetireSetCount.ToString(ic)} closure={closureCount.ToString(ic)}");
        }

        /// <summary>
        /// Player words for a refused merge, appended to the existing Re-Fly merge dialog
        /// body (no new UI surface).
        /// </summary>
        internal static string DescribeRefusal(ReFlyMergeDominanceVerdict v)
        {
            if (v == null || v.Pass) return null;
            if (v.WorldKind == "kerbal")
                return $"{v.WorldId} dies in this attempt, but a flight already on the timeline " +
                    "takes them up again later.";
            if (v.WorldKind == "facility")
                return "This attempt destroys a Space Center building that the timeline upgrades later.";
            switch (v.Pool)
            {
                case "sealing":
                    return "The flight this attempt replaces earned something that cannot be given back.";
                case "error":
                    return "Parsek could not check what this merge would change in your career.";
            }
            if (v.SpendingRow != null)
                return $"A purchase already on the timeline could no longer be paid for with {v.Pool}.";
            return $"Merging it would leave your {v.Pool} lower, later in the timeline, " +
                "than the timeline has now.";
        }
    }
}
