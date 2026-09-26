using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Contracts;

namespace Parsek
{
    /// <summary>
    /// One contract active now, as the slot forecast needs it: its key (guid string), the
    /// UT it was accepted and its absolute deadline (NaN when it has none).
    /// </summary>
    internal struct ContractSlotHolder
    {
        internal string Key;
        internal double AcceptUT;
        internal double DeadlineUT;

        internal ContractSlotHolder(string key, double acceptUT, double deadlineUT)
        {
            Key = key;
            AcceptUT = acceptUT;
            DeadlineUT = deadlineUT;
        }
    }

    /// <summary>
    /// The Mission Control slot forecast over the committed timeline: how many active
    /// contracts the committed future holds at its busiest point, and how many slots a
    /// contract accepted NOW could take without starving a committed accept. Immutable.
    /// Produced by <see cref="ContractSlotReservation.Forecast(CommittedFutureIndex, IEnumerable{ContractSlotHolder}, int, double, Func{int, int})"/>.
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
        /// Slots left for a contract accepted now that holds its slot throughout (no
        /// deadline): the minimum, over now and every committed accept still ahead, of the
        /// slot limit at that moment minus the committed count at that moment. With no
        /// committed Mission Control upgrade ahead this is <c>LimitNow - PeakCommitted</c>.
        /// Negative when the committed timeline itself is over-subscribed.
        /// </summary>
        internal readonly int FreeSlotsForNewAcceptNow;

        /// <summary>The earliest committed accept a contract accepted now (and still holding
        /// its slot then) would leave without a slot, or null when none would.</summary>
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
        /// True when Parsek refuses a new accept now of a contract that would hold its slot
        /// until <paramref name="newContractReleaseUT"/> (its deadline, or +inf; see
        /// <see cref="ContractSlotReservation.SlotReleaseUT"/>): stock itself would allow it (a
        /// slot is free NOW), but the earliest committed accept left without a slot comes
        /// strictly before the new contract's release (a release at the same UT frees the
        /// slot first). When no slot is free now stock's own rule already greys Accept, so
        /// Parsek does not report it again.
        /// </summary>
        internal bool BlocksNewAccept(double newContractReleaseUT)
        {
            return ActiveNow < LimitNow
                   && FirstStarvedAccept != null
                   && FirstStarvedAccept.UT < newContractReleaseUT;
        }

        /// <summary><see cref="BlocksNewAccept"/> for a new contract with no deadline.</summary>
        internal bool BlocksNewAcceptNow => BlocksNewAccept(double.PositiveInfinity);

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
    /// <item>every contract active now holds a slot from now until the EARLIER of its first
    /// committed ContractComplete / ContractFail / ContractCancel row after now and its
    /// deadline, or throughout when it has neither;</item>
    /// <item>every committed ContractAccept after now adds its contract at that UT, until the
    /// earlier of its first committed resolution at or after the accept and its accepted
    /// deadline (a contract that is already active is not counted twice, and a stock
    /// auto-accept contract, <see cref="CommittedFutureEntry.AutoAccept"/>, is not counted:
    /// stock's <c>GetActiveContractCount</c> skips those);</item>
    /// <item>a deadline releases the slot AT the deadline UT, the same rule the ledger walk's
    /// <c>ContractsModule.CheckDeadlines</c> uses (<c>HasContractDeadlineElapsed</c>) and
    /// stock's <c>Contract.Update</c> (<c>GameTime &gt;= dateDeadline</c> expires it); a NaN
    /// deadline and an implausible one (<c>ContractsModule.IsImplausibleContractDeadline</c>:
    /// not strictly after the accept) are open-ended (<see cref="SlotReleaseUT"/>);</item>
    /// <item>on a UT tie, removals come before a committed Mission Control upgrade, which comes
    /// before accepts (stock checks the slot at accept time); a contract accepted and resolved
    /// at the same UT still holds its slot at that accept;</item>
    /// <item>the limit starts at stock's limit now and rises at each committed Mission Control
    /// upgrade to stock's limit for the upgraded level.</item>
    /// </list>
    /// <para>A new contract accepted now holds its slot until its own deadline
    /// (<see cref="NewAcceptReleaseUT(Contract.DeadlineType, double, double, double)"/>), so it
    /// fits only if every committed accept before that release still finds a slot with it
    /// added (<see cref="ContractSlotForecast.BlocksNewAccept"/>). Nothing after the last
    /// committed accept can raise the count, so the walk stops there.</para>
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
        /// The UT a contract accepted at <paramref name="acceptUT"/> gives its slot back by
        /// deadline: <paramref name="deadlineUT"/> when it is a real deadline, else +inf
        /// (NaN, or implausible per <c>ContractsModule.IsImplausibleContractDeadline</c>).
        /// <c>ContractsModule.HasContractDeadlineElapsed(t, deadline, accept)</c> is true
        /// exactly for <c>t &gt;= </c> this value, so the forecast frees the slot where the
        /// ledger walk does.
        /// </summary>
        internal static double SlotReleaseUT(double acceptUT, double deadlineUT)
        {
            if (double.IsNaN(deadlineUT) || ContractsModule.IsImplausibleContractDeadline(deadlineUT, acceptUT))
                return double.PositiveInfinity;
            return deadlineUT;
        }

