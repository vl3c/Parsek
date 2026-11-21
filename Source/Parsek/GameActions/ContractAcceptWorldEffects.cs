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
        /// <c>VesselRepairContract</c>: <c>OnAccepted</c> sets <c>waitingForVslSpawn</c> and calls
        /// <c>ContractSystem.SpawnPreBuiltShip</c>, whose <c>ConstructShip</c> coroutine has no
        /// <c>yield return</c>, so the craft is built and added inside the accept and the
        /// contract's own <c>onContractPreBuiltVesselSpawned</c> handler stores its persistent id
        /// (<c>constructionVslId</c> / <c>roverVslId</c> / <c>repairVslId</c>) before
        /// <c>onAccepted</c> fires. <c>waitingForVslSpawn</c> is never saved.</summary>
        PrebuiltShip,
        /// <summary><c>Contracts.Templates.PartTest</c>: an unpurchased part becomes
        /// experimental (<c>ResearchAndDevelopment.AddExperimentalPart</c>, a per-part count in
        /// R&amp;D's <c>ExpParts</c> node; <c>OnFinished</c> takes the count back). The snapshot
        /// carries <c>exp = True</c> when the accept added one.</summary>
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

    /// <summary>
    /// What a rebuilt construction or repair contract (<see cref="ContractAcceptWorldEffect.PrebuiltShip"/>)
    /// would need: the guard a re-spawn of its craft must pass. First match wins.
    /// </summary>
    internal enum PrebuiltShipDecision
    {
        /// <summary>The contract names no craft definition (its <c>OnAccepted</c> would throw).</summary>
        NotReadable,
        /// <summary>The contract names no vessel id: the first timeline's accept built nothing
        /// (stock's <c>ConstructShip</c> refused the craft), so there is nothing to restore.</summary>
        NoSpawnRecorded,
        /// <summary>A vessel with the contract's persistent id, or with its saved vessel name
        /// (stock's own <c>OnLoad</c> fallback), is in the world: nothing to do. A re-spawned
        /// craft is live at once and the contract's handler stores its new id, so a second
        /// rebuild or drain finds it here.</summary>
        TargetPresent,
        /// <summary>The committed timeline completes, fails or cancels the contract: its outcome
        /// is decided, and a second craft would be a paradox.</summary>
        CommittedResolution,
        /// <summary>A committed recording records the contract's vessel (its persistent id): that
        /// flight owns the vessel (a ghost of it, a spawn at its end), so a re-spawn would make
        /// a second one.</summary>
        VesselRecordedByCommittedFlight,
        /// <summary>The craft is missing and nothing above holds it: stock's accept-side spawn runs once.</summary>
        Regenerate
    }

    /// <summary>
    /// What a rebuilt PartTest (<see cref="ContractAcceptWorldEffect.ExperimentalPart"/>) would
    /// need: the guard of re-adding its experimental part. First match wins.
    /// </summary>
    internal enum ExperimentalPartDecision
    {
        /// <summary>The snapshot names no part, or the part is not loaded.</summary>
        NotReadable,
        /// <summary>The snapshot's <c>exp</c> is False: the first timeline's accept added no
        /// experimental part (the part was already purchased), so nothing is missing.</summary>
        NotExperimentalAtAccept,
        /// <summary>R&amp;D is not loaded, or its experimental count cannot be read.</summary>
        RnDNotReadable,
        /// <summary>R&amp;D's experimental count for the part already covers this contract and every
        /// other Active experimental PartTest of the same part. Also the double-add guard: stock
        /// counts, it never dedups, so a second add would outlive this contract's
        /// <c>OnFinished</c>.</summary>
        Present,
        /// <summary>The part is purchased now: stock's own accept in this world would add
        /// nothing, so nothing is missing.</summary>
        PartPurchased,
        /// <summary>The committed timeline completes, fails or cancels the contract. Parsek
        /// retires such a contract without stock's <c>OnFinished</c>, so an added count would
        /// never be taken back and the part would stay usable for free.</summary>
        CommittedResolution,
        /// <summary>The count is short and nothing above holds it: add one, as <c>OnAccepted</c> does.</summary>
        Add
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
        ConvertTourists,
        /// <summary>Run the rebuilt construction / repair contract's own stock <c>OnAccepted</c>
        /// once (Space Center only, where stock's accept runs): it re-spawns the craft through
        /// <c>SpawnPreBuiltShip</c> and the contract's handler stores the new vessel id.</summary>
        SpawnPrebuiltShip,
        /// <summary>A re-spawn outside the Space Center: the contract id is kept in
        /// <see cref="RescueAcceptDeferrals"/> and the craft is spawned, under the same guards,
        /// at the next Space Center visit.</summary>
        DeferPrebuiltShip,
        /// <summary>Add the PartTest's part to R&amp;D as experimental once
        /// (<c>ResearchAndDevelopment.AddExperimentalPart</c>, what <c>OnAccepted</c> does).</summary>
        AddExperimentalPart
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

    /// <summary>The craft a construction / repair contract names (its own saved values).</summary>
    internal struct PrebuiltShipTarget
    {
        internal bool Readable;
        internal string CraftUrl;
        /// <summary>The contract's vessel persistent id; 0 when it names none.</summary>
        internal uint VesselPid;
        /// <summary>The saved vessel name (orbital construction and repair save it; the rover
        /// derives it from the mission seed, which stock's own <c>OnLoad</c> matches), else null.</summary>
        internal string VesselName;
    }

    /// <summary>The live facts the prebuilt-ship guard reads, as delegates so it is pure.</summary>
    internal struct PrebuiltShipWorldFacts
    {
        internal Func<uint, bool> VesselPidInWorld;
        internal Func<string, bool> VesselNamedInWorld;
        internal Func<uint, bool> VesselRecordedByCommittedFlight;
    }

    /// <summary>The part a PartTest snapshot names.</summary>
    internal struct ExperimentalPartTarget
    {
        internal bool Readable;
        internal string PartName;
        /// <summary>The snapshot's <c>exp</c>: the accept made the part experimental.</summary>
        internal bool ExperimentalAtAccept;
    }

    /// <summary>The live R&amp;D facts the experimental-part guard reads.</summary>
    internal struct ExperimentalPartFacts
    {
        internal Func<string, bool> PartKnown;
        /// <summary>The part's tech node lists it purchased (not merely experimental).</summary>
        internal Func<string, bool> PartPurchased;
        /// <summary>R&amp;D's experimental count for the part; negative when R&amp;D is not loaded or
        /// the count cannot be read.</summary>
        internal Func<string, int> ExperimentalCount;
        /// <summary>Active PartTest contracts of the same part, other than this one, whose
        /// <c>PartIsExperimental</c> is set: each holds one count.</summary>
        internal Func<string, int> OtherExperimentalTests;
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
        /// <summary>The craft the contract names (PrebuiltShip only).</summary>
        internal PrebuiltShipTarget Prebuilt;
        /// <summary>The part the contract names (ExperimentalPart only).</summary>
        internal ExperimentalPartTarget Experimental;
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
            return DecideDeferredRescueDrainStep(ContractAcceptWorldEffect.StrandedAsset,
                scene, loadOrPatchPending, activeRescueInStock, ledgerActiveByNow);
        }

        /// <summary>
        /// The scene a construction / repair contract's craft is re-spawned in: the Space Center
        /// only, the one scene stock's own accept (Mission Control) runs
        /// <c>SpawnPreBuiltShip</c> in. <c>ConstructShip</c> builds the craft with
        /// <c>ShipConstruction.AssembleForLaunch</c> and has a separate Flight branch; the
        /// Tracking Station path is unproven, so it waits for the next Space Center visit.
        /// </summary>
        internal static bool IsPrebuiltShipSpawnScene(GameScenes scene)
        {
            return scene == GameScenes.SPACECENTER;
        }

        /// <summary>The protected flag a construction / repair template sets in
        /// <c>OnAccepted</c> and clears in its <c>onContractPreBuiltVesselSpawned</c> handler
        /// (decompiled KSP 1.12.5, all three templates).</summary>
        internal const string PrebuiltSpawnWaitField = "waitingForVslSpawn";

        /// <summary>
        /// Clears <see cref="PrebuiltSpawnWaitField"/> on a contract whose re-run
        /// <c>OnAccepted</c> added no vessel. <c>ConstructShip</c> can return before it spawns
        /// (craft file missing, no launch site), and a flag left set makes the contract adopt
        /// the next prebuilt craft any other contract spawns. Returns the outcome token
        /// (<c>cleared</c>, <c>already-clear</c>, <c>no-field</c>, <c>no-contract</c>).
        /// </summary>
        internal static string ClearPrebuiltSpawnWait(object contract)
        {
            if (contract == null) return "no-contract";
            for (Type t = contract.GetType(); t != null; t = t.BaseType)
            {
                System.Reflection.FieldInfo f = t.GetField(PrebuiltSpawnWaitField,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
                if (f == null || f.FieldType != typeof(bool)) continue;
                if (!(bool)f.GetValue(contract)) return "already-clear";
                f.SetValue(contract, false);
                return "cleared";
            }
            return "no-field";
        }

        /// <summary>The scene a deferred repair of <paramref name="effect"/> may run in.</summary>
        internal static bool IsDeferredRepairScene(ContractAcceptWorldEffect effect, GameScenes scene)
        {
            return effect == ContractAcceptWorldEffect.PrebuiltShip
                ? IsPrebuiltShipSpawnScene(scene)
                : IsRegenerationScene(scene);
        }

        /// <summary>
        /// The deferral drain's first step for one listed contract whose template has
        /// <paramref name="effect"/> (a rescue or a construction / repair contract; the list
        /// holds both). <paramref name="activeRescueInStock"/> is "stock holds the contract
        /// Active as a template that defers"; <paramref name="ledgerActiveByNow"/> is null when
        /// the ledger cannot answer yet.
        /// </summary>
        internal static DeferredRescueDrainStep DecideDeferredRescueDrainStep(
            ContractAcceptWorldEffect effect, GameScenes scene, bool loadOrPatchPending,
            bool activeRescueInStock, bool? ledgerActiveByNow)
        {
            if (!IsDeferredRepairScene(effect, scene))
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
            return Plan(contractTypeName, snapshot, strandedFacts, touristFactOf, scene,
                default(PrebuiltShipWorldFacts), default(ExperimentalPartFacts));
        }

        /// <summary>
        /// <see cref="Plan(string, ConfigNode, StrandedAssetWorldFacts, Func{string, TouristRosterFact}, GameScenes)"/>
        /// with the prebuilt-ship and experimental-part facts. For a construction / repair
        /// contract <paramref name="snapshot"/> is the LIVE rebuilt contract's save (stock's
        /// <c>OnLoad</c> has already re-pointed its vessel id at a same-named vessel); for every
        /// other template it is the accept snapshot. The committed resolution of every template
        /// is <see cref="StrandedAssetWorldFacts.CommittedResolution"/>.
        /// </summary>
        internal static ContractAcceptWorldEffectPlan Plan(
            string contractTypeName, ConfigNode snapshot,
            StrandedAssetWorldFacts strandedFacts, Func<string, TouristRosterFact> touristFactOf,
            GameScenes scene, PrebuiltShipWorldFacts prebuiltFacts, ExperimentalPartFacts experimentalFacts)
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
                    PlanPrebuiltShip(ref plan, contractTypeName, snapshot, prebuiltFacts,
                        strandedFacts.CommittedResolution, scene);
                    break;
                case ContractAcceptWorldEffect.ExperimentalPart:
                    PlanExperimentalPart(ref plan, snapshot, experimentalFacts, strandedFacts.CommittedResolution);
                    break;
            }
            return plan;
        }

        private static void PlanPrebuiltShip(ref ContractAcceptWorldEffectPlan plan, string contractTypeName,
            ConfigNode node, PrebuiltShipWorldFacts facts, bool committedResolution, GameScenes scene)
        {
            PrebuiltShipTarget t = ReadPrebuiltShipTarget(contractTypeName, node);
            PrebuiltShipDecision d = DecidePrebuiltShip(t, facts, committedResolution);
            plan.Prebuilt = t;
            plan.Outcome = d.ToString();
            plan.Detail = DescribePrebuiltTarget(t);
            plan.WorldEffectMissing = d != PrebuiltShipDecision.TargetPresent
                && d != PrebuiltShipDecision.NotReadable
                && d != PrebuiltShipDecision.NoSpawnRecorded;
            if (d == PrebuiltShipDecision.Regenerate)
            {
                if (IsPrebuiltShipSpawnScene(scene))
                {
                    plan.Repair = ContractAcceptRepair.SpawnPrebuiltShip;
                }
                else
                {
                    plan.Repair = ContractAcceptRepair.DeferPrebuiltShip;
                    plan.RepairSkipReason = "scene=" + scene;
                }
            }
            else if (plan.WorldEffectMissing)
            {
                plan.RepairSkipReason = d.ToString();
            }
        }

        private static void PlanExperimentalPart(ref ContractAcceptWorldEffectPlan plan, ConfigNode node,
            ExperimentalPartFacts facts, bool committedResolution)
        {
            ExperimentalPartTarget t = ReadExperimentalPartTarget(node);
            int count, others;
            ExperimentalPartDecision d = DecideExperimentalPart(t, facts, committedResolution, out count, out others);
            plan.Experimental = t;
            plan.Outcome = d.ToString();
            plan.Detail = DescribeExperimentalTarget(t, count, others);
            plan.WorldEffectMissing = d == ExperimentalPartDecision.Add
                || d == ExperimentalPartDecision.CommittedResolution;
            if (d == ExperimentalPartDecision.Add)
                plan.Repair = ContractAcceptRepair.AddExperimentalPart;
            else if (plan.WorldEffectMissing || d == ExperimentalPartDecision.RnDNotReadable)
                plan.RepairSkipReason = d.ToString();
        }

        /// <summary>Reads a construction / repair contract's own saved values (decompiled
        /// <c>OnSave</c>: the craft URL under <c>constructionCraftDef</c> or, for the rover and the
        /// repair contract, <c>roverCraftDef</c>; the vessel id under <c>constructionVslId</c> /
        /// <c>roverVslId</c> / <c>repairVslId</c>; <c>vesselName</c> for orbital construction
        /// and repair).</summary>
        internal static PrebuiltShipTarget ReadPrebuiltShipTarget(string contractTypeName, ConfigNode node)
        {
            var t = new PrebuiltShipTarget();
            if (node == null)
                return t;
            string craftKey, idKey;
            switch (contractTypeName)
            {
                case "OrbitalConstructionContract":
                    craftKey = "constructionCraftDef";
                    idKey = "constructionVslId";
                    break;
                case "RoverConstructionContract":
                    craftKey = "roverCraftDef";
                    idKey = "roverVslId";
                    break;
                case "VesselRepairContract":
                    craftKey = "roverCraftDef";
                    idKey = "repairVslId";
                    break;
                default:
                    return t;
            }
            t.CraftUrl = node.GetValue(craftKey);
            uint pid;
            string rawId = node.GetValue(idKey);
            if (!string.IsNullOrEmpty(rawId) && uint.TryParse(rawId, NumberStyles.Integer, IC, out pid))
                t.VesselPid = pid;
            string name = node.GetValue("vesselName");
            t.VesselName = string.IsNullOrEmpty(name) ? null : name;
            t.Readable = !string.IsNullOrEmpty(t.CraftUrl);
            return t;
        }

        /// <summary>The guard a re-spawn of a construction / repair contract's craft must pass.</summary>
        internal static PrebuiltShipDecision DecidePrebuiltShip(
            PrebuiltShipTarget target, PrebuiltShipWorldFacts facts, bool committedResolution)
        {
            if (!target.Readable)
                return PrebuiltShipDecision.NotReadable;
            if (target.VesselPid == 0)
                return PrebuiltShipDecision.NoSpawnRecorded;
            if (facts.VesselPidInWorld != null && facts.VesselPidInWorld(target.VesselPid))
                return PrebuiltShipDecision.TargetPresent;
            if (target.VesselName != null && facts.VesselNamedInWorld != null
                && facts.VesselNamedInWorld(target.VesselName))
                return PrebuiltShipDecision.TargetPresent;
            if (committedResolution)
                return PrebuiltShipDecision.CommittedResolution;
            if (facts.VesselRecordedByCommittedFlight != null
                && facts.VesselRecordedByCommittedFlight(target.VesselPid))
                return PrebuiltShipDecision.VesselRecordedByCommittedFlight;
            return PrebuiltShipDecision.Regenerate;
        }

        /// <summary>Reads a PartTest snapshot's own values (<c>PartTest.OnSave</c>: <c>part</c>,
        /// the internal part name or the literal <c>Part</c> when none; <c>exp</c>).</summary>
        internal static ExperimentalPartTarget ReadExperimentalPartTarget(ConfigNode node)
        {
            var t = new ExperimentalPartTarget();
            if (node == null)
                return t;
            string part = node.GetValue("part");
            string exp = node.GetValue("exp");
            bool expValue;
            if (string.IsNullOrEmpty(part) || part == "Part"
                || string.IsNullOrEmpty(exp) || !bool.TryParse(exp.Trim(), out expValue))
                return t;
            t.PartName = part;
            t.ExperimentalAtAccept = expValue;
            t.Readable = true;
            return t;
        }

        /// <summary>The guard of re-adding a rebuilt PartTest's experimental part. Hands back
        /// the R&amp;D count and the other tests' count it read (-1 when not read).</summary>
        internal static ExperimentalPartDecision DecideExperimentalPart(
            ExperimentalPartTarget target, ExperimentalPartFacts facts, bool committedResolution,
            out int count, out int otherTests)
        {
            count = -1;
            otherTests = -1;
            if (!target.Readable || facts.PartKnown == null || !facts.PartKnown(target.PartName))
                return ExperimentalPartDecision.NotReadable;
            if (!target.ExperimentalAtAccept)
                return ExperimentalPartDecision.NotExperimentalAtAccept;
            count = facts.ExperimentalCount != null ? facts.ExperimentalCount(target.PartName) : -1;
            if (count < 0)
                return ExperimentalPartDecision.RnDNotReadable;
            otherTests = facts.OtherExperimentalTests != null ? facts.OtherExperimentalTests(target.PartName) : 0;
            if (otherTests < 0)
                otherTests = 0;
            if (count >= otherTests + 1)
                return ExperimentalPartDecision.Present;
            if (facts.PartPurchased != null && facts.PartPurchased(target.PartName))
                return ExperimentalPartDecision.PartPurchased;
            if (committedResolution)
                return ExperimentalPartDecision.CommittedResolution;
            return ExperimentalPartDecision.Add;
        }

        internal static string DescribePrebuiltTarget(PrebuiltShipTarget t)
        {
            if (!t.Readable)
                return "craft unreadable";
            return "craft='" + t.CraftUrl + "' vesselPid=" + t.VesselPid.ToString(IC) +
                (t.VesselName != null ? " vesselName='" + t.VesselName + "'" : "");
        }

        internal static string DescribeExperimentalTarget(ExperimentalPartTarget t, int count, int otherTests)
        {
            if (!t.Readable)
                return "part unreadable";
            return "part='" + t.PartName + "' expAtAccept=" + t.ExperimentalAtAccept.ToString(IC) +
                " rndCount=" + count.ToString(IC) + " otherTests=" + otherTests.ToString(IC);
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
