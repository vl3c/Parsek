using Xunit;

namespace Parsek.Tests
{
    public class TerrainCorrectorTailLiftTests
    {
        [Fact]
        public void BuildTailLiftPlan_ReturnsInactive_OnNullTerminal()
        {
            TerrainCorrector.TailLiftPlan plan = TerrainCorrector.BuildTailLiftPlan(
                null, 100.0, 112.0, 200.0, 30.0, 2.0);

            Assert.False(plan.Active);
        }

        [Fact]
        public void BuildTailLiftPlan_ReturnsInactive_OnNonSurfaceTerminal()
        {
            TerrainCorrector.TailLiftPlan plan = TerrainCorrector.BuildTailLiftPlan(
                TerminalState.Destroyed, 100.0, 112.0, 200.0, 30.0, 2.0);

            Assert.False(plan.Active);
        }

        [Fact]
        public void BuildTailLiftPlan_ReturnsInactive_OnNaNTerrain()
        {
            Assert.False(TerrainCorrector.BuildTailLiftPlan(
                TerminalState.Landed, double.NaN, 112.0, 200.0, 30.0, 2.0).Active);
            Assert.False(TerrainCorrector.BuildTailLiftPlan(
                TerminalState.Landed, 100.0, double.NaN, 200.0, 30.0, 2.0).Active);
        }

        [Fact]
        public void BuildTailLiftPlan_ReturnsInactive_OnNonPositiveRampSeconds()
        {
            Assert.False(TerrainCorrector.BuildTailLiftPlan(
                TerminalState.Landed, 100.0, 112.0, 200.0, 0.0, 2.0).Active);
            Assert.False(TerrainCorrector.BuildTailLiftPlan(
                TerminalState.Landed, 100.0, 112.0, 200.0, -1.0, 2.0).Active);
        }

        [Fact]
        public void BuildTailLiftPlan_ReturnsInactive_OnSmallDelta()
        {
            TerrainCorrector.TailLiftPlan plan = TerrainCorrector.BuildTailLiftPlan(
                TerminalState.Landed, 100.0, 101.0, 200.0, 30.0, 2.0);

            Assert.False(plan.Active);
        }

        [Theory]
        [InlineData(TerminalState.Landed)]
        [InlineData(TerminalState.Splashed)]
        [InlineData(TerminalState.Recovered)]
        public void BuildTailLiftPlan_ReturnsActive_WithCorrectRampEndpoints(
            TerminalState terminalState)
        {
            TerrainCorrector.TailLiftPlan plan = TerrainCorrector.BuildTailLiftPlan(
                terminalState, 100.0, 112.0, 200.0, 30.0, 2.0);

            Assert.True(plan.Active);
            Assert.Equal(200.0, plan.TerminalUT);
            Assert.Equal(170.0, plan.RampStartUT);
            Assert.Equal(12.0, plan.TerrainDelta);
        }

        [Fact]
        public void EvaluateTailLift_BeforeRamp_ReturnsZero()
        {
            var plan = new TerrainCorrector.TailLiftPlan(
                terminalUT: 100.0, rampStartUT: 70.0, terrainDelta: 12.0);

            Assert.Equal(0.0, TerrainCorrector.EvaluateTailLift(69.9, in plan));
            Assert.Equal(0.0, TerrainCorrector.EvaluateTailLift(70.0, in plan));
        }

        [Fact]
        public void EvaluateTailLift_AfterTerminal_ReturnsDelta()
        {
            var plan = new TerrainCorrector.TailLiftPlan(
                terminalUT: 100.0, rampStartUT: 70.0, terrainDelta: 12.0);

            Assert.Equal(12.0, TerrainCorrector.EvaluateTailLift(100.0, in plan));
            Assert.Equal(12.0, TerrainCorrector.EvaluateTailLift(101.0, in plan));
        }

        [Fact]
        public void EvaluateTailLift_AtMidpoint_ReturnsHalfDelta()
        {
            var plan = new TerrainCorrector.TailLiftPlan(
                terminalUT: 100.0, rampStartUT: 70.0, terrainDelta: 12.0);

            Assert.Equal(6.0, TerrainCorrector.EvaluateTailLift(85.0, in plan));
        }

        [Fact]
        public void EvaluateTailLift_InactivePlan_ReturnsZero()
        {
            TerrainCorrector.TailLiftPlan plan = TerrainCorrector.TailLiftPlan.Inactive;

            Assert.Equal(0.0, TerrainCorrector.EvaluateTailLift(100.0, in plan));
        }

        [Fact]
        public void EvaluateTailLift_ZeroSpanRamp_ReturnsDeltaAtOrAfterTerminal()
        {
            var plan = new TerrainCorrector.TailLiftPlan(
                terminalUT: 100.0, rampStartUT: 100.0, terrainDelta: 12.0);

            Assert.Equal(0.0, TerrainCorrector.EvaluateTailLift(99.9, in plan));
            Assert.Equal(12.0, TerrainCorrector.EvaluateTailLift(100.0, in plan));
            Assert.Equal(12.0, TerrainCorrector.EvaluateTailLift(101.0, in plan));
        }
    }
}
