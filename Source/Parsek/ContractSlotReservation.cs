using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Contracts;

namespace Parsek
{
    /// <summary>
    /// The Mission Control slot forecast over the committed timeline: how many active
    /// contracts the committed future holds at its busiest point, and how many slots a
    /// contract accepted NOW could take without starving a committed accept. Immutable.
    /// Produced by <see cref="ContractSlotReservation.Forecast"/>.
    /// </summary>
    internal sealed class ContractSlotForecast
    {
        /// <summary>Stock's slot limit now (<c>GameVariables.GetActiveContractsLimit</c> at
        /// the current Mission Control level).</summary>
        internal readonly int LimitNow;

        /// <summary>Contracts active now, counted the way stock counts them
        /// (<c>ContractSystem.GetActiveContractCount</c>: Active and not auto-accepted).</summary>
        internal readonly int ActiveNow;

        /// <summary>The largest number of contracts active at once from now to the end of
        /// the committed timeline, without any new accept. Never below <see cref="ActiveNow"/>.</summary>
        internal readonly int PeakCommitted;

        /// <summary>
        /// Slots left for a contract accepted now and never resolved: the minimum, over now
        /// and every committed accept still ahead, of the slot limit at that moment minus
        /// the committed count at that moment. With no committed Mission Control upgrade
        /// ahead this is <c>LimitNow - PeakCommitted</c>. Negative when the committed
        /// timeline itself is over-subscribed.
        /// </summary>
        internal readonly int FreeSlotsForNewAcceptNow;

        /// <summary>The earliest committed accept a contract accepted now would leave without
        /// a slot (the committed count reaches the limit there), or null when none would.</summary>
        internal readonly CommittedFutureEntry FirstStarvedAccept;

        internal ContractSlotForecast(
            int limitNow, int activeNow, int peakCommitted, int freeSlotsForNewAcceptNow,
            CommittedFutureEntry firstStarvedAccept)
        {
            LimitNow = limitNow;
            ActiveNow = activeNow;
            PeakCommitted = peakCommitted;
            FreeSlotsForNewAcceptNow = freeSlotsForNewAcceptNow;
            FirstStarvedAccept = firstStarvedAccept;
        }

        /// <summary>Slots the committed timeline fills later: <c>PeakCommitted - ActiveNow</c>.</summary>
        internal int ReservedForLater => PeakCommitted - ActiveNow;

        /// <summary>
        /// True when Parsek refuses a new accept now: stock itself would allow it (a slot is
        /// free NOW), but no slot is left at a committed accept later. When no slot is free
        /// now stock's own rule already greys Accept, so Parsek does not report it again.
        /// </summary>
        internal bool BlocksNewAcceptNow => ActiveNow < LimitNow && FreeSlotsForNewAcceptNow <= 0;

        /// <summary>The per-forecast log fragment, invariant.</summary>
        internal string Describe()
        {
            var ic = CultureInfo.InvariantCulture;
            return "limitNow=" + LimitNow.ToString(ic)
                   + " activeNow=" + ActiveNow.ToString(ic)
                   + " peak=" + PeakCommitted.ToString(ic)
                   + " reservedForLater=" + ReservedForLater.ToString(ic)
                   + " free=" + FreeSlotsForNewAcceptNow.ToString(ic)
                   + " blocksNewAccept=" + (BlocksNewAcceptNow ? "true" : "false")
                   + " starvedAccept=" + (FirstStarvedAccept != null
                       ? FirstStarvedAccept.Key + "@" + FirstStarvedAccept.UT.ToString("F0", ic)
                       : "none");
        }
    }