        /// <summary>
        /// The slot-release UT of an Offered contract accepted at <paramref name="nowUT"/>,
        /// following stock's <c>Contract.Accept</c>: a Floating deadline becomes
        /// <c>now + TimeDeadline</c>, a Fixed one keeps the <c>DateDeadline</c> set at offer
        /// time, and None never expires. Then <see cref="SlotReleaseUT"/>.
        /// </summary>
        internal static double NewAcceptReleaseUT(
            Contract.DeadlineType deadlineType, double dateDeadline, double timeDeadline, double nowUT)
        {
            switch (deadlineType)
            {
                case Contract.DeadlineType.None:
                    return double.PositiveInfinity;
                case Contract.DeadlineType.Fixed:
                    return SlotReleaseUT(nowUT, dateDeadline);
                default:
                    return SlotReleaseUT(nowUT, nowUT + timeDeadline);
            }
        }

        /// <summary>
        /// The slot forecast at <paramref name="currentUT"/>.
        /// </summary>
        /// <param name="index">The committed-future index.</param>
        /// <param name="activeNow">The contracts active now, counted the way stock counts them
        /// (Active, not auto-accepted), with their accept UT and deadline.</param>
        /// <param name="limitNow">Stock's slot limit now.</param>
        /// <param name="currentUT">Now. A committed row at this UT is already applied.</param>
        /// <param name="slotsForLevel">Slot limit for a 1-based Mission Control level (the level
        /// a committed upgrade reaches); null uses <see cref="LedgerOrchestrator.GetContractSlots"/>.</param>
        internal static ContractSlotForecast Forecast(
            CommittedFutureIndex index,
            IEnumerable<ContractSlotHolder> activeNow,
            int limitNow,
            double currentUT,
            Func<int, int> slotsForLevel = null)
        {
            var active = new Dictionary<string, ContractSlotHolder>(StringComparer.Ordinal);
            if (activeNow != null)
                foreach (var holder in activeNow)
                    if (!string.IsNullOrEmpty(holder.Key) && !active.ContainsKey(holder.Key))
                        active.Add(holder.Key, holder);

            int count = active.Count;
            long limit = limitNow;
            long free = limit - count;
            int peak = count;
            CommittedFutureEntry starved = null;

            if (index == null)
                return new ContractSlotForecast(limitNow, count, peak, ClampToInt(free), null);

            // One committed accept per contract: the earliest still ahead, for a contract
            // not already active and not a stock auto-accept contract.
            var accepts = new List<CommittedFutureEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var allAccepts = index.FutureEntriesOfKind(CommittedFutureKind.ContractAccept, currentUT);
            for (int i = 0; i < allAccepts.Count; i++)
            {
                var a = allAccepts[i];
                if (active.ContainsKey(a.Key) || !seen.Add(a.Key)) continue;
                if (a.AutoAccept) continue;
                accepts.Add(a);
            }
            if (accepts.Count == 0)
                return new ContractSlotForecast(limitNow, count, peak, ClampToInt(free), null);
            double lastAcceptUT = accepts[accepts.Count - 1].UT;

            var events = new List<SlotEvent>();
            foreach (var holder in active.Values)
            {
                var resolution = FirstResolution(index, holder.Key, currentUT, strictlyAfter: true);
                double release = Math.Min(
                    resolution != null ? resolution.UT : double.PositiveInfinity,
                    SlotReleaseUT(holder.AcceptUT, holder.DeadlineUT));
                if (release <= lastAcceptUT)
                    events.Add(new SlotEvent(release, RankRemoval, -1, holder.Key));
            }
            for (int i = 0; i < accepts.Count; i++)
            {
                var a = accepts[i];
                events.Add(new SlotEvent(a.UT, RankAccept, +1, a.Key, a));
                var resolution = FirstResolution(index, a.Key, a.UT, strictlyAfter: false);
                double release = Math.Min(
                    resolution != null ? resolution.UT : double.PositiveInfinity,
                    SlotReleaseUT(a.UT, a.DeadlineUT));
                if (release > lastAcceptUT) continue;
                // Resolved at its own accept UT: it still held the slot when accepted.
                events.Add(new SlotEvent(release, release > a.UT ? RankRemoval : RankSameUtRemoval, -1, a.Key));
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
                return c != 0 ? c : string.CompareOrdinal(x.Key, y.Key);
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
                // A contract accepted now and still holding its slot here would make this
                // accept overflow (count + 1 > limit).
                if (starved == null && slack <= 0) starved = e.Entry;
            }

            return new ContractSlotForecast(limitNow, active.Count, peak, ClampToInt(free), starved);
        }

