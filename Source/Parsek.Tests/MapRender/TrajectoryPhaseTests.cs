using System.Linq;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-1 guard for the concrete <see cref="TrajectoryPhase"/> subclasses (design §6): each
    /// subclass's <see cref="TrajectoryPhase.ResolveTreatment"/>, <see cref="TrajectoryPhase.CoversUt"/>,
    /// and <see cref="TrajectoryPhase.Emit"/> (the typed-but-unwired geometry contract that Phase 2
    /// byte-parities against the legacy assembler).
    ///
    /// Each assertion states the bug it catches: a wrong treatment would draw a conic as a polyline
    /// (or vice-versa); a wrong CoversUt would mis-route a frame to the wrong phase or freeze a ghost
    /// across a warp-skipped hold; a wrong Emit body/seam/payload would break the Phase-2 geometry
    /// parity gate.
    /// </summary>
    public class TrajectoryPhaseTests
    {
        private static readonly AnchorFrame Kerbin = new AnchorFrame.BodyAnchor("Kerbin");
        private static readonly AnchorFrame Sun = new AnchorFrame.BodyAnchor("Sun");

        private static OrbitSegment Conic(string body, double start, double end)
            => new OrbitSegment { startUT = start, endUT = end, bodyName = body, semiMajorAxis = 7e5 };

        private static PhaseId Id(int ordinal) => new PhaseId("rec-A", 0, ordinal);
        private static SampleContext Ctx(double ut, string body = "Kerbin") => new SampleContext(ut, body);

        // ---- ResolveTreatment per subclass ----

        [Fact]
        public void Treatments_MatchTheSection6Table()
        {
            Assert.Equal(Treatment.TracedPath,
                new AscentPhase(Id(0), SegmentProvenance.Recorded, Kerbin, 0, 10).ResolveTreatment());
            Assert.Equal(Treatment.StockConic,
                new DepartureLoiterPhase(Id(1), SegmentProvenance.Recorded, Kerbin, 10, 20, Conic("Kerbin", 10, 20)).ResolveTreatment());
            Assert.Equal(Treatment.StockConic,
                new HeliocentricTransferPhase(Id(2), SegmentProvenance.Synthesized, Sun, 20, 30, Conic("Sun", 20, 30)).ResolveTreatment());
            Assert.Equal(Treatment.StockConic,
                new ArrivalLoiterPhase(Id(3), SegmentProvenance.Recorded, Kerbin, 30, 40, Conic("Duna", 30, 40)).ResolveTreatment());
            Assert.Equal(Treatment.TracedPath,
                new DescentPhase(Id(4), SegmentProvenance.Recorded, Kerbin, 40, 50).ResolveTreatment());
            Assert.Equal(Treatment.TracedPath,
                new SurfacePhase(Id(5), SegmentProvenance.Recorded, Kerbin, 50, 60).ResolveTreatment());
            Assert.Equal(Treatment.None,
                new HoldPhase(Id(6), Kerbin, 60, 70).ResolveTreatment());
        }

        // Treatment is internal -> pass the underlying int into the [Theory] (CS0051 idiom).
        [Theory]
        [InlineData((int)Treatment.StockConic)]
        [InlineData((int)Treatment.TracedPath)]
        public void DualTreatmentSoiDeparture_HonoursExplicitTreatment(int treatmentInt)
        {
            var treatment = (Treatment)treatmentInt;
            var p = new SoiDeparturePhase(
                Id(0), treatment, SegmentProvenance.Recorded, Kerbin, 0, 10, Conic("Kerbin", 0, 10));
            Assert.Equal(treatment, p.ResolveTreatment());
        }

        [Theory]
        [InlineData((int)Treatment.StockConic)]
        [InlineData((int)Treatment.TracedPath)]
        public void DualTreatmentSoiArrival_HonoursExplicitTreatment(int treatmentInt)
        {
            var treatment = (Treatment)treatmentInt;
            var p = new SoiArrivalPhase(
                Id(0), treatment, SegmentProvenance.Recorded, Sun, 0, 10, Conic("Duna", 0, 10));
            Assert.Equal(treatment, p.ResolveTreatment());
        }

        // ---- CoversUt ----

        [Theory]
        [InlineData(10.0, true)]   // inclusive start
        [InlineData(15.0, true)]
        [InlineData(19.9, true)]
        [InlineData(20.0, false)]  // exclusive end (belongs to the next phase)
        [InlineData(9.9, false)]
        [InlineData(25.0, false)]
        public void CoversUt_IsHalfOpenInterval(double ut, bool expected)
        {
            var p = new DepartureLoiterPhase(Id(0), SegmentProvenance.Recorded, Kerbin, 10, 20, Conic("Kerbin", 10, 20));
            Assert.Equal(expected, p.CoversUt(ut));
        }

        [Fact]
        public void CoversUt_NonFinite_IsFalse()
        {
            var p = new DepartureLoiterPhase(Id(0), SegmentProvenance.Recorded, Kerbin, 10, 20, Conic("Kerbin", 10, 20));
            Assert.False(p.CoversUt(double.NaN));
            Assert.False(p.CoversUt(double.PositiveInfinity));
        }

        [Theory]
        // A HoldPhase must cover its WHOLE span so a high-warp frame landing anywhere inside the hold
        // resolves to the hold (never a spurious gap that freezes the ghost for multiple frames, §11.3).
        [InlineData(100.0, true)]
        [InlineData(5000.0, true)]
        [InlineData(99999.0, true)]
        [InlineData(100000.0, false)] // exclusive end
        [InlineData(99.0, false)]
        public void HoldPhase_CoversWholeSpan_WarpStepSafe(double ut, bool expected)
        {
            var hold = new HoldPhase(Id(0), Kerbin, 100, 100000);
            Assert.Equal(expected, hold.CoversUt(ut));
        }

        // ---- Emit geometry contract ----

        [Fact]
        public void ConicPhase_Emits_OneStockConicSegment_WithConicPayloadAndBody()
        {
            var conic = Conic("Duna", 30, 40);
            var p = new ArrivalLoiterPhase(Id(0), SegmentProvenance.Recorded, new AnchorFrame.BodyAnchor("Duna"), 30, 40, conic);
            var segs = p.Emit(Ctx(35, "Duna")).ToList();

            Assert.Single(segs);
            var s = segs[0];
            Assert.Equal(Treatment.StockConic, s.Treatment);
            Assert.Equal(30, s.StartUT);
            Assert.Equal(40, s.EndUT);
            Assert.Equal("Duna", s.FrameBodyName);   // resolved from the BodyAnchor
            Assert.True(s.Payload.HasConic);
            Assert.Equal("Duna", s.Payload.Conic.bodyName);
            Assert.False(s.IsGenerated);             // Recorded provenance -> not generated
        }

        [Fact]
        public void TracedPhase_Emits_OneTracedPathSegment_NoConic()
        {
            var p = new DescentPhase(Id(0), SegmentProvenance.Recorded, new AnchorFrame.BodyAnchor("Duna"), 40, 50);
            var segs = p.Emit(Ctx(45, "Duna")).ToList();

            Assert.Single(segs);
            var s = segs[0];
            Assert.Equal(Treatment.TracedPath, s.Treatment);
            Assert.False(s.Payload.HasConic);
            Assert.Equal(SegmentKind.Landing, s.Kind); // §6 map: Landing -> Descent
            Assert.Equal("Duna", s.FrameBodyName);
        }

        [Fact]
        public void SynthesizedConicPhase_Emits_IsGenerated()
        {
            var p = new HeliocentricTransferPhase(
                Id(0), SegmentProvenance.Synthesized, Sun, 20, 30, Conic("Sun", 20, 30));
            var s = p.Emit(Ctx(25, "Sun")).Single();
            Assert.True(s.IsGenerated); // re-aim synthesized -> the legacy "generated" geometry flag
            Assert.Equal(SegmentKind.Transfer, s.Kind);
        }

        [Fact]
        public void HoldPhase_Emits_Nothing()
        {
            var hold = new HoldPhase(Id(0), Kerbin, 60, 70);
            Assert.Empty(hold.Emit(Ctx(65)));
        }

        [Fact]
        public void Emit_MapsPhaseSeam_DownToLegacySeamKind()
        {
            // A FlexibleSoi leading seam + Rigid trailing seam must surface on the emitted RenderSegment
            // as the legacy SeamKind the live draw path reads.
            var crossing = new SoiCrossing("Sun", "Duna", 30, 1e8);
            var p = new SoiArrivalPhase(
                Id(0), Treatment.StockConic, SegmentProvenance.Recorded, new AnchorFrame.BodyAnchor("Duna"),
                30, 40, Conic("Duna", 30, 40),
                leadingSeam: PhaseSeam.FlexibleSoi(crossing),
                trailingSeam: PhaseSeam.Rigid());
            var s = p.Emit(Ctx(35, "Duna")).Single();
            Assert.Equal(SeamKind.FlexibleSoi, s.LeadingSeam);
            Assert.Equal(SeamKind.Rigid, s.TrailingSeam);
        }

        [Fact]
        public void Emit_SwitchContinuationSeam_MapsToLegacyNone()
        {
            // SwitchContinuation is a member boundary, not an intra-chain seam -> legacy None.
            var p = new AscentPhase(
                Id(0), SegmentProvenance.Recorded, Kerbin, 0, 10,
                leadingSeam: PhaseSeam.SwitchContinuation());
            var s = p.Emit(Ctx(5)).Single();
            Assert.Equal(SeamKind.None, s.LeadingSeam);
        }

        [Fact]
        public void DualTreatment_TracedPath_EmitsNoConicPayload()
        {
            var p = new SoiDeparturePhase(
                Id(0), Treatment.TracedPath, SegmentProvenance.Recorded, Kerbin, 0, 10, Conic("Kerbin", 0, 10));
            var s = p.Emit(Ctx(5)).Single();
            Assert.Equal(Treatment.TracedPath, s.Treatment);
            Assert.False(s.Payload.HasConic);
            Assert.Equal(SegmentKind.Eject, s.Kind);
        }
    }
}
