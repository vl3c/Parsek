using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A RELATIVE section can claim a longer span than its samples cover: one sample, two samples
    /// at one UT (todo SWITCHED-TO-KERBAL-UNRECORDED-UNTIL-TRIGGER), or a playback UT outside the
    /// first / last one. Every reader then holds the offset beside the pose the ANCHOR has at the
    /// playback UT, never at the UT of the held sample. The numbers are the EVA-17 flight's: two
    /// frames at 423.893, the section ends at 435.093.
    /// </summary>
    [Collection("Sequential")]
    public class RelativeSampleHoldTests : IDisposable
    {
        private const double FrameUT = 423.893;
        private const double SectionEndUT = 435.093;
        private const double PlaybackUT = 435.05;

        private readonly List<string> logLines = new List<string>();

        public RelativeSampleHoldTests()
        {
            RecordingStore.ResetForTesting();
            RelativeAnchorResolver.ResetForTesting();
            RecordedRelativeAnchorPoseResolver.BodyPoseSourceForTesting = new RayBodyPoseSource();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecordedRelativeAnchorPoseResolver.ResetForTesting();
            RelativeAnchorResolver.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static TrajectoryPoint Offset(double ut, double dx, double dy, double dz)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = dx,
                longitude = dy,
                altitude = dz,
                bodyName = "Kerbin",
                rotation = new Quaternion(0, 0, 0, 1),
            };
        }

        private static List<TrajectoryPoint> Frames(params TrajectoryPoint[] points)
        {
            return new List<TrajectoryPoint>(points);
        }

        // ---- flight: the offset selection (watch / world-position reader) ----

        [Fact]
        public void Offset_NoFrames_IsRefused()
        {
            Assert.False(ParsekFlightGhostPositioner.TrySelectRelativeOffset(
                null, PlaybackUT, out _, out _, out _));
            Assert.False(ParsekFlightGhostPositioner.TrySelectRelativeOffset(
                Frames(), PlaybackUT, out _, out _, out _));
        }

        [Theory]
        [InlineData(423.0)]
        [InlineData(FrameUT)]
        [InlineData(PlaybackUT)]
        public void Offset_OneSample_IsHeldAtEveryUT(double ut)
        {
            Assert.True(ParsekFlightGhostPositioner.TrySelectRelativeOffset(
                Frames(Offset(FrameUT, 1, 2, 3)), ut, out double dx, out double dy, out double dz));
            Assert.Equal(1.0, dx, 9);
            Assert.Equal(2.0, dy, 9);
            Assert.Equal(3.0, dz, 9);
        }

        [Theory]
        [InlineData(423.0)]
        [InlineData(FrameUT)]
        [InlineData(PlaybackUT)]
        public void Offset_TwoSamplesAtOneUT_IsHeldAtEveryUT(double ut)
        {
            Assert.True(ParsekFlightGhostPositioner.TrySelectRelativeOffset(
                Frames(Offset(FrameUT, 1, 2, 3), Offset(FrameUT, 1, 2, 3)),
                ut, out double dx, out double dy, out double dz));
            Assert.Equal(1.0, dx, 9);
            Assert.Equal(2.0, dy, 9);
            Assert.Equal(3.0, dz, 9);
        }

        [Theory]
        [InlineData(99.0, 10.0)]   // before the first sample: held there
        [InlineData(100.0, 10.0)]
        [InlineData(105.0, 20.0)]
        [InlineData(110.0, 30.0)]
        [InlineData(150.0, 30.0)]  // past the last sample: held there
        public void Offset_InterpolatesInsideAndHoldsOutside(double ut, double expectedDx)
        {
            Assert.True(ParsekFlightGhostPositioner.TrySelectRelativeOffset(
                Frames(Offset(100, 10, 0, 0), Offset(110, 30, 0, 0)),
                ut, out double dx, out _, out _));
            Assert.Equal(expectedDx, dx, 6);
        }

        // ---- flight: both positioning paths resolve the anchor at the playback UT ----

        [Fact]
        public void FlightSinglePositioning_ResolvesTheAnchorAtThePlaybackUT()
        {
            string body = MethodBody("ParsekFlightGhostPositioner.RecordedRelative.cs",
                "private bool TryPositionGhostRecordedRelativeAt(");
            Assert.Matches(@"TryResolveRecordedRelativeAnchorPose\(\s*target,\s*playbackUT,", body);
            // LateUpdate re-resolves the anchor at the entry's pointUT.
            Assert.Matches(@"\bpointUT = playbackUT,", body);
            // The sample's own UT only says how long the sample has been held.
            Assert.Single(Regex.Matches(body, @"\bpoint\.ut\b"));
            Assert.Contains("double heldSeconds = playbackUT - point.ut;", body);
        }

        [Fact]
        public void FlightPositioning_HandsThePlaybackUTToBothSingleSampleCases()
        {
            string body = MethodBody("ParsekFlightGhostPositioner.RecordedRelative.cs",
                "void InterpolateAndPositionRecordedRelative(");
            Assert.Matches(@"TryPositionGhostRecordedRelativeAt\(\s*ghost,\s*frames\[0\],\s*targetUT,", body);
            Assert.Matches(@"TryPositionGhostRecordedRelativeAt\(\s*ghost,\s*before,\s*targetUT,", body);
            Assert.Equal(2, Regex.Matches(body, @"TryPositionGhostRecordedRelativeAt\(").Count);
            Assert.Matches(@"TryResolveRecordedRelativeAnchorPose\(\s*target,\s*targetUT,", body);
        }

        [Fact]
        public void FlightWorldPosition_ResolvesTheAnchorAtThePlaybackUT()
        {
            string body = MethodBody("ParsekFlightGhostPositioner.WorldPosition.cs",
                "bool TryResolveRelativeWorldPosition(");
            Assert.Matches(
                @"TryResolveRelativeOffsetWorldPosition\(\s*dx,\s*dy,\s*dz,\s*target,\s*targetUT,", body);
            Assert.Single(Regex.Matches(body, @"TryResolveRelativeOffsetWorldPosition\("));
        }

        // ---- flight: the held-sample line ----

        [Theory]
        [InlineData(0.0, false)]
        [InlineData(0.02, false)]
        [InlineData(0.06, true)]
        [InlineData(11.16, true)]
        [InlineData(-3.0, true)]   // before the first sample
        [InlineData(double.NaN, false)]
        public void HeldSample_IsReportedPastOnePhysicsStep(double heldSeconds, bool expected)
        {
            Assert.Equal(expected, ParsekFlightGhostPositioner.IsHeldRelativeSample(heldSeconds));
        }

        [Fact]
        public void HeldSampleLine_NamesTheSectionAnchorAndHold()
        {
            var target = new RelativeSectionPlaybackTarget(
                "9cee0a67281445659aedc73e04976f58", 0,
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Relative,
                    anchorRecordingId = "aa792b388a4b41fa9b4d2ab1266fdfe9",
                });
            Assert.Equal(
                "RELATIVE sample held beside its anchor: recordingId=9cee0a67 sectionIndex=0 " +
                "anchorRec=aa792b38 playbackUT=435.05 heldSeconds=11.16 offset=(0.00,0.74,-1.29)",
                ParsekFlightGhostPositioner.FormatHeldRelativeSample(
                    target, PlaybackUT, 11.157, 0.0, 0.7413, -1.2855));
        }

        // ---- KSC ----

        private static Recording KscRecording(params TrajectoryPoint[] frames)
        {
            var rec = new Recording
            {
                VesselName = "RelativeKscHold",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            rec.Points.AddRange(frames);
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 420.0,
                endUT = SectionEndUT,
                anchorRecordingId = "anchor-rec",
                frames = new List<TrajectoryPoint>(frames),
            });
            return rec;
        }

        /// <summary>An anchor flying along +x at 2000 m/s, zero at UT 400.</summary>
        private static bool MovingAnchor(Recording rec, TrackSection section, int sectionIndex,
            double targetUT, out ParsekKSC.KscAnchorFrame frame)
        {
            frame = new ParsekKSC.KscAnchorFrame(
                new Vector3d((targetUT - 400.0) * 2000.0, 0, 0), new Quaternion(0, 0, 0, 1));
            return true;
        }

        private static bool NoSurface(string bodyName, double lat, double lon, double alt,
            out Vector3d worldPos, out Quaternion bodyRot)
        {
            worldPos = Vector3d.zero;
            bodyRot = new Quaternion(0, 0, 0, 1);
            return false;
        }

        private static Vector3d KscPose(Recording rec, double ut)
        {
            int cachedIndex = 0;
            int cachedFrameSourceKey = ParsekKSC.KscFlatPointFrameSourceKey;
            Assert.True(ParsekKSC.TryInterpolateKscPlaybackPose(
                rec, ref cachedIndex, ref cachedFrameSourceKey, ut,
                NoSurface, MovingAnchor, out ParsekKSC.KscPoseResolution pose));
            Assert.Equal("relative", pose.Branch);
            return pose.WorldPos;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void Ksc_OneSampleSection_HoldsBesideTheMovingAnchor(int copies)
        {
            var frames = new TrajectoryPoint[copies];
            for (int i = 0; i < copies; i++)
                frames[i] = Offset(FrameUT, 5, 2, 3);
            Vector3d pos = KscPose(KscRecording(frames), PlaybackUT);
            Assert.Equal((PlaybackUT - 400.0) * 2000.0 + 5.0, pos.x, 3);
            Assert.Equal(2.0, pos.y, 6);
            Assert.Equal(3.0, pos.z, 6);
        }

        [Fact]
        public void Ksc_PastTheLastSample_HoldsBesideTheMovingAnchor()
        {
            Recording rec = KscRecording(Offset(423, 1, 0, 0), Offset(424, 9, 0, 0));
            Assert.Equal((430.0 - 400.0) * 2000.0 + 9.0, KscPose(rec, 430.0).x, 3);
        }

        [Fact]
        public void Ksc_BeforeTheFirstSample_HoldsBesideTheMovingAnchor()
        {
            Recording rec = KscRecording(Offset(423, 1, 0, 0), Offset(424, 9, 0, 0));
            Assert.Equal((421.0 - 400.0) * 2000.0 + 1.0, KscPose(rec, 421.0).x, 3);
        }

        [Fact]
        public void Ksc_InsideTheSamples_IsUnchanged()
        {
            Recording rec = KscRecording(Offset(423, 1, 0, 0), Offset(424, 9, 0, 0));
            Assert.Equal((423.5 - 400.0) * 2000.0 + 5.0, KscPose(rec, 423.5).x, 3);
        }

        // ---- map (state-vector orbit seed) ----

        /// <summary>Surface points sit on the +x ray, so a pose reads its altitude back.</summary>
        private sealed class RayBodyPoseSource : IAnchorBodyPoseSource
        {
            public bool TryGetSurfaceWorldPosition(
                string bodyName, double latitude, double longitude, double altitude, out Vector3d worldPos)
            {
                worldPos = new Vector3d(600000.0 + altitude, 0, 0);
                return true;
            }

            public bool TryGetBodyWorldRotation(string bodyName, out Quaternion rotation)
            {
                rotation = new Quaternion(0, 0, 0, 1);
                return true;
            }
        }

        private static Recording MapFocus(out TrajectoryPoint sample)
        {
            sample = Offset(FrameUT, 5, 2, 3);
            var tree = new RecordingTree { Id = "hold-tree" };
            var anchor = new Recording
            {
                RecordingId = "anchor-rec",
                TreeId = tree.Id,
                VesselName = "Anchor",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            anchor.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 420.0,
                endUT = 440.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 420.0, altitude = 0.0, bodyName = "Kerbin",
                        rotation = new Quaternion(0, 0, 0, 1),
                    },
                    new TrajectoryPoint
                    {
                        ut = 440.0, altitude = 40000.0, bodyName = "Kerbin",
                        rotation = new Quaternion(0, 0, 0, 1),
                    },
                },
            });
            var focus = new Recording
            {
                RecordingId = "focus-rec",
                TreeId = tree.Id,
                VesselName = "Focus",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            focus.Points.Add(sample);
            focus.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = FrameUT,
                endUT = SectionEndUT,
                anchorRecordingId = anchor.RecordingId,
                frames = new List<TrajectoryPoint> { sample, sample },
            });
            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(focus);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return focus;
        }

        [Theory]
        [InlineData(FrameUT)]
        [InlineData(PlaybackUT)]
        public void Map_HeldSample_SeedsBesideTheAnchorAtThePlaybackUT(double playbackUT)
        {
            Recording focus = MapFocus(out TrajectoryPoint sample);
            GhostMapPresence.StateVectorWorldFrame frame =
                GhostMapPresence.ResolveStateVectorWorldPosition(focus, sample, null, playbackUT);
            Assert.True(frame.Resolved, frame.FailureReason);
            Assert.Equal("relative", frame.Branch);
            // The anchor climbs 2000 m a second from 420.
            Assert.Equal(600000.0 + (playbackUT - 420.0) * 2000.0 + 5.0, frame.WorldPos.x, 2);
            Assert.Equal(2.0, frame.WorldPos.y, 6);
            Assert.Equal(3.0, frame.WorldPos.z, 6);
        }

        [Fact]
        public void Map_AnchorWithNoPoseAtThePlaybackUT_IsNotSeededFromAStaleOne()
        {
            Recording focus = MapFocus(out TrajectoryPoint sample);
            GhostMapPresence.StateVectorWorldFrame frame =
                GhostMapPresence.ResolveStateVectorWorldPosition(focus, sample, null, 500.0);
            Assert.False(frame.Resolved);
            Assert.Equal("anchor-not-found", frame.FailureReason);
        }

        [Fact]
        public void Map_EveryCallerSeedsAtItsOwnPlaybackUT()
        {
            string root = SourceDir("Ghost");
            string create = Prepared(Path.Combine(root, "GhostMapPresence.StateVectors.cs"));
            Assert.Equal(2, Regex.Matches(create,
                @"ResolveStateVectorWorldPosition\(\s*traj,\s*point,\s*body,\s*ut,").Count);
            string overlap = Prepared(Path.Combine(root, "GhostMapPresence.FlightMapLifecycle.cs"));
            Assert.Matches(@"ResolveStateVectorWorldPosition\(\s*traj,\s*point,\s*body,\s*effUT,", overlap);
        }

        // ---- source helpers ----

        private static string SourceDir(string folder)
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return Path.Combine(repo, "Source", "Parsek", folder);
        }

        private static string Prepared(string path)
        {
            return SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
        }

        private static string MethodBody(string file, string signature)
        {
            string prepared = Prepared(Path.Combine(SourceDir("Controllers"), file));
            int sig = prepared.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(sig >= 0, "signature not found: " + signature);
            return SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sig));
        }
    }
}
