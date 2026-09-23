using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Captures what a KSC building repair cost, and batches the ledger writes of one
    /// facility repair so its per-building rows reconcile against stock's single funds debit.
    ///
    /// <para>Stock mechanics (decompiled KSP 1.12.5): <c>SpaceCenterBuilding.RepairFacility(bool
    /// deduceFunds)</c> is the only funded repair entry point (the KSC context menu passes
    /// <c>Funding.Instance != null</c>). It checks affordability, calls
    /// <c>Funding.Instance.AddFunds(-|GetRepairsCost()|, TransactionReasons.StructureRepair)</c>,
    /// and only THEN runs <c>RepairStructures()</c>, which calls <c>DestructibleBuilding.Repair()</c>
    /// on every building of the facility. <c>Repair()</c> sets <c>destroyed = false</c> and
    /// fires <c>GameEvents.OnKSCStructureRepairing</c> synchronously for each building that was
    /// destroyed (an intact or already-repairing one returns early with no event).
    /// <c>GetRepairsCost()</c> is the sum of <c>RepairCost</c> over the destroyed buildings,
    /// times <c>Career.FundsLossMultiplier</c>. Neither <c>Repair()</c> nor the event carries
    /// the cost, so the Harmony scope on <c>RepairFacility</c> computes each building's share
    /// the same way stock computes the total.</para>
    ///
    /// <para>The FundsChanged(StructureRepair) event is recorded by the ordinary funds handler
    /// but never converted into a ledger row (<c>GameStateEventConverter</c> drops every
    /// FundsChanged reason except the strategy exchange carve-outs), so the repair row's
    /// <see cref="GameAction.FacilityCost"/> is the ONLY place the ledger carries this spend -
    /// no double count.</para>
    /// </summary>
    internal static class FacilityRepairCapture
    {
        private const string Tag = "GameStateRecorder";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>One building of the facility being repaired, as stock sees it before the repair.</summary>
        internal struct BuildingRepairInput
        {
            public string BuildingId;
            public float RepairCost;
            public bool IsDestroyed;
        }

        private static bool scopeActive;
        private static string scopeFacility;
        private static Dictionary<string, float> scopeCostShares;
        private static readonly List<GameStateEvent> scopePendingForward = new List<GameStateEvent>();

        /// <summary>True while a <c>SpaceCenterBuilding.RepairFacility</c> call is on the stack.</summary>
        internal static bool IsScopeActive => scopeActive;

        /// <summary>
        /// Raised by the <c>SpaceCenterBuilding.ResetStructures</c> postfix with the ids of the
        /// buildings that were destroyed before the reset and are intact after it. Stock calls
        /// ResetStructures from UpgradeFacility / DowngradeFacility, so upgrading a destroyed
        /// facility repairs its buildings for free and fires no repair event.
        /// </summary>
        internal static event Action<IList<string>> StructuresReset;

        // ================================================================
        // Pure decisions
        // ================================================================

        /// <summary>
        /// Pure: each destroyed building's funds share of one repair, mirroring stock's
        /// <c>GetRepairsCost</c> (RepairCost of every destroyed building, times the career's
        /// funds-loss multiplier). Empty when the repair deducts no funds (Science mode,
        /// <c>deduceFunds=false</c>), so every repair row of such a call carries cost 0.
        /// A negative multiplier or cost is treated as 0: stock deducts <c>Math.Abs</c> of the
        /// total, and a negative share would read as a refund.
        /// </summary>
        internal static Dictionary<string, float> BuildRepairCostShares(
            IEnumerable<BuildingRepairInput> buildings,
            float fundsLossMultiplier,
            bool fundsCharged)
        {
            var shares = new Dictionary<string, float>(StringComparer.Ordinal);
            if (!fundsCharged || buildings == null)
                return shares;

            float multiplier = fundsLossMultiplier > 0f ? fundsLossMultiplier : 0f;
            foreach (var b in buildings)
            {
                if (!b.IsDestroyed || string.IsNullOrEmpty(b.BuildingId))
                    continue;
                float cost = b.RepairCost > 0f ? b.RepairCost * multiplier : 0f;
                float existing;
                shares.TryGetValue(b.BuildingId, out existing);
                shares[b.BuildingId] = existing + cost;
            }
            return shares;
        }

        /// <summary>Pure: the share for <paramref name="buildingId"/>, or 0 when it has none.</summary>
        internal static float ResolveRepairCost(
            IReadOnlyDictionary<string, float> shares, string buildingId)
        {
            if (shares == null || string.IsNullOrEmpty(buildingId))
                return 0f;
            float cost;
            return shares.TryGetValue(buildingId, out cost) ? cost : 0f;
        }

        /// <summary>
        /// Pure: the buildings a <c>ResetStructures</c> call repairs - the ones destroyed before
        /// it (Reset makes every building intact). A building mid-repair already reported its
        /// repair through <c>OnKSCStructureRepairing</c>, so only <c>IsDestroyed</c> counts.
        /// </summary>
        internal static List<string> SelectResetRepairedBuildings(IEnumerable<BuildingRepairInput> buildings)
        {
            var ids = new List<string>();
            if (buildings == null)
                return ids;
            foreach (var b in buildings)
            {
                if (b.IsDestroyed && !string.IsNullOrEmpty(b.BuildingId) && !ids.Contains(b.BuildingId))
                    ids.Add(b.BuildingId);
            }
            return ids;
        }

        /// <summary>Pure: the BuildingRepaired event detail read by <c>ConvertBuildingRepaired</c>.</summary>
        internal static string BuildRepairDetail(float cost)
        {
            return "cost=" + cost.ToString("R", IC);
        }

        // ================================================================
        // Scope (driven by the RepairFacility Harmony patch)
        // ================================================================

        /// <summary>
        /// Opens the repair scope. A scope left open by an interrupted call is flushed first,
        /// so no captured row is ever lost.
        /// </summary>
        internal static void BeginScope(string facility, Dictionary<string, float> costShares)
        {
            if (scopeActive)
            {
                ParsekLog.Warn(Tag,
                    $"FacilityRepair scope: '{scopeFacility ?? "(null)"}' was still open when " +
                    $"'{facility ?? "(null)"}' began - flushing it first");
                EndScope("reentered");
            }

            scopeActive = true;
            scopeFacility = facility;
            scopeCostShares = costShares ?? new Dictionary<string, float>(StringComparer.Ordinal);
            scopePendingForward.Clear();

            float total = 0f;
            foreach (var kvp in scopeCostShares)
                total += kvp.Value;
            ParsekLog.Verbose(Tag,
                $"FacilityRepair scope open: facility='{facility ?? "(null)"}' " +
                $"destroyedBuildings={scopeCostShares.Count.ToString(IC)} " +
                $"totalCost={total.ToString("R", IC)}");
        }

        /// <summary>The funds share of <paramref name="buildingId"/> in the open scope, 0 outside one.</summary>
        internal static float CostForBuilding(string buildingId)
        {
            return scopeActive ? ResolveRepairCost(scopeCostShares, buildingId) : 0f;
        }

        /// <summary>
        /// Queues a direct-ledger repair event for the batch write at scope end. Returns false
        /// (the caller forwards it immediately) when no scope is open.
        /// </summary>
        internal static bool TryDeferForward(GameStateEvent evt)
        {
            if (!scopeActive)
                return false;
            scopePendingForward.Add(evt);
            return true;
        }

        /// <summary>
        /// Closes the scope and writes every queued repair row in one batch: all rows reach the
        /// ledger before any is reconciled, so each row's reconcile sums the whole facility's
        /// repair against the single FundsChanged(StructureRepair) debit, and one recalc runs.
        /// </summary>
        internal static void EndScope(string reason)
        {
            if (!scopeActive)
                return;

            var pending = new List<GameStateEvent>(scopePendingForward);
            string facility = scopeFacility;
            scopeActive = false;
            scopeFacility = null;
            scopeCostShares = null;
            scopePendingForward.Clear();

            ParsekLog.Verbose(Tag,
                $"FacilityRepair scope close: facility='{facility ?? "(null)"}' " +
                $"reason={reason ?? "(none)"} rowsToWrite={pending.Count.ToString(IC)}");

            if (pending.Count > 0)
                LedgerOrchestrator.OnKscSpendingBatch(pending, "facility-repair");
        }

        /// <summary>Raises <see cref="StructuresReset"/> (called by the ResetStructures postfix).</summary>
        internal static void ReportStructuresReset(IList<string> repairedBuildingIds)
        {
            if (repairedBuildingIds == null || repairedBuildingIds.Count == 0)
                return;
            var handler = StructuresReset;
            if (handler == null)
            {
                ParsekLog.Verbose(Tag,
                    $"ResetStructures repaired {repairedBuildingIds.Count.ToString(IC)} building(s) " +
                    "with no subscribed recorder - not captured");
                return;
            }
            handler(repairedBuildingIds);
        }

        internal static void ResetForTesting()
        {
            scopeActive = false;
            scopeFacility = null;
            scopeCostShares = null;
            scopePendingForward.Clear();
            StructuresReset = null;
        }
    }
}
