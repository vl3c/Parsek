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
        SlotNeeded
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
    /// StrategyDeactivate row like any other deactivation.</para>
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
            Func<int, int> slotsForLevel = null)
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
            if (decision.Kind == StrategyActivationBlockKind.SlotNeeded)
                s += " count=" + decision.CountAtOverflow.ToString(ic)
                     + " limit=" + decision.LimitAtOverflow.ToString(ic);
            return s;
        }
    }
}
