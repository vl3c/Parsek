using System;
using System.Collections.Generic;
using System.Reflection;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of the orbital-checkpoint anchor pose delegate
    /// (<see cref="TryResolveOrbitalAnchorPose"/>) that each of the five relative-anchor
    /// hosts hands <see cref="RelativeAnchorResolverContext"/>: the recorded-anchor
    /// playback resolver, the render-anchor world-frame resolver, flight timeline playback,
    /// the background recorder and the active recorder. Each delegate is read from the
    /// context its host builds (never by the delegate's own name), so the cells survive a
    /// change of which method a host passes. Four hosts pass the shared pose; the
    /// render-anchor resolver passes its boundary-instant pose (todo
    /// REFLY-ANCHOR-EPSILON-1300KM), which picks the segment and misses the same way.
    /// <para>
    /// Headless, <c>FlightGlobals.Bodies</c> throws <see cref="TypeInitializationException"/>
    /// on first read, so every body lookup misses and no cell reaches stock <c>Orbit</c>:
    /// what can be pinned is every path that answers false without a body (no checkpoints,
    /// no segment covering the UT, a segment with no body name) and the lookup path that
    /// swallows the initializer failure. A hit needs live KSP (the H11 / LT-1 in-game cells
    /// and the recorder lanes exercise it).
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class OrbitalCheckpointAnchorPoseCharacterizationTests : IDisposable
    {
        public const string Recorded = "recorded";
        public const string Production = "production";
        public const string FlightPlayback = "flight-playback";
        public const string ActiveRecorder = "active-recorder";

        private readonly List<string> logLines = new List<string>();

        public OrbitalCheckpointAnchorPoseCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public static IEnumerable<object[]> Hosts()
        {
            yield return new object[] { Recorded };
            yield return new object[] { Production };
            yield return new object[] { FlightPlayback };
            yield return new object[] { ActiveRecorder };
        }

        [Theory]
        [MemberData(nameof(Hosts))]
        public void Host_WiresAnOrbitalCheckpointPoseResolver(string host)
        {
            Assert.NotNull(ContextFor(host, out _).OrbitalCheckpointPoseResolver);
        }

        [Theory]
        [MemberData(nameof(Hosts))]
        public void NullCheckpoints_ReturnFalse_WithDefaultPose(string host)
        {
            var resolver = ResolverFor(host, out Recording rec);
            var section = OrbitalSection(null);
            AssertMiss(resolver, rec, section, 15.0);
        }

        [Theory]
        [MemberData(nameof(Hosts))]
        public void EmptyCheckpoints_ReturnFalse_WithDefaultPose(string host)
        {
            var resolver = ResolverFor(host, out Recording rec);
            AssertMiss(resolver, rec, OrbitalSection(new List<OrbitSegment>()), 15.0);
        }

        [Theory]
        [MemberData(nameof(Hosts))]
        public void UtOutsideEveryCheckpoint_ReturnsFalse_WithDefaultPose(string host)
        {
            var resolver = ResolverFor(host, out Recording rec);
            var section = OrbitalSection(new List<OrbitSegment>
            {
                Segment(10.0, 20.0, "Kerbin"),
                Segment(30.0, 40.0, "Kerbin"),
            });
            AssertMiss(resolver, rec, section, 5.0);
            AssertMiss(resolver, rec, section, 25.0);
            AssertMiss(resolver, rec, section, 45.0);
        }

        [Theory]
        [MemberData(nameof(Hosts))]
        public void CoveringSegmentWithNoBodyName_ReturnsFalse(string host)
        {
            var resolver = ResolverFor(host, out Recording rec);
            AssertMiss(resolver, rec, OrbitalSection(new List<OrbitSegment> { Segment(10.0, 20.0, null) }), 15.0);
            AssertMiss(resolver, rec, OrbitalSection(new List<OrbitSegment> { Segment(10.0, 20.0, "") }), 15.0);
        }

        [Theory]
        [MemberData(nameof(Hosts))]
        public void FirstCoveringSegmentDecides_ALaterNamedSegmentIsNotTried(string host)
        {
            var resolver = ResolverFor(host, out Recording rec);
            var section = OrbitalSection(new List<OrbitSegment>
            {
                Segment(10.0, 20.0, null),
                Segment(10.0, 20.0, "Kerbin"),
            });
            AssertMiss(resolver, rec, section, 15.0);
        }

        [Theory]
        [MemberData(nameof(Hosts))]
        public void NamedBody_FlightGlobalsInitializerFailure_IsSwallowed(string host)
        {
            // The lookup's TypeInitializationException path: every boundary of the
            // covering segment, both ends inclusive, reaches it and answers false.
            var resolver = ResolverFor(host, out Recording rec);
            var section = OrbitalSection(new List<OrbitSegment> { Segment(10.0, 20.0, "Kerbin") });
            AssertMiss(resolver, rec, section, 10.0);
            AssertMiss(resolver, rec, section, 15.0);
            AssertMiss(resolver, rec, section, 20.0);
            // A second read takes the same path (the failed initializer is cached).
            AssertMiss(resolver, rec, section, 15.0);
        }

        [Theory]
        [MemberData(nameof(Hosts))]
        public void ThroughTheCore_AMissLogsOrbitalPoseUnresolved(string host)
        {
            RelativeAnchorResolverContext context = ContextFor(host, out Recording rec);
            Recording anchor = OrbitalAnchorRecording("anchor-" + host, "Kerbin");
            bool resolved = RelativeAnchorResolver.TryResolveRecordingPose(
                context,
                anchor,
                15.0,
                new HashSet<string>(StringComparer.Ordinal),
                out AnchorPose pose,
                out RelativeAnchorResolveFailure failure);
            AssertCoreMiss(resolved, pose, failure, anchor.RecordingId);
        }

        [Fact]
        public void BackgroundRecorder_ThroughItsRecordedAnchorEntry_LogsOrbitalPoseUnresolved()
        {
            var tree = new RecordingTree { Id = "tree-bg" };
            Recording anchor = OrbitalAnchorRecording("anchor-background", "Kerbin");
            anchor.TreeId = tree.Id;
            tree.Recordings[anchor.RecordingId] = anchor;
            var bg = new BackgroundRecorder(tree);
            MethodInfo entry = typeof(BackgroundRecorder).GetMethod(
                "TryResolveBackgroundRecordedAnchorPose",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(entry);
            object[] args = { anchor.RecordingId, 15.0, null, null };
            bool resolved = (bool)entry.Invoke(bg, args);
            AssertCoreMiss(resolved, (AnchorPose)args[2], (RelativeAnchorResolveFailure)args[3], anchor.RecordingId);
        }

        [Fact]
        public void BackgroundRecorder_NoBodyName_LogsOrbitalPoseUnresolved()
        {
            var tree = new RecordingTree { Id = "tree-bg-null" };
            Recording anchor = OrbitalAnchorRecording("anchor-background-null", null);
            anchor.TreeId = tree.Id;
            tree.Recordings[anchor.RecordingId] = anchor;
            var bg = new BackgroundRecorder(tree);
            MethodInfo entry = typeof(BackgroundRecorder).GetMethod(
                "TryResolveBackgroundRecordedAnchorPose",
                BindingFlags.Instance | BindingFlags.NonPublic);
            object[] args = { anchor.RecordingId, 15.0, null, null };
            bool resolved = (bool)entry.Invoke(bg, args);
            AssertCoreMiss(resolved, (AnchorPose)args[2], (RelativeAnchorResolveFailure)args[3], anchor.RecordingId);
        }

        private void AssertCoreMiss(
            bool resolved, AnchorPose pose, RelativeAnchorResolveFailure failure, string anchorId)
        {
            Assert.False(resolved);
            AssertDefaultPose(pose);
            Assert.Equal(RelativeAnchorResolveOutcome.Other, failure.Outcome);
            Assert.Equal("orbital-pose-unresolved", failure.Reason);
            Assert.Equal(0, failure.SectionIndex);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN][RelativeAnchorResolver]")
                && l.Contains("relative-anchor-unresolved: reason=orbital-pose-unresolved")
                && l.Contains("anchorRecordingId=" + anchorId)
                && l.Contains("sectionIndex=0")
                && l.Contains("ut=15"));
        }

        private static void AssertMiss(
            TryResolveOrbitalAnchorPose resolver, Recording rec, TrackSection section, double ut)
        {
            bool resolved = resolver(rec, section, 3, ut, out AnchorPose pose);
            Assert.False(resolved);
            AssertDefaultPose(pose);
        }

        private static void AssertDefaultPose(AnchorPose pose)
        {
            Assert.Equal(0.0, pose.WorldPos.x);
            Assert.Equal(0.0, pose.WorldPos.y);
            Assert.Equal(0.0, pose.WorldPos.z);
            Assert.Equal(0, pose.ResolvedSectionIndex);
            Assert.Null(pose.ResolvedRecordingId);
            Assert.Equal(0f, pose.WorldRotation.x);
            Assert.Equal(0f, pose.WorldRotation.y);
            Assert.Equal(0f, pose.WorldRotation.z);
            Assert.Equal(0f, pose.WorldRotation.w);
        }

        private static TryResolveOrbitalAnchorPose ResolverFor(string host, out Recording rec)
        {
            TryResolveOrbitalAnchorPose resolver = ContextFor(host, out rec).OrbitalCheckpointPoseResolver;
            Assert.NotNull(resolver);
            return resolver;
        }

        private static RelativeAnchorResolverContext ContextFor(string host, out Recording rec)
        {
            var tree = new RecordingTree { Id = "tree-" + host };
            rec = new Recording
            {
                RecordingId = "focus-" + host,
                TreeId = tree.Id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            tree.Recordings[rec.RecordingId] = rec;
            switch (host)
            {
                case Recorded:
                {
                    RecordingStore.AddCommittedTreeForTesting(tree);
                    object[] args = { rec, null };
                    MethodInfo m = typeof(RecordedRelativeAnchorPoseResolver).GetMethod(
                        "TryBuildContext", BindingFlags.Static | BindingFlags.NonPublic);
                    Assert.NotNull(m);
                    Assert.True((bool)m.Invoke(null, args));
                    return (RelativeAnchorResolverContext)args[1];
                }
                case Production:
                {
                    RecordingStore.AddCommittedTreeForTesting(tree);
                    object[] args = { rec, null };
                    MethodInfo m = typeof(ProductionAnchorWorldFrameResolver).GetMethod(
                        "TryBuildRelativeAnchorResolverContext", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(m);
                    Assert.True((bool)m.Invoke(new ProductionAnchorWorldFrameResolver(), args));
                    return (RelativeAnchorResolverContext)args[1];
                }
                case FlightPlayback:
                    return ParsekFlight.BuildFlightRelativeAnchorResolverContext(tree, rec.RecordingId, null);
                case ActiveRecorder:
                {
                    var recorder = new FlightRecorder();
                    MethodInfo m = typeof(FlightRecorder).GetMethod(
                        "BuildRecorderRelativeAnchorResolverContext", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(m);
                    return (RelativeAnchorResolverContext)m.Invoke(recorder, null);
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(host), host, null);
            }
        }

        private static Recording OrbitalAnchorRecording(string id, string bodyName)
        {
            var anchor = new Recording
            {
                RecordingId = id,
                TreeId = "tree",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            anchor.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                environment = SegmentEnvironment.ExoBallistic,
                source = TrackSectionSource.Checkpoint,
                startUT = 0.0,
                endUT = 100.0,
                checkpoints = new List<OrbitSegment> { Segment(10.0, 20.0, bodyName) },
            });
            return anchor;
        }

        private static TrackSection OrbitalSection(List<OrbitSegment> checkpoints)
        {
            return new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                environment = SegmentEnvironment.ExoBallistic,
                source = TrackSectionSource.Checkpoint,
                startUT = 0.0,
                endUT = 100.0,
                checkpoints = checkpoints,
            };
        }

        private static OrbitSegment Segment(double startUT, double endUT, string bodyName)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = bodyName,
                semiMajorAxis = 700000.0,
                eccentricity = 0.01,
                inclination = 5.0,
                epoch = startUT,
            };
        }
    }
}
