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
        /// already in the world: nothing to do. This is also what keeps a regeneration from
        /// running twice: the vessel it adds is saved with the game.</summary>
        TargetPresent,
        /// <summary>The committed timeline completes, fails or cancels this contract: its
        /// outcome is decided, and a second stranded copy would be a paradox.</summary>
        CommittedResolution,
        /// <summary>A committed flight names the kerbal: that flight owns him (a ghost
        /// carrying him, a spawn at its end), so a stranded copy would be a duplicate.</summary>
        KerbalHeldByCommittedFlight,
        /// <summary>The kerbal to rescue is not in the roster (the offer was generated after
        /// the rewind save), so stock's generators would add nothing.</summary>
        KerbalNotInRoster,
        /// <summary>A target is missing and nothing above holds it: stock's accept-side
        /// generation would have to run once.</summary>
        Regenerate
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
        internal Func<string, bool> KerbalNamedByCommittedFlight;
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
            bool kerbalPresent = !target.RecoveringKerbal
                || (facts.KerbalAboardAnyVessel != null && facts.KerbalAboardAnyVessel(target.KerbalName));
            bool partPresent = !target.RecoveringPart
                || (facts.PartInAnyVessel != null && facts.PartInAnyVessel(target.PartId));
            if (kerbalPresent && partPresent)
                return StrandedAssetDecision.TargetPresent;
            if (facts.CommittedResolution)
                return StrandedAssetDecision.CommittedResolution;
            if (target.RecoveringKerbal && facts.KerbalNamedByCommittedFlight != null
                && facts.KerbalNamedByCommittedFlight(target.KerbalName))
                return StrandedAssetDecision.KerbalHeldByCommittedFlight;
            if (target.RecoveringKerbal
                && (facts.KerbalInRoster == null || !facts.KerbalInRoster(target.KerbalName)))
                return StrandedAssetDecision.KerbalNotInRoster;
            return StrandedAssetDecision.Regenerate;
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
        /// The plan for one rebuilt contract. <paramref name="kerbalIsTourist"/> answers
        /// whether the roster holds that kerbal as a Tourist (what TourismContract.OnAccepted
        /// makes him).
        /// </summary>
        internal static ContractAcceptWorldEffectPlan Plan(
            string contractTypeName, ConfigNode snapshot,
            StrandedAssetWorldFacts strandedFacts, Func<string, bool> kerbalIsTourist)
        {
            var plan = new ContractAcceptWorldEffectPlan
            {
                Effect = Classify(contractTypeName),
                Outcome = "none",
                Detail = ""
            };
            switch (plan.Effect)
            {
                case ContractAcceptWorldEffect.StrandedAsset:
                {
                    RecoverAssetTarget t = ReadRecoverAssetTarget(snapshot);
                    StrandedAssetDecision d = DecideStrandedAsset(t, strandedFacts);
                    plan.Outcome = d.ToString();
                    plan.WorldEffectMissing = d != StrandedAssetDecision.TargetPresent
                        && d != StrandedAssetDecision.NotReadable;
                    plan.Detail = DescribeTarget(t);
                    break;
                }
                case ContractAcceptWorldEffect.TouristRoster:
                {
                    List<string> tourists = ReadTourists(snapshot);
                    var notTourist = new List<string>();
                    for (int i = 0; i < tourists.Count; i++)
                    {
                        if (kerbalIsTourist == null || !kerbalIsTourist(tourists[i]))
                            notTourist.Add(tourists[i]);
                    }
                    plan.WorldEffectMissing = notTourist.Count > 0;
                    plan.Outcome = tourists.Count == 0 ? "unchecked" : (plan.WorldEffectMissing ? "missing" : "present");
                    plan.Detail = notTourist.Count > 0
                        ? "not a Tourist in the roster: [" + string.Join(", ", notTourist.ToArray()) + "]"
                        : "tourists=" + tourists.Count.ToString(IC);
                    break;
                }
                case ContractAcceptWorldEffect.PrebuiltShip:
                case ContractAcceptWorldEffect.ExperimentalPart:
                    plan.Outcome = "unchecked";
                    break;
            }
            return plan;
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