        /// <summary>
        /// The forecast for contracts active now given by key only: each holds its slot until
        /// a committed resolution, with no deadline (open-ended).
        /// </summary>
        internal static ContractSlotForecast Forecast(
            CommittedFutureIndex index,
            IEnumerable<string> activeNow,
            int limitNow,
            double currentUT,
            Func<int, int> slotsForLevel = null)
        {
            return Forecast(index, OpenEnded(activeNow), limitNow, currentUT, slotsForLevel);
        }

        private static IEnumerable<ContractSlotHolder> OpenEnded(IEnumerable<string> keys)
        {
            if (keys == null) yield break;
            foreach (var key in keys)
                yield return new ContractSlotHolder(key, double.NaN, double.NaN);
        }

        /// <summary>
        /// Slots left for a contract accepted now with no deadline
        /// (<see cref="ContractSlotForecast.FreeSlotsForNewAcceptNow"/>).
        /// </summary>
        internal static int FreeSlotsForNewAcceptNow(
            CommittedFutureIndex index, IEnumerable<ContractSlotHolder> activeNow, int limitNow, double currentUT,
            Func<int, int> slotsForLevel = null)
        {
            return Forecast(index, activeNow, limitNow, currentUT, slotsForLevel).FreeSlotsForNewAcceptNow;
        }

        /// <summary>
        /// Slots the committed timeline fills later (<see cref="ContractSlotForecast.ReservedForLater"/>).
        /// </summary>
        internal static int ReservedForLater(
            CommittedFutureIndex index, IEnumerable<ContractSlotHolder> activeNow, int limitNow, double currentUT,
            Func<int, int> slotsForLevel = null)
        {
            return Forecast(index, activeNow, limitNow, currentUT, slotsForLevel).ReservedForLater;
        }

