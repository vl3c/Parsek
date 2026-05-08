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
                out AnchorPose pose);

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
                out AnchorPose pose);

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
                out AnchorPose pose);

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
                out AnchorPose pose);

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
                out _);

            Assert.False(resolved);
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
                out _);

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
                out AnchorPose pose);

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
            anchor.TrackSections[0].absoluteFrames.Insert(
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
                out _);

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
                out _);

            Assert.False(resolved);
            Assert.DoesNotContain(logLines, l => l.Contains("small section gap"));
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=anchor-out-of-recorded-range") &&
                l.Contains("recordingId=relative-anchor"));
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
                out AnchorPose pose);

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
                out AnchorPose pose);

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
                out AnchorPose pose);

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
                out AnchorPose pose);

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
                out _);

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
                out AnchorPose pose);

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
                out _);

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
                out _);

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
                out _);

            Assert.False(resolved);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=loop-anchor-out-of-scope"));
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
                out _);

            Assert.False(resolved);
            Assert.Contains(logLines, l =>
                l.Contains("[RelativeAnchorResolver]") &&
                l.Contains("reason=anchor-cross-tree-out-of-scope"));
        }

        private static RelativeAnchorResolverContext MakeContext(
            RecordingTree tree,
            Func<Recording, TrackSection, int, string> anchorRecordingIdResolver = null,
            RecordingTree pendingTree = null,
            ReFlySessionMarker marker = null)
        {
            return new RelativeAnchorResolverContext(
                tree,
                focusRecordingId: null,
                focusTreeId: tree?.Id,
                activeReFlyMarker: marker,
                pendingTree: pendingTree,
                sectionAnchorRecordingIdResolver: anchorRecordingIdResolver,
                absoluteWorldPositionResolver: p => new Vector3d(p.latitude, p.longitude, p.altitude),
                bodyWorldRotationResolver: p => Quaternion.identity);
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
                firstSection.absoluteFrames = new List<TrajectoryPoint>
                {
                    MakePoint(0.0, GapWorld(200.0, 0.0), Quaternion.identity),
                    MakePoint(10.0, GapWorld(200.0, 10.0), Quaternion.identity),
                };
                secondSection.absoluteFrames = new List<TrajectoryPoint>
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
