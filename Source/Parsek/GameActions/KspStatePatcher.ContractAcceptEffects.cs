using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Contracts;
using Contracts.Parameters;
using Contracts.Templates;

namespace Parsek
{
    // Contract rebuild: the stock accept-side world effects Contract.Load does not run.
    internal static partial class KspStatePatcher
    {
        private const BindingFlags RecoverAssetPrivate = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>The <c>onContractsLoaded</c> subscription that drains
        /// <see cref="RescueAcceptDeferrals"/>; added once, the first time the list is not empty.</summary>
        private static EventVoid.OnEvent rescueDeferralDrainHandler;

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
            KerbalsModule kerbals = LedgerOrchestrator.Kerbals;
            Func<string, ProtoCrewMember> lookup = BuildRosterLookup();
            StrandedAssetWorldFacts facts = BuildStrandedAssetWorldFacts(w, lookup, kerbals, contractId);
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

            // The rebuild decided this rescue again; an earlier deferral of it is spent.
            if (plan.Effect == ContractAcceptWorldEffect.StrandedAsset
                && plan.Repair != ContractAcceptRepair.DeferStrandedAsset)
                RescueAcceptDeferrals.Remove(contractId, "rebuilt again: outcome=" + plan.Outcome + " repair=" + plan.Repair);

            switch (plan.Repair)
            {
                case ContractAcceptRepair.RegenerateStrandedAsset:
                    if (RegenerateRebuiltStrandedAsset(contractId, contract, plan.Target))
                    {
                        repaired++;
                        // The new vessel is live (FlightGlobals) and not yet in flightState;
                        // the next contract's index re-reads both lists.
                        world = null;
                    }
                    break;
                case ContractAcceptRepair.DeferStrandedAsset:
                    // Owner ruling 2026-11-20: regenerated at the next Space Center or Tracking
                    // Station visit, under the same guards.
                    RescueAcceptDeferrals.Add(contractId, "rebuild scene=" + scene);
                    EnsureRescueDeferralDrainSubscribed();
                    break;
                case ContractAcceptRepair.ConvertTourists:
                    if (ConvertRebuiltContractTourists(contractId, lookup, plan.Tourists) > 0)
                        repaired++;
                    break;
            }
            return plan.WorldEffectMissing;
        }

        private static Func<string, ProtoCrewMember> BuildRosterLookup()
        {
            KerbalRoster roster = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.CrewRoster : null;
            return name => roster != null && !string.IsNullOrEmpty(name) ? roster[name] : null;
        }

        private static StrandedAssetWorldFacts BuildStrandedAssetWorldFacts(
            ContractAcceptWorldIndex w, Func<string, ProtoCrewMember> lookup, KerbalsModule kerbals,
            string contractId)
        {
            return new StrandedAssetWorldFacts
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
        }

