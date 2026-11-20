using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// How a recording's ending is chosen and kept: the final verdicts a later lifecycle event
    /// may not overwrite (todo DOCKED-ENDING-OVERWRITTEN-BY-RECOVERY) and an ending a newer
    /// build wrote (todo NEWER-ENDING-ERASED-BY-OLDER-BUILD).
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

        // ---- an ending a newer build wrote (todo NEWER-ENDING-ERASED-BY-OLDER-BUILD) ----

        private static Recording LoadWithTerminal(string raw)
        {
            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, new Recording
            {
                RecordingId = "rec-newer-ending",
                VesselName = "Probe",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            });
            node.SetValue("terminalState", raw, createIfNotFound: true);
            var rec = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(node, rec);
            return rec;
        }

        [Fact]
        public void Codec_UnknownTerminalState_IsHeldAndWrittenBackVerbatim()
        {
            Recording rec = LoadWithTerminal("99");

            Assert.True(rec.TerminalStateValue.HasValue);
            Assert.Equal(99, (int)rec.TerminalStateValue.Value);
            Assert.Contains(logLines, l => l.Contains("[WARN][Codec]")
                && l.Contains("terminalState=99 is unknown to this build"));

            var outNode = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(outNode, rec);
            Assert.Equal("99", outNode.GetValue("terminalState"));
        }

        [Fact]
        public void Codec_KnownTerminalState_LoadsWithoutWarning()
        {
            Recording rec = LoadWithTerminal(((int)TerminalState.Landed).ToString());

            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
            Assert.DoesNotContain(logLines, l => l.Contains("is unknown to this build"));
        }

        [Fact]
        public void TailTrim_UnknownTerminalState_RefusesTrim()
        {
            // An unclassified ending is the final playback: the tail must not be cut.
            Recording rec = LoadWithTerminal("99");
            Assert.False(RecordingOptimizer.TailPreservesTerminalSpawnState(rec, 100.0));
            Assert.False(GhostPlaybackLogic.IsSpawnableTerminal(rec.TerminalStateValue.Value));
        }

        [Fact]
        public void CanOverwriteTerminalState_UnknownEndingIsFinal()
        {
            Assert.False(ParsekScenario.CanOverwriteTerminalState((TerminalState)99, TerminalState.Recovered));
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("CanOverwriteTerminalState: refused existing=99 incoming=Recovered"));
        }
    }
}
