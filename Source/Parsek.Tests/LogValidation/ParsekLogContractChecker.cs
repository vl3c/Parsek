using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Parsek.Tests.LogValidation
{
    internal static class ParsekLogContractChecker
    {
        private static readonly HashSet<string> AllowedLevels = new HashSet<string>(StringComparer.Ordinal)
        {
            "INFO",
            "VERBOSE",
            "WARN",
            "ERROR"
        };

        private static readonly Regex SuppressedPattern = new Regex(
            @"\bsuppressed=(?<count>[^\s|,]+)",
            RegexOptions.Compiled);

        private static readonly Regex StopMetricsPattern = new Regex(
            @"^Recording stopped(?: \(chain boundary\))?\.\s*(?<points>\d+)\s+points,\s*(?<segments>-?\d+)\s+orbit segments over\s*(?<duration>-?[0-9]+(?:\.[0-9]+)?)s$",
            RegexOptions.Compiled);

        private static readonly Regex ResourcePostValuePattern = new Regex(
            @"^Game state: (?<type>FundsChanged|ScienceChanged|ReputationChanged).*(?:\u2192|->)\s*(?<value>-?[0-9]+(?:\.[0-9]+)?)$",
            RegexOptions.Compiled);

        private static readonly Regex SessionStartPattern = new Regex(
            @"^SessionStart runUtc=(?<utc>\d+)$",
            RegexOptions.Compiled);

        public static IReadOnlyList<LogViolation> ValidateLatestSession(IReadOnlyList<KspLogEntry> parsekEntries)
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
                        message: "Malformed Parsek log line; expected [Parsek][LEVEL][Subsystem] message format.",
                        rawLine: entry.RawLine));
                    continue;
                }

                if (!AllowedLevels.Contains(entry.Level ?? string.Empty))
                {
                    violations.Add(new LogViolation(
                        code: "FMT-002",
                        lineNumber: entry.LineNumber,
                        message: $"Invalid log level '{entry.Level}'.",
                        rawLine: entry.RawLine));
                }

                if (IsSessionStart(entry))
                {
                    sessionMarkerCount++;
                    if (!SessionStartPattern.IsMatch(entry.Message ?? string.Empty))
                    {
                        violations.Add(new LogViolation(
                            code: "SES-002",
                            lineNumber: entry.LineNumber,
                            message: "SessionStart marker must use 'SessionStart runUtc=<unix-seconds>'.",
                            rawLine: entry.RawLine));
                    }
                }

                // TODO: add whitelist support when intentional error-path scenarios are introduced.
                if (string.Equals(entry.Level, "ERROR", StringComparison.Ordinal))
                {
                    violations.Add(new LogViolation(
                        code: "ERR-001",
                        lineNumber: entry.LineNumber,
                        message: "Unexpected ERROR log found in latest session.",
                        rawLine: entry.RawLine));
                }

                if (string.Equals(entry.Level, "WARN", StringComparison.Ordinal) &&
                    (entry.Message ?? string.Empty).TrimStart().StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add(new LogViolation(
                        code: "WRN-001",
                        lineNumber: entry.LineNumber,
                        message: "WARN logs should not include a redundant 'WARNING:' prefix in the message.",
                        rawLine: entry.RawLine));
                }

                ValidateSuppressedCount(entry, violations);
                ValidateStopMetrics(entry, violations);
                ValidateResourcePostValue(entry, violations);

                if (IsRecordingStart(entry))
                    hasRecordingStart = true;
                if (IsRecordingStop(entry))
                    hasRecordingStop = true;
            }

            if (sessionMarkerCount != 1)
            {
                violations.Add(new LogViolation(
                    code: "SES-001",
                    lineNumber: 0,
                    message: $"Latest session must contain exactly one SessionStart marker, found {sessionMarkerCount}.",
                    rawLine: string.Empty));
            }

            if (!hasRecordingStart)
            {
                violations.Add(new LogViolation(
                    code: "REC-001",
                    lineNumber: 0,
                    message: "Latest session is missing a Recorder 'Recording started' line.",
                    rawLine: string.Empty));
            }

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

            return (entry.Message ?? string.Empty).StartsWith("SessionStart runUtc=", StringComparison.Ordinal);
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

            return (entry.Message ?? string.Empty).StartsWith("Recording stopped", StringComparison.Ordinal);
        }

        private static void ValidateSuppressedCount(KspLogEntry entry, List<LogViolation> violations)
        {
            MatchCollection matches = SuppressedPattern.Matches(entry.Message ?? string.Empty);
            for (int i = 0; i < matches.Count; i++)
            {
                string raw = matches[i].Groups["count"].Value;
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 1)
                {
                    violations.Add(new LogViolation(
                        code: "RAT-001",
                        lineNumber: entry.LineNumber,
                        message: $"Invalid suppressed count '{raw}'. Expected integer >= 1.",
                        rawLine: entry.RawLine));
                }
            }
        }

        private static void ValidateStopMetrics(KspLogEntry entry, List<LogViolation> violations)
        {
            Match match = StopMetricsPattern.Match(entry.Message ?? string.Empty);
            if (!match.Success)
                return;

            bool parsedPoints = int.TryParse(
                match.Groups["points"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int points);
            bool parsedSegments = int.TryParse(
                match.Groups["segments"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int orbitSegments);
            bool parsedDuration = double.TryParse(
                match.Groups["duration"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double durationSeconds);

            if (!parsedPoints || !parsedSegments || !parsedDuration ||
                points < 2 || orbitSegments < 0 || durationSeconds <= 0.0)
            {
                violations.Add(new LogViolation(
                    code: "REC-002",
                    lineNumber: entry.LineNumber,
                    message: "Recording stopped metrics must satisfy points>=2, orbitSegments>=0, duration>0.",
                    rawLine: entry.RawLine));
            }
        }

        private static void ValidateResourcePostValue(KspLogEntry entry, List<LogViolation> violations)
        {
            Match match = ResourcePostValuePattern.Match(entry.Message ?? string.Empty);
            if (!match.Success)
                return;

            if (!double.TryParse(
                    match.Groups["value"].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value))
            {
                violations.Add(new LogViolation(
                    code: "RES-002",
                    lineNumber: entry.LineNumber,
                    message: $"Unable to parse resource post-value '{match.Groups["value"].Value}'.",
                    rawLine: entry.RawLine));
                return;
            }

            if (value < 0.0)
            {
                violations.Add(new LogViolation(
                    code: "RES-001",
                    lineNumber: entry.LineNumber,
                    message: "Resource post-value must be non-negative.",
                    rawLine: entry.RawLine));
            }
        }
    }
}
