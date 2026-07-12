using System;
using System.Collections.Generic;
using Parsek;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    // Log-assertion tests for the M-A3 hook H2 exit tail (design "Test Plan" ->
    // "H2 emits the pre-quit line before invoking the quit"). These exercise the
    // extracted InGameTestRunner.PerformAutorunExit seam with an injected quit
    // callback, so the ordering + error-containment contracts are provable without
    // a live KSP. Touches ParsekLog + the static quit seam, so it is Sequential.
    [Collection("Sequential")]
    public class AutorunExitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public AutorunExitTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // Guards the H2 durability contract: the pre-quit Info line is written BEFORE the
        // quit callback fires, so a killed process always leaves the "quitting" record as
        // its last durable log line. Fails if the quit races ahead of the log.
        [Fact]
        public void PerformAutorunExit_PreQuitLine_PrecedesQuitCallback()
        {
            var order = new List<string>();
            ParsekLog.TestSinkForTesting = line =>
            {
                if (line.Contains("autorun exit:")) order.Add("log");
            };

            InGameTestRunner.PerformAutorunExit(() => order.Add("quit"), "FLIGHT");

            Assert.Equal(new[] { "log", "quit" }, order.ToArray());
        }

        // Guards: the durable pre-quit line carries the mechanism + scene so the log alone
        // names how and where the session ended.
        [Fact]
        public void PerformAutorunExit_PreQuitLine_CarriesMechanismAndScene()
        {
            InGameTestRunner.PerformAutorunExit(() => { }, "FLIGHT");

            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][TestRunner]")
                && l.Contains("autorun exit:")
                && l.Contains("mechanism=ApplicationQuit")
                && l.Contains("scene=FLIGHT"));
        }

        // Guards edge 12: a throwing quit is contained as an ERROR line (not rethrown), so
        // the batch-end region cannot wedge on an unhandled throw; the orchestrator's
        // timeout reaps the process instead.
        [Fact]
        public void PerformAutorunExit_ThrowingQuit_IsContainedAsError()
        {
            var ex = Record.Exception(() =>
                InGameTestRunner.PerformAutorunExit(
                    () => throw new InvalidOperationException("boom"), "SPACECENTER"));

            Assert.Null(ex);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][ERROR][TestRunner]")
                && l.Contains("autorun exit quit call failed")
                && l.Contains("InvalidOperationException"));
        }

        // Guards edge 13 wiring: a button-initiated batch (wasAutorunBatch=false) never
        // reaches PerformAutorunExit because H2ExitDecision.ShouldQuit is false even with
        // the exit env armed. This is the decision that gates the exit tail; the pure
        // AutorunHooksTests cover the full truth table, this pins the wiring intent.
        [Fact]
        public void H2Decision_ButtonBatchWithExitArmed_DoesNotQuit()
        {
            var d = AutorunHooks.H2ExitDecision(
                exitArmed: true, wasAutorunBatch: false, bounceArmed: false);
            Assert.False(d.ShouldQuit);
        }
    }
}
