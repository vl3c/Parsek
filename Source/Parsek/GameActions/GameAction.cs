using System;
using System.Collections.Generic;
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
        FundsInitial          = 20,
        ScienceInitial        = 21,
        ReputationInitial     = 22,

        // ---- Route actions (logistics supply routes; design doc §6 / §10) ----

        /// <summary>
        /// Scheduler decided the cycle is good to go after destination + origin + funds
        /// checks (design doc §6.1 step 5). Carries <c>RouteId</c>, <c>RouteCycleId</c>,
        /// and the scheduled dispatch UT. Resumes immediately after a <see cref="RoutePaused"/>
        /// row — there is no explicit RouteUnpaused/RouteResumed type because the next
        /// RouteDispatched IS the resumption signal.
        /// </summary>
        RouteDispatched       = 23,

        /// <summary>
        /// Physical/funds debit applied to origin (design doc §6.1 step 5 / §6.3): non-KSC
        /// resource or inventory removal, or KSC funds charge in Career. Separated from
        /// <see cref="RouteDispatched"/> so a future module can sequence the actual debit
        /// at a different tier slot from the dispatch decision (e.g. so the funds module
        /// can reuse its existing ContractFail-style penalty path for the KSC charge while
        /// the route module owns the dispatch counter).
        /// </summary>
        RouteCargoDebited     = 24,

        /// <summary>
        /// Delivery boundary reached (design doc §6.3). Carries the actual per-resource
        /// delivery manifest. For partial-fill (§10.5) the requested manifest is also
        /// carried so the player can see requested-vs-actual instead of silent loss.
        /// </summary>
        RouteCargoDelivered   = 25,

        /// <summary>
        /// Player Pause action, OR auto-pause when status transitions to EndpointLost /
        /// MissingSourceRecording / SourceChanged (design doc §6.6, §10.6). The reason is
        /// captured in <see cref="GameAction.RouteEndpointReason"/>. §10.6 needs this row
        /// in the timeline so revert past a dispatch can correctly suspend future cycles.
        /// </summary>
        RoutePaused           = 26,

        /// <summary>
        /// Endpoint resolution failed (design doc §10.1, §10.2). Distinct from
        /// <see cref="RoutePaused"/> because the recovery contract differs — endpoint
        /// loss may auto-recover through surface-proximity fallback while a player pause
        /// can only be cleared by explicit unpause. Reason text is in
        /// <see cref="GameAction.RouteEndpointReason"/>.
        /// </summary>
        RouteEndpointLost     = 27
    }

    /// <summary>How science was collected — transmitted from orbit or recovered on the ground.</summary>
    public enum ScienceMethod
    {
        Transmitted = 0,
        Recovered   = 1
    }

    /// <summary>Where fund earnings came from.</summary>
    public enum FundsEarningSource
    {
        ContractComplete = 0,
        ContractAdvance  = 1,
        Recovery         = 2,
        Milestone        = 3,
        Other            = 4,
        /// <summary>
        /// Synthetic earning injected by <see cref="LedgerOrchestrator.MigrateLegacyTreeResources"/>
        /// on load to reconcile a pre-Phase-F tree's persisted legacy funds residual against the
        /// ledger. Tag-only — <see cref="FundsModule"/> treats it as a normal earning via its
        /// default branch.
        /// </summary>
        LegacyMigration  = 5
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

        /// <summary>
        /// Stable immutable identifier for this action (design doc section 5.6 +
        /// section 9). New actions auto-assign <c>"act_" + Guid.NewGuid("N")</c>
        /// at construction. Load-time migration re-hydrates a deterministic id
        /// for pre-feature actions via
        /// <see cref="ComputeLegacyActionId(double, GameActionType, string, int)"/>.
        /// Referenced by <see cref="LedgerTombstone.ActionId"/>.
        /// </summary>
        public string ActionId = "act_" + Guid.NewGuid().ToString("N");

        /// <summary>Universal time when the action occurred.</summary>
        public double UT;

        /// <summary>Discriminator — determines which fields are populated.</summary>
        public GameActionType Type;

        /// <summary>Recording that produced this action. Null for KSC spending actions and system-generated actions.</summary>
        public string RecordingId;

        /// <summary>Ordering within the same UT for spending actions. 0 for earnings.</summary>
        public int Sequence;

        // ---- Derived fields (recalculated, NOT serialized) ----

        /// <summary>
        /// Whether this action's effects are active after recalculation.
        /// Set by first-tier modules (e.g., MilestonesModule sets false for duplicate milestones,
        /// ContractsModule sets false for duplicate completions). Defaults to true.
        /// NOT serialized — recomputed from scratch on every recalculation walk.
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
        public FundsEarningSource FundsSource;

        /// <summary>Funds spent (immutable).</summary>
        public float FundsSpent;

        /// <summary>Source of fund spending.</summary>
        public FundsSpendingSource FundsSpendingSource;

        /// <summary>
        /// Optional secondary dedup discriminator. Populated for action types whose
        /// natural key (<see cref="RecordingId"/>) collides at near-identical UTs —
        /// notably <see cref="FundsSpendingSource.Other"/> part purchases (part name)
        /// and <see cref="FundsEarningSource.Recovery"/> payouts (paired recovery-event
        /// fingerprint). Serialized for both <see cref="FundsEarning"/> and
        /// <see cref="FundsSpending"/> so save/load preserves the same dedup identity.
        /// See <see cref="LedgerOrchestrator.GetActionKey"/>.
        /// </summary>
        public string DedupKey;

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

        /// <summary>Science awarded by the milestone (immutable, 0 in pure-funds/rep milestones).
        /// Consumed by <see cref="ScienceModule.ProcessMilestoneScienceReward"/> so first-
        /// reached milestones credit the R&amp;D pool. Without this field, the sci= value
        /// recorded in the event detail was silently dropped at convert time.</summary>
        public float MilestoneScienceAwarded;

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

        /// <summary>One-time science cost on activation.</summary>
        public float SetupScienceCost;

        /// <summary>One-time reputation cost on activation.</summary>
        public float SetupReputationCost;

        // ---- Route fields ----

        /// <summary>
        /// Stable identifier of the logistics route this action belongs to
        /// (design doc §6, §10). Null on non-route actions. Skeleton-only:
        /// route entities themselves are not yet defined in the codebase, so
        /// this is treated as opaque string identity.
        /// </summary>
        public string RouteId;

        /// <summary>
        /// Per-dispatch cycle identifier — groups one
        /// <see cref="GameActionType.RouteDispatched"/> row with its matching
        /// <see cref="GameActionType.RouteCargoDebited"/> and
        /// <see cref="GameActionType.RouteCargoDelivered"/> rows. Used by future
        /// dispatch/delivery walkers to correlate within-cycle effects. Null on
        /// non-cycle-scoped route actions (e.g. <see cref="GameActionType.RoutePaused"/>
        /// at the route level, not a specific cycle).
        /// </summary>
        public string RouteCycleId;

        /// <summary>
        /// 0-based stop index inside the route's stop list (design doc §6.3). Sentinel
        /// value -1 means "not applicable / route-level event". v1 routes have a single
        /// stop (§11), so the only non-sentinel value in v1 will be 0. Persisted as a
        /// non-negative integer so future multi-stop routes can populate it without a
        /// schema change.
        /// </summary>
        public int RouteStopIndex = -1;

        /// <summary>
        /// Per-resource signed delivered/debited amount keyed by stock resource name
        /// (design doc §6.3, §6.5). For <see cref="GameActionType.RouteCargoDelivered"/>
        /// the value is positive amount actually delivered to the destination
        /// (post-clamp by <c>maxAmount</c>). For <see cref="GameActionType.RouteCargoDebited"/>
        /// the value is positive amount removed from the origin. Both directions
        /// intentionally store positive magnitudes — the action type carries the sign.
        /// <para>
        /// <c>maxAmount</c> is deliberately NOT carried — delivery is amount-only
        /// (design doc §11, §6.5). Tank capacity is a destination property and is
        /// re-read each tick by the scheduler; carrying it on the ledger row would
        /// be a stale snapshot.
        /// </para>
        /// <para>Null or empty when the action carries no resource manifest (e.g. the
        /// KSC-funds-only debit case, where <see cref="RouteKscFundsCost"/> is set instead).</para>
        /// </summary>
        public Dictionary<string, double> RouteResourceManifest;

        /// <summary>
        /// Requested per-resource delivery amounts, populated only on
        /// <see cref="GameActionType.RouteCargoDelivered"/> rows where the actual delivery
        /// was partially filled (design doc §10.5). Same keying as
        /// <see cref="RouteResourceManifest"/>. Null when the actual delivery met the
        /// request in full — saves a few bytes on the common case. The pair
        /// (requested, actual) is what UI / future dispatch tuning reads to expose
        /// "delivered X / Y" badges.
        /// </summary>
        public Dictionary<string, double> RouteRequestedResourceManifest;

        /// <summary>
        /// KSC funds charge in funds-units (design doc §6.1 step 5: the Career-mode
        /// KSC-origin dispatch cost). Zero when the dispatch had no KSC funds component
        /// (Science / Sandbox modes or non-KSC origins). Stored on
        /// <see cref="GameActionType.RouteCargoDebited"/> rows.
        /// </summary>
        public float RouteKscFundsCost;

        /// <summary>
        /// Short human/machine-readable reason for a
        /// <see cref="GameActionType.RoutePaused"/> or
        /// <see cref="GameActionType.RouteEndpointLost"/> row (design doc §6.6, §10.1,
        /// §10.2, §10.15, §10.16). Typical values: <c>"PlayerPause"</c>,
        /// <c>"AutoPause:EndpointLost"</c>, <c>"AutoPause:MissingSourceRecording"</c>,
        /// <c>"AutoPause:SourceChanged"</c>, <c>"EndpointLost:OrbitalNoFallback"</c>.
        /// Free-form by design — the route module logs it but does not branch on it,
        /// so adding new reasons is a non-breaking change.
        /// </summary>
        public string RouteEndpointReason;

        // ---- Initial seed fields ----

        /// <summary>Career starting funds, extracted from save file.</summary>
        public float InitialFunds;

        /// <summary>Existing science balance when Parsek is first installed mid-career.</summary>
        public float InitialScience;

        /// <summary>Existing reputation when Parsek is first installed mid-career.</summary>
        public float InitialReputation;

        // ================================================================
        // Derived fields — set during recalculation walk, NOT serialized
        // ================================================================

        /// <summary>
        /// Science actually credited after applying subject cap headroom.
        /// Set by ScienceModule during recalculation walk. Always derived, never stored.
        /// </summary>
        public float EffectiveScience;

        /// <summary>
        /// Whether a spending action was affordable at the time it was processed in the walk.
        /// Set by resource modules during recalculation walk. Always derived, never stored.
        /// </summary>
        public bool Affordable;

        /// <summary>
        /// Actual reputation change after applying the gain/loss curve against running rep.
        /// Set by ReputationModule during recalculation walk. Positive for gains, negative for losses.
        /// Always derived, never stored.
        /// </summary>
        public float EffectiveRep;

        /// <summary>Transformed funds reward after strategy application (derived, not serialized).</summary>
        public float TransformedFundsReward;
        /// <summary>Transformed science reward after strategy application (derived, not serialized).</summary>
        public float TransformedScienceReward;
        /// <summary>Transformed rep reward after strategy application (derived, not serialized).</summary>
        public float TransformedRepReward;

        // ================================================================
        // Serialization
        // ================================================================

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private static readonly NumberStyles NS = NumberStyles.Float;

        /// <summary>
        /// Computes a deterministic legacy <see cref="ActionId"/> for
        /// pre-Rewind-to-Staging actions that lack a persisted id
        /// (design doc section 5.6 + 9). The hash input is the concatenation
        /// <c>UT.ToString("R", InvariantCulture) + "|" + Type + "|" +
        /// (RecordingId ?? "") + "|" + Sequence</c>; the output is
        /// <c>"act_legacy_" + first 16 hex chars of SHA1(input)</c>. Idempotent:
        /// the same inputs always produce the same id, so repeated loads do not
        /// drift.
        /// </summary>
        internal static string ComputeLegacyActionId(double ut, GameActionType type, string recordingId, int sequence)
        {
            string input = ut.ToString("R", IC) + "|"
                + type.ToString() + "|"
                + (recordingId ?? "") + "|"
                + sequence.ToString(IC);
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
                var sb = new System.Text.StringBuilder("act_legacy_", 11 + 16);
                int take = System.Math.Min(8, hash.Length); // 8 bytes = 16 hex chars
                for (int i = 0; i < take; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Serializes this action into a GAME_ACTION ConfigNode under the given parent.
        /// Only writes fields relevant to the action type — does not write nulls or defaults.
        /// </summary>
        public void SerializeInto(ConfigNode parent)
        {
            ConfigNode node = parent.AddNode("GAME_ACTION");
            node.AddValue("ut", UT.ToString("R", IC));
            node.AddValue("type", ((int)Type).ToString(IC));

            // Rewind-to-Staging (design section 5.6) — every action has an ActionId.
            // Auto-assigned at construction for new actions; deterministically rehydrated
            // on load for legacy actions (see DeserializeFrom + ComputeLegacyActionId).
            if (string.IsNullOrEmpty(ActionId))
                ActionId = "act_" + Guid.NewGuid().ToString("N");
            node.AddValue("actionId", ActionId);

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
                case GameActionType.ScienceInitial:
                    SerializeScienceInitial(node);
                    break;
                case GameActionType.ReputationInitial:
                    SerializeReputationInitial(node);
                    break;
                case GameActionType.RouteDispatched:
                    SerializeRouteDispatched(node);
                    break;
                case GameActionType.RouteCargoDebited:
                    SerializeRouteCargoDebited(node);
                    break;
                case GameActionType.RouteCargoDelivered:
                    SerializeRouteCargoDelivered(node);
                    break;
                case GameActionType.RoutePaused:
                    SerializeRoutePaused(node);
                    break;
                case GameActionType.RouteEndpointLost:
                    SerializeRouteEndpointLost(node);
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

            // Rewind-to-Staging (design section 5.6 + 9). Legacy actions without
            // `actionId` get a deterministic hash-based id so tombstones remain
            // stable across reloads. Counter bumped for the one-shot Info log.
            string actionIdStr = node.GetValue("actionId");
            if (!string.IsNullOrEmpty(actionIdStr))
            {
                a.ActionId = actionIdStr;
            }
            else
            {
                a.ActionId = ComputeLegacyActionId(a.UT, a.Type, a.RecordingId, a.Sequence);
                Ledger.BumpLegacyActionIdMigrationCounterForTesting();
            }

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
                case GameActionType.ScienceInitial:
                    DeserializeScienceInitial(node, a);
                    break;
                case GameActionType.ReputationInitial:
                    DeserializeReputationInitial(node, a);
                    break;
                case GameActionType.RouteDispatched:
                    DeserializeRouteDispatched(node, a);
                    break;
                case GameActionType.RouteCargoDebited:
                    DeserializeRouteCargoDebited(node, a);
                    break;
                case GameActionType.RouteCargoDelivered:
                    DeserializeRouteCargoDelivered(node, a);
                    break;
                case GameActionType.RoutePaused:
                    DeserializeRoutePaused(node, a);
                    break;
                case GameActionType.RouteEndpointLost:
                    DeserializeRouteEndpointLost(node, a);
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
            if (!float.IsNaN(EndUT))
            {
                n.AddValue("startUT", StartUT.ToString("R", IC));
                n.AddValue("endUT", EndUT.ToString("R", IC));
            }
        }

        private static void DeserializeScienceEarning(ConfigNode n, GameAction a)
        {
            a.SubjectId = n.GetValue("subjectId");
            a.ExperimentId = n.GetValue("experimentId");
            a.Body = n.GetValue("body");
            a.Situation = n.GetValue("situation");
            a.Biome = n.GetValue("biome");
            TryParseFloat(n, "scienceAwarded", out a.ScienceAwarded);
            TryParseEnum(n, "method", out a.Method);
            TryParseFloat(n, "transmitScalar", out a.TransmitScalar);
            TryParseFloat(n, "subjectMaxValue", out a.SubjectMaxValue);
            TryParseFloat(n, "startUT", out a.StartUT);
            if (!TryParseFloat(n, "endUT", out a.EndUT))
                a.EndUT = float.NaN;
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
            n.AddValue("fundsSource", ((int)FundsSource).ToString(IC));
            if (!string.IsNullOrEmpty(DedupKey))
                n.AddValue("dedupKey", DedupKey);
        }

        private static void DeserializeFundsEarning(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "fundsAwarded", out a.FundsAwarded);
            TryParseEnum(n, "fundsSource", out a.FundsSource);
            a.DedupKey = n.GetValue("dedupKey");
        }

        private void SerializeFundsSpending(ConfigNode n)
        {
            n.AddValue("fundsSpent", FundsSpent.ToString("R", IC));
            n.AddValue("fundsSpendingSource", ((int)FundsSpendingSource).ToString(IC));
            // DedupKey disambiguates same-UT KSC spendings (e.g. multiple part purchases
            // with recordingId=null) — must round-trip so reload doesn't re-collapse them.
            if (!string.IsNullOrEmpty(DedupKey))
                n.AddValue("dedupKey", DedupKey);
        }

        private static void DeserializeFundsSpending(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "fundsSpent", out a.FundsSpent);
            TryParseEnum(n, "fundsSpendingSource", out a.FundsSpendingSource);
            a.DedupKey = n.GetValue("dedupKey");
        }

        private void SerializeMilestone(ConfigNode n)
        {
            if (MilestoneId != null) n.AddValue("milestoneId", MilestoneId);
            n.AddValue("milestoneFundsAwarded", MilestoneFundsAwarded.ToString("R", IC));
            n.AddValue("milestoneRepAwarded", MilestoneRepAwarded.ToString("R", IC));
            n.AddValue("milestoneSciAwarded", MilestoneScienceAwarded.ToString("R", IC));
        }

        private static void DeserializeMilestone(ConfigNode n, GameAction a)
        {
            a.MilestoneId = n.GetValue("milestoneId");
            TryParseFloat(n, "milestoneFundsAwarded", out a.MilestoneFundsAwarded);
            TryParseFloat(n, "milestoneRepAwarded", out a.MilestoneRepAwarded);
            // Backward compat: pre-fix saves have no milestoneSciAwarded key; default to 0.
            TryParseFloat(n, "milestoneSciAwarded", out a.MilestoneScienceAwarded);
        }

        private void SerializeContractAccept(ConfigNode n)
        {
            if (ContractId != null) n.AddValue("contractId", ContractId);
            if (ContractType != null) n.AddValue("contractType", ContractType);
            if (ContractTitle != null) n.AddValue("contractTitle", ContractTitle);
            n.AddValue("advanceFunds", AdvanceFunds.ToString("R", IC));
            if (!float.IsNaN(DeadlineUT))
                n.AddValue("deadlineUT", DeadlineUT.ToString("R", IC));
            if (FundsPenalty != 0f)
                n.AddValue("fundsPenalty", FundsPenalty.ToString("R", IC));
            if (RepPenalty != 0f)
                n.AddValue("repPenalty", RepPenalty.ToString("R", IC));
        }

        private static void DeserializeContractAccept(ConfigNode n, GameAction a)
        {
            a.ContractId = n.GetValue("contractId");
            a.ContractType = n.GetValue("contractType");
            a.ContractTitle = n.GetValue("contractTitle");
            TryParseFloat(n, "advanceFunds", out a.AdvanceFunds);
            if (!TryParseFloat(n, "deadlineUT", out a.DeadlineUT))
                a.DeadlineUT = float.NaN;
            TryParseFloat(n, "fundsPenalty", out a.FundsPenalty);
            TryParseFloat(n, "repPenalty", out a.RepPenalty);
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

        private void SerializeContractFail(ConfigNode n) => SerializeContractPenalty(n);
        private static void DeserializeContractFail(ConfigNode n, GameAction a) => DeserializeContractPenalty(n, a);

        private void SerializeContractCancel(ConfigNode n) => SerializeContractPenalty(n);
        private static void DeserializeContractCancel(ConfigNode n, GameAction a) => DeserializeContractPenalty(n, a);

        private void SerializeContractPenalty(ConfigNode n)
        {
            if (ContractId != null) n.AddValue("contractId", ContractId);
            n.AddValue("fundsPenalty", FundsPenalty.ToString("R", IC));
            n.AddValue("repPenalty", RepPenalty.ToString("R", IC));
        }

        private static void DeserializeContractPenalty(ConfigNode n, GameAction a)
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
            TryParseEnum(n, "repSource", out a.RepSource);
        }

        private void SerializeRepPenalty(ConfigNode n)
        {
            n.AddValue("nominalPenalty", NominalPenalty.ToString("R", IC));
            n.AddValue("repPenaltySource", ((int)RepPenaltySource).ToString(IC));
        }

        private static void DeserializeRepPenalty(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "nominalPenalty", out a.NominalPenalty);
            TryParseEnum(n, "repPenaltySource", out a.RepPenaltySource);
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
            TryParseEnum(n, "endState", out a.KerbalEndStateField);
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
            n.AddValue("setupSci", SetupScienceCost.ToString("R", IC));
            n.AddValue("setupRep", SetupReputationCost.ToString("R", IC));
        }

        private static void DeserializeStrategyActivate(ConfigNode n, GameAction a)
        {
            a.StrategyId = n.GetValue("strategyId");
            TryParseEnum(n, "sourceResource", out a.SourceResource);
            TryParseEnum(n, "targetResource", out a.TargetResource);
            TryParseFloat(n, "commitment", out a.Commitment);
            TryParseFloat(n, "setupCost", out a.SetupCost);
            TryParseFloat(n, "setupSci", out a.SetupScienceCost);
            TryParseFloat(n, "setupRep", out a.SetupReputationCost);
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

        private void SerializeScienceInitial(ConfigNode n)
        {
            n.AddValue("initialScience", InitialScience.ToString("R", IC));
        }

        private static void DeserializeScienceInitial(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "initialScience", out a.InitialScience);
        }

        private void SerializeReputationInitial(ConfigNode n)
        {
            n.AddValue("initialReputation", InitialReputation.ToString("R", IC));
        }

        private static void DeserializeReputationInitial(ConfigNode n, GameAction a)
        {
            TryParseFloat(n, "initialReputation", out a.InitialReputation);
        }

        // ---- Route action serialization helpers ----
        //
        // Manifest encoding: each non-zero/non-empty manifest serializes as one
        //   resource = <name>|<amount-R-invariant>
        // line per entry. Skipping zero/null fields keeps the on-disk shape small
        // and lets future readers detect "no manifest" via the absence of the key.

        private void SerializeRouteDispatched(ConfigNode n)
        {
            WriteRouteCommon(n);
        }

        private static void DeserializeRouteDispatched(ConfigNode n, GameAction a)
        {
            ReadRouteCommon(n, a);
        }

        private void SerializeRouteCargoDebited(ConfigNode n)
        {
            WriteRouteCommon(n);
            WriteResourceManifest(n, "resource", RouteResourceManifest);
            if (RouteKscFundsCost != 0f)
                n.AddValue("routeKscFundsCost", RouteKscFundsCost.ToString("R", IC));
        }

        private static void DeserializeRouteCargoDebited(ConfigNode n, GameAction a)
        {
            ReadRouteCommon(n, a);
            a.RouteResourceManifest = ReadResourceManifest(n, "resource");
            TryParseFloat(n, "routeKscFundsCost", out a.RouteKscFundsCost);
        }

        private void SerializeRouteCargoDelivered(ConfigNode n)
        {
            WriteRouteCommon(n);
            WriteResourceManifest(n, "resource", RouteResourceManifest);
            WriteResourceManifest(n, "requestedResource", RouteRequestedResourceManifest);
        }

        private static void DeserializeRouteCargoDelivered(ConfigNode n, GameAction a)
        {
            ReadRouteCommon(n, a);
            a.RouteResourceManifest = ReadResourceManifest(n, "resource");
            a.RouteRequestedResourceManifest = ReadResourceManifest(n, "requestedResource");
        }

        private void SerializeRoutePaused(ConfigNode n)
        {
            WriteRouteCommon(n);
            if (!string.IsNullOrEmpty(RouteEndpointReason))
                n.AddValue("routeEndpointReason", RouteEndpointReason);
        }

        private static void DeserializeRoutePaused(ConfigNode n, GameAction a)
        {
            ReadRouteCommon(n, a);
            a.RouteEndpointReason = n.GetValue("routeEndpointReason");
        }

        private void SerializeRouteEndpointLost(ConfigNode n)
        {
            WriteRouteCommon(n);
            if (!string.IsNullOrEmpty(RouteEndpointReason))
                n.AddValue("routeEndpointReason", RouteEndpointReason);
        }

        private static void DeserializeRouteEndpointLost(ConfigNode n, GameAction a)
        {
            ReadRouteCommon(n, a);
            a.RouteEndpointReason = n.GetValue("routeEndpointReason");
        }

        /// <summary>
        /// Writes route-common identity fields (RouteId, RouteCycleId, RouteStopIndex).
        /// Sentinel <c>RouteStopIndex == -1</c> is skipped so a not-applicable stop index
        /// does not pollute the on-disk shape.
        /// </summary>
        private void WriteRouteCommon(ConfigNode n)
        {
            if (!string.IsNullOrEmpty(RouteId))
                n.AddValue("routeId", RouteId);
            if (!string.IsNullOrEmpty(RouteCycleId))
                n.AddValue("routeCycleId", RouteCycleId);
            if (RouteStopIndex >= 0)
                n.AddValue("routeStopIndex", RouteStopIndex.ToString(IC));
        }

        private static void ReadRouteCommon(ConfigNode n, GameAction a)
        {
            a.RouteId = n.GetValue("routeId");
            a.RouteCycleId = n.GetValue("routeCycleId");
            string stopStr = n.GetValue("routeStopIndex");
            if (stopStr != null && int.TryParse(stopStr, NumberStyles.Integer, IC, out int idx))
                a.RouteStopIndex = idx;
            else
                a.RouteStopIndex = -1;
        }

        /// <summary>
        /// Writes a manifest as one <c><paramref name="key"/> = name|amount</c> line per
        /// non-zero entry. Empty / null manifests write nothing.
        /// </summary>
        private static void WriteResourceManifest(ConfigNode n, string key, Dictionary<string, double> manifest)
        {
            if (manifest == null || manifest.Count == 0)
                return;

            foreach (var kv in manifest)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    continue;
                n.AddValue(key, kv.Key + "|" + kv.Value.ToString("R", IC));
            }
        }

        /// <summary>
        /// Reads all values for <paramref name="key"/> back into a manifest dict. Returns
        /// null when no values are present so callers can distinguish "absent" from "empty".
        /// </summary>
        private static Dictionary<string, double> ReadResourceManifest(ConfigNode n, string key)
        {
            string[] raws = n.GetValues(key);
            if (raws == null || raws.Length == 0)
                return null;

            var dict = new Dictionary<string, double>(raws.Length, StringComparer.Ordinal);
            for (int i = 0; i < raws.Length; i++)
            {
                string raw = raws[i];
                if (string.IsNullOrEmpty(raw))
                    continue;
                int sep = raw.IndexOf('|');
                if (sep <= 0 || sep == raw.Length - 1)
                {
                    ParsekLog.Warn("GameAction",
                        $"Route manifest entry malformed under key '{key}': '{raw}' (expected 'name|amount')");
                    continue;
                }
                string name = raw.Substring(0, sep);
                string amountStr = raw.Substring(sep + 1);
                if (!double.TryParse(amountStr, NS, IC, out double amount))
                {
                    ParsekLog.Warn("GameAction",
                        $"Route manifest amount unparseable under key '{key}' for resource '{name}': '{amountStr}'");
                    continue;
                }
                dict[name] = amount;
            }
            return dict.Count == 0 ? null : dict;
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

        private static bool TryParseEnum<T>(ConfigNode n, string key, out T result) where T : struct
        {
            result = default(T);
            string val = n.GetValue(key);
            if (val == null) return false;
            int intVal;
            if (!int.TryParse(val, NumberStyles.Integer, IC, out intVal)) return false;
            if (!Enum.IsDefined(typeof(T), intVal)) return false;
            result = (T)(object)intVal;
            return true;
        }
    }
}
