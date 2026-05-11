using System;
using System.Collections.Generic;
using System.Reflection;
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
                out AnchorPose pose,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.Equal(RelativeAnchorResolveOutcome.PreconditionFailed, failure.Outcome);
            Assert.Equal("anchor-recording-id-missing", failure.Reason);
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
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.Equal(RelativeAnchorResolveOutcome.AnchorRecordingNotFound, failure.Outcome);
            Assert.Equal("anchor-recording-not-found", failure.Reason);
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
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.Equal(RelativeAnchorResolveOutcome.Other, failure.Outcome);
            Assert.Equal("anchor-recording-id-missing", failure.Reason);
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
                out AnchorPose pose, out _);

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

        [Fact]
        public void TryResolveAnchorPose_SingleFrameRelativeSectionCoversSectionInterval()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording absolute = MakeAbsoluteRecording(
                "absolute-anchor",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0));
            Recording relative = MakeRelativeRecording(
                "relative-child",
                tree.Id,
                localOffset: new Vector3d(1, 2, 3),
                anchorRecordingId: absolute.RecordingId);
            TrackSection relativeSection = relative.TrackSections[0];
            relativeSection.frames = new List<TrajectoryPoint>
            {
                MakePoint(0.0, new Vector3d(1, 2, 3), Quaternion.identity),
            };
            relative.TrackSections[0] = relativeSection;

            tree.AddOrReplaceRecording(absolute);
            tree.AddOrReplaceRecording(relative);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                relative.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose, out _);

            Assert.True(resolved);
            Assert.Equal(relative.RecordingId, pose.ResolvedRecordingId);
            Assert.Equal(0, pose.ResolvedSectionIndex);
            Assert.Equal(106.0, pose.WorldPos.x, 6);
            Assert.Equal(2.0, pose.WorldPos.y, 6);
            Assert.Equal(3.0, pose.WorldPos.z, 6);
        }

        [Fact]
        public void TryResolveAnchorPose_SingleFrameAbsoluteSectionCoversSectionInterval()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording absolute = MakeAbsoluteRecording(
                "absolute-anchor",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0));
            TrackSection absoluteSection = absolute.TrackSections[0];
            absoluteSection.frames = new List<TrajectoryPoint>
            {
                MakePoint(0.0, new Vector3d(100, 0, 0), Quaternion.identity),
            };
            absolute.TrackSections[0] = absoluteSection;
            Recording relative = MakeRelativeRecording(
                "relative-child",
                tree.Id,
                localOffset: new Vector3d(1, 2, 3),
                anchorRecordingId: absolute.RecordingId);

            tree.AddOrReplaceRecording(absolute);
            tree.AddOrReplaceRecording(relative);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                relative.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose, out _);

            Assert.True(resolved);
            Assert.Equal(relative.RecordingId, pose.ResolvedRecordingId);
            Assert.Equal(101.0, pose.WorldPos.x, 6);
            Assert.Equal(2.0, pose.WorldPos.y, 6);
            Assert.Equal(3.0, pose.WorldPos.z, 6);
        }

        [Fact]
        public void TryResolveAnchorPose_AbsoluteSectionFrameGap_UsesFlatFallbackCoverage()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording absolute = MakeAbsoluteRecording(
                "absolute-anchor",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0),
                startUT: 0.0,
                endUT: 10.0);

            var p0 = MakePoint(0.0, new Vector3d(100, 0, 0), Quaternion.identity);
            var p10 = MakePoint(10.0, new Vector3d(110, 0, 0), Quaternion.identity);
            var p20 = MakePoint(20.0, new Vector3d(120, 0, 0), Quaternion.identity);
            TrackSection absoluteSection = absolute.TrackSections[0];
            absoluteSection.endUT = 20.0;
            absoluteSection.frames = new List<TrajectoryPoint> { p0, p10 };
            absolute.TrackSections[0] = absoluteSection;
            absolute.Points.Add(p0);
            absolute.Points.Add(p10);
            absolute.Points.Add(p20);

            Recording relative = MakeRelativeRecording(
                "relative-child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: absolute.RecordingId,
                startUT: 10.0,
                endUT: 20.0);

            tree.AddOrReplaceRecording(absolute);
            tree.AddOrReplaceRecording(relative);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                relative.RecordingId,
                15.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose, out _);

            Assert.True(resolved);
            Assert.Equal(relative.RecordingId, pose.ResolvedRecordingId);
            Assert.Equal(116.0, pose.WorldPos.x, 6);
            Assert.Equal(0.0, pose.WorldPos.y, 6);
            Assert.Equal(0.0, pose.WorldPos.z, 6);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("Absolute section anchor pose fell back to flat trajectory coverage") &&
                l.Contains("recordingId=absolute-anchor"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=anchor-out-of-recorded-range") &&
                l.Contains("recordingId=absolute-anchor"));
        }

        [Fact]
        public void TryResolveAnchorPose_AnchorSectionGap_UsesLocalFlatPointFallback()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteGapAnchor(
                "absolute-anchor",
                tree.Id,
                gapStartUT: 10.0,
                gapEndUT: 10.04);
            Recording child = MakeRelativeRecording(
                "relative-child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: anchor.RecordingId,
                startUT: 10.0,
                endUT: 20.0);

            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                10.02,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose, out _);

            Assert.True(resolved);
            Assert.Equal(child.RecordingId, pose.ResolvedRecordingId);
            Assert.Equal(111.02, pose.WorldPos.x, 6);
            Assert.Equal(0.0, pose.WorldPos.y, 6);
            Assert.Equal(0.0, pose.WorldPos.z, 6);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("small section gap") &&
                l.Contains("recordingId=absolute-anchor") &&
                l.Contains("previousSectionIndex=0") &&
                l.Contains("nextSectionIndex=1"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("relative-anchor-unresolved") &&
                l.Contains("recordingId=absolute-anchor"));
        }

        [Fact]
        public void TryResolveAnchorPose_AnchorSectionGap_WideGapFailsClosed()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteGapAnchor(
                "absolute-anchor",
                tree.Id,
                gapStartUT: 10.0,
                gapEndUT: 10.50);
            Recording child = MakeRelativeRecording(
                "relative-child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: anchor.RecordingId,
                startUT: 10.0,
                endUT: 20.0);

            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                10.25,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.Equal(RelativeAnchorResolveOutcome.NoSectionAtUT, failure.Outcome);
            Assert.Equal("anchor-out-of-recorded-range", failure.Reason);
            Assert.Equal(-1, failure.SectionIndex);
            Assert.Equal(0.0, failure.RangeStartUT, 6);
            Assert.Equal(20.0, failure.RangeEndUT, 6);
            Assert.DoesNotContain(logLines, l => l.Contains("small section gap"));
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=anchor-out-of-recorded-range") &&
                l.Contains("recordingId=absolute-anchor"));
        }

        [Fact]
        public void TryResolveAnchorPose_AnchorSectionGap_WithoutLocalFlatBracketFailsClosed()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteGapAnchor(
                "absolute-anchor",
                tree.Id,
                gapStartUT: 10.0,
                gapEndUT: 10.04,
                includeFlatPoints: false);
            anchor.Points.Add(MakePoint(0.0, GapWorld(100.0, 0.0), Quaternion.identity));
            anchor.Points.Add(MakePoint(20.0, GapWorld(100.0, 20.0), Quaternion.identity));
            Recording child = MakeRelativeRecording(
                "relative-child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: anchor.RecordingId,
                startUT: 10.0,
                endUT: 20.0);

            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                10.02,
                new HashSet<string>(StringComparer.Ordinal),
                out _, out _);

            Assert.False(resolved);
            Assert.DoesNotContain(logLines, l => l.Contains("small section gap"));
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=anchor-out-of-recorded-range") &&
                l.Contains("recordingId=absolute-anchor"));
        }

        [Fact]
        public void TryResolveAnchorPose_RelativeAnchorSectionGap_UsesAbsoluteShadowFallback()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeRelativeGapAnchor(
                "relative-anchor",
                tree.Id,
                includeAbsoluteShadows: true);
            Recording child = MakeRelativeRecording(
                "relative-child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: anchor.RecordingId,
                startUT: 10.0,
                endUT: 20.0);

            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                10.02,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose, out _);

            Assert.True(resolved);
            Assert.Equal(child.RecordingId, pose.ResolvedRecordingId);
            Assert.Equal(211.02, pose.WorldPos.x, 6);
            Assert.Equal(0.0, pose.WorldPos.y, 6);
            Assert.Equal(0.0, pose.WorldPos.z, 6);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("small section gap") &&
                l.Contains("recordingId=relative-anchor"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("relative-anchor-unresolved") &&
                l.Contains("recordingId=relative-anchor"));
        }

        [Fact]
        public void TryResolveAnchorPose_RelativeAnchorSectionGap_NonFiniteShadowUTFailsClosed()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeRelativeGapAnchor(
                "relative-anchor",
                tree.Id,
                includeAbsoluteShadows: true);
            anchor.TrackSections[0].bodyFixedFrames.Insert(
                1,
                MakePoint(double.NaN, GapWorld(200.0, 10.01), Quaternion.identity));
            Recording child = MakeRelativeRecording(
                "relative-child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: anchor.RecordingId,
                startUT: 10.0,
                endUT: 20.0);

            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                10.02,
                new HashSet<string>(StringComparer.Ordinal),
                out _, out _);

            Assert.False(resolved);
            Assert.DoesNotContain(logLines, l => l.Contains("small section gap"));
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=anchor-out-of-recorded-range") &&
                l.Contains("recordingId=relative-anchor"));
        }

        [Fact]
        public void TryResolveAnchorPose_RelativeAnchorSectionGap_WithoutAbsoluteShadowFailsClosed()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeRelativeGapAnchor(
                "relative-anchor",
                tree.Id,
                includeAbsoluteShadows: false);
            Recording child = MakeRelativeRecording(
                "relative-child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: anchor.RecordingId,
                startUT: 10.0,
                endUT: 20.0);

            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                10.02,
                new HashSet<string>(StringComparer.Ordinal),
                out _, out _);

            Assert.False(resolved);
            Assert.DoesNotContain(logLines, l => l.Contains("small section gap"));
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=anchor-out-of-recorded-range") &&
                l.Contains("recordingId=relative-anchor"));
        }

        [Fact]
        public void TryResolveAnchorPose_EndPlusTinyEpsilon_UsesResolverSectionSeamTolerance()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteRecording(
                "absolute-anchor",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0),
                startUT: 0.0,
                endUT: 10.0);
            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: anchor.RecordingId,
                startUT: 0.0,
                endUT: 10.0);
            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                10.0 + 1e-12,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose,
                out _);

            Assert.True(resolved);
            Assert.Equal(child.RecordingId, pose.ResolvedRecordingId);
            Assert.Equal(111.0, pose.WorldPos.x, 6);
            Assert.DoesNotContain(logLines, l => l.Contains("relative-anchor-terminal-clamp"));
        }

        [Fact]
        public void TryResolveAnchorPose_AnchorOnePhysicsTickPastTerminalSection_ClampsToFinalPlayableFrameAndLogs()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeTerminalEdgeAbsoluteRecording(
                "terminal-anchor",
                tree.Id,
                sectionEndUT: 10.0,
                terminalPlayableUT: 9.98,
                terminalWorld: new Vector3d(109.98, 0, 0));
            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: anchor.RecordingId,
                startUT: 0.0,
                endUT: 20.0);
            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(child);

            double requestedUT = 10.0 + RelativeAnchorResolver.TerminalClampPhysicsTickSeconds;
            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                requestedUT,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose,
                out _);

            Assert.True(resolved);
            Assert.Equal(child.RecordingId, pose.ResolvedRecordingId);
            Assert.Equal(110.98, pose.WorldPos.x, 6);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("relative-anchor-terminal-clamp") &&
                l.Contains("recordingId=terminal-anchor") &&
                l.Contains("sectionIndex=0") &&
                l.Contains("clampedUT=9.98") &&
                l.Contains("sectionEndUT=10") &&
                l.Contains("terminalPlayableUT=9.98") &&
                l.Contains("thresholdSeconds="));
        }

        [Fact]
        public void TryResolveAnchorPose_RelativeTerminalClamp_ReentersRelativeAnchorUnderVisitedGuard()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording root = MakeTerminalEdgeAbsoluteRecording(
                "root-terminal",
                tree.Id,
                sectionEndUT: 10.0,
                terminalPlayableUT: 9.98,
                terminalWorld: new Vector3d(109.98, 0, 0));
            Recording mid = MakeTerminalEdgeRelativeRecording(
                "mid-terminal",
                tree.Id,
                localOffset: new Vector3d(2, 0, 0),
                anchorRecordingId: root.RecordingId,
                sectionEndUT: 10.0,
                terminalPlayableUT: 9.98);
            Recording outer = MakeTerminalEdgeRelativeRecording(
                "outer-terminal",
                tree.Id,
                localOffset: new Vector3d(3, 0, 0),
                anchorRecordingId: mid.RecordingId,
                sectionEndUT: 10.0,
                terminalPlayableUT: 9.98);
            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(4, 0, 0),
                anchorRecordingId: outer.RecordingId,
                startUT: 0.0,
                endUT: 20.0);
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(mid);
            tree.AddOrReplaceRecording(outer);
            tree.AddOrReplaceRecording(child);

            double requestedUT = 10.0 + RelativeAnchorResolver.TerminalClampPhysicsTickSeconds;
            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                requestedUT,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose,
                out _);

            Assert.True(resolved);
            Assert.Equal(child.RecordingId, pose.ResolvedRecordingId);
            Assert.Equal(118.98, pose.WorldPos.x, 6);
            Assert.Equal(0.0, pose.WorldPos.y, 6);
            Assert.Equal(0.0, pose.WorldPos.z, 6);
            Assert.Single(logLines.FindAll(l => l.Contains("relative-anchor-terminal-clamp")));
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("relative-anchor-terminal-clamp") &&
                l.Contains("recordingId=outer-terminal") &&
                l.Contains("anchorRecordingId=mid-terminal") &&
                l.Contains("clampedUT=9.98"));
            Assert.DoesNotContain(logLines, l => l.Contains("anchor-cycle-detected"));
        }

        [Fact]
        public void TryResolveAnchorPose_TerminalClampThresholdBoundary_PassesThenFailsClosed()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeTerminalEdgeAbsoluteRecording(
                "terminal-anchor",
                tree.Id,
                sectionEndUT: 10.0,
                terminalPlayableUT: 10.0,
                terminalWorld: new Vector3d(110, 0, 0));
            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: anchor.RecordingId,
                startUT: 0.0,
                endUT: 20.0);
            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(child);

            double threshold = RelativeAnchorResolver.TerminalClampThresholdSeconds;
            double smallDelta = 1e-7;
            bool resolvedInside = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                10.0 + threshold - smallDelta,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose,
                out _);

            Assert.True(resolvedInside);
            Assert.Equal(111.0, pose.WorldPos.x, 6);
            Assert.Contains(logLines, l => l.Contains("relative-anchor-terminal-clamp"));

            logLines.Clear();
            ParsekLog.ResetRateLimitsForTesting();

            bool resolvedOutside = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                10.0 + threshold + smallDelta,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolvedOutside);
            Assert.Equal(RelativeAnchorResolveOutcome.NoSectionAtUT, failure.Outcome);
            Assert.Equal("anchor-out-of-recorded-range", failure.Reason);
            Assert.DoesNotContain(logLines, l => l.Contains("relative-anchor-terminal-clamp"));
        }

        [Fact]
        public void TryResolveAnchorPose_AnchorPastTerminalClampWindow_FailsClosed()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeTerminalEdgeAbsoluteRecording(
                "terminal-anchor",
                tree.Id,
                sectionEndUT: 10.0,
                terminalPlayableUT: 10.0,
                terminalWorld: new Vector3d(110, 0, 0));
            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: anchor.RecordingId,
                startUT: 0.0,
                endUT: 20.0);
            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                10.2,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.Equal(RelativeAnchorResolveOutcome.NoSectionAtUT, failure.Outcome);
            Assert.Equal("anchor-out-of-recorded-range", failure.Reason);
            Assert.DoesNotContain(logLines, l => l.Contains("relative-anchor-terminal-clamp"));
        }

        [Fact]
        public void TryResolveAnchorPose_SameChainSuccessorAtTerminalEdge_WinsBeforePredecessorClamp()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording firstHalf = MakeTerminalEdgeAbsoluteRecording(
                "first-half",
                tree.Id,
                sectionEndUT: 10.0,
                terminalPlayableUT: 9.98,
                terminalWorld: new Vector3d(109.98, 0, 0));
            firstHalf.ChainId = "chain-parent";
            firstHalf.ChainIndex = 0;

            Recording secondHalf = MakeAbsoluteRecording(
                "second-half",
                tree.Id,
                new Vector3d(200, 0, 0),
                new Vector3d(210, 0, 0),
                startUT: 10.0,
                endUT: 20.0);
            secondHalf.ChainId = firstHalf.ChainId;
            secondHalf.ChainIndex = 1;

            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: firstHalf.RecordingId,
                startUT: 0.0,
                endUT: 20.0);
            tree.AddOrReplaceRecording(firstHalf);
            tree.AddOrReplaceRecording(secondHalf);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                10.0 + RelativeAnchorResolver.TerminalClampPhysicsTickSeconds,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose,
                out _);

            Assert.True(resolved);
            Assert.Equal(201.02, pose.WorldPos.x, 5);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("Anchor recording continued through same-chain successor") &&
                l.Contains("recordingId=first-half") &&
                l.Contains("successorRecordingId=second-half"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("relative-anchor-terminal-clamp") &&
                l.Contains("recordingId=first-half"));
        }

                        [Fact]
        public void TryResolveAnchorPose_TwoLinkRelativeChain_ComposesThroughRecordedAnchors()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording root = MakeAbsoluteRecording(
                "root",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0));
            Recording mid = MakeRelativeRecording(
                "mid",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: root.RecordingId);
            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(0, 2, 0),
                anchorRecordingId: mid.RecordingId);

            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(mid);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose, out _);

            Assert.True(resolved);
            Assert.Equal(child.RecordingId, pose.ResolvedRecordingId);
            Assert.Equal(106.0, pose.WorldPos.x, 6);
            Assert.Equal(2.0, pose.WorldPos.y, 6);
            Assert.Equal(0.0, pose.WorldPos.z, 6);
        }

        [Fact]
        public void TryResolveAnchorPose_AnchorPastOptimizerSplit_UsesSameChainSuccessor()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording firstHalf = MakeAbsoluteRecording(
                "first-half",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0),
                startUT: 0.0,
                endUT: 10.0);
            firstHalf.ChainId = "chain-parent";
            firstHalf.ChainIndex = 0;

            Recording secondHalf = MakeAbsoluteRecording(
                "second-half",
                tree.Id,
                new Vector3d(110, 0, 0),
                new Vector3d(120, 0, 0),
                startUT: 10.0,
                endUT: 20.0);
            secondHalf.ChainId = firstHalf.ChainId;
            secondHalf.ChainIndex = 1;

            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: firstHalf.RecordingId,
                startUT: 10.0,
                endUT: 20.0);

            tree.AddOrReplaceRecording(firstHalf);
            tree.AddOrReplaceRecording(secondHalf);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                12.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose, out _);

            Assert.True(resolved);
            Assert.Equal(child.RecordingId, pose.ResolvedRecordingId);
            Assert.Equal(113.0, pose.WorldPos.x, 6);
            Assert.Equal(0.0, pose.WorldPos.y, 6);
            Assert.Equal(0.0, pose.WorldPos.z, 6);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("Anchor recording continued through same-chain successor") &&
                l.Contains("recordingId=first-half") &&
                l.Contains("successorRecordingId=second-half"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("reason=anchor-out-of-recorded-range") &&
                l.Contains("recordingId=first-half"));
        }

        [Fact]
        public void TryResolveAnchorPose_AnchorPastOptimizerSplit_PendingSuccessorOverridesStaleFocusTree()
        {
            var focusTree = new RecordingTree { Id = "tree" };
            var pendingTree = new RecordingTree { Id = "tree" };
            Recording firstHalf = MakeAbsoluteRecording(
                "first-half",
                focusTree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0),
                startUT: 0.0,
                endUT: 10.0);
            firstHalf.ChainId = "chain-parent";
            firstHalf.ChainIndex = 0;

            Recording staleSecondHalf = MakeAbsoluteRecording(
                "second-half",
                focusTree.Id,
                new Vector3d(1000, 0, 0),
                new Vector3d(1010, 0, 0),
                startUT: 10.0,
                endUT: 20.0);
            staleSecondHalf.ChainId = firstHalf.ChainId;
            staleSecondHalf.ChainIndex = 1;

            Recording pendingSecondHalf = MakeAbsoluteRecording(
                "second-half",
                pendingTree.Id,
                new Vector3d(110, 0, 0),
                new Vector3d(120, 0, 0),
                startUT: 10.0,
                endUT: 20.0);
            pendingSecondHalf.ChainId = firstHalf.ChainId;
            pendingSecondHalf.ChainIndex = 1;

            Recording child = MakeRelativeRecording(
                "child",
                focusTree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: firstHalf.RecordingId,
                startUT: 10.0,
                endUT: 20.0);

            focusTree.AddOrReplaceRecording(firstHalf);
            focusTree.AddOrReplaceRecording(staleSecondHalf);
            focusTree.AddOrReplaceRecording(child);
            pendingTree.AddOrReplaceRecording(pendingSecondHalf);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(focusTree, pendingTree: pendingTree),
                child.RecordingId,
                12.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose, out _);

            Assert.True(resolved);
            Assert.Equal(113.0, pose.WorldPos.x, 6);
            Assert.Equal(0.0, pose.WorldPos.y, 6);
            Assert.Equal(0.0, pose.WorldPos.z, 6);
        }

        [Fact]
        public void TryResolveAnchorPose_AnchorPastOptimizerSplit_ChoosesFirstChronologicalContinuation()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording firstHalf = MakeAbsoluteRecording(
                "first-half",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0),
                startUT: 0.0,
                endUT: 10.0);
            firstHalf.ChainId = "chain-parent";
            firstHalf.ChainIndex = 0;

            Recording earlierContinuation = MakeAbsoluteRecording(
                "earlier-continuation",
                tree.Id,
                new Vector3d(110, 0, 0),
                new Vector3d(120, 0, 0),
                startUT: 10.0,
                endUT: 20.0);
            earlierContinuation.ChainId = firstHalf.ChainId;
            earlierContinuation.ChainIndex = 1;

            Recording laterContinuation = MakeAbsoluteRecording(
                "later-continuation",
                tree.Id,
                new Vector3d(1010, 0, 0),
                new Vector3d(1020, 0, 0),
                startUT: 10.0,
                endUT: 20.0);
            laterContinuation.ChainId = firstHalf.ChainId;
            laterContinuation.ChainIndex = 2;

            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: firstHalf.RecordingId,
                startUT: 10.0,
                endUT: 20.0);

            tree.AddOrReplaceRecording(firstHalf);
            tree.AddOrReplaceRecording(laterContinuation);
            tree.AddOrReplaceRecording(earlierContinuation);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                12.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose, out _);

            Assert.True(resolved);
            Assert.Equal(113.0, pose.WorldPos.x, 6);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("successorRecordingId=earlier-continuation"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("successorRecordingId=later-continuation"));
        }

        [Fact]
        public void TryResolveAnchorPose_V6OrNewerWithoutTrackSections_FailsClosed()
        {
            var tree = new RecordingTree { Id = "tree" };
            var recording = new Recording
            {
                RecordingId = "sectionless-v11",
                RecordingFormatVersion = RecordingStore.RecordingAnchorChainFormatVersion,
                TreeId = tree.Id,
                VesselName = "sectionless-v11",
            };
            recording.Points.Add(MakePoint(5.0, new Vector3d(1, 2, 3), Quaternion.identity));
            tree.AddOrReplaceRecording(recording);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                recording.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _, out _);

            Assert.False(resolved);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=anchor-track-sections-missing") &&
                l.Contains("recordingId=sectionless-v11"));
        }

        [Fact]
        public void TryResolveAnchorPose_ActiveReFlyAnchorWithFrozenSnapshot_UsesSnapshot()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording active = MakeAbsoluteRecording(
                "active-refly",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0));
            active.CapturePreReFlyAnchorTrajectory("session-a");

            Recording mutatedActive = MakeAbsoluteRecording(
                active.RecordingId,
                tree.Id,
                new Vector3d(900, 0, 0),
                new Vector3d(910, 0, 0));
            active.TrackSections.Clear();
            active.TrackSections.Add(mutatedActive.TrackSections[0]);

            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: active.RecordingId);
            tree.AddOrReplaceRecording(active);
            tree.AddOrReplaceRecording(child);

            var marker = new ReFlySessionMarker
            {
                SessionId = "session-a",
                TreeId = tree.Id,
                ActiveReFlyRecordingId = active.RecordingId,
                OriginChildRecordingId = active.RecordingId,
            };

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree, marker: marker),
                child.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose, out _);

            Assert.True(resolved);
            Assert.Equal(child.RecordingId, pose.ResolvedRecordingId);
            Assert.Equal(106.0, pose.WorldPos.x, 6);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("Using frozen pre-Re-Fly anchor trajectory") &&
                l.Contains("recordingId=active-refly"));
        }

        [Fact]
        public void TryResolveAnchorPose_ActiveReFlyAnchorWithoutFrozenSnapshot_ReturnsFalse()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording active = MakeAbsoluteRecording(
                "active-refly",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0));
            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: active.RecordingId);
            tree.AddOrReplaceRecording(active);
            tree.AddOrReplaceRecording(child);

            var marker = new ReFlySessionMarker
            {
                SessionId = "session-a",
                TreeId = tree.Id,
                ActiveReFlyRecordingId = active.RecordingId,
                OriginChildRecordingId = active.RecordingId,
            };

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree, marker: marker),
                child.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _, out _);

            Assert.False(resolved);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=active-provisional-out-of-scope") &&
                l.Contains("anchorRecordingId=active-refly"));
        }

        [Fact]
        public void TryResolveAnchorPose_Cycle_ReturnsFalseWithReason()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording a = MakeRelativeRecording(
                "a",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: "b");
            Recording b = MakeRelativeRecording(
                "b",
                tree.Id,
                localOffset: new Vector3d(0, 1, 0),
                anchorRecordingId: "a");
            tree.AddOrReplaceRecording(a);
            tree.AddOrReplaceRecording(b);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                a.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _, out _);

            Assert.False(resolved);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=anchor-cycle-detected"));
        }

        [Fact]
        public void TryResolveAnchorPose_LoopRootedAnchor_ReturnsFalse()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording loopRoot = MakeAbsoluteRecording(
                "loop-root",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0));
            loopRoot.LoopPlayback = true;
            loopRoot.LoopAnchorVesselId = 42u;
            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: loopRoot.RecordingId);
            tree.AddOrReplaceRecording(loopRoot);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _, out _);

            Assert.False(resolved);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=loop-anchor-out-of-scope"));
        }

        [Fact]
        public void TryResolveAnchorPose_ProvisionalAnchorOverridesCommittedFocusTree()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording committedAnchor = MakeAbsoluteRecording(
                "anchor",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0));
            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: committedAnchor.RecordingId);
            tree.AddOrReplaceRecording(committedAnchor);
            tree.AddOrReplaceRecording(child);

            Recording provisionalAnchor = MakeAbsoluteRecording(
                committedAnchor.RecordingId,
                tree.Id,
                new Vector3d(200, 0, 0),
                new Vector3d(210, 0, 0));
            var provisional = new Dictionary<string, Recording>(StringComparer.Ordinal)
            {
                { provisionalAnchor.RecordingId, provisionalAnchor },
            };

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree, provisionalRecordings: provisional),
                child.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose,
                out RelativeAnchorResolveFailure failure);

            Assert.True(resolved, failure.Reason);
            Assert.Equal(206.0, pose.WorldPos.x, 6);
        }

        [Fact]
        public void TryResolveAnchorPose_ProvisionalFocusDebrisAllowsLoopRootedAnchor()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording loopRoot = MakeAbsoluteRecording(
                "loop-root",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0));
            loopRoot.LoopPlayback = true;
            loopRoot.LoopAnchorVesselId = 42u;
            Recording committedChild = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: loopRoot.RecordingId);
            committedChild.IsDebris = false;
            tree.AddOrReplaceRecording(loopRoot);
            tree.AddOrReplaceRecording(committedChild);

            Recording provisionalChild = MakeRelativeRecording(
                committedChild.RecordingId,
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: loopRoot.RecordingId);
            provisionalChild.IsDebris = true;
            provisionalChild.DebrisParentRecordingId = loopRoot.RecordingId;
            var provisional = new Dictionary<string, Recording>(StringComparer.Ordinal)
            {
                { provisionalChild.RecordingId, provisionalChild },
            };

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(
                    tree,
                    focusRecordingId: provisionalChild.RecordingId,
                    provisionalRecordings: provisional),
                provisionalChild.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose,
                out RelativeAnchorResolveFailure failure);

            Assert.True(resolved, failure.Reason);
            Assert.Equal(106.0, pose.WorldPos.x, 6);
        }

        [Fact]
        public void TryResolveAnchorPose_DebrisFocusAllowsLiveAnchorLeaf()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording loopRoot = MakeRelativeRecording(
                "loop-root",
                tree.Id,
                localOffset: new Vector3d(5, 0, 0),
                legacyAnchorPid: 42u);
            loopRoot.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            loopRoot.LoopPlayback = true;
            loopRoot.LoopAnchorVesselId = 42u;
            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 2, 3),
                anchorRecordingId: loopRoot.RecordingId);
            child.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            child.IsDebris = true;
            child.DebrisParentRecordingId = loopRoot.RecordingId;
            tree.AddOrReplaceRecording(loopRoot);
            tree.AddOrReplaceRecording(child);

            bool callbackInvoked = false;
            var context = MakeContext(
                tree,
                focusRecordingId: child.RecordingId,
                liveAnchorTransformResolver: (pid, victimRecordingId, ut) =>
                {
                    callbackInvoked = true;
                    Assert.Equal(42u, pid);
                    Assert.Equal(loopRoot.RecordingId, victimRecordingId);
                    Assert.Equal(5.0, ut);
                    return (new Vector3d(100, 0, 0), Quaternion.identity);
                });

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                context,
                child.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose,
                out RelativeAnchorResolveFailure failure);

            Assert.True(resolved, failure.Reason);
            Assert.True(callbackInvoked);
            Assert.Equal(106.0, pose.WorldPos.x, 6);
            Assert.Equal(2.0, pose.WorldPos.y, 6);
            Assert.Equal(3.0, pose.WorldPos.z, 6);
        }

        [Fact]
        public void TryResolveAnchorPose_DebrisFocusUsesRecordingLoopAnchorWhenSectionPidEmpty()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording loopRoot = MakeRelativeRecording(
                "loop-root",
                tree.Id,
                localOffset: new Vector3d(5, 0, 0));
            loopRoot.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            loopRoot.LoopPlayback = true;
            loopRoot.LoopAnchorVesselId = 42u;
            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 2, 3),
                anchorRecordingId: loopRoot.RecordingId);
            child.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            child.IsDebris = true;
            child.DebrisParentRecordingId = loopRoot.RecordingId;
            tree.AddOrReplaceRecording(loopRoot);
            tree.AddOrReplaceRecording(child);

            bool callbackInvoked = false;
            var context = MakeContext(
                tree,
                focusRecordingId: child.RecordingId,
                liveAnchorTransformResolver: (pid, victimRecordingId, ut) =>
                {
                    callbackInvoked = true;
                    Assert.Equal(42u, pid);
                    Assert.Equal(loopRoot.RecordingId, victimRecordingId);
                    Assert.Equal(5.0, ut);
                    return (new Vector3d(100, 0, 0), Quaternion.identity);
                });

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                context,
                child.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose,
                out RelativeAnchorResolveFailure failure);

            Assert.True(resolved, failure.Reason);
            Assert.True(callbackInvoked);
            Assert.Equal(106.0, pose.WorldPos.x, 6);
            Assert.Equal(2.0, pose.WorldPos.y, 6);
            Assert.Equal(3.0, pose.WorldPos.z, 6);
        }

        [Fact]
        public void TryResolveAnchorPose_DebrisCascadeFocusAllowsLoopAnchoredAncestorLeaf()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording loopRoot = MakeRelativeRecording(
                "loop-root",
                tree.Id,
                localOffset: new Vector3d(5, 0, 0),
                legacyAnchorPid: 42u);
            loopRoot.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            loopRoot.LoopPlayback = true;
            loopRoot.LoopAnchorVesselId = 42u;
            Recording firstDebris = MakeRelativeRecording(
                "first-debris",
                tree.Id,
                localOffset: new Vector3d(2, 3, 0),
                anchorRecordingId: loopRoot.RecordingId);
            firstDebris.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            firstDebris.IsDebris = true;
            firstDebris.DebrisParentRecordingId = loopRoot.RecordingId;
            Recording secondDebris = MakeRelativeRecording(
                "second-debris",
                tree.Id,
                localOffset: new Vector3d(1, 0, 4),
                anchorRecordingId: firstDebris.RecordingId);
            secondDebris.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            secondDebris.IsDebris = true;
            secondDebris.DebrisParentRecordingId = firstDebris.RecordingId;
            tree.AddOrReplaceRecording(loopRoot);
            tree.AddOrReplaceRecording(firstDebris);
            tree.AddOrReplaceRecording(secondDebris);

            bool callbackInvoked = false;
            var context = MakeContext(
                tree,
                focusRecordingId: secondDebris.RecordingId,
                liveAnchorTransformResolver: (pid, victimRecordingId, ut) =>
                {
                    callbackInvoked = true;
                    Assert.Equal(42u, pid);
                    Assert.Equal(loopRoot.RecordingId, victimRecordingId);
                    Assert.Equal(5.0, ut);
                    return (new Vector3d(100, 0, 0), Quaternion.identity);
                });

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                context,
                secondDebris.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose,
                out RelativeAnchorResolveFailure failure);

            Assert.True(resolved, failure.Reason);
            Assert.True(callbackInvoked);
            Assert.Equal(108.0, pose.WorldPos.x, 6);
            Assert.Equal(3.0, pose.WorldPos.y, 6);
            Assert.Equal(4.0, pose.WorldPos.z, 6);
        }

        [Fact]
        public void TryResolveAnchorPose_DebrisFocusLiveAnchorLeafNull_ReturnsLoopLiveAnchorUnresolved()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording loopRoot = MakeRelativeRecording(
                "loop-root",
                tree.Id,
                localOffset: new Vector3d(5, 0, 0),
                legacyAnchorPid: 42u);
            loopRoot.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            loopRoot.LoopPlayback = true;
            loopRoot.LoopAnchorVesselId = 42u;
            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 2, 3),
                anchorRecordingId: loopRoot.RecordingId);
            child.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            child.IsDebris = true;
            child.DebrisParentRecordingId = loopRoot.RecordingId;
            tree.AddOrReplaceRecording(loopRoot);
            tree.AddOrReplaceRecording(child);

            bool callbackInvoked = false;
            var context = MakeContext(
                tree,
                focusRecordingId: child.RecordingId,
                liveAnchorTransformResolver: (pid, victimRecordingId, ut) =>
                {
                    callbackInvoked = true;
                    Assert.Equal(42u, pid);
                    Assert.Equal(loopRoot.RecordingId, victimRecordingId);
                    Assert.Equal(5.0, ut);
                    return null;
                });

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                context,
                child.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.True(callbackInvoked);
            Assert.Equal(RelativeAnchorResolveOutcome.AnchorOutOfScope, failure.Outcome);
            Assert.Equal("loop-live-anchor-unresolved", failure.Reason);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=loop-live-anchor-unresolved") &&
                l.Contains("recordingId=loop-root"));
        }

        [Fact]
        public void BuildFlightRelativeAnchorResolverContext_PopulatesSharedLiveAnchorCallback()
        {
            MethodInfo method = typeof(ParsekFlight).GetMethod(
                "BuildFlightRelativeAnchorResolverContext",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var tree = new RecordingTree { Id = "tree" };
            var context = (RelativeAnchorResolverContext)method.Invoke(
                null,
                new object[] { tree, "focus-rec", null });

            Assert.NotNull(context.TryResolveLiveAnchorTransform);
            Assert.Same(
                ParsekFlight.TryGetLiveAnchorTransformDelegate(),
                context.TryResolveLiveAnchorTransform);
        }

        [Fact]
        public void TryResolveAnchorPose_NonDebrisFocusRejectsLiveAnchorLeaf()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording livePidRelative = MakeRelativeRecording(
                "live-pid-relative",
                tree.Id,
                localOffset: new Vector3d(5, 0, 0),
                legacyAnchorPid: 42u);
            livePidRelative.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 2, 3),
                anchorRecordingId: livePidRelative.RecordingId);
            child.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            child.IsDebris = false;
            tree.AddOrReplaceRecording(livePidRelative);
            tree.AddOrReplaceRecording(child);

            bool callbackInvoked = false;
            var context = MakeContext(
                tree,
                focusRecordingId: child.RecordingId,
                liveAnchorTransformResolver: (pid, victimRecordingId, ut) =>
                {
                    callbackInvoked = true;
                    return (new Vector3d(100, 0, 0), Quaternion.identity);
                });

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                context,
                child.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.False(callbackInvoked);
            Assert.Equal(RelativeAnchorResolveOutcome.Other, failure.Outcome);
            Assert.Equal("anchor-recording-id-missing", failure.Reason);
        }

        [Fact]
        public void TryResolveAnchorPose_PendingTreeOutsideFocusScope_ReturnsFalse()
        {
            var focusTree = new RecordingTree { Id = "focus-tree" };
            var pendingTree = new RecordingTree { Id = "other-tree" };
            Recording child = MakeRelativeRecording(
                "child",
                focusTree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: "pending-anchor");
            Recording pendingAnchor = MakeAbsoluteRecording(
                "pending-anchor",
                pendingTree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0));
            focusTree.AddOrReplaceRecording(child);
            pendingTree.AddOrReplaceRecording(pendingAnchor);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(focusTree, pendingTree: pendingTree),
                child.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _, out _);

            Assert.False(resolved);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=anchor-cross-tree-out-of-scope"));
        }

                                                [Fact]
        public void TryResolveAnchorPose_PendingTreeOutsideFocusScope_ReturnsAnchorOutOfScopeFailure()
        {
            var focusTree = new RecordingTree { Id = "focus-tree" };
            var pendingTree = new RecordingTree { Id = "other-tree" };
            Recording child = MakeRelativeRecording(
                "child",
                focusTree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: "pending-anchor");
            Recording pendingAnchor = MakeAbsoluteRecording(
                "pending-anchor",
                pendingTree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0));
            focusTree.AddOrReplaceRecording(child);
            pendingTree.AddOrReplaceRecording(pendingAnchor);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(focusTree, pendingTree: pendingTree),
                child.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.Equal(RelativeAnchorResolveOutcome.AnchorOutOfScope, failure.Outcome);
            Assert.Equal("anchor-cross-tree-out-of-scope", failure.Reason);
        }

        [Fact]
        public void TryResolveAnchorPose_RelativePoseNonFinite_ReturnsPoseNonFiniteFailure()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording absolute = MakeAbsoluteRecording(
                "absolute-anchor",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0));
            Recording relative = MakeRelativeRecording(
                "relative-child",
                tree.Id,
                localOffset: new Vector3d(double.NaN, 0, 0),
                anchorRecordingId: absolute.RecordingId);
            tree.AddOrReplaceRecording(absolute);
            tree.AddOrReplaceRecording(relative);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                relative.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.Equal(RelativeAnchorResolveOutcome.PoseNonFinite, failure.Outcome);
            Assert.Equal("relative-pose-nonfinite", failure.Reason);
        }

        [Fact]
        public void TryResolveAnchorPose_RelativeFrameInterpolationMiss_ReturnsSectionRange()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording absolute = MakeAbsoluteRecording(
                "absolute-anchor",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(120, 0, 0),
                startUT: 0.0,
                endUT: 20.0);
            Recording relative = MakeRelativeRecording(
                "relative-child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: absolute.RecordingId,
                startUT: 0.0,
                endUT: 20.0);
            TrackSection section = relative.TrackSections[0];
            section.frames = new List<TrajectoryPoint>
            {
                MakePoint(0.0, new Vector3d(1, 0, 0), Quaternion.identity),
                MakePoint(10.0, new Vector3d(1, 0, 0), Quaternion.identity),
            };
            relative.TrackSections[0] = section;
            tree.AddOrReplaceRecording(absolute);
            tree.AddOrReplaceRecording(relative);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                relative.RecordingId,
                15.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.Equal(RelativeAnchorResolveOutcome.OutOfSectionRange, failure.Outcome);
            Assert.Equal("anchor-out-of-recorded-range", failure.Reason);
            Assert.Equal(0, failure.SectionIndex);
            Assert.Equal(0.0, failure.RangeStartUT, 6);
            Assert.Equal(20.0, failure.RangeEndUT, 6);
        }

        [Fact]
        public void TryResolveAnchorPose_EmptybodyFixedFrames_ReturnsOutOfSectionRangeWithNaNRange()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording absolute = MakeAbsoluteRecording(
                "absolute-anchor",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0));
            TrackSection section = absolute.TrackSections[0];
            section.frames = new List<TrajectoryPoint>();
            absolute.TrackSections[0] = section;
            absolute.Points.Clear();
            tree.AddOrReplaceRecording(absolute);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                absolute.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.Equal(RelativeAnchorResolveOutcome.OutOfSectionRange, failure.Outcome);
            Assert.Equal("anchor-out-of-recorded-range", failure.Reason);
            Assert.True(double.IsNaN(failure.RangeStartUT));
            Assert.True(double.IsNaN(failure.RangeEndUT));
        }

        [Fact]
        public void TryResolveAnchorPose_AbsolutePoseNonFinite_ReturnsPoseNonFiniteFailure()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording absolute = MakeAbsoluteRecording(
                "absolute-anchor",
                tree.Id,
                new Vector3d(double.MaxValue, 0, 0),
                new Vector3d(-double.MaxValue, 0, 0));
            tree.AddOrReplaceRecording(absolute);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                absolute.RecordingId,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.Equal(RelativeAnchorResolveOutcome.PoseNonFinite, failure.Outcome);
            Assert.Equal("absolute-pose-nonfinite", failure.Reason);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=absolute-pose-nonfinite") &&
                l.Contains("recordingId=absolute-anchor"));
        }

        [Fact]
        public void TryResolveAnchorPose_SinglebodyFixedFrameFailure_ReportsRequestedUT()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording absolute = MakeAbsoluteRecording(
                "absolute-anchor",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0),
                startUT: 10.0,
                endUT: 20.0);
            TrackSection section = absolute.TrackSections[0];
            section.frames = new List<TrajectoryPoint>
            {
                MakePoint(10.0, new Vector3d(100, 0, 0), Quaternion.identity),
            };
            absolute.TrackSections[0] = section;
            tree.AddOrReplaceRecording(absolute);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(
                    tree,
                    absoluteWorldPositionResolver: p => new Vector3d(double.NaN, double.NaN, double.NaN)),
                absolute.RecordingId,
                15.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.Equal(RelativeAnchorResolveOutcome.Other, failure.Outcome);
            Assert.Equal("absolute-position-unresolved", failure.Reason);
            Assert.Equal(15.0, failure.RequestedUT, 6);
        }

        [Fact]
        public void TryResolveAnchorPose_SmallSectionGapResolverFailureDoesNotEmitOuterRangeWarning()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording anchor = MakeAbsoluteGapAnchor(
                "absolute-anchor",
                tree.Id,
                gapStartUT: 10.0,
                gapEndUT: 10.04);
            Recording child = MakeRelativeRecording(
                "relative-child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: anchor.RecordingId,
                startUT: 10.0,
                endUT: 20.0);
            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(
                    tree,
                    absoluteWorldPositionResolver: p => new Vector3d(double.NaN, double.NaN, double.NaN)),
                child.RecordingId,
                10.02,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.Equal(RelativeAnchorResolveOutcome.Other, failure.Outcome);
            Assert.Equal("absolute-position-unresolved", failure.Reason);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=anchor-out-of-recorded-range") &&
                l.Contains("recordingId=absolute-anchor"));
        }

        [Fact]
        public void TryResolveAnchorPose_SameChainContinuationRevisit_ReturnsCycleFailure()
        {
            var tree = new RecordingTree { Id = "tree" };
            Recording firstHalf = MakeAbsoluteRecording(
                "first-half",
                tree.Id,
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0),
                startUT: 0.0,
                endUT: 10.0);
            firstHalf.ChainId = "chain";
            firstHalf.ChainIndex = 0;

            Recording child = MakeRelativeRecording(
                "child",
                tree.Id,
                localOffset: new Vector3d(1, 0, 0),
                anchorRecordingId: firstHalf.RecordingId,
                startUT: 10.0,
                endUT: 20.0);
            child.ChainId = firstHalf.ChainId;
            child.ChainIndex = 1;
            tree.AddOrReplaceRecording(firstHalf);
            tree.AddOrReplaceRecording(child);

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                MakeContext(tree),
                child.RecordingId,
                12.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out RelativeAnchorResolveFailure failure);

            Assert.False(resolved);
            Assert.Equal(RelativeAnchorResolveOutcome.AnchorCycleDetected, failure.Outcome);
            Assert.Equal("anchor-cycle-detected", failure.Reason);
            Assert.Equal(child.RecordingId, failure.AnchorRecordingId);
        }

        [Fact]
        public void ResolverFalseReturns_HaveStructuredFailure()
        {
            var context = MakeContext(new RecordingTree { Id = "tree" });
            RelativeAnchorResolveFailure failure;

            bool resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                context,
                "",
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out failure);
            AssertFalseWithStructuredFailure("empty anchor recording id", resolved, failure);

            resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                context,
                "cycle",
                5.0,
                new HashSet<string>(StringComparer.Ordinal) { "cycle" },
                out _,
                out failure);
            AssertFalseWithStructuredFailure("anchor cycle", resolved, failure);

            resolved = RelativeAnchorResolver.TryResolveRecordingPose(
                context,
                null,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out failure);
            AssertFalseWithStructuredFailure("null recording", resolved, failure);

            Recording loopAnchor = MakeAbsoluteRecording(
                "loop-anchor",
                "tree",
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0));
            loopAnchor.LoopAnchorVesselId = 123u;
            resolved = RelativeAnchorResolver.TryResolveRecordingPose(
                context,
                loopAnchor,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out failure);
            AssertFalseWithStructuredFailure("loop anchor", resolved, failure);

            var missingSections = new Recording
            {
                RecordingId = "missing-sections",
                RecordingFormatVersion = RelativeAnchorResolver.RecordingAnchorChainFormatVersion,
                TreeId = "tree",
            };
            resolved = RelativeAnchorResolver.TryResolveRecordingPose(
                context,
                missingSections,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out failure);
            AssertFalseWithStructuredFailure("missing track sections", resolved, failure);

            Recording unknownFrame = MakeAbsoluteRecording(
                "unknown-frame",
                "tree",
                new Vector3d(100, 0, 0),
                new Vector3d(110, 0, 0));
            TrackSection unknownSection = unknownFrame.TrackSections[0];
            unknownSection.referenceFrame = (ReferenceFrame)999;
            unknownFrame.TrackSections[0] = unknownSection;
            resolved = RelativeAnchorResolver.TryResolveRecordingPose(
                context,
                unknownFrame,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out failure);
            AssertFalseWithStructuredFailure("unknown section frame", resolved, failure);

            Recording relativeMissingAnchor = MakeRelativeRecording(
                "relative-missing-anchor",
                "tree",
                new Vector3d(1, 0, 0));
            resolved = RelativeAnchorResolver.TryResolveRelativeSectionPose(
                context,
                relativeMissingAnchor,
                relativeMissingAnchor.TrackSections[0],
                0,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out failure);
            AssertFalseWithStructuredFailure("relative section missing anchor", resolved, failure);

            var orbitalWithoutResolver = new Recording
            {
                RecordingId = "orbital-anchor",
                RecordingFormatVersion = RelativeAnchorResolver.RecordingAnchorChainFormatVersion,
                TreeId = "tree",
            };
            orbitalWithoutResolver.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 0.0,
                endUT = 10.0,
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Checkpoint,
            });
            resolved = RelativeAnchorResolver.TryResolveRecordingPose(
                context,
                orbitalWithoutResolver,
                5.0,
                new HashSet<string>(StringComparer.Ordinal),
                out _,
                out failure);
            AssertFalseWithStructuredFailure("orbital resolver missing", resolved, failure);
        }

        private static void AssertFalseWithStructuredFailure(
            string scenario,
            bool resolved,
            RelativeAnchorResolveFailure failure)
        {
            Assert.False(resolved);
            Assert.True(
                failure.HasFailure,
                scenario + " returned false with default failure");
            Assert.NotEqual(RelativeAnchorResolveOutcome.None, failure.Outcome);
            Assert.False(
                string.IsNullOrWhiteSpace(failure.Reason),
                scenario + " returned false without a reason");
        }

        private static RelativeAnchorResolverContext MakeContext(
            RecordingTree tree,
            Func<Recording, TrackSection, int, string> anchorRecordingIdResolver = null,
            RecordingTree pendingTree = null,
            ReFlySessionMarker marker = null,
            Func<TrajectoryPoint, Vector3d> absoluteWorldPositionResolver = null,
            string focusRecordingId = null,
            IReadOnlyDictionary<string, Recording> provisionalRecordings = null,
            Func<uint, string, double, (Vector3d pos, Quaternion rot)?> liveAnchorTransformResolver = null)
        {
            return new RelativeAnchorResolverContext(
                tree,
                focusRecordingId: focusRecordingId,
                focusTreeId: tree?.Id,
                activeReFlyMarker: marker,
                provisionalRecordings: provisionalRecordings,
                pendingTree: pendingTree,
                sectionAnchorRecordingIdResolver: anchorRecordingIdResolver,
                absoluteWorldPositionResolver: absoluteWorldPositionResolver
                    ?? (p => new Vector3d(p.latitude, p.longitude, p.altitude)),
                bodyWorldRotationResolver: p => Quaternion.identity,
                tryResolveLiveAnchorTransform: liveAnchorTransformResolver);
        }

        private static Recording MakeAbsoluteRecording(
            string recordingId,
            string treeId,
            Vector3d startWorld,
            Vector3d endWorld,
            Quaternion? rotation = null,
            double startUT = 0.0,
            double endUT = 10.0)
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
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(startUT, startWorld, rot),
                    MakePoint(endUT, endWorld, rot),
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });
            return rec;
        }

        private static Recording MakeTerminalEdgeAbsoluteRecording(
            string recordingId,
            string treeId,
            double sectionEndUT,
            double terminalPlayableUT,
            Vector3d terminalWorld)
        {
            Recording rec = MakeAbsoluteRecording(
                recordingId,
                treeId,
                new Vector3d(100, 0, 0),
                terminalWorld,
                startUT: 0.0,
                endUT: sectionEndUT);
            TrackSection section = rec.TrackSections[0];
            section.sampleRateHz = 50f;
            section.frames = new List<TrajectoryPoint>
            {
                MakePoint(0.0, new Vector3d(100, 0, 0), Quaternion.identity),
                MakePoint(terminalPlayableUT, terminalWorld, Quaternion.identity),
            };
            rec.TrackSections[0] = section;
            return rec;
        }

        private static Recording MakeTerminalEdgeRelativeRecording(
            string recordingId,
            string treeId,
            Vector3d localOffset,
            string anchorRecordingId,
            double sectionEndUT,
            double terminalPlayableUT)
        {
            Recording rec = MakeRelativeRecording(
                recordingId,
                treeId,
                localOffset,
                anchorRecordingId: anchorRecordingId,
                startUT: 0.0,
                endUT: sectionEndUT);
            TrackSection section = rec.TrackSections[0];
            section.sampleRateHz = 50f;
            section.frames = new List<TrajectoryPoint>
            {
                MakePoint(0.0, localOffset, Quaternion.identity),
                MakePoint(terminalPlayableUT, localOffset, Quaternion.identity),
            };
            rec.TrackSections[0] = section;
            return rec;
        }

        private static Recording MakeRelativeRecording(
            string recordingId,
            string treeId,
            Vector3d localOffset,
            string anchorRecordingId = null,
            uint legacyAnchorPid = 0u,
            double startUT = 0.0,
            double endUT = 10.0)
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
                startUT = startUT,
                endUT = endUT,
                anchorVesselId = legacyAnchorPid,
                anchorRecordingId = anchorRecordingId,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(startUT, localOffset, Quaternion.identity),
                    MakePoint(endUT, localOffset, Quaternion.identity),
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });
            return rec;
        }

        private static Recording MakeAbsoluteGapAnchor(
            string recordingId,
            string treeId,
            double gapStartUT,
            double gapEndUT,
            bool includeFlatPoints = true)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = RelativeAnchorResolver.RecordingAnchorChainFormatVersion,
                TreeId = treeId,
                VesselName = recordingId,
            };
            var beforeGap = MakePoint(gapStartUT, GapWorld(100.0, gapStartUT), Quaternion.identity);
            var afterGap = MakePoint(gapEndUT, GapWorld(100.0, gapEndUT), Quaternion.identity);
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 0.0,
                endUT = gapStartUT,
                sampleRateHz = 50f,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(0.0, GapWorld(100.0, 0.0), Quaternion.identity),
                    beforeGap,
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = gapEndUT,
                endUT = 20.0,
                sampleRateHz = 50f,
                frames = new List<TrajectoryPoint>
                {
                    afterGap,
                    MakePoint(20.0, GapWorld(100.0, 20.0), Quaternion.identity),
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            });

            if (includeFlatPoints)
            {
                rec.Points.Add(MakePoint(0.0, GapWorld(100.0, 0.0), Quaternion.identity));
                rec.Points.Add(beforeGap);
                rec.Points.Add(afterGap);
                rec.Points.Add(MakePoint(20.0, GapWorld(100.0, 20.0), Quaternion.identity));
            }

            return rec;
        }

        private static Recording MakeRelativeGapAnchor(
            string recordingId,
            string treeId,
            bool includeAbsoluteShadows)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = RelativeAnchorResolver.RecordingAnchorChainFormatVersion,
                TreeId = treeId,
                VesselName = recordingId,
            };
            var firstSection = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 0.0,
                endUT = 10.0,
                sampleRateHz = 50f,
                anchorRecordingId = "root-anchor",
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(0.0, new Vector3d(0, 0, 0), Quaternion.identity),
                    MakePoint(10.0, new Vector3d(0, 0, 0), Quaternion.identity),
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            };
            var secondSection = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 10.04,
                endUT = 20.0,
                sampleRateHz = 50f,
                anchorRecordingId = "root-anchor",
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(10.04, new Vector3d(0, 0, 0), Quaternion.identity),
                    MakePoint(20.0, new Vector3d(0, 0, 0), Quaternion.identity),
                },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            };

            if (includeAbsoluteShadows)
            {
                firstSection.bodyFixedFrames = new List<TrajectoryPoint>
                {
                    MakePoint(0.0, GapWorld(200.0, 0.0), Quaternion.identity),
                    MakePoint(10.0, GapWorld(200.0, 10.0), Quaternion.identity),
                };
                secondSection.bodyFixedFrames = new List<TrajectoryPoint>
                {
                    MakePoint(10.04, GapWorld(200.0, 10.04), Quaternion.identity),
                    MakePoint(20.0, GapWorld(200.0, 20.0), Quaternion.identity),
                };
            }

            rec.TrackSections.Add(firstSection);
            rec.TrackSections.Add(secondSection);
            rec.Points.Add(MakePoint(10.0, new Vector3d(900, 0, 0), Quaternion.identity));
            rec.Points.Add(MakePoint(10.04, new Vector3d(904, 0, 0), Quaternion.identity));
            return rec;
        }

        private static Vector3d GapWorld(double baseX, double ut)
        {
            return new Vector3d(baseX + ut, 0, 0);
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
