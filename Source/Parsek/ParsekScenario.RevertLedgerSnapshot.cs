using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// A stock revert removes only the rows the reverted flight wrote (owner ruling O2,
    /// 2026-10-21; game-actions design 3.13). Both revert buttons go back to the state stock
    /// captured at the last flight entry (<c>FlightDriver.PostInitState</c>, rebuilt on every
    /// flight start, a revert and an F9 included; the pre-launch state of that same flight for
    /// Revert-to-editor), and a revert never crosses a cold load. So the rows the reverted
    /// flight wrote are exactly the rows added since that entry: every flight-scene OnLoad
    /// that is not itself a revert remembers the ledger's row ids, and the revert prune skips
    /// a remembered row. The committed future a rewind left behind (KSC purchases, credits kept
    /// from a discard, all untagged and later than the launch) is remembered, so it stays.
    /// Stock's <c>FlightStateCache.Load()</c> runs the scenario OnLoad before
    /// <c>ShipConstruction.AssembleForLaunch</c> and <c>OnVesselRollout</c>, so a fresh launch's
    /// rollout row is never remembered and Revert-to-editor still refunds it. [ERS-exempt]: the
    /// remembered set is the raw row ids the raw-row revert prune compares against.
    /// </summary>
    public partial class ParsekScenario
    {
        // The ledger row ids present at the last non-revert flight-scene OnLoad. Memory only.
        private static HashSet<string> flightEntryLedgerActionIds;

        internal static IReadOnlyCollection<string> FlightEntryLedgerActionIdsForTesting =>
            flightEntryLedgerActionIds;

        internal static void SetFlightEntryLedgerActionIdsForTesting(HashSet<string> ids)
        {
            flightEntryLedgerActionIds = ids;
        }

        /// <summary>
        /// Builds the set of row ids <see cref="Ledger.Actions"/> holds right now. Pure over
        /// the list it reads.
        /// </summary>
        internal static HashSet<string> CollectLedgerActionIds(IReadOnlyList<GameAction> actions)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (actions == null)
                return ids;
            for (int i = 0; i < actions.Count; i++)
            {
                string id = actions[i]?.ActionId;
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// Remembers the ledger's rows at a flight entry: the revert target of the flight that
        /// starts here. Called at the tail of every flight-scene OnLoad that is not a revert
        /// (a revert keeps the entry it went back to, so a later Revert-to-editor still refunds
        /// the rollout).
        /// </summary>
        internal static void RememberFlightEntryLedgerRows(string reason)
        {
            flightEntryLedgerActionIds = CollectLedgerActionIds(Ledger.Actions);
            ParsekLog.Verbose("Scenario",
                "Flight entry: remembered " +
                flightEntryLedgerActionIds.Count.ToString(CultureInfo.InvariantCulture) +
                " ledger row(s) as the revert target's rows (reason=" + (reason ?? "unspecified") + ")");
        }

        /// <summary>
        /// The revert prune: removes the untagged rows after the launch boundary that the
        /// reverted flight wrote, keeping every row the flight entry already held (O2). With no
        /// remembered entry (never in normal play: every flight start runs a flight-scene OnLoad)
        /// it falls back to removing every untagged row after the boundary, with a Warn.
        /// </summary>
        internal static int PruneRevertedFlightLedgerRows(double cutoffUT, bool inclusive)
        {
            HashSet<string> keep = flightEntryLedgerActionIds;
            if (keep == null)
            {
                ParsekLog.Warn("Scenario",
                    "Revert: no flight-entry ledger rows remembered; pruning every untagged row " +
                    (inclusive ? "at/after" : "after") + " UT " +
                    cutoffUT.ToString("R", CultureInfo.InvariantCulture));
            }
            return Ledger.PruneOrphanActionsAfterUT(cutoffUT, inclusive, keep);
        }
    }
}
