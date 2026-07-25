using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the commit-path terminal verdict log emitted by
    /// <c>ParsekFlight.CommitTreeFlight</c> via
    /// <c>ParsekFlight.LogCommitTerminalVerdicts</c>.
    ///
    /// WHY THIS EXISTS: "CommitTreeFlight: starting tree commit at UT=" is logged
    /// unconditionally the moment the path is entered, and the test-command seam's
    /// "committree committed=true" only proves the call returned without throwing.
    /// Neither observes the foreign-SOI-sensitive outputs, so a tree that ended
    /// around the Mun and one that ended around Kerbin used to produce identical
    /// evidence. These lines name the terminal classification and the terminal
    /// orbit body per recording, which is what the autotest ORBIT lane (B11/B12)
    /// asserts as a log contract.
    ///
    /// <c>CommitTreeFlight</c> itself needs a live flight scene, so the message
    /// construction lives in pure <c>internal static</c> formatters (the established
    /// repo pattern) and those are what these tests drive; the live site calls the
    /// same formatters through <c>LogCommitTerminalVerdicts</c>, which is also
    /// exercised here through the log sink.
    /// </summary>
    [Collection("Sequential")]
    public class CommitTerminalVerdictLoggingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CommitTerminalVerdictLoggingTests()
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

        private static Recording Rec(
            string id,
            TerminalState? terminal = null,
            string terminalBody = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Test " + id,
                TerminalStateValue = terminal,
                TerminalOrbitBody = terminalBody,
            };
        }

        private static RecordingTree TreeWith(params Recording[] recordings)
        {
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "Kerbal X",
            };
            foreach (var rec in recordings)
                tree.Recordings[rec.RecordingId] = rec;
            if (recordings.Length > 0)
            {
                tree.RootRecordingId = recordings[0].RecordingId;
                tree.ActiveRecordingId = recordings[0].RecordingId;
            }
            return tree;
        }

        // --- Formatter ---------------------------------------------------------

        [Fact]
        public void FormatCommitTerminalLine_NamesStateAndBody()
        {
            string line = ParsekFlight.FormatCommitTerminalLine(
                Rec("abc123", TerminalState.Orbiting, "Mun"));

            Assert.Equal(
                "CommitTreeFlight terminal: rec=abc123 terminalState=Orbiting terminalOrbitBody=Mun",
                line);
        }

        [Fact]
        public void FormatCommitTerminalLine_ForeignBodyIsDistinguishableFromHome()
        {
            string mun = ParsekFlight.FormatCommitTerminalLine(
                Rec("r1", TerminalState.Orbiting, "Mun"));
            string kerbin = ParsekFlight.FormatCommitTerminalLine(
                Rec("r1", TerminalState.Orbiting, "Kerbin"));

            // The regression this guards: a commit in a FOREIGN SOI must not read
            // identically to one at the home body.
            Assert.NotEqual(mun, kerbin);
            Assert.Contains("terminalOrbitBody=Mun", mun);
            Assert.Contains("terminalOrbitBody=Kerbin", kerbin);
        }

        [Fact]
        public void FormatCommitTerminalLine_SubOrbitalIsDistinguishableFromOrbiting()
        {
            string orbiting = ParsekFlight.FormatCommitTerminalLine(
                Rec("r1", TerminalState.Orbiting, "Minmus"));
            string subOrbital = ParsekFlight.FormatCommitTerminalLine(
                Rec("r1", TerminalState.SubOrbital, "Minmus"));

            Assert.NotEqual(orbiting, subOrbital);
            Assert.Contains("terminalState=Orbiting", orbiting);
            Assert.Contains("terminalState=SubOrbital", subOrbital);
        }

        [Fact]
        public void FormatCommitTerminalLine_NullTerminalFieldsRenderAsNullToken()
        {
            string line = ParsekFlight.FormatCommitTerminalLine(Rec("r1"));

            Assert.Equal(
                "CommitTreeFlight terminal: rec=r1 terminalState=(null) terminalOrbitBody=(null)",
                line);
        }

        [Fact]
        public void FormatCommitTerminalLine_NullRecordingDoesNotThrow()
        {
            string line = ParsekFlight.FormatCommitTerminalLine(null);

            Assert.Equal(
                "CommitTreeFlight terminal: rec=(null) terminalState=(null) terminalOrbitBody=(null)",
                line);
        }

        [Fact]
        public void FormatCommitTerminalSummaryLine_NamesRootRecording()
        {
            var root = Rec("root-1", TerminalState.Orbiting, "Mun");
            var tree = TreeWith(root, Rec("debris-1", TerminalState.Destroyed));

            string line = ParsekFlight.FormatCommitTerminalSummaryLine(tree);

            Assert.Contains("2 recordings", line);
            Assert.Contains("root rec=root-1", line);
            Assert.Contains("terminalState=Orbiting", line);
            Assert.Contains("terminalOrbitBody=Mun", line);
        }

        // --- Live-site batching ------------------------------------------------

        [Fact]
        public void LogCommitTerminalVerdicts_BoundedTree_LogsOneLinePerRecording()
        {
            var tree = TreeWith(
                Rec("root-1", TerminalState.Orbiting, "Mun"),
                Rec("debris-1", TerminalState.SubOrbital, "Kerbin"),
                Rec("debris-2", TerminalState.Destroyed));

            ParsekFlight.LogCommitTerminalVerdicts(tree);

            Assert.Equal(3, logLines.Count(l => l.Contains("CommitTreeFlight terminal: rec=")));
            Assert.Contains(logLines, l =>
                l.Contains("[Flight]")
                && l.Contains("CommitTreeFlight terminal: rec=root-1")
                && l.Contains("terminalState=Orbiting")
                && l.Contains("terminalOrbitBody=Mun"));
            Assert.Contains(logLines, l =>
                l.Contains("CommitTreeFlight terminal: rec=debris-2")
                && l.Contains("terminalState=Destroyed")
                && l.Contains("terminalOrbitBody=(null)"));
            Assert.DoesNotContain(logLines, l => l.Contains("CommitTreeFlight terminal summary"));
        }

        [Fact]
        public void LogCommitTerminalVerdicts_OverCap_LogsSingleSummaryNamingRoot()
        {
            var recordings = new List<Recording>
            {
                Rec("root-1", TerminalState.Orbiting, "Minmus"),
            };
            for (int i = 0; i < ParsekFlight.CommitTerminalLogLimit; i++)
                recordings.Add(Rec("debris-" + i, TerminalState.Destroyed));

            var tree = TreeWith(recordings.ToArray());
            Assert.True(tree.Recordings.Count > ParsekFlight.CommitTerminalLogLimit);

            ParsekFlight.LogCommitTerminalVerdicts(tree);

            Assert.Empty(logLines.Where(l => l.Contains("CommitTreeFlight terminal: rec=")));
            Assert.Single(logLines.Where(l => l.Contains("CommitTreeFlight terminal summary")));
            Assert.Contains(logLines, l =>
                l.Contains("[Flight]")
                && l.Contains("CommitTreeFlight terminal summary")
                && l.Contains("root rec=root-1")
                && l.Contains("terminalOrbitBody=Minmus"));
        }

        [Fact]
        public void LogCommitTerminalVerdicts_EmptyOrNullTree_LogsWithoutThrowing()
        {
            ParsekFlight.LogCommitTerminalVerdicts(new RecordingTree { TreeName = "Empty" });
            ParsekFlight.LogCommitTerminalVerdicts(null);

            Assert.Equal(
                2,
                logLines.Count(l => l.Contains("CommitTreeFlight terminal: no recordings in tree")));
            Assert.Contains(logLines, l => l.Contains("'Empty'"));
            Assert.Contains(logLines, l => l.Contains("'(null)'"));
        }
    }
}
