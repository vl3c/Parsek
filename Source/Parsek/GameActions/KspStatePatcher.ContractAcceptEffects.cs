using System;
using System.Collections.Generic;

namespace Parsek
{
    // Contract rebuild: observation of stock accept-side world effects the rebuild does not run.
    internal static partial class KspStatePatcher
    {
        /// <summary>
        /// The live world facts a restored contract's accept-side check reads, built once per
        /// patch and only when a restored contract's template has an accept-side world effect.
        /// </summary>
        private sealed class ContractAcceptWorldIndex
        {
            internal readonly HashSet<uint> PartFlightIds = new HashSet<uint>();
            internal readonly HashSet<string> CrewAboard = new HashSet<string>(StringComparer.Ordinal);
            internal int VesselCount;
        }

        /// <summary>
        /// Todo CONTRACT-REBUILD-SKIPS-ACCEPT-WORLD-EFFECTS. Observation only: logs, for one
        /// contract the patch just rebuilt from its snapshot, the stock accept-side world
        /// effect the rebuild did not run and whether the world lacks it. Changes nothing.
        /// Returns 1 when the world lacks it, else 0; a failed check logs and returns 0.
        /// </summary>
        private static int ReportRestoredContractAcceptWorldEffect(
            string contractId, string typeName, ConfigNode snapshot, ref ContractAcceptWorldIndex world)
        {
            if (ContractAcceptWorldEffects.Classify(typeName) == ContractAcceptWorldEffect.None)
                return 0;
            try
            {
                return ReportRestoredContractAcceptWorldEffectCore(contractId, typeName, snapshot, ref world) ? 1 : 0;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"PatchContracts: accept-side world effect check for '{contractId}' threw: {ex.Message}");
                return 0;
            }
        }

        private static void LogRestoredContractAcceptWorldEffectsSummary(int missing)
        {
            if (missing <= 0)
                return;
            ParsekLog.Info(Tag,
                $"PatchContracts: {missing.ToString(IC)} rebuilt contract(s) name world objects " +
                "their stock accept created and this world lacks (todo CONTRACT-REBUILD-SKIPS-ACCEPT-WORLD-EFFECTS)");
        }

        private static bool ReportRestoredContractAcceptWorldEffectCore(
            string contractId, string typeName, ConfigNode snapshot, ref ContractAcceptWorldIndex world)
        {
            if (world == null)
                world = BuildContractAcceptWorldIndex();
            ContractAcceptWorldIndex w = world;
            KerbalRoster roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            KerbalsModule kerbals = LedgerOrchestrator.Kerbals;
            var facts = new StrandedAssetWorldFacts
            {
                PartInAnyVessel = id => w.PartFlightIds.Contains(id),
                KerbalAboardAnyVessel = name => !string.IsNullOrEmpty(name) && w.CrewAboard.Contains(name),
                KerbalInRoster = name => roster != null && !string.IsNullOrEmpty(name) && roster[name] != null,
                KerbalNamedByCommittedFlight = name => kerbals != null && kerbals.IsNamedByCommittedFlight(name),
                CommittedResolution = HasCommittedContractResolution(CommittedFutureIndexCache.Current, contractId)
            };
            ContractAcceptWorldEffectPlan plan = ContractAcceptWorldEffects.Plan(
                typeName, snapshot, facts,
                name =>
                {
                    ProtoCrewMember pcm = roster != null && !string.IsNullOrEmpty(name) ? roster[name] : null;
                    return pcm != null && pcm.type == ProtoCrewMember.KerbalType.Tourist;
                });
            ParsekLog.Info(Tag,
                $"PatchContracts: accept-side world effect not run on rebuild: contractId='{contractId}' " +
                $"type='{typeName}' effect={plan.Effect} outcome={plan.Outcome} " +
                $"worldMissing={plan.WorldEffectMissing.ToString(IC)} ({plan.Detail}; " +
                $"vessels={w.VesselCount.ToString(IC)})");
            return plan.WorldEffectMissing;
        }

        /// <summary>A committed ContractComplete / Fail / Cancel row of this contract at any UT
        /// (the kinds <c>StockUiReservationPredicates.CommittedContractResolution</c> reads).</summary>
        private static bool HasCommittedContractResolution(CommittedFutureIndex index, string contractId)
        {
            if (index == null || string.IsNullOrEmpty(contractId))
                return false;
            return index.FirstFuture(CommittedFutureKind.ContractComplete, contractId, double.NegativeInfinity) != null
                || index.FirstFuture(CommittedFutureKind.ContractFail, contractId, double.NegativeInfinity) != null
                || index.FirstFuture(CommittedFutureKind.ContractCancel, contractId, double.NegativeInfinity) != null;
        }

        private static ContractAcceptWorldIndex BuildContractAcceptWorldIndex()
        {
            var index = new ContractAcceptWorldIndex();
            FlightState flightState = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.flightState : null;
            if (flightState == null || flightState.protoVessels == null)
                return index;
            for (int v = 0; v < flightState.protoVessels.Count; v++)
            {
                ProtoVessel pv = flightState.protoVessels[v];
                if (pv == null || pv.protoPartSnapshots == null)
                    continue;
                index.VesselCount++;
                for (int p = 0; p < pv.protoPartSnapshots.Count; p++)
                {
                    ProtoPartSnapshot pps = pv.protoPartSnapshots[p];
                    if (pps == null)
                        continue;
                    index.PartFlightIds.Add(pps.flightID);
                    if (pps.protoModuleCrew == null)
                        continue;
                    for (int c = 0; c < pps.protoModuleCrew.Count; c++)
                    {
                        ProtoCrewMember crew = pps.protoModuleCrew[c];
                        if (crew != null && !string.IsNullOrEmpty(crew.name))
                            index.CrewAboard.Add(crew.name);
                    }
                }
            }
            return index;
        }
    }
}
