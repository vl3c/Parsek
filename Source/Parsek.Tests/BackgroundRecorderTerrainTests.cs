using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 7 (review pass 3 — P2 follow-up): the loaded-background sampler
    /// in <see cref="BackgroundRecorder"/> must mirror
    /// <see cref="FlightRecorder.ShouldEmitSurfaceClearance"/>'s clearance
    /// gate so background-recorded surface sections get the same continuous
    /// terrain correction as foreground sections. Without this, a surface
    /// vessel that goes out of focus and continues recording in the background
    /// would silently emit NaN clearance points and fall back to the legacy
    /// altitude path.
    ///
    /// <para>Pin the four-condition gate exhaustively: only active Absolute
    /// surface sections with PQS pass; every other variant must fail closed
    /// and leave clearance at the NaN sentinel.</para>
    /// </summary>
    [Collection("Sequential")]
    public class BackgroundRecorderTerrainTests
    {
        // ----- gate predicate (pure-static helper) -----

        [Fact]
        public void ShouldEmit_SurfaceMobileAllFourConditionsTrue_GatePasses()
        {
            // The canonical SurfaceMobile case: active section, Absolute
            // frame, SurfaceMobile env, PQS controller present.
            Assert.True(BackgroundRecorder.ShouldEmitBackgroundSurfaceClearance(
                trackSectionActive: true,
                frame: ReferenceFrame.Absolute,
                env: SegmentEnvironment.SurfaceMobile,
                hasPqsController: true));
        }

        [Fact]
        public void ShouldEmit_SurfaceStationaryAllFourConditionsTrue_GatePasses()
        {
            // Recovered-on-ground recordings can end with a stationary surface
            // tail. Persist clearance there too so final ghost holds track the
            // current terrain instead of falling back to raw recorded altitude.
            Assert.True(BackgroundRecorder.ShouldEmitBackgroundSurfaceClearance(
                trackSectionActive: true,
                frame: ReferenceFrame.Absolute,
                env: SegmentEnvironment.SurfaceStationary,
                hasPqsController: true));
        }

        [Fact]
        public void ShouldEmit_TrackSectionInactive_GateFails()
        {
            // No active section ⇒ no anchor for the clearance to belong to.
            // The accumulator state would be orphaned across the eventual
            // section boundary; better to skip emission entirely.
            Assert.False(BackgroundRecorder.ShouldEmitBackgroundSurfaceClearance(
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
            Assert.False(BackgroundRecorder.ShouldEmitBackgroundSurfaceClearance(
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
            Assert.False(BackgroundRecorder.ShouldEmitBackgroundSurfaceClearance(
                trackSectionActive: true,
                frame: ReferenceFrame.OrbitalCheckpoint,
                env: SegmentEnvironment.SurfaceMobile,
                hasPqsController: true));
        }

        [Theory]
        [InlineData(SegmentEnvironment.Atmospheric)]
        [InlineData(SegmentEnvironment.ExoBallistic)]
        [InlineData(SegmentEnvironment.ExoPropulsive)]
        [InlineData(SegmentEnvironment.Approach)]
        public void ShouldEmit_NonSurfaceEnvironment_GateFails(SegmentEnvironment env)
        {
            // Terrain correction is meaningful only for surface sections.
            // Atmospheric / exo / approach sections fall through to the
            // legacy altitude path by NaN sentinel.
            Assert.False(BackgroundRecorder.ShouldEmitBackgroundSurfaceClearance(
                trackSectionActive: true,
                frame: ReferenceFrame.Absolute,
                env: env,
                hasPqsController: true));
        }

        [Fact]
        public void ShouldEmit_NoPqsController_GateFails()
        {
            // Bodies without PQS (gas giants like Jool — vessels can be in
            // atmosphere but never have a surface section there). The
            // KSP `TerrainAltitude` API returns 0 in that case which would
            // produce a wrong-sign clearance equal to the recorded altitude.
            Assert.False(BackgroundRecorder.ShouldEmitBackgroundSurfaceClearance(
                trackSectionActive: true,
                frame: ReferenceFrame.Absolute,
                env: SegmentEnvironment.SurfaceMobile,
                hasPqsController: false));
        }

        // ----- foreground / background gate parity -----

        [Theory]
        [InlineData(true,  ReferenceFrame.Absolute,         SegmentEnvironment.SurfaceMobile,    true)]
        [InlineData(true,  ReferenceFrame.Absolute,         SegmentEnvironment.SurfaceStationary, true)]
        [InlineData(false, ReferenceFrame.Absolute,         SegmentEnvironment.SurfaceMobile,    true)]
        [InlineData(true,  ReferenceFrame.Relative,         SegmentEnvironment.SurfaceMobile,    true)]
        [InlineData(true,  ReferenceFrame.OrbitalCheckpoint, SegmentEnvironment.SurfaceMobile,   true)]
        [InlineData(true,  ReferenceFrame.Absolute,         SegmentEnvironment.Atmospheric,      true)]
        [InlineData(true,  ReferenceFrame.Absolute,         SegmentEnvironment.SurfaceMobile,    false)]
        public void ShouldEmit_MatchesForegroundCommitRecordedPointGate(
            bool trackSectionActive,
            ReferenceFrame frame,
            SegmentEnvironment env,
            bool hasPqsController)
        {
            // The BG gate must produce identical pass/fail decisions to the
            // foreground helper used by FlightRecorder.CommitRecordedPoint.
            // Any divergence between the two would mean a vessel that drops
            // out of focus mid-section produces a clearance discontinuity.
            bool foregroundGate = FlightRecorder.ShouldEmitSurfaceClearance(
                trackSectionActive, frame, env, hasPqsController);
            bool backgroundGate = BackgroundRecorder.ShouldEmitBackgroundSurfaceClearance(
                trackSectionActive, frame, env, hasPqsController);

            Assert.Equal(foregroundGate, backgroundGate);
        }

        // ----- body-fixed shadow clearance gate (v13 follow-up) -----
        //
        // Under v13, parent-anchored debris records a body-fixed shadow into
        // TrackSection.bodyFixedFrames while the active section is RELATIVE.
        // The shadow carries genuine body-fixed lat/lon/alt and v13's
        // body-fixed primary playback path applies terrain correction via
        // recordedGroundClearance. The shadow-side gate therefore needs to
        // emit clearance for the SurfaceMobile/Stationary env case WITHOUT
        // gating on the section frame (the primary path's frame gate stays
        // in place to keep anchor-local Relative metres out of terrain math).

        [Fact]
        public void ShadowGate_RelativeSectionWithSurfaceMobileAndPqs_GatePasses()
        {
            // The defining v13 case: parent-anchored surface debris recorded
            // inside a parent-relative section. The primary gate would fail
            // closed (frame=Relative), but the shadow gate must emit so the
            // body-fixed shadow gets v9 terrain correction at playback.
            Assert.True(BackgroundRecorder.ShouldEmitBackgroundBodyFixedShadowClearance(
                trackSectionActive: true,
                env: SegmentEnvironment.SurfaceMobile,
                hasPqsController: true));
        }

        [Fact]
        public void ShadowGate_RelativeSectionWithSurfaceStationaryAndPqs_GatePasses()
        {
            // Same as above for a stationary-on-surface debris piece.
            Assert.True(BackgroundRecorder.ShouldEmitBackgroundBodyFixedShadowClearance(
                trackSectionActive: true,
                env: SegmentEnvironment.SurfaceStationary,
                hasPqsController: true));
        }

        [Fact]
        public void ShadowGate_TrackSectionInactive_GateFails()
        {
            // No section ⇒ nothing to attach the shadow clearance to.
            Assert.False(BackgroundRecorder.ShouldEmitBackgroundBodyFixedShadowClearance(
                trackSectionActive: false,
                env: SegmentEnvironment.SurfaceMobile,
                hasPqsController: true));
        }

        [Theory]
        [InlineData(SegmentEnvironment.Atmospheric)]
        [InlineData(SegmentEnvironment.ExoBallistic)]
        [InlineData(SegmentEnvironment.ExoPropulsive)]
        [InlineData(SegmentEnvironment.Approach)]
        public void ShadowGate_NonSurfaceEnvironment_GateFails(SegmentEnvironment env)
        {
            // Terrain correction is meaningful only for surface sections.
            // The shadow gate inherits the env restriction.
            Assert.False(BackgroundRecorder.ShouldEmitBackgroundBodyFixedShadowClearance(
                trackSectionActive: true,
                env: env,
                hasPqsController: true));
        }

        [Fact]
        public void ShadowGate_NoPqsController_GateFails()
        {
            // Gas giants (no PQS) -- the shadow gate still fails closed.
            Assert.False(BackgroundRecorder.ShouldEmitBackgroundBodyFixedShadowClearance(
                trackSectionActive: true,
                env: SegmentEnvironment.SurfaceMobile,
                hasPqsController: false));
        }

        [Fact]
        public void ShadowGate_DivergesFromPrimaryGateOnlyByFrame()
        {
            // The shadow gate must agree with the primary gate on every
            // input EXCEPT the frame: the shadow's body-fixed lat/lon/alt
            // is meaningful even when the active section is RELATIVE.
            foreach (bool active in new[] { false, true })
            foreach (var env in new[]
            {
                SegmentEnvironment.SurfaceMobile,
                SegmentEnvironment.SurfaceStationary,
                SegmentEnvironment.Atmospheric,
                SegmentEnvironment.ExoBallistic,
                SegmentEnvironment.ExoPropulsive,
            })
            foreach (bool pqs in new[] { false, true })
            {
                bool shadow = BackgroundRecorder.ShouldEmitBackgroundBodyFixedShadowClearance(
                    active, env, pqs);
                bool primaryAsAbsolute = BackgroundRecorder.ShouldEmitBackgroundSurfaceClearance(
                    active, ReferenceFrame.Absolute, env, pqs);
                Assert.Equal(primaryAsAbsolute, shadow);
            }
        }
    }
}
