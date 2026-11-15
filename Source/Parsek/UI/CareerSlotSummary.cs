using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// How many contract and strategy slots are free NOW once the committed future has
    /// taken its share: the one fact the player cannot read on a stock screen (stock's
    /// Mission Control and Administration count only what is active now). Drawn as one
    /// sentence in the hover of the Timeline's Contracts and Strategies view buttons
    /// (<see cref="FormatSlotSentence"/>), Career mode only.
    ///
    /// <para>Contracts read the shared Mission Control slot forecast
    /// (<see cref="ContractSlotReservation"/>), the same query that refuses an accept at
    /// Mission Control, so the hover and the block can never disagree. Strategies have no
    /// shared query (Administration's per-strategy refusal,
    /// <c>StrategyReservationPredicates</c>, has no count to read) and use the peak walk
    /// here (<see cref="ComputeSlotUsage"/>).</para>
    ///
    /// <para>Pure: no Unity or KSP state is read. The caller passes the effective ledger
    /// (<c>EffectiveState.ComputeELS()</c>), the live UT and, when stock's contract state is
    /// readable, the live forecast.</para>
    /// </summary>
    internal static class CareerSlotSummary
    {
        /// <summary>A slot limit at or above this is stock's "no limit" (Mission Control L3 = 999).</summary>
        internal const int UnlimitedSlotThreshold = 999;

        // The two buildings whose level sets a slot limit (matches
        // LedgerOrchestrator.UpdateSlotLimitsFromFacilities).
        private const string MissionControlFacilityId = "MissionControl";
        private const string AdministrationFacilityId = "Administration";

        /// <summary>Which slot pool a sentence is about.</summary>
        internal enum SlotKind
        {
            Contracts,
            Strategies,
        }

        /// <summary>
        /// One change the recorded timeline makes to a slot pool after live UT: an entry
        /// taking a slot (+1) or giving one back (-1), or the building upgraded
        /// (<see cref="NewLimit"/>; -1 when the limit does not change).
        /// </summary>
        internal struct SlotChange
        {
            public double UT;
            public int Delta;
            public int NewLimit;

            internal static SlotChange Occupy(double ut) => new SlotChange { UT = ut, Delta = 1, NewLimit = -1 };
            internal static SlotChange Release(double ut) => new SlotChange { UT = ut, Delta = -1, NewLimit = -1 };
            internal static SlotChange Limit(double ut, int newLimit) => new SlotChange { UT = ut, Delta = 0, NewLimit = newLimit };
        }

        /// <summary>
        /// A pool's slots NOW, read against the recorded future:
        /// <c>Free + Active + Reserved == Limit</c> whenever the limit is not over-subscribed.
        /// </summary>
        internal struct SlotUsage
        {
            public int Active;       // active at live UT
            public int Limit;        // the slot limit at live UT
            public bool Unlimited;   // Limit >= UnlimitedSlotThreshold
            public int PeakNeed;     // most slots the recorded future holds at once, against today's limit
            public int Reserved;     // PeakNeed - Active, never negative
            public int Free;         // Limit - PeakNeed, never negative
        }

        /// <summary>Both pools at one live UT, plus what the log line names.</summary>
        internal struct Snapshot
        {
            public SlotUsage Contracts;
            public SlotUsage Strategies;
            // "live" when the caller's stock forecast was used, else "ledger".
            public string ContractSource;
            public int MissionControlLevel;
            public int AdministrationLevel;
        }

        private struct ContractNow
        {
            public double AcceptUT;
            public double DeadlineUT;
        }

        /// <summary>
        /// Walks <paramref name="actions"/> once in order. Only
        /// <see cref="GameAction.Effective"/> actions count (mirrors ContractsModule /
        /// StrategiesModule.ProcessAction); an action with <c>UT &lt;= liveUT</c> is already
        /// applied. A contract whose deadline has passed by an action's UT closes at its
        /// deadline before that action is read (ContractsModule runs CheckDeadlines before it
        /// dispatches). Facility upgrades are read only for the Mission Control and
        /// Administration levels behind the two limits.
        ///
        /// <para>Contracts: <paramref name="liveContractForecast"/> when the caller has one
        /// (<c>ContractSlotReservation.ForecastNow()</c>), else the same pure forecast over
        /// these rows (<see cref="ForecastContractSlotsFromLedger"/>).
        /// <paramref name="isAutoAcceptContract"/> marks stock auto-accept contracts, which
        /// hold no slot; null treats none as one.</para>
        /// </summary>
        internal static Snapshot Build(
            IReadOnlyList<GameAction> actions,
            double liveUT,
            Func<string, bool> isAutoAcceptContract = null,
            ContractSlotForecast liveContractForecast = null)
        {
            var activeContracts = new Dictionary<string, ContractNow>(StringComparer.Ordinal);
            var activeStrategies = new HashSet<string>(StringComparer.Ordinal);
            var facilityLevels = new Dictionary<string, int>(StringComparer.Ordinal);
            var strategyChanges = new List<SlotChange>();
            var expired = new List<string>();

            Dictionary<string, ContractNow> contractsNow = null;
            int strategiesNow = 0;
            int missionControlNow = 1;
            int administrationNow = 1;
            bool snapshotTaken = false;

            int count = actions != null ? actions.Count : 0;
            for (int i = 0; i < count; i++)
            {
                GameAction a = actions[i];
                if (a == null) continue;

                if (!snapshotTaken && a.UT > liveUT)
                {
                    contractsNow = new Dictionary<string, ContractNow>(activeContracts, StringComparer.Ordinal);
                    strategiesNow = activeStrategies.Count;
                    missionControlNow = LevelOf(facilityLevels, MissionControlFacilityId);
                    administrationNow = LevelOf(facilityLevels, AdministrationFacilityId);
                    snapshotTaken = true;
                }

                if (activeContracts.Count > 0)
                {
                    expired.Clear();
                    foreach (var kvp in activeContracts)
                        if (ContractsModule.HasContractDeadlineElapsed(
                                a.UT, kvp.Value.DeadlineUT, kvp.Value.AcceptUT))
                            expired.Add(kvp.Key);
                    for (int e = 0; e < expired.Count; e++)
                        activeContracts.Remove(expired[e]);
                }

                if (!a.Effective) continue;

                switch (a.Type)
                {
                    case GameActionType.ContractAccept:
                        activeContracts[a.ContractId ?? ""] = new ContractNow
                        {
                            AcceptUT = a.UT,
                            DeadlineUT = a.DeadlineUT
                        };
                        break;

                    case GameActionType.ContractComplete:
                    case GameActionType.ContractFail:
                    case GameActionType.ContractCancel:
                        activeContracts.Remove(a.ContractId ?? "");
                        break;

                    case GameActionType.StrategyActivate:
                    {
                        string sid = a.StrategyId ?? "";
                        if (snapshotTaken && !activeStrategies.Contains(sid))
                            strategyChanges.Add(SlotChange.Occupy(a.UT));
                        activeStrategies.Add(sid);
                        break;
                    }

                    case GameActionType.StrategyDeactivate:
                    {
                        string sid = a.StrategyId ?? "";
                        if (activeStrategies.Remove(sid) && snapshotTaken)
                            strategyChanges.Add(SlotChange.Release(a.UT));
                        break;
                    }

                    case GameActionType.FacilityUpgrade:
                    {
                        string facility = FacilityDisplayNames.FacilityIdForBuilding(a.FacilityId);
                        facilityLevels[facility] = a.ToLevel;
                        if (snapshotTaken && facility == AdministrationFacilityId)
                            strategyChanges.Add(SlotChange.Limit(
                                a.UT, LedgerOrchestrator.GetStrategySlots(a.ToLevel)));
                        break;
                    }
                }
            }

            if (!snapshotTaken)
            {
                contractsNow = activeContracts;
                strategiesNow = activeStrategies.Count;
                missionControlNow = LevelOf(facilityLevels, MissionControlFacilityId);
                administrationNow = LevelOf(facilityLevels, AdministrationFacilityId);
            }

            ContractSlotForecast forecast = liveContractForecast;
            string source = "live";
            if (forecast == null)
            {
                var holders = new List<ContractSlotHolder>(contractsNow.Count);
                foreach (var kvp in contractsNow)
                    holders.Add(new ContractSlotHolder(kvp.Key, kvp.Value.AcceptUT, kvp.Value.DeadlineUT));
                forecast = ForecastContractSlotsFromLedger(actions, holders,
                    LedgerOrchestrator.GetContractSlots(missionControlNow), liveUT,
                    isAutoAcceptContract);
                source = "ledger";
            }

            return new Snapshot
            {
                Contracts = SlotUsageFromForecast(forecast),
                Strategies = ComputeSlotUsage(strategiesNow,
                    LedgerOrchestrator.GetStrategySlots(administrationNow), strategyChanges),
                ContractSource = source,
                MissionControlLevel = missionControlNow,
                AdministrationLevel = administrationNow
            };
        }

        /// <summary>
        /// The pure Mission Control slot forecast over ledger rows: the committed-future
        /// index built from the EFFECTIVE rows (every row counts as committed - the caller
        /// already hands the effective ledger), the contracts active now with their accept
        /// UT and deadline (a stock auto-accept contract holds no slot, as stock counts), and
        /// the Mission Control limit now. The fallback when stock's contract state is not
        /// readable, and what the unit tests see.
        /// </summary>
        internal static ContractSlotForecast ForecastContractSlotsFromLedger(
            IReadOnlyList<GameAction> actions,
            IEnumerable<ContractSlotHolder> activeNow,
            int limitNow,
            double liveUT,
            Func<string, bool> isAutoAcceptContract)
        {
            var effective = new List<GameAction>(actions != null ? actions.Count : 0);
            if (actions != null)
                for (int i = 0; i < actions.Count; i++)
                    if (actions[i] != null && actions[i].Effective) effective.Add(actions[i]);
            CommittedFutureIndex index = CommittedFutureIndex.Build(
                effective, id => true, null, null, isAutoAcceptContract);

            var holders = new List<ContractSlotHolder>();
            if (activeNow != null)
            {
                foreach (var holder in activeNow)
                {
                    if (isAutoAcceptContract != null && isAutoAcceptContract(holder.Key)) continue;
                    holders.Add(holder);
                }
            }
            return ContractSlotReservation.Forecast(index, holders, limitNow, liveUT,
                LedgerOrchestrator.GetContractSlots);
        }

        /// <summary>
        /// A pool's numbers from the shared contract slot forecast. Active and the limit are
        /// the forecast's own. Free is <see cref="ContractSlotForecast.FreeSlotsForNewAcceptNow"/>
        /// clamped at 0 (negative means the committed timeline itself is over-subscribed).
        /// The peak is counted against TODAY's limit - the forecast's minimum slack is
        /// <c>Limit - peak</c> with every committed Mission Control upgrade's added slots
        /// taken off the count at its moment - so <c>PeakNeed = Limit - Free(raw)</c> and
        /// reserved is <c>PeakNeed - Active</c> (at least 0). That equals
        /// <see cref="ContractSlotForecast.ReservedForLater"/> when no upgrade lies ahead,
        /// and is smaller when an upgrade supplies the slots a later accept needs. Null
        /// reads an empty usage.
        /// </summary>
        internal static SlotUsage SlotUsageFromForecast(ContractSlotForecast forecast)
        {
            if (forecast == null) return new SlotUsage();
            long peakAgainstToday = (long)forecast.LimitNow - forecast.FreeSlotsForNewAcceptNow;
            int peak = (int)Math.Max(forecast.ActiveNow, Math.Min(int.MaxValue, peakAgainstToday));
            return new SlotUsage
            {
                Active = forecast.ActiveNow,
                Limit = forecast.LimitNow,
                Unlimited = forecast.LimitNow >= UnlimitedSlotThreshold,
                PeakNeed = peak,
                Free = Math.Max(0, forecast.FreeSlotsForNewAcceptNow),
                Reserved = Math.Max(0, peak - forecast.ActiveNow)
            };
        }

        /// <summary>
        /// A pool's slots now, read against the recorded future. Walks the future
        /// <paramref name="changes"/> in UT order from <paramref name="activeNow"/> (a release
        /// before an occupy at the same UT: a slot a deactivation frees is free for an
        /// activation on the same tick) and keeps the PEAK number held at once. A strategy
        /// that ends on day 50 and one a flight activates on day 60 share one slot; two that
        /// overlap need two. A later upgrade of the building counts against today's limit:
        /// the need at a moment is what is held then minus how many slots the upgrade has
        /// added by then.
        /// </summary>
        internal static SlotUsage ComputeSlotUsage(int activeNow, int limitNow,
                                                  IList<SlotChange> changes)
        {
            var usage = new SlotUsage
            {
                Active = activeNow,
                Limit = limitNow,
                Unlimited = limitNow >= UnlimitedSlotThreshold,
                PeakNeed = activeNow
            };
            if (changes != null && changes.Count > 0)
            {
                var sorted = new List<KeyValuePair<int, SlotChange>>(changes.Count);
                for (int i = 0; i < changes.Count; i++)
                    sorted.Add(new KeyValuePair<int, SlotChange>(i, changes[i]));
                sorted.Sort(CompareSlotChanges);
                int held = activeNow;
                int limit = limitNow;
                for (int i = 0; i < sorted.Count; i++)
                {
                    SlotChange c = sorted[i].Value;
                    held += c.Delta;
                    if (c.NewLimit >= 0) limit = c.NewLimit;
                    // No limit at that moment: nothing held then can crowd out today.
                    if (limit >= UnlimitedSlotThreshold) continue;
                    int need = held - (limit - limitNow);
                    if (need > usage.PeakNeed) usage.PeakNeed = need;
                }
            }
            usage.Reserved = Math.Max(0, usage.PeakNeed - activeNow);
            usage.Free = Math.Max(0, limitNow - usage.PeakNeed);
            return usage;
        }

        // UT order; at one UT releases, then limit changes, then occupies; then input
        // order, so the sort is stable.
        private static int CompareSlotChanges(KeyValuePair<int, SlotChange> x,
                                              KeyValuePair<int, SlotChange> y)
        {
            int byUt = x.Value.UT.CompareTo(y.Value.UT);
            if (byUt != 0) return byUt;
            int byRank = SlotChangeRank(x.Value).CompareTo(SlotChangeRank(y.Value));
            if (byRank != 0) return byRank;
            return x.Key.CompareTo(y.Key);
        }

        private static int SlotChangeRank(SlotChange c)
        {
            if (c.Delta < 0) return 0;
            if (c.Delta == 0) return 1;
            return 2;
        }

        // A facility with no upgrade in the ledger is at level 1.
        private static int LevelOf(Dictionary<string, int> levels, string facilityId)
        {
            int level;
            return levels != null && levels.TryGetValue(facilityId, out level) ? level : 1;
        }

        // ---- Text (pure, InvariantCulture, testable) ----

        /// <summary>
        /// The hover sentence, free first, then active, then reserved:
        /// <c>Contract slots: 4 of 7 free now (2 active, 1 reserved for later).</c>, the
        /// reserved clause dropped when the recorded future reserves none, and
        /// <c>Contract slots: no slot limit (2 active).</c> at an unlimited building.
        /// </summary>
        internal static string FormatSlotSentence(SlotKind kind, SlotUsage u)
        {
            var ic = CultureInfo.InvariantCulture;
            string head = kind == SlotKind.Strategies ? "Strategy slots: " : "Contract slots: ";
            string active = u.Active.ToString(ic) + " active";
            if (u.Unlimited)
                return head + "no slot limit (" + active + ").";
            string text = head + u.Free.ToString(ic) + " of " + u.Limit.ToString(ic)
                + " free now (" + active;
            if (u.Reserved > 0)
                text += ", " + u.Reserved.ToString(ic) + " reserved for later";
            return text + ").";
        }

        /// <summary>One pool for the log: <c>active=2/limit=7/peak=3/reserved=1/free=4</c>.</summary>
        internal static string FormatSlotUsageForLog(SlotUsage u)
        {
            var ic = CultureInfo.InvariantCulture;
            return "active=" + u.Active.ToString(ic)
                + "/limit=" + (u.Unlimited ? "none" : u.Limit.ToString(ic))
                + "/peak=" + u.PeakNeed.ToString(ic)
                + "/reserved=" + u.Reserved.ToString(ic)
                + "/free=" + u.Free.ToString(ic);
        }

        /// <summary>The whole snapshot for the log.</summary>
        internal static string FormatSnapshotForLog(Snapshot s, double liveUT)
        {
            var ic = CultureInfo.InvariantCulture;
            return "liveUT=" + liveUT.ToString("F0", ic)
                + " contractSlots=" + FormatSlotUsageForLog(s.Contracts)
                + "/source=" + (s.ContractSource ?? "none")
                + " strategySlots=" + FormatSlotUsageForLog(s.Strategies)
                + " missionControl=L" + s.MissionControlLevel.ToString(ic)
                + " administration=L" + s.AdministrationLevel.ToString(ic);
        }
    }
}