        /// <summary>
        /// Subscribes the deferred-rescue drain to stock's <c>onContractsLoaded</c>, which stock
        /// fires once its ContractSystem has loaded a scene's contracts and the contract patch
        /// fires at its end. Idempotent; the subscription lives for the process (a no-op while
        /// the list is empty).
        /// </summary>
        internal static void EnsureRescueDeferralDrainSubscribed()
        {
            if (rescueDeferralDrainHandler != null)
                return;
            try
            {
                SubscribeRescueDeferralDrainCore();
                ParsekLog.Verbose(Tag,
                    "Rescue accept deferrals: drain subscribed to onContractsLoaded " +
                    $"(pending={RescueAcceptDeferrals.Count.ToString(IC)})");
            }
            catch (Exception ex)
            {
                rescueDeferralDrainHandler = null;
                ParsekLog.Warn(Tag,
                    $"Rescue accept deferrals: drain subscription failed ({ex.GetType().Name}: {ex.Message}); " +
                    $"pending={RescueAcceptDeferrals.Count.ToString(IC)} wait for the next subscription attempt");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SubscribeRescueDeferralDrainCore()
        {
            EventVoid.OnEvent handler = () => DrainRescueAcceptDeferrals("onContractsLoaded");
            GameEvents.Contract.onContractsLoaded.Add(handler);
            rescueDeferralDrainHandler = handler;
        }

        /// <summary>
        /// Owner ruling 2026-11-20 (todo CONTRACT-REBUILD-SKIPS-ACCEPT-WORLD-EFFECTS): regenerates
        /// the stranded vessel of every rescue a rebuild deferred, once the scene is the Space
        /// Center or the Tracking Station and stock has loaded its contracts. Each listed id is
        /// kept (another scene, a load or contract patch still running, no ledger yet), dropped
        /// (stock holds no Active RecoverAsset with it, or the ledger no longer holds it accepted
        /// by now) or evaluated: the live contract is re-planned with the rebuild's own guards
        /// (target present or half present, committed resolution, reserved kerbal, roster) and
        /// regenerated only on Regenerate. An evaluated id leaves the list whatever the outcome,
        /// so no contract is regenerated twice. Logs one line per evaluated or dropped id and
        /// one summary.
        /// </summary>
        internal static void DrainRescueAcceptDeferrals(string trigger)
        {
            if (RescueAcceptDeferrals.Count == 0)
                return;
            try
            {
                DrainRescueAcceptDeferralsCore(trigger);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Rescue accept deferrals: drain ({trigger}) threw {ex.GetType().Name}: {ex.Message}; " +
                    $"pending={RescueAcceptDeferrals.Count.ToString(IC)} kept");
            }
        }

        private static void DrainRescueAcceptDeferralsCore(string trigger)
        {
            GameScenes scene = HighLogic.LoadedScene;
            bool loadOrPatchPending = ParsekScenario.IsOnLoadInProgress || DeferredContractPatchState.HasPending;
            ContractSystem system = ContractSystem.Instance;
            List<Contract> current = system != null ? system.Contracts : null;
            ContractsModule ledger = LedgerOrchestrator.Contracts;
            double nowUT = ParsekScenario.GetCurrentTimelineUTForLedgerRecalc();

            var ids = new List<string>(RescueAcceptDeferrals.Pending);
            int kept = 0, dropped = 0, evaluated = 0, regenerated = 0;
            DeferredRescueDrainStep lastKeep = DeferredRescueDrainStep.Evaluate;
            ContractAcceptWorldIndex world = null;
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                Contract contract = FindContractById(current, id);
                bool activeRescue = contract is RecoverAsset && contract.ContractState == Contract.State.Active;
                bool? ledgerActive = null;
                if (ledger != null && current != null)
                    ledgerActive = IsLedgerActiveByNow(ledger, id, nowUT);
                DeferredRescueDrainStep step = ContractAcceptWorldEffects.DecideDeferredRescueDrainStep(
                    scene, loadOrPatchPending, activeRescue, ledgerActive);
                switch (step)
                {
                    case DeferredRescueDrainStep.KeepNotRegenerationScene:
                    case DeferredRescueDrainStep.KeepLoadOrPatchPending:
                    case DeferredRescueDrainStep.KeepLedgerNotReady:
                        kept++;
                        lastKeep = step;
                        continue;
                    case DeferredRescueDrainStep.DropContractNotActive:
                    case DeferredRescueDrainStep.DropNotLedgerActive:
                        dropped++;
                        RescueAcceptDeferrals.Remove(id, "drain " + step + " (stock state=" +
                            (contract != null ? contract.GetType().Name + "/" + contract.ContractState : "absent") + ")");
                        continue;
                }

                evaluated++;
                if (world == null)
                    world = BuildContractAcceptWorldIndex();
                if (EvaluateDeferredRescue(id, (RecoverAsset)contract, world, scene))
                {
                    regenerated++;
                    world = null;
                }
            }

            string summary =
                $"Rescue accept deferrals drained ({trigger}, scene={scene}): evaluated={evaluated.ToString(IC)} " +
                $"regenerated={regenerated.ToString(IC)} dropped={dropped.ToString(IC)} kept={kept.ToString(IC)}" +
                (kept > 0 ? " keepReason=" + lastKeep : "") +
                $" pending={RescueAcceptDeferrals.Count.ToString(IC)}";
            if (evaluated > 0 || dropped > 0)
                ParsekLog.Info(Tag, summary);
            else
                ParsekLog.Verbose(Tag, summary);
        }

