using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests on the three units of <c>LedgerOrchestrator.ClassifyKerbalAssignmentRepair</c>:
    /// <see cref="LedgerOrchestrator.FilterKerbalRepairExistingRows"/>,
    /// <see cref="LedgerOrchestrator.MatchKerbalRepairRowsBySameName"/> and
    /// <see cref="LedgerOrchestrator.MatchKerbalRepairRowsAsRemaps"/>. The whole-method
    /// behaviour is pinned by <see cref="ClassifyKerbalAssignmentRepairCharacterizationTests"/>.
    /// </summary>
    public class KerbalRepairClassifyUnitsTests
    {
        private static GameAction Row(string kerbal, KerbalEndState end = KerbalEndState.Aboard,
            string role = "Pilot", double ut = 100.0)
        {
            return new GameAction
            {
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-a",
                KerbalName = kerbal,
                KerbalRole = role,
                KerbalEndStateField = end,
                UT = ut,
                StartUT = 100f,
                EndUT = 200f,
            };
        }

        [Fact]
        public void Filter_DropsNullsAndTouristsOfAnyCase_KeepsOrder()
        {
            var stats = new LedgerOrchestrator.KerbalAssignmentRepairStats();
            var jeb = Row("Jeb");
            var bill = Row("Bill");

            var kept = LedgerOrchestrator.FilterKerbalRepairExistingRows(
                new List<GameAction> { null, Row("T1", role: "Tourist"), jeb, Row("T2", role: "TOURIST"), bill },
                stats);

            Assert.Equal(new[] { jeb, bill }, kept);
            Assert.Equal(2, stats.TouristRowsSkipped);
        }

        [Fact]
        public void Filter_AddsToAnExistingTouristCount()
        {
            var stats = new LedgerOrchestrator.KerbalAssignmentRepairStats();
            stats.TouristRowsSkipped = 3;

            var kept = LedgerOrchestrator.FilterKerbalRepairExistingRows(
                new List<GameAction> { Row("T", role: "tourist") }, stats);

            Assert.Empty(kept);
            Assert.Equal(4, stats.TouristRowsSkipped);
        }

        [Fact]
        public void SameName_PairsByNameAndRecordsOnlyEndStateChanges()
        {
            var stats = new LedgerOrchestrator.KerbalAssignmentRepairStats();
            var existing = new List<GameAction> { Row("Jeb", KerbalEndState.Aboard), Row("Bill", KerbalEndState.Dead) };
            var desired = new List<GameAction> { Row("Bill", KerbalEndState.Dead), Row("Neo"), Row("Jeb", KerbalEndState.Recovered) };
            var me = new bool[existing.Count];
            var md = new bool[desired.Count];

            LedgerOrchestrator.MatchKerbalRepairRowsBySameName(existing, desired, me, md, stats);

            Assert.Equal(new[] { true, true }, me);
            Assert.Equal(new[] { true, false, true }, md);
            Assert.Empty(stats.RemappedRows);
            var w = Assert.Single(stats.EndStateRewrites);
            Assert.Equal("Jeb", w.KerbalName);
            Assert.Equal(KerbalEndState.Aboard, w.FromState);
            Assert.Equal(KerbalEndState.Recovered, w.ToState);
        }

        [Fact]
        public void SameName_SkipsConsumedRowsAndNullDesiredRows()
        {
            var stats = new LedgerOrchestrator.KerbalAssignmentRepairStats();
            var existing = new List<GameAction> { Row("Jeb", KerbalEndState.Dead), Row("Jeb", KerbalEndState.Aboard) };
            var desired = new List<GameAction> { null, Row("Jeb", KerbalEndState.Aboard) };
            var me = new[] { true, false };
            var md = new bool[desired.Count];

            LedgerOrchestrator.MatchKerbalRepairRowsBySameName(existing, desired, me, md, stats);

            Assert.Equal(new[] { true, true }, me);
            Assert.Equal(new[] { false, true }, md);
            Assert.Empty(stats.EndStateRewrites);
        }

        [Fact]
        public void Remaps_SkipsDesiredRowsAlreadyMatched()
        {
            var stats = new LedgerOrchestrator.KerbalAssignmentRepairStats();
            var existing = new List<GameAction> { Row("Old") };
            var desired = new List<GameAction> { Row("Taken"), Row("New") };
            var me = new bool[1];
            var md = new[] { true, false };

            LedgerOrchestrator.MatchKerbalRepairRowsAsRemaps(existing, desired, me, md, stats);

            Assert.Equal(new[] { true }, me);
            Assert.Equal(new[] { true, true }, md);
            var r = Assert.Single(stats.RemappedRows);
            Assert.Equal("Old", r.FromName);
            Assert.Equal("New", r.ToName);
            Assert.Empty(stats.EndStateRewrites);
        }

        [Fact]
        public void Remaps_RecordsRenameAndEndStateTogether()
        {
            var stats = new LedgerOrchestrator.KerbalAssignmentRepairStats();
            var existing = new List<GameAction> { Row("Old", KerbalEndState.Aboard) };
            var desired = new List<GameAction> { Row("New", KerbalEndState.Dead) };

            LedgerOrchestrator.MatchKerbalRepairRowsAsRemaps(existing, desired, new bool[1], new bool[1], stats);

            Assert.Single(stats.RemappedRows);
            var w = Assert.Single(stats.EndStateRewrites);
            Assert.Equal("New", w.KerbalName);
            Assert.Equal(KerbalEndState.Aboard, w.FromState);
            Assert.Equal(KerbalEndState.Dead, w.ToState);
        }

        [Fact]
        public void Remaps_SameNameLeftForThisPass_RecordsOnlyTheEndState()
        {
            var stats = new LedgerOrchestrator.KerbalAssignmentRepairStats();
            var existing = new List<GameAction> { Row("Jeb", KerbalEndState.Aboard) };
            var desired = new List<GameAction> { Row("Jeb", KerbalEndState.Recovered) };

            LedgerOrchestrator.MatchKerbalRepairRowsAsRemaps(existing, desired, new bool[1], new bool[1], stats);

            Assert.Empty(stats.RemappedRows);
            Assert.Single(stats.EndStateRewrites);
        }

        [Fact]
        public void Remaps_OutsideTheTimingWindow_IsLeftUnmatched()
        {
            var stats = new LedgerOrchestrator.KerbalAssignmentRepairStats();
            var existing = new List<GameAction> { Row("Old", ut: 100.0) };
            var desired = new List<GameAction> { Row("New", ut: 100.125) };
            var me = new bool[1];
            var md = new bool[1];

            LedgerOrchestrator.MatchKerbalRepairRowsAsRemaps(existing, desired, me, md, stats);

            Assert.False(me[0]);
            Assert.False(md[0]);
            Assert.Empty(stats.RemappedRows);
            Assert.Empty(stats.EndStateRewrites);
        }
    }
}
