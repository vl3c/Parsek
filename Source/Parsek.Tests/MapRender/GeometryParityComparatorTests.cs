using System.Collections.Generic;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-2 guard for <see cref="GeometryParityComparator"/> (migration plan §4): the PURE byte-parity
    /// comparator over the GEOMETRY fields of a <see cref="PhaseChain"/> vs a <see cref="GhostRenderChain"/>.
    /// Covers (1) a matching chain returns <see cref="GeometryParityComparator.ParityResult.Match"/>, and
    /// (2) a deliberately-mismatched chain on EACH geometry field (treatment / UTs / frame / conic
    /// elements / chain window / faithful-fallback / count) returns the right diverging field.
    /// Crucially: a chain that differs ONLY in <see cref="PhaseKind"/> / <see cref="SegmentProvenance"/>
    /// (the NON-parity fields) must still MATCH.
    ///
    /// Each assertion states the bug it catches: a false MATCH would let the Phase-2 shadow gate pass a
    /// wrong-geometry factory; a false DIVERGE (e.g. flagging a kind/provenance difference) would spam
    /// the factory-parity anomaly on a correct factory and block Phase 3.
    /// </summary>
    public class GeometryParityComparatorTests
    {
        private static readonly AnchorFrame Kerbin = new AnchorFrame.BodyAnchor("Kerbin");
        private static readonly AnchorFrame Mun = new AnchorFrame.BodyAnchor("Mun");

        private static OrbitSegment Conic(string body, double s, double e, double sma = 7e5)
            => new OrbitSegment { startUT = s, endUT = e, bodyName = body, semiMajorAxis = sma };

        private static PhaseId Id(int ord) => new PhaseId("rec-A", 0, ord);

        // A reference chain: ascent (Kerbin traced 0..10), loiter (Kerbin conic 10..20), arrival (Mun
        // traced 20..30). Window [0,30].
        private static (PhaseChain factory, GhostRenderChain assembler) BuildAligned()
        {
            var phases = new List<TrajectoryPhase>
            {
                new AscentPhase(Id(0), SegmentProvenance.Recorded, Kerbin, 0, 10,
                    trailingSeam: PhaseSeam.Rigid()),
                new DepartureLoiterPhase(Id(1), SegmentProvenance.Recorded, Kerbin, 10, 20, Conic("Kerbin", 10, 20),
                    leadingSeam: PhaseSeam.Rigid(), trailingSeam: PhaseSeam.FlexibleSoi(null)),
                new DescentPhase(Id(2), SegmentProvenance.Recorded, Mun, 20, 30,
                    leadingSeam: PhaseSeam.FlexibleSoi(null)),
            };
            var factory = new PhaseChain("rec-A", 3, 0, phases, 0, 30, false);

            var segs = new List<RenderSegment>
            {
                new RenderSegment(SegmentKind.Surface, Treatment.TracedPath, 0, 10, "Kerbin", SegmentPayload.Traced,
                    leadingSeam: SeamKind.None, trailingSeam: SeamKind.Rigid),
                new RenderSegment(SegmentKind.Loiter, Treatment.StockConic, 10, 20, "Kerbin",
                    SegmentPayload.ForConic(Conic("Kerbin", 10, 20)),
                    leadingSeam: SeamKind.Rigid, trailingSeam: SeamKind.FlexibleSoi),
                new RenderSegment(SegmentKind.Surface, Treatment.TracedPath, 20, 30, "Mun", SegmentPayload.Traced,
                    leadingSeam: SeamKind.FlexibleSoi),
            };
            var assembler = new GhostRenderChain("rec-A", 3, 0, segs, 0, 30, false);
            return (factory, assembler);
        }

        // ---- Match cases ----

        [Fact]
        public void AlignedChains_Match()
        {
            var (factory, assembler) = BuildAligned();
            var r = GeometryParityComparator.Compare(factory, assembler);
            Assert.True(r.IsMatch, r.ToString());
            Assert.Null(r.DivergingField);
            Assert.False(r.CountMismatch);
        }

        [Fact]
        public void BothNull_Match()
        {
            Assert.True(GeometryParityComparator.Compare(null, null).IsMatch);
        }

        [Fact]
        public void DiffOnlyInKindOrProvenance_StillMatches()
        {
            // The factory phase uses a DIFFERENT legacy kind + provenance than the assembler's segment, but
            // the GEOMETRY (treatment/UT/body/conic) is identical -> MATCH (kind/provenance are non-parity).
            var phases = new List<TrajectoryPhase>
            {
                // ArrivalLoiterPhase emits SegmentKind.ArrivalLoiter; the assembler segment below is Loiter.
                new ArrivalLoiterPhase(Id(0), SegmentProvenance.Synthesized, Kerbin, 10, 20, Conic("Kerbin", 10, 20)),
            };
            var factory = new PhaseChain("rec-A", 0, 0, phases, 0, 30, false);

            var segs = new List<RenderSegment>
            {
                new RenderSegment(SegmentKind.Loiter, Treatment.StockConic, 10, 20, "Kerbin",
                    SegmentPayload.ForConic(Conic("Kerbin", 10, 20)), isGenerated: false),
            };
            var assembler = new GhostRenderChain("rec-A", 0, 0, segs, 0, 30, false);

            Assert.True(GeometryParityComparator.Compare(factory, assembler).IsMatch);
        }

        [Fact]
        public void HoldPhase_EmitsNothing_DoesNotDisruptAlignment()
        {
            // A HoldPhase (no geometry) interleaved in the factory chain must not shift the projected
            // segment alignment - the assembler never emits a hold.
            var phases = new List<TrajectoryPhase>
            {
                new DepartureLoiterPhase(Id(0), SegmentProvenance.Recorded, Kerbin, 10, 20, Conic("Kerbin", 10, 20)),
                new HoldPhase(Id(1), Kerbin, 20, 25),
                new DescentPhase(Id(2), SegmentProvenance.Recorded, Mun, 25, 30),
            };
            var factory = new PhaseChain("rec-A", 0, 0, phases, 0, 30, false);
            var segs = new List<RenderSegment>
            {
                new RenderSegment(SegmentKind.Loiter, Treatment.StockConic, 10, 20, "Kerbin",
                    SegmentPayload.ForConic(Conic("Kerbin", 10, 20))),
                new RenderSegment(SegmentKind.Surface, Treatment.TracedPath, 25, 30, "Mun", SegmentPayload.Traced),
            };
            var assembler = new GhostRenderChain("rec-A", 0, 0, segs, 0, 30, false);

            Assert.True(GeometryParityComparator.Compare(factory, assembler).IsMatch);
        }

        // ---- Deliberate divergence per field ----

        [Fact]
        public void TreatmentMismatch_Diverges()
        {
            var (factory, assembler) = BuildAligned();
            // Replace the assembler's loiter conic with a TracedPath (treatment mismatch).
            var segs = new List<RenderSegment>(assembler.Segments);
            segs[1] = new RenderSegment(SegmentKind.Loiter, Treatment.TracedPath, 10, 20, "Kerbin", SegmentPayload.Traced);
            var mutated = new GhostRenderChain("rec-A", 3, 0, segs, 0, 30, false);

            var r = GeometryParityComparator.Compare(factory, mutated);
            Assert.False(r.IsMatch);
            Assert.Equal("Treatment", r.DivergingField);
            Assert.Equal(1, r.SegmentIndex);
        }

        [Fact]
        public void StartUtMismatch_Diverges()
        {
            var (factory, assembler) = BuildAligned();
            var segs = new List<RenderSegment>(assembler.Segments);
            segs[0] = new RenderSegment(SegmentKind.Surface, Treatment.TracedPath, 0.5, 10, "Kerbin", SegmentPayload.Traced);
            var mutated = new GhostRenderChain("rec-A", 3, 0, segs, 0, 30, false);
            var r = GeometryParityComparator.Compare(factory, mutated);
            Assert.Equal("StartUt", r.DivergingField);
            Assert.Equal(0, r.SegmentIndex);
        }

        [Fact]
        public void EndUtMismatch_Diverges()
        {
            var (factory, assembler) = BuildAligned();
            var segs = new List<RenderSegment>(assembler.Segments);
            segs[2] = new RenderSegment(SegmentKind.Surface, Treatment.TracedPath, 20, 29.5, "Mun", SegmentPayload.Traced);
            var mutated = new GhostRenderChain("rec-A", 3, 0, segs, 0, 30, false);
            var r = GeometryParityComparator.Compare(factory, mutated);
            Assert.Equal("EndUt", r.DivergingField);
        }

        [Fact]
        public void FrameBodyMismatch_Diverges()
        {
            var (factory, assembler) = BuildAligned();
            var segs = new List<RenderSegment>(assembler.Segments);
            segs[2] = new RenderSegment(SegmentKind.Surface, Treatment.TracedPath, 20, 30, "Minmus", SegmentPayload.Traced);
            var mutated = new GhostRenderChain("rec-A", 3, 0, segs, 0, 30, false);
            var r = GeometryParityComparator.Compare(factory, mutated);
            Assert.Equal("FrameBodyName", r.DivergingField);
        }

        [Fact]
        public void HasConicMismatch_Diverges()
        {
            var (factory, assembler) = BuildAligned();
            var segs = new List<RenderSegment>(assembler.Segments);
            // assembler loiter has a conic; drop it (Traced payload but still StockConic treatment).
            segs[1] = new RenderSegment(SegmentKind.Loiter, Treatment.StockConic, 10, 20, "Kerbin", SegmentPayload.Traced);
            var mutated = new GhostRenderChain("rec-A", 3, 0, segs, 0, 30, false);
            var r = GeometryParityComparator.Compare(factory, mutated);
            Assert.Equal("HasConic", r.DivergingField);
        }

        [Fact]
        public void ConicElementMismatch_Diverges()
        {
            var (factory, assembler) = BuildAligned();
            var segs = new List<RenderSegment>(assembler.Segments);
            // different sma in the loiter conic
            segs[1] = new RenderSegment(SegmentKind.Loiter, Treatment.StockConic, 10, 20, "Kerbin",
                SegmentPayload.ForConic(Conic("Kerbin", 10, 20, sma: 8e5)));
            var mutated = new GhostRenderChain("rec-A", 3, 0, segs, 0, 30, false);
            var r = GeometryParityComparator.Compare(factory, mutated);
            Assert.Equal("conic.semiMajorAxis", r.DivergingField);
        }

        [Fact]
        public void ConicIsPredictedMismatch_Diverges()
        {
            var phases = new List<TrajectoryPhase>
            {
                new DepartureLoiterPhase(Id(0), SegmentProvenance.Recorded, Kerbin, 10, 20,
                    new OrbitSegment { startUT = 10, endUT = 20, bodyName = "Kerbin", semiMajorAxis = 7e5, isPredicted = false }),
            };
            var factory = new PhaseChain("rec-A", 0, 0, phases, 0, 30, false);
            var segs = new List<RenderSegment>
            {
                new RenderSegment(SegmentKind.Loiter, Treatment.StockConic, 10, 20, "Kerbin",
                    SegmentPayload.ForConic(new OrbitSegment { startUT = 10, endUT = 20, bodyName = "Kerbin", semiMajorAxis = 7e5, isPredicted = true })),
            };
            var assembler = new GhostRenderChain("rec-A", 0, 0, segs, 0, 30, false);
            var r = GeometryParityComparator.Compare(factory, assembler);
            Assert.Equal("conic.isPredicted", r.DivergingField);
        }

        [Fact]
        public void WindowStartMismatch_Diverges()
        {
            var (factory, assembler) = BuildAligned();
            var mutated = new GhostRenderChain("rec-A", 3, 0, assembler.Segments, 1.0, 30, false);
            var r = GeometryParityComparator.Compare(factory, mutated);
            Assert.Equal("WindowStartUt", r.DivergingField);
            Assert.Equal(-1, r.SegmentIndex);
        }

        [Fact]
        public void WindowEndMismatch_Diverges()
        {
            var (factory, assembler) = BuildAligned();
            var mutated = new GhostRenderChain("rec-A", 3, 0, assembler.Segments, 0, 31, false);
            var r = GeometryParityComparator.Compare(factory, mutated);
            Assert.Equal("WindowEndUt", r.DivergingField);
        }

        [Fact]
        public void FaithfulFallbackMismatch_Diverges()
        {
            var (factory, assembler) = BuildAligned();
            var mutated = new GhostRenderChain("rec-A", 3, 0, assembler.Segments, 0, 30, true);
            var r = GeometryParityComparator.Compare(factory, mutated);
            Assert.Equal("IsFaithfulFallback", r.DivergingField);
        }

        [Fact]
        public void SegmentCountMismatch_Diverges_AndFlagsCountMismatch()
        {
            var (factory, assembler) = BuildAligned();
            // assembler has one extra (aligned) segment; the first 3 match, the 4th makes counts differ.
            var segs = new List<RenderSegment>(assembler.Segments)
            {
                new RenderSegment(SegmentKind.Surface, Treatment.TracedPath, 30, 40, "Mun", SegmentPayload.Traced),
            };
            var mutated = new GhostRenderChain("rec-A", 3, 0, segs, 0, 30, false);
            var r = GeometryParityComparator.Compare(factory, mutated);
            Assert.False(r.IsMatch);
            Assert.True(r.CountMismatch);
            Assert.Equal("segment-count", r.DivergingField);
        }

        [Fact]
        public void OneNull_Diverges()
        {
            var (factory, _) = BuildAligned();
            Assert.False(GeometryParityComparator.Compare(factory, null).IsMatch);
            Assert.False(GeometryParityComparator.Compare(null, new GhostRenderChain("x", 0, 0, null, 0, 1, false)).IsMatch);
        }

        [Fact]
        public void ProjectGeometry_SkipsHoldPhases()
        {
            var phases = new List<TrajectoryPhase>
            {
                new HoldPhase(Id(0), Kerbin, 0, 10),
                new DepartureLoiterPhase(Id(1), SegmentProvenance.Recorded, Kerbin, 10, 20, Conic("Kerbin", 10, 20)),
            };
            var chain = new PhaseChain("rec-A", 0, 0, phases, 0, 30, false);
            var projected = GeometryParityComparator.ProjectGeometry(chain);
            Assert.Single(projected);
            Assert.Equal(Treatment.StockConic, projected[0].Treatment);
        }
    }
}
