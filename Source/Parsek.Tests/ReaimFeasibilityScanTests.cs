using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // M-MIS-1 (docs/dev/plans/reaim-resolver-reliability.md): the pure departure-scan selection
    // helpers behind the deterministic re-aim E2E. The scan covers exactly one synodic period, so
    // the feasibility band is CYCLIC (it can wrap from the last index back to 0); the helpers pick
    // the band CENTER (the robust mid-band departure for the strict test) and the band EDGE (the
    // first success, the fragile departure the old live-UT test picked by accident).
    public class ReaimFeasibilityScanTests
    {
        private static bool[] Scan(string pattern)
        {
            var scan = new bool[pattern.Length];
            for (int i = 0; i < pattern.Length; i++)
                scan[i] = pattern[i] == 'X';
            return scan;
        }

        [Theory]
        [InlineData("........", -1)]
        [InlineData("X.......", 0)]
        [InlineData("...X....", 3)]
        [InlineData("...XXXX.", 3)]
        [InlineData("XXXXXXXX", 0)]
        public void FirstSuccessIndex_ReturnsFirstTrue(string pattern, int expected)
        {
            Assert.Equal(expected, ReaimFeasibilityScan.FirstSuccessIndex(Scan(pattern)));
        }

        [Fact]
        public void FirstSuccessIndex_NullOrEmpty_ReturnsMinusOne()
        {
            Assert.Equal(-1, ReaimFeasibilityScan.FirstSuccessIndex(null));
            Assert.Equal(-1, ReaimFeasibilityScan.FirstSuccessIndex(new bool[0]));
        }

        [Theory]
        [InlineData("........", -1)]  // no feasible departure
        [InlineData("...X....", 3)]   // single entry run
        [InlineData("..XXXXX.", 4)]   // single run: center of 5 starting at 2
        [InlineData("..XXXX..", 3)]   // even-length run: lower-middle of 4 starting at 2
        [InlineData("XXXXXXXX", 3)]   // all-true: run starts at 0, lower-middle of 8
        [InlineData("XX....XX", 7)]   // wrapped run 6,7,0,1 (len 4): lower-middle by run order = 7
        [InlineData("X.....XX", 7)]   // wrapped run 6,7,0 (len 3): center = index 7
        [InlineData("XXX..XX.", 1)]   // two runs, no wrap (last entry false): len-3 run at 0 wins
        [InlineData(".XX..XX.", 1)]   // tie between len-2 runs: first in scan order wins
        [InlineData("XX..XXX.", 5)]   // longer later run wins over earlier shorter run
        public void CenterOfLongestRunIndex_Cyclic_PicksBandCenter(string pattern, int expected)
        {
            Assert.Equal(expected, ReaimFeasibilityScan.CenterOfLongestRunIndex(Scan(pattern), cyclic: true));
        }

        [Theory]
        [InlineData("XX....XX", 0)]   // non-cyclic: runs at 0..1 and 6..7 tie, first wins, center 0
        [InlineData("X.....XX", 6)]   // non-cyclic: len-2 run at 6 beats len-1 run at 0
        public void CenterOfLongestRunIndex_NonCyclic_DoesNotWrap(string pattern, int expected)
        {
            Assert.Equal(expected, ReaimFeasibilityScan.CenterOfLongestRunIndex(Scan(pattern), cyclic: false));
        }

        [Fact]
        public void CenterOfLongestRunIndex_NullOrEmpty_ReturnsMinusOne()
        {
            Assert.Equal(-1, ReaimFeasibilityScan.CenterOfLongestRunIndex(null, cyclic: true));
            Assert.Equal(-1, ReaimFeasibilityScan.CenterOfLongestRunIndex(new bool[0], cyclic: true));
        }

        [Fact]
        public void CenterOfLongestRunIndex_WrappedRunLosesTiesToLinearRun()
        {
            // Wrapped run 7,0 (len 2) ties the linear run 3,4 (len 2): the wrapped run wins only
            // when STRICTLY longer, so the linear run's center (index 3) is returned.
            Assert.Equal(3, ReaimFeasibilityScan.CenterOfLongestRunIndex(Scan("X..XX..X"), cyclic: true));
        }
    }
}
