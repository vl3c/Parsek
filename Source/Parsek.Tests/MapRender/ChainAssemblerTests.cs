using System.Collections.Generic;
using Parsek;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-1 guard for the assembler's treatment-assignment + intra-arc body-split + seam logic
    /// (design §6.2 / §3.3) with surface=null (no below-surface exclusion — that predicate is
    /// covered by the polyline-renderer tests; here we test the assembler's OWN routing).
    ///
    /// A regression here is a conic drawn as a polyline (or vice-versa), a two-body segment (the
    /// "teleport next to a moon" class), or a rigid SOI seam where it should be tolerated.
    /// </summary>
    public class ChainAssemblerTests
    {
        private static TrajectoryPoint Pt(double ut, string body)
            => new TrajectoryPoint { ut = ut, bodyName = body };

        // ascent (Kerbin points 0..8) → loiter (Kerbin orbit 10..30) → arrival (Mun points 32..36)
        private static MockTrajectory FullChain()
        {
            return new MockTrajectory
            {
                RecordingId = "rec-1",
                Points = new List<TrajectoryPoint>
                {
                    Pt(0, "Kerbin"), Pt(2, "Kerbin"), Pt(4, "Kerbin"), Pt(6, "Kerbin"), Pt(8, "Kerbin"),
                    Pt(32, "Mun"), Pt(34, "Mun"), Pt(36, "Mun"),
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Kerbin", semiMajorAxis = 700000, eccentricity = 0 },
                },
            };
        }

        [Fact]
        public void Build_AssignsTreatmentsFramesAndSeams()
        {
            var chain = ChainAssembler.Build(FullChain(), committedIndex: 5, instanceKey: 0,
                windowStartUT: 0, windowEndUT: 40);

            Assert.Equal("rec-1", chain.RecordingId);
            Assert.Equal(5, chain.CommittedIndex);
            Assert.Equal(3, chain.SegmentCount);

            var s0 = chain.Segments[0];
            var s1 = chain.Segments[1];
            var s2 = chain.Segments[2];

            // treatment + frame
            Assert.Equal(Treatment.TracedPath, s0.Treatment);  // ascent points
            Assert.Equal("Kerbin", s0.FrameBodyName);
            Assert.Equal(Treatment.StockConic, s1.Treatment);  // orbit segment
            Assert.Equal("Kerbin", s1.FrameBodyName);
            Assert.True(s1.Payload.HasConic);
            Assert.Equal(Treatment.TracedPath, s2.Treatment);  // arrival points (different body)
            Assert.Equal("Mun", s2.FrameBodyName);

            // ordering
            Assert.True(s0.StartUT < s1.StartUT && s1.StartUT < s2.StartUT);

            // seams: same-body Kerbin→Kerbin is rigid; Kerbin→Mun is a flexible SOI seam
            Assert.Equal(SeamKind.None, s0.LeadingSeam);
            Assert.Equal(SeamKind.Rigid, s0.TrailingSeam);
            Assert.Equal(SeamKind.Rigid, s1.LeadingSeam);
            Assert.Equal(SeamKind.FlexibleSoi, s1.TrailingSeam);
            Assert.Equal(SeamKind.FlexibleSoi, s2.LeadingSeam);
            Assert.Equal(SeamKind.None, s2.TrailingSeam);
        }

        [Fact]
        public void Build_BodyChangeSplitsTracedRun_OneFramePerSegment()
        {
            // a single traced run that crosses Kerbin→Mun must split into two one-body segments
            var traj = new MockTrajectory
            {
                RecordingId = "rec-2",
                Points = new List<TrajectoryPoint> { Pt(0, "Kerbin"), Pt(2, "Kerbin"), Pt(4, "Mun"), Pt(6, "Mun") },
            };
            var chain = ChainAssembler.Build(traj, 0, 0, 0, 10);
            Assert.Equal(2, chain.SegmentCount);
            Assert.Equal("Kerbin", chain.Segments[0].FrameBodyName);
            Assert.Equal("Mun", chain.Segments[1].FrameBodyName);
            Assert.Equal(SeamKind.FlexibleSoi, chain.Segments[0].TrailingSeam);
        }

        [Fact]
        public void Build_FaithfulFallbackFlag_PassesThrough()
        {
            var chain = ChainAssembler.Build(FullChain(), 0, 0, 0, 40, faithfulFallback: true);
            Assert.True(chain.IsFaithfulFallback);
        }

        [Fact]
        public void Build_NullTrajectory_EmptyChain()
        {
            var chain = ChainAssembler.Build(null, 0, 0, 0, 10);
            Assert.Equal(0, chain.SegmentCount);
        }

        [Fact]
        public void Build_DropsSinglePointRun()
        {
            // one lone Kerbin point inside the window but no second sample → no drawable run
            var traj = new MockTrajectory { RecordingId = "rec-3", Points = new List<TrajectoryPoint> { Pt(5, "Kerbin") } };
            var chain = ChainAssembler.Build(traj, 0, 0, 0, 10);
            Assert.Equal(0, chain.SegmentCount);
        }
    }
}
