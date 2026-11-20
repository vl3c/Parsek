using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// How a recording's ending is chosen and kept: the final verdicts a later lifecycle event
    /// may not overwrite (todo DOCKED-ENDING-OVERWRITTEN-BY-RECOVERY) and an ending a newer
    /// build wrote (todo NEWER-ENDING-ERASED-BY-OLDER-BUILD), and where an ending is read from
    /// (todo ENDPOINT-READS-RELATIVE-OFFSET-AS-LATLON).
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

        // ---- the endpoint resolver's last point (todo ENDPOINT-READS-RELATIVE-OFFSET-AS-LATLON) ----

        private static Recording RelativeTailRecording(bool withShadow)
        {
            var offset = new TrajectoryPoint
            {
                ut = 200.0, latitude = 812.5, longitude = -1300.0, altitude = 45.0, bodyName = "Kerbin"
            };
            var shadow = new TrajectoryPoint
            {
                ut = 200.0, latitude = -0.0972, longitude = -74.557, altitude = 70.0, bodyName = "Kerbin"
            };
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 190.0,
                endUT = 200.0,
                frames = new List<TrajectoryPoint> { offset },
                bodyFixedFrames = withShadow ? new List<TrajectoryPoint> { shadow } : null
            };
            var rec = new Recording
            {
                RecordingId = withShadow ? "eva-shadow" : "eva-no-shadow",
                EndpointPhase = RecordingEndpointPhase.TrajectoryPoint,
                EndpointBodyName = "Kerbin"
            };
            rec.TrackSections.Add(section);
            rec.Points.Add(offset);
            return rec;
        }

        [Fact]
        public void EndpointCoordinates_RelativeLastPointWithShadow_ReadsTheShadow()
        {
            Recording rec = RelativeTailRecording(withShadow: true);

            Assert.True(RecordingEndpointResolver.TryGetRecordingEndpointCoordinates(
                rec, out string body, out double lat, out double lon, out double alt));
            Assert.Equal("Kerbin", body);
            Assert.Equal(-0.0972, lat);
            Assert.Equal(-74.557, lon);
            Assert.Equal(70.0, alt);
        }

        [Fact]
        public void EndpointCoordinates_RelativeLastPointWithoutShadow_Refuses()
        {
            Recording rec = RelativeTailRecording(withShadow: false);

            Assert.False(RecordingEndpointResolver.TryGetRecordingEndpointCoordinates(
                rec, out _, out _, out _, out _));
            Assert.Contains(logLines, l => l.Contains("[EndpointDecision]")
                && l.Contains("refused last point of 'eva-no-shadow'"));
        }

        [Fact]
        public void EndpointCoordinates_AbsoluteLastPoint_Unchanged()
        {
            var rec = new Recording
            {
                RecordingId = "abs",
                EndpointPhase = RecordingEndpointPhase.TrajectoryPoint,
                EndpointBodyName = "Kerbin"
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 10.0, latitude = 1.5, longitude = 2.5, altitude = 3.5, bodyName = "Kerbin"
            });

            Assert.True(RecordingEndpointResolver.TryGetRecordingEndpointCoordinates(
                rec, out _, out double lat, out double lon, out double alt));
            Assert.Equal(1.5, lat);
            Assert.Equal(2.5, lon);
            Assert.Equal(3.5, alt);
        }
    }
}
