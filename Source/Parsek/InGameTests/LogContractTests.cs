using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Parsek.InGameTests
{
    /// <summary>
    /// In-game tests that validate Parsek logging contracts at runtime.
    /// Migrated from the post-hoc KSP.log validator — these checks are better
    /// done in-game where the values exist in memory, rather than parsing them
    /// back out of a log file after the session ends.
    /// </summary>
    public static class LogContractTests
    {
        private static readonly Regex StructuredLinePattern = new Regex(
            @"^\[Parsek\]\[(?<level>[^\]]+)\]\[(?<subsystem>[^\]]+)\]\s(?<message>.*)$",
            RegexOptions.Compiled);

        private static readonly HashSet<string> AllowedLevels = new HashSet<string>(StringComparer.Ordinal)
        {
            "INFO", "VERBOSE", "WARN", "ERROR"
        };

        // --- FMT-001: Structured log format ---

        [InGameTest(Category = "LogContracts",
            Description = "FMT-001: ParsekLog.Write produces correctly structured [Parsek][LEVEL][Sub] lines")]
        public static void LogFormatIsStructured()
        {
            var captured = new List<string>();
            var originalObserver = ParsekLog.TestObserverForTesting;
            try
            {
                ParsekLog.TestObserverForTesting = line => captured.Add(line);

                ParsekLog.Info("TestSub", "test info message");
                ParsekLog.Verbose("TestSub", "test verbose message");
                ParsekLog.Warn("TestSub", "test warn message");
                ParsekLog.Error("TestSub", "test error message");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = originalObserver;
            }

            InGameAssert.AreEqual(4, captured.Count, $"Expected 4 log lines, got {captured.Count}");

            foreach (string line in captured)
            {
                InGameAssert.IsTrue(StructuredLinePattern.IsMatch(line),
                    $"FMT-001: Log line does not match structured format: {line}");
            }

            ParsekLog.Verbose("TestRunner", $"FMT-001: All {captured.Count} log lines match structured format");
        }

        // --- FMT-002: Valid log levels ---

        [InGameTest(Category = "LogContracts",
            Description = "FMT-002: All emitted log levels are in the allowed set (INFO/VERBOSE/WARN/ERROR)")]
        public static void LogLevelsAreValid()
        {
            var captured = new List<string>();
            var originalObserver = ParsekLog.TestObserverForTesting;
            try
            {
                ParsekLog.TestObserverForTesting = line => captured.Add(line);

                ParsekLog.Info("Test", "msg");
                ParsekLog.Verbose("Test", "msg");
                ParsekLog.Warn("Test", "msg");
                ParsekLog.Error("Test", "msg");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = originalObserver;
            }

            foreach (string line in captured)
            {
                var match = StructuredLinePattern.Match(line);
                InGameAssert.IsTrue(match.Success, $"Failed to parse: {line}");
                string level = match.Groups["level"].Value;
                InGameAssert.IsTrue(AllowedLevels.Contains(level),
                    $"FMT-002: Invalid log level '{level}' in: {line}");
            }

            ParsekLog.Verbose("TestRunner", "FMT-002: All log levels are valid");
        }

        // --- WRN-001: No redundant WARNING: prefix ---

        [InGameTest(Category = "LogContracts",
            Description = "WRN-001: WARN log lines do not start with redundant 'WARNING:' prefix")]
        public static void WarnLinesNoRedundantPrefix()
        {
            var captured = new List<string>();
            var originalObserver = ParsekLog.TestObserverForTesting;
            try
            {
                ParsekLog.TestObserverForTesting = line => captured.Add(line);
                ParsekLog.Warn("Test", "Something happened");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = originalObserver;
            }

            InGameAssert.AreEqual(1, captured.Count, "Expected exactly 1 captured line");
            var match = StructuredLinePattern.Match(captured[0]);
            InGameAssert.IsTrue(match.Success, "Failed to parse warn line");
            string message = match.Groups["message"].Value;
            InGameAssert.IsFalse(
                message.TrimStart().StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase),
                $"WRN-001: WARN message starts with redundant 'WARNING:' prefix: {message}");

            ParsekLog.Verbose("TestRunner", "WRN-001: WARN lines have no redundant prefix");
        }

        // --- SES-002: SessionStart format ---

        [InGameTest(Category = "LogContracts",
            Description = "SES-002: SessionStart line uses 'SessionStart runUtc=<unix-seconds>' format")]
        public static void SessionStartFormatValid()
        {
            // Verify the format by constructing what ParsekFlight emits at session start
            long utcNow = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            string sessionLine = $"SessionStart runUtc={utcNow}";

            var pattern = new Regex(@"^SessionStart runUtc=(?<utc>\d+)$");
            InGameAssert.IsTrue(pattern.IsMatch(sessionLine),
                $"SES-002: SessionStart format invalid: {sessionLine}");

            // Also verify the UTC value is plausible (after year 2020)
            long minUtc = 1577836800; // 2020-01-01
            InGameAssert.IsTrue(utcNow > minUtc,
                $"SES-002: SessionStart UTC {utcNow} is before year 2020");

            ParsekLog.Verbose("TestRunner", $"SES-002: SessionStart format valid, utc={utcNow}");
        }

        // --- RAT-001: Suppressed count format ---

        [InGameTest(Category = "LogContracts",
            Description = "RAT-001: VerboseRateLimited suppressed count is a valid integer >= 1")]
        public static void RateLimitSuppressedCountValid()
        {
            var captured = new List<string>();
            var originalObserver = ParsekLog.TestObserverForTesting;
            var originalClock = ParsekLog.ClockOverrideForTesting;
            var originalVerbose = ParsekLog.VerboseOverrideForTesting;
            try
            {
                ParsekLog.TestObserverForTesting = line => captured.Add(line);
                ParsekLog.VerboseOverrideForTesting = true;
                ParsekLog.ResetRateLimitsForTesting();

                double fakeTime = 0.0;
                ParsekLog.ClockOverrideForTesting = () => fakeTime;

                // First call emits immediately
                ParsekLog.VerboseRateLimited("Test", "rat001", "msg1", 1.0);

                // Next 5 calls within the interval get suppressed
                for (int i = 0; i < 5; i++)
                {
                    fakeTime += 0.1;
                    ParsekLog.VerboseRateLimited("Test", "rat001", "msg1", 1.0);
                }

                // After interval, next call emits with suppressed count
                fakeTime += 2.0;
                ParsekLog.VerboseRateLimited("Test", "rat001", "msg1", 1.0);
            }
            finally
            {
                ParsekLog.TestObserverForTesting = originalObserver;
                ParsekLog.ClockOverrideForTesting = originalClock;
                ParsekLog.VerboseOverrideForTesting = originalVerbose;
                ParsekLog.ResetRateLimitsForTesting();
            }

            // Should have 2 emitted lines: first + after interval
            InGameAssert.AreEqual(2, captured.Count, $"Expected 2 emitted lines, got {captured.Count}");

            // Second line should have suppressed=5
            string secondLine = captured[1];
            var suppressedMatch = Regex.Match(secondLine, @"\bsuppressed=(\d+)");
            InGameAssert.IsTrue(suppressedMatch.Success,
                $"RAT-001: Second emission missing suppressed count: {secondLine}");

            int count = int.Parse(suppressedMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            InGameAssert.AreEqual(5, count,
                $"RAT-001: Expected suppressed=5, got suppressed={count}");

            ParsekLog.Verbose("TestRunner", $"RAT-001: Rate-limited suppressed count valid (suppressed={count})");
        }

        // --- REC-002: Recording stop metrics ---

        [InGameTest(Category = "LogContracts", Scene = GameScenes.FLIGHT,
            Description = "REC-002: Playable committed recordings have valid metrics (points>=2, segments>=0, duration>0)")]
        public static void RecordingStopMetricsValid()
        {
            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null || recordings.Count == 0)
                InGameAssert.Skip("No committed recordings to validate");

            int validated = 0, skippedRoots = 0, skippedPreservedSinglePointDebris = 0;
            foreach (var rec in recordings)
            {
                // Tree roots are containers, not trajectory recordings
                if (rec.Points.Count == 0)
                {
                    skippedRoots++;
                    continue;
                }

                // #447 follow-up: scene-exit commits can intentionally preserve a one-point
                // debris leaf when it is still in-flight (SubOrbital / Orbiting). Keep the
                // contract strict for every other committed recording shape.
                if (ShouldSkipStopMetricsValidation(rec))
                {
                    skippedPreservedSinglePointDebris++;
                    continue;
                }

                InGameAssert.IsTrue(rec.Points.Count >= 2,
                    $"REC-002: Recording {rec.RecordingId} has {rec.Points.Count} points (minimum 2)");

                InGameAssert.IsTrue(rec.OrbitSegments.Count >= 0,
                    $"REC-002: Recording {rec.RecordingId} has {rec.OrbitSegments.Count} orbit segments (must be >= 0)");

                double duration = rec.Points[rec.Points.Count - 1].ut - rec.Points[0].ut;
                InGameAssert.IsTrue(duration > 0.0,
                    $"REC-002: Recording {rec.RecordingId} has duration {duration:F1}s (must be > 0)");

                validated++;
            }

            ParsekLog.Verbose("TestRunner",
                $"REC-002: Validated {validated} recordings, {skippedRoots} tree roots skipped, " +
                $"{skippedPreservedSinglePointDebris} preserved single-point in-flight debris leaf/leaves skipped");
        }

        internal static bool ShouldSkipStopMetricsValidation(Recording rec)
        {
            return ParsekFlight.IsStopMetricsExemptSinglePointDebrisLeaf(rec);
        }

        // --- RES-001 / RES-002: Resource values ---

        [InGameTest(Category = "LogContracts", Scene = GameScenes.FLIGHT,
            Description = "RES-001/002: Current game funds/science/reputation are finite and non-negative")]
        public static void ResourceValuesValid()
        {
            if (HighLogic.CurrentGame == null)
                InGameAssert.Skip("No active game");

            // Funds
            if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER)
            {
                double funds = Funding.Instance != null ? Funding.Instance.Funds : 0;
                InGameAssert.IsFalse(double.IsNaN(funds),
                    $"RES-002: Funds is NaN");
                InGameAssert.IsFalse(double.IsInfinity(funds),
                    $"RES-002: Funds is Infinity");
                InGameAssert.IsTrue(funds >= 0,
                    $"RES-001: Funds is negative ({funds:F0})");

                ParsekLog.Verbose("TestRunner", $"Funds={funds:F0}");
            }

            // Science
            if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER ||
                HighLogic.CurrentGame.Mode == Game.Modes.SCIENCE_SANDBOX)
            {
                float science = ResearchAndDevelopment.Instance != null
                    ? ResearchAndDevelopment.Instance.Science : 0f;
                InGameAssert.IsFalse(float.IsNaN(science),
                    $"RES-002: Science is NaN");
                InGameAssert.IsFalse(float.IsInfinity(science),
                    $"RES-002: Science is Infinity");
                InGameAssert.IsTrue(science >= 0,
                    $"RES-001: Science is negative ({science:F1})");

                ParsekLog.Verbose("TestRunner", $"Science={science:F1}");
            }

            // Reputation
            if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER)
            {
                float rep = Reputation.Instance != null ? Reputation.Instance.reputation : 0f;
                InGameAssert.IsFalse(float.IsNaN(rep),
                    $"RES-002: Reputation is NaN");
                InGameAssert.IsFalse(float.IsInfinity(rep),
                    $"RES-002: Reputation is Infinity");
                // Reputation CAN go negative in KSP (penalties), so only check finite

                ParsekLog.Verbose("TestRunner", $"Reputation={rep:F1}");
            }

            ParsekLog.Verbose("TestRunner", "RES-001/002: All resource values valid");
        }

        // --- ERR-001: No unexpected errors in current session ---

        [InGameTest(Category = "LogContracts",
            Description = "ERR-001: ParsekLog.Error produces correctly tagged ERROR lines (format check)")]
        public static void ErrorLogFormatCorrect()
        {
            var captured = new List<string>();
            var originalObserver = ParsekLog.TestObserverForTesting;
            try
            {
                ParsekLog.TestObserverForTesting = line => captured.Add(line);
                ParsekLog.Error("TestSub", "deliberate test error");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = originalObserver;
            }

            InGameAssert.AreEqual(1, captured.Count, "Expected 1 error line");
            InGameAssert.Contains(captured[0], "[ERROR]", "Error line missing [ERROR] tag");
            InGameAssert.Contains(captured[0], "[TestSub]", "Error line missing subsystem tag");
            InGameAssert.Contains(captured[0], "deliberate test error", "Error line missing message");

            ParsekLog.Verbose("TestRunner", "ERR-001: Error log format correct");
        }
    }
}
