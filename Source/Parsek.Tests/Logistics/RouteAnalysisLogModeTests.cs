using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the <see cref="RouteAnalysisLogMode"/> gating: the ~1/second candidate
    /// sweep (<c>RouteCandidateFinder</c>) passes <see cref="RouteAnalysisLogMode.Quiet"/>
    /// so the engine emits no per-tree INFO (the fix for the once-per-second
    /// "RouteAnalysis rejected: missing route proof" log spam), while one-shot
    /// callers (Create Route / commit) keep the detailed
    /// <see cref="RouteAnalysisLogMode.Diagnostic"/> INFO. Uses the canonical
    /// log-capture pattern; Sequential because it touches the global log sink.
    /// </summary>
    [Collection("Sequential")]
    public class RouteAnalysisLogModeTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteAnalysisLogModeTests()
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

        // A recording with no RouteConnectionWindows resolves to MissingRouteProof,
        // the rejection that spammed the log on every candidate poll.
        private static Recording NoProofRecording() => new Recording { RecordingId = "no-proof" };

        [Fact]
        public void Diagnostic_LogsRejectionAtInfo()
        {
            RouteAnalysisResult r = RouteAnalysisEngine.AnalyzeRecording(
                NoProofRecording(), RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(RouteAnalysisStatus.MissingRouteProof, r.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("RouteAnalysis rejected: missing route proof"));
        }

        [Fact]
        public void Quiet_SuppressesRejectionLog_SameDecision()
        {
            RouteAnalysisResult r = RouteAnalysisEngine.AnalyzeRecording(
                NoProofRecording(), RouteAnalysisLogMode.Quiet);

            // Same analysis outcome, but no per-call spam line.
            Assert.Equal(RouteAnalysisStatus.MissingRouteProof, r.Status);
            Assert.DoesNotContain(logLines, l => l.Contains("RouteAnalysis rejected"));
        }

        [Fact]
        public void DefaultMode_IsDiagnostic()
        {
            RouteAnalysisEngine.AnalyzeRecording(NoProofRecording());

            Assert.Contains(logLines, l =>
                l.Contains("RouteAnalysis rejected: missing route proof"));
        }
    }
}
