using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>Why activating a strategy now is refused. <see cref="None"/> allows it.</summary>
    internal enum StrategyActivationBlockKind
    {
        None,
        /// <summary>The committed timeline activates this strategy later.</summary>
        FutureActivation,
        /// <summary>Activating it now would leave no free slot for a committed activation.</summary>
        SlotNeeded,
        /// <summary>With it active, stock's conflict rule would refuse a committed activation.</summary>
        ConflictsWithCommitted
    }

    /// <summary>One stock strategy as the conflict rule sees it: its id and group tags,
    /// listed in <c>StrategySystem.Strategies</c> order (the rule depends on the order).</summary>
    internal struct StrategyTagInfo
    {
        internal string Id;
        internal string[] Tags;

        internal StrategyTagInfo(string id, string[] tags)
        {
            Id = id;
            Tags = tags;
        }
    }

    /// <summary>The activation decision for one strategy, with the committed row behind it.</summary>
    internal struct StrategyActivationDecision
    {
        internal StrategyActivationBlockKind Kind;
        /// <summary>The committed row the refusal names: the strategy's own future
        /// activation, or the committed activation that would overflow the slots. Null
        /// when allowed.</summary>
        internal CommittedFutureEntry Entry;
        /// <summary>SlotNeeded only: the active count at the overflowing activation,
        /// counting the candidate.</summary>
        internal int CountAtOverflow;
        /// <summary>SlotNeeded only: the slot limit at that activation.</summary>
        internal int LimitAtOverflow;
        /// <summary>ConflictsWithCommitted only: the committed deactivation of the
        /// conflicting strategy after its activation, or null when none is committed.</summary>
        internal CommittedFutureEntry ConflictEnd;

        internal bool Blocked => Kind != StrategyActivationBlockKind.None;
    }

    /// <summary>
    /// The strategy click-block predicates (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md,
    /// section 4 "S1", section 10 step 5). Pure: every live value (the stock active set, the
    /// slot limit) comes in as an argument, and every committed row comes from the
    /// <see cref="CommittedFutureIndex"/> the other stock-screen blocks read.
    ///
    /// <para><b>Activation of S is refused</b> when:</para>
    /// <list type="number">
    /// <item>the committed timeline activates S later (a future StrategyActivate row for S):
    /// activating it now would happen earlier, and the committed row would then overwrite
    /// it and charge the setup cost a second time (the S1 hole the verification cell
    /// <c>S1_SecondActivationOfTheSameStrategy_OverwritesAndChargesSetupAgain_DocumentsHole</c>
    /// pins in the walk); or</item>
    /// <item>activating S now leaves no free slot at a committed future activation. Peak
    /// model: start from the strategies active in stock now plus S (a fresh activation has
    /// no committed deactivation, so it counts throughout), apply the committed future
    /// activations and deactivations of every strategy in UT order (deactivations before
    /// activations at the same UT, since stock checks the slot at activation time), raise
    /// the limit at each committed Administration upgrade, and refuse when the count
    /// exceeds the limit at any committed activation. Nothing after the last committed
    /// activation can raise the count, so the walk stops there. An overflow at NOW alone
    /// is not reported: stock's own slot check already refuses it with its own reason.</item>
    /// <item>with S active, stock's conflict rule would refuse a committed activation of
    /// another strategy X. The same walk evaluates, at each committed activation of X (X not
    /// already active), <see cref="StockHasConflictingActiveStrategies"/> over the modeled
    /// active set, and refuses when it is true WITH S and false without it: the conflict
    /// only counts while S is still active at X's date (S stays active from now on unless a
    /// committed deactivation of S comes first), and a conflict the committed timeline
    /// already has without S is not blamed on S. Needs stock's ordered strategy list; with
    /// none the branch is skipped.</item>
    /// </list>
    ///
    /// <para><b>Deactivation of S by the player is refused</b> when the committed timeline
    /// has a later StrategyActivate or StrategyDeactivate row for S: the committed timeline
    /// decides S's state on that date. A player deactivation before a committed
    /// deactivation would move it earlier (the committed row then reads "not currently
    /// active" in the walk). No other committed row depends on S being active: the walk's
    /// contract-reward transform is an identity no-op (stock applies the strategy before
    /// the capture), and the strategy conversion rows (StrategyScienceDebit / Credit, the
    /// StrategyConverter funds and reputation sources) are captured post-transform, carry
    /// no strategy id and replay unconditionally. So deactivating S changes no committed
    /// row's value unless a committed row for S itself lies ahead.</para>
    ///
    /// <para>Stock auto-expiry (KSPCommunityFixes' StrategyDuration fix; dead code in pure
    /// stock) goes through <c>Strategy.Deactivate()</c> gated on <c>CanBeDeactivated</c>,
    /// which Parsek does not patch. The deactivation refusal is wired only to the
    /// Administration player path, so an expiry still deactivates and is captured as a
    /// StrategyDeactivate row like any other deactivation. The one exception is an expiry
    /// the committed timeline already made (a replay after a rewind):
    /// <see cref="DecideStockUpdate"/> lets the <c>Strategy.Update()</c> prefix apply the
    /// committed deactivation first, so stock never expires it a second time.</para>
    /// </summary>
    internal static class StrategyReservationPredicates
    {
        /// <summary>The Administration facility's upgradeable id, as FacilityUpgrade rows key it.</summary>
        internal const string AdministrationFacilityId = "SpaceCenter/Administration";

        /// <summary>
        /// Decides whether activating <paramref name="strategyId"/> at
        /// <paramref name="currentUT"/> is refused.
        /// </summary>
        /// <param name="activeNow">The ids stock has active now (after the state patch
        /// this is the ledger's active set).</param>
        /// <param name="currentLimit">Stock's slot limit now
        /// (<c>Administration.MaxActiveStrategies</c>).</param>
        /// <param name="slotsForLevel">Slot limit for a 1-based Administration level;
        /// null uses <see cref="LedgerOrchestrator.GetStrategySlots"/>.</param>
        internal static StrategyActivationDecision EvaluateActivation(
            CommittedFutureIndex index,
            string strategyId,
            double currentUT,
            IEnumerable<string> activeNow,
            int currentLimit,
            Func<int, int> slotsForLevel = null,
            IReadOnlyList<StrategyTagInfo> stockStrategies = null)
        {
            var none = new StrategyActivationDecision { Kind = StrategyActivationBlockKind.None };
            if (index == null || string.IsNullOrEmpty(strategyId)) return none;

            var ownFuture = index.FirstFuture(CommittedFutureKind.StrategyActivate, strategyId, currentUT);
            if (ownFuture != null)
            {
                return new StrategyActivationDecision
                {
                    Kind = StrategyActivationBlockKind.FutureActivation,
                    Entry = ownFuture
                };
            }

            var activations = index.FutureEntriesOfKind(CommittedFutureKind.StrategyActivate, currentUT);
            if (activations.Count == 0) return none;
            double lastActivationUT = activations[activations.Count - 1].UT;

            var events = new List<SlotEvent>();
            for (int i = 0; i < activations.Count; i++)
                events.Add(new SlotEvent(activations[i], 2));
            var deactivations = index.FutureEntriesOfKind(CommittedFutureKind.StrategyDeactivate, currentUT);
            for (int i = 0; i < deactivations.Count; i++)
                if (deactivations[i].UT <= lastActivationUT) events.Add(new SlotEvent(deactivations[i], 1));
            AddAdministrationUpgrades(index, AdministrationFacilityId, currentUT, lastActivationUT, events);
            AddAdministrationUpgrades(index, KspFacilityIds.ToDisplayFacilityId(AdministrationFacilityId),
                currentUT, lastActivationUT, events);
            events.Sort((a, b) =>
            {
                int c = a.Entry.UT.CompareTo(b.Entry.UT);
                if (c != 0) return c;
                c = a.Rank.CompareTo(b.Rank);
                return c != 0 ? c : string.CompareOrdinal(a.Entry.Key, b.Entry.Key);
            });

            Func<int, int> slots = slotsForLevel ?? LedgerOrchestrator.GetStrategySlots;
            var active = new HashSet<string>(StringComparer.Ordinal);
            if (activeNow != null)
                foreach (var id in activeNow)
                    if (!string.IsNullOrEmpty(id)) active.Add(id);
            active.Add(strategyId);
            int limit = currentLimit;

            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i].Entry;
                switch (events[i].Rank)
                {
                    case 0:
                        if (e.FacilityToLevel > 0) limit = Math.Max(limit, slots(e.FacilityToLevel));
                        break;
                    case 1:
                        active.Remove(e.Key);
                        break;
                    default:
                        if (stockStrategies != null && !active.Contains(e.Key)
                            && IsConflictCausedBy(stockStrategies, active, e.Key, strategyId))
                        {
                            return new StrategyActivationDecision
                            {
                                Kind = StrategyActivationBlockKind.ConflictsWithCommitted,
                                Entry = e,
                                ConflictEnd = FirstDeactivationAfter(deactivations, e.Key, e.UT)
                            };
                        }
                        active.Add(e.Key);
                        if (active.Count > limit)
                        {
                            return new StrategyActivationDecision
                            {
                                Kind = StrategyActivationBlockKind.SlotNeeded,
                                Entry = e,
                                CountAtOverflow = active.Count,
                                LimitAtOverflow = limit
                            };
                        }
                        break;
                }
            }
            return none;
        }

        private static bool IsConflictCausedBy(
            IReadOnlyList<StrategyTagInfo> stockStrategies, HashSet<string> active, string committedId, string candidateId)
        {
            string[] tags = TagsOf(stockStrategies, committedId);
            if (tags == null || !active.Contains(candidateId)) return false;
            if (!StockHasConflictingActiveStrategies(stockStrategies, active, tags)) return false;
            var without = new HashSet<string>(active, StringComparer.Ordinal);
            without.Remove(candidateId);
            return !StockHasConflictingActiveStrategies(stockStrategies, without, tags);
        }

        private static CommittedFutureEntry FirstDeactivationAfter(
            List<CommittedFutureEntry> deactivations, string key, double afterUT)
        {
            for (int i = 0; i < deactivations.Count; i++)
                if (deactivations[i].Key == key && deactivations[i].UT >= afterUT) return deactivations[i];
            return null;
        }

        private static string[] TagsOf(IReadOnlyList<StrategyTagInfo> stockStrategies, string id)
        {
            for (int i = 0; i < stockStrategies.Count; i++)
                if (stockStrategies[i].Id == id) return stockStrategies[i].Tags ?? new string[0];
            return null;
        }

        /// <summary>
        /// A line-for-line mirror of stock
        /// <c>Strategies.StrategySystem.HasConflictingActiveStrategies(string[] groupTags)</c>
        /// (KSP 1.12.5 IL), which <c>Strategy.CanBeActivated</c> calls with the candidate's
        /// <c>GroupTags</c> (<c>Config.GroupTags</c>, the config's comma-split, trimmed
        /// <c>groupTag</c>). Stock:
        /// <list type="number">
        /// <item>collects the active strategies;</item>
        /// <item>counts the active strategies sharing at least one tag with
        /// <paramref name="groupTags"/>, stopping at 2 (<c>shared</c>);</item>
        /// <item>sets <c>threshold = 3 - shared</c>;</item>
        /// <item>for i from activeCount - 1 down to 0 reads <c>this.strategies[i]</c>, the FULL
        /// strategy list indexed by the ACTIVE list's positions (so it inspects the first
        /// activeCount strategies in list order, active or not), counts how many of
        /// <paramref name="groupTags"/> that strategy carries, and returns true once the
        /// count reaches the threshold;</item>
        /// <item>returns false.</item>
        /// </list>
        /// The index quirk in step 4 is kept on purpose: this must answer exactly what stock
        /// answers. Tags compare ordinally (<c>string.op_Equality</c>). A strategy with no
        /// tags reads as an empty tag list here, where stock would throw.
        /// </summary>
        internal static bool StockHasConflictingActiveStrategies(
            IReadOnlyList<StrategyTagInfo> allInStockOrder, ICollection<string> activeIds, string[] groupTags)
        {
            if (allInStockOrder == null || activeIds == null || groupTags == null) return false;

            int activeCount = 0;
            for (int i = allInStockOrder.Count - 1; i >= 0; i--)
                if (activeIds.Contains(allInStockOrder[i].Id)) activeCount++;

            int shared = 0;
            for (int i = allInStockOrder.Count - 1; i >= 0; i--)
            {
                if (!activeIds.Contains(allInStockOrder[i].Id)) continue;
                if (SharesAnyTag(allInStockOrder[i].Tags, groupTags)) shared++;
                if (shared >= 2) break;
            }

            int threshold = 3 - shared;
            for (int i = activeCount - 1; i >= 0; i--)
            {
                if (i >= allInStockOrder.Count) continue;
                string[] tags = allInStockOrder[i].Tags ?? new string[0];
                int matched = 0;
                for (int k = groupTags.Length - 1; k >= 0; k--)
                {
                    for (int j = tags.Length - 1; j >= 0; j--)
                    {
                        if (tags[j] == groupTags[k])
                        {
                            matched++;
                            break;
                        }
                    }
                    if (matched >= threshold) return true;
                }
            }
            return false;
        }

        private static bool SharesAnyTag(string[] a, string[] b)
        {
            if (a == null || b == null) return false;
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < b.Length; j++)
                    if (a[i] == b[j]) return true;
            return false;
        }

        private static void AddAdministrationUpgrades(
            CommittedFutureIndex index, string key, double currentUT, double untilUT, List<SlotEvent> events)
        {
            var upgrades = index.FutureEntries(CommittedFutureKind.FacilityUpgrade, key, currentUT);
            for (int i = 0; i < upgrades.Count; i++)
                if (upgrades[i].UT <= untilUT) events.Add(new SlotEvent(upgrades[i], 0));
        }

        private struct SlotEvent
        {
            internal readonly CommittedFutureEntry Entry;
            /// <summary>0 = Administration upgrade, 1 = deactivation, 2 = activation.</summary>
            internal readonly int Rank;

            internal SlotEvent(CommittedFutureEntry entry, int rank)
            {
                Entry = entry;
                Rank = rank;
            }
        }

        /// <summary>The earliest committed StrategyActivate or StrategyDeactivate row for this
        /// strategy still ahead, or null.</summary>
        internal static CommittedFutureEntry FirstFutureRow(CommittedFutureIndex index, string strategyId, double currentUT)
        {
            if (index == null || string.IsNullOrEmpty(strategyId)) return null;
            var on = index.FirstFuture(CommittedFutureKind.StrategyActivate, strategyId, currentUT);
            var off = index.FirstFuture(CommittedFutureKind.StrategyDeactivate, strategyId, currentUT);
            if (on == null) return off;
            if (off == null) return on;
            // Same UT: the deactivation reads first ("stays active until").
            return off.UT <= on.UT ? off : on;
        }

        /// <summary>The player-path deactivation refusal: a committed row for the strategy
        /// is still ahead.</summary>
        internal static bool IsDeactivationBlocked(CommittedFutureIndex index, string strategyId, double currentUT)
        {
            return FirstFutureRow(index, strategyId, currentUT) != null;
        }

        /// <summary>The text for a refused activation; empty text for an allowed one.</summary>
        internal static ReservationText ExplainActivation(
            StrategyActivationDecision decision,
            Func<string, string> strategyTitle,
            Func<double, string> formatDate)
        {
            switch (decision.Kind)
            {
                case StrategyActivationBlockKind.FutureActivation:
                    return ReservationExplanation.StrategyActivation(decision.Entry, formatDate);
                case StrategyActivationBlockKind.SlotNeeded:
                    string title = decision.Entry != null && strategyTitle != null
                        ? strategyTitle(decision.Entry.Key)
                        : null;
                    return ReservationExplanation.StrategySlot(decision.Entry, title, formatDate);
                case StrategyActivationBlockKind.ConflictsWithCommitted:
                    string other = decision.Entry != null && strategyTitle != null
                        ? strategyTitle(decision.Entry.Key)
                        : null;
                    return ReservationExplanation.StrategyConflict(
                        decision.Entry, decision.ConflictEnd, other, formatDate);
                default:
                    return new ReservationText();
            }
        }

        internal static ReservationText ExplainDeactivation(
            CommittedFutureIndex index, string strategyId, double currentUT, Func<double, string> formatDate)
        {
            return ReservationExplanation.StrategyDeactivation(
                FirstFutureRow(index, strategyId, currentUT), formatDate);
        }

        /// <summary>The per-decision log fragment, invariant.</summary>
        internal static string DescribeActivation(StrategyActivationDecision decision)
        {
            var ic = CultureInfo.InvariantCulture;
            if (!decision.Blocked) return "kind=None";
            string s = "kind=" + decision.Kind
                + " committedKey=" + (decision.Entry?.Key ?? "(none)")
                + " committedUT=" + (decision.Entry != null ? decision.Entry.UT.ToString("F0", ic) : "NaN");
            if (decision.Kind == StrategyActivationBlockKind.ConflictsWithCommitted)
                s += " conflictEndUT=" + (decision.ConflictEnd != null ? decision.ConflictEnd.UT.ToString("F0", ic) : "none");
            if (decision.Kind == StrategyActivationBlockKind.SlotNeeded)
                s += " count=" + decision.CountAtOverflow.ToString(ic)
                     + " limit=" + decision.LimitAtOverflow.ToString(ic);
            return s;
        }

        /// <summary>
        /// What the <c>Strategy.Update()</c> prefix does with one stock-active strategy this
        /// frame (todo STRATEGY-EXPIRY-REPLAY-DUPLICATE-DEACTIVATE-ROW, option c). Pure.
        ///
        /// <para>With KSPCommunityFixes' StrategyDuration fix, stock <c>Strategy.Update()</c>
        /// calls <c>Deactivate()</c> on the first frame with
        /// <c>DateActivated + LongestDuration &lt;= now</c>, and the capture postfix records
        /// it. After a rewind the loaded save has the strategy active with its original
        /// activation date, so that expiry would replay and record a second deactivation of
        /// a strategy the committed timeline already switches off. This decision lets the
        /// committed timeline act first, so stock never produces the duplicate:</para>
        /// <list type="number">
        /// <item><see cref="StrategyStockUpdateAction.ApplyCommittedDeactivation"/>: the
        /// latest committed row at or before now is a deactivation (so it comes after the
        /// latest committed activation), and it is later than stock's own activation date.
        /// The caller switches the strategy off without a capture and skips stock's Update.
        /// This also applies a committed player deactivation on time.</item>
        /// <item><see cref="StrategyStockUpdateAction.HoldExpiryUntilCommittedDeactivation"/>:
        /// the committed timeline has the strategy on at now, stock's expiry is due, and the
        /// strategy's next committed row is a deactivation. A committed expiry row carries
        /// the frame UT at which the original run expired it, which is at or after stock's
        /// threshold, and a replayed run's frames fall at other UTs; without the hold the
        /// replay could expire it a frame early and still record the duplicate. The caller
        /// skips stock's expiry until the committed UT, where case 1 applies.</item>
        /// <item><see cref="StrategyStockUpdateAction.RunStock"/> otherwise: a strategy the
        /// committed timeline never activated (unmanaged), a stock activation dated at or
        /// after the committed deactivation (a newer activation the committed timeline does
        /// not end), or a committed timeline that keeps it on with no committed deactivation
        /// next (a genuine expiry in the new timeline, captured as before).</item>
        /// </list>
        /// A committed activation and deactivation at the same UT read deactivation first
        /// (as <see cref="FirstFutureRow"/> does), so the state after the tie is on, and a
        /// stock activation dated at the deactivation's UT is never switched off: an
        /// ambiguous case leaves the strategy on.
        /// </summary>
        internal static StrategyStockUpdateDecision DecideStockUpdate(
            StrategyCommittedRows rows,
            bool stockActive,
            double stockDateActivated,
            bool stockExpiryDue,
            double nowUT)
        {
            if (!stockActive)
                return StrategyStockUpdateDecision.Run("not-active");
            if (rows == null || !rows.HasActivation)
                return StrategyStockUpdateDecision.Run("unmanaged");

            int last = rows.LastAtOrBefore(nowUT);
            if (last >= 0 && !rows.IsActivation(last))
            {
                double offUT = rows.UTAt(last);
                if (offUT > stockDateActivated)
                {
                    return new StrategyStockUpdateDecision
                    {
                        Action = StrategyStockUpdateAction.ApplyCommittedDeactivation,
                        CommittedUT = offUT,
                        Reason = "committed-off"
                    };
                }
                return StrategyStockUpdateDecision.Run("activated-after-committed-off");
            }

            if (!stockExpiryDue)
                return StrategyStockUpdateDecision.Run("committed-on");
            if (last < 0)
                return StrategyStockUpdateDecision.Run("no-committed-activation-at-now");

            int next = rows.FirstAfter(nowUT);
            if (next < 0)
                return StrategyStockUpdateDecision.Run("no-committed-row-ahead");
            if (rows.IsActivation(next))
                return StrategyStockUpdateDecision.Run("committed-activation-ahead");
            return new StrategyStockUpdateDecision
            {
                Action = StrategyStockUpdateAction.HoldExpiryUntilCommittedDeactivation,
                CommittedUT = rows.UTAt(next),
                Reason = "held-for-committed-deactivation"
            };
        }
    }

    /// <summary>What the <c>Strategy.Update()</c> prefix does this frame.</summary>
    internal enum StrategyStockUpdateAction
    {
        /// <summary>Stock's Update runs (a genuine expiry included, captured as before).</summary>
        RunStock,
        /// <summary>The committed timeline has the strategy off at now: switch it off
        /// without a capture and skip stock's Update.</summary>
        ApplyCommittedDeactivation,
        /// <summary>Stock's expiry is due but the committed deactivation is still ahead:
        /// skip the expiry until the committed UT.</summary>
        HoldExpiryUntilCommittedDeactivation
    }

    /// <summary>The prefix decision, the committed row behind it and a log reason.</summary>
    internal struct StrategyStockUpdateDecision
    {
        internal StrategyStockUpdateAction Action;
        /// <summary>The committed deactivation's UT (applied or awaited); NaN for RunStock.</summary>
        internal double CommittedUT;
        internal string Reason;

        internal static StrategyStockUpdateDecision Run(string reason)
        {
            return new StrategyStockUpdateDecision
            {
                Action = StrategyStockUpdateAction.RunStock,
                CommittedUT = double.NaN,
                Reason = reason
            };
        }
    }

    /// <summary>
    /// One strategy's committed StrategyActivate / StrategyDeactivate UTs, ascending, a
    /// deactivation before an activation at the same UT (the order
    /// <c>StrategyReservationPredicates.FirstFutureRow</c> reads). Built once per strategy
    /// per committed-future index instance (<c>CommittedFutureIndex.StrategyRows</c>), so a
    /// new index (a ledger change) brings new rows; the per-frame reads are binary searches.
    /// Deliberately references no ledger or index type: it is plain data.
    /// </summary>
    internal sealed class StrategyCommittedRows
    {
        private readonly double[] uts;
        private readonly bool[] activation;

        /// <summary>True when the committed timeline activates the strategy at least once
        /// (the prefix leaves a strategy it never activated alone).</summary>
        internal bool HasActivation { get; }
        internal int Count => uts.Length;

        internal StrategyCommittedRows(IEnumerable<double> activationUTs, IEnumerable<double> deactivationUTs)
        {
            var rows = new List<KeyValuePair<double, bool>>();
            if (activationUTs != null)
                foreach (double ut in activationUTs) rows.Add(new KeyValuePair<double, bool>(ut, true));
            if (deactivationUTs != null)
                foreach (double ut in deactivationUTs) rows.Add(new KeyValuePair<double, bool>(ut, false));
            // UT ascending, deactivation (false) before activation (true) at a tie.
            rows.Sort((a, b) =>
            {
                int c = a.Key.CompareTo(b.Key);
                return c != 0 ? c : a.Value.CompareTo(b.Value);
            });
            uts = new double[rows.Count];
            activation = new bool[rows.Count];
            bool any = false;
            for (int i = 0; i < rows.Count; i++)
            {
                uts[i] = rows[i].Key;
                activation[i] = rows[i].Value;
                any |= rows[i].Value;
            }
            HasActivation = any;
        }

        internal double UTAt(int i) => uts[i];
        internal bool IsActivation(int i) => activation[i];

        /// <summary>The index of the first row still ahead of <paramref name="nowUT"/>, or
        /// <see cref="Count"/> when none. A row is ahead when its UT is strictly greater than
        /// now: the boundary of <c>CommittedFutureIndex.IsFuture</c>, restated here so this
        /// data type stays free of the index (a cell pins the two agree).</summary>
        private int InsertionPoint(double nowUT)
        {
            int lo = 0, hi = uts.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (uts[mid] > nowUT) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }

        /// <summary>The last row at or before <paramref name="nowUT"/>, or -1.</summary>
        internal int LastAtOrBefore(double nowUT)
        {
            return InsertionPoint(nowUT) - 1;
        }

        /// <summary>The first row still ahead of <paramref name="nowUT"/>, or -1.</summary>
        internal int FirstAfter(double nowUT)
        {
            int i = InsertionPoint(nowUT);
            return i < uts.Length ? i : -1;
        }

        /// <summary>True when the first committed row still ahead of
        /// <paramref name="nowUT"/> is a deactivation: the committed timeline keeps the
        /// strategy on until then. The state patch's FutureDeactivation and the
        /// <c>Strategy.Update()</c> prefix's hold read this one predicate.</summary>
        internal bool NextRowIsDeactivation(double nowUT)
        {
            int next = FirstAfter(nowUT);
            return next >= 0 && !activation[next];
        }
    }
}
