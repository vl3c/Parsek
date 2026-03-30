using System;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    public class GameActionSerializationTests
    {
        // ================================================================
        // Round-trip tests — one per action type
        // ================================================================

        [Fact]
        public void ScienceEarning_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17500.123456789,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_001",
                SubjectId = "crewReport@MunSrfLandedMidlands",
                ExperimentId = "crewReport",
                Body = "Mun",
                Situation = "SrfLanded",
                Biome = "Midlands",
                ScienceAwarded = 8.5f,
                Method = ScienceMethod.Recovered,
                TransmitScalar = 1.0f,
                SubjectMaxValue = 15.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(original.UT, result.UT);
            Assert.Equal(GameActionType.ScienceEarning, result.Type);
            Assert.Equal("rec_001", result.RecordingId);
            Assert.Equal("crewReport@MunSrfLandedMidlands", result.SubjectId);
            Assert.Equal("crewReport", result.ExperimentId);
            Assert.Equal("Mun", result.Body);
            Assert.Equal("SrfLanded", result.Situation);
            Assert.Equal("Midlands", result.Biome);
            Assert.Equal(8.5f, result.ScienceAwarded);
            Assert.Equal(ScienceMethod.Recovered, result.Method);
            Assert.Equal(1.0f, result.TransmitScalar);
            Assert.Equal(15.0f, result.SubjectMaxValue);
        }

        [Fact]
        public void ScienceEarning_Transmitted_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 18000.0,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_002",
                SubjectId = "temperatureScan@KerbinFlyingLowGrasslands",
                ExperimentId = "temperatureScan",
                Body = "Kerbin",
                Situation = "FlyingLow",
                Biome = "Grasslands",
                ScienceAwarded = 2.4f,
                Method = ScienceMethod.Transmitted,
                TransmitScalar = 0.3f,
                SubjectMaxValue = 8.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(ScienceMethod.Transmitted, result.Method);
            Assert.Equal(0.3f, result.TransmitScalar);
            Assert.Equal(2.4f, result.ScienceAwarded);
        }

        [Fact]
        public void ScienceSpending_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 18000.0,
                Type = GameActionType.ScienceSpending,
                Sequence = 2,
                NodeId = "survivability",
                Cost = 45.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(18000.0, result.UT);
            Assert.Equal(GameActionType.ScienceSpending, result.Type);
            Assert.Null(result.RecordingId);
            Assert.Equal(2, result.Sequence);
            Assert.Equal("survivability", result.NodeId);
            Assert.Equal(45.0f, result.Cost);
        }

        [Fact]
        public void FundsEarning_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17100.0,
                Type = GameActionType.FundsEarning,
                RecordingId = "rec_003",
                FundsAwarded = 50000.5f,
                FundsSource = FundsEarningSource.ContractComplete
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.FundsEarning, result.Type);
            Assert.Equal("rec_003", result.RecordingId);
            Assert.Equal(50000.5f, result.FundsAwarded);
            Assert.Equal(FundsEarningSource.ContractComplete, result.FundsSource);
        }

        [Fact]
        public void FundsEarning_Recovery_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17200.0,
                Type = GameActionType.FundsEarning,
                RecordingId = "rec_004",
                FundsAwarded = 12300.0f,
                FundsSource = FundsEarningSource.Recovery
            };

            var result = RoundTrip(original);

            Assert.Equal(FundsEarningSource.Recovery, result.FundsSource);
            Assert.Equal(12300.0f, result.FundsAwarded);
        }

        [Fact]
        public void FundsSpending_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17050.0,
                Type = GameActionType.FundsSpending,
                RecordingId = "rec_005",
                FundsSpent = 25000.0f,
                FundsSpendingSource = FundsSpendingSource.VesselBuild
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.FundsSpending, result.Type);
            Assert.Equal("rec_005", result.RecordingId);
            Assert.Equal(25000.0f, result.FundsSpent);
            Assert.Equal(FundsSpendingSource.VesselBuild, result.FundsSpendingSource);
        }

        [Fact]
        public void FundsSpending_FacilityUpgrade_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17300.0,
                Type = GameActionType.FundsSpending,
                Sequence = 1,
                FundsSpent = 150000.0f,
                FundsSpendingSource = FundsSpendingSource.FacilityUpgrade
            };

            var result = RoundTrip(original);

            Assert.Null(result.RecordingId);
            Assert.Equal(1, result.Sequence);
            Assert.Equal(FundsSpendingSource.FacilityUpgrade, result.FundsSpendingSource);
        }

        [Fact]
        public void MilestoneAchievement_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17500.0,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = "rec_006",
                MilestoneId = "FirstMunLanding",
                MilestoneFundsAwarded = 10000.0f,
                MilestoneRepAwarded = 15.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.MilestoneAchievement, result.Type);
            Assert.Equal("rec_006", result.RecordingId);
            Assert.Equal("FirstMunLanding", result.MilestoneId);
            Assert.Equal(10000.0f, result.MilestoneFundsAwarded);
            Assert.Equal(15.0f, result.MilestoneRepAwarded);
        }

        [Fact]
        public void ContractAccept_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ContractAccept,
                Sequence = 0,
                ContractId = "contract-guid-001",
                ContractType = "ExploreBody",
                ContractTitle = "Explore the Mun",
                AdvanceFunds = 5000.0f,
                DeadlineUT = 50000.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.ContractAccept, result.Type);
            Assert.Equal("contract-guid-001", result.ContractId);
            Assert.Equal("ExploreBody", result.ContractType);
            Assert.Equal("Explore the Mun", result.ContractTitle);
            Assert.Equal(5000.0f, result.AdvanceFunds);
            Assert.Equal(50000.0f, result.DeadlineUT);
        }

        [Fact]
        public void ContractAccept_NoDeadline_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ContractAccept,
                ContractId = "contract-guid-002",
                ContractType = "TourismContract",
                ContractTitle = "Fly tourists around Kerbin",
                AdvanceFunds = 2000.0f,
                DeadlineUT = float.NaN
            };

            var result = RoundTrip(original);

            Assert.True(float.IsNaN(result.DeadlineUT), "DeadlineUT should be NaN for no-deadline contracts");
        }

        [Fact]
        public void ContractComplete_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17500.0,
                Type = GameActionType.ContractComplete,
                RecordingId = "rec_007",
                ContractId = "contract-guid-001",
                FundsReward = 40000.0f,
                RepReward = 15.0f,
                ScienceReward = 5.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.ContractComplete, result.Type);
            Assert.Equal("rec_007", result.RecordingId);
            Assert.Equal("contract-guid-001", result.ContractId);
            Assert.Equal(40000.0f, result.FundsReward);
            Assert.Equal(15.0f, result.RepReward);
            Assert.Equal(5.0f, result.ScienceReward);
        }

        [Fact]
        public void ContractFail_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 18000.0,
                Type = GameActionType.ContractFail,
                ContractId = "contract-guid-003",
                FundsPenalty = 10000.0f,
                RepPenalty = 8.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.ContractFail, result.Type);
            Assert.Null(result.RecordingId);
            Assert.Equal("contract-guid-003", result.ContractId);
            Assert.Equal(10000.0f, result.FundsPenalty);
            Assert.Equal(8.0f, result.RepPenalty);
        }

        [Fact]
        public void ContractCancel_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17800.0,
                Type = GameActionType.ContractCancel,
                Sequence = 1,
                ContractId = "contract-guid-004",
                FundsPenalty = 5000.0f,
                RepPenalty = 3.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.ContractCancel, result.Type);
            Assert.Equal(1, result.Sequence);
            Assert.Equal("contract-guid-004", result.ContractId);
            Assert.Equal(5000.0f, result.FundsPenalty);
            Assert.Equal(3.0f, result.RepPenalty);
        }

        [Fact]
        public void ReputationEarning_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17500.0,
                Type = GameActionType.ReputationEarning,
                RecordingId = "rec_008",
                NominalRep = 50.0f,
                RepSource = ReputationSource.ContractComplete
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.ReputationEarning, result.Type);
            Assert.Equal("rec_008", result.RecordingId);
            Assert.Equal(50.0f, result.NominalRep);
            Assert.Equal(ReputationSource.ContractComplete, result.RepSource);
        }

        [Fact]
        public void ReputationPenalty_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17600.0,
                Type = GameActionType.ReputationPenalty,
                RecordingId = "rec_009",
                NominalPenalty = 25.0f,
                RepPenaltySource = ReputationPenaltySource.KerbalDeath
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.ReputationPenalty, result.Type);
            Assert.Equal("rec_009", result.RecordingId);
            Assert.Equal(25.0f, result.NominalPenalty);
            Assert.Equal(ReputationPenaltySource.KerbalDeath, result.RepPenaltySource);
        }

        [Fact]
        public void KerbalAssignment_Recovered_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec_010",
                KerbalName = "Jebediah Kerman",
                KerbalRole = "Pilot",
                StartUT = 17000.0f,
                EndUT = 18500.0f,
                KerbalEndStateField = KerbalEndState.Recovered,
                XpGained = 4.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.KerbalAssignment, result.Type);
            Assert.Equal("rec_010", result.RecordingId);
            Assert.Equal("Jebediah Kerman", result.KerbalName);
            Assert.Equal("Pilot", result.KerbalRole);
            Assert.Equal(17000.0f, result.StartUT);
            Assert.Equal(18500.0f, result.EndUT);
            Assert.Equal(KerbalEndState.Recovered, result.KerbalEndStateField);
            Assert.Equal(4.0f, result.XpGained);
        }

        [Fact]
        public void KerbalAssignment_Stranded_NaNEndUT_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec_011",
                KerbalName = "Jebediah Kerman",
                KerbalRole = "Pilot",
                StartUT = 17000.0f,
                EndUT = float.NaN,
                KerbalEndStateField = KerbalEndState.Unknown,
                XpGained = 0.0f
            };

            var result = RoundTrip(original);

            Assert.True(float.IsNaN(result.EndUT), "EndUT should be NaN for open-ended kerbals");
            Assert.Equal(KerbalEndState.Unknown, result.KerbalEndStateField);
        }

        [Fact]
        public void KerbalAssignment_Dead_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec_012",
                KerbalName = "Bill Kerman",
                KerbalRole = "Engineer",
                StartUT = 17000.0f,
                EndUT = 17200.0f,
                KerbalEndStateField = KerbalEndState.Dead,
                XpGained = 0.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(KerbalEndState.Dead, result.KerbalEndStateField);
        }

        [Fact]
        public void KerbalHire_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 18000.0,
                Type = GameActionType.KerbalHire,
                Sequence = 0,
                KerbalName = "Wehrner Kerman",
                KerbalRole = "Pilot",
                HireCost = 25000.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.KerbalHire, result.Type);
            Assert.Null(result.RecordingId);
            Assert.Equal("Wehrner Kerman", result.KerbalName);
            Assert.Equal("Pilot", result.KerbalRole);
            Assert.Equal(25000.0f, result.HireCost);
        }

        [Fact]
        public void KerbalRescue_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 19000.0,
                Type = GameActionType.KerbalRescue,
                RecordingId = "rec_013",
                KerbalName = "Stranded Kerman",
                KerbalRole = "Scientist",
                EndUT = 19500.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.KerbalRescue, result.Type);
            Assert.Equal("rec_013", result.RecordingId);
            Assert.Equal("Stranded Kerman", result.KerbalName);
            Assert.Equal("Scientist", result.KerbalRole);
            Assert.Equal(19500.0f, result.EndUT);
        }

        [Fact]
        public void KerbalStandIn_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.KerbalStandIn,
                KerbalName = "Hanley Kerman",
                KerbalRole = "Pilot",
                ReplacesKerbal = "Jebediah Kerman",
                Courage = 0.65f,
                Stupidity = 0.3f
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.KerbalStandIn, result.Type);
            Assert.Equal("Hanley Kerman", result.KerbalName);
            Assert.Equal("Pilot", result.KerbalRole);
            Assert.Equal("Jebediah Kerman", result.ReplacesKerbal);
            Assert.Equal(0.65f, result.Courage);
            Assert.Equal(0.3f, result.Stupidity);
        }

        [Fact]
        public void FacilityUpgrade_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 18500.0,
                Type = GameActionType.FacilityUpgrade,
                Sequence = 0,
                FacilityId = "LaunchPad",
                ToLevel = 2,
                FacilityCost = 150000.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.FacilityUpgrade, result.Type);
            Assert.Null(result.RecordingId);
            Assert.Equal("LaunchPad", result.FacilityId);
            Assert.Equal(2, result.ToLevel);
            Assert.Equal(150000.0f, result.FacilityCost);
        }

        [Fact]
        public void FacilityDestruction_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17800.0,
                Type = GameActionType.FacilityDestruction,
                RecordingId = "rec_014",
                FacilityId = "LaunchPad"
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.FacilityDestruction, result.Type);
            Assert.Equal("rec_014", result.RecordingId);
            Assert.Equal("LaunchPad", result.FacilityId);
        }

        [Fact]
        public void FacilityRepair_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 18000.0,
                Type = GameActionType.FacilityRepair,
                Sequence = 0,
                FacilityId = "LaunchPad",
                FacilityCost = 30000.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.FacilityRepair, result.Type);
            Assert.Equal("LaunchPad", result.FacilityId);
            Assert.Equal(30000.0f, result.FacilityCost);
        }

        [Fact]
        public void StrategyActivate_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 19000.0,
                Type = GameActionType.StrategyActivate,
                Sequence = 0,
                StrategyId = "UnpaidResearch",
                SourceResource = StrategyResource.Reputation,
                TargetResource = StrategyResource.Science,
                Commitment = 0.10f,
                SetupCost = 5.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.StrategyActivate, result.Type);
            Assert.Equal("UnpaidResearch", result.StrategyId);
            Assert.Equal(StrategyResource.Reputation, result.SourceResource);
            Assert.Equal(StrategyResource.Science, result.TargetResource);
            Assert.Equal(0.10f, result.Commitment);
            Assert.Equal(5.0f, result.SetupCost);
        }

        [Fact]
        public void StrategyDeactivate_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 20000.0,
                Type = GameActionType.StrategyDeactivate,
                Sequence = 0,
                StrategyId = "UnpaidResearch"
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.StrategyDeactivate, result.Type);
            Assert.Equal("UnpaidResearch", result.StrategyId);
        }

        [Fact]
        public void FundsInitial_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 25000.0f
            };

            var result = RoundTrip(original);

            Assert.Equal(GameActionType.FundsInitial, result.Type);
            Assert.Equal(0.0, result.UT);
            Assert.Equal(25000.0f, result.InitialFunds);
        }

        // ================================================================
        // Derived fields are NOT serialized
        // ================================================================

        [Fact]
        public void DerivedFields_NotSerialized()
        {
            // effectiveScience, effectiveRep, effective, affordable are derived —
            // they should never appear in the serialized ConfigNode.
            var action = new GameAction
            {
                UT = 17500.0,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_001",
                SubjectId = "crewReport@KerbinSrfLandedShores",
                ExperimentId = "crewReport",
                Body = "Kerbin",
                Situation = "SrfLanded",
                Biome = "Shores",
                ScienceAwarded = 5.0f,
                Method = ScienceMethod.Recovered,
                TransmitScalar = 1.0f,
                SubjectMaxValue = 8.0f
            };

            var parent = new ConfigNode("ROOT");
            action.SerializeInto(parent);
            var node = parent.GetNode("GAME_ACTION");

            // These derived field keys must not be present
            Assert.Null(node.GetValue("effectiveScience"));
            Assert.Null(node.GetValue("effectiveRep"));
            Assert.Null(node.GetValue("effective"));
            Assert.Null(node.GetValue("affordable"));
        }

        // ================================================================
        // Backward compatibility — unknown fields are ignored gracefully
        // ================================================================

        [Fact]
        public void UnknownFields_IgnoredGracefully()
        {
            // Simulate a ConfigNode from a future version with extra fields
            var node = new ConfigNode("GAME_ACTION");
            node.AddValue("ut", "17500");
            node.AddValue("type", ((int)GameActionType.ScienceEarning).ToString());
            node.AddValue("recordingId", "rec_future");
            node.AddValue("subjectId", "mysteryExperiment@MinmusSrfLandedFlats");
            node.AddValue("experimentId", "mysteryExperiment");
            node.AddValue("body", "Minmus");
            node.AddValue("situation", "SrfLanded");
            node.AddValue("biome", "Flats");
            node.AddValue("scienceAwarded", "12.5");
            node.AddValue("method", "1");
            node.AddValue("transmitScalar", "1");
            node.AddValue("subjectMaxValue", "20");
            // Future unknown fields
            node.AddValue("futureField1", "someValue");
            node.AddValue("futureField2", "42");
            node.AddNode("FUTURE_SUBNODE");

            var result = GameAction.DeserializeFrom(node);

            // Known fields deserialized correctly
            Assert.Equal(17500.0, result.UT);
            Assert.Equal(GameActionType.ScienceEarning, result.Type);
            Assert.Equal("rec_future", result.RecordingId);
            Assert.Equal("mysteryExperiment@MinmusSrfLandedFlats", result.SubjectId);
            Assert.Equal(12.5f, result.ScienceAwarded);
            Assert.Equal(ScienceMethod.Recovered, result.Method);
        }

        [Fact]
        public void UnknownActionType_DefaultsToZero()
        {
            // A future action type that doesn't exist in the current enum
            var node = new ConfigNode("GAME_ACTION");
            node.AddValue("ut", "17500");
            node.AddValue("type", "999");

            // Should not throw — logs a warning instead
            ParsekLog.SuppressLogging = true;
            try
            {
                var result = GameAction.DeserializeFrom(node);
                // Type defaults to 0 (ScienceEarning) since the unknown type isn't set
                Assert.Equal(17500.0, result.UT);
            }
            finally
            {
                ParsekLog.SuppressLogging = false;
            }
        }

        // ================================================================
        // Null recordingId is not serialized
        // ================================================================

        [Fact]
        public void NullRecordingId_NotSerialized()
        {
            var action = new GameAction
            {
                UT = 18000.0,
                Type = GameActionType.ScienceSpending,
                RecordingId = null,
                NodeId = "basicRocketry",
                Cost = 5.0f
            };

            var parent = new ConfigNode("ROOT");
            action.SerializeInto(parent);
            var node = parent.GetNode("GAME_ACTION");

            Assert.Null(node.GetValue("recordingId"));
        }

        // ================================================================
        // Sequence 0 is not serialized (default)
        // ================================================================

        [Fact]
        public void ZeroSequence_NotSerialized()
        {
            var action = new GameAction
            {
                UT = 18000.0,
                Type = GameActionType.ScienceSpending,
                Sequence = 0,
                NodeId = "basicRocketry",
                Cost = 5.0f
            };

            var parent = new ConfigNode("ROOT");
            action.SerializeInto(parent);
            var node = parent.GetNode("GAME_ACTION");

            Assert.Null(node.GetValue("seq"));
        }

        [Fact]
        public void NonZeroSequence_IsSerialized()
        {
            var action = new GameAction
            {
                UT = 18000.0,
                Type = GameActionType.ScienceSpending,
                Sequence = 3,
                NodeId = "advRocketry",
                Cost = 90.0f
            };

            var parent = new ConfigNode("ROOT");
            action.SerializeInto(parent);
            var node = parent.GetNode("GAME_ACTION");

            Assert.Equal("3", node.GetValue("seq"));
        }

        // ================================================================
        // Float precision with InvariantCulture round-trip
        // ================================================================

        [Fact]
        public void FloatPrecision_InvariantCulture_RoundTrip()
        {
            var original = new GameAction
            {
                UT = 17123.456789012345,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_precision",
                SubjectId = "precision@test",
                ExperimentId = "precision",
                Body = "Kerbin",
                Situation = "SrfLanded",
                Biome = "KSC",
                ScienceAwarded = 3.141593f,
                Method = ScienceMethod.Recovered,
                TransmitScalar = 0.6789f,
                SubjectMaxValue = 99.99f
            };

            var result = RoundTrip(original);

            Assert.Equal(original.UT, result.UT);
            Assert.Equal(original.ScienceAwarded, result.ScienceAwarded);
            Assert.Equal(original.TransmitScalar, result.TransmitScalar);
            Assert.Equal(original.SubjectMaxValue, result.SubjectMaxValue);
        }

        // ================================================================
        // All FundsSource enum values round-trip
        // ================================================================

        [Theory]
        [InlineData(FundsEarningSource.ContractComplete)]
        [InlineData(FundsEarningSource.ContractAdvance)]
        [InlineData(FundsEarningSource.Recovery)]
        [InlineData(FundsEarningSource.Milestone)]
        [InlineData(FundsEarningSource.Other)]
        public void FundsSource_AllValues_RoundTrip(FundsEarningSource source)
        {
            var original = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.FundsEarning,
                FundsAwarded = 1000.0f,
                FundsSource = source
            };

            var result = RoundTrip(original);
            Assert.Equal(source, result.FundsSource);
        }

        // ================================================================
        // All FundsSpendingSource enum values round-trip
        // ================================================================

        [Theory]
        [InlineData(FundsSpendingSource.VesselBuild)]
        [InlineData(FundsSpendingSource.FacilityUpgrade)]
        [InlineData(FundsSpendingSource.FacilityRepair)]
        [InlineData(FundsSpendingSource.KerbalHire)]
        [InlineData(FundsSpendingSource.ContractPenalty)]
        [InlineData(FundsSpendingSource.Strategy)]
        [InlineData(FundsSpendingSource.Other)]
        public void FundsSpendingSource_AllValues_RoundTrip(FundsSpendingSource source)
        {
            var original = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.FundsSpending,
                FundsSpent = 500.0f,
                FundsSpendingSource = source
            };

            var result = RoundTrip(original);
            Assert.Equal(source, result.FundsSpendingSource);
        }

        // ================================================================
        // All ReputationSource enum values round-trip
        // ================================================================

        [Theory]
        [InlineData(ReputationSource.ContractComplete)]
        [InlineData(ReputationSource.Milestone)]
        [InlineData(ReputationSource.Other)]
        public void ReputationSource_AllValues_RoundTrip(ReputationSource source)
        {
            var original = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ReputationEarning,
                NominalRep = 10.0f,
                RepSource = source
            };

            var result = RoundTrip(original);
            Assert.Equal(source, result.RepSource);
        }

        // ================================================================
        // All ReputationPenaltySource enum values round-trip
        // ================================================================

        [Theory]
        [InlineData(ReputationPenaltySource.ContractFail)]
        [InlineData(ReputationPenaltySource.ContractDecline)]
        [InlineData(ReputationPenaltySource.KerbalDeath)]
        [InlineData(ReputationPenaltySource.Strategy)]
        [InlineData(ReputationPenaltySource.Other)]
        public void ReputationPenaltySource_AllValues_RoundTrip(ReputationPenaltySource source)
        {
            var original = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ReputationPenalty,
                NominalPenalty = 5.0f,
                RepPenaltySource = source
            };

            var result = RoundTrip(original);
            Assert.Equal(source, result.RepPenaltySource);
        }

        // ================================================================
        // All KerbalEndState enum values round-trip
        // ================================================================

        [Theory]
        [InlineData(KerbalEndState.Aboard)]
        [InlineData(KerbalEndState.Dead)]
        [InlineData(KerbalEndState.Recovered)]
        [InlineData(KerbalEndState.Unknown)]
        public void KerbalEndState_AllValues_RoundTrip(KerbalEndState state)
        {
            var original = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec_state",
                KerbalName = "Test Kerman",
                KerbalRole = "Pilot",
                StartUT = 17000.0f,
                EndUT = state == KerbalEndState.Unknown ? float.NaN : 18000.0f,
                KerbalEndStateField = state,
                XpGained = 0f
            };

            var result = RoundTrip(original);
            Assert.Equal(state, result.KerbalEndStateField);
        }

        // ================================================================
        // All StrategyResource enum values round-trip
        // ================================================================

        [Theory]
        [InlineData(StrategyResource.Funds)]
        [InlineData(StrategyResource.Science)]
        [InlineData(StrategyResource.Reputation)]
        public void StrategyResource_AllValues_RoundTrip(StrategyResource resource)
        {
            var original = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.StrategyActivate,
                StrategyId = "TestStrategy",
                SourceResource = resource,
                TargetResource = StrategyResource.Funds,
                Commitment = 0.05f,
                SetupCost = 1.0f
            };

            var result = RoundTrip(original);
            Assert.Equal(resource, result.SourceResource);
        }

        // ================================================================
        // Multiple actions in one parent node
        // ================================================================

        [Fact]
        public void MultipleActions_SerializeDeserialize()
        {
            var parent = new ConfigNode("LEDGER");

            var a1 = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 25000.0f
            };
            var a2 = new GameAction
            {
                UT = 17100.0,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = "rec_001",
                MilestoneId = "FirstLaunch",
                MilestoneFundsAwarded = 5000.0f,
                MilestoneRepAwarded = 5.0f
            };

            a1.SerializeInto(parent);
            a2.SerializeInto(parent);

            var nodes = parent.GetNodes("GAME_ACTION");
            Assert.Equal(2, nodes.Length);

            var r1 = GameAction.DeserializeFrom(nodes[0]);
            var r2 = GameAction.DeserializeFrom(nodes[1]);

            Assert.Equal(GameActionType.FundsInitial, r1.Type);
            Assert.Equal(25000.0f, r1.InitialFunds);
            Assert.Equal(GameActionType.MilestoneAchievement, r2.Type);
            Assert.Equal("FirstLaunch", r2.MilestoneId);
        }

        // ================================================================
        // Helper
        // ================================================================

        private static GameAction RoundTrip(GameAction original)
        {
            var parent = new ConfigNode("ROOT");
            original.SerializeInto(parent);
            var node = parent.GetNode("GAME_ACTION");
            Assert.NotNull(node);
            return GameAction.DeserializeFrom(node);
        }
    }
}
