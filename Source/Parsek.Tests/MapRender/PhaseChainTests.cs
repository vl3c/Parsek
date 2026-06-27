using System.Collections.Generic;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-1 guard for <see cref="PhaseChain"/> (design §6), the <see cref="GhostRenderChain"/>
    /// successor. Mirrors <c>GhostRenderChainTests</c>: builds a chain with rigid-adjacent phases, an
    /// interior gap (a FlexibleSoi seam UT), and a final phase, then sweeps UTs through locate +
    /// the three-valued coverage. Also covers the HoldPhase whole-span coverage inside a chain.
    ///
    /// Each assertion states the bug it catches: a wrong locate mis-routes a frame to the wrong phase
    /// (wrong treatment/position); a wrong coverage tri-state would double-draw, blink a ghost off in a
    /// gap (should hold), or fail to retire past the window end.
    /// </summary>
    public class PhaseChainTests
    {
        private static readonly AnchorFrame Kerbin = new AnchorFrame.BodyAnchor("Kerbin");

        private static OrbitSegment Conic(double s, double e)
            => new OrbitSegment { startUT = s, endUT = e, bodyName = "Kerbin" };

        private static PhaseId Id(int ordinal) => new PhaseId("rec-A", 0, ordinal);

        // phase0 [0,10) ascent, phase1 [10,20) loiter (rigid-adjacent), gap [20,30), phase2 [30,40] arrival.
        // Window [0,40].
        private static PhaseChain BuildChain()
        {
            var phases = new List<TrajectoryPhase>
            {
                new AscentPhase(Id(0), SegmentProvenance.Recorded, Kerbin, 0, 10),
                new DepartureLoiterPhase(Id(1), SegmentProvenance.Recorded, Kerbin, 10, 20, Conic(10, 20)),
                new ArrivalLoiterPhase(Id(2), SegmentProvenance.Recorded, Kerbin, 30, 40, Conic(30, 40)),
            };
            return new PhaseChain("rec-A", committedIndex: 3, instanceKey: 0,
                phases: phases, windowStartUt: 0, windowEndUt: 40);
        }

        [Theory]
        // ut, expected coverage, expected phase index (-1 when not InSegment)
        [InlineData(-1.0, (int)Coverage.OutsideWindow, -1)]
        [InlineData(0.0, (int)Coverage.InSegment, 0)]          // window/phase start inclusive
        [InlineData(5.0, (int)Coverage.InSegment, 0)]
        [InlineData(10.0, (int)Coverage.InSegment, 1)]         // shared rigid boundary -> later phase
        [InlineData(19.9, (int)Coverage.InSegment, 1)]
        [InlineData(20.0, (int)Coverage.InInteriorGap, -1)]    // exclusive end of a non-last phase + gap
        [InlineData(25.0, (int)Coverage.InInteriorGap, -1)]    // mid-gap -> hold, never retire/blink
        [InlineData(30.0, (int)Coverage.InSegment, 2)]
        [InlineData(40.0, (int)Coverage.InSegment, 2)]         // last phase end inclusive
        [InlineData(40.001, (int)Coverage.OutsideWindow, -1)]  // past end -> retire
        [InlineData(100.0, (int)Coverage.OutsideWindow, -1)]
        public void ClassifyCoverage_ResolvesPhasesGapsAndWindow(double ut, int expectedCoverage, int expectedIndex)
        {
            Coverage expected = (Coverage)expectedCoverage;
            var chain = BuildChain();
            var cov = chain.ClassifyCoverage(ut, out TrajectoryPhase phase, out int index);
            Assert.Equal(expected, cov);
            Assert.Equal(expectedIndex, index);
            if (expected == Coverage.InSegment)
            {
                Assert.NotNull(phase);
                Assert.Equal(chain.Phases[expectedIndex].StartUt, phase.StartUt);
            }
        }

        [Fact]
        public void LocatePhaseIndex_MatchesClassify_ForInPhaseUTs()
        {
            var chain = BuildChain();
            Assert.Equal(0, chain.LocatePhaseIndex(5.0));
            Assert.Equal(1, chain.LocatePhaseIndex(10.0));
            Assert.Equal(2, chain.LocatePhaseIndex(40.0)); // last phase inclusive end
            Assert.Equal(-1, chain.LocatePhaseIndex(25.0)); // gap
            Assert.Equal(-1, chain.LocatePhaseIndex(-5.0)); // before first
        }

        [Fact]
        public void EmptyChain_IsAlwaysGapOrOutside_NeverThrows()
        {
            var chain = new PhaseChain("rec-empty", 0, 0,
                phases: new List<TrajectoryPhase>(), windowStartUt: 0, windowEndUt: 10);
            Assert.Equal(-1, chain.LocatePhaseIndex(5.0));
            Assert.Equal(Coverage.InInteriorGap, chain.ClassifyCoverage(5.0, out _, out _));
            Assert.Equal(Coverage.OutsideWindow, chain.ClassifyCoverage(50.0, out _, out _));
        }

        [Fact]
        public void NonFiniteUt_IsOutsideWindow()
        {
            var chain = BuildChain();
            Assert.Equal(Coverage.OutsideWindow, chain.ClassifyCoverage(double.NaN, out _, out _));
            Assert.Equal(-1, chain.LocatePhaseIndex(double.PositiveInfinity));
        }

        [Fact]
        public void HoldPhaseInChain_CoversItsWholeSpan_NotAGap()
        {
            // A HoldPhase sitting between two phases must NOT classify as an interior gap mid-hold - it
            // covers its whole span (warp-step safety, §11.3).
            var phases = new List<TrajectoryPhase>
            {
                new DepartureLoiterPhase(Id(0), SegmentProvenance.Recorded, Kerbin, 0, 10, Conic(0, 10)),
                new HoldPhase(Id(1), Kerbin, 10, 1000),
                new ArrivalLoiterPhase(Id(2), SegmentProvenance.Recorded, Kerbin, 1000, 1010, Conic(1000, 1010)),
            };
            var chain = new PhaseChain("rec-A", 0, 0, phases, windowStartUt: 0, windowEndUt: 1010);

            Assert.Equal(1, chain.LocatePhaseIndex(10.0));   // hold start
            Assert.Equal(1, chain.LocatePhaseIndex(500.0));  // deep inside the hold (a high-warp landing)
            Assert.Equal(1, chain.LocatePhaseIndex(999.0));
            Assert.Equal(2, chain.LocatePhaseIndex(1000.0)); // shared boundary -> the later phase

            Assert.Equal(Coverage.InSegment, chain.ClassifyCoverage(500.0, out TrajectoryPhase p, out _));
            Assert.IsType<HoldPhase>(p);
        }

        [Fact]
        public void TryGetPhase_ReturnsPhaseOrFalse()
        {
            var chain = BuildChain();
            Assert.True(chain.TryGetPhase(5.0, out TrajectoryPhase p, out int idx));
            Assert.Equal(0, idx);
            Assert.IsType<AscentPhase>(p);
            Assert.False(chain.TryGetPhase(25.0, out _, out _)); // gap
        }

        [Fact]
        public void ChainCarriesKeyingAndWindowFields()
        {
            var chain = BuildChain();
            Assert.Equal("rec-A", chain.RecordingId);
            Assert.Equal(3, chain.CommittedIndex);
            Assert.Equal(0, chain.InstanceKey);
            Assert.Equal(0, chain.WindowStartUt);
            Assert.Equal(40, chain.WindowEndUt);
            Assert.Equal(3, chain.PhaseCount);
            Assert.False(chain.IsFaithfulFallback);
        }
    }
}
