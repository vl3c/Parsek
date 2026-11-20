using System;
using System.Collections.Generic;
using System.Reflection;
using Contracts;
using Contracts.Parameters;
using Contracts.Templates;

namespace Parsek
{
    // Contract rebuild: the stock accept-side world effects Contract.Load does not run.
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

        private const BindingFlags RecoverAssetPrivate = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>
        /// Todo CONTRACT-REBUILD-SKIPS-ACCEPT-WORLD-EFFECTS. For one contract the patch just
        /// rebuilt from its snapshot, logs the stock accept-side world effect the rebuild did
        /// not run and whether the world lacks it, then repairs it where the pure plan says
        /// so: a RecoverAsset's stranded vessel through its own stock generator, a
        /// TourismContract's tourists through <c>OnAccepted</c>'s roster change. Returns 1
        /// when the world lacked the effect, else 0; a failed check logs and returns 0.
        /// </summary>
        private static int ReportRestoredContractAcceptWorldEffect(
            string contractId, string typeName, ConfigNode snapshot, Contract contract,
            ref ContractAcceptWorldIndex world, ref int repaired)
        {
            if (ContractAcceptWorldEffects.Classify(typeName) == ContractAcceptWorldEffect.None)
                return 0;
            try
            {
                return ReportRestoredContractAcceptWorldEffectCore(
                    contractId, typeName, snapshot, contract, ref world, ref repaired) ? 1 : 0;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"PatchContracts: accept-side world effect check for '{contractId}' threw: {ex.Message}");
                return 0;
            }
        }

        private static void LogRestoredContractAcceptWorldEffectsSummary(int missing, int repaired)
        {
            if (missing <= 0)
                return;
            ParsekLog.Info(Tag,
                $"PatchContracts: {missing.ToString(IC)} rebuilt contract(s) name world objects " +
                $"their stock accept created and this world lacked; repaired={repaired.ToString(IC)} " +
                "(todo CONTRACT-REBUILD-SKIPS-ACCEPT-WORLD-EFFECTS)");
        }

        private static bool ReportRestoredContractAcceptWorldEffectCore(
            string contractId, string typeName, ConfigNode snapshot, Contract contract,
            ref ContractAcceptWorldIndex world, ref int repaired)
        {
            if (world == null)
                world = BuildContractAcceptWorldIndex();
            ContractAcceptWorldIndex w = world;
            KerbalRoster roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            KerbalsModule kerbals = LedgerOrchestrator.Kerbals;
            Func<string, ProtoCrewMember> lookup =
                name => roster != null && !string.IsNullOrEmpty(name) ? roster[name] : null;
            var facts = new StrandedAssetWorldFacts
            {
                PartInAnyVessel = id => w.PartFlightIds.Contains(id),
                KerbalAboardAnyVessel = name => !string.IsNullOrEmpty(name) && w.CrewAboard.Contains(name),
                KerbalInRoster = name => lookup(name) != null,
                KerbalUnowned = name =>
                {
                    ProtoCrewMember pcm = lookup(name);
                    return pcm != null && pcm.type == ProtoCrewMember.KerbalType.Unowned;
                },
                KerbalReserved = name => IsKerbalHeldFromAcceptEffect(kerbals, name),
                CommittedResolution = HasCommittedContractResolution(CommittedFutureIndexCache.Current, contractId)
            };
            GameScenes scene = HighLogic.LoadedScene;
            ContractAcceptWorldEffectPlan plan = ContractAcceptWorldEffects.Plan(
                typeName, snapshot, facts,
                name =>
                {
                    ProtoCrewMember pcm = lookup(name);
                    return new TouristRosterFact
                    {
                        InRoster = pcm != null,
                        IsTourist = pcm != null && pcm.type == ProtoCrewMember.KerbalType.Tourist,
                        IsUnowned = pcm != null && pcm.type == ProtoCrewMember.KerbalType.Unowned,
                        Reserved = pcm != null && IsKerbalHeldFromAcceptEffect(kerbals, name)
                    };
                },
                scene);
            ParsekLog.Info(Tag,
                $"PatchContracts: accept-side world effect not run on rebuild: contractId='{contractId}' " +
                $"type='{typeName}' effect={plan.Effect} outcome={plan.Outcome} " +
                $"worldMissing={plan.WorldEffectMissing.ToString(IC)} repair={plan.Repair} " +
                $"skip={plan.RepairSkipReason ?? "none"} ({plan.Detail}; " +
                $"vessels={w.VesselCount.ToString(IC)} scene={scene})");

            switch (plan.Repair)
            {
                case ContractAcceptRepair.RegenerateStrandedAsset:
                    if (RegenerateRebuiltStrandedAsset(contractId, contract, plan.Target))
                    {
                        repaired++;
                        // The new vessel is in flightState now; the next contract re-reads it.
                        world = null;
                    }
                    break;
                case ContractAcceptRepair.ConvertTourists:
                    if (ConvertRebuiltContractTourists(contractId, lookup, plan.Tourists) > 0)
                        repaired++;
                    break;
            }
            return plan.WorldEffectMissing;
        }

        /// <summary>
        /// A kerbal no accept-side repair may touch: Parsek manages him (reserved now, a
        /// stand-in, retired), a committed flight names him, or he is on either side of the
        /// crew swap map. That flight or that bookkeeping owns where he is.
        /// </summary>
        private static bool IsKerbalHeldFromAcceptEffect(KerbalsModule kerbals, string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (kerbals != null && kerbals.ShouldBlockDismissal(name))
                return true;
            IReadOnlyDictionary<string, string> swaps = CrewReservationManager.CrewReplacements;
            if (swaps == null)
                return false;
            if (swaps.ContainsKey(name))
                return true;
            foreach (KeyValuePair<string, string> kv in swaps)
            {
                if (string.Equals(kv.Value, name, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Runs a rebuilt RecoverAsset's own stock generator once (the half of
        /// <c>RecoverAsset.OnAccepted</c> that writes into the game: <c>GenerateLandRecovery</c>
        /// for a surface rescue, else <c>GenerateOrbitalRecovery</c>; both end in
        /// <c>Game.AddVessel</c>). The other half, the parameters' kerbal and part lists, the
        /// snapshot already carries; a part id the generator re-draws is swapped in them.
        /// Returns true when stock added a vessel.
        /// </summary>
        private static bool RegenerateRebuiltStrandedAsset(
            string contractId, Contract contract, RecoverAssetTarget target)
        {
            var rescue = contract as RecoverAsset;
            if (rescue == null)
            {
                ParsekLog.Warn(Tag,
                    $"PatchContracts: stranded asset not regenerated for '{contractId}': the rebuilt " +
                    $"contract is {(contract != null ? contract.GetType().Name : "null")}, not RecoverAsset");
                return false;
            }
            FlightState flightState = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.flightState : null;
            if (flightState == null || flightState.protoVessels == null)
            {
                ParsekLog.Warn(Tag,
                    $"PatchContracts: stranded asset not regenerated for '{contractId}': no flightState");
                return false;
            }
            Type t = typeof(RecoverAsset);
            string generatorName = target.Surface ? "GenerateLandRecovery" : "GenerateOrbitalRecovery";
            MethodInfo generator = t.GetMethod(generatorName, RecoverAssetPrivate, null, Type.EmptyTypes, null);
            FieldInfo partIdField = t.GetField("partID", RecoverAssetPrivate);
            FieldInfo kerbalField = t.GetField("recoveryKerbal", RecoverAssetPrivate);
            if (generator == null || partIdField == null || kerbalField == null)
            {
                ParsekLog.Warn(Tag,
                    $"PatchContracts: stranded asset not regenerated for '{contractId}': RecoverAsset member " +
                    $"missing (generator={(generator != null)} partID={(partIdField != null)} " +
                    $"recoveryKerbal={(kerbalField != null)})");
                return false;
            }
            if (target.RecoveringKerbal && kerbalField.GetValue(rescue) == null)
            {
                // RecoverAsset.OnLoad resolves the kerbal from the roster; the generators'
                // vessel name dereferences him.
                ParsekLog.Warn(Tag,
                    $"PatchContracts: stranded asset not regenerated for '{contractId}': the rebuilt " +
                    $"contract resolved no kerbal for '{target.KerbalName}'");
                return false;
            }

            uint partIdBefore = (uint)partIdField.GetValue(rescue);
            int vesselsBefore = flightState.protoVessels.Count;
            try
            {
                using (SuppressionGuard.Crew())
                    generator.Invoke(rescue, null);
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                ParsekLog.Warn(Tag,
                    $"PatchContracts: RecoverAsset.{generatorName} threw for rebuilt contract '{contractId}' " +
                    $"({inner.GetType().Name}: {inner.Message}); vessels added before the throw=" +
                    $"{(flightState.protoVessels.Count - vesselsBefore).ToString(IC)}");
                return flightState.protoVessels.Count > vesselsBefore;
            }
            uint partIdAfter = (uint)partIdField.GetValue(rescue);
            int added = flightState.protoVessels.Count - vesselsBefore;

            var names = new List<string>();
            for (int i = vesselsBefore; i < flightState.protoVessels.Count; i++)
            {
                ProtoVessel pv = flightState.protoVessels[i];
                if (pv != null)
                    names.Add("'" + pv.vesselName + "' pid=" + pv.persistentId.ToString(IC));
            }
            ParsekLog.Info(Tag,
                $"PatchContracts: regenerated stranded asset for rebuilt contract '{contractId}' through " +
                $"RecoverAsset.{generatorName}: added={added.ToString(IC)} " +
                $"[{string.Join(", ", names.ToArray())}] ({ContractAcceptWorldEffects.DescribeTarget(target)}; " +
                $"partID {partIdBefore.ToString(IC)} -> {partIdAfter.ToString(IC)})");

            if (ContractAcceptWorldEffects.NeedsPartListRepair(target.RecoveringPart, partIdBefore, partIdAfter))
                RepairRebuiltRescuePartLists(contractId, rescue, partIdBefore, partIdAfter);
            return added > 0;
        }

        /// <summary>The rebuilt AcquirePart / RecoverPart still list the snapshot's part id;
        /// point them at the part the generator just made.</summary>
        private static void RepairRebuiltRescuePartLists(
            string contractId, RecoverAsset rescue, uint oldId, uint newId)
        {
            AcquirePart acquire = rescue.GetParameter<AcquirePart>();
            RecoverPart recover = rescue.GetParameter<RecoverPart>();
            if (acquire != null)
            {
                acquire.RemovePart(oldId);
                acquire.AddPart(newId);
            }
            if (recover != null)
            {
                recover.RemovePartToRecover(oldId);
                recover.AddPartToRecover(newId);
            }
            ParsekLog.Info(Tag,
                $"PatchContracts: rebuilt rescue '{contractId}' part lists now name partID={newId.ToString(IC)} " +
                $"(was {oldId.ToString(IC)}; acquirePart={(acquire != null)} recoverPart={(recover != null)})");
        }

        /// <summary>
        /// <c>TourismContract.OnAccepted</c>'s roster change (decompiled: type Tourist, the
        /// tourist trait, Available) for each listed tourist whose decision is Convert.
        /// Returns how many it converted. Crew events are suppressed: this replays an accept
        /// the first timeline already recorded.
        /// </summary>
        private static int ConvertRebuiltContractTourists(
            string contractId, Func<string, ProtoCrewMember> lookup,
            List<KeyValuePair<string, TouristConversion>> tourists)
        {
            if (tourists == null)
                return 0;
            int converted = 0;
            using (SuppressionGuard.Crew())
            {
                for (int i = 0; i < tourists.Count; i++)
                {
                    string name = tourists[i].Key;
                    if (tourists[i].Value != TouristConversion.Convert)
                    {
                        ParsekLog.Verbose(Tag,
                            $"PatchContracts: tourist '{name}' of rebuilt contract '{contractId}' left as is: " +
                            $"{tourists[i].Value}");
                        continue;
                    }
                    ProtoCrewMember pcm = lookup(name);
                    if (pcm == null)
                        continue;
                    ProtoCrewMember.RosterStatus fromStatus = pcm.rosterStatus;
                    pcm.type = ProtoCrewMember.KerbalType.Tourist;
                    KerbalRoster.SetExperienceTrait(pcm, KerbalRoster.touristTrait);
                    pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                    converted++;
                    ParsekLog.Info(Tag,
                        $"PatchContracts: tourist '{name}' of rebuilt contract '{contractId}' converted the way " +
                        $"TourismContract.OnAccepted does: Unowned -> Tourist, {fromStatus} -> Available");
                }
            }
            return converted;
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
