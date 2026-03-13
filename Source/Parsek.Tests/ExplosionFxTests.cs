using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for explosion FX trigger logic and RecordingBuilder terminal state support.
    /// Validates guard conditions, logging, and serialization roundtrip.
    /// </summary>
    [Collection("Sequential")]
    public class ExplosionFxTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ExplosionFxTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // --- ShouldTriggerExplosion guard condition tests ---

        [Fact]
        public void ShouldTriggerExplosion_Destroyed_GhostAlive_ReturnsTrueAndLogs()
        {
            bool result = ParsekFlight.ShouldTriggerExplosion(
                explosionAlreadyFired: false,
                terminalState: TerminalState.Destroyed,
                ghostExists: true,
                vesselName: "TestVessel",
                recIdx: 3);

            Assert.True(result);
            Assert.Contains(logLines, l =>
                l.Contains("[ExplosionFx]") && l.Contains("will fire") && l.Contains("#3") && l.Contains("TestVessel"));
        }

        [Fact]
        public void ShouldTriggerExplosion_AlreadyFired_ReturnsFalseAndLogs()
        {
            bool result = ParsekFlight.ShouldTriggerExplosion(
                explosionAlreadyFired: true,
                terminalState: TerminalState.Destroyed,
                ghostExists: true,
                vesselName: "TestVessel",
                recIdx: 5);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[ExplosionFx]") && l.Contains("already fired") && l.Contains("#5"));
        }

        [Fact]
        public void ShouldTriggerExplosion_NotDestroyed_Landed_ReturnsFalseAndLogs()
        {
            bool result = ParsekFlight.ShouldTriggerExplosion(
                explosionAlreadyFired: false,
                terminalState: TerminalState.Landed,
                ghostExists: true,
                vesselName: "Lander",
                recIdx: 1);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[ExplosionFx]") && l.Contains("terminalState=Landed") && l.Contains("not Destroyed"));
        }

        [Fact]
        public void ShouldTriggerExplosion_NullTerminalState_ReturnsFalseAndLogs()
        {
            bool result = ParsekFlight.ShouldTriggerExplosion(
                explosionAlreadyFired: false,
                terminalState: null,
                ghostExists: true,
                vesselName: "Legacy",
                recIdx: 0);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[ExplosionFx]") && l.Contains("terminalState=null") && l.Contains("not Destroyed"));
        }

        [Fact]
        public void ShouldTriggerExplosion_GhostNull_ReturnsFalseAndLogs()
        {
            bool result = ParsekFlight.ShouldTriggerExplosion(
                explosionAlreadyFired: false,
                terminalState: TerminalState.Destroyed,
                ghostExists: false,
                vesselName: "NoGhost",
                recIdx: 7);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[ExplosionFx]") && l.Contains("ghost GO is null") && l.Contains("#7"));
        }

        [Theory]
        [InlineData(TerminalState.Orbiting)]
        [InlineData(TerminalState.Splashed)]
        [InlineData(TerminalState.SubOrbital)]
        [InlineData(TerminalState.Recovered)]
        [InlineData(TerminalState.Docked)]
        [InlineData(TerminalState.Boarded)]
        public void ShouldTriggerExplosion_NonDestroyedStates_AllReturnFalse(TerminalState state)
        {
            bool result = ParsekFlight.ShouldTriggerExplosion(
                explosionAlreadyFired: false,
                terminalState: state,
                ghostExists: true,
                vesselName: "Vessel",
                recIdx: 0);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[ExplosionFx]") && l.Contains("not Destroyed"));
        }

        // --- Guard evaluation order: already-fired checked before terminal state ---

        [Fact]
        public void ShouldTriggerExplosion_AlreadyFired_SkipsBeforeCheckingTerminalState()
        {
            // Even with Destroyed state, already-fired should be checked first
            ParsekFlight.ShouldTriggerExplosion(
                explosionAlreadyFired: true,
                terminalState: TerminalState.Destroyed,
                ghostExists: true,
                vesselName: "V",
                recIdx: 0);

            Assert.Contains(logLines, l => l.Contains("already fired"));
            Assert.DoesNotContain(logLines, l => l.Contains("will fire"));
        }

        // --- RecordingBuilder.WithTerminalState serialization ---

        [Fact]
        public void WithTerminalState_Build_EmitsTerminalStateValue()
        {
            var builder = new RecordingBuilder("Crasher");
            builder.WithTerminalState((int)TerminalState.Destroyed);
            builder.AddPoint(100, -0.09, -74.5, 70);

            var node = builder.Build();

            string val = node.GetValue("terminalState");
            Assert.NotNull(val);
            Assert.Equal("4", val);
        }

        [Fact]
        public void WithTerminalState_BuildV3Metadata_EmitsTerminalStateValue()
        {
            var builder = new RecordingBuilder("Crasher");
            builder.WithTerminalState((int)TerminalState.Destroyed);
            builder.AddPoint(100, -0.09, -74.5, 70);

            var node = builder.BuildV3Metadata();

            string val = node.GetValue("terminalState");
            Assert.NotNull(val);
            Assert.Equal("4", val);
        }

        [Fact]
        public void WithoutTerminalState_Build_OmitsTerminalStateKey()
        {
            var builder = new RecordingBuilder("Normal");
            builder.AddPoint(100, -0.09, -74.5, 70);

            var node = builder.Build();

            Assert.Null(node.GetValue("terminalState"));
        }

        [Fact]
        public void WithoutTerminalState_BuildV3Metadata_OmitsTerminalStateKey()
        {
            var builder = new RecordingBuilder("Normal");
            builder.AddPoint(100, -0.09, -74.5, 70);

            var node = builder.BuildV3Metadata();

            Assert.Null(node.GetValue("terminalState"));
        }

        [Theory]
        [InlineData(0, "0")]   // Orbiting
        [InlineData(1, "1")]   // Landed
        [InlineData(4, "4")]   // Destroyed
        [InlineData(5, "5")]   // Recovered
        public void WithTerminalState_AllValues_RoundtripCorrectly(int stateInt, string expected)
        {
            var builder = new RecordingBuilder("V");
            builder.WithTerminalState(stateInt);
            builder.AddPoint(100, 0, 0, 70);

            var buildNode = builder.Build();
            Assert.Equal(expected, buildNode.GetValue("terminalState"));

            var v3Node = builder.BuildV3Metadata();
            Assert.Equal(expected, v3Node.GetValue("terminalState"));
        }

        // --- KscPadDestroyed synthetic recording has terminal state ---

        [Fact]
        public void KscPadDestroyed_HasDestroyedTerminalState()
        {
            var node = SyntheticRecordingTests.KscPadDestroyed().Build();

            string val = node.GetValue("terminalState");
            Assert.NotNull(val);
            Assert.Equal("4", val); // TerminalState.Destroyed
        }

        [Fact]
        public void KscPadDestroyed_V3Metadata_HasDestroyedTerminalState()
        {
            var node = SyntheticRecordingTests.KscPadDestroyed().BuildV3Metadata();

            string val = node.GetValue("terminalState");
            Assert.NotNull(val);
            Assert.Equal("4", val);
        }
    }
}
