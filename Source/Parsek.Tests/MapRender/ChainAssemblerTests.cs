using System.Collections.Generic;
using Parsek;
using Parsek.Display;
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

        // A synthetic surface provider (no FlightGlobals): Kerbin has the given radius.
        private static GhostTrajectoryPolylineRenderer.BodySurfaceProvider KerbinRadius(double r)
            => (string b, out GhostTrajectoryPolylineRenderer.BodySurfaceInfo info) =>
               { info = new GhostTrajectoryPolylineRenderer.BodySurfaceInfo { radius = r }; return string.Equals(b, "Kerbin"); };

        [Fact]
        public void Build_BelowSurfaceOrbit_FallsToTracedPath_NotStockConic()
        {
            // periapsis 500 km < 600 km radius => below surface => excluded from the conic cover (FIX #27)
            // => the descent points in that span become a TracedPath run, NOT a StockConic segment.
            var traj = new MockTrajectory
            {
                RecordingId = "rec-desc",
                Points = new List<TrajectoryPoint> { Pt(12, "Kerbin"), Pt(16, "Kerbin"), Pt(20, "Kerbin"), Pt(24, "Kerbin"), Pt(28, "Kerbin") },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Kerbin", semiMajorAxis = 500000, eccentricity = 0 },
                },
            };
            var chain = ChainAssembler.Build(traj, 0, 0, 0, 40, surface: KerbinRadius(600000));
            Assert.DoesNotContain(chain.Segments, s => s.Treatment == Treatment.StockConic);
            Assert.Contains(chain.Segments, s => s.Treatment == Treatment.TracedPath && s.FrameBodyName == "Kerbin");
        }

        [Fact]
        public void Build_AboveSurfaceOrbit_WithProvider_StaysStockConic()
        {
            // periapsis 700 km > 600 km radius => above surface => the provider does NOT exclude it.
            var traj = new MockTrajectory
            {
                RecordingId = "rec-orb",
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 30, bodyName = "Kerbin", semiMajorAxis = 700000, eccentricity = 0 },
                },
            };
            var chain = ChainAssembler.Build(traj, 0, 0, 0, 40, surface: KerbinRadius(600000));
            Assert.Single(chain.Segments);
            Assert.Equal(Treatment.StockConic, chain.Segments[0].Treatment);
        }

        [Fact]
        public void Build_CoalescesSameOrbitFragments_IntoOneStockConic()
        {
            // The recorder split one parking coast into 3 same-orbit fragments with sampling gaps
            // (background/foreground switches), the last a ~40s tail. The chain must render ONE StockConic
            // for the coast (not three short ones the loop clock flashes across), and keep the escape burn
            // (different orbit) as its own segment. This is the s15 "Kerbal X" decouple-seam fix at the
            // chain/director path (the legacy path coalesces separately in ReaimSegmentAssembler).
            var traj = new MockTrajectory
            {
                RecordingId = "rec-frag",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10, endUT = 100, bodyName = "Kerbin", semiMajorAxis = 700000, eccentricity = 0.001, epoch = 10 },
                    new OrbitSegment { startUT = 150, endUT = 300, bodyName = "Kerbin", semiMajorAxis = 700000, eccentricity = 0.001, epoch = 150 }, // 50s gap
                    new OrbitSegment { startUT = 311, endUT = 351, bodyName = "Kerbin", semiMajorAxis = 700000, eccentricity = 0.001, epoch = 311 }, // 11s gap, ~40s tail
                    new OrbitSegment { startUT = 400, endUT = 5000, bodyName = "Kerbin", semiMajorAxis = -380000, eccentricity = 1.2, epoch = 400 }, // escape burn
                },
            };
            var chain = ChainAssembler.Build(traj, 0, 0, 0, 6000);

            Assert.Equal(2, chain.SegmentCount); // one coalesced parking + the escape (was 4)
            Assert.All(chain.Segments, s => Assert.Equal(Treatment.StockConic, s.Treatment));
            Assert.Equal(10.0, chain.Segments[0].StartUT, 3);   // first fragment's start
            Assert.Equal(351.0, chain.Segments[0].EndUT, 3);    // last parking fragment's end (spans gaps)
            Assert.Equal(-380000.0, chain.Segments[1].Payload.Conic.semiMajorAxis, 0); // escape kept separate
        }
    }
}
