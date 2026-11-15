using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// A stock revert removes only the rows the reverted flight wrote (owner ruling O2,
    /// 2026-10-21; game-actions design 3.13). A revert goes back to a state stock captured at a
    /// flight entry and never crosses a cold load into another save, so the rows the reverted
    /// flight wrote are exactly the rows added since that entry: Parsek remembers the ledger's
    /// row ids at the same entries, and the revert prune skips a remembered row. The committed
    /// future a rewind left behind (KSC purchases, credits kept from a discard, all untagged and
    /// later than the launch) is remembered, so it stays.
    /// <para>The two buttons have different targets, so there are two remembered sets.
    /// Revert-to-Launch goes back to <c>FlightDriver.PostInitState</c>, rebuilt at every flight
    /// start (a revert and an F9 included): the launch set is taken at every flight-scene OnLoad
    /// that is not a revert. Revert-to-editor goes back to <c>FlightDriver.PreLaunchState</c>,
    /// which stock rebuilds only at a fresh launch from the editor and still offers after a resume
    /// of the same vessel on the pad (an F9, a Fly from the Tracking Station; decompiled
    /// <c>FlightDriver.Start</c>): the prelaunch set is taken only at a fresh launch and, like
    /// stock's <c>PreLaunchState</c>, survives later loads. Stock's <c>FlightStateCache.Load()</c>
    /// runs the scenario OnLoad before <c>ShipConstruction.AssembleForLaunch</c> and
    /// <c>OnVesselRollout</c>, so a fresh launch's rollout row is in neither set and
    /// Revert-to-editor refunds it, after a resume too. [ERS-exempt]: the remembered sets are
    /// the raw row ids the raw-row revert prune compares against.</para>
    /// </summary>
    public partial class ParsekScenario
    {
        // The ledger row ids at the last non-revert flight-scene OnLoad (Revert-to-Launch).
        private static HashSet<string> flightEntryLedgerActionIds;

        // The ledger row ids at the last fresh launch (Revert-to-editor). Kept across loads,
        // as stock keeps PreLaunchState.
        private static HashSet<string> prelaunchEntryLedgerActionIds;

        // Set by this OnLoad's fresh-launch decision, consumed by its tail.
        private static bool freshLaunchEntryPending;

        internal static IReadOnlyCollection<string> FlightEntryLedgerActionIdsForTesting =>
            flightEntryLedgerActionIds;

        internal static IReadOnlyCollection<string> PrelaunchEntryLedgerActionIdsForTesting =>
            prelaunchEntryLedgerActionIds;

        internal static void SetFlightEntryLedgerActionIdsForTesting(HashSet<string> ids)
        {
            flightEntryLedgerActionIds = ids;
        }

        internal static void SetPrelaunchEntryLedgerActionIdsForTesting(HashSet<string> ids)
        {
            prelaunchEntryLedgerActionIds = ids;
        }

        internal static void SetFreshLaunchEntryPendingForTesting(bool pending)
        {
            freshLaunchEntryPending = pending;
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
        /// Remembers the ledger's rows at a flight entry: the Revert-to-Launch target of the
        /// flight that starts here, and at a fresh launch (<see cref="freshLaunchEntryPending"/>,
        /// set by this OnLoad's fresh-launch decision) the Revert-to-editor target too. Called at
        /// the tail of every flight-scene OnLoad that is not a revert (a revert keeps the entries
        /// it went back to).
        /// </summary>
        internal static void RememberFlightEntryLedgerRows(string reason)
        {
            bool freshLaunch = freshLaunchEntryPending;
            freshLaunchEntryPending = false;
            flightEntryLedgerActionIds = CollectLedgerActionIds(Ledger.Actions);
            if (freshLaunch)
                prelaunchEntryLedgerActionIds = new HashSet<string>(flightEntryLedgerActionIds, StringComparer.Ordinal);
            ParsekLog.Verbose("Scenario",
                "Flight entry: remembered " +
                flightEntryLedgerActionIds.Count.ToString(CultureInfo.InvariantCulture) +
                " ledger row(s) as the revert target's rows (reason=" + (reason ?? "unspecified") +
                ", freshLaunch=" + freshLaunch + ")");
        }

        /// <summary>
        /// The revert prune: removes the untagged rows after the launch boundary that the
        /// reverted flight wrote, keeping every row its revert target already held (O2):
        /// the prelaunch set for Revert-to-editor, the launch set for Revert-to-Launch. With no
        /// remembered target (never in normal play) it falls back to removing every untagged
        /// row after the boundary, with a Warn.
        /// </summary>
        internal static int PruneRevertedFlightLedgerRows(
            double cutoffUT, bool inclusive, RevertKind revertKind)
        {
            HashSet<string> keep = revertKind == RevertKind.Prelaunch
                ? prelaunchEntryLedgerActionIds
                : flightEntryLedgerActionIds;
            if (keep == null)
            {
                ParsekLog.Warn("Scenario",
                    "Revert (" + revertKind + "): no revert-target ledger rows remembered; pruning every untagged row " +
                    (inclusive ? "at/after" : "after") + " UT " +
                    cutoffUT.ToString("R", CultureInfo.InvariantCulture));
            }
            return Ledger.PruneOrphanActionsAfterUT(cutoffUT, inclusive, keep);
        }
    }
}
