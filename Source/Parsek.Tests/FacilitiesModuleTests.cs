using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class FacilitiesModuleTests : IDisposable
    {
        private readonly FacilitiesModule module;
        private readonly List<string> logLines = new List<string>();

        public FacilitiesModuleTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            module = new FacilitiesModule();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helpers — build facility actions
        // ================================================================

        private static GameAction MakeUpgrade(double ut, string facilityId, int toLevel,
            float cost = 0f, int sequence = 0)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FacilityUpgrade,
                FacilityId = facilityId,
                ToLevel = toLevel,
                FacilityCost = cost,
                Sequence = sequence
            };
        }

        private static GameAction MakeDestruction(double ut, string facilityId,
            string recordingId = null)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FacilityDestruction,
                FacilityId = facilityId,
                RecordingId = recordingId
            };
        }

        private static GameAction MakeRepair(double ut, string facilityId,
            float cost = 0f, int sequence = 0)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FacilityRepair,
                FacilityId = facilityId,
                FacilityCost = cost,
                Sequence = sequence
            };
        }

        // ================================================================
        // Basic upgrade
        // ================================================================

        [Fact]
        public void BasicUpgrade_SetsLevel()
        {
            module.ProcessAction(MakeUpgrade(500, "LaunchPad", 2));

            Assert.Equal(2, module.GetFacilityLevel("LaunchPad"));
            Assert.False(module.IsFacilityDestroyed("LaunchPad"));
        }

        // ================================================================
        // Destruction
        // ================================================================

        [Fact]
        public void Destruction_MarksDestroyed()
        {
            module.ProcessAction(MakeDestruction(1000, "LaunchPad", recordingId: "recA"));

            Assert.True(module.IsFacilityDestroyed("LaunchPad"));
            // Level preserved at default (1) when no prior upgrade
            Assert.Equal(1, module.GetFacilityLevel("LaunchPad"));
        }

        // ================================================================
        // Repair
        // ================================================================

        [Fact]
        public void Repair_ClearsDestroyed()
        {
            // Upgrade first so we can verify level is preserved
            module.ProcessAction(MakeUpgrade(500, "LaunchPad", 2));
            module.ProcessAction(MakeDestruction(1000, "LaunchPad", recordingId: "recA"));

            Assert.True(module.IsFacilityDestroyed("LaunchPad"));

            module.ProcessAction(MakeRepair(1100, "LaunchPad"));

            Assert.False(module.IsFacilityDestroyed("LaunchPad"));
            // Level preserved through destruction and repair
            Assert.Equal(2, module.GetFacilityLevel("LaunchPad"));
        }

        // ================================================================
        // Upgrade sequence
        // ================================================================

        [Fact]
        public void UpgradeSequence_Tracked()
        {
            module.ProcessAction(MakeUpgrade(500, "LaunchPad", 2));
            Assert.Equal(2, module.GetFacilityLevel("LaunchPad"));

            module.ProcessAction(MakeUpgrade(2000, "LaunchPad", 3));
            Assert.Equal(3, module.GetFacilityLevel("LaunchPad"));
        }

        // ================================================================
        // Design doc 10.4 — Full lifecycle
        // ================================================================

        [Fact]
        public void DestructionThenRepairThenUpgrade()
        {
            // UT=500:  Upgrade launchpad to level 2
            module.ProcessAction(MakeUpgrade(500, "LaunchPad", 2));
            Assert.Equal(2, module.GetFacilityLevel("LaunchPad"));
            Assert.False(module.IsFacilityDestroyed("LaunchPad"));

            // UT=1000: Destruction (recording A crash)
            module.ProcessAction(MakeDestruction(1000, "LaunchPad", recordingId: "recA"));
            Assert.Equal(2, module.GetFacilityLevel("LaunchPad"));
            Assert.True(module.IsFacilityDestroyed("LaunchPad"));

            // UT=1100: Repair
            module.ProcessAction(MakeRepair(1100, "LaunchPad"));
            Assert.Equal(2, module.GetFacilityLevel("LaunchPad"));
            Assert.False(module.IsFacilityDestroyed("LaunchPad"));

            // UT=2000: Upgrade to level 3
            module.ProcessAction(MakeUpgrade(2000, "LaunchPad", 3));
            Assert.Equal(3, module.GetFacilityLevel("LaunchPad"));
            Assert.False(module.IsFacilityDestroyed("LaunchPad"));
        }

        // ================================================================
        // Multiple buildings — independent tracking
        // ================================================================

        [Fact]
        public void MultipleBuildings_Independent()
        {
            module.ProcessAction(MakeUpgrade(500, "LaunchPad", 2));
            module.ProcessAction(MakeUpgrade(600, "VehicleAssemblyBuilding", 3));
            module.ProcessAction(MakeDestruction(1000, "LaunchPad", recordingId: "recA"));

            Assert.Equal(2, module.GetFacilityLevel("LaunchPad"));
            Assert.True(module.IsFacilityDestroyed("LaunchPad"));

            Assert.Equal(3, module.GetFacilityLevel("VehicleAssemblyBuilding"));
            Assert.False(module.IsFacilityDestroyed("VehicleAssemblyBuilding"));

            Assert.Equal(2, module.FacilityCount);
        }

        // ================================================================
        // Default values for unknown facilities
        // ================================================================

        [Fact]
        public void GetFacilityLevel_Unknown_ReturnsDefault()
        {
            Assert.Equal(1, module.GetFacilityLevel("UnknownFacility"));
        }

        [Fact]
        public void IsFacilityDestroyed_NotDestroyed_ReturnsFalse()
        {
            Assert.False(module.IsFacilityDestroyed("UnknownFacility"));
        }

        [Fact]
        public void GetFacilityState_Unknown_ReturnsDefault()
        {
            var state = module.GetFacilityState("UnknownFacility");
            Assert.Equal(1, state.Level);
            Assert.False(state.Destroyed);
        }

        [Fact]
        public void GetFacilityLevel_Null_ReturnsDefault()
        {
            Assert.Equal(1, module.GetFacilityLevel(null));
        }

        [Fact]
        public void IsFacilityDestroyed_Null_ReturnsFalse()
        {
            Assert.False(module.IsFacilityDestroyed(null));
        }

        // ================================================================
        // Reset
        // ================================================================

        [Fact]
        public void Reset_ClearsState()
        {
            module.ProcessAction(MakeUpgrade(500, "LaunchPad", 2));
            module.ProcessAction(MakeDestruction(1000, "VehicleAssemblyBuilding", recordingId: "recA"));
            Assert.Equal(2, module.FacilityCount);

            module.Reset();

            Assert.Equal(0, module.FacilityCount);
            Assert.Equal(1, module.GetFacilityLevel("LaunchPad"));
            Assert.False(module.IsFacilityDestroyed("VehicleAssemblyBuilding"));
        }

        // ================================================================
        // Ignores non-facility actions
        // ================================================================

        [Theory]
        [InlineData(GameActionType.ScienceEarning)]
        [InlineData(GameActionType.ScienceSpending)]
        [InlineData(GameActionType.FundsEarning)]
        [InlineData(GameActionType.FundsSpending)]
        [InlineData(GameActionType.MilestoneAchievement)]
        [InlineData(GameActionType.ContractAccept)]
        [InlineData(GameActionType.ContractComplete)]
        [InlineData(GameActionType.ReputationEarning)]
        [InlineData(GameActionType.KerbalAssignment)]
        [InlineData(GameActionType.StrategyActivate)]
        [InlineData(GameActionType.FundsInitial)]
        public void ProcessAction_IgnoresNonFacilityActions(GameActionType type)
        {
            var action = new GameAction { Type = type, UT = 1000.0 };

            module.ProcessAction(action);

            Assert.Equal(0, module.FacilityCount);
        }

        [Fact]
        public void ProcessAction_NullAction_NoError()
        {
            module.ProcessAction(null);
            Assert.Equal(0, module.FacilityCount);
        }

        // ================================================================
        // GetAllFacilities
        // ================================================================

        [Fact]
        public void GetAllFacilities_ReturnsTrackedState()
        {
            module.ProcessAction(MakeUpgrade(500, "LaunchPad", 2));
            module.ProcessAction(MakeUpgrade(600, "VehicleAssemblyBuilding", 3));

            var all = module.GetAllFacilities();

            Assert.Equal(2, all.Count);
            Assert.Equal(2, all["LaunchPad"].Level);
            Assert.Equal(3, all["VehicleAssemblyBuilding"].Level);
        }

        // ================================================================
        // Log assertion tests
        // ================================================================

        [Fact]
        public void Upgrade_LogsFacilityAndLevel()
        {
            module.ProcessAction(MakeUpgrade(500, "LaunchPad", 2));

            Assert.Contains(logLines, l =>
                l.Contains("[Facilities]") &&
                l.Contains("Upgrade") &&
                l.Contains("LaunchPad") &&
                l.Contains("toLevel=2"));
        }

        [Fact]
        public void Destruction_LogsFacilityAndRecording()
        {
            module.ProcessAction(MakeDestruction(1000, "LaunchPad", recordingId: "recA"));

            Assert.Contains(logLines, l =>
                l.Contains("[Facilities]") &&
                l.Contains("Destruction") &&
                l.Contains("LaunchPad") &&
                l.Contains("recA"));
        }

        [Fact]
        public void Repair_LogsFacilityAndWasDestroyed()
        {
            module.ProcessAction(MakeDestruction(1000, "LaunchPad", recordingId: "recA"));
            logLines.Clear();

            module.ProcessAction(MakeRepair(1100, "LaunchPad"));

            Assert.Contains(logLines, l =>
                l.Contains("[Facilities]") &&
                l.Contains("Repair") &&
                l.Contains("LaunchPad") &&
                l.Contains("wasDestroyed=True"));
        }

        [Fact]
        public void Reset_LogsClearedCount()
        {
            module.ProcessAction(MakeUpgrade(500, "LaunchPad", 2));
            module.ProcessAction(MakeUpgrade(600, "VehicleAssemblyBuilding", 3));
            logLines.Clear();

            module.Reset();

            Assert.Contains(logLines, l =>
                l.Contains("[Facilities]") &&
                l.Contains("Reset") &&
                l.Contains("2"));
        }

        [Fact]
        public void UpgradeWhileDestroyed_LogsDestroyedFlag()
        {
            module.ProcessAction(MakeDestruction(500, "LaunchPad", recordingId: "recA"));
            logLines.Clear();

            module.ProcessAction(MakeUpgrade(600, "LaunchPad", 3));

            Assert.Contains(logLines, l =>
                l.Contains("[Facilities]") &&
                l.Contains("Upgrade") &&
                l.Contains("LaunchPad") &&
                l.Contains("destroyed=True"));
        }

        [Fact]
        public void Repair_NotDestroyed_LogsWasDestroyedFalse()
        {
            // Repair on a non-destroyed facility (edge case)
            module.ProcessAction(MakeRepair(500, "LaunchPad"));

            Assert.Contains(logLines, l =>
                l.Contains("[Facilities]") &&
                l.Contains("Repair") &&
                l.Contains("LaunchPad") &&
                l.Contains("wasDestroyed=False"));
        }

        // ================================================================
        // Integration — works with RecalculationEngine
        // ================================================================

        [Fact]
        public void Integration_EngineDispatchesFacilityActions()
        {
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.Facilities);

            var actions = new List<GameAction>
            {
                MakeUpgrade(500, "LaunchPad", 2),
                MakeDestruction(1000, "LaunchPad", recordingId: "recA"),
                MakeRepair(1100, "LaunchPad"),
                MakeUpgrade(2000, "LaunchPad", 3)
            };

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(3, module.GetFacilityLevel("LaunchPad"));
            Assert.False(module.IsFacilityDestroyed("LaunchPad"));
        }
    }
}
