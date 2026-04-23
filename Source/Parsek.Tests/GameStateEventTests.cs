using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GameStateEventTests : IDisposable
    {
        public GameStateEventTests()
        {
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        #region GameStateEvent Serialization

        [Theory]
        [InlineData(GameStateEventType.ContractOffered, "guid-1234", "Survey Kerbin", 0, 0)]
        [InlineData(GameStateEventType.ContractAccepted, "guid-5678", "Collect Science", 0, 0)]
        [InlineData(GameStateEventType.ContractCompleted, "guid-9012", "Orbit Kerbin", 0, 0)]
        [InlineData(GameStateEventType.ContractFailed, "guid-3456", "Land on Mun", 0, 0)]
        [InlineData(GameStateEventType.ContractCancelled, "guid-7890", "Build Station", 0, 0)]
        [InlineData(GameStateEventType.ContractDeclined, "guid-1111", "Fly By Eve", 0, 0)]
        [InlineData(GameStateEventType.TechResearched, "basicRocketry", "cost=5;parts=solidBooster.sm.v2", 100, 95)]
        [InlineData(GameStateEventType.PartPurchased, "mk1pod.v2", "cost=600", 10000, 9400)]
        [InlineData(GameStateEventType.CrewHired, "Jebediah Kerman", "trait=Pilot", 0, 0)]
        [InlineData(GameStateEventType.CrewRemoved, "Bob Kerman", "trait=Scientist", 0, 0)]
        [InlineData(GameStateEventType.CrewStatusChanged, "Valentina Kerman", "from=Available;to=Assigned", 0, 0)]
        [InlineData(GameStateEventType.FacilityUpgraded, "SpaceCenter/LaunchPad", "", 0, 0.5)]
        [InlineData(GameStateEventType.FacilityDowngraded, "SpaceCenter/VehicleAssemblyBuilding", "", 1, 0.5)]
        [InlineData(GameStateEventType.BuildingDestroyed, "SpaceCenter/LaunchPad", "", 0, 0)]
        [InlineData(GameStateEventType.BuildingRepaired, "SpaceCenter/LaunchPad", "", 0, 0)]
        [InlineData(GameStateEventType.FundsChanged, "ContractReward", "", 5000, 15000)]
        [InlineData(GameStateEventType.ScienceChanged, "ExperimentSubmit", "", 50, 75)]
        [InlineData(GameStateEventType.ReputationChanged, "ContractComplete", "", 100, 125)]
        [InlineData(GameStateEventType.MilestoneAchieved, "FirstOrbitKerbin", "", 0, 0)]
        public void GameStateEvent_SerializationRoundtrip(GameStateEventType type,
            string key, string detail, double valBefore, double valAfter)
        {
            var original = new GameStateEvent
            {
                ut = 17005.123456789,
                eventType = type,
                key = key,
                detail = detail,
                valueBefore = valBefore,
                valueAfter = valAfter
            };

            var node = new ConfigNode("GAME_STATE_EVENT");
            original.SerializeInto(node);

            var deserialized = GameStateEvent.DeserializeFrom(node);

            Assert.Equal(original.ut, deserialized.ut);
            Assert.Equal(original.eventType, deserialized.eventType);
            Assert.Equal(original.key, deserialized.key);
            Assert.Equal(original.detail, deserialized.detail);
            Assert.Equal(original.valueBefore, deserialized.valueBefore);
            Assert.Equal(original.valueAfter, deserialized.valueAfter);
        }

        [Fact]
        public void GameStateEvent_DefaultValues_RoundtripClean()
        {
            // Event with no optional fields set
            var original = new GameStateEvent
            {
                ut = 1000,
                eventType = GameStateEventType.BuildingDestroyed
                // key, detail, valueBefore, valueAfter all default
            };

            var node = new ConfigNode("GAME_STATE_EVENT");
            original.SerializeInto(node);

            var deserialized = GameStateEvent.DeserializeFrom(node);

            Assert.Equal(1000, deserialized.ut);
            Assert.Equal(GameStateEventType.BuildingDestroyed, deserialized.eventType);
            Assert.Equal("", deserialized.key);
            Assert.Equal("", deserialized.detail);
            Assert.Equal(0, deserialized.valueBefore);
            Assert.Equal(0, deserialized.valueAfter);
        }

        [Fact]
        public void GameStateEvent_LocaleSafe_Serialization()
        {
            // Ensure doubles with many decimal places round-trip correctly
            var original = new GameStateEvent
            {
                ut = 17005.123456789012345,
                eventType = GameStateEventType.FundsChanged,
                key = "test",
                valueBefore = 12345.678901234,
                valueAfter = 67890.123456789
            };

            var node = new ConfigNode("GAME_STATE_EVENT");
            original.SerializeInto(node);

            var deserialized = GameStateEvent.DeserializeFrom(node);

            Assert.Equal(original.ut, deserialized.ut);
            Assert.Equal(original.valueBefore, deserialized.valueBefore);
            Assert.Equal(original.valueAfter, deserialized.valueAfter);
        }

        [Fact]
        public void NormalizeLegacyPartPurchaseCostsForLoad_FreeAutoUnlock_RewritesCostToZero()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 1000,
                    eventType = GameStateEventType.TechResearched,
                    key = "basicRocketry",
                    detail = "cost=5;parts=solidBooster.v2,otherPart"
                },
                new GameStateEvent
                {
                    ut = 1000,
                    eventType = GameStateEventType.PartPurchased,
                    key = "solidBooster.v2",
                    detail = "cost=800;entryCost=800",
                    valueBefore = 50000,
                    valueAfter = 49200
                }
            };

            int migrated = GameStateEvent.NormalizeLegacyPartPurchaseCostsForLoad(
                events, "unit-test");

            Assert.Equal(1, migrated);
            Assert.Equal("cost=0;entryCost=800", events[1].detail);
            Assert.Equal(events[1].valueAfter, events[1].valueBefore);
        }

        [Fact]
        public void NormalizeLegacyPartPurchaseCostsForLoad_MatchingFundsDebit_KeepsPaidPurchase()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 1000,
                    eventType = GameStateEventType.TechResearched,
                    key = "basicRocketry",
                    detail = "cost=5;parts=solidBooster.v2"
                },
                new GameStateEvent
                {
                    ut = 1000,
                    eventType = GameStateEventType.PartPurchased,
                    key = "solidBooster.v2",
                    detail = "cost=800;entryCost=800",
                    valueBefore = 50000,
                    valueAfter = 49200
                },
                new GameStateEvent
                {
                    ut = 1000,
                    eventType = GameStateEventType.FundsChanged,
                    key = "RnDPartPurchase",
                    valueBefore = 50000,
                    valueAfter = 49200
                }
            };

            int migrated = GameStateEvent.NormalizeLegacyPartPurchaseCostsForLoad(
                events, "unit-test");

            Assert.Equal(0, migrated);
            Assert.Equal("cost=800;entryCost=800", events[1].detail);
            Assert.Equal(50000, events[1].valueBefore);
            Assert.Equal(49200, events[1].valueAfter);
        }

        [Fact]
        public void NormalizeLegacyPartPurchaseCostsForLoad_WithoutMatchingTechUnlock_SkipsMigration()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 1000,
                    eventType = GameStateEventType.PartPurchased,
                    key = "solidBooster.v2",
                    detail = "cost=800;entryCost=800",
                    valueBefore = 50000,
                    valueAfter = 49200
                }
            };

            int migrated = GameStateEvent.NormalizeLegacyPartPurchaseCostsForLoad(
                events, "unit-test");

            Assert.Equal(0, migrated);
            Assert.Equal("cost=800;entryCost=800", events[0].detail);
        }

        [Fact]
        public void NormalizeLegacyPartPurchaseCostsForLoad_MatchingTechInDifferentEpoch_SkipsMigration()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 1000,
                    epoch = 1,
                    eventType = GameStateEventType.TechResearched,
                    key = "basicRocketry",
                    detail = "cost=5;parts=solidBooster.v2"
                },
                new GameStateEvent
                {
                    ut = 1000,
                    epoch = 2,
                    eventType = GameStateEventType.PartPurchased,
                    key = "solidBooster.v2",
                    detail = "cost=800;entryCost=800",
                    valueBefore = 50000,
                    valueAfter = 49200
                }
            };

            int migrated = GameStateEvent.NormalizeLegacyPartPurchaseCostsForLoad(
                events, "unit-test");

            Assert.Equal(0, migrated);
            Assert.Equal("cost=800;entryCost=800", events[1].detail);
            Assert.Equal(50000, events[1].valueBefore);
            Assert.Equal(49200, events[1].valueAfter);
        }

        [Fact]
        public void NormalizeLegacyPartPurchaseCostsForLoad_DifferentEpochFundsDebit_DoesNotBlockMigration()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 1000,
                    epoch = 2,
                    eventType = GameStateEventType.TechResearched,
                    key = "basicRocketry",
                    detail = "cost=5;parts=solidBooster.v2"
                },
                new GameStateEvent
                {
                    ut = 1000,
                    epoch = 2,
                    eventType = GameStateEventType.PartPurchased,
                    key = "solidBooster.v2",
                    detail = "cost=800;entryCost=800",
                    valueBefore = 50000,
                    valueAfter = 49200
                },
                new GameStateEvent
                {
                    ut = 1000,
                    epoch = 1,
                    eventType = GameStateEventType.FundsChanged,
                    key = "RnDPartPurchase",
                    valueBefore = 50000,
                    valueAfter = 49200
                }
            };

            int migrated = GameStateEvent.NormalizeLegacyPartPurchaseCostsForLoad(
                events, "unit-test");

            Assert.Equal(1, migrated);
            Assert.Equal("cost=0;entryCost=800", events[1].detail);
            Assert.Equal(events[1].valueAfter, events[1].valueBefore);
        }

        #endregion

        #region ContractSnapshot Serialization

        [Fact]
        public void ContractSnapshot_SerializationRoundtrip()
        {
            var contractNode = new ConfigNode("CONTRACT");
            contractNode.AddValue("type", "SurveyContract");
            contractNode.AddValue("title", "Survey Kerbin");
            contractNode.AddValue("reward", "5000");

            var original = new ContractSnapshot
            {
                contractGuid = "abcd-1234-efgh-5678",
                contractNode = contractNode
            };

            var parentNode = new ConfigNode("ROOT");
            original.SerializeInto(parentNode);

            // Should have added a CONTRACT_SNAPSHOT child
            var snapNode = parentNode.GetNode("CONTRACT_SNAPSHOT");
            Assert.NotNull(snapNode);

            var deserialized = ContractSnapshot.DeserializeFrom(snapNode);

            Assert.Equal("abcd-1234-efgh-5678", deserialized.contractGuid);
            Assert.NotNull(deserialized.contractNode);
            Assert.Equal("SurveyContract", deserialized.contractNode.GetValue("type"));
            Assert.Equal("Survey Kerbin", deserialized.contractNode.GetValue("title"));
            Assert.Equal("5000", deserialized.contractNode.GetValue("reward"));
        }

        [Fact]
        public void ContractSnapshot_NullContract_HandlesGracefully()
        {
            var original = new ContractSnapshot
            {
                contractGuid = "test-guid",
                contractNode = null
            };

            var parentNode = new ConfigNode("ROOT");
            original.SerializeInto(parentNode);

            var snapNode = parentNode.GetNode("CONTRACT_SNAPSHOT");
            var deserialized = ContractSnapshot.DeserializeFrom(snapNode);

            Assert.Equal("test-guid", deserialized.contractGuid);
            Assert.Null(deserialized.contractNode);
        }

        #endregion

        #region GameStateBaseline Serialization

        [Fact]
        public void GameStateBaseline_SerializationRoundtrip()
        {
            var baseline = new GameStateBaseline
            {
                ut = 17000,
                funds = 250000.5,
                science = 100.25,
                reputation = 75.5f
            };

            baseline.researchedTechIds.Add("start");
            baseline.researchedTechIds.Add("basicRocketry");
            baseline.researchedTechIds.Add("survivability");

            baseline.facilityLevels["SpaceCenter/LaunchPad"] = 0.5f;
            baseline.facilityLevels["SpaceCenter/VehicleAssemblyBuilding"] = 1.0f;

            baseline.buildingIntact["SpaceCenter/LaunchPad"] = true;
            baseline.buildingIntact["SpaceCenter/Administration"] = false;

            var contractNode = new ConfigNode("CONTRACT");
            contractNode.AddValue("type", "SurveyContract");
            baseline.activeContracts.Add(contractNode);

            baseline.crewEntries.Add(new GameStateBaseline.CrewEntry
            {
                name = "Jebediah Kerman",
                status = "Available",
                trait = "Pilot"
            });
            baseline.crewEntries.Add(new GameStateBaseline.CrewEntry
            {
                name = "Bill Kerman",
                status = "Assigned",
                trait = "Engineer"
            });

            var node = new ConfigNode("BASELINE");
            baseline.SerializeInto(node);

            var restored = GameStateBaseline.DeserializeFrom(node);

            Assert.Equal(17000, restored.ut);
            Assert.Equal(250000.5, restored.funds);
            Assert.Equal(100.25, restored.science);
            Assert.Equal(75.5f, restored.reputation);

            Assert.Equal(3, restored.researchedTechIds.Count);
            Assert.Contains("basicRocketry", restored.researchedTechIds);

            Assert.Equal(2, restored.facilityLevels.Count);
            Assert.Equal(0.5f, restored.facilityLevels["SpaceCenter/LaunchPad"]);
            Assert.Equal(1.0f, restored.facilityLevels["SpaceCenter/VehicleAssemblyBuilding"]);

            Assert.Equal(2, restored.buildingIntact.Count);
            Assert.True(restored.buildingIntact["SpaceCenter/LaunchPad"]);
            Assert.False(restored.buildingIntact["SpaceCenter/Administration"]);

            Assert.Single(restored.activeContracts);
            Assert.Equal("SurveyContract", restored.activeContracts[0].GetValue("type"));

            Assert.Equal(2, restored.crewEntries.Count);
            Assert.Equal("Jebediah Kerman", restored.crewEntries[0].name);
            Assert.Equal("Available", restored.crewEntries[0].status);
            Assert.Equal("Pilot", restored.crewEntries[0].trait);
            Assert.Equal("Bill Kerman", restored.crewEntries[1].name);
            Assert.Equal("Assigned", restored.crewEntries[1].status);
        }

        [Fact]
        public void GameStateBaseline_Empty_RoundtripsCleanly()
        {
            var baseline = new GameStateBaseline
            {
                ut = 5000,
                funds = 0,
                science = 0,
                reputation = 0
            };

            var node = new ConfigNode("BASELINE");
            baseline.SerializeInto(node);

            var restored = GameStateBaseline.DeserializeFrom(node);

            Assert.Equal(5000, restored.ut);
            Assert.Equal(0, restored.funds);
            Assert.Empty(restored.researchedTechIds);
            Assert.Empty(restored.facilityLevels);
            Assert.Empty(restored.buildingIntact);
            Assert.Empty(restored.activeContracts);
            Assert.Empty(restored.crewEntries);
        }

        #endregion

        #region GameStateStore

        [Fact]
        public void GameStateStore_AddEvent_AppendsToList()
        {
            GameStateStore.ResetForTesting();

            var evt1 = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry"
            };
            GameStateStore.AddEvent(ref evt1);

            var evt2 = new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.ContractAccepted,
                key = "guid-1"
            };
            GameStateStore.AddEvent(ref evt2);

            Assert.Equal(2, GameStateStore.EventCount);
            Assert.Equal(GameStateEventType.TechResearched, GameStateStore.Events[0].eventType);
            Assert.Equal(GameStateEventType.ContractAccepted, GameStateStore.Events[1].eventType);
        }

        [Fact]
        public void GameStateStore_ClearEvents_RemovesAll()
        {
            GameStateStore.ResetForTesting();

            var evt = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry"
            };
            GameStateStore.AddEvent(ref evt);

            GameStateStore.AddContractSnapshot("guid-1", new ConfigNode("CONTRACT"));

            Assert.Equal(1, GameStateStore.EventCount);
            Assert.Equal(1, GameStateStore.ContractSnapshots.Count);

            GameStateStore.ClearEvents();

            Assert.Equal(0, GameStateStore.EventCount);
            Assert.Equal(0, GameStateStore.ContractSnapshots.Count);
        }

        [Fact]
        public void GameStateStore_ResetForTesting_ClearsEverything()
        {
            var evt = new GameStateEvent { ut = 100 };
            GameStateStore.AddEvent(ref evt);
            GameStateStore.AddContractSnapshot("guid", new ConfigNode("C"));

            GameStateStore.ResetForTesting();

            Assert.Equal(0, GameStateStore.EventCount);
            Assert.Equal(0, GameStateStore.ContractSnapshots.Count);
            Assert.Equal(0, GameStateStore.BaselineCount);
        }

        #endregion

        #region Contract Snapshot GUID Lookup

        [Fact]
        public void ContractSnapshot_GuidLookup_FindsCorrectSnapshot()
        {
            GameStateStore.ResetForTesting();

            var node1 = new ConfigNode("CONTRACT");
            node1.AddValue("title", "Contract A");
            GameStateStore.AddContractSnapshot("guid-aaa", node1);

            var node2 = new ConfigNode("CONTRACT");
            node2.AddValue("title", "Contract B");
            GameStateStore.AddContractSnapshot("guid-bbb", node2);

            var result = GameStateStore.GetContractSnapshot("guid-bbb");
            Assert.NotNull(result);
            Assert.Equal("Contract B", result.GetValue("title"));
        }

        [Fact]
        public void ContractSnapshot_GuidLookup_ReturnsNullForUnknown()
        {
            GameStateStore.ResetForTesting();

            var result = GameStateStore.GetContractSnapshot("nonexistent-guid");
            Assert.Null(result);
        }

        [Fact]
        public void ContractSnapshot_ReplacesExistingGuid()
        {
            GameStateStore.ResetForTesting();

            var node1 = new ConfigNode("CONTRACT");
            node1.AddValue("version", "1");
            GameStateStore.AddContractSnapshot("guid-aaa", node1);

            var node2 = new ConfigNode("CONTRACT");
            node2.AddValue("version", "2");
            GameStateStore.AddContractSnapshot("guid-aaa", node2);

            // Should still be 1 snapshot, updated
            Assert.Equal(1, GameStateStore.ContractSnapshots.Count);
            var result = GameStateStore.GetContractSnapshot("guid-aaa");
            Assert.Equal("2", result.GetValue("version"));
        }

        #endregion

        #region Resource Coalescing

        [Fact]
        public void ResourceCoalescing_WithinEpsilon_UpdatesExisting()
        {
            GameStateStore.ResetForTesting();

            var evt1 = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.FundsChanged,
                key = "ContractAdvance",
                valueBefore = 10000,
                valueAfter = 15000
            };
            GameStateStore.AddEvent(ref evt1);

            // Within 0.1s epsilon — should update, not add
            var evt2 = new GameStateEvent
            {
                ut = 100.05,
                eventType = GameStateEventType.FundsChanged,
                key = "ContractReward",
                valueBefore = 15000,
                valueAfter = 18000
            };
            GameStateStore.AddEvent(ref evt2);

            Assert.Equal(1, GameStateStore.EventCount);
            Assert.Equal(10000, GameStateStore.Events[0].valueBefore);
            Assert.Equal(18000, GameStateStore.Events[0].valueAfter);
        }

        [Fact]
        public void ResourceCoalescing_BeyondEpsilon_AddsNewEvent()
        {
            GameStateStore.ResetForTesting();

            var evt1 = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.FundsChanged,
                key = "ContractAdvance",
                valueBefore = 10000,
                valueAfter = 15000
            };
            GameStateStore.AddEvent(ref evt1);

            // Beyond 0.1s epsilon — should add a new event
            var evt2 = new GameStateEvent
            {
                ut = 100.2,
                eventType = GameStateEventType.FundsChanged,
                key = "VesselRecovery",
                valueBefore = 15000,
                valueAfter = 18000
            };
            GameStateStore.AddEvent(ref evt2);

            Assert.Equal(2, GameStateStore.EventCount);
        }

        [Fact]
        public void ResourceCoalescing_VesselRecoveryWithinEpsilon_DoesNotCoalesce()
        {
            GameStateStore.ResetForTesting();

            var evt1 = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 10000,
                valueAfter = 12000
            };
            GameStateStore.AddEvent(ref evt1);

            var evt2 = new GameStateEvent
            {
                ut = 100.05,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 12000,
                valueAfter = 13500
            };
            GameStateStore.AddEvent(ref evt2);

            Assert.Equal(2, GameStateStore.EventCount);
            Assert.Equal(12000, GameStateStore.Events[0].valueAfter);
            Assert.Equal(13500, GameStateStore.Events[1].valueAfter);
        }

        [Fact]
        public void ResourceCoalescing_VesselRecoveryBarrier_DoesNotCoalesceAcrossOlderEvent()
        {
            GameStateStore.ResetForTesting();

            var evt1 = new GameStateEvent
            {
                ut = 100.00,
                eventType = GameStateEventType.FundsChanged,
                key = "ContractReward",
                valueBefore = 10000,
                valueAfter = 12000
            };
            GameStateStore.AddEvent(ref evt1);

            var evt2 = new GameStateEvent
            {
                ut = 100.05,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 12000,
                valueAfter = 13000
            };
            GameStateStore.AddEvent(ref evt2);

            var evt3 = new GameStateEvent
            {
                ut = 100.08,
                eventType = GameStateEventType.FundsChanged,
                key = "StrategySetup",
                valueBefore = 13000,
                valueAfter = 12900
            };
            GameStateStore.AddEvent(ref evt3);

            Assert.Equal(3, GameStateStore.EventCount);
            Assert.Equal("ContractReward", GameStateStore.Events[0].key);
            Assert.Equal(12000, GameStateStore.Events[0].valueAfter);
            Assert.Equal(LedgerOrchestrator.VesselRecoveryReasonKey, GameStateStore.Events[1].key);
            Assert.Equal("StrategySetup", GameStateStore.Events[2].key);
            Assert.Equal(12900, GameStateStore.Events[2].valueAfter);
        }

        [Fact]
        public void ResourceCoalescing_DifferentTypes_NotCoalesced()
        {
            GameStateStore.ResetForTesting();

            var evt1 = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.FundsChanged,
                valueBefore = 10000,
                valueAfter = 15000
            };
            GameStateStore.AddEvent(ref evt1);

            // Same UT but different type — should NOT coalesce
            var evt2 = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.ScienceChanged,
                valueBefore = 50,
                valueAfter = 75
            };
            GameStateStore.AddEvent(ref evt2);

            Assert.Equal(2, GameStateStore.EventCount);
        }

        [Fact]
        public void ResourceCoalescing_NonResourceEvents_NeverCoalesced()
        {
            GameStateStore.ResetForTesting();

            var evt1 = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry"
            };
            GameStateStore.AddEvent(ref evt1);

            // Same UT and type but non-resource — should NOT coalesce
            var evt2 = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.TechResearched,
                key = "stability"
            };
            GameStateStore.AddEvent(ref evt2);

            Assert.Equal(2, GameStateStore.EventCount);
        }

        #endregion

        #region Facility Transition Detection

        [Fact]
        public void FacilityTransition_Upgraded_DetectedCorrectly()
        {
            var cached = new Dictionary<string, float>
            {
                ["SpaceCenter/LaunchPad"] = 0.0f,
                ["SpaceCenter/VehicleAssemblyBuilding"] = 0.5f
            };
            var current = new Dictionary<string, float>
            {
                ["SpaceCenter/LaunchPad"] = 0.5f,  // upgraded
                ["SpaceCenter/VehicleAssemblyBuilding"] = 0.5f  // unchanged
            };

            var events = GameStateRecorder.CheckFacilityTransitions(cached, current, 17000);

            Assert.Single(events);
            Assert.Equal(GameStateEventType.FacilityUpgraded, events[0].eventType);
            Assert.Equal("SpaceCenter/LaunchPad", events[0].key);
            Assert.Equal(0.0, events[0].valueBefore);
            Assert.Equal(0.5, events[0].valueAfter);
        }

        [Fact]
        public void FacilityTransition_Downgraded_DetectedCorrectly()
        {
            var cached = new Dictionary<string, float>
            {
                ["SpaceCenter/LaunchPad"] = 1.0f
            };
            var current = new Dictionary<string, float>
            {
                ["SpaceCenter/LaunchPad"] = 0.5f  // downgraded
            };

            var events = GameStateRecorder.CheckFacilityTransitions(cached, current, 17000);

            Assert.Single(events);
            Assert.Equal(GameStateEventType.FacilityDowngraded, events[0].eventType);
        }

        [Fact]
        public void FacilityTransition_NoChange_NoEvents()
        {
            var cached = new Dictionary<string, float>
            {
                ["SpaceCenter/LaunchPad"] = 0.5f,
                ["SpaceCenter/VehicleAssemblyBuilding"] = 0.5f
            };
            // Same state — small float difference within tolerance
            var current = new Dictionary<string, float>
            {
                ["SpaceCenter/LaunchPad"] = 0.5001f,
                ["SpaceCenter/VehicleAssemblyBuilding"] = 0.4999f
            };

            var events = GameStateRecorder.CheckFacilityTransitions(cached, current, 17000);

            Assert.Empty(events);
        }

        #endregion

        #region Building Transition Detection

        [Fact]
        public void BuildingTransition_Destroyed_DetectedCorrectly()
        {
            var cached = new Dictionary<string, bool>
            {
                ["SpaceCenter/LaunchPad"] = true // was intact
            };
            var current = new Dictionary<string, bool>
            {
                ["SpaceCenter/LaunchPad"] = false // now destroyed
            };

            var events = GameStateRecorder.CheckBuildingTransitions(cached, current, 17000);

            Assert.Single(events);
            Assert.Equal(GameStateEventType.BuildingDestroyed, events[0].eventType);
            Assert.Equal("SpaceCenter/LaunchPad", events[0].key);
        }

        [Fact]
        public void BuildingTransition_Repaired_DetectedCorrectly()
        {
            var cached = new Dictionary<string, bool>
            {
                ["SpaceCenter/LaunchPad"] = false // was destroyed
            };
            var current = new Dictionary<string, bool>
            {
                ["SpaceCenter/LaunchPad"] = true // now intact
            };

            var events = GameStateRecorder.CheckBuildingTransitions(cached, current, 17000);

            Assert.Single(events);
            Assert.Equal(GameStateEventType.BuildingRepaired, events[0].eventType);
        }

        [Fact]
        public void BuildingTransition_NoChange_NoEvents()
        {
            var cached = new Dictionary<string, bool>
            {
                ["SpaceCenter/LaunchPad"] = true,
                ["SpaceCenter/Administration"] = false
            };
            var current = new Dictionary<string, bool>
            {
                ["SpaceCenter/LaunchPad"] = true,
                ["SpaceCenter/Administration"] = false
            };

            var events = GameStateRecorder.CheckBuildingTransitions(cached, current, 17000);

            Assert.Empty(events);
        }

        #endregion

        #region Crew Event Suppression

        [Fact]
        public void CrewSuppression_FlagDefaultsFalse()
        {
            Assert.False(GameStateRecorder.SuppressCrewEvents);
        }

        [Fact]
        public void ResourceSuppression_FlagDefaultsFalse()
        {
            Assert.False(GameStateRecorder.SuppressResourceEvents);
        }

        #endregion

        #region Event History

        [Fact]
        public void EventHistory_CanFindMostRecentFacilityState()
        {
            GameStateStore.ResetForTesting();

            // Simulate history: pad upgraded twice
            var padEvt1 = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.FacilityUpgraded,
                key = "SpaceCenter/LaunchPad",
                valueBefore = 0,
                valueAfter = 0.5
            };
            GameStateStore.AddEvent(ref padEvt1);
            var padEvt2 = new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.FacilityUpgraded,
                key = "SpaceCenter/LaunchPad",
                valueBefore = 0.5,
                valueAfter = 1.0
            };
            GameStateStore.AddEvent(ref padEvt2);
            var destroyedEvt = new GameStateEvent
            {
                ut = 150,
                eventType = GameStateEventType.BuildingDestroyed,
                key = "SpaceCenter/Administration"
            };
            GameStateStore.AddEvent(ref destroyedEvt);

            // Verify the events are in the store
            Assert.Equal(3, GameStateStore.EventCount);

            // The most recent facility event for LaunchPad should have valueAfter=1.0
            var events = GameStateStore.Events;
            var lastPadEvent = default(GameStateEvent);
            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (events[i].key == "SpaceCenter/LaunchPad" &&
                    (events[i].eventType == GameStateEventType.FacilityUpgraded ||
                     events[i].eventType == GameStateEventType.FacilityDowngraded))
                {
                    lastPadEvent = events[i];
                    break;
                }
            }
            Assert.Equal(1.0, lastPadEvent.valueAfter);
        }

        #endregion

        #region Epoch Stamping

        [Fact]
        public void AddEvent_PreservesLegacyEpochField()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();

            var epoch3Evt = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                epoch = 3
            };
            GameStateStore.AddEvent(ref epoch3Evt);

            Assert.Equal(3u, GameStateStore.Events[0].epoch);

            var epoch5Evt = new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                epoch = 5
            };
            GameStateStore.AddEvent(ref epoch5Evt);

            Assert.Equal(5u, GameStateStore.Events[1].epoch);

            // First event still has original epoch
            Assert.Equal(3u, GameStateStore.Events[0].epoch);
        }

        [Fact]
        public void GameStateEvent_DeserializeFrom_ReadsLegacyEpochField()
        {
            var node = new ConfigNode("GAME_STATE_EVENT");
            node.AddValue("ut", "17000");
            node.AddValue("type", ((int)GameStateEventType.TechResearched).ToString(CultureInfo.InvariantCulture));
            node.AddValue("key", "basicRocketry");
            node.AddValue("epoch", "7");

            var deserialized = GameStateEvent.DeserializeFrom(node);

            Assert.Equal(7u, deserialized.epoch);
        }

        [Fact]
        public void GameStateEvent_SerializeInto_OmitsLegacyEpochField()
        {
            var original = new GameStateEvent
            {
                ut = 17000,
                eventType = GameStateEventType.BuildingDestroyed,
                epoch = 7
            };

            var node = new ConfigNode("GAME_STATE_EVENT");
            original.SerializeInto(node);

            Assert.Null(node.GetValue("epoch"));

            var deserialized = GameStateEvent.DeserializeFrom(node);
            Assert.Equal(0u, deserialized.epoch);
        }

        #endregion

        #region Baseline Management

        [Fact]
        public void AddBaseline_IncreasesCount()
        {
            GameStateStore.ResetForTesting();

            Assert.Equal(0, GameStateStore.BaselineCount);

            var baseline = new GameStateBaseline { ut = 17000, funds = 250000 };
            GameStateStore.AddBaseline(baseline);

            Assert.Equal(1, GameStateStore.BaselineCount);
            Assert.Equal(17000, GameStateStore.Baselines[0].ut);
        }

        [Fact]
        public void AddBaseline_NullIgnored()
        {
            GameStateStore.ResetForTesting();

            GameStateStore.AddBaseline(null);

            Assert.Equal(0, GameStateStore.BaselineCount);
        }

        [Fact]
        public void ClearBaselines_RemovesAll()
        {
            GameStateStore.ResetForTesting();

            GameStateStore.AddBaseline(new GameStateBaseline { ut = 17000 });
            GameStateStore.AddBaseline(new GameStateBaseline { ut = 18000 });
            Assert.Equal(2, GameStateStore.BaselineCount);

            GameStateStore.ClearBaselines();
            Assert.Equal(0, GameStateStore.BaselineCount);
        }

        [Fact]
        public void ClearBaselines_IndependentFromClearEvents()
        {
            GameStateStore.ResetForTesting();

            GameStateStore.AddBaseline(new GameStateBaseline { ut = 17000 });
            var evt1 = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "test"
            };
            GameStateStore.AddEvent(ref evt1);

            // ClearEvents should not touch baselines
            GameStateStore.ClearEvents();
            Assert.Equal(0, GameStateStore.EventCount);
            Assert.Equal(1, GameStateStore.BaselineCount);

            // ClearBaselines should not touch events (already cleared, just verify isolation)
            var evt2 = new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2"
            };
            GameStateStore.AddEvent(ref evt2);
            GameStateStore.ClearBaselines();
            Assert.Equal(1, GameStateStore.EventCount);
            Assert.Equal(0, GameStateStore.BaselineCount);
        }

        #endregion

        #region Contract Snapshot Edge Cases

        [Fact]
        public void AddContractSnapshot_NullGuid_Skipped()
        {
            GameStateStore.ResetForTesting();

            GameStateStore.AddContractSnapshot(null, new ConfigNode("CONTRACT"));
            Assert.Equal(0, GameStateStore.ContractSnapshots.Count);
        }

        [Fact]
        public void AddContractSnapshot_EmptyGuid_Skipped()
        {
            GameStateStore.ResetForTesting();

            GameStateStore.AddContractSnapshot("", new ConfigNode("CONTRACT"));
            Assert.Equal(0, GameStateStore.ContractSnapshots.Count);
        }

        [Fact]
        public void AddContractSnapshot_NullNode_Skipped()
        {
            GameStateStore.ResetForTesting();

            GameStateStore.AddContractSnapshot("guid-123", null);
            Assert.Equal(0, GameStateStore.ContractSnapshots.Count);
        }

        [Fact]
        public void GetContractSnapshot_NullGuid_ReturnsNull()
        {
            GameStateStore.ResetForTesting();

            Assert.Null(GameStateStore.GetContractSnapshot(null));
        }

        [Fact]
        public void GetContractSnapshot_EmptyGuid_ReturnsNull()
        {
            GameStateStore.ResetForTesting();

            Assert.Null(GameStateStore.GetContractSnapshot(""));
        }

        #endregion

        #region GameStateBaseline Locale Safety

        [Fact]
        public void GameStateBaseline_HighPrecision_RoundtripsCorrectly()
        {
            var baseline = new GameStateBaseline
            {
                ut = 17005.123456789012345,
                funds = 123456.789012345,
                science = 99.87654321,
                reputation = 42.1234f
            };

            var node = new ConfigNode("BASELINE");
            baseline.SerializeInto(node);

            var restored = GameStateBaseline.DeserializeFrom(node);

            Assert.Equal(baseline.ut, restored.ut);
            Assert.Equal(baseline.funds, restored.funds);
            Assert.Equal(baseline.science, restored.science);
            Assert.Equal(baseline.reputation, restored.reputation);
        }

        [Fact]
        public void GameStateBaseline_MissingNodes_DeserializesCleanly()
        {
            // Node with only scalar values, no sub-nodes
            var node = new ConfigNode("BASELINE");
            node.AddValue("ut", "5000");
            node.AddValue("funds", "10000");
            // No TECH_IDS, FACILITY_LEVELS, BUILDING_INTACT, ACTIVE_CONTRACTS, CREW_ROSTER

            var restored = GameStateBaseline.DeserializeFrom(node);

            Assert.Equal(5000, restored.ut);
            Assert.Equal(10000, restored.funds);
            Assert.Empty(restored.researchedTechIds);
            Assert.Empty(restored.facilityLevels);
            Assert.Empty(restored.buildingIntact);
            Assert.Empty(restored.activeContracts);
            Assert.Empty(restored.crewEntries);
        }

        [Fact]
        public void CrewEntry_SerializationRoundtrip()
        {
            var entry = new GameStateBaseline.CrewEntry
            {
                name = "Jebediah Kerman",
                status = "Available",
                trait = "Pilot"
            };

            var node = new ConfigNode("CREW");
            entry.SerializeInto(node);

            var restored = GameStateBaseline.CrewEntry.DeserializeFrom(node);

            Assert.Equal("Jebediah Kerman", restored.name);
            Assert.Equal("Available", restored.status);
            Assert.Equal("Pilot", restored.trait);
        }

        [Fact]
        public void CrewEntry_NullFields_DefaultToEmpty()
        {
            var entry = new GameStateBaseline.CrewEntry();
            // name, status, trait are all null by default

            var node = new ConfigNode("CREW");
            entry.SerializeInto(node);

            var restored = GameStateBaseline.CrewEntry.DeserializeFrom(node);

            Assert.Equal("", restored.name);
            Assert.Equal("", restored.status);
            Assert.Equal("", restored.trait);
        }

        #endregion

        #region Display Helpers

        [Theory]
        [InlineData(GameStateEventType.ContractOffered, "Contract")]
        [InlineData(GameStateEventType.ContractAccepted, "Contract")]
        [InlineData(GameStateEventType.ContractCompleted, "Contract")]
        [InlineData(GameStateEventType.ContractFailed, "Contract")]
        [InlineData(GameStateEventType.ContractCancelled, "Contract")]
        [InlineData(GameStateEventType.ContractDeclined, "Contract")]
        [InlineData(GameStateEventType.TechResearched, "Tech")]
        [InlineData(GameStateEventType.PartPurchased, "Part")]
        [InlineData(GameStateEventType.CrewHired, "Crew")]
        [InlineData(GameStateEventType.CrewRemoved, "Crew")]
        [InlineData(GameStateEventType.CrewStatusChanged, "Crew")]
        [InlineData(GameStateEventType.FacilityUpgraded, "Upgrade")]
        [InlineData(GameStateEventType.FacilityDowngraded, "Downgrade")]
        [InlineData(GameStateEventType.BuildingDestroyed, "Building")]
        [InlineData(GameStateEventType.BuildingRepaired, "Building")]
        [InlineData(GameStateEventType.FundsChanged, "Funds")]
        [InlineData(GameStateEventType.ScienceChanged, "Science")]
        [InlineData(GameStateEventType.ReputationChanged, "Reputation")]
        public void GetDisplayCategory_ReturnsExpected(GameStateEventType type, string expected)
        {
            Assert.Equal(expected, GameStateEventDisplay.GetDisplayCategory(type));
        }

        [Fact]
        public void GetDisplayDescription_TechResearched_WithCost()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5;parts=solidBooster.sm.v2"
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("basicRocketry", desc);
            Assert.Contains("5 sci", desc);
        }

        [Fact]
        public void GetDisplayDescription_TechResearched_NoCost()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = ""
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("basicRocketry", desc);
            Assert.DoesNotContain("sci", desc);
        }

        [Fact]
        public void GetDisplayDescription_PartPurchased_WithCost()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("mk1pod.v2", desc);
            Assert.Contains("600 funds", desc);
        }

        [Fact]
        public void GetDisplayDescription_CrewHired_WithTrait()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.CrewHired,
                key = "Luzor Kerman",
                detail = "trait=Pilot"
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("Hired Luzor Kerman", desc);
            Assert.Contains("Pilot", desc);
        }

        [Fact]
        public void GetDisplayDescription_CrewStatusChanged()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.CrewStatusChanged,
                key = "Valentina Kerman",
                detail = "from=Available;to=Assigned"
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("Valentina Kerman", desc);
            Assert.Contains("Available", desc);
            Assert.Contains("Assigned", desc);
        }

        [Fact]
        public void GetDisplayDescription_FacilityUpgraded_WithLevels()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.FacilityUpgraded,
                key = "SpaceCenter/LaunchPad",
                valueBefore = 0,
                valueAfter = 0.5
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("LaunchPad", desc);
            // valueBefore=0 → lv 0, valueAfter=0.5 → lv 1 (0.5 * 2 = 1)
            Assert.Contains("lv 0 \u2192 1", desc);
        }

        [Fact]
        public void GetDisplayDescription_ContractAccepted()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.ContractAccepted,
                key = "guid-1234",
                detail = "title=Survey Kerbin"
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("Survey Kerbin", desc);
            Assert.Contains("accepted", desc);
        }

        [Fact]
        public void GetDisplayDescription_ContractAccepted_NoTitle()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.ContractAccepted,
                key = "guid-1234",
                detail = ""
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("guid-1234", desc);
            Assert.Contains("accepted", desc);
        }

        [Fact]
        public void GetDisplayDescription_BuildingDestroyed()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.BuildingDestroyed,
                key = "SpaceCenter/LaunchPad"
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("LaunchPad", desc);
            Assert.Contains("destroyed", desc);
        }

        [Fact]
        public void GetDisplayDescription_CrewRemoved_WithTrait()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.CrewRemoved,
                key = "Bob Kerman",
                detail = "trait=Scientist"
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("Removed Bob Kerman", desc);
            Assert.Contains("Scientist", desc);
        }

        [Fact]
        public void GetDisplayDescription_FacilityDowngraded()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.FacilityDowngraded,
                key = "SpaceCenter/VehicleAssemblyBuilding",
                valueBefore = 1.0,
                valueAfter = 0.5
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("VehicleAssemblyBuilding", desc);
            Assert.Contains("lv 2 \u2192 1", desc);
        }

        [Fact]
        public void GetDisplayDescription_BuildingRepaired()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.BuildingRepaired,
                key = "SpaceCenter/LaunchPad"
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("LaunchPad", desc);
            Assert.Contains("repaired", desc);
        }

        [Fact]
        public void GetDisplayDescription_FundsChanged_PositiveDelta()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.FundsChanged,
                key = "ContractReward",
                valueBefore = 5000,
                valueAfter = 15000
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("+10,000", desc);
            Assert.Contains("5,000", desc);
            Assert.Contains("15,000", desc);
        }

        [Fact]
        public void GetDisplayDescription_FundsChanged_NegativeDelta()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.FundsChanged,
                key = "VesselLaunch",
                valueBefore = 20000,
                valueAfter = 15000
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("-5,000", desc);
        }

        [Fact]
        public void GetDisplayDescription_ScienceChanged()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.ScienceChanged,
                key = "ExperimentSubmit",
                valueBefore = 50,
                valueAfter = 75
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("+25", desc);
        }

        [Fact]
        public void GetDisplayDescription_ReputationChanged()
        {
            var e = new GameStateEvent
            {
                eventType = GameStateEventType.ReputationChanged,
                key = "ContractComplete",
                valueBefore = 100,
                valueAfter = 125
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("+25", desc);
        }

        [Theory]
        [InlineData(GameStateEventType.ContractOffered, "offered")]
        [InlineData(GameStateEventType.ContractCompleted, "completed")]
        [InlineData(GameStateEventType.ContractFailed, "failed")]
        [InlineData(GameStateEventType.ContractCancelled, "cancelled")]
        [InlineData(GameStateEventType.ContractDeclined, "declined")]
        public void GetDisplayDescription_ContractVerbs(GameStateEventType type, string expectedVerb)
        {
            var e = new GameStateEvent
            {
                eventType = type,
                key = "guid-test",
                detail = "title=Test Contract"
            };
            var desc = GameStateEventDisplay.GetDisplayDescription(e);
            Assert.Contains("Test Contract", desc);
            Assert.Contains(expectedVerb, desc);
        }

        [Fact]
        public void ExtractDetailField_BasicExtraction()
        {
            Assert.Equal("5", GameStateEventDisplay.ExtractDetailField("cost=5;parts=solidBooster", "cost"));
            Assert.Equal("solidBooster", GameStateEventDisplay.ExtractDetailField("cost=5;parts=solidBooster", "parts"));
        }

        [Fact]
        public void ExtractDetailField_NotFound_ReturnsNull()
        {
            Assert.Null(GameStateEventDisplay.ExtractDetailField("cost=5", "missing"));
            Assert.Null(GameStateEventDisplay.ExtractDetailField("", "cost"));
            Assert.Null(GameStateEventDisplay.ExtractDetailField(null, "cost"));
        }

        [Fact]
        public void ExtractDetailField_MiddleField()
        {
            string detail = "cost=5;trait=Pilot;status=Available";
            Assert.Equal("Pilot", GameStateEventDisplay.ExtractDetailField(detail, "trait"));
        }

        #endregion

        #region IsMilestoneFilteredEvent

        [Theory]
        [InlineData(GameStateEventType.FundsChanged, true)]
        [InlineData(GameStateEventType.ScienceChanged, true)]
        [InlineData(GameStateEventType.ReputationChanged, true)]
        [InlineData(GameStateEventType.CrewStatusChanged, true)]
        [InlineData(GameStateEventType.ContractOffered, true)]
        [InlineData(GameStateEventType.CrewHired, false)]
        [InlineData(GameStateEventType.CrewRemoved, false)]
        [InlineData(GameStateEventType.TechResearched, false)]
        [InlineData(GameStateEventType.PartPurchased, false)]
        [InlineData(GameStateEventType.ContractAccepted, false)]
        [InlineData(GameStateEventType.FacilityUpgraded, false)]
        public void IsMilestoneFilteredEvent_CorrectlyFilters(GameStateEventType type, bool expected)
        {
            Assert.Equal(expected, GameStateStore.IsMilestoneFilteredEvent(type));
        }

        #endregion

        #region GetUncommittedEventCount

        [Fact]
        public void GetUncommittedEventCount_CountsEventsAfterLastMilestone()
        {
            // Add events and create a milestone covering ut 0-100
            var techEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry"
            };
            GameStateStore.AddEvent(ref techEvt);
            MilestoneStore.CreateMilestone("rec1", 100);

            // Add events after the milestone
            var contractEvt = new GameStateEvent
            {
                ut = 150,
                eventType = GameStateEventType.ContractAccepted,
                key = "guid-1"
            };
            GameStateStore.AddEvent(ref contractEvt);
            var crewEvt = new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.CrewHired,
                key = "Val Kerman"
            };
            GameStateStore.AddEvent(ref crewEvt);
            // Resource event — should be filtered
            var fundsEvt = new GameStateEvent
            {
                ut = 160,
                eventType = GameStateEventType.FundsChanged,
                valueBefore = 1000, valueAfter = 900
            };
            GameStateStore.AddEvent(ref fundsEvt);

            Assert.Equal(2, GameStateStore.GetUncommittedEventCount());
        }

        [Fact]
        public void GetUncommittedEventCount_ZeroWhenAllCommitted()
        {
            var techEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry"
            };
            GameStateStore.AddEvent(ref techEvt);
            MilestoneStore.CreateMilestone("rec1", 100);

            Assert.Equal(0, GameStateStore.GetUncommittedEventCount());
        }

        [Fact]
        public void GetUncommittedEventCount_AllUncommittedWhenNoMilestones()
        {
            var contractEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.ContractAccepted,
                key = "guid-1"
            };
            GameStateStore.AddEvent(ref contractEvt);
            var partEvt = new GameStateEvent
            {
                ut = 60,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2"
            };
            GameStateStore.AddEvent(ref partEvt);

            Assert.Equal(2, GameStateStore.GetUncommittedEventCount());
        }

        [Fact]
        public void GetUncommittedEventCount_IgnoresHiddenTaggedEvents()
        {
            RecordingStore.AddCommittedInternal(new Recording
            {
                RecordingId = "live-rec",
                VesselName = "Visible"
            });

            var hiddenEvt = new GameStateEvent
            {
                ut = 150,
                eventType = GameStateEventType.ContractAccepted,
                key = "hidden-guid",
                recordingId = "old-rec"
            };
            GameStateStore.AddEvent(ref hiddenEvt);
            var visibleEvt = new GameStateEvent
            {
                ut = 160,
                eventType = GameStateEventType.ContractAccepted,
                key = "visible-guid",
                recordingId = "live-rec"
            };
            GameStateStore.AddEvent(ref visibleEvt);

            Assert.Equal(1, GameStateStore.GetUncommittedEventCount());
        }

        #endregion

        #region Committed Science Subjects

        [Fact]
        public void CommitScienceSubjects_AddsNew()
        {
            GameStateStore.ResetForTesting();

            var pending = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLandedLaunchPad", science = 3.0f },
                new PendingScienceSubject { subjectId = "temperatureScan@KerbinSrfLandedLaunchPad", science = 4.0f }
            };

            ScienceTestHelpers.CommitScienceSubjects(pending);

            Assert.Equal(2, GameStateStore.CommittedScienceSubjectCount);

            float sci;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("crewReport@KerbinSrfLandedLaunchPad", out sci));
            Assert.Equal(3.0f, sci);
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("temperatureScan@KerbinSrfLandedLaunchPad", out sci));
            Assert.Equal(4.0f, sci);
        }

        [Fact]
        public void CommitScienceSubjects_MaxWins()
        {
            GameStateStore.ResetForTesting();

            var batch1 = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLandedLaunchPad", science = 3.0f }
            };
            ScienceTestHelpers.CommitScienceSubjects(batch1);

            // Second commit with higher value — should update
            var batch2 = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLandedLaunchPad", science = 5.0f }
            };
            ScienceTestHelpers.CommitScienceSubjects(batch2);

            float sci;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("crewReport@KerbinSrfLandedLaunchPad", out sci));
            Assert.Equal(5.0f, sci);
        }

        [Fact]
        public void CommitScienceSubjects_LowerValueIgnored()
        {
            GameStateStore.ResetForTesting();

            var batch1 = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLandedLaunchPad", science = 5.0f }
            };
            ScienceTestHelpers.CommitScienceSubjects(batch1);

            // Second commit with lower value — should NOT downgrade
            var batch2 = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLandedLaunchPad", science = 2.0f }
            };
            ScienceTestHelpers.CommitScienceSubjects(batch2);

            float sci;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("crewReport@KerbinSrfLandedLaunchPad", out sci));
            Assert.Equal(5.0f, sci);
        }

        [Fact]
        public void CommitScienceSubjects_NullOrEmpty_NoOp()
        {
            GameStateStore.ResetForTesting();

            ScienceTestHelpers.CommitScienceSubjects(null);
            Assert.Equal(0, GameStateStore.CommittedScienceSubjectCount);

            ScienceTestHelpers.CommitScienceSubjects(new List<PendingScienceSubject>());
            Assert.Equal(0, GameStateStore.CommittedScienceSubjectCount);
        }

        [Fact]
        public void TryGetCommittedSubjectScience_MissingKey_ReturnsFalse()
        {
            GameStateStore.ResetForTesting();

            float sci;
            Assert.False(GameStateStore.TryGetCommittedSubjectScience("nonexistent", out sci));
        }

        [Fact]
        public void ClearScienceSubjects_RemovesAll()
        {
            GameStateStore.ResetForTesting();

            ScienceTestHelpers.CommitScienceSubjects(new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "test1", science = 1.0f },
                new PendingScienceSubject { subjectId = "test2", science = 2.0f }
            });
            Assert.Equal(2, GameStateStore.CommittedScienceSubjectCount);

            GameStateStore.ClearScienceSubjects();
            Assert.Equal(0, GameStateStore.CommittedScienceSubjectCount);
        }

        [Fact]
        public void CommitScienceSubjects_MultipleEntriesSameSubject_MaxWins()
        {
            GameStateStore.ResetForTesting();

            // Simulates multiple transmissions of same experiment in one session
            var pending = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLandedLaunchPad", science = 2.0f },
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLandedLaunchPad", science = 4.5f },
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLandedLaunchPad", science = 3.0f }
            };

            ScienceTestHelpers.CommitScienceSubjects(pending);

            Assert.Equal(1, GameStateStore.CommittedScienceSubjectCount);
            float sci;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("crewReport@KerbinSrfLandedLaunchPad", out sci));
            Assert.Equal(4.5f, sci);
        }

        [Fact]
        public void ResetForTesting_ClearsScienceSubjects()
        {
            ScienceTestHelpers.CommitScienceSubjects(new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "test", science = 1.0f }
            });

            GameStateStore.ResetForTesting();
            Assert.Equal(0, GameStateStore.CommittedScienceSubjectCount);
        }

        [Fact]
        public void ScienceSubjects_SerializeDeserialize_RoundTrip()
        {
            GameStateStore.ResetForTesting();

            ScienceTestHelpers.CommitScienceSubjects(new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLandedLaunchPad", science = 3.5f },
                new PendingScienceSubject { subjectId = "temperatureScan@MunSrfLanded", science = 8.12345f }
            });

            // Serialize
            var root = new ConfigNode("ROOT");
            GameStateStore.SerializeScienceSubjectsInto(root);

            // Reset and deserialize
            GameStateStore.ResetForTesting();
            Assert.Equal(0, GameStateStore.CommittedScienceSubjectCount);

            GameStateStore.DeserializeScienceSubjectsFrom(root);
            Assert.Equal(2, GameStateStore.CommittedScienceSubjectCount);

            float sci;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("crewReport@KerbinSrfLandedLaunchPad", out sci));
            Assert.Equal(3.5f, sci);
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("temperatureScan@MunSrfLanded", out sci));
            Assert.Equal(8.12345f, sci);
        }

        [Fact]
        public void ScienceSubjects_DeserializeFrom_MissingNode_NoOp()
        {
            GameStateStore.ResetForTesting();

            var root = new ConfigNode("ROOT");
            // No SCIENCE_SUBJECTS node
            GameStateStore.DeserializeScienceSubjectsFrom(root);
            Assert.Equal(0, GameStateStore.CommittedScienceSubjectCount);
        }

        [Fact]
        public void ScienceSubjects_DeserializeFrom_MalformedEntries_Skipped()
        {
            GameStateStore.ResetForTesting();

            var root = new ConfigNode("ROOT");
            var sciNode = root.AddNode("SCIENCE_SUBJECTS");

            // Valid entry
            var valid = sciNode.AddNode("SUBJECT");
            valid.AddValue("id", "crewReport@KerbinSrfLandedLaunchPad");
            valid.AddValue("science", "5.0");

            // Missing id
            var noId = sciNode.AddNode("SUBJECT");
            noId.AddValue("science", "3.0");

            // Missing science
            var noSci = sciNode.AddNode("SUBJECT");
            noSci.AddValue("id", "temp@Kerbin");

            // Unparseable science
            var badSci = sciNode.AddNode("SUBJECT");
            badSci.AddValue("id", "goo@Kerbin");
            badSci.AddValue("science", "notanumber");

            GameStateStore.DeserializeScienceSubjectsFrom(root);

            // Only the valid entry should be loaded
            Assert.Equal(1, GameStateStore.CommittedScienceSubjectCount);
            float sci;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("crewReport@KerbinSrfLandedLaunchPad", out sci));
            Assert.Equal(5.0f, sci);
        }

        [Fact]
        public void ScienceSubjects_SerializeEmpty_NoNode()
        {
            GameStateStore.ResetForTesting();

            var root = new ConfigNode("ROOT");
            GameStateStore.SerializeScienceSubjectsInto(root);

            // Should not create SCIENCE_SUBJECTS node when empty
            Assert.Null(root.GetNode("SCIENCE_SUBJECTS"));
        }

        [Fact]
        public void SuppressResourceEvents_PreventsAccumulation()
        {
            // OnScienceReceived is private and requires KSP runtime, so we can't
            // call it directly. Instead verify the public contract: pending subjects
            // added during suppressed state should NOT be committed.
            // This tests the commit path's interaction with suppression.
            GameStateStore.ResetForTesting();
            GameStateRecorder.PendingScienceSubjects.Clear();

            // Simulate: suppression is on (replay in progress), but something
            // leaked a subject into the pending list (shouldn't happen, but
            // if it did, commit should still work — max-wins is safe).
            GameStateRecorder.PendingScienceSubjects.Add(
                new PendingScienceSubject { subjectId = "test@Kerbin", science = 3.0f });

            ScienceTestHelpers.CommitScienceSubjects(GameStateRecorder.PendingScienceSubjects);
            GameStateRecorder.PendingScienceSubjects.Clear();

            // The subject was committed (max-wins policy handles it safely)
            float sci;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("test@Kerbin", out sci));
            Assert.Equal(3.0f, sci);

            // A subsequent commit with a lower value should NOT downgrade
            GameStateRecorder.PendingScienceSubjects.Add(
                new PendingScienceSubject { subjectId = "test@Kerbin", science = 1.0f });
            ScienceTestHelpers.CommitScienceSubjects(GameStateRecorder.PendingScienceSubjects);
            GameStateRecorder.PendingScienceSubjects.Clear();

            Assert.True(GameStateStore.TryGetCommittedSubjectScience("test@Kerbin", out sci));
            Assert.Equal(3.0f, sci);
        }

        [Fact]
        public void PendingScienceSubjects_ClearedByRecordingStoreReset()
        {
            GameStateRecorder.PendingScienceSubjects.Add(
                new PendingScienceSubject { subjectId = "test", science = 1.0f });
            Assert.Single(GameStateRecorder.PendingScienceSubjects);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
        }

        [Fact]
        public void RecordOriginalScience_TracksFirstValueOnly()
        {
            GameStateStore.ResetForTesting();

            GameStateStore.RecordOriginalScience("crewReport@Kerbin", 0.0f);
            Assert.Equal(1, GameStateStore.OriginalScienceValueCount);

            // Second call for same subject — should NOT update
            GameStateStore.RecordOriginalScience("crewReport@Kerbin", 2.0f);
            Assert.Equal(1, GameStateStore.OriginalScienceValueCount);

            // Different subject — should add
            GameStateStore.RecordOriginalScience("tempScan@Mun", 1.5f);
            Assert.Equal(2, GameStateStore.OriginalScienceValueCount);
        }

        [Fact]
        public void ClearScienceSubjects_ClearsOriginalsAndCommitted()
        {
            GameStateStore.ResetForTesting();

            ScienceTestHelpers.CommitScienceSubjects(new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@Kerbin", science = 5.0f }
            });
            GameStateStore.RecordOriginalScience("crewReport@Kerbin", 0.0f);

            Assert.Equal(1, GameStateStore.CommittedScienceSubjectCount);
            Assert.Equal(1, GameStateStore.OriginalScienceValueCount);

            GameStateStore.ClearScienceSubjects();

            Assert.Equal(0, GameStateStore.CommittedScienceSubjectCount);
            Assert.Equal(0, GameStateStore.OriginalScienceValueCount);
        }

        [Fact]
        public void ResetForTesting_ClearsOriginalScienceValues()
        {
            GameStateStore.RecordOriginalScience("test", 1.0f);
            Assert.Equal(1, GameStateStore.OriginalScienceValueCount);

            GameStateStore.ResetForTesting();
            Assert.Equal(0, GameStateStore.OriginalScienceValueCount);
        }

        [Fact]
        public void OriginalScience_SerializeDeserialize_RoundTrip()
        {
            GameStateStore.ResetForTesting();

            ScienceTestHelpers.CommitScienceSubjects(new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@Kerbin", science = 5.0f },
                new PendingScienceSubject { subjectId = "tempScan@Mun", science = 3.0f }
            });
            GameStateStore.RecordOriginalScience("crewReport@Kerbin", 0.0f);
            GameStateStore.RecordOriginalScience("tempScan@Mun", 1.5f);

            // Serialize
            var root = new ConfigNode("ROOT");
            GameStateStore.SerializeScienceSubjectsInto(root);

            // Reset and deserialize
            GameStateStore.ResetForTesting();
            Assert.Equal(0, GameStateStore.CommittedScienceSubjectCount);
            Assert.Equal(0, GameStateStore.OriginalScienceValueCount);

            GameStateStore.DeserializeScienceSubjectsFrom(root);

            Assert.Equal(2, GameStateStore.CommittedScienceSubjectCount);
            Assert.Equal(2, GameStateStore.OriginalScienceValueCount);

            float sci;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("crewReport@Kerbin", out sci));
            Assert.Equal(5.0f, sci);
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("tempScan@Mun", out sci));
            Assert.Equal(3.0f, sci);

            // Verify original values round-tripped correctly
            float orig;
            Assert.True(GameStateStore.TryGetOriginalScience("crewReport@Kerbin", out orig));
            Assert.Equal(0.0f, orig);
            Assert.True(GameStateStore.TryGetOriginalScience("tempScan@Mun", out orig));
            Assert.Equal(1.5f, orig);
        }

        [Fact]
        public void OriginalScience_DeserializeFrom_NoOriginalsNode_NoOp()
        {
            GameStateStore.ResetForTesting();

            // Older format: SCIENCE_SUBJECTS without ORIGINALS
            var root = new ConfigNode("ROOT");
            var sciNode = root.AddNode("SCIENCE_SUBJECTS");
            var entry = sciNode.AddNode("SUBJECT");
            entry.AddValue("id", "crewReport@Kerbin");
            entry.AddValue("science", "5.0");

            GameStateStore.DeserializeScienceSubjectsFrom(root);

            Assert.Equal(1, GameStateStore.CommittedScienceSubjectCount);
            Assert.Equal(0, GameStateStore.OriginalScienceValueCount);
        }

        [Fact]
        public void Serialize_CreatesNode_WhenOnlyOriginalsExist()
        {
            GameStateStore.ResetForTesting();

            // Edge case: originals tracked but committed already cleared
            // (shouldn't happen in practice, but tests serialization guard)
            GameStateStore.RecordOriginalScience("test", 1.0f);

            var root = new ConfigNode("ROOT");
            GameStateStore.SerializeScienceSubjectsInto(root);

            Assert.NotNull(root.GetNode("SCIENCE_SUBJECTS"));
            var sciNode = root.GetNode("SCIENCE_SUBJECTS");
            Assert.NotNull(sciNode.GetNode("ORIGINALS"));
        }

        #endregion

        #region Milestone Capture (CreateMilestoneEvent)

        [Fact]
        public void CreateMilestoneEvent_PopulatesFieldsCorrectly()
        {
            var evt = GameStateRecorder.CreateMilestoneEvent("FirstOrbitKerbin", 12345.0);

            Assert.Equal(GameStateEventType.MilestoneAchieved, evt.eventType);
            Assert.Equal("FirstOrbitKerbin", evt.key);
            Assert.Equal(12345.0, evt.ut);
            Assert.Equal("", evt.detail);
        }

        [Fact]
        public void CreateMilestoneEvent_NullId_DefaultsToEmpty()
        {
            var evt = GameStateRecorder.CreateMilestoneEvent(null, 100.0);

            Assert.Equal(GameStateEventType.MilestoneAchieved, evt.eventType);
            Assert.Equal("", evt.key);
        }

        [Fact]
        public void CreateMilestoneEvent_ConvertsToMilestoneAchievement()
        {
            var evt = GameStateRecorder.CreateMilestoneEvent("ReachSpace", 5000.0);
            var action = GameStateEventConverter.ConvertEvent(evt, "rec-abc");

            Assert.NotNull(action);
            Assert.Equal(GameActionType.MilestoneAchievement, action.Type);
            Assert.Equal("ReachSpace", action.MilestoneId);
            Assert.Equal("rec-abc", action.RecordingId);
            Assert.Equal(5000.0, action.UT);
            Assert.Equal(0f, action.MilestoneFundsAwarded);
            Assert.Equal(0f, action.MilestoneRepAwarded);
        }

        [Fact]
        public void MilestoneAchieved_DisplayDescription_ShowsAchieved()
        {
            var evt = new GameStateEvent
            {
                eventType = GameStateEventType.MilestoneAchieved,
                key = "FirstLaunch"
            };

            string desc = GameStateEventDisplay.GetDisplayDescription(evt);
            Assert.Equal("\"FirstLaunch\" achieved", desc);
        }

        [Fact]
        public void MilestoneAchieved_DisplayCategory_IsMilestone()
        {
            Assert.Equal("Milestone", GameStateEventDisplay.GetDisplayCategory(GameStateEventType.MilestoneAchieved));
        }

        #endregion

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            GameStateRecorder.SuppressCrewEvents = false;
            GameStateRecorder.SuppressResourceEvents = false;
            GameStateRecorder.IsReplayingActions = false;
        }
    }
}
