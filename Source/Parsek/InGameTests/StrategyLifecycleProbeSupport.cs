using System;
using System.Collections.Generic;
using System.Text;
using KSP.UI.Screens;

namespace Parsek.InGameTests
{
    internal static class StrategyLifecycleProbeSupport
    {
        internal const string StrategySystemNotReadyReason = "StrategySystem.Instance is null";
        internal const string StrategyListNotReadyReason = "StrategySystem.Strategies is null";
        internal const string AdministrationNotReadyReason =
            "Administration.Instance is null (stock Strategy.CanBeActivated dereferences it before Administration finishes hydrating)";

        internal static string GetGlobalReadinessBlockReason(
            Strategies.StrategySystem system,
            IList<Strategies.Strategy> strategies)
        {
            return GetGlobalReadinessBlockReason(
                strategySystemAvailable: system != null,
                strategyListAvailable: strategies != null,
                administrationAvailable: Administration.Instance != null);
        }

        // xUnit cannot safely fake hydrated KSP singletons with FormatterServices:
        // Unity fake-null semantics make those wrappers compare equal to null. Keep
        // the stock runtime check above, but expose a plain CLR seam for unit tests.
        internal static string GetGlobalReadinessBlockReason(
            bool strategySystemAvailable,
            bool strategyListAvailable,
            bool administrationAvailable)
        {
            if (!strategySystemAvailable)
                return StrategySystemNotReadyReason;
            if (!strategyListAvailable)
                return StrategyListNotReadyReason;
            if (!administrationAvailable)
                return AdministrationNotReadyReason;

            return null;
        }

        internal static bool ShouldHydrateAdministrationSingleton(
            bool administrationAvailable,
            bool isSpaceCenterScene,
            bool isCareerMode)
        {
            return !administrationAvailable
                && isSpaceCenterScene
                && isCareerMode;
        }

        /// <summary>
        /// Post-warmup rehydrate gate. Unity completes <c>Object.Destroy</c> at frame
        /// end, so the prior StrategyLifecycle canary's Dispose tear-down can leave
        /// <c>Administration.Instance</c> looking alive at entry and then flip it to
        /// null during the warmup frames. This predicate reports whether the second
        /// hydration pass must run: rehydrate when Administration became unavailable
        /// during warmup, skip when it is still available. The <paramref name="canvasExists"/>
        /// input is retained for full truth-table coverage - the rehydrate helper
        /// destroys any stale canvas before rebuilding, so both canvas states are
        /// valid inputs for the "unavailable" branch.
        /// </summary>
        internal static bool ShouldRehydrateAdministrationAfterWarmup(
            bool canvasExists,
            bool administrationAvailable)
        {
            // canvasExists participates in the contract even though the decision
            // collapses to !administrationAvailable: it documents that the rehydrate
            // helper handles both fresh-canvas and stale-canvas cases.
            _ = canvasExists;
            return !administrationAvailable;
        }

        internal static string FormatExceptionSummary(Exception ex)
        {
            if (ex == null)
                return "(null exception)";

            string message = string.IsNullOrEmpty(ex.Message) ? "(no message)" : ex.Message;
            return $"{ex.GetType().Name}: {message}";
        }

        internal static string BuildPollExceptionSummary(
            int strategyCount,
            int probeThrows,
            int firstThrowIndex,
            string firstThrowSummary)
        {
            var sb = new StringBuilder();
            sb.Append("StrategyLifecycle readiness probe saw ")
              .Append(probeThrows)
              .Append("/")
              .Append(strategyCount)
              .Append(" CanBeActivated exception(s) in this poll");

            if (firstThrowIndex >= 0)
                sb.Append("; firstIndex=").Append(firstThrowIndex);
            if (!string.IsNullOrEmpty(firstThrowSummary))
                sb.Append("; firstException='").Append(firstThrowSummary).Append("'");

            return sb.ToString();
        }

        internal static string BuildReadinessWaitingSummary(string readinessReason)
        {
            return $"StrategyLifecycle readiness waiting: {NormalizeDiagnostic(readinessReason)}";
        }

        internal static string BuildAdministrationHydrationReadySummary(
            int waitedFrames,
            int maxFrames)
        {
            return $"StrategyLifecycle: hidden Administration canvas hydrated after {waitedFrames}/{maxFrames} frames";
        }