    /// <summary>
    /// The contract-slot reservation (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md,
    /// section 4 "C2", section 10 step 4). Pure: every live value (the contracts active now,
    /// the slot limit) comes in as an argument, and every committed row comes from the
    /// <see cref="CommittedFutureIndex"/> the other stock-screen blocks read, so a
    /// live or Re-Fly tree and an uncommitted recording reserve nothing.
    ///
    /// <para><b>Peak model.</b> From now to the end of the committed timeline:</para>
    /// <list type="bullet">
    /// <item>every contract active now counts from now until its first committed
    /// ContractComplete / ContractFail / ContractCancel row after now, or throughout when it
    /// has none (a derived deadline expiry is not a committed row);</item>
    /// <item>every committed ContractAccept after now adds its contract at that UT, until its
    /// first committed resolution at or after the accept (a contract that is already active
    /// is not counted twice);</item>
    /// <item>on a UT tie, removals come before a committed Mission Control upgrade, which comes
    /// before accepts (stock checks the slot at accept time); a contract accepted and resolved
    /// at the same UT still holds its slot at that accept;</item>
    /// <item>the limit starts at stock's limit now and rises at each committed Mission Control
    /// upgrade to stock's limit for the upgraded level.</item>
    /// </list>
    /// <para>A new contract accepted now counts throughout (it has no committed resolution),
    /// so it fits only if every committed accept still finds a slot with it added. Nothing
    /// after the last committed accept can raise the count, so the walk stops there.</para>
    /// </summary>
    internal static class ContractSlotReservation
    {
        private const string Tag = "ContractSlotReservation";

        /// <summary>The Mission Control facility's upgradeable id, as FacilityUpgrade rows key it.</summary>
        internal const string MissionControlFacilityId = "SpaceCenter/MissionControl";

        private static readonly CommittedFutureKind[] ResolutionKinds =
        {
            CommittedFutureKind.ContractComplete,
            CommittedFutureKind.ContractFail,
            CommittedFutureKind.ContractCancel
        };

        /// <summary>
        /// The slot forecast at <paramref name="currentUT"/>.
        /// </summary>
        /// <param name="index">The committed-future index.</param>
        /// <param name="activeNow">Contract keys (guid strings) active now, counted the way stock
        /// counts them (Active, not auto-accepted).</param>
        /// <param name="limitNow">Stock's slot limit now.</param>
        /// <param name="currentUT">Now. A committed row at this UT is already applied.</param>
        /// <param name="slotsForLevel">Slot limit for a 1-based Mission Control level (the level
        /// a committed upgrade reaches); null uses <see cref="LedgerOrchestrator.GetContractSlots"/>.</param>
        internal static ContractSlotForecast Forecast(
            CommittedFutureIndex index,
            IEnumerable<string> activeNow,
            int limitNow,
            double currentUT,
            Func<int, int> slotsForLevel = null)
        {
            var active = new HashSet<string>(StringComparer.Ordinal);
            if (activeNow != null)
                foreach (var key in activeNow)
                    if (!string.IsNullOrEmpty(key)) active.Add(key);

            int count = active.Count;
            long limit = limitNow;
            long free = limit - count;
            int peak = count;
            CommittedFutureEntry starved = null;

            if (index == null)
                return new ContractSlotForecast(limitNow, count, peak, ClampToInt(free), null);

            // One committed accept per contract: the earliest still ahead, for a contract
            // not already active.
            var accepts = new List<CommittedFutureEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var allAccepts = index.FutureEntriesOfKind(CommittedFutureKind.ContractAccept, currentUT);
            for (int i = 0; i < allAccepts.Count; i++)
            {
                var a = allAccepts[i];
                if (active.Contains(a.Key) || !seen.Add(a.Key)) continue;
                accepts.Add(a);
            }
            if (accepts.Count == 0)
                return new ContractSlotForecast(limitNow, count, peak, ClampToInt(free), null);
            double lastAcceptUT = accepts[accepts.Count - 1].UT;

            var events = new List<SlotEvent>();
            foreach (var key in active)
            {
                var end = FirstResolution(index, key, currentUT, strictlyAfter: true);
                if (end != null && end.UT <= lastAcceptUT)
                    events.Add(new SlotEvent(end.UT, RankRemoval, -1, end));
            }
            for (int i = 0; i < accepts.Count; i++)
            {
                var a = accepts[i];
                events.Add(new SlotEvent(a.UT, RankAccept, +1, a));
                var end = FirstResolution(index, a.Key, a.UT, strictlyAfter: false);
                if (end == null || end.UT > lastAcceptUT) continue;
                // Resolved at its own accept UT: it still held the slot when accepted.
                events.Add(new SlotEvent(end.UT, end.UT > a.UT ? RankRemoval : RankSameUtRemoval, -1, end));
            }
            AddUpgrades(index, MissionControlFacilityId, currentUT, lastAcceptUT, events);
            string displayId = KspFacilityIds.ToDisplayFacilityId(MissionControlFacilityId);
            if (!string.Equals(displayId, MissionControlFacilityId, StringComparison.Ordinal))
                AddUpgrades(index, displayId, currentUT, lastAcceptUT, events);

            events.Sort((x, y) =>
            {
                int c = x.UT.CompareTo(y.UT);
                if (c != 0) return c;
                c = x.Rank.CompareTo(y.Rank);
                return c != 0 ? c : string.CompareOrdinal(x.Entry.Key, y.Entry.Key);
            });

            Func<int, int> slots = slotsForLevel ?? LedgerOrchestrator.GetContractSlots;
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e.Rank == RankUpgrade)
                {
                    if (e.Entry.FacilityToLevel > 0)
                        limit = Math.Max(limit, slots(e.Entry.FacilityToLevel));
                    continue;
                }
                count += e.Delta;
                if (e.Rank != RankAccept) continue;

