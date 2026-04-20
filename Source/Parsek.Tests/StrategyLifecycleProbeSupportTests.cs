using System;
using System.Collections.Generic;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class StrategyLifecycleProbeSupportTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public StrategyLifecycleProbeSupportTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void GetGlobalReadinessBlockReason_NullStrategySystem_ReturnsSystemReason()
        {
            string reason = StrategyLifecycleProbeSupport.GetGlobalReadinessBlockReason(
                strategySystemAvailable: false,
                strategyListAvailable: true,
                administrationAvailable: true);

            Assert.Equal(StrategyLifecycleProbeSupport.StrategySystemNotReadyReason, reason);
        }

        [Fact]
        public void GetGlobalReadinessBlockReason_NullStrategyList_ReturnsListReason()
        {
            string reason = StrategyLifecycleProbeSupport.GetGlobalReadinessBlockReason(
                strategySystemAvailable: true,
                strategyListAvailable: false,
                administrationAvailable: true);

            Assert.Equal(StrategyLifecycleProbeSupport.StrategyListNotReadyReason, reason);
        }

        [Fact]
        public void GetGlobalReadinessBlockReason_MissingAdministration_ReturnsAdministrationReason()
        {
            string reason = StrategyLifecycleProbeSupport.GetGlobalReadinessBlockReason(
                strategySystemAvailable: true,
                strategyListAvailable: true,
                administrationAvailable: false);

            Assert.Equal(StrategyLifecycleProbeSupport.AdministrationNotReadyReason, reason);
        }

        [Fact]
        public void GetGlobalReadinessBlockReason_WhenReady_ReturnsNull()
        {
            string reason = StrategyLifecycleProbeSupport.GetGlobalReadinessBlockReason(
                strategySystemAvailable: true,
                strategyListAvailable: true,
                administrationAvailable: true);

            Assert.Null(reason);
        }

        [Fact]
        public void BuildProbeDiagnostic_IncludesCountsAndStack()
        {
            string diagnostic = StrategyLifecycleProbeSupport.BuildProbeDiagnostic(
                strategyCount: 11,
                nullEntries: 0,
                activeEntries: 0,
                configlessEntries: 0,
                namelessEntries: 0,
                blockedEntries: 0,
                probeThrows: 11,
                firstThrowIndex: 7,
                firstThrowSummary: "NullReferenceException: boom",
                firstThrowDetail: "System.NullReferenceException: boom\n at Strategy.CanBeActivated()");

            Assert.Contains("count=11", diagnostic);
            Assert.Contains("probeThrows=11", diagnostic);
            Assert.Contains("firstProbeIndex=7", diagnostic);
            Assert.Contains("firstProbe='NullReferenceException: boom'", diagnostic);
            Assert.Contains("stack=System.NullReferenceException: boom", diagnostic);
        }

        [Fact]
        public void BuildPollExceptionSummary_FormatsFirstFailure()
        {
            string summary = StrategyLifecycleProbeSupport.BuildPollExceptionSummary(
                strategyCount: 11,
                probeThrows: 11,
                firstThrowIndex: 0,
                firstThrowSummary: "NullReferenceException: boom");

            Assert.Contains("11/11", summary);
            Assert.Contains("firstIndex=0", summary);
            Assert.Contains("firstException='NullReferenceException: boom'", summary);
        }

        [Fact]
        public void FormatExceptionSummary_UsesTypeAndMessage()
        {
            var ex = new InvalidOperationException("nope");

            string summary = StrategyLifecycleProbeSupport.FormatExceptionSummary(ex);

            Assert.Equal("InvalidOperationException: nope", summary);
        }

        [Fact]
        public void LogReadinessSettled_EmitsInfoWithAttemptCount()
        {
            StrategyLifecycleProbeSupport.LogReadinessSettled(
                attemptCount: 4,
                maxAttempts: 30,
                diagnostic: "selected activatable strategy 'OpenSourceTechProgram'");

            Assert.Single(logLines);
            Assert.Equal(
                "[Parsek][INFO][TestRunner] StrategyLifecycle readiness settled after 4/30 attempts: " +
                "selected activatable strategy 'OpenSourceTechProgram'",
                logLines[0]);
        }

        [Fact]
        public void LogReadinessWaiting_EmitsVerboseRateLimitedLine()
        {
            StrategyLifecycleProbeSupport.LogReadinessWaiting(
                StrategyLifecycleProbeSupport.AdministrationNotReadyReason);

            Assert.Single(logLines);
            Assert.Equal(
                "[Parsek][VERBOSE][TestRunner] StrategyLifecycle readiness waiting: " +
                StrategyLifecycleProbeSupport.AdministrationNotReadyReason,
                logLines[0]);
        }

        [Fact]
        public void LogReadinessTimeout_EmitsWarnWithAttemptCount()
        {
            StrategyLifecycleProbeSupport.LogReadinessTimeout(
                attemptCount: 30,
                maxAttempts: 30,
                diagnostic: StrategyLifecycleProbeSupport.AdministrationNotReadyReason);

            Assert.Single(logLines);
            Assert.Equal(
                "[Parsek][WARN][TestRunner] StrategyLifecycle readiness timed out after 30/30 attempts: " +
                StrategyLifecycleProbeSupport.AdministrationNotReadyReason,
                logLines[0]);
        }

        [Fact]
        public void LogPollExceptions_EmitsWarnUsingPollSummary()
        {
            StrategyLifecycleProbeSupport.LogPollExceptions(
                strategyCount: 11,
                probeThrows: 11,
                firstThrowIndex: 0,
                firstThrowSummary: "NullReferenceException: boom");

            Assert.Single(logLines);
            Assert.Equal(
                "[Parsek][WARN][TestRunner] StrategyLifecycle readiness probe saw 11/11 CanBeActivated exception(s) in this poll; firstIndex=0; firstException='NullReferenceException: boom'",
                logLines[0]);
        }

        [Fact]
        public void ShouldFailUnavailableSelection_ClearedTransientIssue_Skips()
        {
            // Final-state-only contract: early readiness waits or probe exceptions may
            // clear, and the later legitimate "no activatable strategy" outcome stays
            // a skip rather than being poisoned into a hard failure.
            bool shouldFail = StrategyLifecycleProbeSupport.ShouldFailUnavailableSelection(
                finalProbeHadException: false,
                finalProbeHadRetryableReadinessBlock: false);

            Assert.False(shouldFail);
        }

        [Fact]
        public void ShouldFailUnavailableSelection_FinalReadinessBlock_Fails()
        {
            bool shouldFail = StrategyLifecycleProbeSupport.ShouldFailUnavailableSelection(
                finalProbeHadException: false,
                finalProbeHadRetryableReadinessBlock: true);

            Assert.True(shouldFail);
        }

        [Fact]
        public void ShouldFailUnavailableSelection_FinalProbeException_Fails()
        {
            bool shouldFail = StrategyLifecycleProbeSupport.ShouldFailUnavailableSelection(
                finalProbeHadException: true,
                finalProbeHadRetryableReadinessBlock: false);

            Assert.True(shouldFail);
        }
    }
}
