using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the four units extracted from
    /// <see cref="FlightRecorder.RestoreTrackSectionAfterFalseAlarm(double, OrbitSegment?)"/>:
    /// the resume-anchor validation, the Relative-to-Absolute boundary repair, the reopened
    /// section's start-UT alignment and the seam overhang clamp with its log lines.
    /// </summary>
    [Collection("Sequential")]
    public class FalseAlarmResumeHelperTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FalseAlarmResumeHelperTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.ResetForTesting();
            FlightRecorder.ResetResumeValidationVesselsOverrideForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            FlightRecorder.ResetResumeValidationVesselsOverrideForTesting();
        }

        private static TrajectoryPoint Pt(double ut, double alt)
            => RestoreTrackSectionAfterFalseAlarmCharacterizationTests.Pt(ut, 1.0, 2.0, alt);

        private static TrackSection RelativeSection(string anchorId, uint anchorPid, List<TrajectoryPoint> bodyFixed = null)
            => RestoreTrackSectionAfterFalseAlarmCharacterizationTests.Section(
                SegmentEnvironment.Atmospheric, ReferenceFrame.Relative, TrackSectionSource.Active,
                200.0, 210.0, new List<TrajectoryPoint> { Pt(209.5, -3.4) }, bodyFixed,
                anchorRecordingId: anchorId, anchorVesselId: anchorPid);

        private static T Get<T>(FlightRecorder r, string name)
            => RestoreTrackSectionAfterFalseAlarmCharacterizationTests.GetField<T>(r, name);

        private static void Set(FlightRecorder r, string name, object value)
            => RestoreTrackSectionAfterFalseAlarmCharacterizationTests.SetField(r, name, value);

        // ---------- ApplyFalseAlarmResumeAnchor ----------------------------------------

        [Fact]
        public void ApplyAnchor_NonRelative_ClearsAnchorAndRelativeMode()
        {
            var r = new FlightRecorder();
            Set(r, "isRelativeMode", true);
            Set(r, "currentAnchorRecordingId", "x");
            Set(r, "currentAnchorPid", 5u);
            ReferenceFrame frame = ReferenceFrame.Absolute;

            r.ApplyFalseAlarmResumeAnchor(10.0, null, ref frame, out string anchorId);

            Assert.Equal(ReferenceFrame.Absolute, frame);
            Assert.Null(anchorId);
            Assert.False(Get<bool>(r, "isRelativeMode"));
            Assert.Null(Get<string>(r, "currentAnchorRecordingId"));
            Assert.Equal(0u, Get<uint>(r, "currentAnchorPid"));
            Assert.Empty(logLines);
        }

        [Fact]
        public void ApplyAnchor_RelativeSectionResolves_AdoptsSectionAnchor()
        {
            RestoreTrackSectionAfterFalseAlarmCharacterizationTests.InstallAnchorResolver();
            var r = new FlightRecorder();
            Set(r, "hasCurrentAnchorCandidate", true);
            Set(r, "currentAnchorDistanceMeters", 4.0);
            ReferenceFrame frame = ReferenceFrame.Relative;

            r.ApplyFalseAlarmResumeAnchor(210.0, RelativeSection("anchor-rec", 4242u), ref frame, out string anchorId);

            Assert.Equal(ReferenceFrame.Relative, frame);
            Assert.Equal("anchor-rec", anchorId);
            Assert.True(Get<bool>(r, "isRelativeMode"));
            Assert.Equal("anchor-rec", Get<string>(r, "currentAnchorRecordingId"));
            Assert.Equal(4242u, Get<uint>(r, "currentAnchorPid"));
            Assert.False(Get<bool>(r, "hasCurrentAnchorCandidate"));
            Assert.True(double.IsNaN(Get<double>(r, "currentAnchorDistanceMeters")));
            Assert.Empty(logLines);
        }

        [Fact]
        public void ApplyAnchor_NoSection_UsesRecorderAnchorFields()
        {
            RestoreTrackSectionAfterFalseAlarmCharacterizationTests.InstallAnchorResolver();
            var r = new FlightRecorder();
            Set(r, "currentAnchorRecordingId", "anchor-rec");
            Set(r, "currentAnchorPid", 77u);
            ReferenceFrame frame = ReferenceFrame.Relative;

            r.ApplyFalseAlarmResumeAnchor(60.0, null, ref frame, out string anchorId);

            Assert.Equal(ReferenceFrame.Relative, frame);
            Assert.Equal("anchor-rec", anchorId);
            Assert.Equal(77u, Get<uint>(r, "currentAnchorPid"));
        }

        [Fact]
        public void ApplyAnchor_MissingAnchorId_DowngradesAndWarns()
        {
            var r = new FlightRecorder();
            Set(r, "isRelativeMode", true);
            ReferenceFrame frame = ReferenceFrame.Relative;

            r.ApplyFalseAlarmResumeAnchor(210.0, RelativeSection(null, 2450432355u), ref frame, out string anchorId);

            Assert.Equal(ReferenceFrame.Absolute, frame);
            Assert.Null(anchorId);
            Assert.False(Get<bool>(r, "isRelativeMode"));
            Assert.Contains(logLines, l => l.Contains("[Anchor]")
                && l.Contains("RELATIVE resume rejected: missing anchorRecordingId")
                && l.Contains("legacyDiagnosticPid=2450432355")
                && l.Contains("vesselPid=0"));
        }

        [Fact]
        public void ApplyAnchor_UnresolvedAnchor_DowngradesWithReason()
        {
            RestoreTrackSectionAfterFalseAlarmCharacterizationTests.InstallAnchorResolver();
            var r = new FlightRecorder();
            ReferenceFrame frame = ReferenceFrame.Relative;

            r.ApplyFalseAlarmResumeAnchor(210.0, RelativeSection("gone", 31u), ref frame, out string anchorId);

            Assert.Equal(ReferenceFrame.Absolute, frame);
            Assert.Null(anchorId);
            Assert.Null(Get<string>(r, "currentAnchorRecordingId"));
            Assert.Contains(logLines, l => l.Contains("[Anchor]")
                && l.Contains("RELATIVE resume rejected: anchorRecordingId=gone")
                && l.Contains("reason=test-anchor-unresolved")
                && l.Contains("diagnosticPid=31"));
        }

        [Fact]
        public void ApplyAnchor_UnresolvedAnchorWithoutReason_UsesFallbackReason()
        {
            RestoreTrackSectionAfterFalseAlarmCharacterizationTests.InstallReasonlessFailingResolver();
            var r = new FlightRecorder();
            ReferenceFrame frame = ReferenceFrame.Relative;

            r.ApplyFalseAlarmResumeAnchor(210.0, RelativeSection("anchor-rec", 0u), ref frame, out _);

            Assert.Equal(ReferenceFrame.Absolute, frame);
            Assert.Contains(logLines, l => l.Contains("reason=recorded-anchor-unresolved"));
        }

        // ---------- RepairDowngradedResumeBoundaryPoints ------------------------------

        [Fact]
        public void Repair_NotDowngraded_LeavesBothPoints()
        {
            TrajectoryPoint? boundary = Pt(209.5, -3.4);
            TrajectoryPoint? shadow = Pt(209.5, 76140.0);

            FlightRecorder.RepairDowngradedResumeBoundaryPoints(
                RelativeSection("a", 0u), ReferenceFrame.Relative, ref boundary, ref shadow);

            Assert.Equal(-3.4, boundary.Value.altitude);
            Assert.Equal(76140.0, shadow.Value.altitude);
            Assert.Empty(logLines);
        }

        [Fact]
        public void Repair_AbsoluteSectionResume_LeavesBothPoints()
        {
            TrajectoryPoint? boundary = Pt(109.5, 3.0);
            TrajectoryPoint? shadow = null;
            TrackSection? section = RestoreTrackSectionAfterFalseAlarmCharacterizationTests.Section(
                SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active, 100.0, 110.0);

            FlightRecorder.RepairDowngradedResumeBoundaryPoints(section, ReferenceFrame.Absolute, ref boundary, ref shadow);

            Assert.Equal(3.0, boundary.Value.altitude);
            Assert.False(shadow.HasValue);
            Assert.Empty(logLines);
        }

        [Fact]
        public void Repair_DowngradedWithShadow_SubstitutesShadowAndClearsIt()
        {
            TrajectoryPoint? boundary = Pt(209.5, -3.4);
            TrajectoryPoint? shadow = Pt(209.25, 76140.0);

            FlightRecorder.RepairDowngradedResumeBoundaryPoints(
                RelativeSection("a", 0u), ReferenceFrame.Absolute, ref boundary, ref shadow);

            Assert.Equal(76140.0, boundary.Value.altitude);
            Assert.Equal(209.25, boundary.Value.ut);
            Assert.False(shadow.HasValue);
            Assert.Contains(logLines, l => l.Contains("[Anchor]")
                && l.Contains("substituting body-fixed-primary boundary point at ut=209.25"));
        }

        [Fact]
        public void Repair_DowngradedWithoutShadow_DropsBoundarySeed()
        {
            TrajectoryPoint? boundary = Pt(209.5, -3.4);
            TrajectoryPoint? shadow = null;

            FlightRecorder.RepairDowngradedResumeBoundaryPoints(
                RelativeSection("a", 0u), ReferenceFrame.Absolute, ref boundary, ref shadow);

            Assert.False(boundary.HasValue);
            Assert.False(shadow.HasValue);
            Assert.Contains(logLines, l => l.Contains("[Anchor]")
                && l.Contains("prior Relative section has no body-fixed-primary"));
        }

        // ---------- ResolveFalseAlarmResumeSectionStartUT -----------------------------

        [Fact]
        public void StartUT_CheckpointResume_IgnoresSeed()
        {
            Assert.Equal(330.0, FlightRecorder.ResolveFalseAlarmResumeSectionStartUT(
                330.0, ReferenceFrame.OrbitalCheckpoint, Pt(329.0, 1.0)));
            Assert.Empty(logLines);
        }

        [Fact]
        public void StartUT_NoSeedOrNonFiniteOrLater_KeepsResumeUT()
        {
            Assert.Equal(110.0, FlightRecorder.ResolveFalseAlarmResumeSectionStartUT(110.0, ReferenceFrame.Absolute, null));
            Assert.Equal(110.0, FlightRecorder.ResolveFalseAlarmResumeSectionStartUT(
                110.0, ReferenceFrame.Absolute, Pt(double.NaN, 1.0)));
            Assert.Equal(110.0, FlightRecorder.ResolveFalseAlarmResumeSectionStartUT(
                110.0, ReferenceFrame.Absolute, Pt(110.5, 1.0)));
            Assert.Empty(logLines);
        }

        [Fact]
        public void StartUT_SeedWithinEpsilon_AdoptsSeedWithoutLog()
        {
            double seedUT = 110.0 + 5e-7;
            Assert.Equal(seedUT, FlightRecorder.ResolveFalseAlarmResumeSectionStartUT(
                110.0, ReferenceFrame.Relative, Pt(seedUT, 1.0)));
            Assert.Empty(logLines);
        }

        [Fact]
        public void StartUT_EarlierSeed_AlignsAndLogs()
        {
            Assert.Equal(109.5, FlightRecorder.ResolveFalseAlarmResumeSectionStartUT(
                110.0, ReferenceFrame.Absolute, Pt(109.5, 1.0)));
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("ResumeAfterFalseAlarm: aligning reopened TrackSection startUT from 110.000 to boundary seed UT=109.500"));
        }

        // ---------- ClampClosedSectionOverhangAtResumeSeam -----------------------------

        [Fact]
        public void Clamp_NoSections_NoLog()
        {
            var sections = new List<TrackSection>();
            FlightRecorder.ClampClosedSectionOverhangAtResumeSeam(sections, 10.0);
            Assert.Empty(sections);
            Assert.Empty(logLines);
        }

        [Fact]
        public void Clamp_Overhang_ClampsAndLogsInfo()
        {
            var sections = new List<TrackSection>
            {
                RestoreTrackSectionAfterFalseAlarmCharacterizationTests.Section(
                    SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active,
                    100.0, 110.0, new List<TrajectoryPoint> { Pt(109.5, 1.0) })
            };

            FlightRecorder.ClampClosedSectionOverhangAtResumeSeam(sections, 109.5);

            Assert.Equal(109.5, sections[0].endUT);
            Assert.Contains(logLines, l => l.Contains("[INFO]") && l.Contains("[Recorder]")
                && l.Contains("clamped closed TrackSection endUT from 110.000 to boundary seed UT=109.500")
                && l.Contains("disjoint-cover"));
        }

        [Fact]
        public void Clamp_Refused_LeavesSectionAndWarns()
        {
            var sections = new List<TrackSection>
            {
                RestoreTrackSectionAfterFalseAlarmCharacterizationTests.Section(
                    SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active,
                    110.0, 110.02, new List<TrajectoryPoint> { Pt(109.5, 1.0) })
            };

            FlightRecorder.ClampClosedSectionOverhangAtResumeSeam(sections, 109.5);

            Assert.Equal(110.02, sections[0].endUT);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("[Recorder]")
                && l.Contains("closed TrackSection endUT 110.020 overhangs boundary seed UT=109.500")
                && l.Contains("reason=would-invert-section")
                && l.Contains("sectionStartUT=110.000"));
        }
    }
}
