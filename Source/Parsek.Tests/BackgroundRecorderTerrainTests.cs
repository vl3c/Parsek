using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 7 (review pass 3 — P2 follow-up): the loaded-background sampler
    /// in <see cref="BackgroundRecorder"/> must mirror
    /// <see cref="FlightRecorder.CommitRecordedPoint"/>'s clearance gate so
    /// background-recorded SurfaceMobile sections get the same continuous
    /// terrain correction as foreground sections. Without this, a rover that
    /// goes out of focus and continues recording in the background would
    /// silently emit NaN clearance points and fall back to the legacy
    /// altitude path.
    ///
    /// <para>Pin the four-condition gate exhaustively: only the canonical
    /// (active && Absolute && SurfaceMobile && PQS) combination passes;
    /// every variant must fail closed and leave clearance at the NaN
    /// sentinel.</para>
    /// </summary>
    [Collection("Sequential")]
    public class BackgroundRecorderTerrainTests
    {
        // ----- gate predicate (pure-static helper) -----

        [Fact]
        public void ShouldEmit_AllFourConditionsTrue_GatePasses()
        {
            // The canonical SurfaceMobile case: active section, Absolute
            // frame, SurfaceMobile env, PQS controller present. This is the
            // only combination that should populate clearance.
            Assert.True(BackgroundRecorder.ShouldEmitBackgroundSurfaceMobileClearance(
                trackSectionActive: true,
                frame: ReferenceFrame.Absolute,
                env: SegmentEnvironment.SurfaceMobile,
                hasPqsController: true));
        }

        [Fact]
        public void ShouldEmit_TrackSectionInactive_GateFails()
        {
            // No active section ⇒ no anchor for the clearance to belong to.
            // The accumulator state would be orphaned across the eventual
            // section boundary; better to skip emission entirely.
            Assert.False(BackgroundRecorder.ShouldEmitBackgroundSurfaceMobileClearance(
                trackSectionActive: false,
                frame: ReferenceFrame.Absolute,
                env: SegmentEnvironment.SurfaceMobile,
                hasPqsController: true));
        }

        [Fact]
        public void ShouldEmit_RelativeFrame_GateFails()
        {
            // RELATIVE sections store metres in lat/lon/alt (anchor-local).
            // Calling TerrainAltitude with metre-scale lat would produce
            // nonsense. The gate must fail closed for safety.
            Assert.False(BackgroundRecorder.ShouldEmitBackgroundSurfaceMobileClearance(
                trackSectionActive: true,
                frame: ReferenceFrame.Relative,
                env: SegmentEnvironment.SurfaceMobile,
                hasPqsController: true));
        }

        [Fact]
        public void ShouldEmit_OrbitalCheckpointFrame_GateFails()
        {
            // OrbitalCheckpoint sections drive playback from Kepler orbits,
            // not lat/lon samples. SurfaceMobile + OrbitalCheckpoint should
            // never co-occur in practice, but the gate fails closed if it
            // somehow does.
            Assert.False(BackgroundRecorder.ShouldEmitBackgroundSurfaceMobileClearance(
                trackSectionActive: true,
                frame: ReferenceFrame.OrbitalCheckpoint,
                env: SegmentEnvironment.SurfaceMobile,
                hasPqsController: true));
        }

        [Theory]
        [InlineData(SegmentEnvironment.Atmospheric)]
        [InlineData(SegmentEnvironment.ExoBallistic)]
        [InlineData(SegmentEnvironment.ExoPropulsive)]
        [InlineData(SegmentEnvironment.SurfaceStationary)]
        [InlineData(SegmentEnvironment.Approach)]
        public void ShouldEmit_NonSurfaceMobileEnvironment_GateFails(SegmentEnvironment env)
        {
            // Terrain correction is meaningful only for ground-following
            // vessels (rovers, surface bases on wheels, kerbals walking).
            // Atmospheric / exo / approach / stationary sections fall
            // through to the legacy altitude path by NaN sentinel.
            Assert.False(BackgroundRecorder.ShouldEmitBackgroundSurfaceMobileClearance(
                trackSectionActive: true,
                frame: ReferenceFrame.Absolute,
                env: env,
                hasPqsController: true));
        }

        [Fact]
        public void ShouldEmit_NoPqsController_GateFails()
        {
            // Bodies without PQS (gas giants like Jool — vessels can be in
            // atmosphere but never have a SurfaceMobile section there). The
            // KSP `TerrainAltitude` API returns 0 in that case which would
            // produce a wrong-sign clearance equal to the recorded altitude.
            Assert.False(BackgroundRecorder.ShouldEmitBackgroundSurfaceMobileClearance(
                trackSectionActive: true,
                frame: ReferenceFrame.Absolute,
                env: SegmentEnvironment.SurfaceMobile,
                hasPqsController: false));
        }

        // ----- foreground / background gate parity -----

        [Theory]
        [InlineData(true,  ReferenceFrame.Absolute,         SegmentEnvironment.SurfaceMobile,    true)]
        [InlineData(false, ReferenceFrame.Absolute,         SegmentEnvironment.SurfaceMobile,    true)]
        [InlineData(true,  ReferenceFrame.Relative,         SegmentEnvironment.SurfaceMobile,    true)]
        [InlineData(true,  ReferenceFrame.OrbitalCheckpoint, SegmentEnvironment.SurfaceMobile,   true)]
        [InlineData(true,  ReferenceFrame.Absolute,         SegmentEnvironment.Atmospheric,      true)]
        [InlineData(true,  ReferenceFrame.Absolute,         SegmentEnvironment.SurfaceStationary, true)]
        [InlineData(true,  ReferenceFrame.Absolute,         SegmentEnvironment.SurfaceMobile,    false)]
        public void ShouldEmit_MatchesForegroundCommitRecordedPointGate(
            bool trackSectionActive,
            ReferenceFrame frame,
            SegmentEnvironment env,
            bool hasPqsController)
        {
            // The BG gate must produce identical pass/fail decisions to the
            // foreground inline gate at FlightRecorder.cs:5848-5851:
            //   trackSectionActive
            //   && currentTrackSection.referenceFrame == ReferenceFrame.Absolute
            //   && currentTrackSection.environment == SegmentEnvironment.SurfaceMobile
            //   && v.mainBody?.pqsController != null
            // Any divergence between the two would mean a vessel that drops
            // out of focus mid-section produces a clearance discontinuity.
            bool foregroundGate = trackSectionActive
                && frame == ReferenceFrame.Absolute
                && env == SegmentEnvironment.SurfaceMobile
                && hasPqsController;
            bool backgroundGate = BackgroundRecorder.ShouldEmitBackgroundSurfaceMobileClearance(
                trackSectionActive, frame, env, hasPqsController);

            Assert.Equal(foregroundGate, backgroundGate);
        }
    }
}
