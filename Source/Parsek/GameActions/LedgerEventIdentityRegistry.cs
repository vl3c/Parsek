using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// How the replay decides a repeated occurrence of one identity (design section 3.15,
    /// step 1). The value is the TARGET rule; <see cref="LedgerEventIdentityEntry.EnforcedToday"/>
    /// says whether the walk applies it yet.
    /// </summary>
    internal enum LedgerIdentityRule
    {
        /// <summary>A seed (<c>*Initial</c>): a starting value, not an event.</summary>
        NotAnEvent = 0,
        /// <summary>Science: earliest first, each row earns what the subject cap leaves.</summary>
        CapHeadroom,
        /// <summary>The first row of the identity in walk order earns; later rows earn nothing.</summary>
        FirstWins,
        /// <summary>First wins, and only while the walk has the contract open (not completed,
        /// failed or cancelled).</summary>
        FirstWinsWhileContractOpen,
        /// <summary>A world record: each threshold once, earliest crossing first.</summary>
        OncePerThreshold,
        /// <summary>Once per accept of the contract.</summary>
        OncePerAcceptLifecycle,
        /// <summary>Once per celestial body.</summary>
        OncePerBody,
        /// <summary>Charged when the walk state at the row does not already own the item.</summary>
        PurchaseStateRule,
        /// <summary>Once per activation of the strategy.</summary>
        OncePerActivation,
        /// <summary>A strategy conversion leg: scaled by its cause row's effective share.</summary>
        FollowsCause,
        /// <summary>Applied as captured; the row's own key already names one occurrence.</summary>
        AsRecorded,
        /// <summary>Income with no ledger channel: a row at its own event UT that a rewind
        /// to before it drops.</summary>
        LiveOnly,
        /// <summary>Moves no pool (state, roster or bookkeeping row): nothing to earn twice.</summary>
        NoEconomicEffect,
        /// <summary>Reserved type: no row is ever written and an old row is dropped at load.</summary>
        NoLedgerRow
    }

    /// <summary>One registry entry: a row type (and, where the type splits, one source or
    /// subtype), the identity it is keyed on, its rule, and whether the walk enforces the
    /// rule today.</summary>
    internal sealed class LedgerEventIdentityEntry
    {
        internal readonly GameActionType Type;

        /// <summary>The source or subtype within <see cref="Type"/>; null when the type has
        /// one entry. A source enum split uses the member name.</summary>
        internal readonly string Variant;

        /// <summary>What names one occurrence (design 3.15 step 1's "Identity" column).</summary>
        internal readonly string IdentityKey;

        internal readonly LedgerIdentityRule Rule;

        /// <summary>True when the walk applies <see cref="Rule"/> today.</summary>
        internal readonly bool EnforcedToday;

        /// <summary>Where the rule is enforced (enforced entries only).</summary>
        internal readonly string EnforcedBy;

        /// <summary>The 3.15 step that builds the rule (planned entries only).</summary>
        internal readonly string PlannedStep;

        /// <summary>What the walk does with the row until then (planned entries only).</summary>
        internal readonly string TodayBehaviour;

        internal LedgerEventIdentityEntry(
            GameActionType type, string variant, string identityKey, LedgerIdentityRule rule,
            bool enforcedToday, string enforcedBy, string plannedStep, string todayBehaviour)
        {
            Type = type;
            Variant = variant;
            IdentityKey = identityKey;
            Rule = rule;
            EnforcedToday = enforcedToday;
            EnforcedBy = enforcedBy;
            PlannedStep = plannedStep;
            TodayBehaviour = todayBehaviour;
        }

        /// <summary>The entry's name: <c>Type</c> or <c>Type/Variant</c>.</summary>
        internal string Name
        {
            get { return Variant == null ? Type.ToString() : Type.ToString() + "/" + Variant; }
        }

        /// <summary>One line for logs, e.g.
        /// <c>ScienceSpending key=[tech node id] rule=PurchaseStateRule planned=3.15 step 6
        /// (today: ...)</c>.</summary>
        internal string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Name).Append(" key=[").Append(IdentityKey).Append("] rule=").Append(Rule);
            if (EnforcedToday)
                sb.Append(" enforced by ").Append(EnforcedBy);
            else
                sb.Append(" planned=").Append(PlannedStep).Append(" (today: ").Append(TodayBehaviour).Append(")");
            return sb.ToString();
        }
    }

    /// <summary>
    /// The one place that names, for every <see cref="GameActionType"/> and for every source or
    /// subtype that needs its own rule, the identity a row is keyed on and the rule the replay
    /// uses to decide a repeat of it (design section 3.15 step 1, build order step 3). Pure and
    /// static: no KSP state, no ledger reads.
    ///
    /// <para>Each entry says whether the walk ENFORCES its rule today or which later step plans
    /// it. Step numbers are the numbered parts of section 3.15 (step 4 records per threshold, built,
    /// step 6 the purchase state rule, step 7 conversion legs, step 9 live-only income); the
    /// orbital survey bonus is planned in the section's build order step 7. The registry
    /// changes no walk behaviour by itself: the modules still hold the logic, and later steps
    /// wire them to it.</para>
    ///
    /// <para>The suite gate (<c>LedgerEventIdentityRegistryTests</c>) enumerates every
    /// <see cref="GameActionType"/> and every source enum the registry splits on, and fails on a
    /// value with no entry, so a new type or source cannot ship without a decision.</para>
    /// </summary>
    internal static class LedgerEventIdentityRegistry
    {
        private const string Tag = "LedgerIdentity";

        /// <summary><see cref="GameActionType.MilestoneAchievement"/> for a one-shot progress
        /// node (a world-first).</summary>
        internal const string MilestoneOneShot = "one-shot";

        /// <summary><see cref="GameActionType.MilestoneAchievement"/> for a repeatable world
        /// record (<see cref="MilestonesModule.IsRepeatableWorldRecordMilestone"/>).</summary>
        internal const string MilestoneWorldRecord = "world-record";

        /// <summary><see cref="GameActionType.ScienceLabEarning"/> paid by a Mobile Processing
        /// Lab (no <see cref="GameAction.ScanBodyName"/>).</summary>
        internal const string LabPayout = "lab";

        /// <summary><see cref="GameActionType.ScienceLabEarning"/> paid by an orbital survey
        /// (<see cref="GameAction.ScanBodyName"/> set).</summary>
        internal const string OrbitalSurvey = "survey";

        private const string ConversionLegToday =
            "applied as captured, never scaled by its cause";
        private const string PurchaseToday =
            "charged on every row, as recorded";

        private static readonly LedgerEventIdentityEntry[] entries = BuildEntries();

        private static readonly Dictionary<string, LedgerEventIdentityEntry> byName = BuildIndex(entries);

        /// <summary>Every entry, in declaration order.</summary>
        internal static IReadOnlyList<LedgerEventIdentityEntry> Entries
        {
            get { return entries; }
        }

        /// <summary>
        /// The source enum a type's entries split on, or null when the type does not split on
        /// an enum (one entry, or a split on a row predicate: see <see cref="VariantsFor"/>).
        /// </summary>
        internal static Type SplitEnumFor(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.FundsEarning: return typeof(FundsEarningSource);
                case GameActionType.FundsSpending: return typeof(FundsSpendingSource);
                case GameActionType.ReputationEarning: return typeof(ReputationSource);
                case GameActionType.ReputationPenalty: return typeof(ReputationPenaltySource);
                case GameActionType.StrategyScienceDebit: return typeof(StrategyConversionSource);
                default: return null;
            }
        }

        /// <summary>
        /// The variants a type must have entries for: every member name of its split enum, the
        /// two predicate subtypes of a milestone or a lab row, or a single null for a type with
        /// one entry.
        /// </summary>
        internal static IReadOnlyList<string> VariantsFor(GameActionType type)
        {
            Type split = SplitEnumFor(type);
            if (split != null)
                return Enum.GetNames(split);
            switch (type)
            {
                case GameActionType.MilestoneAchievement:
                    return new[] { MilestoneOneShot, MilestoneWorldRecord };
                case GameActionType.ScienceLabEarning:
                    return new[] { LabPayout, OrbitalSurvey };
                default:
                    return new string[] { null };
            }
        }

        /// <summary>The variant a row belongs to, read from the field its type splits on; null
        /// for a type with one entry.</summary>
        internal static string VariantOf(GameAction action)
        {
            if (action == null)
                return null;
            switch (action.Type)
            {
                case GameActionType.FundsEarning: return action.FundsSource.ToString();
                case GameActionType.FundsSpending: return action.FundsSpendingSource.ToString();
                case GameActionType.ReputationEarning: return action.RepSource.ToString();
                case GameActionType.ReputationPenalty: return action.RepPenaltySource.ToString();
                case GameActionType.StrategyScienceDebit: return action.ConversionSource.ToString();
                case GameActionType.MilestoneAchievement:
                    return MilestonesModule.IsRepeatableWorldRecordMilestone(action.MilestoneId ?? "")
                        ? MilestoneWorldRecord
                        : MilestoneOneShot;
                case GameActionType.ScienceLabEarning:
                    return string.IsNullOrEmpty(action.ScanBodyName) ? LabPayout : OrbitalSurvey;
                default:
                    return null;
            }
        }

        /// <summary>The entry for a type and variant (null variant for a type with one entry).</summary>
        internal static bool TryGet(GameActionType type, string variant, out LedgerEventIdentityEntry entry)
        {
            return byName.TryGetValue(KeyOf(type, variant), out entry);
        }

        /// <summary>
        /// The entry for a row, or null when none exists (a source value a newer build wrote;
        /// the suite gate keeps every value this build defines covered). A miss is logged as a
        /// rate-limited warning naming the type and variant.
        /// </summary>
        internal static LedgerEventIdentityEntry For(GameAction action)
        {
            if (action == null)
                return null;
            string variant = VariantOf(action);
            LedgerEventIdentityEntry entry;
            if (TryGet(action.Type, variant, out entry))
                return entry;
            string name = variant == null ? action.Type.ToString() : action.Type + "/" + variant;
            ParsekLog.WarnRateLimited(Tag, "no-entry-" + name,
                "No identity registry entry for " + name + " (actionId=" + (action.ActionId ?? "(none)")
                + "); the walk decides it with its module's own logic");
            return null;
        }

        /// <summary>One line for logs naming the row's identity and rule, or a fixed text when
        /// the registry has no entry for it.</summary>
        internal static string Describe(GameAction action)
        {
            var entry = For(action);
            return entry != null ? entry.Describe() : "(no identity registry entry)";
        }

        /// <summary>A one-line count of the registry: entries, enforced, planned.</summary>
        internal static string Summary()
        {
            int enforced = 0;
            for (int i = 0; i < entries.Length; i++)
                if (entries[i].EnforcedToday) enforced++;
            var ic = CultureInfo.InvariantCulture;
            return "identity registry: entries=" + entries.Length.ToString(ic)
                + " enforced=" + enforced.ToString(ic)
                + " planned=" + (entries.Length - enforced).ToString(ic);
        }

        private static string KeyOf(GameActionType type, string variant)
        {
            return variant == null ? type.ToString() : type.ToString() + "/" + variant;
        }

        private static Dictionary<string, LedgerEventIdentityEntry> BuildIndex(LedgerEventIdentityEntry[] list)
        {
            var index = new Dictionary<string, LedgerEventIdentityEntry>(StringComparer.Ordinal);
            for (int i = 0; i < list.Length; i++)
                index[KeyOf(list[i].Type, list[i].Variant)] = list[i];
            return index;
        }

        private static LedgerEventIdentityEntry Enforced(
            GameActionType type, string variant, string key, LedgerIdentityRule rule, string enforcedBy)
        {
            return new LedgerEventIdentityEntry(type, variant, key, rule, true, enforcedBy, null, null);
        }

        private static LedgerEventIdentityEntry Planned(
            GameActionType type, string variant, string key, LedgerIdentityRule rule,
            string plannedStep, string today)
        {
            return new LedgerEventIdentityEntry(type, variant, key, rule, false, null, plannedStep, today);
        }

        private static LedgerEventIdentityEntry NoEffect(GameActionType type, string key)
        {
            return Enforced(type, null, key, LedgerIdentityRule.NoEconomicEffect, "no module moves a pool for it");
        }

        private static LedgerEventIdentityEntry Reserved(GameActionType type)
        {
            return Enforced(type, null, "none (reserved supply-route type)", LedgerIdentityRule.NoLedgerRow,
                "LedgerLoadMigration.IsLegacyRouteNode (dropped at ledger load)");
        }

        private static LedgerEventIdentityEntry Seed(GameActionType type)
        {
            return Enforced(type, null, "none (a starting value)", LedgerIdentityRule.NotAnEvent,
                "the walk's seed handling");
        }

        private static LedgerEventIdentityEntry[] BuildEntries()
        {
            const string F = "FundsModule";
            const string R = "ReputationModule";
            return new[]
            {
                // ---- Science ----
                Enforced(GameActionType.ScienceEarning, null, "subject id", LedgerIdentityRule.CapHeadroom,
                    "ScienceModule.ProcessEarning (headroom = subject cap - credited total)"),
                Planned(GameActionType.ScienceSpending, null, "tech node id", LedgerIdentityRule.PurchaseStateRule,
                    "3.15 step 6", "charged when affordable; an unaffordable tech is not charged and stays researched"),
                Planned(GameActionType.StrategyScienceDebit, StrategyConversionSource.Converter.ToString(),
                    "its cause row", LedgerIdentityRule.FollowsCause, "3.15 step 7", ConversionLegToday),
                Enforced(GameActionType.StrategyScienceDebit, StrategyConversionSource.Exchanger.ToString(),
                    "strategy + activation (the exchange pays with it)", LedgerIdentityRule.AsRecorded,
                    "ScienceModule (applied as captured; StrategyActivationGate never switches a one-time exchanger on again, 3.14)"),
                Planned(GameActionType.StrategyScienceCredit, null, "its cause row", LedgerIdentityRule.FollowsCause,
                    "3.15 step 7", ConversionLegToday),
                Enforced(GameActionType.ScienceLabEarning, LabPayout, "none (the lab bounds itself)",
                    LedgerIdentityRule.AsRecorded, "ScienceModule.ProcessLabEarning (uncapped, as captured)"),
                Planned(GameActionType.ScienceLabEarning, OrbitalSurvey, "body", LedgerIdentityRule.OncePerBody,
                    "3.15 build order step 7 (once a decompile confirms stock pays once per body)",
                    "every survey row is credited as captured"),

                // ---- Funds ----
                Enforced(GameActionType.FundsEarning, FundsEarningSource.ContractComplete.ToString(),
                    "contract guid (legacy source: contract rewards ride ContractComplete)",
                    LedgerIdentityRule.AsRecorded, F + " (no producer writes this source)"),
                Enforced(GameActionType.FundsEarning, FundsEarningSource.ContractAdvance.ToString(),
                    "contract guid + accept (legacy source: the advance rides ContractAccept)",
                    LedgerIdentityRule.AsRecorded, F + " (no producer writes this source)"),
                Enforced(GameActionType.FundsEarning, FundsEarningSource.Recovery.ToString(),
                    "owner recording + launch guid", LedgerIdentityRule.AsRecorded,
                    F + " (one paired recovery row per owner)"),
                Enforced(GameActionType.FundsEarning, FundsEarningSource.Milestone.ToString(),
                    "progress node id (legacy source: milestone rewards ride MilestoneAchievement)",
                    LedgerIdentityRule.AsRecorded, F + " (no producer writes this source)"),
                Planned(GameActionType.FundsEarning, FundsEarningSource.Other.ToString(),
                    "reason + event UT", LedgerIdentityRule.LiveOnly, "3.15 step 9",
                    "no producer writes it; income with no channel is filed as a StockPoolAdjustment"),
                Enforced(GameActionType.FundsEarning, FundsEarningSource.LegacyMigration.ToString(),
                    "none (synthetic tag of an older save)", LedgerIdentityRule.AsRecorded,
                    F + " (default earning branch)"),
                Enforced(GameActionType.FundsEarning, FundsEarningSource.Strategy.ToString(),
                    "strategy + activation (the exchange pays with it)", LedgerIdentityRule.AsRecorded,
                    F + " (as captured; StrategyActivationGate never switches a one-time exchanger on again, 3.14)"),

                Enforced(GameActionType.FundsSpending, FundsSpendingSource.VesselBuild.ToString(),
                    "launch guid", LedgerIdentityRule.AsRecorded, F + " (rollout dedup on the launch guid)"),
                Enforced(GameActionType.FundsSpending, FundsSpendingSource.FacilityUpgrade.ToString(),
                    "facility + level (legacy source: upgrades are FacilityUpgrade rows)",
                    LedgerIdentityRule.AsRecorded, F + " (no producer writes this source)"),
                Enforced(GameActionType.FundsSpending, FundsSpendingSource.FacilityRepair.ToString(),
                    "facility (legacy source: repairs are FacilityRepair rows)",
                    LedgerIdentityRule.AsRecorded, F + " (no producer writes this source)"),
                Enforced(GameActionType.FundsSpending, FundsSpendingSource.KerbalHire.ToString(),
                    "kerbal name (legacy source: hires are KerbalHire rows)",
                    LedgerIdentityRule.AsRecorded, F + " (no producer writes this source)"),
                Enforced(GameActionType.FundsSpending, FundsSpendingSource.ContractPenalty.ToString(),
                    "contract guid (legacy source: penalties ride ContractFail / ContractCancel)",
                    LedgerIdentityRule.AsRecorded, F + " (no producer writes this source)"),
                Enforced(GameActionType.FundsSpending, FundsSpendingSource.Strategy.ToString(),
                    "strategy + activation (the exchange pays with it)", LedgerIdentityRule.AsRecorded,
                    F + " (as captured; StrategyActivationGate never switches a one-time exchanger on again, 3.14)"),
                Planned(GameActionType.FundsSpending, FundsSpendingSource.Other.ToString(),
                    "part name (a part purchase)", LedgerIdentityRule.PurchaseStateRule, "3.15 step 6",
                    PurchaseToday),
                Planned(GameActionType.FundsSpending, FundsSpendingSource.StrategyConverter.ToString(),
                    "its cause row", LedgerIdentityRule.FollowsCause, "3.15 step 7", ConversionLegToday),
                Planned(GameActionType.FundsSpending, FundsSpendingSource.PartUpgrade.ToString(),
                    "part upgrade name", LedgerIdentityRule.PurchaseStateRule, "3.15 step 6", PurchaseToday),

                // ---- Milestones ----
                Enforced(GameActionType.MilestoneAchievement, MilestoneOneShot,
                    "body-qualified progress node id", LedgerIdentityRule.FirstWins,
                    "MilestonesModule.ProcessAction (MilestoneAlreadyAchieved)"),
                Enforced(GameActionType.MilestoneAchievement, MilestoneWorldRecord,
                    "node id + threshold index", LedgerIdentityRule.OncePerThreshold,
                    "MilestonesModule.ProcessAction (RecordThresholdBook; RecordThresholdsAlreadyCredited)"),

                // ---- Contracts ----
                Enforced(GameActionType.ContractAccept, null, "contract guid + accept",
                    LedgerIdentityRule.OncePerAcceptLifecycle,
                    F + ".ProcessContractAccept (one advance per accept row; a twin offer is 3.15 step 8)"),
                Enforced(GameActionType.ContractComplete, null, "contract guid, one outcome ever (re-accept does not reopen it)",
                    LedgerIdentityRule.FirstWins,
                    "ContractsModule.ProcessComplete (once ever; not after a deadline, fail or cancel)"),
                Enforced(GameActionType.ContractFail, null, "contract guid, one outcome ever (re-accept does not reopen it)",
                    LedgerIdentityRule.FirstWins, "ContractsModule.ProcessFail (ContractOutcomeAfterEnd)"),
                Enforced(GameActionType.ContractCancel, null, "contract guid, one outcome ever (re-accept does not reopen it)",
                    LedgerIdentityRule.FirstWins, "ContractsModule.ProcessCancel (ContractOutcomeAfterEnd)"),
                Enforced(GameActionType.ContractParameterComplete, null, "contract guid + parameter path",
                    LedgerIdentityRule.FirstWinsWhileContractOpen,
                    "ContractsModule.ProcessParameterComplete (once per parameter; not after the contract's completion, deadline, fail or cancel)"),

                // ---- Reputation ----
                Enforced(GameActionType.ReputationEarning, ReputationSource.ContractComplete.ToString(),
                    "contract guid (legacy source: rep rides ContractComplete)", LedgerIdentityRule.AsRecorded,
                    R + " (no producer writes this source; curve re-run)"),
                Enforced(GameActionType.ReputationEarning, ReputationSource.Milestone.ToString(),
                    "progress node id (legacy source: rep rides MilestoneAchievement)", LedgerIdentityRule.AsRecorded,
                    R + " (no producer writes this source; curve re-run)"),
                Planned(GameActionType.ReputationEarning, ReputationSource.Other.ToString(),
                    "reason + event UT", LedgerIdentityRule.LiveOnly, "3.15 step 9",
                    "no producer writes it; income with no channel is filed as a StockPoolAdjustment"),
                Planned(GameActionType.ReputationEarning, ReputationSource.Strategy.ToString(),
                    "its cause row", LedgerIdentityRule.FollowsCause, "3.15 step 7", ConversionLegToday),

                Enforced(GameActionType.ReputationPenalty, ReputationPenaltySource.ContractFail.ToString(),
                    "contract guid (legacy source: the penalty rides ContractFail)", LedgerIdentityRule.AsRecorded,
                    R + " (no producer writes this source)"),
                Enforced(GameActionType.ReputationPenalty, ReputationPenaltySource.ContractDecline.ToString(),
                    "contract guid (decline)", LedgerIdentityRule.AsRecorded, R + " (as captured, post-curve)"),
                Enforced(GameActionType.ReputationPenalty, ReputationPenaltySource.KerbalDeath.ToString(),
                    "owner recording (one death penalty per recording)", LedgerIdentityRule.AsRecorded,
                    R + " (as captured, post-curve)"),
                Enforced(GameActionType.ReputationPenalty, ReputationPenaltySource.Strategy.ToString(),
                    "strategy + activation (the exchange pays with it)", LedgerIdentityRule.AsRecorded,
                    R + " (as captured, post-curve; StrategyActivationGate never switches a one-time exchanger on again, 3.14)"),
                Enforced(GameActionType.ReputationPenalty, ReputationPenaltySource.Other.ToString(),
                    "reason + event UT", LedgerIdentityRule.AsRecorded, R + " (as captured; curve re-run)"),
                Planned(GameActionType.ReputationPenalty, ReputationPenaltySource.StrategyConverter.ToString(),
                    "its cause row", LedgerIdentityRule.FollowsCause, "3.15 step 7", ConversionLegToday),

                // ---- Kerbals ----
                NoEffect(GameActionType.KerbalAssignment, "recording + kerbal"),
                Planned(GameActionType.KerbalHire, null, "kerbal name (price as captured)",
                    LedgerIdentityRule.PurchaseStateRule, "3.15 step 6", PurchaseToday),
                NoEffect(GameActionType.KerbalRescue, "kerbal name"),
                NoEffect(GameActionType.KerbalStandIn, "stand-in kerbal name"),
                NoEffect(GameActionType.KerbalExperience, "kerbal name + career log entries"),
                NoEffect(GameActionType.KerbalRecovered, "kerbal name + owner recording"),
                NoEffect(GameActionType.VesselRecovered, "owner recording + recovered vessel"),

                // ---- Facilities ----
                Planned(GameActionType.FacilityUpgrade, null, "facility + exact level",
                    LedgerIdentityRule.PurchaseStateRule, "3.15 step 6", PurchaseToday),
                NoEffect(GameActionType.FacilityDestruction, "facility + destruction"),
                Enforced(GameActionType.FacilityRepair, null, "facility + destruction",
                    LedgerIdentityRule.PurchaseStateRule,
                    F + ".ProcessFacilityRepair (charged only while the walk has the building destroyed)"),

                // ---- Strategies ----
                Enforced(GameActionType.StrategyActivate, null, "strategy + activation",
                    LedgerIdentityRule.OncePerActivation,
                    "one setup charge per activation row; StrategyActivationGate adds no second charge (3.14)"),
                NoEffect(GameActionType.StrategyDeactivate, "strategy + activation"),

                // ---- Seeds ----
                Seed(GameActionType.FundsInitial),
                Seed(GameActionType.ScienceInitial),
                Seed(GameActionType.ReputationInitial),

                // ---- Other ----
                NoEffect(GameActionType.ResourceScanUnlocked, "body + biome"),
                Enforced(GameActionType.StockPoolAdjustment, null, "pool + patch UT",
                    LedgerIdentityRule.AsRecorded,
                    "StockPoolReference (an older save's pool difference; 3.15 step 9 files new channel-less income live-only instead)"),

                // ---- Reserved supply-route types ----
                Reserved(GameActionType.RouteDispatched),
                Reserved(GameActionType.RouteCargoDebited),
                Reserved(GameActionType.RouteCargoDelivered),
                Reserved(GameActionType.RoutePaused),
                Reserved(GameActionType.RouteEndpointLost),
                Reserved(GameActionType.RouteRecoveryCredited),
                Reserved(GameActionType.RouteCargoPickedUp),
                Reserved(GameActionType.RouteResumed),
                Reserved(GameActionType.RouteHeld),
            };
        }
    }
}
