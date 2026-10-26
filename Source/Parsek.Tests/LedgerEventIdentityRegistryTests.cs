using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The suite gate of design 3.15 step 1 (build order step 3): every
    /// <see cref="GameActionType"/>, and every source or subtype the registry splits on, has
    /// one identity registry entry, so a new type or source cannot ship without a decision.
    /// Plus: each entry the registry marks enforced today really is enforced by the walk (a
    /// probe per decided rule), and the planned entries probed here still behave as their
    /// "today" text says, so a later step that builds one must flip the registry too.
    /// </summary>
    [Collection("Sequential")]
    public class LedgerEventIdentityRegistryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LedgerEventIdentityRegistryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecalculationEngine.ClearModules();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
        }

        // ================================================================
        // The gate
        // ================================================================

        // The source enums the registry must split on, named here independently of the
        // registry's own SplitEnumFor, so dropping a split is caught as well as a new member.
        private static readonly Dictionary<GameActionType, Type> ExpectedSplits = new Dictionary<GameActionType, Type>
        {
            { GameActionType.FundsEarning, typeof(FundsEarningSource) },
            { GameActionType.FundsSpending, typeof(FundsSpendingSource) },
            { GameActionType.ReputationEarning, typeof(ReputationSource) },
            { GameActionType.ReputationPenalty, typeof(ReputationPenaltySource) },
            { GameActionType.StrategyScienceDebit, typeof(StrategyConversionSource) },
        };

        // catches: a new GameActionType, or a new member of a source enum the registry splits
        // on, shipping with no identity decision.
        [Fact]
        public void EveryTypeAndEverySplitSourceValue_HasAnEntry()
        {
            var missing = new List<string>();
            foreach (GameActionType type in Enum.GetValues(typeof(GameActionType)))
            {
                Type split;
                IEnumerable<string> variants = ExpectedSplits.TryGetValue(type, out split)
                    ? Enum.GetNames(split)
                    : LedgerEventIdentityRegistry.VariantsFor(type);
                foreach (string variant in variants)
                {
                    LedgerEventIdentityEntry entry;
                    if (!LedgerEventIdentityRegistry.TryGet(type, variant, out entry))
                        missing.Add(variant == null ? type.ToString() : type + "/" + variant);
                }
            }
            Assert.True(missing.Count == 0,
                "GameActionType / source values with no identity registry entry (design 3.15 step 1; add a "
                + "decision to LedgerEventIdentityRegistry): " + string.Join(", ", missing));
        }

        [Fact]
        public void SplitEnums_AreTheExpectedOnes()
        {
            foreach (GameActionType type in Enum.GetValues(typeof(GameActionType)))
            {
                Type expected;
                ExpectedSplits.TryGetValue(type, out expected);
                Assert.Equal(expected, LedgerEventIdentityRegistry.SplitEnumFor(type));
            }
            Assert.Equal(new[] { LedgerEventIdentityRegistry.MilestoneOneShot, LedgerEventIdentityRegistry.MilestoneWorldRecord },
                LedgerEventIdentityRegistry.VariantsFor(GameActionType.MilestoneAchievement));
            Assert.Equal(new[] { LedgerEventIdentityRegistry.LabPayout, LedgerEventIdentityRegistry.OrbitalSurvey },
                LedgerEventIdentityRegistry.VariantsFor(GameActionType.ScienceLabEarning));
        }

        // catches: an entry for a variant the type does not split on (it could never be looked
        // up), or two entries for one variant.
        [Fact]
        public void NoOrphanOrDuplicateEntries()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in LedgerEventIdentityRegistry.Entries)
            {
                Assert.True(names.Add(e.Name), "duplicate registry entry " + e.Name);
                Assert.Contains(e.Variant, LedgerEventIdentityRegistry.VariantsFor(e.Type));
            }
            int expected = Enum.GetValues(typeof(GameActionType)).Cast<GameActionType>()
                .Sum(t => LedgerEventIdentityRegistry.VariantsFor(t).Count);
            Assert.Equal(expected, LedgerEventIdentityRegistry.Entries.Count);
        }

        [Fact]
        public void EveryEntry_StatesItsKeyAndEitherWhereItIsEnforcedOrWhichStepPlansIt()
        {
            foreach (var e in LedgerEventIdentityRegistry.Entries)
            {
                Assert.False(string.IsNullOrWhiteSpace(e.IdentityKey), e.Name + " has no identity key");
                if (e.EnforcedToday)
                {
                    Assert.False(string.IsNullOrWhiteSpace(e.EnforcedBy), e.Name + " names no enforcer");
                    Assert.Null(e.PlannedStep);
                    Assert.Null(e.TodayBehaviour);
                }
                else
                {
                    Assert.StartsWith("3.15 ", e.PlannedStep);
                    Assert.False(string.IsNullOrWhiteSpace(e.TodayBehaviour), e.Name + " says nothing about today");
                    Assert.Null(e.EnforcedBy);
                }
                foreach (char c in e.Describe())
                    Assert.True(c < 128, e.Name + " description is not plain ASCII");
            }
        }

        // ================================================================
        // Lookup and description
        // ================================================================

        [Fact]
        public void For_ResolvesTheVariantFromTheRow()
        {
            Assert.Equal("MilestoneAchievement/world-record", LedgerEventIdentityRegistry.For(
                new GameAction { Type = GameActionType.MilestoneAchievement, MilestoneId = "RecordsSpeed" }).Name);
            Assert.Equal("MilestoneAchievement/one-shot", LedgerEventIdentityRegistry.For(
                new GameAction { Type = GameActionType.MilestoneAchievement, MilestoneId = "Mun/Landing" }).Name);
            Assert.Equal("ScienceLabEarning/survey", LedgerEventIdentityRegistry.For(
                new GameAction { Type = GameActionType.ScienceLabEarning, ScanBodyName = "Mun" }).Name);
            Assert.Equal("ScienceLabEarning/lab", LedgerEventIdentityRegistry.For(
                new GameAction { Type = GameActionType.ScienceLabEarning }).Name);
            Assert.Equal("FundsEarning/Recovery", LedgerEventIdentityRegistry.For(
                new GameAction { Type = GameActionType.FundsEarning, FundsSource = FundsEarningSource.Recovery }).Name);
            Assert.Equal("ReputationPenalty/KerbalDeath", LedgerEventIdentityRegistry.For(
                new GameAction { Type = GameActionType.ReputationPenalty, RepPenaltySource = ReputationPenaltySource.KerbalDeath }).Name);
            Assert.Equal("StrategyScienceDebit/Converter", LedgerEventIdentityRegistry.For(
                new GameAction { Type = GameActionType.StrategyScienceDebit, ConversionSource = StrategyConversionSource.Converter }).Name);
            Assert.Equal("ContractParameterComplete", LedgerEventIdentityRegistry.For(
                new GameAction { Type = GameActionType.ContractParameterComplete }).Name);
            Assert.Null(LedgerEventIdentityRegistry.For(null));
        }

        // catches: a source value a newer build wrote reading as some other entry instead of a
        // logged miss.
        [Fact]
        public void For_UnknownSourceValue_ReturnsNull_AndWarns()
        {
            var row = new GameAction { Type = GameActionType.FundsEarning, FundsSource = (FundsEarningSource)99 };
            Assert.Null(LedgerEventIdentityRegistry.For(row));
            Assert.Equal("(no identity registry entry)", LedgerEventIdentityRegistry.Describe(row));
            Assert.Contains(logLines, l => l.Contains("[WARN][LedgerIdentity]")
                && l.Contains("No identity registry entry for FundsEarning/99"));
        }

        [Fact]
        public void Describe_NamesKeyRuleAndEnforcementInOneLine()
        {
            LedgerEventIdentityEntry record;
            Assert.True(LedgerEventIdentityRegistry.TryGet(GameActionType.MilestoneAchievement,
                LedgerEventIdentityRegistry.MilestoneWorldRecord, out record));
            Assert.Equal("MilestoneAchievement/world-record key=[node id + threshold index] rule=OncePerThreshold "
                + "enforced by MilestonesModule.ProcessAction (RecordThresholdBook; RecordThresholdsAlreadyCredited)",
                record.Describe());

            LedgerEventIdentityEntry tech;
            Assert.True(LedgerEventIdentityRegistry.TryGet(GameActionType.ScienceSpending, null, out tech));
            Assert.Equal("ScienceSpending key=[tech node id] rule=PurchaseStateRule planned=3.15 step 6 "
                + "(today: charged when affordable; an unaffordable tech is not charged and stays researched)",
                tech.Describe());

            LedgerEventIdentityEntry science;
            Assert.True(LedgerEventIdentityRegistry.TryGet(GameActionType.ScienceEarning, null, out science));
            Assert.StartsWith("ScienceEarning key=[subject id] rule=CapHeadroom enforced by ScienceModule.ProcessEarning",
                science.Describe());
            Assert.DoesNotContain("\n", science.Describe());

            Assert.StartsWith("identity registry: entries=" + LedgerEventIdentityRegistry.Entries.Count,
                LedgerEventIdentityRegistry.Summary());
        }

        // The rows design 3.15 step 1's table names, pinned to the registry's rule for them.
        [Theory]
        [InlineData(GameActionType.ScienceEarning, null, "CapHeadroom", true)]
        [InlineData(GameActionType.MilestoneAchievement, "one-shot", "FirstWins", true)]
        [InlineData(GameActionType.MilestoneAchievement, "world-record", "OncePerThreshold", true)]
        [InlineData(GameActionType.ContractComplete, null, "FirstWins", true)]
        [InlineData(GameActionType.ContractFail, null, "FirstWins", true)]
        [InlineData(GameActionType.ContractCancel, null, "FirstWins", true)]
        [InlineData(GameActionType.ContractParameterComplete, null, "FirstWinsWhileContractOpen", true)]
        [InlineData(GameActionType.ContractAccept, null, "OncePerAcceptLifecycle", true)]
        [InlineData(GameActionType.ScienceLabEarning, "survey", "OncePerBody", false)]
        [InlineData(GameActionType.ScienceLabEarning, "lab", "AsRecorded", true)]
        [InlineData(GameActionType.ScienceSpending, null, "PurchaseStateRule", false)]
        [InlineData(GameActionType.FundsSpending, "Other", "PurchaseStateRule", false)]
        [InlineData(GameActionType.FundsSpending, "PartUpgrade", "PurchaseStateRule", false)]
        [InlineData(GameActionType.FacilityUpgrade, null, "PurchaseStateRule", false)]
        [InlineData(GameActionType.FacilityRepair, null, "PurchaseStateRule", true)]
        [InlineData(GameActionType.KerbalHire, null, "PurchaseStateRule", false)]
        [InlineData(GameActionType.FundsSpending, "VesselBuild", "AsRecorded", true)]
        [InlineData(GameActionType.FundsEarning, "Recovery", "AsRecorded", true)]
        [InlineData(GameActionType.StrategyActivate, null, "OncePerActivation", true)]
        [InlineData(GameActionType.FundsSpending, "StrategyConverter", "FollowsCause", false)]
        [InlineData(GameActionType.ReputationEarning, "Strategy", "FollowsCause", false)]
        [InlineData(GameActionType.ReputationPenalty, "StrategyConverter", "FollowsCause", false)]
        [InlineData(GameActionType.StrategyScienceCredit, null, "FollowsCause", false)]
        [InlineData(GameActionType.StrategyScienceDebit, "Converter", "FollowsCause", false)]
        [InlineData(GameActionType.FundsEarning, "Other", "LiveOnly", false)]
        [InlineData(GameActionType.FundsInitial, null, "NotAnEvent", true)]
        [InlineData(GameActionType.ScienceInitial, null, "NotAnEvent", true)]
        [InlineData(GameActionType.ReputationInitial, null, "NotAnEvent", true)]
        public void DesignTableRows_HaveTheirRule(GameActionType type, string variant, string rule, bool enforced)
        {
            LedgerEventIdentityEntry e;
            Assert.True(LedgerEventIdentityRegistry.TryGet(type, variant, out e));
            Assert.Equal(rule, e.Rule.ToString());
            Assert.Equal(enforced, e.EnforcedToday);
        }

        // ================================================================
        // Enforced entries really are enforced
        // ================================================================

        // Rules that decide a repeat: an entry with one of these marked enforced must have a
        // probe below that shows the walk deciding it.
        private static readonly HashSet<LedgerIdentityRule> DecidingRules = new HashSet<LedgerIdentityRule>
        {
            LedgerIdentityRule.CapHeadroom,
            LedgerIdentityRule.FirstWins,
            LedgerIdentityRule.FirstWinsWhileContractOpen,
            LedgerIdentityRule.OncePerThreshold,
            LedgerIdentityRule.OncePerBody,
            LedgerIdentityRule.PurchaseStateRule,
        };

        private sealed class Walk
        {
            internal MilestonesModule Milestones = new MilestonesModule();
            internal ContractsModule Contracts = new ContractsModule();
            internal ScienceModule Science = new ScienceModule();
            internal FundsModule Funds = new FundsModule();
            internal ReputationModule Rep = new ReputationModule();
            internal FacilitiesModule Facilities = new FacilitiesModule();
        }

        // Production tiers (LedgerOrchestrator.Initialize).
        private static Walk Register()
        {
            var w = new Walk();
            RecalculationEngine.RegisterModule(w.Milestones, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(w.Contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(w.Science, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(w.Funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(w.Rep, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(w.Facilities, RecalculationEngine.ModuleTier.Facilities);
            return w;
        }

        private static List<GameAction> Seeds()
        {
            return new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 100000f },
                new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 0f },
                new GameAction { UT = 0, Type = GameActionType.ReputationInitial, InitialReputation = 0f },
            };
        }

        private static GameAction Contract(GameActionType type, double ut, string id, string paramId = null)
        {
            return new GameAction
            {
                UT = ut, Type = type, ContractId = id, ContractParameterId = paramId,
                FundsReward = 1000f, FundsPenalty = 100f
            };
        }

        private static readonly Dictionary<string, Action> Probes = new Dictionary<string, Action>
        {
            { "ScienceEarning", ProbeScienceCapHeadroom },
            { "MilestoneAchievement/one-shot", ProbeMilestoneFirstWins },
            { "MilestoneAchievement/world-record", ProbeWorldRecordOncePerThreshold },
            { "ContractComplete", ProbeContractCompleteFirstWins },
            { "ContractFail", ProbeContractFailFirstOutcomeWins },
            { "ContractCancel", ProbeContractCancelFirstOutcomeWins },
            { "ContractParameterComplete", ProbeParameterFirstWinsWhileOpen },
            { "FacilityRepair", ProbeFacilityRepairStateRule },
        };

        public static IEnumerable<object[]> EnforcedDecidingEntries()
        {
            return LedgerEventIdentityRegistry.Entries
                .Where(e => e.EnforcedToday && DecidingRules.Contains(e.Rule))
                .Select(e => new object[] { e.Name });
        }

        // catches: the registry claiming a deciding rule is enforced with no walk behind it, or
        // a module change that silently drops the enforcement the registry advertises.
        [Theory]
        [MemberData(nameof(EnforcedDecidingEntries))]
        public void EnforcedEntry_IsEnforcedByTheWalk(string entryName)
        {
            Action probe;
            Assert.True(Probes.TryGetValue(entryName, out probe),
                entryName + " is marked enforced with a deciding rule but has no probe in this test");
            RecalculationEngine.ClearModules();
            probe();
        }

        [Fact]
        public void EveryProbe_IsForAnEnforcedEntry()
        {
            var enforced = new HashSet<string>(EnforcedDecidingEntries().Select(o => (string)o[0]));
            foreach (string name in Probes.Keys)
                Assert.Contains(name, enforced);
        }

        private static void ProbeScienceCapHeadroom()
        {
            var w = Register();
            var actions = Seeds();
            var later = new GameAction
            {
                UT = 1000, Type = GameActionType.ScienceEarning, SubjectId = "mysteryGoo@KerbinSrfLandedLaunchPad",
                ScienceAwarded = 3f, SubjectMaxValue = 3.9f
            };
            var earlier = new GameAction
            {
                UT = 200, Type = GameActionType.ScienceEarning, SubjectId = "mysteryGoo@KerbinSrfLandedLaunchPad",
                ScienceAwarded = 3f, SubjectMaxValue = 3.9f
            };
            actions.Add(later);
            actions.Add(earlier);
            RecalculationEngine.Recalculate(actions);
            Assert.Equal(3.0, earlier.EffectiveScience, 3);
            Assert.Equal(0.9, later.EffectiveScience, 3);
            Assert.Equal(3.9, w.Science.GetRunningScience(), 3);
        }

        private static void ProbeMilestoneFirstWins()
        {
            Register();
            var actions = Seeds();
            var first = new GameAction { UT = 100, Type = GameActionType.MilestoneAchievement, MilestoneId = "Mun/Landing", MilestoneFundsAwarded = 500f };
            var again = new GameAction { UT = 300, Type = GameActionType.MilestoneAchievement, MilestoneId = "Mun/Landing", MilestoneFundsAwarded = 500f };
            actions.Add(again);
            actions.Add(first);
            RecalculationEngine.Recalculate(actions);
            Assert.True(first.Effective);
            Assert.False(again.Effective);
            Assert.Equal(GameActionNotCountedReason.MilestoneAlreadyAchieved, again.NotCountedReason);
        }

        // A committed flight crossed record thresholds 1-3 at UT 700; after a rewind a new
        // flight (captured later) crossed 1-2 at UT 580. The earliest crossing wins each
        // threshold: the new flight earns its two, the committed row only its third.
        private static void ProbeWorldRecordOncePerThreshold()
        {
            var w = Register();
            var actions = Seeds();
            var committed = new GameAction
            {
                UT = 700, CaptureSequence = 10, Type = GameActionType.MilestoneAchievement,
                MilestoneId = "RecordsAltitude", MilestoneFundsAwarded = 600f, MilestoneRecordThresholds = 3,
                MilestoneRecordBand = new List<RecordThresholdCredit>
                {
                    new RecordThresholdCredit(1, 100f, 0f, 0f),
                    new RecordThresholdCredit(2, 200f, 0f, 0f),
                    new RecordThresholdCredit(3, 300f, 0f, 0f),
                }
            };
            var reclimb = new GameAction
            {
                UT = 580, CaptureSequence = 20, Type = GameActionType.MilestoneAchievement,
                MilestoneId = "RecordsAltitude", MilestoneFundsAwarded = 300f, MilestoneRecordThresholds = 2,
                MilestoneRecordBand = new List<RecordThresholdCredit>
                {
                    new RecordThresholdCredit(1, 100f, 0f, 0f),
                    new RecordThresholdCredit(2, 200f, 0f, 0f),
                }
            };
            var again = new GameAction
            {
                UT = 800, CaptureSequence = 30, Type = GameActionType.MilestoneAchievement,
                MilestoneId = "RecordsAltitude", MilestoneFundsAwarded = 100f,
                MilestoneRecordBand = new List<RecordThresholdCredit> { new RecordThresholdCredit(1, 100f, 0f, 0f) }
            };
            actions.Add(committed);
            actions.Add(reclimb);
            actions.Add(again);
            RecalculationEngine.Recalculate(actions);
            Assert.True(reclimb.Effective);
            Assert.False(reclimb.RecordPartlyCredited);
            Assert.True(committed.Effective);
            Assert.True(committed.RecordPartlyCredited);
            Assert.Equal(300f, committed.CreditedMilestoneFunds);
            Assert.False(again.Effective);
            Assert.Equal(GameActionNotCountedReason.RecordThresholdsAlreadyCredited, again.NotCountedReason);
            Assert.Equal(3, w.Milestones.GetEffectiveMilestoneCount("RecordsAltitude"));
            Assert.Equal(100000.0 + 300.0 + 300.0, w.Funds.GetRunningBalance(), 3);
        }

        private static void ProbeContractCompleteFirstWins()
        {
            Register();
            var actions = Seeds();
            actions.Add(Contract(GameActionType.ContractAccept, 10, "c1"));
            var first = Contract(GameActionType.ContractComplete, 100, "c1");
            var again = Contract(GameActionType.ContractComplete, 200, "c1");
            actions.Add(again);
            actions.Add(first);
            RecalculationEngine.Recalculate(actions);
            Assert.True(first.Effective);
            Assert.False(again.Effective);
            Assert.Equal(GameActionNotCountedReason.ContractAlreadyCompleted, again.NotCountedReason);
        }

        private static void ProbeContractFailFirstOutcomeWins()
        {
            Register();
            var actions = Seeds();
            actions.Add(Contract(GameActionType.ContractAccept, 10, "c1"));
            var complete = Contract(GameActionType.ContractComplete, 100, "c1");
            var fail = Contract(GameActionType.ContractFail, 200, "c1");
            actions.Add(complete);
            actions.Add(fail);
            RecalculationEngine.Recalculate(actions);
            Assert.True(complete.Effective);
            Assert.False(fail.Effective);
            Assert.Equal(GameActionNotCountedReason.ContractOutcomeAfterEnd, fail.NotCountedReason);
        }

        private static void ProbeContractCancelFirstOutcomeWins()
        {
            Register();
            var actions = Seeds();
            actions.Add(Contract(GameActionType.ContractAccept, 10, "c1"));
            var first = Contract(GameActionType.ContractCancel, 100, "c1");
            var again = Contract(GameActionType.ContractCancel, 200, "c1");
            actions.Add(first);
            actions.Add(again);
            RecalculationEngine.Recalculate(actions);
            Assert.True(first.Effective);
            Assert.False(again.Effective);
            Assert.Equal(GameActionNotCountedReason.ContractOutcomeAfterEnd, again.NotCountedReason);
        }

        private static void ProbeParameterFirstWinsWhileOpen()
        {
            Register();
            var actions = Seeds();
            actions.Add(Contract(GameActionType.ContractAccept, 10, "c1"));
            var first = Contract(GameActionType.ContractParameterComplete, 50, "c1", "0");
            var again = Contract(GameActionType.ContractParameterComplete, 60, "c1", "0");
            var completion = Contract(GameActionType.ContractComplete, 100, "c1");
            var afterCompletion = Contract(GameActionType.ContractParameterComplete, 150, "c1", "1");
            actions.Add(afterCompletion);
            actions.Add(completion);
            actions.Add(again);
            actions.Add(first);
            RecalculationEngine.Recalculate(actions);
            Assert.True(first.Effective);
            Assert.False(again.Effective);
            Assert.Equal(GameActionNotCountedReason.ContractAlreadyCompleted, again.NotCountedReason);
            Assert.True(completion.Effective);
            Assert.False(afterCompletion.Effective);
            Assert.Equal(GameActionNotCountedReason.ContractParameterAfterCompletion, afterCompletion.NotCountedReason);
        }

        private static void ProbeFacilityRepairStateRule()
        {
            Register();
            var actions = Seeds();
            const string building = "SpaceCenter/LaunchPad/Facility/mainBuilding";
            actions.Add(new GameAction { UT = 100, Type = GameActionType.FacilityDestruction, FacilityId = building });
            var repair = new GameAction { UT = 200, Type = GameActionType.FacilityRepair, FacilityId = building, FacilityCost = 4000f };
            var again = new GameAction { UT = 300, Type = GameActionType.FacilityRepair, FacilityId = building, FacilityCost = 4000f };
            actions.Add(repair);
            actions.Add(again);
            RecalculationEngine.Recalculate(actions);
            Assert.True(repair.Effective);
            Assert.False(again.Effective);
            Assert.Equal(GameActionNotCountedReason.FacilityAlreadyIntact, again.NotCountedReason);
        }

        // ================================================================
        // Planned entries still behave as their "today" text says
        // ================================================================

        // catches: the survey bonus becoming once per body without the registry entry flipping
        // to enforced.
        [Fact]
        public void PlannedOrbitalSurvey_TodayEverySurveyRowIsCredited()
        {
            LedgerEventIdentityEntry e;
            Assert.True(LedgerEventIdentityRegistry.TryGet(GameActionType.ScienceLabEarning,
                LedgerEventIdentityRegistry.OrbitalSurvey, out e));
            Assert.False(e.EnforcedToday);

            var w = Register();
            var actions = Seeds();
            actions.Add(new GameAction { UT = 100, Type = GameActionType.ScienceLabEarning, ScanBodyName = "Mun", ScienceAwarded = 12f });
            actions.Add(new GameAction { UT = 300, Type = GameActionType.ScienceLabEarning, ScanBodyName = "Mun", ScienceAwarded = 12f });
            RecalculationEngine.Recalculate(actions);
            Assert.Equal(24.0, w.Science.GetRunningScience(), 3);
        }
    }
}
