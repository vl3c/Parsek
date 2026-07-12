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

        // ------------------------------------------------------------------
        // Run-shape rule suppression (M-A5 PARSEK_LIVE_SUPPRESS_RULES contract).
        // ------------------------------------------------------------------

        // Guards: an empty/unset env value suppresses nothing (default behaviour
        // unchanged) and never trips the illegal guard.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ParseSuppressionList_EmptyValue_SuppressesNothing(string env)
        {
            var parse = ParsekLogContractChecker.ParseSuppressionList(env);

            Assert.True(parse.Ok);
            Assert.Empty(parse.Suppressed);
            Assert.Empty(parse.IllegalCodes);
        }

        // Guards the NoRecordingRun profile: exactly REC-001/REC-003 suppressed, ok.
        [Fact]
        public void ParseSuppressionList_NoRecordingRunList_SuppressesRecRules()
        {
            var parse = ParsekLogContractChecker.ParseSuppressionList("REC-001,REC-003");

            Assert.True(parse.Ok);
            Assert.Equal(new[] { "REC-001", "REC-003" }, parse.Suppressed);
            Assert.Empty(parse.IllegalCodes);
        }

        // Guards the KilledRun profile: the four marker-pairing rules suppressed, ok,
        // and returned in canonical order regardless of the env token order.
        [Fact]
        public void ParseSuppressionList_KilledRunList_SuppressesAllMarkerPairingRules()
        {
            var parse = ParsekLogContractChecker.ParseSuppressionList("REC-003, REC-001 ,SES-001,SES-000");

            Assert.True(parse.Ok);
            Assert.Equal(new[] { "SES-000", "SES-001", "REC-001", "REC-003" }, parse.Suppressed);
            Assert.Empty(parse.IllegalCodes);
        }

        // Guards case/whitespace/duplicate tolerance so a harness formatting quirk
        // does not spuriously fail the illegal guard.
        [Fact]
        public void ParseSuppressionList_CaseWhitespaceDuplicates_Normalized()
        {
            var parse = ParsekLogContractChecker.ParseSuppressionList(" rec-001 ,,REC-001, sES-000 ");

            Assert.True(parse.Ok);
            Assert.Equal(new[] { "SES-000", "REC-001" }, parse.Suppressed);
            Assert.Empty(parse.IllegalCodes);
        }

        // THE cannot-mask guarantee: FMT/WRN rules are unsuppressable. A request to
        // suppress them is illegal (not ok) and never lands in the suppressed set.
        [Theory]
        [InlineData("FMT-001")]
        [InlineData("FMT-002")]
        [InlineData("WRN-001")]
        public void ParseSuppressionList_FmtWrnCode_IsIllegalAndNeverSuppressed(string code)
        {
            var parse = ParsekLogContractChecker.ParseSuppressionList(code);

            Assert.False(parse.Ok);
            Assert.Contains(code, parse.IllegalCodes);
            Assert.Empty(parse.Suppressed);
        }

        // Guards: an unknown rule code is illegal (a typo cannot silently pass).
        [Fact]
        public void ParseSuppressionList_UnknownCode_IsIllegal()
        {
            var parse = ParsekLogContractChecker.ParseSuppressionList("XYZ-999");

            Assert.False(parse.Ok);
            Assert.Contains("XYZ-999", parse.IllegalCodes);
            Assert.Empty(parse.Suppressed);
        }

        // Guards: a mix of a valid marker-pairing code and an illegal one is NOT ok
        // (the whole request is rejected) and the illegal one never suppresses.
        [Fact]
        public void ParseSuppressionList_MixedValidAndIllegal_RejectsAndIsolates()
        {
            var parse = ParsekLogContractChecker.ParseSuppressionList("REC-001,FMT-001");

            Assert.False(parse.Ok);
            Assert.Equal(new[] { "REC-001" }, parse.Suppressed);
            Assert.Contains("FMT-001", parse.IllegalCodes);
        }

        // Guards the killed-run behaviour end-to-end: suppressing the marker-pairing
        // rules drops the missing recording start/stop violations, but an FMT-002
        // level violation in the same session still surfaces (unsuppressable).
        [Fact]
        public void ValidateLatestSession_SuppressedRecRules_DropsRecButKeepsFmt()
        {
            var entries = ParsekKspLogParser.ParseLines(new[]
            {
                "[LOG] [Parsek][INFO][Init] SessionStart runUtc=4000",
                "[LOG] [Parsek][TRACE][Recorder] a bad level line"
            });

            var suppressed = ParsekLogContractChecker.ParseSuppressionList(
                "SES-000,SES-001,REC-001,REC-003").Suppressed;
            var violations = ParsekLogContractChecker.ValidateLatestSession(entries, suppressed);

            Assert.DoesNotContain(violations, v => v.Code == "REC-001");
            Assert.DoesNotContain(violations, v => v.Code == "REC-003");
            Assert.Contains(violations, v => v.Code == "FMT-002");
        }

        // Defence in depth: even if an FMT code is passed straight to the validator
        // (bypassing ParseSuppressionList), it is filtered against the suppressible
        // set and never masks the FMT violation.
        [Fact]
        public void ValidateLatestSession_FmtInSuppressList_StillNotMasked()
        {
            var entries = ParsekKspLogParser.ParseLines(new[]
            {
                "[LOG] [Parsek][INFO][Init] SessionStart runUtc=4001",
                "[LOG] [Parsek][TRACE][Recorder] Recording started (physics-frame sampling)",
                "[LOG] [Parsek][INFO][Recorder] Recording stopped. 4 points, 0 orbit segments over 3.0s"
            });

            var violations = ParsekLogContractChecker.ValidateLatestSession(
                entries, new[] { "FMT-002" });

            Assert.Contains(violations, v => v.Code == "FMT-002");
        }
    }
}
