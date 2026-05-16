using System;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Computes the funds cost of a single KSC dispatch from the recorded
    /// vessel snapshot. Pure aside from one diagnostic <see cref="ParsekLog.Warn"/>
    /// emitted when a part name has no known cost — both lookup callbacks are
    /// injected so tests can supply deterministic prices.
    /// </summary>
    internal static class RouteFundsCalculator
    {
        /// <summary>
        /// Walk every <c>PART</c> node summing
        /// <c>partCostLookup(name) + Σ RESOURCE.amount * resourceUnitCostLookup(name)</c>.
        /// Returns 0 when the snapshot is null or empty.
        /// </summary>
        /// <param name="vesselSnapshot">
        /// ConfigNode whose <c>PART</c> children describe the transport vessel
        /// (matches the snapshot layout used by recordings + RouteOriginProof).
        /// </param>
        /// <param name="partCostLookup">
        /// <c>partName -> stock cost</c>. Tests inject a deterministic dictionary;
        /// production hands in a <see cref="PartLoader"/>-backed delegate.
        /// </param>
        /// <param name="resourceUnitCostLookup">
        /// <c>resourceName -> unit cost</c>. Tests inject a deterministic dictionary;
        /// production hands in a <see cref="PartResourceLibrary"/>-backed delegate.
        /// </param>
        internal static double ComputeDispatchFundsCost(
            ConfigNode vesselSnapshot,
            Func<string, float> partCostLookup,
            Func<string, float> resourceUnitCostLookup)
        {
            if (vesselSnapshot == null) return 0.0;

            ConfigNode[] parts = vesselSnapshot.GetNodes("PART");
            if (parts == null || parts.Length == 0) return 0.0;

            double total = 0.0;
            for (int i = 0; i < parts.Length; i++)
            {
                ConfigNode partNode = parts[i];
                if (partNode == null) continue;

                string partName = partNode.GetValue("name") ?? partNode.GetValue("part");
                if (string.IsNullOrEmpty(partName)) continue;

                float partCost = partCostLookup != null ? partCostLookup(partName) : 0f;
                if (partCost == 0f)
                {
                    ParsekLog.Warn(RouteOrchestrator.Tag,
                        $"Unknown part cost: name={partName}; treating as 0");
                }
                total += partCost;

                ConfigNode[] resources = partNode.GetNodes("RESOURCE");
                if (resources == null) continue;

                for (int j = 0; j < resources.Length; j++)
                {
                    ConfigNode resNode = resources[j];
                    if (resNode == null) continue;

                    string resName = resNode.GetValue("name");
                    if (string.IsNullOrEmpty(resName)) continue;

                    string amountStr = resNode.GetValue("amount");
                    if (string.IsNullOrEmpty(amountStr)) continue;

                    if (!double.TryParse(
                            amountStr,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double amount))
                    {
                        continue;
                    }

                    float unitCost = resourceUnitCostLookup != null ? resourceUnitCostLookup(resName) : 0f;
                    total += amount * unitCost;
                }
            }

            return total;
        }
    }
}