        /// <summary>Re-plans one deferred rescue from its live contract, regenerates on
        /// Regenerate, and takes it off the list. Returns true when stock added a vessel.</summary>
        private static bool EvaluateDeferredRescue(
            string contractId, RecoverAsset contract, ContractAcceptWorldIndex world, GameScenes scene)
        {
            var live = new ConfigNode("CONTRACT");
            contract.Save(live);
            StrandedAssetWorldFacts facts = BuildStrandedAssetWorldFacts(
                world, BuildRosterLookup(), LedgerOrchestrator.Kerbals, contractId);
            ContractAcceptWorldEffectPlan plan = ContractAcceptWorldEffects.Plan(
                "RecoverAsset", live, facts, null, scene);
            ParsekLog.Info(Tag,
                $"Rescue accept deferral evaluated: contractId='{contractId}' outcome={plan.Outcome} " +
                $"repair={plan.Repair} skip={plan.RepairSkipReason ?? "none"} ({plan.Detail}; " +
                $"vessels={world.VesselCount.ToString(IC)} scene={scene})");
            bool added = plan.Repair == ContractAcceptRepair.RegenerateStrandedAsset
                && RegenerateRebuiltStrandedAsset(contractId, contract, plan.Target);
            RescueAcceptDeferrals.Remove(contractId,
                "drain evaluated: outcome=" + plan.Outcome + " regenerated=" + added);
            return added;
        }

