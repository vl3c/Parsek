using System;
using System.Collections.Generic;
using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RelativeAnchorResolverTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RelativeAnchorResolverTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void TryResolveAnchorPose_EmptyAnchorRecordingId_ReturnsFalseWithReason()
        {
            var context = MakeContext(new RecordingTree { Id = "tree" });

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                context,
                "",
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose);

            Assert.False(resolved);
            Assert.Equal(0.0, pose.WorldPos.x, 6);
            Assert.Equal(0.0, pose.WorldPos.y, 6);
            Assert.Equal(0.0, pose.WorldPos.z, 6);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=anchor-recording-id-missing"));
        }

        [Fact]
        public void TryResolveAnchorPose_MissingAnchorRecording_ReturnsFalseWithReason()
        {
            var context = MakeContext(new RecordingTree { Id = "tree" });

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                context,
                "missing-anchor",
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _);

            Assert.False(resolved);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=anchor-recording-not-found") &&
                l.Contains("anchorRecordingId=missing-anchor"));
        }

        [Fact]
        public void TryResolveAnchorPose_V11RelativeSectionMissingAnchorRecordingId_DoesNotUseLegacyPid()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording absolute = MakeAbsoluteRecording(
                "absolute-anchor",
                tree.Id,
                new Vector3d(10, 20, 30),
                new Vector3d(20, 20, 30));
            absolute.VesselPersistentId = 12345u;
            Recording relative = MakeRelativeRecording(
                "relative-child",
                tree.Id,
                localOffset: new Vector3d(1, 2, 3),
                legacyAnchorPid: 12345u);

            tree.AddOrReplaceRecording(absolute);
            tree.AddOrReplaceRecording(relative);

            var context = MakeContext(tree);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                context,
                relative.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _);

            Assert.False(resolved);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=anchor-recording-id-missing") &&
                l.Contains("recordingId=relative-child"));
            Assert.DoesNotContain(logLines, l => l.Contains("anchorPid=12345"));
        }

        [Fact]
        public void TryResolveAnchorPose_SingleLinkRelativeToAbsolute_ComposesPoseFromRecordedData()
        {
            var tree = new RecordingTree { Id = "tree" };
            Quaternion anchorRotation = RotationZDegrees(90.0);
            Recording absolute = MakeAbsoluteRecording(
                "absolute-anchor",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0),
                anchorRotation);
            Recording relative = MakeRelativeRecording(
                "relative-child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: absolute.RecordingId);

            tree.AddOrReplaceRecording(absolute);
            tree.AddOrReplaceRecording(relative);

            var context = MakeContext(tree);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                context,
                relative.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose);

            Assert.True(resolved);
            Assert.Equal(relative.RecordingId, pose.ResolvedRecordingId);
            Assert.Equal(0, pose.ResolvedSectionIndex);
            Assert.Equal(105.0, pose.WorldPos.x, 6);
            Assert.Equal(1.0, pose.WorldPos.y, 6);
            Assert.Equal(0.0, pose.WorldPos.z, 6);
            Assert.True(
                TrajectoryMath.ComputeQuaternionAngleDegrees(anchorRotation, pose.WorldRotation) < 0.01f,
                "relative identity rotation should compose to the anchor world rotation");
            Assert.DoesNotContain(logLines, l => l.Contains("relative-anchor-unresolved"));
        }

        private static RelativeAnchorResolverContext MakeContext(
            RecordingTree tree,
            Func<Recording, TrackSection, int, string> anchorRecordingIdResolver = null)
        {
            return new RelativeAnchorResolverContext(
                tree,
                focusRecordingId: null,
                focusTreeId: tree?.Id,
                sectionAnchorRecordingIdResolver: anchorRecordingIdResolver,
                absoluteWorldPositionResolver: p => new Vector3d(p.latitude, p.longitude, p.altitude),
                bodyWorldRotationResolver: p => Quaternion.identity);
        }

        private static Recording MakeAbsoluteRecording(
            string recordingId,
            string treeId,
            Vector3d startWorld,
            Vector3d endWorld,
            Quaternion? rotation = null)
        {
            Quaternion rot = rotation ?? Quaternion.identity;
            var rec = new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = RelativeAnchorResolver.RecordingAnchorChainFormatVersion,
                TreeId = treeId,
                VesselName = recordingId,
            };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 0.0,
                endUT = 10.0,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(0.0, startWorld, rot),
                    MakePoint(10.0, endWorld, rot),
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });
            return rec;
        }

        private static Recording MakeRelativeRecording(
            string recordingId,
            string treeId,
            Vector3d localOffset,
            string anchorRecordingId = null,
            uint legacyAnchorPid = 0u)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = RelativeAnchorResolver.RecordingAnchorChainFormatVersion,
                TreeId = treeId,
                VesselName = recordingId,
            };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 0.0,
                endUT = 10.0,
                anchorVesselId = legacyAnchorPid,
                anchorRecordingId = anchorRecordingId,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(0.0, localOffset, Quaternion.identity),
                    MakePoint(10.0, localOffset, Quaternion.identity),
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });
            return rec;
        }

        private static TrajectoryPoint MakePoint(double ut, Vector3d xyz, Quaternion rotation)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = xyz.x,
                longitude = xyz.y,
                altitude = xyz.z,
                bodyName = "Kerbin",
                rotation = rotation,
            };
        }

        private static Quaternion RotationZDegrees(double degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            double half = radians * 0.5;
            return new Quaternion(
                0f,
                0f,
                (float)Math.Sin(half),
                (float)Math.Cos(half));
        }
    }
}
