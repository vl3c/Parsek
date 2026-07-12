using System;
using System.IO;
using System.Text;
using Parsek.Tests.LogValidation;
using Xunit;

namespace Parsek.Tests
{
    public class LiveKspLogValidationTests
    {
        [Fact]
        public void ValidateLatestSession()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("PARSEK_LIVE_VALIDATE_REQUIRED"),
                    "1",
                    StringComparison.Ordinal))
            {
                return;
            }

            string logPath = Environment.GetEnvironmentVariable("PARSEK_LIVE_KSP_LOG_PATH");
            Assert.False(
                string.IsNullOrWhiteSpace(logPath),
                "PARSEK_LIVE_KSP_LOG_PATH must be set when PARSEK_LIVE_VALIDATE_REQUIRED=1.");
            Assert.True(
                File.Exists(logPath),
                $"Live validation log path does not exist: {logPath}");

            // Run-shape rule suppression (M-A5): the harness passes
            // PARSEK_LIVE_SUPPRESS_RULES to switch off the marker-pairing rules that
            // a KILLED (truncated tail) or recording-free run legitimately trips.
            // FMT/WRN and unknown codes are UNSUPPRESSABLE: ParseSuppressionList
            // classifies them illegal, and an illegal request is a HARD test failure
            // (the cannot-mask guarantee), never a silent skip.
            string suppressEnv = Environment.GetEnvironmentVariable("PARSEK_LIVE_SUPPRESS_RULES");
            var suppression = ParsekLogContractChecker.ParseSuppressionList(suppressEnv);
            if (!suppression.Ok)
            {
                Assert.True(
                    false,
                    "PARSEK_LIVE_SUPPRESS_RULES names unsuppressable/unknown rule codes ["
                    + string.Join(",", suppression.IllegalCodes)
                    + "]. Only the marker-pairing rules ["
                    + string.Join(",", ParsekLogContractChecker.SuppressibleRuleCodes)
                    + "] may be suppressed; FMT/WRN and unknown rules can never be masked.");
            }
            foreach (string code in suppression.Suppressed)
                Console.WriteLine($"[LiveValidate] suppressing rule {code} per PARSEK_LIVE_SUPPRESS_RULES");

            var entries = ParsekKspLogParser.ParseFile(logPath);
            var violations = ParsekLogContractChecker.ValidateLatestSession(entries, suppression.Suppressed);

            if (violations.Count == 0)
                return;

            var sb = new StringBuilder();
            sb.AppendLine($"Live KSP.log validation failed for '{logPath}'.");
            sb.AppendLine("Violations:");
            for (int i = 0; i < violations.Count; i++)
                sb.AppendLine(violations[i].ToDisplayString());

            Assert.True(false, sb.ToString());
        }
    }
}
