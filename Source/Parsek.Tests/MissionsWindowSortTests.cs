using Xunit;
using static Parsek.MissionsWindowUI;

namespace Parsek.Tests
{
    // Unit tests for the pure mission-row sort comparison extracted from MissionsWindowUI.
    // Verifies the primary key per column, the tree-index / original-position tiebreakers
    // (which keep a tree's clones grouped), and ascending/descending direction.
    public class MissionsWindowSortTests
    {
        // Sign of the comparison, so assertions don't depend on the exact magnitude.
        private static int Cmp(
            string aName, int aIndex, double aStart, int aOrig,
            string bName, int bIndex, double bStart, int bOrig,
            MissionSortColumn col, bool asc)
        {
            return System.Math.Sign(CompareMissionRows(
                aName, aIndex, aStart, aOrig, bName, bIndex, bStart, bOrig, col, asc));
        }

        [Fact]
        public void Index_OrdersByTreeIndex_Ascending()
        {
            Assert.Equal(-1, Cmp("Z", 1, 0, 0, "A", 2, 0, 0, MissionSortColumn.Index, true));
            Assert.Equal(1, Cmp("A", 3, 0, 0, "A", 2, 0, 0, MissionSortColumn.Index, true));
        }

        [Fact]
        public void Index_SameIndex_TiebreaksByOriginalPosition()
        {
            // Same tree index (e.g. a clone) -> the earlier list position wins, keeping the
            // original above its clone regardless of name.
            Assert.Equal(-1, Cmp("clone", 1, 0, 0, "orig", 1, 0, 1, MissionSortColumn.Index, true));
        }

        [Fact]
        public void Name_OrdersAlphabetically_CaseInsensitive_ThenIndex()
        {
            Assert.Equal(-1, Cmp("apple", 9, 0, 0, "Banana", 1, 0, 0, MissionSortColumn.Name, true));
            // Same name -> tiebreak by tree index.
            Assert.Equal(-1, Cmp("Same", 1, 0, 5, "Same", 2, 0, 0, MissionSortColumn.Name, true));
        }

        [Fact]
        public void StartTime_OrdersByStartUT_ThenIndex()
        {
            Assert.Equal(-1, Cmp("b", 2, 100.0, 0, "a", 1, 200.0, 0, MissionSortColumn.StartTime, true));
            // Same start -> tiebreak by index.
            Assert.Equal(-1, Cmp("b", 1, 50.0, 0, "a", 2, 50.0, 0, MissionSortColumn.StartTime, true));
        }

        [Fact]
        public void Descending_NegatesTheComparison()
        {
            int asc = Cmp("A", 1, 0, 0, "B", 2, 0, 0, MissionSortColumn.Index, true);
            int desc = Cmp("A", 1, 0, 0, "B", 2, 0, 0, MissionSortColumn.Index, false);
            Assert.Equal(asc, -desc);
        }

        [Fact]
        public void EqualRows_CompareEqual()
        {
            Assert.Equal(0, Cmp("X", 1, 10.0, 3, "X", 1, 10.0, 3, MissionSortColumn.Name, true));
            Assert.Equal(0, Cmp("X", 1, 10.0, 3, "X", 1, 10.0, 3, MissionSortColumn.StartTime, false));
        }
    }
}
