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
        public void MalformedParsekLineInLatestSession_ReportsFmt001()
        {
            var entries = ParsekKspLogParser.ParseLines(new[]
            {
                "[LOG] [Parsek][INFO][Init] SessionStart runUtc=3004",
                "[LOG] [Parsek][INFO][Recorder] Recording started (physics-frame sampling)",
                "[LOG] [Parsek] legacy unstructured line",
                "[LOG] [Parsek][INFO][Recorder] Recording stopped. 4 points, 0 orbit segments over 3.0s"
            });

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.Contains(violations, v => v.Code == "FMT-001" && v.LineNumber == 3);
        }

        [Fact]
        public void InvalidLevelInLatestSession_ReportsFmt002()
        {
            var entries = ParsekKspLogParser.ParseLines(new[]
            {
                "[LOG] [Parsek][INFO][Init] SessionStart runUtc=3005",
                "[LOG] [Parsek][TRACE][Recorder] Recording started (physics-frame sampling)",
                "[LOG] [Parsek][INFO][Recorder] Recording stopped. 4 points, 0 orbit segments over 3.0s"
            });

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.Contains(violations, v => v.Code == "FMT-002" && v.LineNumber == 2);
        }

        [Fact]
        public void WarnPayloadWithRedundantWarningPrefix_ReportsWrn001()
        {
            var entries = ParsekKspLogParser.ParseLines(new[]
            {
                "[LOG] [Parsek][INFO][Init] SessionStart runUtc=3006",
                "[LOG] [Parsek][INFO][Recorder] Recording started (physics-frame sampling)",
                "[LOG] [Parsek][WARN][TimeJump] WARNING: vessel is in atmosphere",
                "[LOG] [Parsek][INFO][Recorder] Recording stopped. 4 points, 0 orbit segments over 3.0s"
            });

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.Contains(violations, v => v.Code == "WRN-001" && v.LineNumber == 3);
        }

        [Fact]
        public void MalformedLineBeforeLatestSession_IsIgnored()
        {
            var entries = ParsekKspLogParser.ParseLines(new[]
            {
                "[LOG] [Parsek] old legacy unstructured line",
                "[LOG] [Parsek][INFO][Init] SessionStart runUtc=3007",
                "[LOG] [Parsek][INFO][Recorder] Recording started (physics-frame sampling)",
                "[LOG] [Parsek][INFO][Recorder] Recording stopped. 4 points, 0 orbit segments over 3.0s"
            });

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.DoesNotContain(violations, v => v.Code == "FMT-001");
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
            Assert.Contains(session, e => e.Subsystem == "Flight" &&
                (e.Message ?? "").Contains("Boundary split: committing segment") && (e.Message ?? "").Contains("phase=atmo"));
            Assert.Contains(session, e => e.Subsystem == "RecordingStore" &&
                (e.Message ?? "").Contains("Validating chains"));
            Assert.Contains(session, e => e.Subsystem == "RecordingStore" &&
                (e.Message ?? "").Contains("All chains validated OK"));
            Assert.Contains(session, e => e.Subsystem == "Flight" &&
                (e.Message ?? "").Contains("Ghost #0 destroyed") && (e.Message ?? "").Contains("segment disabled"));
        }

        [Fact]
        public void AtmoSoiFixture_ChainBoundaryStopMetricsValidated()
        {
            var entries = ParsekKspLogParser.ParseFile(TestFixtureLoader.GetFixturePath("good_atmo_soi_session.log"));
            var session = ParsekKspLogParser.SelectLatestSession(entries);

            var chainBoundaryStops = session.Where(e =>
                e.Subsystem == "Recorder" &&
                (e.Message ?? "").Contains("Recording stopped (chain boundary)")).ToList();

            Assert.True(chainBoundaryStops.Count >= 2,
                $"Expected at least 2 chain boundary stops, found {chainBoundaryStops.Count}");
        }

        [Fact]
        public void AtmoSoiSession_MultipleStartStopPairs_NoRecViolations()
        {
            var entries = ParsekKspLogParser.ParseFile(TestFixtureLoader.GetFixturePath("good_atmo_soi_session.log"));

            var violations = ParsekLogContractChecker.ValidateLatestSession(entries);

            Assert.DoesNotContain(violations, v => v.Code == "REC-001");
            Assert.DoesNotContain(violations, v => v.Code == "REC-003");
        }
    }
}
