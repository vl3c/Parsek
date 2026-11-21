using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// What a stock contract template's <c>OnAccepted</c> does to the WORLD, outside the
    /// contract's own saved state (decompiled KSP 1.12.5). Stock runs it inside
    /// <c>Contract.SetState(State.Active)</c>, after <c>AwardAdvance</c> and before
    /// <c>onAccepted</c> fires, so the snapshot Parsek takes at <c>onAccepted</c> already
    /// holds what <c>OnAccepted</c> wrote INTO the contract (the rescue's kerbal and part ids
    /// in its parameters, a launch id) but not what it wrote into the game.
    /// </summary>
    internal enum ContractAcceptWorldEffect
    {
        /// <summary>OnAccepted changes nothing outside the contract, or the template is not
        /// stock (BaseContract / SatelliteContract / StationContract / GrandTour only stamp a
        /// launch id into a parameter, which the snapshot carries).</summary>
        None,
        /// <summary><c>Contracts.Templates.RecoverAsset</c>: <c>GenerateOrbitalRecovery</c> /
        /// <c>GenerateLandRecovery</c> add the stranded vessel (the kerbal and / or the part)
        /// to <c>HighLogic.CurrentGame</c> with <c>Game.AddVessel</c>.</summary>
        StrandedAsset,
        /// <summary><c>FinePrint.Contracts.TourismContract</c>: each tourist, created Unowned
        /// and Assigned when the contract was offered, becomes a Tourist and Available.</summary>
        TouristRoster,
        /// <summary><c>OrbitalConstructionContract</c>, <c>RoverConstructionContract</c>,
        /// <c>VesselRepairContract</c>: <c>ContractSystem.SpawnPreBuiltShip</c> spawns the
        /// craft the contract is about.</summary>
        PrebuiltShip,
        /// <summary><c>Contracts.Templates.PartTest</c>: an unpurchased part becomes
        /// experimental (<c>ResearchAndDevelopment.AddExperimentalPart</c>).</summary>
        ExperimentalPart
    }

    /// <summary>
    /// What a rebuilt RecoverAsset contract would need: the guard a regeneration of the
    /// stranded vessel must pass. Ordered: the first matching reason wins.
    /// </summary>
    internal enum StrandedAssetDecision
    {
        /// <summary>The snapshot names no recovery type, location or target.</summary>
        NotReadable,
        /// <summary>Every target (the kerbal aboard some vessel, the part in some vessel) is
        /// already in the world: nothing to do. A regeneration's vessel is live at once and saved
        /// with the game at the next save, and the world index reads both lists
        /// (<see cref="ContractAcceptWorldIndexer"/>), so the next rebuild of the same contract
        /// finds it here, in the same scene or after a load. With
        /// <see cref="PartiallyPresent"/> it is what keeps a regeneration from running twice:
        /// a compound rescue is checked target by target, never only as the pair.</summary>
        TargetPresent,
        /// <summary>A compound rescue (kerbal and part) of which one target is already in the
        /// world and the other is not (the kerbal aboard a vessel while the snapshot's part id is
        /// in none, or the part in a vessel while the kerbal is aboard none). Stock's generators
        /// make both targets together, so a regeneration would seat the same kerbal in a second
        /// vessel (<c>GenerateKerbalInPart</c> -> <c>SeatKerbal</c>) or add a second copy of the
        /// part's vessel. Left alone.</summary>
        PartiallyPresent,
        /// <summary>The committed timeline completes, fails or cancels this contract: its
        /// outcome is decided, and a second stranded copy would be a paradox.</summary>
        CommittedResolution,
        /// <summary>The kerbal is reserved: a committed flight names him, Parsek manages him
        /// (reserved now, a stand-in, retired) or he is in the crew swap map. That flight owns
        /// him (a ghost carrying him, a spawn at its end), so a stranded copy would be a
        /// duplicate and a swap would rename the rescue.</summary>
        KerbalReserved,
        /// <summary>The kerbal to rescue is not in the roster (the offer was generated after
        /// the rewind save), so stock's generators would add nothing. Left alone: re-creating
        /// a kerbal under a given name is a roster write stock never makes (its offer draws a
        /// fresh random name), and the contract's text and parameters would still name him.</summary>
        KerbalNotInRoster,
        /// <summary>The kerbal to rescue is in the roster but no longer the Unowned stranded
        /// kerbal the offer made (crew, applicant, tourist): stock's generators would seat
        /// whoever the name resolves to, so a roster kerbal would be placed in a second spot.</summary>
        KerbalNotUnowned,
        /// <summary>A target is missing and nothing above holds it: stock's accept-side
        /// generation runs once.</summary>
        Regenerate
    }

    /// <summary>What the contract patch does about a rebuilt contract's missing accept-side
    /// world effect.</summary>
    internal enum ContractAcceptRepair
    {
        /// <summary>Nothing: no effect, nothing missing, or a guard holds it.</summary>
        None,
        /// <summary>Run the RecoverAsset's own stock generator once
        /// (<c>GenerateLandRecovery</c> / <c>GenerateOrbitalRecovery</c>).</summary>
        RegenerateStrandedAsset,
        /// <summary>A regeneration the scene refuses (Flight, the editors): the contract id is
        /// kept in <see cref="RescueAcceptDeferrals"/> and the stranded vessel is regenerated, under
        /// the same guards, at the next Space Center or Tracking Station visit.</summary>
        DeferStrandedAsset,
        /// <summary>Apply <c>TourismContract.OnAccepted</c>'s roster change to the tourists
        /// whose decision is <see cref="TouristConversion.Convert"/>.</summary>
        ConvertTourists
    }

    /// <summary>
    /// What the deferred-rescue drain (<see cref="RescueAcceptDeferrals"/>) does with one listed
    /// contract before the stranded-asset guard runs. First match wins.
    /// </summary>
    internal enum DeferredRescueDrainStep
    {
        /// <summary>Not the Space Center or the Tracking Station: the id stays listed.</summary>
        KeepNotRegenerationScene,
        /// <summary>A scenario load or a deferred contract patch is still running; the patch
        /// fires <c>onContractsLoaded</c> again when it is done, and the drain runs then.</summary>
        KeepLoadOrPatchPending,
        /// <summary>The ledger's contracts module is not there yet: the id stays listed.</summary>
        KeepLedgerNotReady,
        /// <summary>Stock holds no Active RecoverAsset with this id (it completed, failed, was
        /// removed or never came back): dropped.</summary>
        DropContractNotActive,
        /// <summary>The ledger no longer holds the contract accepted by now (the next contract
        /// patch takes it off stock's active list): dropped, never regenerated.</summary>
        DropNotLedgerActive,
        /// <summary>Run the stranded-asset guard on the live contract; the id leaves the list
        /// whatever it decides.</summary>
        Evaluate
    }

    /// <summary>What the roster holds for one listed tourist.</summary>
    internal struct TouristRosterFact
    {
        internal bool InRoster;
        internal bool IsTourist;
        internal bool IsUnowned;
        /// <summary>Named by a committed flight, managed by Parsek, or in the crew swap map.</summary>
        internal bool Reserved;
    }

    /// <summary>What to do with one listed tourist of a rebuilt TourismContract.</summary>
    internal enum TouristConversion
    {
        /// <summary>Unowned and unreserved: apply stock's accept effect.</summary>
        Convert,
        AlreadyTourist,
        /// <summary>A committed flight or Parsek's crew bookkeeping holds him: never touched.</summary>
        Reserved,
        /// <summary>Crew or applicant: not the offer's kerbal any more, never touched.</summary>
        NotUnowned,
        /// <summary>Not in the roster (the offer postdates the rewind save).</summary>
        NotInRoster
    }

    /// <summary>The targets a RecoverAsset snapshot names (its own saved values).</summary>
    internal struct RecoverAssetTarget
    {
        internal bool Readable;
        internal string KerbalName;
        internal uint PartId;
        internal bool RecoveringKerbal;
        internal bool RecoveringPart;
        internal bool Surface;
    }

    /// <summary>The live facts the stranded-asset guard reads, as delegates so it is pure.</summary>
    internal struct StrandedAssetWorldFacts
    {
        internal Func<uint, bool> PartInAnyVessel;
        internal Func<string, bool> KerbalAboardAnyVessel;
        internal Func<string, bool> KerbalInRoster;
        /// <summary>The roster holds the kerbal as <c>KerbalType.Unowned</c> (what the offer
        /// made him; stock turns him Crew only when the rescue parameter completes).</summary>
        internal Func<string, bool> KerbalUnowned;
        /// <summary>Named by a committed flight, managed by Parsek, or in the crew swap map.</summary>
        internal Func<string, bool> KerbalReserved;
        /// <summary>The committed timeline completes, fails or cancels the contract (a committed
        /// ContractComplete / Fail / Cancel row at any UT). Read by both repairs: the stranded
        /// asset and the tourist conversion (owner ruling 2026-11-20).</summary>
        internal bool CommittedResolution;
    }

    /// <summary>One restored contract's accept-side world effect and what it would need.</summary>
    internal struct ContractAcceptWorldEffectPlan
    {
        internal ContractAcceptWorldEffect Effect;
        /// <summary>Grep-stable token: <c>none</c>, <c>present</c>, <c>missing</c>,
        /// <c>unchecked</c>, or a <see cref="StrandedAssetDecision"/> name.</summary>
        internal string Outcome;
        internal string Detail;
        /// <summary>True when the world lacks what stock's accept would have made.</summary>
        internal bool WorldEffectMissing;
        /// <summary>What the patch does about it.</summary>
        internal ContractAcceptRepair Repair;
        /// <summary>Grep-stable reason a missing effect is NOT repaired, else null.</summary>
        internal string RepairSkipReason;
        /// <summary>The RecoverAsset target the snapshot names (StrandedAsset only).</summary>
        internal RecoverAssetTarget Target;
        /// <summary>Per listed tourist, in order (TouristRoster only).</summary>
        internal List<KeyValuePair<string, TouristConversion>> Tourists;
    }

    /// <summary>
    /// Pure. A contract Parsek rebuilds from its accept snapshot (<c>Contract.Load</c> in
    /// <c>KspStatePatcher.PatchContractsCore</c>) never runs <c>OnAccepted</c>: Load sets the
    /// state field directly. After a rewind to before the accept, the world the clock
    /// reaches the accept in is the rewind save's, so the rebuilt contract can name a
    /// stranded vessel, tourists or a craft that do not exist (todo
    /// CONTRACT-REBUILD-SKIPS-ACCEPT-WORLD-EFFECTS). This class classifies the template and
    /// decides, from the snapshot and live facts handed in, whether that is so.
    /// </summary>
    internal static class ContractAcceptWorldEffects
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // RecoverAsset's private enums, saved as ints (RecoveryLocation NONE, ORBITLOW,
        // ORBITHIGH, SURFACE; RecoveryType NONE, KERBAL, PART, COMPOUND).
        private const int RecoveryLocationNone = 0;
        private const int RecoveryLocationSurface = 3;
        private const int RecoveryTypeKerbal = 1;
        private const int RecoveryTypePart = 2;
        private const int RecoveryTypeCompound = 3;

        /// <summary>The template's accept-side world effect, by the saved <c>type</c> name.</summary>
        internal static ContractAcceptWorldEffect Classify(string contractTypeName)
        {
            switch (contractTypeName)
            {
                case "RecoverAsset":
                    return ContractAcceptWorldEffect.StrandedAsset;
                case "TourismContract":
                    return ContractAcceptWorldEffect.TouristRoster;
                case "OrbitalConstructionContract":
                case "RoverConstructionContract":
                case "VesselRepairContract":
                    return ContractAcceptWorldEffect.PrebuiltShip;
                case "PartTest":
                    return ContractAcceptWorldEffect.ExperimentalPart;
                default:
                    return ContractAcceptWorldEffect.None;
            }
        }

        /// <summary>Reads a RecoverAsset snapshot's own values (<c>RecoverAsset.OnSave</c>).</summary>
        internal static RecoverAssetTarget ReadRecoverAssetTarget(ConfigNode snapshot)
        {
            var t = new RecoverAssetTarget();
            if (snapshot == null)
                return t;
            int type, location;
            if (!TryReadInt(snapshot, "recoveryType", out type)
                || !TryReadInt(snapshot, "recoveryLocation", out location))
                return t;
            t.RecoveringKerbal = type == RecoveryTypeKerbal || type == RecoveryTypeCompound;
            t.RecoveringPart = type == RecoveryTypePart || type == RecoveryTypeCompound;
            t.Surface = location == RecoveryLocationSurface;
            t.KerbalName = snapshot.GetValue("kerbalName");
            uint partId;
            string rawPart = snapshot.GetValue("partID");
            if (!string.IsNullOrEmpty(rawPart)
                && uint.TryParse(rawPart, NumberStyles.Integer, IC, out partId))
                t.PartId = partId;
            bool kerbalOk = !t.RecoveringKerbal || !string.IsNullOrEmpty(t.KerbalName);
            bool partOk = !t.RecoveringPart || t.PartId != 0;
            t.Readable = location != RecoveryLocationNone
                && (t.RecoveringKerbal || t.RecoveringPart)
                && kerbalOk && partOk;
            return t;
        }

        /// <summary>The guard a regeneration of the stranded vessel must pass.</summary>
        internal static StrandedAssetDecision DecideStrandedAsset(
            RecoverAssetTarget target, StrandedAssetWorldFacts facts)
        {
            if (!target.Readable)
                return StrandedAssetDecision.NotReadable;
            bool kerbalAboard = target.RecoveringKerbal
                && facts.KerbalAboardAnyVessel != null && facts.KerbalAboardAnyVessel(target.KerbalName);
            bool partInWorld = target.RecoveringPart
                && facts.PartInAnyVessel != null && facts.PartInAnyVessel(target.PartId);
            bool kerbalPresent = !target.RecoveringKerbal || kerbalAboard;
            bool partPresent = !target.RecoveringPart || partInWorld;
            if (kerbalPresent && partPresent)
                return StrandedAssetDecision.TargetPresent;
            // Any named target already in the world: stock's generators would duplicate it.
            if (kerbalAboard || partInWorld)
                return StrandedAssetDecision.PartiallyPresent;
            if (facts.CommittedResolution)
                return StrandedAssetDecision.CommittedResolution;
            if (target.RecoveringKerbal && facts.KerbalReserved != null
                && facts.KerbalReserved(target.KerbalName))
                return StrandedAssetDecision.KerbalReserved;
            if (target.RecoveringKerbal
                && (facts.KerbalInRoster == null || !facts.KerbalInRoster(target.KerbalName)))
                return StrandedAssetDecision.KerbalNotInRoster;
            if (target.RecoveringKerbal
                && (facts.KerbalUnowned == null || !facts.KerbalUnowned(target.KerbalName)))
                return StrandedAssetDecision.KerbalNotUnowned;
            return StrandedAssetDecision.Regenerate;
        }

        /// <summary>
        /// The scenes a stranded vessel is regenerated in: Space Center (where stock's own
        /// Mission Control accept runs <c>OnAccepted</c>) and the Tracking Station, both with
        /// every vessel unloaded and packed. Flight and the editors are refused: a flight
        /// scene has a loaded physics bubble and Parsek's recorders listening, an editor has
        /// no planetarium.
        /// </summary>
        internal static bool IsRegenerationScene(GameScenes scene)
        {
            return scene == GameScenes.SPACECENTER || scene == GameScenes.TRACKSTATION;
        }

        /// <summary>
        /// The deferred-rescue drain's first step for one listed contract
        /// (<see cref="DeferredRescueDrainStep"/>). <paramref name="ledgerActiveByNow"/> is null
        /// when the ledger cannot answer yet.
        /// </summary>
        internal static DeferredRescueDrainStep DecideDeferredRescueDrainStep(
            GameScenes scene, bool loadOrPatchPending, bool activeRescueInStock, bool? ledgerActiveByNow)
        {
            if (!IsRegenerationScene(scene))
                return DeferredRescueDrainStep.KeepNotRegenerationScene;
            if (loadOrPatchPending)
                return DeferredRescueDrainStep.KeepLoadOrPatchPending;
            if (!ledgerActiveByNow.HasValue)
                return DeferredRescueDrainStep.KeepLedgerNotReady;
            if (!activeRescueInStock)
                return DeferredRescueDrainStep.DropContractNotActive;
            if (!ledgerActiveByNow.Value)
                return DeferredRescueDrainStep.DropNotLedgerActive;
            return DeferredRescueDrainStep.Evaluate;
        }

        /// <summary>
        /// Whether the parameter part lists need repair after stock's generator ran: the
        /// generators draw a fresh <c>partID</c>, while the rebuilt contract's AcquirePart /
        /// RecoverPart still list the one the snapshot carries. True only for a part recovery
        /// whose id changed to a real one.
        /// </summary>
        internal static bool NeedsPartListRepair(bool recoveringPart, uint partIdBefore, uint partIdAfter)
        {
            return recoveringPart && partIdAfter != 0 && partIdAfter != partIdBefore;
        }

        /// <summary>A TourismContract snapshot's tourists (<c>tourists = a|b|c</c>).</summary>
        internal static List<string> ReadTourists(ConfigNode snapshot)
        {
            var names = new List<string>();
            string raw = snapshot != null ? snapshot.GetValue("tourists") : null;
            if (string.IsNullOrEmpty(raw))
                return names;
            string[] parts = raw.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                string n = parts[i].Trim();
                if (n.Length > 0 && !names.Contains(n))
                    names.Add(n);
            }
            return names;
        }

        /// <summary>
        /// The plan for one rebuilt contract: what its accept-side world effect is, whether
        /// the world lacks it, and what the patch does about it. <paramref name="touristFactOf"/>
        /// answers what the roster holds for one listed tourist; <paramref name="scene"/> is the
        /// scene the patch runs in. A stranded asset to regenerate outside the regeneration
        /// scenes plans <see cref="ContractAcceptRepair.DeferStrandedAsset"/>; a tourism contract
        /// the committed timeline resolves converts nobody.
        /// </summary>
        internal static ContractAcceptWorldEffectPlan Plan(
            string contractTypeName, ConfigNode snapshot,
            StrandedAssetWorldFacts strandedFacts, Func<string, TouristRosterFact> touristFactOf,
            GameScenes scene)
        {
            var plan = new ContractAcceptWorldEffectPlan
            {
                Effect = Classify(contractTypeName),
                Outcome = "none",
                Detail = "",
                Repair = ContractAcceptRepair.None
            };
            switch (plan.Effect)
            {
                case ContractAcceptWorldEffect.StrandedAsset:
                {
                    RecoverAssetTarget t = ReadRecoverAssetTarget(snapshot);
                    StrandedAssetDecision d = DecideStrandedAsset(t, strandedFacts);
                    plan.Target = t;
                    plan.Outcome = d.ToString();
                    plan.WorldEffectMissing = d != StrandedAssetDecision.TargetPresent
                        && d != StrandedAssetDecision.NotReadable;
                    plan.Detail = DescribeTarget(t);
                    if (d == StrandedAssetDecision.Regenerate)
                    {
                        if (IsRegenerationScene(scene))
                        {
                            plan.Repair = ContractAcceptRepair.RegenerateStrandedAsset;
                        }
                        else
                        {
                            // Owner ruling 2026-11-20: regenerated at the next Space Center or
                            // Tracking Station visit (RescueAcceptDeferrals).
                            plan.Repair = ContractAcceptRepair.DeferStrandedAsset;
                            plan.RepairSkipReason = "scene=" + scene;
                        }
                    }
                    else if (plan.WorldEffectMissing)
                    {
                        plan.RepairSkipReason = d.ToString();
                    }
                    break;
                }
                case ContractAcceptWorldEffect.TouristRoster:
                {
                    plan.Tourists = DecideTouristConversions(snapshot, touristFactOf);
                    var notTourist = new List<string>();
                    var held = new List<string>();
                    int convert = 0;
                    for (int i = 0; i < plan.Tourists.Count; i++)
                    {
                        TouristConversion c = plan.Tourists[i].Value;
                        if (c == TouristConversion.AlreadyTourist)
                            continue;
                        notTourist.Add(plan.Tourists[i].Key);
                        if (c == TouristConversion.Convert)
                            convert++;
                        else
                            held.Add(plan.Tourists[i].Key + ":" + c);
                    }
                    plan.WorldEffectMissing = notTourist.Count > 0;
                    plan.Outcome = plan.Tourists.Count == 0
                        ? "unchecked"
                        : (plan.WorldEffectMissing ? "missing" : "present");
                    plan.Detail = notTourist.Count > 0
                        ? "not a Tourist in the roster: [" + string.Join(", ", notTourist.ToArray()) + "]"
                        : "tourists=" + plan.Tourists.Count.ToString(IC);
                    if (convert > 0 && strandedFacts.CommittedResolution)
                    {
                        // Owner ruling 2026-11-20: the committed timeline completes, fails or
                        // cancels this contract, so its accept is not replayed into the roster.
                        plan.RepairSkipReason = StrandedAssetDecision.CommittedResolution.ToString();
                        break;
                    }
                    if (convert > 0)
                        plan.Repair = ContractAcceptRepair.ConvertTourists;
                    if (held.Count > 0)
                        plan.RepairSkipReason = "[" + string.Join(", ", held.ToArray()) + "]";
                    break;
                }
                case ContractAcceptWorldEffect.PrebuiltShip:
                case ContractAcceptWorldEffect.ExperimentalPart:
                    plan.Outcome = "unchecked";
                    break;
            }
            return plan;
        }

        /// <summary>
        /// What to do with one listed tourist, the guard of stock's
        /// <c>TourismContract.OnAccepted</c> effect (type Tourist, tourist trait, Available).
        /// First match wins.
        /// </summary>
        internal static TouristConversion DecideTouristConversion(TouristRosterFact fact)
        {
            if (!fact.InRoster)
                return TouristConversion.NotInRoster;
            if (fact.IsTourist)
                return TouristConversion.AlreadyTourist;
            if (fact.Reserved)
                return TouristConversion.Reserved;
            if (!fact.IsUnowned)
                return TouristConversion.NotUnowned;
            return TouristConversion.Convert;
        }

        /// <summary>The decision for every listed tourist of a TourismContract snapshot, in order.</summary>
        internal static List<KeyValuePair<string, TouristConversion>> DecideTouristConversions(
            ConfigNode snapshot, Func<string, TouristRosterFact> factOf)
        {
            var result = new List<KeyValuePair<string, TouristConversion>>();
            List<string> tourists = ReadTourists(snapshot);
            for (int i = 0; i < tourists.Count; i++)
            {
                TouristRosterFact f = factOf != null ? factOf(tourists[i]) : default(TouristRosterFact);
                result.Add(new KeyValuePair<string, TouristConversion>(tourists[i], DecideTouristConversion(f)));
            }
            return result;
        }

        internal static string DescribeTarget(RecoverAssetTarget t)
        {
            if (!t.Readable)
                return "target unreadable";
            string s = t.Surface ? "surface" : "orbit";
            if (t.RecoveringKerbal)
                s += " kerbal='" + t.KerbalName + "'";
            if (t.RecoveringPart)
                s += " partID=" + t.PartId.ToString(IC);
            return s;
        }

        private static bool TryReadInt(ConfigNode node, string key, out int value)
        {
            value = 0;
            string raw = node.GetValue(key);
            return !string.IsNullOrEmpty(raw)
                && int.TryParse(raw, NumberStyles.Integer, IC, out value);
        }
    }
}