        internal static string BuildAdministrationHydrationTimeoutSummary(
            int waitedFrames,
            int maxFrames)
        {
            return $"StrategyLifecycle: hidden Administration canvas failed to hydrate after {waitedFrames}/{maxFrames} frames";
        }

        internal static string BuildAdministrationHydrationTimeoutDiagnostic(
            int waitedFrames,
            int maxFrames)
        {
            return $"Administration.Instance stayed null after hidden Administration canvas hydration wait ({waitedFrames}/{maxFrames} frames)";
        }

        internal static string BuildProbeDiagnostic(
            int strategyCount,
            int nullEntries,
            int activeEntries,
            int configlessEntries,
            int namelessEntries,
            int blockedEntries,
            int probeThrows,
            int firstThrowIndex,
            string firstThrowSummary,
            string firstThrowDetail)
        {
            var sb = new StringBuilder();
            sb.Append("no CanBeActivated-true stock strategy available (count=")
              .Append(strategyCount)
              .Append(", null=").Append(nullEntries)
              .Append(", active=").Append(activeEntries)
              .Append(", configless=").Append(configlessEntries)
              .Append(", nameless=").Append(namelessEntries)
              .Append(", blocked=").Append(blockedEntries)
              .Append(", probeThrows=").Append(probeThrows);

            if (firstThrowIndex >= 0)
                sb.Append(", firstProbeIndex=").Append(firstThrowIndex);
            if (!string.IsNullOrEmpty(firstThrowSummary))
                sb.Append(", firstProbe='").Append(firstThrowSummary).Append("'");

            sb.Append(")");

            if (!string.IsNullOrEmpty(firstThrowDetail))
                sb.Append(" stack=").Append(firstThrowDetail);

            return sb.ToString();
        }

        internal static string BuildReadinessSettledSummary(
            int attemptCount,
            int maxAttempts,
            string diagnostic)
        {
            return $"StrategyLifecycle readiness settled after {attemptCount}/{maxAttempts} attempts: " +
                NormalizeDiagnostic(diagnostic);
        }

        internal static string BuildReadinessTimeoutSummary(
            int attemptCount,
            int maxAttempts,
            string diagnostic)
        {
            return $"StrategyLifecycle readiness timed out after {attemptCount}/{maxAttempts} attempts: " +
                NormalizeDiagnostic(diagnostic);
        }

        internal static void LogReadinessSettled(
            int attemptCount,
            int maxAttempts,
            string diagnostic)
        {
            ParsekLog.Info("TestRunner",
                BuildReadinessSettledSummary(attemptCount, maxAttempts, diagnostic));
        }

        internal static void LogReadinessTimeout(
            int attemptCount,
            int maxAttempts,
            string diagnostic)
        {
            ParsekLog.Warn("TestRunner",
                BuildReadinessTimeoutSummary(attemptCount, maxAttempts, diagnostic));
        }

        internal static void LogReadinessWaiting(string readinessReason)
        {
            ParsekLog.VerboseRateLimited(
                "TestRunner",
                "StrategyLifecycle-readiness",
                BuildReadinessWaitingSummary(readinessReason));
        }

        internal static void LogAdministrationHydrationReady(
            int waitedFrames,
            int maxFrames)
        {
            ParsekLog.Info(
                "TestRunner",
                BuildAdministrationHydrationReadySummary(waitedFrames, maxFrames));
        }

        internal static void LogAdministrationHydrationTimeout(
            int waitedFrames,
            int maxFrames)
        {
            ParsekLog.Warn(
                "TestRunner",
                BuildAdministrationHydrationTimeoutSummary(waitedFrames, maxFrames));
        }

        internal static void LogPollExceptions(
            int strategyCount,
            int probeThrows,
            int firstThrowIndex,
            string firstThrowSummary)
        {
            ParsekLog.Warn(
                "TestRunner",
                BuildPollExceptionSummary(
                    strategyCount,
                    probeThrows,
                    firstThrowIndex,
                    firstThrowSummary));
        }

        internal static bool ShouldFailUnavailableSelection(
            bool finalProbeHadException,
            bool finalProbeHadRetryableReadinessBlock)
        {
            return finalProbeHadException || finalProbeHadRetryableReadinessBlock;
        }

        private static string NormalizeDiagnostic(string diagnostic)
        {
            return string.IsNullOrEmpty(diagnostic) ? "(no diagnostic)" : diagnostic;
        }
    }
}
