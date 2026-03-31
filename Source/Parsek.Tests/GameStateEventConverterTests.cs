using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GameStateEventConverterTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GameStateEventConverterTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helper — build GameStateEvent
        // ================================================================

        private static GameStateEvent MakeEvent(GameStateEventType type, double ut,
            string key = "", string detail = "", double valBefore = 0, double valAfter = 0)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key,
                detail = detail,
                valueBefore = valBefore,
                valueAfter = valAfter
            };
        }

        // ================================================================
        // TechResearched -> ScienceSpending
        // ================================================================

        [Fact]
        public void ConvertEvent_TechResearched_ReturnsScienceSpending()
        {
            var evt = MakeEvent(GameStateEventType.TechResearched, 1000.0,
                key: "survivability", detail: "cost=45;parts=mk1pod");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec1");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ScienceSpending, action.Type);
            Assert.Equal("survivability", action.NodeId);
            Assert.Equal(45f, action.Cost);
            Assert.Equal(1000.0, action.UT);
            Assert.Equal("rec1", action.RecordingId);
        }

        [Fact]
        public void ConvertEvent_TechResearched_NoCost_DefaultsToZero()
        {
            var evt = MakeEvent(GameStateEventType.TechResearched, 500.0,
                key: "basicRocketry", detail: "parts=fuelTank");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec2");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ScienceSpending, action.Type);
            Assert.Equal("basicRocketry", action.NodeId);
            Assert.Equal(0f, action.Cost);
        }

        // ================================================================
        // PartPurchased -> FundsSpending
        // ================================================================

        [Fact]
        public void ConvertEvent_PartPurchased_ReturnsFundsSpending()
        {
            var evt = MakeEvent(GameStateEventType.PartPurchased, 2000.0,
                key: "mk1pod", detail: "cost=600");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec3");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.FundsSpending, action.Type);
            Assert.Equal(600f, action.FundsSpent);
            Assert.Equal(FundsSpendingSource.Other, action.FundsSpendingSource);
            Assert.Equal("rec3", action.RecordingId);
        }

        // ================================================================
        // FacilityUpgraded -> FacilityUpgrade
        // ================================================================

        [Fact]
        public void ConvertEvent_FacilityUpgraded_ReturnsFacilityUpgrade()
        {
            // Normalized level 0.5 = level 1 (out of 2), target = round(0.5*2) = 1
            var evt = MakeEvent(GameStateEventType.FacilityUpgraded, 3000.0,
                key: "LaunchPad", detail: "cost=75000",
                valBefore: 0.0, valAfter: 0.5);
            var action = GameStateEventConverter.ConvertEvent(evt, "rec4");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.FacilityUpgrade, action.Type);
            Assert.Equal("LaunchPad", action.FacilityId);
            Assert.Equal(1, action.ToLevel);
            Assert.Equal(75000f, action.FacilityCost);
        }

        [Fact]
        public void ConvertEvent_FacilityUpgraded_FullUpgrade_Level2()
        {
            // Normalized level 1.0 = level 2 (fully upgraded)
            var evt = MakeEvent(GameStateEventType.FacilityUpgraded, 4000.0,
                key: "VehicleAssemblyBuilding", valBefore: 0.5, valAfter: 1.0);
            var action = GameStateEventConverter.ConvertEvent(evt, "rec5");

            Assert.NotNull(action);
            Assert.Equal(2, action.ToLevel);
        }

        // ================================================================
        // BuildingDestroyed -> FacilityDestruction
        // ================================================================

        [Fact]
        public void ConvertEvent_BuildingDestroyed_ReturnsFacilityDestruction()
        {
            var evt = MakeEvent(GameStateEventType.BuildingDestroyed, 5000.0,
                key: "LaunchPad");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec6");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.FacilityDestruction, action.Type);
            Assert.Equal("LaunchPad", action.FacilityId);
        }

        // ================================================================
        // BuildingRepaired -> FacilityRepair
        // ================================================================

        [Fact]
        public void ConvertEvent_BuildingRepaired_ReturnsFacilityRepair()
        {
            var evt = MakeEvent(GameStateEventType.BuildingRepaired, 6000.0,
                key: "Runway");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec7");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.FacilityRepair, action.Type);
            Assert.Equal("Runway", action.FacilityId);
        }

        // ================================================================
        // CrewHired -> KerbalHire
        // ================================================================

        [Fact]
        public void ConvertEvent_CrewHired_ReturnsKerbalHire()
        {
            var evt = MakeEvent(GameStateEventType.CrewHired, 7000.0,
                key: "Jebediah Kerman", detail: "trait=Pilot");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec8");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.KerbalHire, action.Type);
            Assert.Equal("Jebediah Kerman", action.KerbalName);
            Assert.Equal("Pilot", action.KerbalRole);
        }

        // ================================================================
        // ContractAccepted -> ContractAccept
        // ================================================================

        [Fact]
        public void ConvertEvent_ContractAccepted_ReturnsContractAccept()
        {
            var evt = MakeEvent(GameStateEventType.ContractAccepted, 8000.0,
                key: "guid-1234", detail: "Explore the Mun");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec9");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ContractAccept, action.Type);
            Assert.Equal("guid-1234", action.ContractId);
            Assert.Equal("Explore the Mun", action.ContractTitle);
        }

        // ================================================================
        // ContractCompleted -> ContractComplete
        // ================================================================

        [Fact]
        public void ConvertEvent_ContractCompleted_ReturnsContractComplete()
        {
            var evt = MakeEvent(GameStateEventType.ContractCompleted, 9000.0,
                key: "guid-5678", detail: "Orbit Kerbin");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec10");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ContractComplete, action.Type);
            Assert.Equal("guid-5678", action.ContractId);
        }

        // ================================================================
        // ContractFailed -> ContractFail
        // ================================================================

        [Fact]
        public void ConvertEvent_ContractFailed_ReturnsContractFail()
        {
            var evt = MakeEvent(GameStateEventType.ContractFailed, 10000.0,
                key: "guid-9999", detail: "Failed Mission");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec11");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ContractFail, action.Type);
            Assert.Equal("guid-9999", action.ContractId);
        }

        // ================================================================
        // ContractCancelled -> ContractCancel
        // ================================================================

        [Fact]
        public void ConvertEvent_ContractCancelled_ReturnsContractCancel()
        {
            var evt = MakeEvent(GameStateEventType.ContractCancelled, 11000.0,
                key: "guid-0001");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec12");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.ContractCancel, action.Type);
            Assert.Equal("guid-0001", action.ContractId);
        }

        // ================================================================
        // Skipped event types -> null
        // ================================================================

        [Theory]
        [InlineData(GameStateEventType.FundsChanged)]
        [InlineData(GameStateEventType.ScienceChanged)]
        [InlineData(GameStateEventType.ReputationChanged)]
        [InlineData(GameStateEventType.CrewStatusChanged)]
        [InlineData(GameStateEventType.CrewRemoved)]
        [InlineData(GameStateEventType.ContractOffered)]
        [InlineData(GameStateEventType.ContractDeclined)]
        [InlineData(GameStateEventType.FacilityDowngraded)]
        public void ConvertEvent_SkippedTypes_ReturnsNull(GameStateEventType type)
        {
            var evt = MakeEvent(type, 100.0, key: "test");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec");
            Assert.Null(action);
        }

        // ================================================================
        // ConvertEvents — batch with UT range filtering
        // ================================================================

        [Fact]
        public void ConvertEvents_FiltersByUtRange()
        {
            var events = new List<GameStateEvent>
            {
                MakeEvent(GameStateEventType.TechResearched, 50.0, key: "early", detail: "cost=10"),
                MakeEvent(GameStateEventType.TechResearched, 100.0, key: "inRange", detail: "cost=20"),
                MakeEvent(GameStateEventType.TechResearched, 200.0, key: "late", detail: "cost=30"),
            };

            var actions = GameStateEventConverter.ConvertEvents(events, "rec", 100.0, 150.0);

            Assert.Single(actions);
            Assert.Equal("inRange", actions[0].NodeId);
        }

        [Fact]
        public void ConvertEvents_SkipsNonConvertibleTypes()
        {
            var events = new List<GameStateEvent>
            {
                MakeEvent(GameStateEventType.TechResearched, 100.0, key: "tech1", detail: "cost=5"),
                MakeEvent(GameStateEventType.FundsChanged, 100.0, key: "None"),
                MakeEvent(GameStateEventType.ScienceChanged, 100.0, key: "None"),
            };

            var actions = GameStateEventConverter.ConvertEvents(events, "rec", 0.0, 200.0);

            Assert.Single(actions);
            Assert.Equal(GameActionType.ScienceSpending, actions[0].Type);
        }

        [Fact]
        public void ConvertEvents_EmptyList_ReturnsEmpty()
        {
            var actions = GameStateEventConverter.ConvertEvents(
                new List<GameStateEvent>(), "rec", 0.0, 100.0);
            Assert.Empty(actions);
        }

        [Fact]
        public void ConvertEvents_NullList_ReturnsEmpty()
        {
            var actions = GameStateEventConverter.ConvertEvents(null, "rec", 0.0, 100.0);
            Assert.Empty(actions);
        }

        // ================================================================
        // ConvertEvents — logging
        // ================================================================

        [Fact]
        public void ConvertEvents_LogsSummary()
        {
            var events = new List<GameStateEvent>
            {
                MakeEvent(GameStateEventType.TechResearched, 100.0, key: "tech1", detail: "cost=5"),
                MakeEvent(GameStateEventType.FundsChanged, 100.0, key: "None"),
            };

            GameStateEventConverter.ConvertEvents(events, "recX", 0.0, 200.0);

            Assert.Contains(logLines, l =>
                l.Contains("[GameStateEventConverter]") && l.Contains("converted=1") && l.Contains("skipped=1"));
        }

        // ================================================================
        // ConvertScienceSubjects
        // ================================================================

        [Fact]
        public void ConvertScienceSubjects_ValidSubjects_ReturnsScienceEarnings()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLanded", science = 5.0f },
                new PendingScienceSubject { subjectId = "evaReport@MunSrfLandedMidlands", science = 8.0f },
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 1000.0);

            Assert.Equal(2, actions.Count);
            Assert.Equal(GameActionType.ScienceEarning, actions[0].Type);
            Assert.Equal("crewReport@KerbinSrfLanded", actions[0].SubjectId);
            Assert.Equal(5.0f, actions[0].ScienceAwarded);
            Assert.Equal(1000.0, actions[0].UT);
            Assert.Equal("rec", actions[0].RecordingId);
        }

        [Fact]
        public void ConvertScienceSubjects_SkipsEmptySubjectId()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "", science = 5.0f },
                new PendingScienceSubject { subjectId = "valid@subject", science = 3.0f },
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 100.0);

            Assert.Single(actions);
            Assert.Equal("valid@subject", actions[0].SubjectId);
        }

        [Fact]
        public void ConvertScienceSubjects_SkipsNonPositiveScience()
        {
            var subjects = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "zero@subject", science = 0.0f },
                new PendingScienceSubject { subjectId = "negative@subject", science = -1.0f },
            };

            var actions = GameStateEventConverter.ConvertScienceSubjects(subjects, "rec", 100.0);

            Assert.Empty(actions);
        }

        [Fact]
        public void ConvertScienceSubjects_NullList_ReturnsEmpty()
        {
            var actions = GameStateEventConverter.ConvertScienceSubjects(null, "rec", 100.0);
            Assert.Empty(actions);
        }

        // ================================================================
        // ExtractDetail round-trip — verifies the detail parsing works
        // through the converter for cost fields
        // ================================================================

        [Fact]
        public void ConvertEvent_TechResearched_MultipleSemicolonFields()
        {
            var evt = MakeEvent(GameStateEventType.TechResearched, 100.0,
                key: "node1", detail: "cost=90;parts=fuelTankSmallFlat,fuelTank");
            var action = GameStateEventConverter.ConvertEvent(evt, "rec");

            Assert.Equal(90f, action.Cost);
        }
    }
}
