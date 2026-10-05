using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for extracted methods in GameStateStore:
    /// BuildEventTypeDistribution (internal static pure method) and the
    /// PurgeEventsForRecordings mechanism (live events + orphaned contract snapshots).
    /// </summary>
    [Collection("Sequential")]
    public class GameStateStoreExtractedTests : System.IDisposable
    {
        public GameStateStoreExtractedTests()
        {
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            GameStateRecorder.TagResolverForTesting = null;
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        #region BuildEventTypeDistribution

        [Fact]
        public void BuildEventTypeDistribution_EmptyList_ReturnsEmptyString()
        {
            var events = new List<GameStateEvent>();

            string result = GameStateStore.BuildEventTypeDistribution(events);

            Assert.Equal("", result);
        }

        [Fact]
        public void BuildEventTypeDistribution_SingleEventType_ReturnsCorrectCount()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent { eventType = GameStateEventType.ContractAccepted },
                new GameStateEvent { eventType = GameStateEventType.ContractAccepted },
                new GameStateEvent { eventType = GameStateEventType.ContractAccepted }
            };

            string result = GameStateStore.BuildEventTypeDistribution(events);

            Assert.Equal("ContractAccepted=3", result);
        }

        [Fact]
        public void BuildEventTypeDistribution_MultipleTypes_AllCounted()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent { eventType = GameStateEventType.ContractAccepted },
                new GameStateEvent { eventType = GameStateEventType.FundsChanged },
                new GameStateEvent { eventType = GameStateEventType.FundsChanged },
                new GameStateEvent { eventType = GameStateEventType.TechResearched },
                new GameStateEvent { eventType = GameStateEventType.ContractAccepted }
            };

            string result = GameStateStore.BuildEventTypeDistribution(events);

            // Order is not guaranteed by Dictionary iteration, so check contains
            Assert.Contains("ContractAccepted=2", result);
            Assert.Contains("FundsChanged=2", result);
            Assert.Contains("TechResearched=1", result);
        }

        [Fact]
        public void BuildEventTypeDistribution_AllEventTypes_HandlesAllEnumValues()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent { eventType = GameStateEventType.ContractOffered },
                new GameStateEvent { eventType = GameStateEventType.CrewHired },
                new GameStateEvent { eventType = GameStateEventType.FacilityUpgraded },
                new GameStateEvent { eventType = GameStateEventType.BuildingDestroyed },
                new GameStateEvent { eventType = GameStateEventType.ReputationChanged }
            };

            string result = GameStateStore.BuildEventTypeDistribution(events);

            Assert.Contains("ContractOffered=1", result);
            Assert.Contains("CrewHired=1", result);
            Assert.Contains("FacilityUpgraded=1", result);
            Assert.Contains("BuildingDestroyed=1", result);
            Assert.Contains("ReputationChanged=1", result);
        }

        #endregion

        #region PurgeEventsForRecordings

        [Fact]
        public void PurgeEventsForRecordings_RemovesTaggedEvent_LiveAndSnapshot()
        {
            // This test pins the purge *mechanism* across the stores touched by
            // PurgeEventsForRecordings: live events and contract snapshots.
            GameStateRecorder.TagResolverForTesting = () => "purge-tagged";

            // Branch 1: live events list.
            var evt = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "contract-1",
                detail = ""
            };
            GameStateRecorder.Emit(ref evt, "test");

            // Branch 2: orphan contract snapshots (must be removed when the matching
            // ContractAccepted event is purged - PurgeOrphanedContractSnapshots).
            var contractNode = new ConfigNode("CONTRACT");
            contractNode.AddValue("guid", "contract-1");
            GameStateStore.AddContractSnapshot("contract-1", contractNode);

            int eventCountBefore = GameStateStore.EventCount;
            int snapshotCountBefore = GameStateStore.ContractSnapshots.Count;
            Assert.True(eventCountBefore >= 1);
            Assert.True(snapshotCountBefore >= 1);

            int removed = GameStateStore.PurgeEventsForRecordings(
                new[] { "purge-tagged" }, "test");

            Assert.Equal(1, removed);
            Assert.Equal(eventCountBefore - 1, GameStateStore.EventCount);
            // Snapshot's matching ContractAccepted event was purged, so the orphan
            // cleanup should have removed the snapshot too.
            Assert.Equal(snapshotCountBefore - 1, GameStateStore.ContractSnapshots.Count);
        }

        #endregion
    }
}
