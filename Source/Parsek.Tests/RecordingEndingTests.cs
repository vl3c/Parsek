using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// How a recording's ending is chosen and kept: the final verdicts a later lifecycle event
    /// may not overwrite (todo DOCKED-ENDING-OVERWRITTEN-BY-RECOVERY).
    /// </summary>
    [Collection("Sequential")]
    public class RecordingEndingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordingEndingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        [Theory]
        [InlineData(TerminalState.Docked)]
        [InlineData(TerminalState.Boarded)]
        public void CanOverwriteTerminalState_MergedEndingIsFinal(TerminalState existing)
        {
            Assert.False(ParsekScenario.CanOverwriteTerminalState(existing, TerminalState.Recovered));
            Assert.False(ParsekScenario.CanOverwriteTerminalState(existing, TerminalState.Destroyed));
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains($"CanOverwriteTerminalState: refused existing={existing} incoming=Recovered"));
        }

        [Theory]
        [InlineData(TerminalState.Orbiting)]
        [InlineData(TerminalState.Landed)]
        [InlineData(TerminalState.Splashed)]
        [InlineData(TerminalState.SubOrbital)]
        public void CanOverwriteTerminalState_SituationEndingStillYields(TerminalState existing)
        {
            Assert.True(ParsekScenario.CanOverwriteTerminalState(existing, TerminalState.Recovered));
            Assert.True(ParsekScenario.CanOverwriteTerminalState(existing, TerminalState.Destroyed));
        }
    }
}
