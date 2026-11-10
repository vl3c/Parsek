using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct cases for the two units split out of
    /// <c>LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard</c>:
    /// <see cref="LedgerOrchestrator.CollectDiscardedIrreversibleEventActions"/> and
    /// <see cref="LedgerOrchestrator.ConvertDiscardedScienceSubjects"/>.
    /// </summary>
    [Collection("Sequential")]
    public class DiscardRehomeUnitsTests : IDisposable
    {
        private const string RecA = "rec-A";
        private const string Other = "rec-other";
        private const string Committed = "rec-committed";

        public DiscardRehomeUnitsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ResetAll();
        }

        public void Dispose()
        {
            ResetAll();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ResetAll()
        {
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
        }

        private static void AddEvent(GameStateEventType type, double ut, string key, string detail,
            string tag, double before = 0.0, double after = 0.0)
        {
            var e = new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key,
                detail = detail,
                recordingId = tag,
                valueBefore = before,
                valueAfter = after,
            };
            GameStateStore.AddEvent(ref e);
        }

        private static void AddSubject(string subjectId, float science, double captureUT, string tag)
        {
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = subjectId,
                science = science,
                subjectMaxValue = 10f,
                captureUT = captureUT,
                reasonKey = "ScienceTransmission",
                recordingId = tag,
                scienceGainMultiplier = 1f,
            });
        }

        private static HashSet<string> Ids(params string[] ids) => new HashSet<string>(ids);

        [Fact]
        public void Collect_ConvertsEachIrreversibleKind_UntaggedAndCountedPerKind()
        {
            AddEvent(GameStateEventType.ContractCompleted, 300.0, "guid-done", "fundsReward=100", RecA);
            AddEvent(GameStateEventType.ContractCancelled, 301.0, "guid-x", "fundsPenalty=5;repPenalty=1", RecA);
            AddEvent(GameStateEventType.MilestoneAchieved, 302.0, "FirstLaunch",
                GameStateRecorder.BuildMilestoneDetail(500.0, 1f, 0.0), RecA);
            AddEvent(GameStateEventType.ScienceChanged, 303.0, GameStateEventConverter.StrategyInputReasonKey,
                "", RecA, 100.0, 75.0);
            AddEvent(GameStateEventType.BuildingDestroyed, 304.0,
                "SpaceCenter/LaunchPad/Facility/mainBuilding", "", RecA);
            AddEvent(GameStateEventType.ScienceLabCredited, 305.0, "ScienceTransmission", "vessel=Lab",
                RecA, 10.0, 12.0);
            AddEvent(GameStateEventType.TechResearched, 306.0, "survivability",
                GameStateRecorder.BuildFreeTechUnlockDetail(45f, null), RecA);

            var rehomed = new List<GameAction>();
            LedgerOrchestrator.CollectDiscardedIrreversibleEventActions(Ids(RecA), rehomed,
                out int contract, out int milestone, out int strategy,
                out int building, out int lab, out int freeTech);

            Assert.Equal(7, rehomed.Count);
            Assert.All(rehomed, a => Assert.True(string.IsNullOrEmpty(a.RecordingId)));
            Assert.Equal(new[] { 300.0, 301.0, 302.0, 303.0, 304.0, 305.0, 306.0 },
                rehomed.Select(a => a.UT).ToArray());
            Assert.Equal(2, contract);
            Assert.Equal(1, milestone);
            Assert.Equal(1, strategy);
            Assert.Equal(1, building);
            Assert.Equal(1, lab);
            Assert.Equal(1, freeTech);
        }

        [Fact]
        public void Collect_SkipsOtherIdsUntaggedCommittedAndReversibleEvents()
        {
            RecordingStore.AddCommittedInternal(new Recording { RecordingId = Committed });
            AddEvent(GameStateEventType.ContractCompleted, 300.0, "guid-other", "fundsReward=100", Other);
            AddEvent(GameStateEventType.ContractCompleted, 301.0, "guid-untagged", "fundsReward=100", "");
            AddEvent(GameStateEventType.ContractCompleted, 302.0, "guid-committed", "fundsReward=100", Committed);
            AddEvent(GameStateEventType.ScienceChanged, 303.0, "ScienceTransmission", "", RecA, 0.0, 40.0);
            AddEvent(GameStateEventType.TechResearched, 304.0, "basicRocketry", "cost=5", RecA);
            AddEvent(GameStateEventType.ContractCompleted, 305.0, "guid-mine", "fundsReward=100", RecA);

            var rehomed = new List<GameAction>();
            LedgerOrchestrator.CollectDiscardedIrreversibleEventActions(Ids(RecA, Committed), rehomed,
                out int contract, out int milestone, out int strategy,
                out int building, out int lab, out int freeTech);

            var row = Assert.Single(rehomed);
            Assert.Equal("guid-mine", row.ContractId);
            Assert.Equal(1, contract);
            Assert.Equal(0, milestone + strategy + building + lab + freeTech);
        }

        [Fact]
        public void Collect_EventWithNoRow_IsSkippedUncounted_AndRowsAppendToTheList()
        {
            AddEvent(GameStateEventType.ScienceLabCredited, 300.0, "ScienceTransmission", "vessel=Lab",
                RecA, 22.0, 22.0);
            var existing = new GameAction { Type = GameActionType.FundsSpending };
            var rehomed = new List<GameAction> { existing };

            LedgerOrchestrator.CollectDiscardedIrreversibleEventActions(Ids(RecA), rehomed,
                out int contract, out int milestone, out int strategy,
                out int building, out int lab, out int freeTech);

            Assert.Same(existing, Assert.Single(rehomed));
            Assert.Equal(0, contract + milestone + strategy + building + lab + freeTech);
        }

        [Fact]
        public void Science_NoMatch_ReturnsNullAndLeavesTheListAlone()
        {
            RecordingStore.AddCommittedInternal(new Recording { RecordingId = Committed });
            AddSubject("crewReport@KerbinSrfLanded", 1.5f, 500.0, Other);
            AddSubject("evaReport@KerbinSrfLanded", 2.5f, 501.0, "");
            AddSubject("goo@KerbinSrfLanded", 3.5f, 502.0, Committed);
            var rehomed = new List<GameAction>();

            var result = LedgerOrchestrator.ConvertDiscardedScienceSubjects(
                Ids(RecA, Committed), rehomed, out int count);

            Assert.Null(result);
            Assert.Empty(rehomed);
            Assert.Equal(0, count);
            Assert.Equal(3, GameStateRecorder.PendingScienceSubjects.Count);
        }

        [Fact]
        public void Science_ConvertsMatchedSubjectsAcrossTheirSpan_UntaggedAndAppended()
        {
            AddSubject("crewReport@KerbinSrfLanded", 1.5f, 500.0, RecA);
            AddSubject("seismic@KerbinSrfLanded", 4.75f, 505.0, Other);
            AddSubject("goo@KerbinSrfLanded", 0f, 510.0, RecA);
            AddSubject("evaReport@KerbinFlyingLow", 3.25f, 520.0, RecA);
            var existing = new GameAction { Type = GameActionType.FundsSpending };
            var rehomed = new List<GameAction> { existing };

            var result = LedgerOrchestrator.ConvertDiscardedScienceSubjects(
                Ids(RecA), rehomed, out int count);

            Assert.NotNull(result);
            Assert.Equal(3, count); // the zero-science subject counts, though it makes no row
            Assert.Equal(new[] { "crewReport@KerbinSrfLanded", "evaReport@KerbinFlyingLow" },
                result.Select(a => a.SubjectId).ToArray());
            Assert.All(result, a =>
            {
                Assert.Equal(GameActionType.ScienceEarning, a.Type);
                Assert.True(string.IsNullOrEmpty(a.RecordingId));
            });
            Assert.Equal(3, rehomed.Count);
            Assert.Same(existing, rehomed[0]);
            Assert.Same(result[0], rehomed[1]);
            Assert.Same(result[1], rehomed[2]);
            // The pending list keeps its entries and their tags; the caller removes them.
            Assert.Equal(new[] { RecA, Other, RecA, RecA },
                GameStateRecorder.PendingScienceSubjects.Select(s => s.recordingId).ToArray());
        }
    }
}
