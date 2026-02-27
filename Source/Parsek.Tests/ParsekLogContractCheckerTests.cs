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
        [InlineData("bad_atmo_split_metrics.log", "REC-002")]
        [InlineData("bad_atmo_warning.log", "WRN-001")]
        [InlineData("bad_atmo_error.log", "ERR-001")]
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
        public void AutoStoppedSceneChange_CountsAsRecordingStop()
        {
            var entries = ParsekKspLogParser.ParseLines(new[]
            {
                "[LOG] [Parsek][INFO][Init] SessionStart runUtc=3003",
                "[LOG] [Parsek][INFO][Recorder] Recording started (physics-frame sampling)",
                "[LOG] [Parsek][INFO][Recorder] Auto-stopped recording due to scene change"
            });

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.Empty(violations);
        }

        [Fact]
        public void MalformedSessionStart_ReportsSes002()
        {
            var entries = ParsekKspLogParser.ParseLines(new[]
            {
                "[LOG] [Parsek][INFO][Init] SessionStart runUtc=abc",
                "[LOG] [Parsek][INFO][Recorder] Recording started (physics-frame sampling)",
                "[LOG] [Parsek][INFO][Recorder] Recording stopped. 3 points, 0 orbit segments over 1.2s"
            });

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.Contains(violations, v => v.Code == "SES-002");
        }

        [Fact]
        public void InvalidLogLevel_ReportsFmt002()
        {
            var entries = ParsekKspLogParser.ParseLines(new[]
            {
                "[LOG] [Parsek][INFO][Init] SessionStart runUtc=3004",
                "[LOG] [Parsek][DEBUG][Recorder] Recording started (physics-frame sampling)",
                "[LOG] [Parsek][INFO][Recorder] Recording stopped. 3 points, 0 orbit segments over 1.2s"
            });

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.Contains(violations, v => v.Code == "FMT-002");
        }

        [Fact]
        public void UnparseableResourcePostValue_ReportsRes002()
        {
            var entries = ParsekKspLogParser.ParseLines(new[]
            {
                "[LOG] [Parsek][INFO][Init] SessionStart runUtc=3005",
                "[LOG] [Parsek][INFO][Recorder] Recording started (physics-frame sampling)",
                "[LOG] [Parsek][INFO][GameStateRecorder] Game state: FundsChanged +50 (Test) -> 9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999",
                "[LOG] [Parsek][INFO][Recorder] Recording stopped. 3 points, 0 orbit segments over 1.2s"
            });

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.Contains(violations, v => v.Code == "RES-002");
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

        [Fact]
        public void AtmoSoiFixture_HasNoViolations()
        {
            var entries = ParsekKspLogParser.ParseFile(TestFixtureLoader.GetFixturePath("good_atmo_soi_session.log"));

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.Empty(violations);
        }

        [Fact]
        public void AtmoSoiFixture_ContainsExpectedSubsystems()
        {
            var entries = ParsekKspLogParser.ParseFile(TestFixtureLoader.GetFixturePath("good_atmo_soi_session.log"));
            var session = ParsekKspLogParser.SelectLatestSession(entries);

            Assert.Contains(session, e => e.Subsystem == "Recorder" &&
                (e.Message ?? "").Contains("Atmosphere boundary confirmed"));
            Assert.Contains(session, e => e.Subsystem == "Recorder" &&
                (e.Message ?? "").Contains("Atmosphere boundary detected"));
            Assert.Contains(session, e => e.Subsystem == "Recorder" &&
                (e.Message ?? "").Contains("Atmosphere state reseeded"));
            Assert.Contains(session, e => e.Subsystem == "Recorder" &&
                (e.Message ?? "").Contains("Boundary detection initialized"));
            Assert.Contains(session, e => e.Subsystem == "Recorder" &&
                (e.Message ?? "").Contains("SOI changed during orbit recording"));
            Assert.Contains(session, e => e.Subsystem == "Flight" &&
                (e.Message ?? "").Contains("Atmosphere auto-split triggered"));
            Assert.Contains(session, e => e.Subsystem == "Flight" &&
                (e.Message ?? "").Contains("SOI auto-split triggered"));
            Assert.Contains(session, e => e.Subsystem == "Flight" &&
                (e.Message ?? "").Contains("Boundary split: committing segment"));
            Assert.Contains(session, e => e.Subsystem == "Flight" &&
                (e.Message ?? "").Contains("Boundary split committed"));
            Assert.Contains(session, e => e.Subsystem == "Scenario" &&
                (e.Message ?? "").Contains("Saved metadata:") && (e.Message ?? "").Contains("phase=atmo"));
            Assert.Contains(session, e => e.Subsystem == "Scenario" &&
                (e.Message ?? "").Contains("Loaded metadata:") && (e.Message ?? "").Contains("phase=space"));
            Assert.Contains(session, e => e.Subsystem == "RecordingStore" &&
                (e.Message ?? "").Contains("Validating chains"));
            Assert.Contains(session, e => e.Subsystem == "RecordingStore" &&
                (e.Message ?? "").Contains("All chains validated OK"));
            Assert.Contains(session, e => e.Subsystem == "Flight" &&
                (e.Message ?? "").Contains("Ghost #0 destroyed") && (e.Message ?? "").Contains("segment disabled"));
            Assert.Contains(session, e => e.Subsystem == "UI" &&
                (e.Message ?? "").Contains("autoSplitAtAtmosphere"));
            Assert.Contains(session, e => e.Subsystem == "UI" &&
                (e.Message ?? "").Contains("autoSplitAtSoi"));
        }

        [Fact]
        public void AtmoSoiFixture_ChainBoundaryStopMetricsValidated()
        {
            // Chain boundary stops must pass the same REC-002 metrics validation
            var entries = ParsekKspLogParser.ParseFile(TestFixtureLoader.GetFixturePath("good_atmo_soi_session.log"));
            var session = ParsekKspLogParser.SelectLatestSession(entries);

            var chainBoundaryStops = session.Where(e =>
                e.Subsystem == "Recorder" &&
                (e.Message ?? "").Contains("Recording stopped (chain boundary)")).ToList();

            Assert.True(chainBoundaryStops.Count >= 2,
                $"Expected at least 2 chain boundary stops, found {chainBoundaryStops.Count}");
        }

        [Fact]
        public void ChainBoundaryStopWithTooFewPoints_ReportsRec002()
        {
            var entries = ParsekKspLogParser.ParseFile(TestFixtureLoader.GetFixturePath("bad_atmo_split_metrics.log"));

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.Contains(violations, v => v.Code == "REC-002");
        }

        [Fact]
        public void AtmoSoiSession_MultipleStartStopPairs_NoRecViolations()
        {
            // A session with atmosphere/SOI splits has multiple start/stop pairs;
            // the contract checker should not report REC-001 or REC-003
            var entries = ParsekKspLogParser.ParseFile(TestFixtureLoader.GetFixturePath("good_atmo_soi_session.log"));

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.DoesNotContain(violations, v => v.Code == "REC-001");
            Assert.DoesNotContain(violations, v => v.Code == "REC-003");
        }
    }
}
