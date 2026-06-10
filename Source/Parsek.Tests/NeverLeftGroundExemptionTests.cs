using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers <see cref="LogContractTests.IsNeverLeftGroundSinglePointRecording"/>,
    /// the REC-002 exemption for single-point recordings of vessels that never left
    /// the surface: clamp-held pad craft (terminal state Landed) and stationary EVA
    /// kerbals sealed without a terminal-state stamp but with a landed/splashed
    /// TerminalPosition (the 2026-06-09 career playtest 'Lars Kerman' recording).
    /// </summary>
    public class NeverLeftGroundExemptionTests
    {
        private static Recording SinglePointRecording()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            return rec;
        }

        [Fact]
        public void NullRecording_NotExempt()
        {
            Assert.False(LogContractTests.IsNeverLeftGroundSinglePointRecording(null));
        }

        [Fact]
        public void SinglePoint_TerminalLanded_Exempt()
        {
            var rec = SinglePointRecording();
            rec.TerminalStateValue = TerminalState.Landed;
            Assert.True(LogContractTests.IsNeverLeftGroundSinglePointRecording(rec));
        }

        [Fact]
        public void SinglePoint_NoTerminalState_GroundedTerminalPosition_Exempt()
        {
            // The EVA-kerbal shape: no terminal-state stamp, but a recorded
            // surface TerminalPosition (only ever set for landed/splashed ends).
            var rec = SinglePointRecording();
            rec.TerminalStateValue = null;
            rec.TerminalPosition = new SurfacePosition
            {
                body = "Kerbin",
                situation = SurfaceSituation.Splashed,
            };
            Assert.True(LogContractTests.IsNeverLeftGroundSinglePointRecording(rec));
        }

        [Fact]
        public void SinglePoint_NoGroundedEvidence_NotExempt()
        {
            var rec = SinglePointRecording();
            rec.TerminalStateValue = null;
            rec.TerminalPosition = null;
            Assert.False(LogContractTests.IsNeverLeftGroundSinglePointRecording(rec));
        }

        [Fact]
        public void SinglePoint_NonLandedTerminalState_NotExempt_EvenWithTerminalPosition()
        {
            // An explicit non-Landed terminal verdict wins over the position
            // node: a 1-point Destroyed recording is still a contract violation.
            var rec = SinglePointRecording();
            rec.TerminalStateValue = TerminalState.Destroyed;
            rec.TerminalPosition = new SurfacePosition
            {
                body = "Kerbin",
                situation = SurfaceSituation.Landed,
            };
            Assert.False(LogContractTests.IsNeverLeftGroundSinglePointRecording(rec));
        }

        [Fact]
        public void TwoPoints_NotExempt()
        {
            var rec = SinglePointRecording();
            rec.Points.Add(new TrajectoryPoint { ut = 101.0 });
            rec.TerminalStateValue = TerminalState.Landed;
            Assert.False(LogContractTests.IsNeverLeftGroundSinglePointRecording(rec));
        }

        [Fact]
        public void SinglePoint_WithOrbitSegment_NotExempt()
        {
            var rec = SinglePointRecording();
            rec.TerminalStateValue = TerminalState.Landed;
            rec.OrbitSegments.Add(new OrbitSegment());
            Assert.False(LogContractTests.IsNeverLeftGroundSinglePointRecording(rec));
        }
    }
}