                if (count > peak) peak = count;
                long slack = limit - count;
                if (slack < free) free = slack;
                // A contract accepted now would make this accept overflow (count + 1 > limit).
                if (starved == null && slack <= 0) starved = e.Entry;
            }

            return new ContractSlotForecast(limitNow, active.Count, peak, ClampToInt(free), starved);
        }

        /// <summary>
        /// Slots left for a contract accepted now (<see cref="ContractSlotForecast.FreeSlotsForNewAcceptNow"/>).
        /// </summary>
        internal static int FreeSlotsForNewAcceptNow(
            CommittedFutureIndex index, IEnumerable<string> activeNow, int limitNow, double currentUT,
            Func<int, int> slotsForLevel = null)
        {
            return Forecast(index, activeNow, limitNow, currentUT, slotsForLevel).FreeSlotsForNewAcceptNow;
        }

        /// <summary>
        /// Slots the committed timeline fills later (<see cref="ContractSlotForecast.ReservedForLater"/>).
        /// </summary>
        internal static int ReservedForLater(
            CommittedFutureIndex index, IEnumerable<string> activeNow, int limitNow, double currentUT,
            Func<int, int> slotsForLevel = null)
        {
            return Forecast(index, activeNow, limitNow, currentUT, slotsForLevel).ReservedForLater;
        }

        /// <summary>
        /// True when accepting the contract <paramref name="contractKey"/> now is refused for a
        /// slot: it is Offered, not auto-accepted, the committed timeline does not accept it
        /// itself (that is the committed-accept block, reported once, not twice), and
        /// <see cref="ContractSlotForecast.BlocksNewAcceptNow"/> holds.
        /// </summary>
        internal static bool IsAcceptSlotBlocked(
            ContractSlotForecast forecast, CommittedFutureIndex index, string contractKey,
            Contract.State state, bool autoAccept, double currentUT)
        {
            if (forecast == null || string.IsNullOrEmpty(contractKey)) return false;
            if (state != Contract.State.Offered || autoAccept) return false;
            if (StockUiReservationPredicates.IsContractAcceptBlocked(index, contractKey, currentUT)) return false;
            return forecast.BlocksNewAcceptNow;
        }

        /// <summary>The explanation for a slot-refused accept.</summary>
        internal static ReservationText Explain(ContractSlotForecast forecast, Func<double, string> formatDate)
        {
            return ReservationExplanation.ContractSlot(forecast != null ? forecast.FirstStarvedAccept : null, formatDate);
        }

        private static CommittedFutureEntry FirstResolution(
            CommittedFutureIndex index, string key, double fromUT, bool strictlyAfter)
        {
            CommittedFutureEntry best = null;
            for (int k = 0; k < ResolutionKinds.Length; k++)
            {
                var list = index.AllEntries(ResolutionKinds[k], key);
                for (int i = 0; i < list.Count; i++)
                {
                    var r = list[i];
                    bool inRange = strictlyAfter ? r.UT > fromUT : r.UT >= fromUT;
                    if (!inRange) continue;
                    if (best == null || r.UT < best.UT) best = r;
                    break; // UT ascending: the first in range is this kind's earliest
                }
            }
            return best;
        }

        private static void AddUpgrades(
            CommittedFutureIndex index, string key, double currentUT, double untilUT, List<SlotEvent> events)
        {
            var upgrades = index.FutureEntries(CommittedFutureKind.FacilityUpgrade, key, currentUT);
            for (int i = 0; i < upgrades.Count; i++)
                if (upgrades[i].UT <= untilUT)
                    events.Add(new SlotEvent(upgrades[i].UT, RankUpgrade, 0, upgrades[i]));
        }

        private static int ClampToInt(long value)
        {
            if (value > int.MaxValue) return int.MaxValue;
            if (value < int.MinValue) return int.MinValue;
            return (int)value;
        }

        private const int RankRemoval = 0;
        private const int RankUpgrade = 1;
        private const int RankAccept = 2;
        private const int RankSameUtRemoval = 3;

        private struct SlotEvent
        {
            internal readonly double UT;
            internal readonly int Rank;
            internal readonly int Delta;
            internal readonly CommittedFutureEntry Entry;

            internal SlotEvent(double ut, int rank, int delta, CommittedFutureEntry entry)
            {
                UT = ut;
                Rank = rank;
                Delta = delta;
                Entry = entry;
            }
        }

        // ---------------------------------------------------------------- live inputs

        /// <summary>
        /// Test seam: replaces the live stock read (the contracts active now and the slot
        /// limits). Null in production.
        /// </summary>
        internal static Func<CommittedFutureIndex, double, ContractSlotForecast> ForecastProviderForTesting;

        /// <summary>
        /// The forecast over the live career: <see cref="CommittedFutureIndexCache.Current"/>,
        /// <see cref="CommittedFutureIndexCache.CurrentUT"/> and stock's contract list and slot
        /// rule. Null when stock's contract system or slot rule is unavailable (no Career game,
        /// a scene without Mission Control state): no slot is reserved then.
        /// </summary>
        internal static ContractSlotForecast ForecastNow()
        {
            return ForecastNow(CommittedFutureIndexCache.Current, CommittedFutureIndexCache.CurrentUT());
        }

        /// <summary>The live forecast over a given index and UT (one fetch per decoration pass).</summary>
        internal static ContractSlotForecast ForecastNow(CommittedFutureIndex index, double currentUT)
        {
            var provider = ForecastProviderForTesting;
            if (provider != null) return provider(index, currentUT);
            try
            {
                List<string> active;
                int limitNow;
                if (!TryReadLiveSlotState(out active, out limitNow))
                    return null;
                return Forecast(index, active, limitNow, currentUT, LiveSlotsForLevel);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "live-forecast",
                    "Contract slot forecast unavailable (" + ex.GetType().Name + ": " + ex.Message
                    + ") - no slot reserved for committed accepts");
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadLiveSlotState(out List<string> active, out int limitNow)
        {
            active = null;
            limitNow = 0;
            ContractSystem system = ContractSystem.Instance;
            GameVariables variables = GameVariables.Instance;
            if (system == null || system.Contracts == null || variables == null)
            {
                ParsekLog.VerboseRateLimited(Tag, "live-unavailable",
                    "Contract slot forecast: ContractSystem or GameVariables unavailable - no slot reserved");
                return false;
            }

            limitNow = variables.GetActiveContractsLimit(
                ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.MissionControl));
            active = new List<string>();
            var contracts = system.Contracts;
            for (int i = 0; i < contracts.Count; i++)
            {
                Contract c = contracts[i];
                // Stock's GetActiveContractCount: Active and not auto-accepted.
                if (c == null || c.ContractState != Contract.State.Active || c.AutoAccept) continue;
                active.Add(c.ContractGuid.ToString());
            }
            return true;
        }

        /// <summary>
        /// Stock's slot limit for a 1-based Mission Control level:
        /// <c>GameVariables.GetActiveContractsLimit</c> at the normalized level
        /// (<c>(level - 1) / 2</c>, stock's three tiers, the inverse of the converter's
        /// <c>ToLevel</c>). Virtual in stock, so a mod's override is honoured.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int LiveSlotsForLevel(int level)
        {
            GameVariables variables = GameVariables.Instance;
            if (variables == null) return LedgerOrchestrator.GetContractSlots(level);
            return variables.GetActiveContractsLimit(NormalizedMissionControlLevel(level));
        }

        /// <summary>The normalized facility level of a 1-based stock tier.</summary>
        internal static float NormalizedMissionControlLevel(int level)
        {
            if (level <= 1) return 0f;
            if (level >= 3) return 1f;
            return (level - 1) * 0.5f;
        }

        internal static void ResetForTesting()
        {
            ForecastProviderForTesting = null;
        }
    }
}
