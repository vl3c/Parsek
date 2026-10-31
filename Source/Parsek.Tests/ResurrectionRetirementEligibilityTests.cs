using System;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Coverage for the two shared predicates of <see cref="ResurrectionRetirementEligibility"/>.
    ///
    /// <para>
    /// The identity gate matters most. KSP bakes <c>persistentId</c> into the .craft and reuses
    /// it on every launch, so <see cref="ResurrectionRetirementEligibility.IsPositivelySameLaunch"/>
    /// requires a POSITIVE launch-guid match and refuses to match on pid alone, deliberately
    /// stricter than <see cref="VesselLaunchIdentity.LiveVesselIsRecordedLaunch"/>, whose
    /// unknown-guid pid-only fallback is right for visibility decisions.
    /// </para>
    /// </summary>
    public class ResurrectionRetirementEligibilityTests
    {
        private const string GuidA = "11111111111111111111111111111111";
        private const string GuidB = "22222222222222222222222222222222";

        private static Recording LaunchRecording(string id, uint pid, string launchGuid)
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = pid,
                RecordedVesselGuid = launchGuid,
                TerminalStateValue = TerminalState.Recovered,
            };
        }

        [Fact]
        public void IsPositivelySameLaunch_RejectsEveryDegenerateInput()
        {
            var rec = LaunchRecording("rec-1", 42u, GuidA);

            Assert.False(ResurrectionRetirementEligibility.IsPositivelySameLaunch(null, 42u, GuidA));
            Assert.False(ResurrectionRetirementEligibility.IsPositivelySameLaunch(rec, 0u, GuidA));
            Assert.False(ResurrectionRetirementEligibility.IsPositivelySameLaunch(rec, 43u, GuidA));
            Assert.True(ResurrectionRetirementEligibility.IsPositivelySameLaunch(rec, 42u, GuidA));

            // Guid normalization: dashed vs undashed must compare equal.
            string dashed = Guid.Parse(GuidA).ToString("D");
            Assert.True(ResurrectionRetirementEligibility.IsPositivelySameLaunch(rec, 42u, dashed));
        }

        [Fact]
        public void IsPositivelySameLaunch_GuidMismatchOrUnknownGuid_IsNotTheSameLaunch()
        {
            // A fresh launch of the same craft carries the baked pid and a new guid.
            Assert.False(ResurrectionRetirementEligibility.IsPositivelySameLaunch(
                LaunchRecording("rec-1", 42u, GuidA), 42u, GuidB));
            // Unknown on either side never degrades to pid-only here.
            Assert.False(ResurrectionRetirementEligibility.IsPositivelySameLaunch(
                LaunchRecording("rec-1", 42u, null), 42u, GuidA));
            Assert.False(ResurrectionRetirementEligibility.IsPositivelySameLaunch(
                LaunchRecording("rec-1", 42u, GuidA), 42u, null));
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData(TerminalState.Orbiting, true)]
        [InlineData(TerminalState.Landed, true)]
        [InlineData(TerminalState.Splashed, true)]
        [InlineData(TerminalState.SubOrbital, true)]
        [InlineData(TerminalState.Recovered, false)]
        [InlineData(TerminalState.Destroyed, false)]
        [InlineData(TerminalState.Docked, false)]
        [InlineData(TerminalState.Boarded, false)]
        [InlineData(TerminalState.Disassembled, false)]
        public void VesselOutlivedTerminal_TruthTable(TerminalState? terminal, bool expected)
        {
            Assert.Equal(expected, ResurrectionRetirementEligibility.VesselOutlivedTerminal(terminal));
        }
    }
}
