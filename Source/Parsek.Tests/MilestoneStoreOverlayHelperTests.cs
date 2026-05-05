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
        /// LastReplayedEventIndex >= eventIdx. Fails if the contract helper's cursor is off-by-one.
        /// </summary>
        [Fact]
        public void GetCommittedContractAcceptIds_PastEvent_NotIncluded()
        {
            AddMilestone(
                lastReplayedIndex: 0,
                Event(GameStateEventType.ContractAccepted, "contract-replayed"));

            var ids = MilestoneStore.GetCommittedContractAcceptIds();

            Assert.Empty(ids);
        }

        /// <summary>
        /// Event in a milestone that is hidden by the abandoned-branch filter. Fails if the contract helper bypasses IsEventVisibleToCurrentTimeline.
        /// </summary>
        [Fact]
        public void GetCommittedContractAcceptIds_HiddenByTimelineFilter_NotIncluded()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.ContractAccepted, "contract-hidden", recordingId: "rec_hidden"));

            var ids = MilestoneStore.GetCommittedContractAcceptIds();

            Assert.Empty(ids);
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

        /// <summary>
        /// LastReplayedEventIndex >= eventIdx. Fails if the retire helper's cursor is off-by-one.
        /// </summary>
        [Fact]
        public void GetCommittedKerbalRetireNames_PastEvent_NotIncluded()
        {
            AddMilestone(
                lastReplayedIndex: 0,
                Event(GameStateEventType.CrewRemoved, "Retired Kerman"));

            var names = MilestoneStore.GetCommittedKerbalRetireNames();

            Assert.Empty(names);
        }

        /// <summary>
        /// Event in a milestone that is hidden by the abandoned-branch filter. Fails if the retire helper bypasses IsEventVisibleToCurrentTimeline.
        /// </summary>
        [Fact]
        public void GetCommittedKerbalRetireNames_HiddenByTimelineFilter_NotIncluded()
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(GameStateEventType.CrewRemoved, "Hidden Retire Kerman", recordingId: "rec_hidden"));

            var names = MilestoneStore.GetCommittedKerbalRetireNames();

            Assert.Empty(names);
        }

        /// <summary>
        /// Uncommitted milestones are not part of the committed-future action set. Fails if any new overlay helper forgets the milestone Committed gate.
        /// </summary>
        [Theory]
        [InlineData(GameStateEventType.ContractAccepted, "contract-uncommitted")]
        [InlineData(GameStateEventType.CrewHired, "Uncommitted Hire Kerman")]
        [InlineData(GameStateEventType.CrewRemoved, "Uncommitted Retire Kerman")]
        public void NewOverlayHelpers_UncommittedMilestone_NotIncluded(GameStateEventType eventType, string key)
        {
            AddMilestone(
                lastReplayedIndex: -1,
                Event(eventType, key),
                committed: false);

            var values = QueryHelper(eventType);

            Assert.Empty(values);
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

        private static void AddMilestone(
            int lastReplayedIndex,
            GameStateEvent event0,
            bool committed = true)
        {
            AddMilestone(lastReplayedIndex, new[] { event0 }, committed);
        }

        private static void AddMilestone(
            int lastReplayedIndex,
            GameStateEvent event0,
            GameStateEvent event1,
            bool committed = true)
        {
            AddMilestone(lastReplayedIndex, new[] { event0, event1 }, committed);
        }

        private static void AddMilestone(
            int lastReplayedIndex,
            GameStateEvent event0,
            GameStateEvent event1,
            GameStateEvent event2,
            bool committed = true)
        {
            AddMilestone(lastReplayedIndex, new[] { event0, event1, event2 }, committed);
        }

        private static void AddMilestone(
            int lastReplayedIndex,
            GameStateEvent[] events,
            bool committed = true)
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = Guid.NewGuid().ToString("N"),
                Committed = committed,
                LastReplayedEventIndex = lastReplayedIndex,
                Events = new List<GameStateEvent>(events)
            });
        }

        private static HashSet<string> QueryHelper(GameStateEventType eventType)
        {
            switch (eventType)
            {
                case GameStateEventType.ContractAccepted:
                    return MilestoneStore.GetCommittedContractAcceptIds();
                case GameStateEventType.CrewHired:
                    return MilestoneStore.GetCommittedKerbalHireNames();
                case GameStateEventType.CrewRemoved:
                    return MilestoneStore.GetCommittedKerbalRetireNames();
                default:
                    throw new ArgumentOutOfRangeException(nameof(eventType), eventType, null);
            }
        }
    }
}
