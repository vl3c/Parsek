using System.Collections.Generic;
using Parsek;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P6.1 log-assertion coverage for the per-command diagnostic lines. Every dispatch /
    /// execution branch routes its log through <see cref="TestCommandDiagnostics"/>; these
    /// tests pin the grep-able shapes (id + reason present) for the load-bearing lines
    /// (receipt, dispatch branches, verdict, timeout, duplicate, reject) so a decision
    /// branch is never silent and the shapes cannot drift under refactor. Uses the
    /// RewindLoggingTests TestSink pattern.
    /// </summary>
    [Collection("Sequential")]
    public class TestCommandDiagnosticsTests
    {
        private readonly List<string> lines = new List<string>();

        public TestCommandDiagnosticsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true; // let Verbose lines through the sink
            ParsekLog.TestSinkForTesting = l => lines.Add(l);
        }

        private void AssertLine(string level, string mustContain)
            => Assert.Contains(lines, l => l.Contains(level) && l.Contains("[TestCommands]") && l.Contains(mustContain));

        [Fact]
        public void Receipt_LogsIdVerbArgs()
        {
            TestCommandDiagnostics.Receipt("0001", "SetSetting", 2);
            AssertLine("[INFO]", "recv id=0001 cmd=SetSetting args=2");
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void DispatchBranches_EachEmitIdAndReason()
        {
            TestCommandDiagnostics.DispatchExecute("a1");
            TestCommandDiagnostics.DispatchDefer("a2", "not-in-flight");
            TestCommandDiagnostics.DispatchReject("a3", "InvokeRewind", "not-implemented-v1");
            TestCommandDiagnostics.DispatchInterrupted("a4", JournalPhase.Claimed);

            AssertLine("[INFO]", "dispatch id=a1 -> EXECUTE");
            AssertLine("[INFO]", "dispatch id=a2 -> DEFER reason=not-in-flight");
            AssertLine("[WARN]", "reject id=a3 cmd=InvokeRewind reason=not-implemented-v1");
            AssertLine("[INFO]", "dispatch id=a4 -> INTERRUPTED (journal=Claimed)");
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void ExecVerdict_AndResponseAppended_Logged()
        {
            TestCommandDiagnostics.ExecVerdict("b1", "OK");
            TestCommandDiagnostics.ResponseAppended("b1", "OK");
            AssertLine("[INFO]", "exec id=b1 verdict=OK");
            AssertLine("[VERBOSE]", "response appended id=b1 verdict=OK");
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void Timeout_LogsDeferredSeconds_InvariantCulture()
        {
            TestCommandDiagnostics.Timeout("c1", "StartRecording", 65.5, "not-in-flight");
            AssertLine("[WARN]", "timeout id=c1 cmd=StartRecording deferred=65.5s reason=not-in-flight");
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void Duplicate_LogsIgnoredWarn()
        {
            TestCommandDiagnostics.Duplicate("d1");
            AssertLine("[WARN]", "duplicate id=d1 ignored");
            ParsekLog.ResetTestOverrides();
        }
    }
}
