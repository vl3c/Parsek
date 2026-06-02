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
    }
}
