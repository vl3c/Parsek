using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// The ONLY rule codes a run-shape suppression profile may switch off: the
        /// marker-pairing rules (session start/end + recording start/stop). A KILLED
        /// run legitimately truncates the tail mid-session, and a recording-free
        /// scenario has no Recording started/stopped lines, so those two profiles
        /// suppress a subset of these. FMT-001/FMT-002/WRN-001 (log line
        /// format/level + the forbidden-warning contract) are UNSUPPRESSABLE by
        /// construction: they are NOT in this set, so any request to suppress them
        /// (or an unknown code) is rejected -- the design's cannot-mask guarantee.
        /// </summary>
        internal static readonly IReadOnlyList<string> SuppressibleRuleCodes =
            new[] { "SES-000", "SES-001", "REC-001", "REC-003" };

        private static readonly Regex SessionStartPattern = new Regex(
            @"^SessionStart runUtc=(?<utc>\d+)$",
            RegexOptions.Compiled);

        /// <summary>
        /// Result of parsing the <c>PARSEK_LIVE_SUPPRESS_RULES</c> env value. Pure,
        /// so the suppression contract + the unsuppressable guard are unit-tested
        /// without a KSP.log.
        /// </summary>
        internal sealed class SuppressionParse
        {
            public IReadOnlyList<string> Suppressed { get; }
            public IReadOnlyList<string> IllegalCodes { get; }

            public SuppressionParse(IReadOnlyList<string> suppressed, IReadOnlyList<string> illegal)
            {
                Suppressed = suppressed;
                IllegalCodes = illegal;
            }

            /// <summary>True iff every requested code is a suppressible rule code.</summary>
            public bool Ok => IllegalCodes.Count == 0;
        }

        /// <summary>
        /// Parse a comma-separated <c>PARSEK_LIVE_SUPPRESS_RULES</c> value into the
        /// suppressible codes it names and the ILLEGAL ones it names. A code that is
        /// not a suppressible marker-pairing rule (FMT/WRN, or anything unknown) goes
        /// into <see cref="SuppressionParse.IllegalCodes"/> and is NEVER placed in
        /// Suppressed, so it can never be masked. Case-insensitive; whitespace and
        /// empty tokens tolerated; duplicates collapsed in canonical order.
        /// </summary>
        internal static SuppressionParse ParseSuppressionList(string envValue)
        {
            var suppressed = new List<string>();
            var illegal = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                foreach (string rawToken in envValue.Split(','))
                {
                    string code = (rawToken ?? string.Empty).Trim().ToUpperInvariant();
                    if (code.Length == 0)
                        continue;
                    if (!seen.Add(code))
                        continue;
                    if (SuppressibleRuleCodes.Contains(code))
                        suppressed.Add(code);
                    else
                        illegal.Add(code);
                }
            }
            // Emit suppressed in the canonical SuppressibleRuleCodes order for a
            // stable, testable list regardless of the env value's token order.
            var ordered = new List<string>();
            foreach (string code in SuppressibleRuleCodes)
                if (suppressed.Contains(code))
                    ordered.Add(code);
            return new SuppressionParse(ordered, illegal);
        }

        public static IReadOnlyList<LogViolation> ValidateLatestSession(
            IReadOnlyList<KspLogEntry> parsekEntries)
        {
            return ValidateLatestSession(parsekEntries, null);
        }

        /// <summary>
        /// Validate the latest session, dropping any violation whose code is in
        /// <paramref name="suppressedRules"/>. The caller MUST have gated the
        /// suppression set through <see cref="ParseSuppressionList"/> so only
        /// suppressible marker-pairing codes ever reach here; an FMT/WRN code passed
        /// in defensively still cannot be masked because it is filtered against
        /// <see cref="SuppressibleRuleCodes"/> below.
        /// </summary>
        public static IReadOnlyList<LogViolation> ValidateLatestSession(
            IReadOnlyList<KspLogEntry> parsekEntries,
            IReadOnlyList<string> suppressedRules)
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
                return ApplySuppression(violations, suppressedRules);
            }

            IReadOnlyList<KspLogEntry> latestSession = ParsekKspLogParser.SelectLatestSession(parsekEntries);
            if (latestSession.Count == 0)
            {
                violations.Add(new LogViolation(
                    code: "SES-000",
                    lineNumber: 0,
                    message: "No [Parsek] lines were found in the latest session.",
                    rawLine: string.Empty));
                return ApplySuppression(violations, suppressedRules);
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

            return ApplySuppression(violations, suppressedRules);
        }

        private static IReadOnlyList<LogViolation> ApplySuppression(
            List<LogViolation> violations, IReadOnlyList<string> suppressedRules)
        {
            if (suppressedRules == null || suppressedRules.Count == 0)
                return violations;
            // Defence in depth: only genuine suppressible marker-pairing codes are
            // ever dropped, even if a caller passed an FMT/WRN code here directly.
            var effective = new HashSet<string>(StringComparer.Ordinal);
            foreach (string code in suppressedRules)
                if (code != null && SuppressibleRuleCodes.Contains(code))
                    effective.Add(code);
            if (effective.Count == 0)
                return violations;
            var kept = new List<LogViolation>();
            foreach (LogViolation v in violations)
                if (!effective.Contains(v.Code))
                    kept.Add(v);
            return kept;
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
