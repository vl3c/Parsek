using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Rewind design section 2.12 / resources design section 3.15 item 10 (owner ruling
    /// 2026-10-26): a Re-Fly seal reads credit, not presence. A row of a slot's retire set
    /// that is not a loss row seals the slot only when the ledger replay COUNTS it, even at
    /// zero value; a duplicate the replay leaves out does not. The replay's verdict comes
    /// from a fresh walk over clones (<see cref="RecalculationEngine.WalkBalancesOnClones"/>)
    /// of the Effective Ledger Set, never from the <c>Effective</c> flags some earlier walk
    /// left on the shared rows: a cutoff walk, a probe or a walk of a list that held an
    /// attempt's rows could leave a stale <c>Effective = false</c> on a row that holds a
    /// real counted gain, and the slot would open. Every doubt seals: a row the walk did not
    /// see, a walk that throws, a reason not reviewed into <see cref="ReplayCredit"/>, a
    /// not-counted row with no counted holder or whose stock payment is kept.
    /// </summary>
    internal static partial class SupersedeCommit
    {
        /// <summary>The credit walk (seam for tests): defaults to
        /// <see cref="RecalculationEngine.WalkBalancesOnClones"/>.</summary>
        internal static Func<List<GameAction>, RecalculationEngine.IsolatedBalanceWalk> SealCreditWalkerForTesting;

        /// <summary>How many credit walks ran (test observability for the cache).</summary>
        internal static int SealCreditWalkCountForTesting;

        // Guarded by worldActionSafetyCacheLock; cleared whenever the gate cache is (every
        // ledger, store, supersede and tombstone version bump), so it is the walk of the
        // ledger the gate verdicts are cached for.
        private static RecalculationEngine.IsolatedBalanceWalk cachedSealCreditWalk;

        // The walk of the ELS plus the rows of the active pending-commit scope
        // (UsePendingCommitRows), shared by every slot a tree commit promotes. Guarded by
        // worldActionSafetyCacheLock; cleared with the gate cache and whenever the scope
        // changes.
        private static RecalculationEngine.IsolatedBalanceWalk cachedPendingSealCreditWalk;
        private static object cachedPendingSealCreditRows;

        private static void ClearPendingSealCreditWalk()
        {
            lock (worldActionSafetyCacheLock)
            {
                cachedPendingSealCreditWalk = null;
                cachedPendingSealCreditRows = null;
            }
        }

        /// <summary>
        /// The rows the replay does not count, by ActionId with the reason, from a fresh
        /// full walk (no cutoff) of the Effective Ledger Set plus <paramref name="extraActions"/>
        /// (rows a commit is about to file, read as if already filed). The walk of the ELS
        /// alone is cached with the gate's own versions, and so is the walk with the active
        /// pending-commit scope's rows (one walk for every gate read inside the scope; `CommitTree` opens one scope per promoted slot); any
        /// other walk with extra rows is not. Returns null when the walk could not run (every
        /// row then seals). <paramref name="source"/> names the walk for the log:
        /// <c>cached</c>, <c>cached+extra</c>, <c>els</c>, <c>els+extra</c> or <c>walk-failed</c>.
        /// </summary>
        internal static IReadOnlyDictionary<string, string> ResolveReplayNotCounted(
            IReadOnlyList<GameAction> extraActions, out string source)
        {
            bool hasExtra = extraActions != null && extraActions.Count > 0;
            var scenario = ParsekScenario.Instance;
            // The pending-commit scope's rows are one list, so its walk is shared by
            // every gate read inside the scope (one slot's qualification).
            bool pendingScope = hasExtra && ReferenceEquals(extraActions, pendingCommitRows);
            lock (worldActionSafetyCacheLock)
            {
                EnsureWorldActionSafetyCacheCurrent(scenario);
                if (!hasExtra && cachedSealCreditWalk != null)
                {
                    source = "cached";
                    return cachedSealCreditWalk.NotCountedByActionId;
                }
                if (pendingScope && cachedPendingSealCreditWalk != null
                    && ReferenceEquals(cachedPendingSealCreditRows, extraActions))
                {
                    source = "cached+extra";
                    return cachedPendingSealCreditWalk.NotCountedByActionId;
                }
            }

            IReadOnlyList<GameAction> els = EffectiveState.ComputeELS();
            var rows = new List<GameAction>(els.Count + (hasExtra ? extraActions.Count : 0));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < els.Count; i++)
            {
                var a = els[i];
                if (a == null) continue;
                rows.Add(a);
                if (!string.IsNullOrEmpty(a.ActionId)) seen.Add(a.ActionId);
            }
            int extraAdded = 0;
            if (hasExtra)
            {
                for (int i = 0; i < extraActions.Count; i++)
                {
                    var a = extraActions[i];
                    if (a == null) continue;
                    if (!string.IsNullOrEmpty(a.ActionId) && !seen.Add(a.ActionId)) continue;
                    rows.Add(a);
                    extraAdded++;
                }
            }

            RecalculationEngine.IsolatedBalanceWalk walk;
            try
            {
                var walker = SealCreditWalkerForTesting;
                walk = walker != null
                    ? walker(rows)
                    : RecalculationEngine.WalkBalancesOnClones(rows);
                SealCreditWalkCountForTesting++;
            }
            catch (Exception ex)
            {
                source = "walk-failed";
                ParsekLog.Warn(Tag,
                    $"Seal credit walk (section 2.12, 3.15 item 10) failed: {ex.GetType().Name}: {ex.Message}; " +
                    "every candidate row seals");
                return null;
            }
            if (walk == null)
            {
                source = "walk-failed";
                ParsekLog.Warn(Tag,
                    "Seal credit walk (section 2.12, 3.15 item 10) returned nothing; every candidate row seals");
                return null;
            }

            source = hasExtra ? "els+extra" : "els";
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose(Tag,
                $"Seal credit walk (section 2.12, 3.15 item 10): rows={rows.Count.ToString(ic)} " +
                $"extra={extraAdded.ToString(ic)} notCounted={walk.NotCountedByActionId.Count.ToString(ic)} " +
                $"scienceWalked={walk.ScienceWalked} source={source}");
            if (!hasExtra || pendingScope)
            {
                lock (worldActionSafetyCacheLock)
                {
                    EnsureWorldActionSafetyCacheCurrent(scenario);
                    if (!hasExtra)
                    {
                        cachedSealCreditWalk = walk;
                    }
                    else
                    {
                        cachedPendingSealCreditWalk = walk;
                        cachedPendingSealCreditRows = extraActions;
                    }
                }
            }
            return walk.NotCountedByActionId;
        }

        /// <summary>
        /// Pure: the sealing rows among <paramref name="retireSet"/>: every row that is not a
        /// loss row (<see cref="IsLossRow"/>) and that the replay counts
        /// (<see cref="ReplayCredit.IsCounted"/> over <paramref name="notCounted"/>), in
        /// retire-set order. <paramref name="notCountedRows"/> receives the non-loss rows the
        /// replay left out with a counted holder and a settled stock payment
        /// (<see cref="ReplayCredit.NotCountedLabel"/>): they do not seal, and a merge still
        /// retires them, which retires nothing the replay counts and no kept payment. A null <paramref name="notCounted"/> leaves every
        /// non-loss row sealing, the presence rule.
        /// </summary>
        internal static List<GameAction> SelectSealingRows(
            IReadOnlyList<GameAction> retireSet,
            IReadOnlyDictionary<string, string> notCounted,
            List<GameAction> notCountedRows)
        {
            var sealing = new List<GameAction>();
            if (retireSet == null)
                return sealing;
            for (int i = 0; i < retireSet.Count; i++)
            {
                var a = retireSet[i];
                if (a == null || IsLossRow(a))
                    continue;
                if (ReplayCredit.IsCounted(a, notCounted))
                    sealing.Add(a);
                else if (notCountedRows != null)
                    notCountedRows.Add(a);
            }
            return sealing;
        }

        /// <summary>True when <paramref name="retireSet"/> holds a row that is not a loss row
        /// (the only rows the seal needs the replay's verdict on).</summary>
        internal static bool HasNonLossRow(IReadOnlyList<GameAction> retireSet)
        {
            if (retireSet == null) return false;
            for (int i = 0; i < retireSet.Count; i++)
                if (retireSet[i] != null && !IsLossRow(retireSet[i]))
                    return true;
            return false;
        }

        /// <summary>Log text for the not-counted rows: <c>Type:ActionId(reason)</c>, at most
        /// <paramref name="cap"/> of them.</summary>
        internal static string FormatNotCountedRows(
            IReadOnlyList<GameAction> rows, IReadOnlyDictionary<string, string> notCounted, int cap = 5)
        {
            if (rows == null || rows.Count == 0)
                return string.Empty;
            var parts = new List<string>();
            for (int i = 0; i < rows.Count && i < cap; i++)
            {
                var a = rows[i];
                string reason;
                if (notCounted == null || a == null || string.IsNullOrEmpty(a.ActionId)
                    || !notCounted.TryGetValue(a.ActionId, out reason))
                    reason = "?";
                parts.Add(FormatRetireSetRowSummary(a) + "(" + reason + ")");
            }
            if (rows.Count > cap)
                parts.Add("...");
            return string.Join(",", parts.ToArray());
        }
    }
}
