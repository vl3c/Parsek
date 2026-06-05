using Parsek;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-5 guard for the treatments' pure applicability predicates (design §6.5): exactly one
    /// treatment claims a given intent, which is what structurally prevents the polyline/orbit
    /// double-draw. The KSP-coupled drive (StockConicTreatment.SeedAndDrive seeds + propagates a live
    /// Orbit) is validated in-game at the 8a cutover, not here.
    ///
    /// What makes it fail: a treatment claims an intent it does not own (double-draw), or refuses one
    /// it does (a blank surface).
    /// </summary>
    public class TreatmentTests
    {
        private static GhostRenderIntent Conic(bool visible)
            => new GhostRenderIntent(visible, Treatment.StockConic, 5.0, "Kerbin",
                SegmentPayload.ForConic(default(OrbitSegment)), "X");

        private static GhostRenderIntent ConicNoPayload(bool visible)
            => new GhostRenderIntent(visible, Treatment.StockConic, 5.0, "Kerbin",
                SegmentPayload.Traced, "X");

        private static GhostRenderIntent Traced(bool visible)
            => new GhostRenderIntent(visible, Treatment.TracedPath, 5.0, "Kerbin",
                SegmentPayload.Traced, "X");

        [Fact]
        public void Kinds_AreDistinct()
        {
            Assert.Equal(Treatment.StockConic, new StockConicTreatment().Kind);
            Assert.Equal(Treatment.TracedPath, new TracedPathTreatment().Kind);
        }

        [Fact]
        public void StockConic_ShouldApply_OnlyForVisibleStockConicWithConic()
        {
            Assert.True(StockConicTreatment.ShouldApply(Conic(visible: true)));
            Assert.False(StockConicTreatment.ShouldApply(Conic(visible: false)));      // hidden
            Assert.False(StockConicTreatment.ShouldApply(ConicNoPayload(true)));        // no conic payload
            Assert.False(StockConicTreatment.ShouldApply(Traced(true)));               // other treatment
            Assert.False(StockConicTreatment.ShouldApply(GhostRenderIntent.Hidden())); // nothing
        }

        [Fact]
        public void TracedPath_ShouldApply_OnlyForVisibleTracedPath()
        {
            Assert.True(TracedPathTreatment.ShouldApply(Traced(visible: true)));
            Assert.False(TracedPathTreatment.ShouldApply(Traced(visible: false)));     // hidden
            Assert.False(TracedPathTreatment.ShouldApply(Conic(true)));                // other treatment
            Assert.False(TracedPathTreatment.ShouldApply(GhostRenderIntent.Hidden())); // nothing
        }

        [Fact]
        public void ExactlyOneTreatmentClaims_AnyVisibleIntent()
        {
            // The invariant the double-draw prevention rests on: for any visible intent, exactly one of
            // the two treatments applies.
            var conic = Conic(true);
            Assert.True(StockConicTreatment.ShouldApply(conic) ^ TracedPathTreatment.ShouldApply(conic));
            var traced = Traced(true);
            Assert.True(StockConicTreatment.ShouldApply(traced) ^ TracedPathTreatment.ShouldApply(traced));
            // A hidden intent: neither applies.
            var hidden = GhostRenderIntent.Hidden();
            Assert.False(StockConicTreatment.ShouldApply(hidden) || TracedPathTreatment.ShouldApply(hidden));
        }

        // --- Phase 8b.1: owned-leg routing (no-double-draw) ---

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ShouldOwnLeg_MirrorsTheDirectorTracedPathActiveFlag(bool directorTracedPathActive)
        {
            // The treatment owns (draws) the leg exactly when the Director's active segment for the pid
            // is a fresh TracedPath this frame - the SAME boolean the Driver uses to stand down on its
            // own direct draw and the suppression patches use to hide the stock proto. One shared
            // predicate => the leg is never drawn twice.
            Assert.Equal(
                directorTracedPathActive,
                TracedPathTreatment.ShouldOwnLeg(directorTracedPathActive));
        }
    }
}
