using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Discriminator for GameAction — one value per action schema from the design doc.
    /// Explicit int values for serialization stability.
    /// </summary>
    public enum GameActionType
    {
        ScienceEarning        = 0,
        ScienceSpending       = 1,
        FundsEarning          = 2,
        FundsSpending         = 3,
        MilestoneAchievement  = 4,
        ContractAccept        = 5,
        ContractComplete      = 6,
        ContractFail          = 7,
        ContractCancel        = 8,
        ReputationEarning     = 9,
        ReputationPenalty     = 10,
        KerbalAssignment      = 11,
        KerbalHire            = 12,
        KerbalRescue          = 13,
        KerbalStandIn         = 14,
        FacilityUpgrade       = 15,
        FacilityDestruction   = 16,
        FacilityRepair        = 17,
        StrategyActivate      = 18,
        StrategyDeactivate    = 19,
        FundsInitial          = 20
    }

    /// <summary>How science was collected — transmitted from orbit or recovered on the ground.</summary>
    public enum ScienceMethod
    {
        Transmitted = 0,
        Recovered   = 1
    }

    /// <summary>Where fund earnings came from.</summary>
    public enum FundsSource
    {
        ContractComplete = 0,
        ContractAdvance  = 1,
        Recovery         = 2,
        Milestone        = 3,
        Other            = 4
    }

    /// <summary>Where funds were spent.</summary>
    public enum FundsSpendingSource
    {
        VesselBuild      = 0,
        FacilityUpgrade  = 1,
        FacilityRepair   = 2,
        KerbalHire       = 3,
        ContractPenalty  = 4,
        Strategy         = 5,
        Other            = 6
    }

    /// <summary>Where reputation earnings came from.</summary>
    public enum ReputationSource
    {
        ContractComplete = 0,
        Milestone        = 1,
        Other            = 2
    }

    /// <summary>Where reputation penalties came from.</summary>
    public enum ReputationPenaltySource
    {
        ContractFail    = 0,
        ContractDecline = 1,
        KerbalDeath     = 2,
        Strategy        = 3,
        Other           = 4
    }

    // KerbalEndState enum is in KerbalEndState.cs (Aboard=0, Dead=1, Recovered=2, Unknown=3)

    /// <summary>
    /// Resource type identifier for strategy source/target fields.
    /// </summary>
    public enum StrategyResource
    {
        Funds      = 0,
        Science    = 1,
        Reputation = 2
    }

    /// <summary>
    /// Union type for all game actions on the ledger timeline.
    /// Uses a single class with nullable/sentinel fields — the <see cref="Type"/> field
    /// discriminates which fields are populated. Simpler serialization than an inheritance hierarchy.
    /// </summary>
    public class GameAction
    {
        // ---- Common fields (all action types) ----

        /// <summary>Universal time when the action occurred.</summary>
        public double UT;

        /// <summary>Discriminator — determines which fields are populated.</summary>
        public GameActionType Type;

        /// <summary>Recording that produced this action. Null for KSC spending actions and system-generated actions.</summary>
        public string RecordingId;

        /// <summary>Ordering within the same UT for spending actions. 0 for earnings.</summary>
        public int Sequence;

        /// <summary>
        /// Derived field set by resource modules during recalculation. NOT serialized.
        /// True means this action's effects should be applied (e.g., first completion of a contract).
        /// False means the action is a duplicate (e.g., second completion of the same contract — rewards zeroed).
        /// Defaults to true — only modules that implement once-ever semantics set this to false.
        /// </summary>
        public bool Effective = true;

        // ---- Science fields ----

        /// <summary>Full KSP subject string, e.g. "crewReport@MunSrfLandedMidlands".</summary>
        public string SubjectId;

        /// <summary>Experiment type, e.g. "crewReport".</summary>
        public string ExperimentId;

        /// <summary>Celestial body name, e.g. "Mun".</summary>
        public string Body;

        /// <summary>KSP situation string, e.g. "SrfLanded".</summary>
        public string Situation;

        /// <summary>Biome name, e.g. "Midlands".</summary>
        public string Biome;

        /// <summary>Science points KSP actually credited (immutable). Post-transmit-scalar.</summary>
        public float ScienceAwarded;

        /// <summary>How the science was collected.</summary>
        public ScienceMethod Method;

        /// <summary>Transmission efficiency for the experiment (0.0 to 1.0).</summary>
        public float TransmitScalar;

        /// <summary>Total science this subject can yield (scienceCap).</summary>
        public float SubjectMaxValue;

        // ---- Science spending fields ----

        /// <summary>Tech tree node ID, e.g. "survivability".</summary>
        public string NodeId;

        /// <summary>Cost in science points or funds (context-dependent on action type).</summary>
        public float Cost;

        // ---- Funds fields ----

        /// <summary>Funds earned (immutable).</summary>
        public float FundsAwarded;

        /// <summary>Source of fund earnings.</summary>
        public FundsSource FundsSourceField;

        /// <summary>Funds spent (immutable).</summary>
        public float FundsSpent;

        /// <summary>Source of fund spending.</summary>
        public FundsSpendingSource FundsSpendingSourceField;

        // ---- Reputation fields ----

        /// <summary>Nominal reputation earned before curve (immutable).</summary>
        public float NominalRep;

        /// <summary>Source of reputation earning.</summary>
        public ReputationSource RepSource;

        /// <summary>Nominal reputation penalty before curve (immutable).</summary>
        public float NominalPenalty;

        /// <summary>Source of reputation penalty.</summary>
        public ReputationPenaltySource RepPenaltySource;

        // ---- Milestone fields ----

        /// <summary>Milestone identifier, e.g. "FirstOrbitKerbin".</summary>
        public string MilestoneId;

        /// <summary>Funds awarded by the milestone (immutable, 0 in Science mode).</summary>
        public float MilestoneFundsAwarded;

        /// <summary>Reputation awarded by the milestone (immutable, 0 in Science mode).</summary>
        public float MilestoneRepAwarded;

        // ---- Contract fields ----

        /// <summary>KSP's unique contract instance ID.</summary>
        public string ContractId;

        /// <summary>Contract type, e.g. "ExploreBody".</summary>
        public string ContractType;

        /// <summary>Human-readable contract title.</summary>
        public string ContractTitle;

        /// <summary>Advance payment received on accept.</summary>
        public float AdvanceFunds;

        /// <summary>Expiration UT. NaN if no deadline.</summary>
        public float DeadlineUT = float.NaN;

        /// <summary>Funds reward on completion.</summary>
        public float FundsReward;

        /// <summary>Reputation reward on completion (nominal, pre-curve).</summary>
        public float RepReward;

        /// <summary>Science reward on completion.</summary>
        public float ScienceReward;

        /// <summary>Funds penalty on fail/cancel.</summary>
        public float FundsPenalty;

        /// <summary>Reputation penalty on fail/cancel (nominal, pre-curve).</summary>
        public float RepPenalty;

        // ---- Kerbal fields ----

        /// <summary>Kerbal's full name.</summary>
        public string KerbalName;

        /// <summary>Kerbal's role/class (Pilot/Engineer/Scientist).</summary>
        public string KerbalRole;

        /// <summary>Mission start UT.</summary>
        public float StartUT;

        /// <summary>Mission end UT. NaN if stranded (open-ended).</summary>
        public float EndUT = float.NaN;

        /// <summary>Kerbal's end state for this assignment.</summary>
        public KerbalEndState KerbalEndStateField;

        /// <summary>XP earned during this recording.</summary>
        public float XpGained;

        /// <summary>Funds spent to hire this kerbal (career only).</summary>
        public float HireCost;

        /// <summary>Name of the kerbal this stand-in replaces.</summary>
        public string ReplacesKerbal;

        /// <summary>Stand-in kerbal's courage (randomized).</summary>
        public float Courage;

        /// <summary>Stand-in kerbal's stupidity (randomized).</summary>
        public float Stupidity;

        // ---- Facility fields ----

        /// <summary>Facility identifier, e.g. "LaunchPad".</summary>
        public string FacilityId;

        /// <summary>Target level after upgrade (2 or 3).</summary>
        public int ToLevel;

        /// <summary>Funds cost for facility upgrade or repair.</summary>
        public float FacilityCost;

        // ---- Strategy fields ----

        /// <summary>Strategy identifier, e.g. "UnpaidResearch".</summary>
        public string StrategyId;

        /// <summary>Resource being diverted from.</summary>
        public StrategyResource SourceResource;

        /// <summary>Resource being diverted to.</summary>
        public StrategyResource TargetResource;

        /// <summary>Diversion percentage (0.01 to 0.25).</summary>
        public float Commitment;

        /// <summary>One-time cost in source resource on activation.</summary>
        public float SetupCost;

        // ---- Funds initial (seed) ----

        /// <summary>Career starting funds, extracted from save file.</summary>
        public float InitialFunds;

        // ================================================================
        // Serialization
        // ================================================================

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private static readonly NumberStyles NS = NumberStyles.Float;

        /// <summary>
        /// Serializes this action into a GAME_ACTION ConfigNode under the given parent.
        /// Only writes fields relevant to the action type — does not write nulls or defaults.
        /// </summary>
        public void SerializeInto(ConfigNode parent)
        {
            ConfigNode node = parent.AddNode("GAME_ACTION");
            node.AddValue("ut", UT.ToString("R", IC));
            node.AddValue("type", ((int)Type).ToString(IC));

            if (RecordingId != null)
                node.AddValue("recordingId", RecordingId);
            if (Sequence != 0)
                node.AddValue("seq", Sequence.ToString(IC));

            switch (Type)
            {
                case GameActionType.ScienceEarning:
                    SerializeScienceEarning(node);
                    break;
                case GameActionType.ScienceSpending:
                    SerializeScienceSpending(node);
                    break;
                case GameActionType.FundsEarning:
                    SerializeFundsEarning(node);
                    break;
                case GameActionType.FundsSpending:
                    SerializeFundsSpending(node);
                    break;
                case GameActionType.MilestoneAchievement:
                    SerializeMilestone(node);
                    break;
                case GameActionType.ContractAccept:
                    SerializeContractAccept(node);
                    break;
                case GameActionType.ContractComplete:
                    SerializeContractComplete(node);
                    break;
                case GameActionType.ContractFail:
                    SerializeContractFail(node);
                    break;
                case GameActionType.ContractCancel:
                    SerializeContractCancel(node);
                    break;
                case GameActionType.ReputationEarning:
                    SerializeRepEarning(node);
                    break;
                case GameActionType.ReputationPenalty:
                    SerializeRepPenalty(node);
                    break;
                case GameActionType.KerbalAssignment:
                    SerializeKerbalAssignment(node);
                    break;
                case GameActionType.KerbalHire:
                    SerializeKerbalHire(node);
                    break;
                case GameActionType.KerbalRescue:
                    SerializeKerbalRescue(node);
                    break;
                case GameActionType.KerbalStandIn:
                    SerializeKerbalStandIn(node);
                    break;
                case GameActionType.FacilityUpgrade:
                    SerializeFacilityUpgrade(node);
                    break;
                case GameActionType.FacilityDestruction:
                    SerializeFacilityDestruction(node);
                    break;
                case GameActionType.FacilityRepair:
                    SerializeFacilityRepair(node);
                    break;
                case GameActionType.StrategyActivate:
                    SerializeStrategyActivate(node);
                    break;
                case GameActionType.StrategyDeactivate:
                    SerializeStrategyDeactivate(node);
                    break;
                case GameActionType.FundsInitial:
                    SerializeFundsInitial(node);
                    break;
            }
        }

        /// <summary>
        /// Deserializes a GameAction from a GAME_ACTION ConfigNode.
        /// Unknown fields are silently ignored for forward compatibility.
        /// </summary>
        public static GameAction DeserializeFrom(ConfigNode node)
        {
            var a = new GameAction();

            string utStr = node.GetValue("ut");
            if (utStr != null)
                double.TryParse(utStr, NS, IC, out a.UT);

            string typeStr = node.GetValue("type");
            if (typeStr != null)
            {
                int typeInt;
                if (int.TryParse(typeStr, NumberStyles.Integer, IC, out typeInt))
                {
                    if (Enum.IsDefined(typeof(GameActionType), typeInt))
                        a.Type = (GameActionType)typeInt;
                    else
                        ParsekLog.Warn("GameAction", $"Unknown action type id '{typeInt}' while deserializing");
                }
            }

            a.RecordingId = node.GetValue("recordingId");

            string seqStr = node.GetValue("seq");
            if (seqStr != null)
                int.TryParse(seqStr, NumberStyles.Integer, IC, out a.Sequence);

            switch (a.Type)
            {
                case GameActionType.ScienceEarning:
                    DeserializeScienceEarning(node, a);
                    break;
                case GameActionType.ScienceSpending:
                    DeserializeScienceSpending(node, a);
                    break;
                case GameActionType.FundsEarning:
                    DeserializeFundsEarning(node, a);
                    break;
                case GameActionType.FundsSpending:
                    DeserializeFundsSpending(node, a);
                    break;
                case GameActionType.MilestoneAchievement:
                    DeserializeMilestone(node, a);
                    break;
                case GameActionType.ContractAccept:
                    DeserializeContractAccept(node, a);
                    break;
                case GameActionType.ContractComplete:
                    DeserializeContractComplete(node, a);
                    break;
                case GameActionType.ContractFail:
                    DeserializeContractFail(node, a);
                    break;
                case GameActionType.ContractCancel:
                    DeserializeContractCancel(node, a);
                    break;
                case GameActionType.ReputationEarning:
                    DeserializeRepEarning(node, a);
                    break;
                case GameActionType.ReputationPenalty:
                    DeserializeRepPenalty(node, a);
                    break;
                case GameActionType.KerbalAssignment:
                    DeserializeKerbalAssignment(node, a);
                    break;
                case GameActionType.KerbalHire:
                    DeserializeKerbalHire(node, a);
                    break;
                case GameActionType.KerbalRescue:
                    DeserializeKerbalRescue(node, a);
                    break;
                case GameActionType.KerbalStandIn:
                    DeserializeKerbalStandIn(node, a);
                    break;
                case GameActionType.FacilityUpgrade:
                    DeserializeFacilityUpgrade(node, a);
                    break;
                case GameActionType.FacilityDestruction:
                    DeserializeFacilityDestruction(node, a);
                    break;
                case GameActionType.FacilityRepair:
                    DeserializeFacilityRepair(node, a);
                    break;
                case GameActionType.StrategyActivate:
                    DeserializeStrategyActivate(node, a);
                    break;
                case GameActionType.StrategyDeactivate:
                    DeserializeStrategyDeactivate(node, a);
                    break;
                case GameActionType.FundsInitial:
                    DeserializeFundsInitial(node, a);
                    break;
            }

            return a;
        }

        // ---- Per-type serialization helpers ----

        private void SerializeScienceEarning(ConfigNode n)
        {
            if (SubjectId != null) n.AddValue("subjectId", SubjectId);
            if (ExperimentId != null) n.AddValue("experimentId", ExperimentId);
            if (Body != null) n.AddValue("body", Body);
            if (Situation != null) n.AddValue("situation", Situation);
            if (Biome != null) n.AddValue("biome", Biome);
            n.AddValue("scienceAwarded", ScienceAwarded.ToString("R", IC));
            n.AddValue("method", ((int)Method).ToString(IC));
            n.AddValue("transmitScalar", TransmitScalar.ToString("R", IC));
            n.AddValue("subjectMaxValue", SubjectMaxValue.ToString("R", IC));
        }

        private static void DeserializeScienceEarning(ConfigNode n, GameAction a)
        {
            a.SubjectId = n.GetValue("subjectId");
            a.ExperimentId = n.GetValue("experimentId");
            a.Body = n.GetValue("body");
            a.Situation = n.GetValue("situation");
            a.Biome = n.GetValue("biome");
            TryParseFloat(n, "scienceAwarded", out a.ScienceAwarded);
            int methodInt;
            if (TryParseInt(n, "method", out methodInt) && Enum.IsDefined(typeof(ScienceMethod), methodInt))
                a.Method = (ScienceMethod)methodInt;
            TryParseFloat(n, "transmitScalar", out a.TransmitScalar);
            TryParseFloat(n, "subjectMaxValue", out a.SubjectMaxValue);
        }

        private void SerializeScienceSpending(ConfigNode n)
        {
            if (NodeId != null) n.AddValue("nodeId", NodeId);
            n.AddValue("cost", Cost.ToString("R", IC));
        }

        private static void DeserializeScienceSpending(ConfigNode n, GameAction a)
        {
            a.NodeId = n.GetValue("nodeId");
            TryParseFloat(n, "cost", out a.Cost);
        }

        private void SerializeFundsEarning(ConfigNode n)
        {
            n.AddValue("fundsAwarded", FundsAwarded.ToString("R", IC));
            n.AddValue("fundsSource", ((int)FundsSourceField).ToString(IC));
        }

        private static void DeserializeFundsEarning(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "fundsAwarded", out a.FundsAwarded);
            int srcInt;
            if (TryParseInt(n, "fundsSource", out srcInt) && Enum.IsDefined(typeof(FundsSource), srcInt))
                a.FundsSourceField = (FundsSource)srcInt;
        }

        private void SerializeFundsSpending(ConfigNode n)
        {
            n.AddValue("fundsSpent", FundsSpent.ToString("R", IC));
            n.AddValue("fundsSpendingSource", ((int)FundsSpendingSourceField).ToString(IC));
        }

        private static void DeserializeFundsSpending(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "fundsSpent", out a.FundsSpent);
            int srcInt;
            if (TryParseInt(n, "fundsSpendingSource", out srcInt) && Enum.IsDefined(typeof(FundsSpendingSource), srcInt))
                a.FundsSpendingSourceField = (FundsSpendingSource)srcInt;
        }

        private void SerializeMilestone(ConfigNode n)
        {
            if (MilestoneId != null) n.AddValue("milestoneId", MilestoneId);
            n.AddValue("milestoneFundsAwarded", MilestoneFundsAwarded.ToString("R", IC));
            n.AddValue("milestoneRepAwarded", MilestoneRepAwarded.ToString("R", IC));
        }

        private static void DeserializeMilestone(ConfigNode n, GameAction a)
        {
            a.MilestoneId = n.GetValue("milestoneId");
            TryParseFloat(n, "milestoneFundsAwarded", out a.MilestoneFundsAwarded);
            TryParseFloat(n, "milestoneRepAwarded", out a.MilestoneRepAwarded);
        }

        private void SerializeContractAccept(ConfigNode n)
        {
            if (ContractId != null) n.AddValue("contractId", ContractId);
            if (ContractType != null) n.AddValue("contractType", ContractType);
            if (ContractTitle != null) n.AddValue("contractTitle", ContractTitle);
            n.AddValue("advanceFunds", AdvanceFunds.ToString("R", IC));
            if (!float.IsNaN(DeadlineUT))
                n.AddValue("deadlineUT", DeadlineUT.ToString("R", IC));
        }

        private static void DeserializeContractAccept(ConfigNode n, GameAction a)
        {
            a.ContractId = n.GetValue("contractId");
            a.ContractType = n.GetValue("contractType");
            a.ContractTitle = n.GetValue("contractTitle");
            TryParseFloat(n, "advanceFunds", out a.AdvanceFunds);
            if (!TryParseFloat(n, "deadlineUT", out a.DeadlineUT))
                a.DeadlineUT = float.NaN;
        }

        private void SerializeContractComplete(ConfigNode n)
        {
            if (ContractId != null) n.AddValue("contractId", ContractId);
            n.AddValue("fundsReward", FundsReward.ToString("R", IC));
            n.AddValue("repReward", RepReward.ToString("R", IC));
            n.AddValue("scienceReward", ScienceReward.ToString("R", IC));
        }

        private static void DeserializeContractComplete(ConfigNode n, GameAction a)
        {
            a.ContractId = n.GetValue("contractId");
            TryParseFloat(n, "fundsReward", out a.FundsReward);
            TryParseFloat(n, "repReward", out a.RepReward);
            TryParseFloat(n, "scienceReward", out a.ScienceReward);
        }

        private void SerializeContractFail(ConfigNode n)
        {
            if (ContractId != null) n.AddValue("contractId", ContractId);
            n.AddValue("fundsPenalty", FundsPenalty.ToString("R", IC));
            n.AddValue("repPenalty", RepPenalty.ToString("R", IC));
        }

        private static void DeserializeContractFail(ConfigNode n, GameAction a)
        {
            a.ContractId = n.GetValue("contractId");
            TryParseFloat(n, "fundsPenalty", out a.FundsPenalty);
            TryParseFloat(n, "repPenalty", out a.RepPenalty);
        }

        private void SerializeContractCancel(ConfigNode n)
        {
            if (ContractId != null) n.AddValue("contractId", ContractId);
            n.AddValue("fundsPenalty", FundsPenalty.ToString("R", IC));
            n.AddValue("repPenalty", RepPenalty.ToString("R", IC));
        }

        private static void DeserializeContractCancel(ConfigNode n, GameAction a)
        {
            a.ContractId = n.GetValue("contractId");
            TryParseFloat(n, "fundsPenalty", out a.FundsPenalty);
            TryParseFloat(n, "repPenalty", out a.RepPenalty);
        }

        private void SerializeRepEarning(ConfigNode n)
        {
            n.AddValue("nominalRep", NominalRep.ToString("R", IC));
            n.AddValue("repSource", ((int)RepSource).ToString(IC));
        }

        private static void DeserializeRepEarning(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "nominalRep", out a.NominalRep);
            int srcInt;
            if (TryParseInt(n, "repSource", out srcInt) && Enum.IsDefined(typeof(ReputationSource), srcInt))
                a.RepSource = (ReputationSource)srcInt;
        }

        private void SerializeRepPenalty(ConfigNode n)
        {
            n.AddValue("nominalPenalty", NominalPenalty.ToString("R", IC));
            n.AddValue("repPenaltySource", ((int)RepPenaltySource).ToString(IC));
        }

        private static void DeserializeRepPenalty(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "nominalPenalty", out a.NominalPenalty);
            int srcInt;
            if (TryParseInt(n, "repPenaltySource", out srcInt) && Enum.IsDefined(typeof(ReputationPenaltySource), srcInt))
                a.RepPenaltySource = (ReputationPenaltySource)srcInt;
        }

        private void SerializeKerbalAssignment(ConfigNode n)
        {
            if (KerbalName != null) n.AddValue("kerbalName", KerbalName);
            if (KerbalRole != null) n.AddValue("kerbalRole", KerbalRole);
            n.AddValue("startUT", StartUT.ToString("R", IC));
            if (!float.IsNaN(EndUT))
                n.AddValue("endUT", EndUT.ToString("R", IC));
            n.AddValue("endState", ((int)KerbalEndStateField).ToString(IC));
            n.AddValue("xpGained", XpGained.ToString("R", IC));
        }

        private static void DeserializeKerbalAssignment(ConfigNode n, GameAction a)
        {
            a.KerbalName = n.GetValue("kerbalName");
            a.KerbalRole = n.GetValue("kerbalRole");
            TryParseFloat(n, "startUT", out a.StartUT);
            if (!TryParseFloat(n, "endUT", out a.EndUT))
                a.EndUT = float.NaN;
            int stateInt;
            if (TryParseInt(n, "endState", out stateInt) && Enum.IsDefined(typeof(KerbalEndState), stateInt))
                a.KerbalEndStateField = (KerbalEndState)stateInt;
            TryParseFloat(n, "xpGained", out a.XpGained);
        }

        private void SerializeKerbalHire(ConfigNode n)
        {
            if (KerbalName != null) n.AddValue("kerbalName", KerbalName);
            if (KerbalRole != null) n.AddValue("kerbalRole", KerbalRole);
            n.AddValue("hireCost", HireCost.ToString("R", IC));
        }

        private static void DeserializeKerbalHire(ConfigNode n, GameAction a)
        {
            a.KerbalName = n.GetValue("kerbalName");
            a.KerbalRole = n.GetValue("kerbalRole");
            TryParseFloat(n, "hireCost", out a.HireCost);
        }

        private void SerializeKerbalRescue(ConfigNode n)
        {
            if (KerbalName != null) n.AddValue("kerbalName", KerbalName);
            if (KerbalRole != null) n.AddValue("kerbalRole", KerbalRole);
            n.AddValue("endUT", EndUT.ToString("R", IC));
        }

        private static void DeserializeKerbalRescue(ConfigNode n, GameAction a)
        {
            a.KerbalName = n.GetValue("kerbalName");
            a.KerbalRole = n.GetValue("kerbalRole");
            TryParseFloat(n, "endUT", out a.EndUT);
        }

        private void SerializeKerbalStandIn(ConfigNode n)
        {
            if (KerbalName != null) n.AddValue("kerbalName", KerbalName);
            if (KerbalRole != null) n.AddValue("kerbalRole", KerbalRole);
            if (ReplacesKerbal != null) n.AddValue("replacesKerbal", ReplacesKerbal);
            n.AddValue("courage", Courage.ToString("R", IC));
            n.AddValue("stupidity", Stupidity.ToString("R", IC));
        }

        private static void DeserializeKerbalStandIn(ConfigNode n, GameAction a)
        {
            a.KerbalName = n.GetValue("kerbalName");
            a.KerbalRole = n.GetValue("kerbalRole");
            a.ReplacesKerbal = n.GetValue("replacesKerbal");
            TryParseFloat(n, "courage", out a.Courage);
            TryParseFloat(n, "stupidity", out a.Stupidity);
        }

        private void SerializeFacilityUpgrade(ConfigNode n)
        {
            if (FacilityId != null) n.AddValue("facilityId", FacilityId);
            n.AddValue("toLevel", ToLevel.ToString(IC));
            n.AddValue("facilityCost", FacilityCost.ToString("R", IC));
        }

        private static void DeserializeFacilityUpgrade(ConfigNode n, GameAction a)
        {
            a.FacilityId = n.GetValue("facilityId");
            TryParseInt(n, "toLevel", out a.ToLevel);
            TryParseFloat(n, "facilityCost", out a.FacilityCost);
        }

        private void SerializeFacilityDestruction(ConfigNode n)
        {
            if (FacilityId != null) n.AddValue("facilityId", FacilityId);
        }

        private static void DeserializeFacilityDestruction(ConfigNode n, GameAction a)
        {
            a.FacilityId = n.GetValue("facilityId");
        }

        private void SerializeFacilityRepair(ConfigNode n)
        {
            if (FacilityId != null) n.AddValue("facilityId", FacilityId);
            n.AddValue("facilityCost", FacilityCost.ToString("R", IC));
        }

        private static void DeserializeFacilityRepair(ConfigNode n, GameAction a)
        {
            a.FacilityId = n.GetValue("facilityId");
            TryParseFloat(n, "facilityCost", out a.FacilityCost);
        }

        private void SerializeStrategyActivate(ConfigNode n)
        {
            if (StrategyId != null) n.AddValue("strategyId", StrategyId);
            n.AddValue("sourceResource", ((int)SourceResource).ToString(IC));
            n.AddValue("targetResource", ((int)TargetResource).ToString(IC));
            n.AddValue("commitment", Commitment.ToString("R", IC));
            n.AddValue("setupCost", SetupCost.ToString("R", IC));
        }

        private static void DeserializeStrategyActivate(ConfigNode n, GameAction a)
        {
            a.StrategyId = n.GetValue("strategyId");
            int srcInt;
            if (TryParseInt(n, "sourceResource", out srcInt) && Enum.IsDefined(typeof(StrategyResource), srcInt))
                a.SourceResource = (StrategyResource)srcInt;
            int tgtInt;
            if (TryParseInt(n, "targetResource", out tgtInt) && Enum.IsDefined(typeof(StrategyResource), tgtInt))
                a.TargetResource = (StrategyResource)tgtInt;
            TryParseFloat(n, "commitment", out a.Commitment);
            TryParseFloat(n, "setupCost", out a.SetupCost);
        }

        private void SerializeStrategyDeactivate(ConfigNode n)
        {
            if (StrategyId != null) n.AddValue("strategyId", StrategyId);
        }

        private static void DeserializeStrategyDeactivate(ConfigNode n, GameAction a)
        {
            a.StrategyId = n.GetValue("strategyId");
        }

        private void SerializeFundsInitial(ConfigNode n)
        {
            n.AddValue("initialFunds", InitialFunds.ToString("R", IC));
        }

        private static void DeserializeFundsInitial(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "initialFunds", out a.InitialFunds);
        }

        // ---- Parse helpers ----

        private static bool TryParseFloat(ConfigNode n, string key, out float result)
        {
            result = 0f;
            string val = n.GetValue(key);
            if (val == null) return false;
            return float.TryParse(val, NS, IC, out result);
        }

        private static bool TryParseInt(ConfigNode n, string key, out int result)
        {
            result = 0;
            string val = n.GetValue(key);
            if (val == null) return false;
            return int.TryParse(val, NumberStyles.Integer, IC, out result);
        }
    }
}
