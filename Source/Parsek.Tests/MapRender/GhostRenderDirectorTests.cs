using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-3 guard for the director's intent invariants (design §6.4 / §6.5):
    ///  - InSegment → a single visible treatment at the sample's DriveUT;
    ///  - InInteriorGap → HOLD the prior intent (never blink/retire mid-gap), or hidden if nothing
    ///    was drawn yet;
    ///  - OutsideWindow → hidden;
    ///  - a treatment swap across frames still emits exactly one treatment per frame.
    ///
    /// A regression here is the historic bug family: icon blink (gap retiring instead of holding),
    /// double-draw (two treatments), or a stale/teleporting icon.
    /// </summary>
    public class GhostRenderDirectorTests
    {
        private static RenderSegment ConicSeg(double s, double e)
            => new RenderSegment(SegmentKind.Loiter, Treatment.StockConic, s, e, "Kerbin", SegmentPayload.ForConic(default(OrbitSegment)));

        private static RenderSegment TracedSeg(double s, double e)
            => new RenderSegment(SegmentKind.Ascent, Treatment.TracedPath, s, e, "Kerbin", SegmentPayload.Traced);

        [Fact]
        public void InSegment_EmitsSingleVisibleTreatment()
        {
            var sample = GhostSample.InSegment(ConicSeg(0, 10), 0, driveUT: 5);
            var intent = GhostRenderDirector.Decide(sample, default(GhostRenderIntent), "Kerbal X");
            Assert.True(intent.Visible);
            Assert.Equal(Treatment.StockConic, intent.Treatment);
            Assert.Equal(5, intent.DriveUT);
            Assert.Equal("Kerbin", intent.FrameBodyName);
            Assert.True(intent.Payload.HasConic);
            Assert.Equal("Kerbal X", intent.Label);
        }

        [Fact]
        public void Gap_WithVisiblePrior_HoldsPrior()
        {
            var prior = GhostRenderDirector.Decide(GhostSample.InSegment(ConicSeg(0, 10), 0, 9), default(GhostRenderIntent), "X");
            var held = GhostRenderDirector.Decide(GhostSample.Gap(driveUT: 11), prior, "X");
            Assert.True(held.Visible);                       // not blinked off
            Assert.Equal(prior.Treatment, held.Treatment);    // same surface held
            Assert.Equal(prior.DriveUT, held.DriveUT);        // frozen at last drawn point (no jump)
        }

        [Fact]
        public void Gap_WithNoPrior_IsHidden()
        {
            var intent = GhostRenderDirector.Decide(GhostSample.Gap(driveUT: 11), default(GhostRenderIntent), "X");
            Assert.False(intent.Visible);
        }

        [Fact]
        public void Outside_IsHidden()
        {
            var prior = GhostRenderDirector.Decide(GhostSample.InSegment(ConicSeg(0, 10), 0, 9), default(GhostRenderIntent), "X");
            var intent = GhostRenderDirector.Decide(GhostSample.Outside(), prior, "X");
            Assert.False(intent.Visible);
            Assert.Equal(Treatment.None, intent.Treatment);
        }

        [Fact]
        public void TreatmentSwap_EmitsExactlyOneTreatmentPerFrame()
        {
            // conic -> traced across two in-segment frames: each intent carries one treatment, never both
            var a = GhostRenderDirector.Decide(GhostSample.InSegment(ConicSeg(0, 10), 0, 9), default(GhostRenderIntent), "X");
            var b = GhostRenderDirector.Decide(GhostSample.InSegment(TracedSeg(10, 20), 1, 11), a, "X");
            Assert.Equal(Treatment.StockConic, a.Treatment);
            Assert.Equal(Treatment.TracedPath, b.Treatment);
            Assert.NotEqual(a.Treatment, b.Treatment);
        }

        // ---- Review N16: the headless twin of WarpThroughInteriorGapSpineInGameTest's warp sequence ----
        // The in-game test's three-frame sampler+director sequence is PURE (a hand-built PhaseChain, the
        // ChainSampler, the director - no Unity reads), so the warp-through-gap no-blink invariant is
        // pinned headless here; the in-game copy remains the in-KSP-runtime confirmation.

        [Fact]
        public void WarpStep_AcrossInteriorGap_HoldsPriorIntent_NoBlink_Headless()
        {
            // Two StockConic phases [100,400] and [700,1000] with the interior gap (400,700) between
            // them; units=Empty maps liveUT through unchanged. A single high-warp step lands IN the gap
            // (frame B): the director must HOLD the prior visible intent (no blink/retire), and the next
            // step past the gap (frame C) resumes visible - never Hidden once visible.
            var anchor = new AnchorFrame.BodyAnchor("Kerbin");
            var chain = new PhaseChain(
                "warp-gap-rec", committedIndex: 0, instanceKey: 0,
                phases: new System.Collections.Generic.List<TrajectoryPhase>
                {
                    new DepartureLoiterPhase(
                        new PhaseId("warp-gap", 0, 0), SegmentProvenance.Recorded, anchor, 100, 400,
                        new OrbitSegment { startUT = 100, endUT = 400, bodyName = "Kerbin", semiMajorAxis = 850000 }),
                    new ArrivalLoiterPhase(
                        new PhaseId("warp-gap", 0, 1), SegmentProvenance.Recorded, anchor, 700, 1000,
                        new OrbitSegment { startUT = 700, endUT = 1000, bodyName = "Kerbin", semiMajorAxis = 850000 }),
                },
                windowStartUt: 100, windowEndUt: 1000);
            var units = GhostPlaybackLogic.LoopUnitSet.Empty;

            // Fixture sanity: the gap must actually classify as an interior gap (non-vacuous hold).
            Assert.Equal(Coverage.InInteriorGap, chain.ClassifyCoverage(550.0, out _, out _));

            GhostSample sampleA = ChainSampler.Sample(chain, 250.0, units);
            GhostRenderIntent intentA = GhostRenderDirector.Decide(sampleA, GhostRenderIntent.Hidden(), "X");
            Assert.Equal(Coverage.InSegment, sampleA.Coverage);
            Assert.True(intentA.Visible);

            GhostSample sampleB = ChainSampler.Sample(chain, 550.0, units);
            GhostRenderIntent intentB = GhostRenderDirector.Decide(sampleB, intentA, "X");
            Assert.Equal(Coverage.InInteriorGap, sampleB.Coverage);
            Assert.True(intentB.Visible);                               // held, no blink
            Assert.Equal(intentA.Treatment, intentB.Treatment);         // no surface flip
            Assert.Equal(intentA.FrameBodyName, intentB.FrameBodyName); // no re-anchor blink

            GhostSample sampleC = ChainSampler.Sample(chain, 850.0, units);
            GhostRenderIntent intentC = GhostRenderDirector.Decide(sampleC, intentB, "X");
            Assert.Equal(Coverage.InSegment, sampleC.Coverage);
            Assert.True(intentC.Visible); // never Hidden once visible across A->B->C
        }
    }
}
