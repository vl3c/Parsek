using System;
using System.Collections.Generic;
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
        /// M2 funds-basis overload (plan D9 / OQ1): parts term from the
        /// snapshot walk as before, RESOURCE term from the full-run START
        /// transport manifest when one is supplied - the player pays for what
        /// KSC supplied at launch, so harvested cargo aboard at dock is never
        /// billed and fuel burned in transit is no longer free. A null
        /// <paramref name="startResourceManifest"/> falls back to the legacy
        /// three-argument overload unchanged (byte-identical, pinned by
        /// <c>ComputeDispatchFundsCost_NullManifest_FallsBackToSnapshotWalk</c>),
        /// which contains the change to routes built from NEW recordings.
        /// </summary>
        internal static double ComputeDispatchFundsCost(
            ConfigNode vesselSnapshot,
            Dictionary<string, ResourceAmount> startResourceManifest,
            Func<string, float> partCostLookup,
            Func<string, float> resourceUnitCostLookup)
        {
            return ComputeDispatchFundsCost(
                vesselSnapshot, startResourceManifest, null, partCostLookup, resourceUnitCostLookup);
        }

        /// <summary>
        /// PID-restricted funds-basis overload. Identical to the four-argument
        /// overload except that, when <paramref name="restrictToPartPersistentIds"/>
        /// is non-null, only <c>PART</c> nodes whose <c>persistentId</c> is IN that
        /// set are priced (parts AND, on the legacy resource basis, their
        /// <c>RESOURCE</c> amounts). A null set prices everything, so both existing
        /// bases route through here unchanged.
        ///
        /// <para>The restriction exists for one caller: pricing a dispatch off a
        /// dock-MERGED (transport + endpoint) snapshot when no un-merged snapshot
        /// survives anywhere in the route's member set. Restricting to the
        /// connection window's TRANSPORT pid set prices exactly the launch vehicle
        /// and never the destination station.</para>
        ///
        /// <para>Fail-closed by construction: under a restriction a <c>PART</c> with
        /// a missing or unparseable <c>persistentId</c> is EXCLUDED, so an
        /// unidentifiable endpoint part can never leak into the bill.</para>
        /// </summary>
        internal static double ComputeDispatchFundsCost(
            ConfigNode vesselSnapshot,
            Dictionary<string, ResourceAmount> startResourceManifest,
            HashSet<uint> restrictToPartPersistentIds,
            Func<string, float> partCostLookup,
            Func<string, float> resourceUnitCostLookup)
        {
            if (startResourceManifest == null)
            {
                // Legacy basis: per-PART resource walk over the stop snapshot.
                return ComputeSnapshotWalk(
                    vesselSnapshot, restrictToPartPersistentIds,
                    partCostLookup, resourceUnitCostLookup);
            }

            if (vesselSnapshot == null) return 0.0;

            ConfigNode[] parts = vesselSnapshot.GetNodes("PART");
            if (parts == null || parts.Length == 0) return 0.0;

            double total = 0.0;
            for (int i = 0; i < parts.Length; i++)
            {
                ConfigNode partNode = parts[i];
                if (partNode == null) continue;
                if (IsRestrictedOut(partNode, restrictToPartPersistentIds)) continue;

                string partName = partNode.GetValue("name") ?? partNode.GetValue("part");
                if (string.IsNullOrEmpty(partName)) continue;

                float partCost = partCostLookup != null ? partCostLookup(partName) : 0f;
                if (partCost == 0f)
                {
                    ParsekLog.Warn(RouteOrchestrator.Tag,
                        $"Unknown part cost: name={partName}; treating as 0");
                }
                total += partCost;
            }

            foreach (KeyValuePair<string, ResourceAmount> kvp in startResourceManifest)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                float unitCost = resourceUnitCostLookup != null ? resourceUnitCostLookup(kvp.Key) : 0f;
                total += kvp.Value.amount * unitCost;
            }

            return total;
        }

        /// <summary>
        /// True when <paramref name="restrict"/> is non-null and this <c>PART</c>
        /// node is NOT in it. Fail-closed: a part carrying no parseable
        /// <c>persistentId</c> is excluded under any restriction.
        /// </summary>
        private static bool IsRestrictedOut(ConfigNode partNode, HashSet<uint> restrict)
        {
            if (restrict == null) return false;

            string pidStr = partNode.GetValue("persistentId");
            if (string.IsNullOrEmpty(pidStr)) return true;
            if (!uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pid))
                return true;
            return !restrict.Contains(pid);
        }

        /// <summary>
        /// Counts how many <c>PART</c> nodes a restriction keeps, out of how many
        /// the snapshot holds - the <c>parts=n/total</c> term on the diagnostic
        /// <c>FundsCost basis=</c> line. Shares <see cref="IsRestrictedOut"/> with
        /// the walk so the count can never disagree with what was priced.
        /// </summary>
        internal static void CountRestrictedParts(
            ConfigNode vesselSnapshot, HashSet<uint> restrict, out int priced, out int total)
        {
            priced = 0;
            total = 0;
            if (vesselSnapshot == null) return;

            ConfigNode[] parts = vesselSnapshot.GetNodes("PART");
            if (parts == null) return;

            for (int i = 0; i < parts.Length; i++)
            {
                ConfigNode partNode = parts[i];
                if (partNode == null) continue;
                total++;
                if (!IsRestrictedOut(partNode, restrict)) priced++;
            }
        }

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
            return ComputeSnapshotWalk(vesselSnapshot, null, partCostLookup, resourceUnitCostLookup);
        }

        /// <summary>
        /// The legacy per-PART walk, optionally restricted to a
        /// <c>persistentId</c> set. <c>restrict == null</c> is the historical
        /// behavior verbatim.
        /// </summary>
        private static double ComputeSnapshotWalk(
            ConfigNode vesselSnapshot,
            HashSet<uint> restrict,
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
                if (IsRestrictedOut(partNode, restrict)) continue;

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
