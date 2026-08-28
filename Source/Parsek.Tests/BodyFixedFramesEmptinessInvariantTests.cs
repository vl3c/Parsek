using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// BODYFIXEDFRAMES-INVISIBLE-TO-BOTH-EMPTINESS-PREDICATES.
    ///
    /// <para>
    /// Guards the invariant that the two emptiness predicates and the recorder /
    /// playback coverage primitives agree on ONE question: does a parent-anchored
    /// Relative section carrying only <c>bodyFixedFrames</c> hold renderable
    /// coverage?
    /// </para>
    ///
    /// <para>
    /// Background. A parent-anchored recording authors two surfaces on its Relative
    /// sections: <c>frames</c> (anchor-local offsets) and <c>bodyFixedFrames</c>
    /// (body-fixed points), and the parent-anchored contract names the latter the
    /// PRIMARY playback surface.
    /// <c>PlaybackTrajectoryBoundsResolver.HasPlayablePayload</c> reads
    /// <c>checkpoints</c> for an OrbitalCheckpoint section and <c>frames</c> for
    /// everything else — never <c>bodyFixedFrames</c>. Both emptiness predicates were
    /// built on it (<c>SupersedeCommit.HasPlayableSupersedePayload</c> at merge,
    /// <c>ParsekFlight.IsZeroPointLeaf</c> at prune), so a section whose ONLY authored
    /// surface is <c>bodyFixedFrames</c> read as EMPTY to both while
    /// <c>DebrisRelativeCoveragePrimitives</c> treated the same bytes as coverage: the
    /// prune deleted recordings the recorder had just declared covered.
    /// </para>
    ///
    /// <para>
    /// Four facts hold the fix together, and these cells exercise each:
    ///   1. The threshold is TWO samples — the binding recorder-persistence invariant
    ///      ("<c>section.frames</c>, two-point <c>bodyFixedFrames</c>, or non-predicted
    ///      checkpoints"), because the shadow renderer interpolates and a single
    ///      body-fixed point may never be clamped into a stale ghost.
    ///   2. The prune and the merge read the SAME widened predicate, so they cannot
    ///      diverge on sections again.
    ///   3. The narrow <c>HasPlayablePayload</c> is UNCHANGED, because it also gates
    ///      the bounds walk, whose non-checkpoint branch indexes <c>frames[0]</c>
    ///      directly and would dereference an empty list.
    ///   4. A bodyFixedFrames-only section round-trips through the trajectory sidecar,
    ///      so the surface the predicates now honour actually survives a save/load.
    /// </para>
    ///
    /// <para>
    /// Related: CLAUDE.md "Parent-anchored contract" / the parent-anchored
    /// debris-frame invariant, and
    /// <c>docs/dev/research/extending-rewind-to-stable-leaves.md</c>.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class BodyFixedFramesEmptinessInvariantTests : IDisposable
    {
        public BodyFixedFramesEmptinessInvariantTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static TrackSection BodyFixedOnlySection(int sampleCount,
            ReferenceFrame frame = ReferenceFrame.Relative)
        {
            var body = new List<TrajectoryPoint>();
            for (int i = 0; i < sampleCount; i++)
                body.Add(new TrajectoryPoint { ut = 100.0 + i, bodyName = "Kerbin" });

            return new TrackSection
            {
                referenceFrame = frame,
                startUT = 100.0,
                endUT = 100.0 + Math.Max(0, sampleCount - 1),
                // The defect shape: the anchor-local surface is allocated but empty,
                // which is exactly what the recorder leaves behind when the body-fixed
                // shadow is the only surface it could author.
                frames = new List<TrajectoryPoint>(),
                bodyFixedFrames = body,
            };
        }

        private static Recording ParentAnchoredLeaf(string id, TrackSection section)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                ParentAnchorRecordingId = "rec_parent",
                IsDebris = true,
                TerminalStateValue = TerminalState.Landed,
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>(),
                SurfacePos = null,
                ChildBranchPointId = null,
                TrackSections = new List<TrackSection> { section },
            };
        }

        // ---------- (1) the two-sample threshold ----------------------------

        [Fact]
        public void TwoBodyFixedSamples_AreAuthoredRenderablePayload()
        {
            var section = BodyFixedOnlySection(2);
            Assert.True(PlaybackTrajectoryBoundsResolver.HasAuthoredRenderablePayload(section));
        }

        [Fact]
        public void OneBodyFixedSample_IsNotPayload_MatchesTheCoveragePrimitive()
        {
            // Not an arbitrary floor: DebrisRelativeCoveragePrimitives refuses a single
            // body-fixed sample on both the recorder-persistable and playback sides,
            // because the shadow renderer interpolates between two samples. The
            // emptiness predicate must not claim coverage the renderer would refuse.
            var section = BodyFixedOnlySection(1);
            Assert.False(PlaybackTrajectoryBoundsResolver.HasAuthoredRenderablePayload(section));
            Assert.False(DebrisRelativeCoveragePrimitives.BodyFixedPrimaryFramesCoverUT(
                section.bodyFixedFrames, 100.0));
            Assert.False(DebrisRelativeCoveragePrimitives.TryGetBodyFixedPrimaryCoverageEndUT(
                section.bodyFixedFrames, out _));
        }

        [Fact]
        public void TheThresholdConstantAgreesWithTheCoveragePrimitive()
        {
            // Pin the constant against the primitive's own behavior so a future edit to
            // either side reds here rather than letting the two drift apart silently.
            Assert.Equal(2, PlaybackTrajectoryBoundsResolver.MinBodyFixedPrimarySamples);

            var justUnder = BodyFixedOnlySection(
                PlaybackTrajectoryBoundsResolver.MinBodyFixedPrimarySamples - 1);
            var atThreshold = BodyFixedOnlySection(
                PlaybackTrajectoryBoundsResolver.MinBodyFixedPrimarySamples);

            Assert.False(DebrisRelativeCoveragePrimitives.TryGetBodyFixedPrimaryCoverageEndUT(
                justUnder.bodyFixedFrames, out _));
            Assert.True(DebrisRelativeCoveragePrimitives.TryGetBodyFixedPrimaryCoverageEndUT(
                atThreshold.bodyFixedFrames, out double endUT));
            Assert.Equal(atThreshold.bodyFixedFrames[
                atThreshold.bodyFixedFrames.Count - 1].ut, endUT);
        }

        [Fact]
        public void BodyFixedPointsOnANonRelativeSection_DoNotCount()
        {
            // The recorder allocates bodyFixedFrames for the Relative frame and no
            // other, so this is a contract assertion: a future producer smuggling
            // body-fixed points onto an Absolute / OrbitalCheckpoint section must not
            // have them silently counted as coverage.
            foreach (var frame in new[]
                     { ReferenceFrame.Absolute, ReferenceFrame.OrbitalCheckpoint })
            {
                var section = BodyFixedOnlySection(4, frame);
                Assert.False(
                    PlaybackTrajectoryBoundsResolver.HasAuthoredRenderablePayload(section),
                    $"body-fixed points on a {frame} section must not count as payload");
            }
        }

        // ---------- (2) prune and merge still agree -------------------------

        [Fact]
        public void BodyFixedOnlyLeaf_IsNotZeroPoint_AndValidatesAsASupersedeTarget()
        {
            var rec = ParentAnchoredLeaf("rec_bodyfixed_only", BodyFixedOnlySection(4));

            Assert.False(ParsekFlight.IsZeroPointLeaf(rec));
            Assert.True(SupersedeCommit.ValidateSupersedeTarget(rec, out string reason),
                $"merge refused a body-fixed-primary recording: {reason}");
            Assert.Null(reason);
        }

        [Fact]
        public void SingleBodyFixedSampleLeaf_IsJudgedEmptyByBothPredicates()
        {
            // The two predicates must agree in BOTH directions on this surface: one
            // sample is not coverage, so the prune reads it as a zero-point leaf and the
            // merge refuses it as a supersede target.
            var rec = ParentAnchoredLeaf("rec_bodyfixed_single", BodyFixedOnlySection(1));

            Assert.True(ParsekFlight.IsZeroPointLeaf(rec));
            Assert.False(SupersedeCommit.ValidateSupersedeTarget(rec, out string reason));
            Assert.Equal("empty Points", reason);
        }

        [Fact]
        public void BothEmptinessPredicatesReadTheSameSectionTerm()
        {
            // Source-level pin. The prune's HasPlayableTrackSection and the merge's
            // HasPlayableSupersedePayload are separate private methods in separate
            // files; the invariant is that they read the SAME per-section notion. A
            // behavioral cell alone would pass if only one of them were widened, so
            // sweep a matrix through both public entry points instead.
            var cases = new (int samples, bool expectPayload)[]
            {
                (0, false), (1, false), (2, true), (7, true),
            };

            foreach (var (samples, expectPayload) in cases)
            {
                var rec = ParentAnchoredLeaf("rec_matrix_" + samples,
                    BodyFixedOnlySection(samples));
                Assert.Equal(!expectPayload, ParsekFlight.IsZeroPointLeaf(rec));
                Assert.Equal(expectPayload,
                    SupersedeCommit.ValidateSupersedeTarget(rec, out _));
            }
        }

        // ---------- (3) the narrow predicate is unchanged --------------------

        [Fact]
        public void HasPlayablePayload_StaysNarrow_SoTheBoundsWalkNeverSeesAnEmptyFramesList()
        {
            // TryGetPlayableTrackSectionPayloadBounds gates on HasPlayablePayload and
            // then indexes section.frames[0] on the non-checkpoint branch. Widening that
            // helper in place would walk a frames-empty section straight into an
            // out-of-range read; the emptiness widening therefore lives in a SEPARATE
            // predicate. This cell is the tripwire for anyone who "simplifies" the two
            // back into one.
            var section = BodyFixedOnlySection(4);
            Assert.False(PlaybackTrajectoryBoundsResolver.HasPlayablePayload(section));

            var traj = new Recording
            {
                RecordingId = "rec_bounds",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>(),
                TrackSections = new List<TrackSection> { section },
            };
            // No exception, and no bounds claimed from a surface this walk cannot read.
            Assert.False(PlaybackTrajectoryBoundsResolver.TryGetGhostPlayablePayloadBounds(
                traj, out _, out _));
        }

        [Fact]
        public void TheBoundsWalkStillGatesOnTheNarrowPredicate_PinnedBySourceInspection()
        {
            string source = ReadSourceFile("PlaybackTrajectoryBoundsResolver.cs");
            int boundsIdx = source.IndexOf(
                "TryGetPlayableTrackSectionPayloadBounds(", StringComparison.Ordinal);
            Assert.True(boundsIdx >= 0, "bounds walk not found");
            string boundsBody = source.Substring(boundsIdx);
            Assert.Contains("if (!HasPlayablePayload(section))", boundsBody);
            Assert.DoesNotContain("HasAuthoredRenderablePayload(section)", boundsBody);
        }

        // ---------- (4) the surface round-trips ------------------------------

        [Fact]
        public void BodyFixedOnlySection_RoundTripsThroughTheTrajectorySidecar()
        {
            // The predicates now honour a surface; that surface has to survive a
            // save/load or the fix only holds in memory.
            var rec = ParentAnchoredLeaf("rec_roundtrip", BodyFixedOnlySection(3));

            var node = new ConfigNode("PARSEK_RECORDING");
            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(node, rec);

            var restored = new Recording { RecordingId = rec.RecordingId };
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, restored);

            Assert.Single(restored.TrackSections);
            TrackSection section = restored.TrackSections[0];
            Assert.Equal(ReferenceFrame.Relative, section.referenceFrame);
            Assert.Equal(3, section.bodyFixedFrames?.Count ?? 0);
            Assert.Equal(0, section.frames?.Count ?? 0);
            Assert.True(PlaybackTrajectoryBoundsResolver.HasAuthoredRenderablePayload(section));

            // The SECTION assertions above are what this cell pins. IsZeroPointLeaf is
            // checked too, but read it as a smoke check only: the codec's flat-fallback
            // heal can rebuild Points from the body-fixed surface, and Points alone
            // would satisfy the prune predicate even pre-fix.
            Assert.False(ParsekFlight.IsZeroPointLeaf(restored));
        }

        private static string ReadSourceFile(string fileName)
        {
            // xUnit runs from Source/Parsek.Tests/bin/Debug/net472 — five segments up.
            string dir = System.IO.Path.GetDirectoryName(
                new Uri(typeof(BodyFixedFramesEmptinessInvariantTests)
                    .Assembly.CodeBase).LocalPath);
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                dir, "..", "..", "..", "..", "..", "Source", "Parsek", fileName));
            Assert.True(System.IO.File.Exists(path), $"source not found: {path}");
            return System.IO.File.ReadAllText(path);
        }
    }
}
