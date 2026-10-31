using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Field-level save/load round-trip of every ledger row type. <see cref="GameAction"/> is one
    /// class whose per-type codec writes a subset of its fields, so the contract here is the
    /// persisted subset per <see cref="GameActionType"/>: each listed field must come back equal,
    /// and every unlisted field must NOT come back (a field the codec writes but the table omits
    /// is caught as well as the reverse). A field that is in no row's subset and not in
    /// <see cref="Derived"/> reds <see cref="EveryField_IsPersistedByARowTypeOrDeclaredDerived"/>.
    /// Runs under a comma-decimal culture, so a culture-sensitive number format reds too.
    /// </summary>
    [Collection("Sequential")]
    [SaveRoundTripCovers(typeof(GameAction))]
    public class GameActionSaveRoundTripTests : IDisposable
    {
        public GameActionSaveRoundTripTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        /// <summary>Written for every row type by the common header of <c>GameAction.SerializeInto</c>.</summary>
        private static readonly string[] Header =
        {
            "ActionId", "UT", "Type", "RecordingId", "Sequence", "CaptureSequence", "CapturedUT",
            "ConversionCause", "IncomeReason", "InsideReputationSeed", "ParsekAuthored",
        };

        /// <summary>Recalculation-walk outputs and in-memory dedup flags; never written by design.</summary>
        private static readonly string[] Derived =
        {
            "ConversionShare", "Effective", "NotCountedReason",
            "RecordPartlyCredited", "RecordCreditedFunds", "RecordCreditedRep", "RecordCreditedScience",
            "RecordThresholdsCredited", "RecordThresholdsTotal",
            "TakeBackFunds", "TakeBackRep", "TakeBackScience", "TakeBackWitness",
            "HasScienceCaptureIdentity", "HasEventCaptureIdentity",
            "EffectiveScience", "Affordable", "UnaffordableRunningScience", "EffectiveRep",
            "TransformedFundsReward", "TransformedScienceReward", "TransformedRepReward",
        };

        private static readonly string[] ContractOutcome = { "ContractId", "ContractTitle" };
        private static readonly string[] Kerbal = { "KerbalName", "KerbalRole" };

        /// <summary>The persisted payload of each row type, beyond <see cref="Header"/>.</summary>
        private static readonly Dictionary<GameActionType, string[]> Payload = new Dictionary<GameActionType, string[]>
        {
            [GameActionType.ScienceEarning] = new[]
            {
                "SubjectId", "ExperimentId", "Body", "Situation", "Biome", "ScienceAwarded", "Method",
                "TransmitScalar", "SubjectMaxValue", "ScienceGainMultiplier", "StartUT", "EndUT",
                "ProducerId", "SliceStartUT", "SliceEndUT",
            },
            [GameActionType.ScienceSpending] = new[] { "NodeId", "Cost" },
            [GameActionType.StrategyScienceDebit] = new[] { "Cost", "ConversionSource" },
            [GameActionType.StrategyScienceCredit] = new[] { "ScienceAwarded" },
            [GameActionType.FundsEarning] = new[] { "FundsAwarded", "FundsSource", "DedupKey" },
            [GameActionType.FundsSpending] = new[] { "FundsSpent", "FundsSpendingSource", "DedupKey" },
            [GameActionType.MilestoneAchievement] = new[]
            {
                "MilestoneId", "MilestoneFundsAwarded", "MilestoneRepAwarded", "MilestoneScienceAwarded",
                "MilestoneRecordThresholds", "MilestoneRecordBand",
            },
            [GameActionType.ContractAccept] = new[]
            {
                "ContractId", "ContractType", "ContractTitle", "ContractStockId", "ContractGoalKey",
                "AdvanceFunds", "DeadlineUT", "FundsPenalty", "RepPenalty",
            },
            [GameActionType.ContractComplete] = ContractOutcome.Concat(new[] { "FundsReward", "RepReward", "ScienceReward" }).ToArray(),
            [GameActionType.ContractParameterComplete] = ContractOutcome
                .Concat(new[] { "ContractParameterId", "FundsReward", "RepReward", "ScienceReward" }).ToArray(),
            [GameActionType.ContractFail] = ContractOutcome.Concat(new[] { "FundsPenalty", "RepPenalty" }).ToArray(),
            [GameActionType.ContractCancel] = ContractOutcome
                .Concat(new[] { "FundsPenalty", "RepPenalty", "ContractRequirementsLost" }).ToArray(),
            [GameActionType.ReputationEarning] = new[] { "NominalRep", "RepSource" },
            [GameActionType.ReputationPenalty] = new[] { "NominalPenalty", "RepPenaltySource" },
            [GameActionType.KerbalAssignment] = Kerbal
                .Concat(new[] { "StartUT", "EndUT", "KerbalEndStateField", "XpGained" }).ToArray(),
            [GameActionType.KerbalHire] = Kerbal.Concat(new[] { "HireCost" }).ToArray(),
            [GameActionType.KerbalDismissal] = Kerbal,
            [GameActionType.KerbalRescue] = Kerbal.Concat(new[] { "EndUT" }).ToArray(),
            [GameActionType.KerbalStandIn] = Kerbal.Concat(new[] { "ReplacesKerbal", "Courage", "Stupidity" }).ToArray(),
            [GameActionType.KerbalExperience] = Kerbal.Concat(new[] { "KerbalCareerEntries" }).ToArray(),
            [GameActionType.KerbalRecovered] = Kerbal,
            [GameActionType.VesselRecovered] = new[] { "RecoveredVesselName", "RecoveredVesselPid" },
            [GameActionType.ResourceScanUnlocked] = new[] { "ScanBodyName", "ScanBiomeName" },
            [GameActionType.ScienceLabEarning] = new[] { "ScienceAwarded", "Method", "ScanBodyName" },
            [GameActionType.StockPoolAdjustment] = new[] { "AdjustmentPool", "AdjustmentAmount", "AdjustmentCarriedFrom" },
            [GameActionType.FacilityUpgrade] = new[] { "FacilityId", "ToLevel", "FacilityCost", "FacilityDowngrade" },
            [GameActionType.FacilityDestruction] = new[] { "FacilityId" },
            [GameActionType.FacilityRepair] = new[] { "FacilityId", "FacilityCost" },
            [GameActionType.StrategyActivate] = new[]
            {
                "StrategyId", "SourceResource", "TargetResource", "Commitment",
                "SetupCost", "SetupScienceCost", "SetupReputationCost",
            },
            [GameActionType.StrategyDeactivate] = new[] { "StrategyId" },
            [GameActionType.FundsInitial] = new[] { "InitialFunds", "InitialFundsConfirmedZero" },
            [GameActionType.ScienceInitial] = new[] { "InitialScience" },
            [GameActionType.ReputationInitial] = new[] { "InitialReputation", "SeedPendingRecordingIds", "SeedCaptureUT" },
        };

        public static IEnumerable<object[]> RowTypes() =>
            Enum.GetValues(typeof(GameActionType)).Cast<GameActionType>()
                .Where(t => !LedgerLoadMigration.IsReservedRouteActionType(t))
                .Select(t => new object[] { t });

        [Fact]
        public void EveryRowType_HasAPayloadEntry()
        {
            var missing = RowTypes().Select(r => (GameActionType)r[0]).Where(t => !Payload.ContainsKey(t)).ToList();
            Assert.True(missing.Count == 0,
                "GameActionType(s) with no persisted-payload entry in this test: " + string.Join(", ", missing)
                + ". Add the type's serialized fields to Payload.");
        }

        [Fact]
        public void EveryField_IsPersistedByARowTypeOrDeclaredDerived()
        {
            var classified = new HashSet<string>(Header.Concat(Derived).Concat(Payload.Values.SelectMany(v => v)));
            var unclassified = FieldRoundTrip.GetFields(typeof(GameAction))
                .Select(FieldRoundTrip.LogicalName)
                .Where(n => !classified.Contains(n))
                .ToList();
            Assert.True(unclassified.Count == 0,
                "GameAction field(s) neither saved by any row type nor declared derived: "
                + string.Join(", ", unclassified)
                + ". Serialize the field in its row type's codec (and list it in Payload), or add it to "
                + "Derived if the recalculation walk recomputes it.");
        }

        [Theory]
        [MemberData(nameof(RowTypes))]
        public void Row_RoundTripsItsPayloadAndNothingElse(GameActionType type)
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(new FieldSpec[0], seed: (int)type * 1000);
                var written = rt.Build<GameAction>();
                written.Type = type;

                var parent = new ConfigNode("LEDGER");
                written.SerializeInto(parent);
                var read = GameAction.DeserializeFrom(parent.GetNode("GAME_ACTION"));

                var persisted = new HashSet<string>(Header.Concat(Payload[type]));
                var problems = new List<string>();
                foreach (var f in FieldRoundTrip.GetFields(typeof(GameAction)))
                {
                    string name = FieldRoundTrip.LogicalName(f);
                    var diff = rt.Diff(f.GetValue(written), f.GetValue(read), type + "." + name);
                    if (persisted.Contains(name))
                        problems.AddRange(diff);
                    else if (diff.Count == 0)
                        problems.Add(type + "." + name + ": round-trips but is not in this row type's Payload list");
                }

                Assert.True(problems.Count == 0, FieldRoundTrip.Report("GameAction " + type, problems));
            });
        }

        [Fact]
        public void Harness_DetectsAFieldTheCodecDrops()
        {
            // Proves the gate can fail: a row whose payload claims a field the codec never
            // writes must report it.
            var rt = new FieldRoundTrip(new FieldSpec[0]);
            var written = rt.Build<GameAction>();
            written.Type = GameActionType.KerbalDismissal;
            var parent = new ConfigNode("LEDGER");
            written.SerializeInto(parent);
            var read = GameAction.DeserializeFrom(parent.GetNode("GAME_ACTION"));
            var field = FieldRoundTrip.GetFields(typeof(GameAction)).Single(f => f.Name == "HireCost");
            Assert.NotEmpty(rt.Diff(field.GetValue(written), field.GetValue(read), "HireCost"));
        }
    }
}
