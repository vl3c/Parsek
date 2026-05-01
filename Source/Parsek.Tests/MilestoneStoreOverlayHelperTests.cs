using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class MilestoneStoreOverlayHelperTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorGameStateStoreSuppress;
        private readonly bool priorRecordingStoreSuppress;

        public MilestoneStoreOverlayHelperTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorGameStateStoreSuppress = GameStateStore.SuppressLogging;
            priorRecordingStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;

            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            GameStateStore.SuppressLogging = priorGameStateStoreSuppress;
            RecordingStore.SuppressLogging = priorRecordingStoreSuppress;
        }

        /// <summary>
        /// Empty store yields empty set. Fails if the helper NREs on cold start.
        /// </summary>
        [Fact]
        public void GetCommittedKerbalHireNames_NoMilestones_EmptySet()
        {
            var names = MilestoneStore.GetCommittedKerbalHireNames();
            Assert.Empty(names);
        }

        /// <summary>
        /// LastReplayedEventIndex >= eventIdx. Fails if the cursor is off-by-one.
        /// </summary>
        [Fact]
        public void GetCommittedKerbalHireNames_PastEvent_NotIncluded()
        {
            AddMilestone(
                lastReplayedIndex: 0,
                Event(GameStateEventType.CrewHired, "Jebediah Kerman"));

            var names = MilestoneStore.GetCommittedKerbalHireNames();

            Assert.Empty(names);
        }

        /// <summary>
        /// Single future event. Fails if IsEventVisibleToCurrentTimeline filtering breaks future visibility.
        /// </summary>
        [Fact]
        public void GetCommittedKerbalHireNames_FutureEvent_Included()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.CrewHired, "Valentina Kerman"));

            var names = MilestoneStore.GetCommittedKerbalHireNames();

            Assert.Single(names);
            Assert.Contains("Valentina Kerman", names);
        }

        /// <summary>
        /// Event in a milestone that is hidden by the abandoned-branch filter. Fails if the helper bypasses IsEventVisibleToCurrentTimeline.
        /// </summary>
        [Fact]
        public void GetCommittedKerbalHireNames_HiddenByTimelineFilter_NotIncluded()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.CrewHired, "Hidden Kerman", recordingId: "rec_hidden"));

            var names = MilestoneStore.GetCommittedKerbalHireNames();

            Assert.Empty(names);
        }

        /// <summary>
        /// Three events with raw contract keys. Expect three-string result, no parsing performed inside the helper. Fails if the helper accidentally calls Guid.TryParse (which would silently drop modded contracts that use a non-standard key shape).
        /// </summary>
        [Fact]
        public void GetCommittedContractAcceptIds_ReturnsRawKeyStrings()
        {
            string guidA = Guid.NewGuid().ToString();
            string guidB = Guid.NewGuid().ToString();
            const string moddedKey = "ContractConfigurator:non-guid-contract-key";
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.ContractAccepted, guidA),
                Event(GameStateEventType.ContractAccepted, guidB),
                Event(GameStateEventType.ContractAccepted, moddedKey));

            var ids = MilestoneStore.GetCommittedContractAcceptIds();

            Assert.Equal(3, ids.Count);
            Assert.Contains(guidA, ids);
            Assert.Contains(guidB, ids);
            Assert.Contains(moddedKey, ids);
        }

        /// <summary>
        /// Log-assertion test on the non-empty Verbose line. Fails if the helper stops emitting the non-empty count.
        /// </summary>
        [Fact]
        public void GetCommittedContractAcceptIds_LogsCount()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.ContractAccepted, "contract-a"),
                Event(GameStateEventType.ContractAccepted, "contract-b"));

            MilestoneStore.GetCommittedContractAcceptIds();

            Assert.Contains(logLines, line =>
                line.Contains("[VERBOSE][MilestoneStore]") &&
                line.Contains("GetCommittedContractAcceptIds: 2 committed contract accept(s)"));
        }

        /// <summary>
        /// Write a synthetic event with key = Guid.NewGuid().ToString() (default "D" form, with hyphens, matching GameStateRecorder.cs:413). Assert the helper returns it; assert that MilestoneStore.FindCommittedEvent(GameStateEventType.ContractAccepted, key) round-trips. Guards against the ToString("N") bug that the design review flagged.
        /// </summary>
        [Fact]
        public void GetCommittedContractAcceptIds_KeyShapeMatchesRecorder()
        {
            string key = Guid.NewGuid().ToString();
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.ContractAccepted, key, ut: 123.0));

            var ids = MilestoneStore.GetCommittedContractAcceptIds();
            var committedEvent = MilestoneStore.FindCommittedEvent(GameStateEventType.ContractAccepted, key);

            Assert.Single(ids);
            Assert.Contains(key, ids);
            Assert.True(committedEvent.HasValue);
            Assert.Equal(123.0, committedEvent.Value.ut);
            Assert.Equal(key, committedEvent.Value.key);
        }

        /// <summary>
        /// GameStateEventType.CrewRemoved is the source for the FutureRetired overlay variant in plan section 4.1. Fails if the helper reads CrewHired or the converted ledger instead of MilestoneStore's raw CrewRemoved slice.
        /// </summary>
        [Fact]
        public void GetCommittedKerbalRetireNames_FutureCrewRemovedEvent_Included()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.CrewRemoved, "Bill Kerman"),
                Event(GameStateEventType.CrewHired, "Bob Kerman"));

            var names = MilestoneStore.GetCommittedKerbalRetireNames();

            Assert.Single(names);
            Assert.Contains("Bill Kerman", names);
            Assert.DoesNotContain("Bob Kerman", names);
        }

        private static GameStateEvent Event(
            GameStateEventType type,
            string key,
            double ut = 100.0,
            string recordingId = null)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key,
                recordingId = recordingId
            };
        }

        private static void AddMilestone(int lastReplayedIndex, params GameStateEvent[] events)
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = Guid.NewGuid().ToString("N"),
                Committed = true,
                LastReplayedEventIndex = lastReplayedIndex,
                Events = new List<GameStateEvent>(events)
            });
        }
    }
}
