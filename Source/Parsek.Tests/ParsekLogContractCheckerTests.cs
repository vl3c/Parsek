using System;
using System.Linq;
using Parsek.Tests.LogValidation;
using Xunit;

namespace Parsek.Tests
{
    public class ParsekLogContractCheckerTests
    {
        [Fact]
        public void GoodFixture_HasNoViolations()
        {
            var entries = ParsekKspLogParser.ParseFile(TestFixtureLoader.GetFixturePath("good_latest_session.log"));

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.Empty(violations);
        }

        [Theory]
        [InlineData("bad_malformed_parsek_line.log", "FMT-001")]
        [InlineData("bad_error_present.log", "ERR-001")]
        [InlineData("bad_warn_warning_prefix.log", "WRN-001")]
        [InlineData("bad_suppressed_value.log", "RAT-001")]
        [InlineData("bad_recording_stop_values.log", "REC-002")]
        [InlineData("bad_negative_resource.log", "RES-001")]
        public void BadFixtures_ReportExpectedViolationCode(string fixtureName, string expectedCode)
        {
            var entries = ParsekKspLogParser.ParseFile(TestFixtureLoader.GetFixturePath(fixtureName));

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.NotEmpty(violations);
            Assert.Contains(violations, v => v.Code == expectedCode);
        }

        [Fact]
        public void MultiSessionFixture_ValidatesOnlyLatestSession()
        {
            var entries = ParsekKspLogParser.ParseFile(TestFixtureLoader.GetFixturePath("multi_session_uses_last_marker.log"));
            var latestSession = ParsekKspLogParser.SelectLatestSession(entries);

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.DoesNotContain(latestSession, e =>
                (e.Message ?? string.Empty).IndexOf("Old failure", StringComparison.Ordinal) >= 0);
            Assert.Empty(violations);
        }

        [Fact]
        public void MissingRecordingStart_ReportsRec001()
        {
            var entries = ParsekKspLogParser.ParseLines(new[]
            {
                "[LOG] [Parsek][INFO][Init] SessionStart runUtc=3000",
                "[LOG] [Parsek][INFO][Recorder] Recording stopped. 4 points, 0 orbit segments over 3.0s"
            });

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.Contains(violations, v => v.Code == "REC-001");
        }

        [Fact]
        public void MissingRecordingStop_ReportsRec003()
        {
            var entries = ParsekKspLogParser.ParseLines(new[]
            {
                "[LOG] [Parsek][INFO][Init] SessionStart runUtc=3001",
                "[LOG] [Parsek][INFO][Recorder] Recording started (physics-frame sampling)"
            });

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.Contains(violations, v => v.Code == "REC-003");
        }

        [Fact]
        public void AsciiArrowResourceLine_IsAccepted()
        {
            var entries = ParsekKspLogParser.ParseLines(new[]
            {
                "[LOG] [Parsek][INFO][Init] SessionStart runUtc=3002",
                "[LOG] [Parsek][INFO][Recorder] Recording started (physics-frame sampling)",
                "[LOG] [Parsek][INFO][GameStateRecorder] Game state: FundsChanged +50 (Test) -> 150",
                "[LOG] [Parsek][INFO][Recorder] Recording stopped. 3 points, 0 orbit segments over 1.2s"
            });

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.Empty(violations);
        }
    }
}
