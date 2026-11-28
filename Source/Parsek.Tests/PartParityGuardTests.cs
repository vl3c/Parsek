using System;
using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// <see cref="PartParitySweepRunner.Guard"/>: a guarded stage that throws becomes a case flag
    /// and detail, and the case's FIRST throw also logs the whole exception (type, message and
    /// stack) once, so a live run names the throwing frame (the first X4 burst flight logged only
    /// <c>trigger: NullReferenceException: Object reference not set ...</c> for all 47 cases).
    /// </summary>
    [Collection("Sequential")]
    public class PartParityGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PartParityGuardTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static void ThrowFromANamedFrame() => throw new NullReferenceException("boom");

        [Fact]
        public void Guard_FirstThrow_LogsTheStackOncePerCase()
        {
            var wc = new PartParitySweepRunner.WaveCase { CaseId = "case-a" };
            PartParitySweepRunner.Guard(null, wc, "trigger", ThrowFromANamedFrame);
            PartParitySweepRunner.Guard(null, wc, "capture", ThrowFromANamedFrame);

            Assert.Equal(2, logLines.FindAll(l => l.Contains("[PartParity]") && l.Contains("case case-a:")
                && l.Contains(": NullReferenceException: boom")).Count);
            List<string> stacks = logLines.FindAll(l => l.Contains("case case-a: trigger exception stack:"));
            Assert.Single(stacks);
            Assert.Contains(nameof(ThrowFromANamedFrame), stacks[0]);
            Assert.DoesNotContain(logLines, l => l.Contains("capture exception stack:"));
            Assert.True(wc.ExceptionStackLogged);
            Assert.Equal("trigger: NullReferenceException: boom", wc.Detail);
            Assert.Contains("exception-trigger", wc.Flags);
            Assert.Contains("exception-capture", wc.Flags);
            Assert.Null(wc.Status);
        }

        [Fact]
        public void Guard_EachCaseLogsItsOwnStack()
        {
            var a = new PartParitySweepRunner.WaveCase { CaseId = "case-a" };
            var b = new PartParitySweepRunner.WaveCase { CaseId = "case-b" };
            PartParitySweepRunner.Guard(null, a, "trigger", ThrowFromANamedFrame);
            PartParitySweepRunner.Guard(null, b, "trigger", ThrowFromANamedFrame);
            Assert.Single(logLines.FindAll(l => l.Contains("case case-a: trigger exception stack:")));
            Assert.Single(logLines.FindAll(l => l.Contains("case case-b: trigger exception stack:")));
        }

        [Fact]
        public void Guard_NoThrow_LogsNothing()
        {
            var wc = new PartParitySweepRunner.WaveCase { CaseId = "case-a" };
            PartParitySweepRunner.Guard(null, wc, "trigger", () => { });
            Assert.Empty(logLines);
            Assert.False(wc.ExceptionStackLogged);
            Assert.Empty(wc.Flags);
        }
    }
}
