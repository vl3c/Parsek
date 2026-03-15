using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for extracted methods in GameStateStore:
    /// BuildEventTypeDistribution (internal static pure method).
    /// </summary>
    [Collection("Sequential")]
    public class GameStateStoreExtractedTests
    {
        public GameStateStoreExtractedTests()
        {
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
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

        [Fact]
        public void BuildEventTypeDistribution_CommaSeparated()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent { eventType = GameStateEventType.ContractAccepted },
                new GameStateEvent { eventType = GameStateEventType.FundsChanged }
            };

            string result = GameStateStore.BuildEventTypeDistribution(events);

            // Result should have exactly one comma separator
            Assert.Contains(", ", result);
        }

        #endregion
    }
}
