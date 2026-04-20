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