        /// <summary>
        /// True when accepting the contract <paramref name="contractKey"/> now is refused for a
        /// slot: it is Offered, not auto-accepted, the committed timeline does not accept it
        /// itself (that is the committed-accept block, reported once, not twice), and
        /// <see cref="ContractSlotForecast.BlocksNewAccept"/> holds for its release UT
        /// (<see cref="NewAcceptReleaseUT(Contract.DeadlineType, double, double, double)"/>;
        /// +inf for no deadline).
        /// </summary>
        internal static bool IsAcceptSlotBlocked(
            ContractSlotForecast forecast, CommittedFutureIndex index, string contractKey,
            Contract.State state, bool autoAccept, double currentUT,
            double newContractReleaseUT = double.PositiveInfinity)
        {
            if (forecast == null || string.IsNullOrEmpty(contractKey)) return false;
            if (state != Contract.State.Offered || autoAccept) return false;
            if (StockUiReservationPredicates.IsContractAcceptBlocked(index, contractKey, currentUT)) return false;
            return forecast.BlocksNewAccept(newContractReleaseUT);
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
                    events.Add(new SlotEvent(upgrades[i].UT, RankUpgrade, 0, upgrades[i].Key, upgrades[i]));
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
            internal readonly string Key;
            /// <summary>The committed row (accepts and upgrades), or null for a removal.</summary>
            internal readonly CommittedFutureEntry Entry;

            internal SlotEvent(double ut, int rank, int delta, string key, CommittedFutureEntry entry = null)
            {
                UT = ut;
                Rank = rank;
                Delta = delta;
                Key = key ?? "";
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
                List<ContractSlotHolder> active;
                int limitNow;
                if (!TryReadLiveSlotState(out active, out limitNow))
                    return null;
                return WithStarvedAcceptAgent(
                    Forecast(index, active, limitNow, currentUT, LiveSlotsForLevel),
                    CommittedFutureIndexCache.LiveContractAgentTitle);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "live-forecast",
                    "Contract slot forecast unavailable (" + ex.GetType().Name + ": " + ex.Message
                    + ") - no slot reserved for committed accepts");
                return null;
            }
        }

        /// <summary>
        /// The forecast with its starved accept's agent filled in at render time. The index
        /// reads the agent from the accept snapshot only; a committed accept with no snapshot
        /// agent is resolved here through <paramref name="resolveAgent"/> (production: stock's
        /// live contract list, loaded by the time Mission Control or the Accept backstop
        /// asks). Returns the forecast unchanged when it has no starved accept, the accept
        /// already names its agent, or the resolver finds none or throws.
        /// </summary>
        internal static ContractSlotForecast WithStarvedAcceptAgent(
            ContractSlotForecast forecast, Func<string, string> resolveAgent)
        {
            if (forecast == null || resolveAgent == null) return forecast;
            CommittedFutureEntry starved = forecast.FirstStarvedAccept;
            if (starved == null || starved.AgentTitle != null || string.IsNullOrEmpty(starved.Key))
                return forecast;

            string agent;
            try
            {
                agent = resolveAgent(starved.Key);
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited(Tag, "starved-accept-agent-error",
                    "Starved accept " + starved.Key + " agent not resolved (" + ex.GetType().Name
                    + ") - slot reason names no agent");
                return forecast;
            }
            agent = agent != null ? agent.Trim() : null;
            if (string.IsNullOrEmpty(agent))
            {
                ParsekLog.VerboseRateLimited(Tag, "starved-accept-agent-none",
                    "Starved accept " + starved.Key + " has no snapshot agent and stock lists none - slot reason names no agent");
                return forecast;
            }

            ParsekLog.VerboseRateLimited(Tag, "starved-accept-agent-resolved",
                "Starved accept " + starved.Key + " agent resolved at render time: '" + agent + "'");
            return new ContractSlotForecast(
                forecast.LimitNow, forecast.ActiveNow, forecast.PeakCommitted, forecast.FreeSlotsForNewAcceptNow,
                starved.WithAgentTitle(agent));
        }

        private static readonly FieldInfo DeadlineTypeField =
            typeof(Contract).GetField("deadlineType", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>
        /// The slot-release UT of a live Offered <paramref name="contract"/> if accepted at
        /// <paramref name="nowUT"/>. Stock keeps the deadline type in a protected field; when
        /// it cannot be read the contract is treated as Floating, stock's default.
        /// </summary>
        internal static double NewAcceptReleaseUT(Contract contract, double nowUT)
        {
            if (contract == null) return double.PositiveInfinity;
            Contract.DeadlineType type = Contract.DeadlineType.Floating;
            try
            {
                if (DeadlineTypeField != null)
                    type = (Contract.DeadlineType)DeadlineTypeField.GetValue(contract);
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited(Tag, "deadline-type",
                    "Contract.deadlineType unreadable (" + ex.GetType().Name + ") - treating the deadline as Floating");
            }
            return NewAcceptReleaseUT(type, contract.DateDeadline, contract.TimeDeadline, nowUT);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadLiveSlotState(out List<ContractSlotHolder> active, out int limitNow)
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
            active = new List<ContractSlotHolder>();
            var contracts = system.Contracts;
            for (int i = 0; i < contracts.Count; i++)
            {
                Contract c = contracts[i];
                // Stock's GetActiveContractCount: Active and not auto-accepted.
                if (c == null || c.ContractState != Contract.State.Active || c.AutoAccept) continue;
                active.Add(new ContractSlotHolder(c.ContractGuid.ToString(), c.DateAccepted, c.DateDeadline));
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
