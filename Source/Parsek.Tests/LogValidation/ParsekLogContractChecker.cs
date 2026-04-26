using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Parsek.Tests.LogValidation
{
    /// <summary>
    /// Post-hoc KSP.log validation for contracts that genuinely require log file analysis.
    /// These check log pipeline health -- things that cannot be verified in-game because
    /// they validate that logging itself happened correctly.
    ///
    /// Rules migrated to in-game tests (InGameTests/LogContractTests.cs):
    ///   SES-002 (SessionStart format), RAT-001 (suppressed count), REC-002 (stop metrics),
    ///   RES-001 (non-negative resources), RES-002 (finite resources), ERR-001 (error format).
    ///
    /// FMT-001/FMT-002/WRN-001 stay here too because only post-hoc validation sees
    /// real production log lines emitted by every call site, not just synthetic
    /// ParsekLog primitive output.
    /// </summary>
    internal static class ParsekLogContractChecker
    {
        private static readonly HashSet<string> AllowedLevels = new HashSet<string>(StringComparer.Ordinal)
        {
            "INFO",
            "VERBOSE",
            "WARN",
            "ERROR"
        };

        private static readonly Regex SessionStartPattern = new Regex(
            @"^SessionStart runUtc=(?<utc>\d+)$",
            RegexOptions.Compiled);

        public static IReadOnlyList<LogViolation> ValidateLatestSession(
            IReadOnlyList<KspLogEntry> parsekEntries)
        {
            if (parsekEntries == null)
                throw new ArgumentNullException(nameof(parsekEntries));

            var violations = new List<LogViolation>();
            if (parsekEntries.Count == 0)
            {
                violations.Add(new LogViolation(
                    code: "SES-000",
                    lineNumber: 0,
                    message: "No [Parsek] lines were found in the log.",
                    rawLine: string.Empty));
                return violations;
            }

            IReadOnlyList<KspLogEntry> latestSession = ParsekKspLogParser.SelectLatestSession(parsekEntries);
            if (latestSession.Count == 0)
            {
                violations.Add(new LogViolation(
                    code: "SES-000",
                    lineNumber: 0,
                    message: "No [Parsek] lines were found in the latest session.",
                    rawLine: string.Empty));
                return violations;
            }

            int sessionMarkerCount = 0;
            bool hasRecordingStart = false;
            bool hasRecordingStop = false;

            foreach (KspLogEntry entry in latestSession)
            {
                if (!entry.IsStructured)
                {
                    violations.Add(new LogViolation(
                        code: "FMT-001",
                        lineNumber: entry.LineNumber,
                        message: "Parsek line is not in structured [Parsek][LEVEL][Subsystem] format.",
                        rawLine: entry.RawLine));
                    continue;
                }

                if (!AllowedLevels.Contains(entry.Level ?? string.Empty))
                {
                    violations.Add(new LogViolation(
                        code: "FMT-002",
                        lineNumber: entry.LineNumber,
                        message: $"Invalid Parsek log level '{entry.Level ?? "(null)"}'.",
                        rawLine: entry.RawLine));
                }

                if (string.Equals(entry.Level, "WARN", StringComparison.Ordinal))
                {
                    string message = (entry.Message ?? string.Empty).TrimStart();
                    if (message.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
                        message.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add(new LogViolation(
                            code: "WRN-001",
                            lineNumber: entry.LineNumber,
                            message: "WARN payload must not start with a redundant WARNING:/WARN: prefix.",
                            rawLine: entry.RawLine));
                    }
                }

                if (IsSessionStart(entry))
                    sessionMarkerCount++;

                if (IsRecordingStart(entry))
                    hasRecordingStart = true;
                if (IsRecordingStop(entry))
                    hasRecordingStop = true;
            }

            // SES-001: Exactly one SessionStart marker per session
            if (sessionMarkerCount != 1)
            {
                violations.Add(new LogViolation(
                    code: "SES-001",
                    lineNumber: 0,
                    message: $"Latest session must contain exactly one SessionStart marker, found {sessionMarkerCount}.",
                    rawLine: string.Empty));
            }

            // REC-001: Recording started line exists
            if (!hasRecordingStart)
            {
                violations.Add(new LogViolation(
                    code: "REC-001",
                    lineNumber: 0,
                    message: "Latest session is missing a Recorder 'Recording started' line.",
                    rawLine: string.Empty));
            }

            // REC-003: Recording stopped line exists
            if (!hasRecordingStop)
            {
                violations.Add(new LogViolation(
                    code: "REC-003",
                    lineNumber: 0,
                    message: "Latest session is missing a Recorder 'Recording stopped' line.",
                    rawLine: string.Empty));
            }

            return violations;
        }

        private static bool IsSessionStart(KspLogEntry entry)
        {
            if (!string.Equals(entry.Subsystem, "Init", StringComparison.Ordinal))
                return false;

            return SessionStartPattern.IsMatch(entry.Message ?? string.Empty);
        }

        private static bool IsRecordingStart(KspLogEntry entry)
        {
            if (!string.Equals(entry.Subsystem, "Recorder", StringComparison.Ordinal))
                return false;

            return (entry.Message ?? string.Empty).StartsWith("Recording started", StringComparison.Ordinal);
        }

        private static bool IsRecordingStop(KspLogEntry entry)
        {
            if (!string.Equals(entry.Subsystem, "Recorder", StringComparison.Ordinal))
                return false;

            string message = entry.Message ?? string.Empty;
            return message.StartsWith("Recording stopped", StringComparison.Ordinal) ||
                message.StartsWith("Auto-stopped recording due to scene change", StringComparison.Ordinal);
        }
    }
}
