using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Golden characterization of <see cref="FlightRecorder.RestoreTrackSectionAfterFalseAlarm(double, OrbitSegment?)"/>.
    /// Each case builds a fresh recorder in one resume shape (absolute / relative / checkpoint resume
    /// section, no resume section with each mode default, the anchor-missing / anchor-unresolved
    /// downgrades with and without a body-fixed shadow, every boundary-seed alignment branch and every
    /// clamp outcome), runs the restore, and folds the recorder's whole resulting state (closed sections,
    /// the open section, the anchor fields, the on-rails fields, the flat point list) plus every log line and
    /// rate-limit key into one FNV-1a digest. Doubles are folded as raw bits and numbers inside log lines
    /// are canonicalized to their double bits, so the digest does not depend on how a runtime spells "R".
    /// </summary>
    [Collection("Sequential")]
    public class RestoreTrackSectionAfterFalseAlarmCharacterizationTests : IDisposable
    {
        private static readonly Regex NumberToken = new Regex(
            @"-?\d+(\.\d+)?(E[+-]?\d+)?", RegexOptions.CultureInvariant);

        private const string AnchorId = "anchor-rec";

        private readonly List<string> logLines = new List<string>();

        public RestoreTrackSectionAfterFalseAlarmCharacterizationTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
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
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            FlightRecorder.ResetResumeValidationVesselsOverrideForTesting();
        }

        // ---------- shape builders ----------------------------------------------

        internal static TrajectoryPoint Pt(double ut, double lat, double lon, double alt)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = "Kerbin"
            };
        }

        internal static TrackSection Section(
            SegmentEnvironment env, ReferenceFrame frame, TrackSectionSource source,
            double startUT, double endUT,
            List<TrajectoryPoint> frames = null,
            List<TrajectoryPoint> bodyFixed = null,
            List<OrbitSegment> checkpoints = null,
            string anchorRecordingId = null,
            uint anchorVesselId = 0u)
        {
            return new TrackSection
            {
                environment = env,
                referenceFrame = frame,
                source = source,
                startUT = startUT,
                endUT = endUT,
                frames = frames ?? new List<TrajectoryPoint>(),
                bodyFixedFrames = bodyFixed,
                checkpoints = checkpoints ?? new List<OrbitSegment>(),
                anchorRecordingId = anchorRecordingId,
                anchorVesselId = anchorVesselId,
                sampleRateHz = 3.5f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            };
        }

        internal static void SetField(FlightRecorder r, string name, object value)
        {
            FieldInfo f = typeof(FlightRecorder).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(f);
            f.SetValue(r, value);
        }

        internal static T GetField<T>(FlightRecorder r, string name)
        {
            FieldInfo f = typeof(FlightRecorder).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(f);
            return (T)f.GetValue(r);
        }

        /// <summary>Recorded anchor resolves only for <see cref="AnchorId"/>.</summary>
        internal static void InstallAnchorResolver()
        {
            FlightRecorder.RecordedAnchorPoseOverrideForTesting =
                (string anchorRecordingId, double ut, out AnchorPose pose, out RelativeAnchorResolveFailure failure) =>
                {
                    pose = default;
                    failure = default;
                    if (anchorRecordingId == AnchorId)
                        return true;
                    failure = RelativeAnchorResolveFailure.Create(
                        RelativeAnchorResolveOutcome.Other,
                        "test-anchor-unresolved",
                        null,
                        anchorRecordingId,
                        ut);
                    return false;
                };
        }

        /// <summary>Recorded anchor never resolves and the failure carries no reason.</summary>
        internal static void InstallReasonlessFailingResolver()
        {
            FlightRecorder.RecordedAnchorPoseOverrideForTesting =
                (string anchorRecordingId, double ut, out AnchorPose pose, out RelativeAnchorResolveFailure failure) =>
                {
                    pose = default;
                    failure = default;
                    return false;
                };
        }

        private static void Add(FlightRecorder r, TrackSection s)
        {
            r.TrackSections.Add(s);
            if (s.frames != null)
                for (int i = 0; i < s.frames.Count; i++)
                    r.Recording.Add(s.frames[i]);
        }

        // ---------- digest -------------------------------------------------------

        private static string Bits(double d)
            => BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture);

        private static string FBits(float f)
            => BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("X8", CultureInfo.InvariantCulture);

        private static string CanonicalizeNumbers(string line)
            => NumberToken.Replace(line, m => Bits(double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture)));

        private static IEnumerable<string> RateLimitKeys()
        {
            var prop = typeof(ParsekLog).GetProperty(
                "rateLimitStateByKey", BindingFlags.Static | BindingFlags.NonPublic);
            var dict = (IDictionary)prop.GetValue(null);
            var keys = new List<string>();
            foreach (object k in dict.Keys)
                keys.Add(CanonicalizeNumbers((string)k));
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static void AppendPoints(StringBuilder sb, string tag, List<TrajectoryPoint> pts)
        {
            if (pts == null)
            {
                sb.Append(' ').Append(tag).Append("=null");
                return;
            }
            sb.Append(' ').Append(tag).Append('[').Append(pts.Count.ToString(CultureInfo.InvariantCulture)).Append(']');
            for (int i = 0; i < pts.Count; i++)
            {
                TrajectoryPoint p = pts[i];
                sb.Append(" (").Append(Bits(p.ut)).Append(',').Append(Bits(p.latitude)).Append(',')
                    .Append(Bits(p.longitude)).Append(',').Append(Bits(p.altitude)).Append(',')
                    .Append(p.bodyName ?? "null").Append(',').Append(p.flags.ToString(CultureInfo.InvariantCulture))
                    .Append(')');
            }
        }

        private static void AppendSegment(StringBuilder sb, OrbitSegment s)
        {
            sb.Append(" seg(").Append(Bits(s.startUT)).Append(',').Append(Bits(s.endUT)).Append(',')
                .Append(Bits(s.semiMajorAxis)).Append(',').Append(Bits(s.eccentricity)).Append(',')
                .Append(Bits(s.inclination)).Append(',').Append(s.bodyName ?? "null").Append(')');
        }

        private static void AppendSection(StringBuilder sb, string tag, TrackSection s)
        {
            sb.Append(tag).Append(' ').Append(s.environment).Append('|').Append(s.referenceFrame).Append('|')
                .Append(s.source).Append('|').Append(Bits(s.startUT)).Append('|').Append(Bits(s.endUT)).Append('|')
                .Append(s.anchorRecordingId ?? "null").Append('|')
                .Append(s.anchorVesselId.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(FBits(s.sampleRateHz)).Append('|').Append(FBits(s.boundaryDiscontinuityMeters)).Append('|')
                .Append(FBits(s.minAltitude)).Append('|').Append(FBits(s.maxAltitude)).Append('|')
                .Append(s.isBoundarySeam ? '1' : '0');
            AppendPoints(sb, "f", s.frames);
            AppendPoints(sb, "b", s.bodyFixedFrames);
            if (s.checkpoints == null)
                sb.Append(" c=null");
            else
            {
                sb.Append(" c[").Append(s.checkpoints.Count.ToString(CultureInfo.InvariantCulture)).Append(']');
                for (int i = 0; i < s.checkpoints.Count; i++)
                    AppendSegment(sb, s.checkpoints[i]);
            }
            sb.Append('\n');
        }

        internal static string DumpState(FlightRecorder r)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < r.TrackSections.Count; i++)
                AppendSection(sb, "S" + i.ToString(CultureInfo.InvariantCulture), r.TrackSections[i]);
            sb.Append("active=").Append(GetField<bool>(r, "trackSectionActive") ? '1' : '0').Append('\n');
            AppendSection(sb, "CUR", GetField<TrackSection>(r, "currentTrackSection"));
            sb.Append("rel=").Append(GetField<bool>(r, "isRelativeMode") ? '1' : '0')
                .Append(" anchorId=").Append(GetField<string>(r, "currentAnchorRecordingId") ?? "null")
                .Append(" anchorPid=").Append(GetField<uint>(r, "currentAnchorPid").ToString(CultureInfo.InvariantCulture))
                .Append(" hasCand=").Append(GetField<bool>(r, "hasCurrentAnchorCandidate") ? '1' : '0')
                .Append(" dist=").Append(Bits(GetField<double>(r, "currentAnchorDistanceMeters")))
                .Append(" rails=").Append(GetField<bool>(r, "isOnRails") ? '1' : '0');
            AppendSegment(sb, GetField<OrbitSegment>(r, "currentOrbitSegment"));
            sb.Append('\n');
            AppendPoints(sb, "R", r.Recording);
            sb.Append('\n');
            return sb.ToString();
        }

        private static string Fnv1a64(string s)
        {
            ulong h = 14695981039346656037UL;
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= 1099511628211UL;
            }
            return h.ToString("X16", CultureInfo.InvariantCulture);
        }

        private void AssertGolden(
            Action<FlightRecorder> arrange, double ut, OrbitSegment? resumeSegment,
            string expectedDigest, int expectedLogs)
        {
            var r = new FlightRecorder();
            arrange(r);
            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();

            r.RestoreTrackSectionAfterFalseAlarm(ut, resumeSegment);

            var sb = new StringBuilder();
            sb.Append(DumpState(r));
            for (int i = 0; i < logLines.Count; i++)
                sb.Append("  L ").Append(CanonicalizeNumbers(logLines[i])).Append('\n');
            foreach (string k in RateLimitKeys())
                sb.Append("  K ").Append(k).Append('\n');
            string actual = $"digest={Fnv1a64(sb.ToString())} logs={logLines.Count}";
            string expected = $"digest={expectedDigest} logs={expectedLogs}";
            Assert.True(expected == actual,
                "golden mismatch\nexpected " + expected + "\nactual   " + actual + "\n" + sb);
        }

        // ---------- cases ------------------------------------------------------------

        [Fact]
        public void Golden_AlreadyActiveSection_ReturnsWithoutChange()
        {
            AssertGolden(r =>
            {
                Add(r, Section(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active,
                    100.0, 110.0, new List<TrajectoryPoint> { Pt(109.5, 1, 2, 300) }));
                r.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, 110.0);
            }, 112.0, null, "7A3709FBE4C8CBC4", 0);
        }

        [Fact]
        public void Golden_NoResumeSection_NoHysteresis_AbsoluteDefaults()
        {
            AssertGolden(r => { }, 50.0, null, "78AE0E3A1206A6AB", 1);
        }

        [Fact]
        public void Golden_NoResumeSection_HysteresisEnvironment_RelativeModeAnchorResolves()
        {
            AssertGolden(r =>
            {
                InstallAnchorResolver();
                SetField(r, "environmentHysteresis", new EnvironmentHysteresis(SegmentEnvironment.SurfaceStationary));
                SetField(r, "isRelativeMode", true);
                SetField(r, "currentAnchorRecordingId", AnchorId);
                SetField(r, "currentAnchorPid", 77u);
                SetField(r, "currentAnchorDistanceMeters", 123.5);
            }, 60.0, null, "FAC9A9DBAA00B9A8", 1);
        }

        [Fact]
        public void Golden_NoResumeSection_RelativeModeMissingAnchorId_DowngradesWarn()
        {
            AssertGolden(r =>
            {
                InstallAnchorResolver();
                SetField(r, "isRelativeMode", true);
                SetField(r, "currentAnchorPid", 88u);
                SetField(r, "currentAnchorDistanceMeters", 9.0);
            }, 60.0, null, "2A1E9C1AD9C6E21D", 2);
        }

        [Fact]
        public void Golden_AbsoluteResume_OverhangClamped()
        {
            AssertGolden(r =>
            {
                Add(r, Section(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active,
                    100.0, 110.0, new List<TrajectoryPoint> { Pt(105.0, 1, 2, 250), Pt(109.5, 1.5, 2.5, 321) }));
            }, 110.0, null, "DFA8FDAF1B7EAA89", 4);
        }

        [Fact]
        public void Golden_AbsoluteResume_ExactTouch_NoAlignNoClamp()
        {
            AssertGolden(r =>
            {
                Add(r, Section(SegmentEnvironment.SurfaceMobile, ReferenceFrame.Absolute, TrackSectionSource.Background,
                    100.0, 110.0, new List<TrajectoryPoint> { Pt(110.0, 1, 2, 3) }));
            }, 110.0, null, "A74F28F2508ED591", 2);
        }

        [Fact]
        public void Golden_AbsoluteResume_BoundaryAfterResumeUT_NotAligned()
        {
            AssertGolden(r =>
            {
                Add(r, Section(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active,
                    100.0, 120.0, new List<TrajectoryPoint> { Pt(119.0, 1, 2, 3) }));
            }, 115.0, null, "9CBEFF0A593C5672", 3);
        }

        [Fact]
        public void Golden_AbsoluteResume_NonFiniteBoundaryUT_NotAligned()
        {
            AssertGolden(r =>
            {
                Add(r, Section(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active,
                    100.0, 110.0, new List<TrajectoryPoint> { Pt(double.NaN, 1, 2, 3) }));
            }, 110.0, null, "4E4AF0AA0D821436", 2);
        }

        [Fact]
        public void Golden_AbsoluteResume_SeedBelowClosedStart_ClampRefusedInvert()
        {
            AssertGolden(r =>
            {
                Add(r, Section(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active,
                    110.0, 110.02, new List<TrajectoryPoint> { Pt(109.5, 1, 2, 4200) }));
            }, 110.04, null, "BE0169B52D8F94BD", 4);
        }

        [Fact]
        public void Golden_AbsoluteResume_PayloadPastSeed_ClampRefused()
        {
            AssertGolden(r =>
            {
                Add(r, Section(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, TrackSectionSource.Active,
                    100.0, 112.0, new List<TrajectoryPoint> { Pt(109.0, 1, 2, 3) },
                    checkpoints: new List<OrbitSegment>
                    {
                        new OrbitSegment { startUT = 100.0, endUT = 111.0, bodyName = "Kerbin", semiMajorAxis = 700000.0 }
                    }));
            }, 110.0, null, "783DA795E35251F2", 4);
        }

        [Fact]
        public void Golden_CurrentSectionWithPayloadPreferredOverClosed()
        {
            AssertGolden(r =>
            {
                Add(r, Section(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active,
                    100.0, 105.0, new List<TrajectoryPoint> { Pt(104.0, 1, 2, 3) }));
                SetField(r, "currentTrackSection", Section(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Absolute,
                    TrackSectionSource.Active, 105.0, 108.0, new List<TrajectoryPoint> { Pt(107.5, 4, 5, 6) }));
            }, 108.0, null, "8F33084FAED9EA51", 3);
        }

        [Fact]
        public void Golden_RelativeResume_AnchorResolves()
        {
            AssertGolden(r =>
            {
                InstallAnchorResolver();
                Add(r, Section(SegmentEnvironment.Atmospheric, ReferenceFrame.Relative, TrackSectionSource.Active,
                    200.0, 210.0, new List<TrajectoryPoint> { Pt(209.5, 12.5, 76.8, -3.4) },
                    bodyFixed: new List<TrajectoryPoint> { Pt(209.5, -0.097, -74.557, 76140.0) },
                    anchorRecordingId: AnchorId, anchorVesselId: 4242u));
            }, 210.0, null, "124AE3BAB1F23657", 4);
        }

        [Fact]
        public void Golden_RelativeResume_MissingAnchorId_DowngradeWithShadow()
        {
            AssertGolden(r =>
            {
                InstallAnchorResolver();
                SetField(r, "isRelativeMode", true);
                SetField(r, "currentAnchorRecordingId", AnchorId);
                Add(r, Section(SegmentEnvironment.ExoBallistic, ReferenceFrame.Relative, TrackSectionSource.Active,
                    200.0, 210.0, new List<TrajectoryPoint> { Pt(209.5, 12.5, 76.8, -3.4) },
                    bodyFixed: new List<TrajectoryPoint> { Pt(209.5, -0.097, -74.557, 76140.0) },
                    anchorVesselId: 2450432355u));
            }, 210.0, null, "4B14038AB1220101", 6);
        }

        [Fact]
        public void Golden_RelativeResume_MissingAnchorId_DowngradeNoShadow()
        {
            AssertGolden(r =>
            {
                InstallAnchorResolver();
                Add(r, Section(SegmentEnvironment.ExoBallistic, ReferenceFrame.Relative, TrackSectionSource.Active,
                    200.0, 210.0, new List<TrajectoryPoint> { Pt(209.5, 12.5, 76.8, -3.4) },
                    anchorVesselId: 2450432355u));
            }, 210.0, null, "EB209043D721F6A3", 3);
        }

        [Fact]
        public void Golden_RelativeResume_UnresolvedAnchor_DowngradeWithShadow()
        {
            AssertGolden(r =>
            {
                InstallAnchorResolver();
                Add(r, Section(SegmentEnvironment.Atmospheric, ReferenceFrame.Relative, TrackSectionSource.Active,
                    200.0, 210.0, new List<TrajectoryPoint> { Pt(209.5, 12.5, 76.8, -3.4) },
                    bodyFixed: new List<TrajectoryPoint> { Pt(208.0, -0.1, -74.5, 76000.0), Pt(209.25, -0.097, -74.557, 76140.0) },
                    anchorRecordingId: "other-anchor", anchorVesselId: 31u));
            }, 210.0, null, "6E79A9A97DE48F42", 6);
        }

        [Fact]
        public void Golden_RelativeResume_UnresolvedAnchorReasonless_DowngradeFallbackReason()
        {
            AssertGolden(r =>
            {
                InstallReasonlessFailingResolver();
                Add(r, Section(SegmentEnvironment.Atmospheric, ReferenceFrame.Relative, TrackSectionSource.Active,
                    200.0, 210.0, new List<TrajectoryPoint> { Pt(209.5, 12.5, 76.8, -3.4) },
                    anchorRecordingId: AnchorId));
            }, 210.0, null, "1AEFE5ECE4C6E1FE", 3);
        }

        [Fact]
        public void Golden_RelativeResume_BodyFixedOnlyPayload()
        {
            AssertGolden(r =>
            {
                InstallAnchorResolver();
                Add(r, Section(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active,
                    100.0, 200.0, new List<TrajectoryPoint> { Pt(199.0, 1, 2, 3) }));
                SetField(r, "currentTrackSection", Section(SegmentEnvironment.Atmospheric, ReferenceFrame.Relative,
                    TrackSectionSource.Active, 200.0, 205.0, new List<TrajectoryPoint>(),
                    bodyFixed: new List<TrajectoryPoint> { Pt(204.5, -0.1, -74.5, 70000.0) },
                    anchorRecordingId: "unresolvable"));
            }, 205.0, null, "D39AEE11B1203AF3", 5);
        }

        [Fact]
        public void Golden_CheckpointResume_WithSegment_OnRails()
        {
            AssertGolden(r =>
            {
                Add(r, Section(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, TrackSectionSource.Checkpoint,
                    300.0, 330.0, checkpoints: new List<OrbitSegment>
                    {
                        new OrbitSegment { startUT = 300.0, endUT = 330.0, bodyName = "Kerbin", semiMajorAxis = 700000.0 }
                    }));
                SetField(r, "isOnRails", true);
            }, 330.0, new OrbitSegment
            {
                startUT = 330.0, bodyName = "Kerbin", semiMajorAxis = 12345.0, eccentricity = 0.1, inclination = 1.5
            }, "1E447617C34812CA", 1);
        }

        [Fact]
        public void Golden_CheckpointResume_WithoutSegment_Warns()
        {
            AssertGolden(r =>
            {
                Add(r, Section(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, TrackSectionSource.Checkpoint,
                    300.0, 331.0,
                    new List<TrajectoryPoint> { Pt(329.0, 1, 2, 3) },
                    checkpoints: new List<OrbitSegment>
                    {
                        new OrbitSegment { startUT = 300.0, endUT = 330.0, bodyName = "Kerbin", semiMajorAxis = 700000.0 }
                    }));
                SetField(r, "isOnRails", true);
            }, 330.0, null, "85E57139697F7B69", 3);
        }
    }
}