        private static Contract FindContractById(List<Contract> contracts, string contractId)
        {
            if (contracts == null || string.IsNullOrEmpty(contractId))
                return null;
            for (int i = 0; i < contracts.Count; i++)
            {
                Contract c = contracts[i];
                if (c != null && string.Equals(c.ContractGuid.ToString(), contractId, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }

        /// <summary>The ledger holds the contract active and accepted by
        /// <paramref name="nowUT"/> (the contract patch's own accepted-by-now rule).</summary>
        private static bool IsLedgerActiveByNow(ContractsModule ledger, string contractId, double nowUT)
        {
            bool inLedger = false;
            foreach (string id in ledger.GetActiveContractIds())
            {
                if (string.Equals(id, contractId, StringComparison.OrdinalIgnoreCase))
                {
                    contractId = id;
                    inLedger = true;
                    break;
                }
            }
            if (!inLedger)
                return false;
            List<string> acceptedLater;
            return SelectContractsAcceptedByNow(
                new[] { contractId }, ledger.GetActiveContractAcceptUT, nowUT, out acceptedLater).Count == 1;
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
            // Game.AddVessel registers the vessel with FlightGlobals and leaves
            // flightState.protoVessels alone until the next save's Game.Updated() recaptures
            // it, so the witness diffs both lists (todo RESCUE-REGEN-COUNTS-PROTOVESSELS).
            int savedBefore = flightState.protoVessels.Count;
            HashSet<object> keysBefore = CaptureWorldVesselKeys();
            List<WorldVesselFact> added;
            try
            {
                using (SuppressionGuard.Crew())
                    generator.Invoke(rescue, null);
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                added = ReadAddedWorldVessels(keysBefore);
                ParsekLog.Warn(Tag,
                    $"PatchContracts: RecoverAsset.{generatorName} threw for rebuilt contract '{contractId}' " +
                    $"({inner.GetType().Name}: {inner.Message}); vessels added before the throw=" +
                    $"{added.Count.ToString(IC)} [{ContractAcceptWorldIndexer.Describe(added)}]");
                return added.Count > 0;
            }
            uint partIdAfter = (uint)partIdField.GetValue(rescue);
            added = ReadAddedWorldVessels(keysBefore);
            int savedGrew = flightState.protoVessels.Count - savedBefore;

            ParsekLog.Info(Tag,
                $"PatchContracts: regenerated stranded asset for rebuilt contract '{contractId}' through " +
                $"RecoverAsset.{generatorName}: added={added.Count.ToString(IC)} " +
                $"[{ContractAcceptWorldIndexer.Describe(added)}] ({ContractAcceptWorldEffects.DescribeTarget(target)}; " +
                $"partID {partIdBefore.ToString(IC)} -> {partIdAfter.ToString(IC)}; " +
                $"flightStateGrew={savedGrew.ToString(IC)}, stock's next save captures it)");

            if (ContractAcceptWorldEffects.NeedsPartListRepair(target.RecoveringPart, partIdBefore, partIdAfter))
                RepairRebuiltRescuePartLists(contractId, rescue, partIdBefore, partIdAfter);
            return added.Count > 0;
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

        /// <summary>
        /// The accept-side world index over BOTH vessel lists, <c>flightState.protoVessels</c>
        /// and <c>FlightGlobals.Vessels</c>. A vessel stock created this scene (a regeneration,
        /// any other <c>Game.AddVessel</c>) is only in the live list until the next save, so an
        /// index read from flightState alone would let a second rebuild or drain in the same
        /// scene regenerate it again (todo RESCUE-REGEN-COUNTS-PROTOVESSELS).
        /// </summary>
        private static ContractAcceptWorldIndex BuildContractAcceptWorldIndex()
        {
            List<WorldVesselFact> saved = ReadSavedWorldVesselFacts();
            int deadSkipped;
            List<WorldVesselFact> live = ReadLiveWorldVesselFacts(out deadSkipped);
            ContractAcceptWorldIndex index = ContractAcceptWorldIndexer.Build(saved, live);
            ParsekLog.Verbose(Tag,
                $"PatchContracts: accept-side world index: vessels={index.VesselCount.ToString(IC)} " +
                $"saved={saved.Count.ToString(IC)} live={live.Count.ToString(IC)} " +
                $"liveOnly={index.LiveOnlyCount.ToString(IC)} deadSkipped={deadSkipped.ToString(IC)} " +
                $"parts={index.PartFlightIds.Count.ToString(IC)} crewAboard={index.CrewAboard.Count.ToString(IC)}");
            return index;
        }

        /// <summary>Every saved and live vessel object now, for the regeneration witness.</summary>
        private static HashSet<object> CaptureWorldVesselKeys()
        {
            var keys = new HashSet<object>();
            int dead;
            AddWorldVesselKeys(keys, ReadSavedWorldVesselFacts());
            AddWorldVesselKeys(keys, ReadLiveWorldVesselFacts(out dead));
            return keys;
        }

        private static void AddWorldVesselKeys(HashSet<object> keys, List<WorldVesselFact> facts)
        {
            for (int i = 0; i < facts.Count; i++)
            {
                if (facts[i].Key != null)
                    keys.Add(facts[i].Key);
            }
        }

        private static List<WorldVesselFact> ReadAddedWorldVessels(HashSet<object> keysBefore)
        {
            int dead;
            return ContractAcceptWorldIndexer.SelectAdded(
                keysBefore, ReadSavedWorldVesselFacts(), ReadLiveWorldVesselFacts(out dead));
        }

        private static List<WorldVesselFact> ReadSavedWorldVesselFacts()
        {
            var facts = new List<WorldVesselFact>();
            FlightState flightState = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.flightState : null;
            if (flightState == null || flightState.protoVessels == null)
                return facts;
            for (int v = 0; v < flightState.protoVessels.Count; v++)
            {
                ProtoVessel pv = flightState.protoVessels[v];
                if (pv == null || pv.protoPartSnapshots == null)
                    continue;
                facts.Add(ProtoVesselFact(pv, pv, live: false));
            }
            return facts;
        }

        /// <summary>The live vessels, or none when FlightGlobals cannot be read (logged; the
        /// index then reads flightState alone).</summary>
        private static List<WorldVesselFact> ReadLiveWorldVesselFacts(out int deadSkipped)
        {
            deadSkipped = 0;
            try
            {
                return ReadLiveWorldVesselFactsCore(out deadSkipped);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    "PatchContracts: accept-side world index could not read FlightGlobals.Vessels " +
                    $"({ex.GetType().Name}: {ex.Message}); live vessels not indexed");
                return new List<WorldVesselFact>();
            }
        }

        /// <summary>
        /// FlightGlobals.Vessels as world facts: a loaded vessel by its live parts, an unloaded
        /// one by its ProtoVessel; dead vessels (the ones stock's save leaves out) are skipped.
        /// Parsek's ghost map vessels need no IsGhostMapVessel check here: each is one
        /// sensorBarometer part made with flight id 0 and no crew
        /// (<c>GhostMapPresence.ProtoVesselLifecycle</c>), and the index answers only "is this
        /// kerbal aboard" and "is this part id in a vessel", dropping flight id 0, so a ghost
        /// adds nothing either answer reads (and naming GhostMapPresence from KspStatePatcher
        /// would add a backward layer pair).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<WorldVesselFact> ReadLiveWorldVesselFactsCore(out int deadSkipped)
        {
            deadSkipped = 0;
            var facts = new List<WorldVesselFact>();
            if (FlightGlobals.fetch == null)
                return facts;
            List<Vessel> vessels = FlightGlobals.Vessels;
            if (vessels == null)
                return facts;
            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel vessel = vessels[i];
                if (vessel == null)
                    continue;
                if (vessel.state == Vessel.State.DEAD)
                {
                    deadSkipped++;
                    continue;
                }
                if (vessel.loaded && vessel.parts != null && vessel.parts.Count > 0)
                {
                    var f = new WorldVesselFact
                    {
                        Key = vessel,
                        Id = vessel.id,
                        PersistentId = vessel.persistentId,
                        Name = vessel.vesselName,
                        Live = true
                    };
                    for (int p = 0; p < vessel.parts.Count; p++)
                    {
                        Part part = vessel.parts[p];
                        if (part == null)
                            continue;
                        f.PartFlightIds.Add(part.flightID);
                        AddWorldVesselCrew(f, part.protoModuleCrew);
                    }
                    facts.Add(f);
                }
                else if (vessel.protoVessel != null && vessel.protoVessel.protoPartSnapshots != null)
                {
                    WorldVesselFact f = ProtoVesselFact(vessel.protoVessel, vessel, live: true);
                    f.Id = vessel.id;
                    f.PersistentId = vessel.persistentId;
                    facts.Add(f);
                }
            }
            return facts;
        }

        private static WorldVesselFact ProtoVesselFact(ProtoVessel pv, object key, bool live)
        {
            var f = new WorldVesselFact
            {
                Key = key,
                Id = pv.vesselID,
                PersistentId = pv.persistentId,
                Name = pv.vesselName,
                Live = live
            };
            for (int p = 0; p < pv.protoPartSnapshots.Count; p++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[p];
                if (pps == null)
                    continue;
                f.PartFlightIds.Add(pps.flightID);
                AddWorldVesselCrew(f, pps.protoModuleCrew);
            }
            return f;
        }

        private static void AddWorldVesselCrew(WorldVesselFact f, List<ProtoCrewMember> crew)
        {
            if (crew == null)
                return;
            for (int c = 0; c < crew.Count; c++)
            {
                if (crew[c] != null && !string.IsNullOrEmpty(crew[c].name))
                    f.Crew.Add(crew[c].name);
            }
        }
    }
}
