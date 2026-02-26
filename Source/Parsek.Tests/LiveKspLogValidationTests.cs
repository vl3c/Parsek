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

            var entries = ParsekKspLogParser.ParseFile(logPath);
            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

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
