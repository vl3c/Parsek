using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GameStateEventTests
    {
        public GameStateEventTests()
        {
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
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

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry"
            });

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.ContractAccepted,
                key = "guid-1"
            });

            Assert.Equal(2, GameStateStore.EventCount);
            Assert.Equal(GameStateEventType.TechResearched, GameStateStore.Events[0].eventType);
            Assert.Equal(GameStateEventType.ContractAccepted, GameStateStore.Events[1].eventType);
        }

        [Fact]
        public void GameStateStore_ClearEvents_RemovesAll()
        {
            GameStateStore.ResetForTesting();

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry"
            });

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
            GameStateStore.AddEvent(new GameStateEvent { ut = 100 });
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

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.FundsChanged,
                key = "ContractAdvance",
                valueBefore = 10000,
                valueAfter = 15000
            });

            // Within 0.1s epsilon — should update, not add
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100.05,
                eventType = GameStateEventType.FundsChanged,
                key = "VesselRecovery",
                valueBefore = 15000,
                valueAfter = 18000
            });

            Assert.Equal(1, GameStateStore.EventCount);
            Assert.Equal(10000, GameStateStore.Events[0].valueBefore);
            Assert.Equal(18000, GameStateStore.Events[0].valueAfter);
        }

        [Fact]
        public void ResourceCoalescing_BeyondEpsilon_AddsNewEvent()
        {
            GameStateStore.ResetForTesting();

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.FundsChanged,
                key = "ContractAdvance",
                valueBefore = 10000,
                valueAfter = 15000
            });

            // Beyond 0.1s epsilon — should add a new event
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100.2,
                eventType = GameStateEventType.FundsChanged,
                key = "VesselRecovery",
                valueBefore = 15000,
                valueAfter = 18000
            });

            Assert.Equal(2, GameStateStore.EventCount);
        }

        [Fact]
        public void ResourceCoalescing_DifferentTypes_NotCoalesced()
        {
            GameStateStore.ResetForTesting();

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.FundsChanged,
                valueBefore = 10000,
                valueAfter = 15000
            });

            // Same UT but different type — should NOT coalesce
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.ScienceChanged,
                valueBefore = 50,
                valueAfter = 75
            });

            Assert.Equal(2, GameStateStore.EventCount);
        }

        [Fact]
        public void ResourceCoalescing_NonResourceEvents_NeverCoalesced()
        {
            GameStateStore.ResetForTesting();

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry"
            });

            // Same UT and type but non-resource — should NOT coalesce
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.TechResearched,
                key = "stability"
            });

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
        public void CrewSuppression_FlagCanBeSet()
        {
            GameStateRecorder.SuppressCrewEvents = true;
            Assert.True(GameStateRecorder.SuppressCrewEvents);

            // Restore
            GameStateRecorder.SuppressCrewEvents = false;
            Assert.False(GameStateRecorder.SuppressCrewEvents);
        }

        [Fact]
        public void ResourceSuppression_FlagDefaultsFalse()
        {
            Assert.False(GameStateRecorder.SuppressResourceEvents);
        }

        [Fact]
        public void ResourceSuppression_FlagCanBeSet()
        {
            GameStateRecorder.SuppressResourceEvents = true;
            Assert.True(GameStateRecorder.SuppressResourceEvents);

            // Restore
            GameStateRecorder.SuppressResourceEvents = false;
            Assert.False(GameStateRecorder.SuppressResourceEvents);
        }

        #endregion

        #region Event History

        [Fact]
        public void EventHistory_CanFindMostRecentFacilityState()
        {
            GameStateStore.ResetForTesting();

            // Simulate history: pad upgraded twice
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.FacilityUpgraded,
                key = "SpaceCenter/LaunchPad",
                valueBefore = 0,
                valueAfter = 0.5
            });
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.FacilityUpgraded,
                key = "SpaceCenter/LaunchPad",
                valueBefore = 0.5,
                valueAfter = 1.0
            });
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 150,
                eventType = GameStateEventType.BuildingDestroyed,
                key = "SpaceCenter/Administration"
            });

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
        public void AddEvent_StampsCurrentEpoch()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();

            MilestoneStore.CurrentEpoch = 3;
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry"
            });

            Assert.Equal(3u, GameStateStore.Events[0].epoch);

            // Change epoch and add another
            MilestoneStore.CurrentEpoch = 5;
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2"
            });

            Assert.Equal(5u, GameStateStore.Events[1].epoch);

            // First event still has original epoch
            Assert.Equal(3u, GameStateStore.Events[0].epoch);
        }

        [Fact]
        public void GameStateEvent_EpochField_SerializationRoundtrip()
        {
            var original = new GameStateEvent
            {
                ut = 17000,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                epoch = 7
            };

            var node = new ConfigNode("GAME_STATE_EVENT");
            original.SerializeInto(node);

            var deserialized = GameStateEvent.DeserializeFrom(node);

            Assert.Equal(7u, deserialized.epoch);
        }

        [Fact]
        public void GameStateEvent_EpochZero_NotSerialized()
        {
            // epoch=0 is omitted from ConfigNode (space optimization)
            var original = new GameStateEvent
            {
                ut = 17000,
                eventType = GameStateEventType.BuildingDestroyed,
                epoch = 0
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
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "test"
            });

            // ClearEvents should not touch baselines
            GameStateStore.ClearEvents();
            Assert.Equal(0, GameStateStore.EventCount);
            Assert.Equal(1, GameStateStore.BaselineCount);

            // ClearBaselines should not touch events (already cleared, just verify isolation)
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2"
            });
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
    }
}
