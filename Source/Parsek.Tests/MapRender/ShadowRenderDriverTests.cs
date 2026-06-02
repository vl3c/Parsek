using System.Collections.Generic;
using Parsek;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-4 guard for the decision-only shadow driver's PURE core: the scope classifier (which
    /// ghosts the MVP shadows vs skips) and the end-to-end pipeline composition DecideForGhost
    /// (assemble → sample → decide). The scene-iterating RunFrame is KSP-coupled and validated
    /// in-game; here we lock the decision logic the reconciler signal depends on.
    ///
    /// What makes it fail: a re-aim / overlap member is shadowed (emitting reconciler noise the MVP
    /// must skip), or the composed pipeline routes a faithful ghost to the wrong treatment / fails to
    /// hold across a gap.
    /// </summary>
    public class ShadowRenderDriverTests
    {
        // ---- ClassifyScope (per-member: heliocentric member skipped, faithful members shadowed) ----

        [Fact]
        public void ClassifyScope_NoUnit_NotHeliocentric_IsFaithful()
        {
            // Faithful non-loop recording (or non-member) → shadow it.
            Assert.Equal(ShadowRenderDriver.ShadowScope.Faithful,
                ShadowRenderDriver.ClassifyScope(memberIsHeliocentric: false, hasUnit: false, overlapCadenceSeconds: 0, spanSeconds: 0));
        }

        [Fact]
        public void ClassifyScope_HeliocentricMember_SkipsReaim()
        {
            // The Sun-relative transfer member is the re-synthesized one → skip.
            Assert.Equal(ShadowRenderDriver.ShadowScope.SkipReaim,
                ShadowRenderDriver.ClassifyScope(memberIsHeliocentric: true, hasUnit: true, overlapCadenceSeconds: 1000, spanSeconds: 1000));
        }

        [Fact]
        public void ClassifyScope_FaithfulMemberOfReaimMission_IsShadowed()
        {
            // A Kerbin-departure / Duna-arrival member of a re-aimed mission is NOT heliocentric → it
            // is faithful and IS shadowed (the key fix: skip per member, not per mission). This is the
            // exact in-game case: a Duna mission's Kerbin-orbit parking member.
            Assert.Equal(ShadowRenderDriver.ShadowScope.Faithful,
                ShadowRenderDriver.ClassifyScope(memberIsHeliocentric: false, hasUnit: true, overlapCadenceSeconds: 1000, spanSeconds: 1000));
        }

        [Fact]
        public void ClassifyScope_OverlapMember_SkipsOverlap()
        {
            // launch cadence (200s) shorter than span (1000s) → several instances live at once → skip.
            Assert.Equal(ShadowRenderDriver.ShadowScope.SkipOverlap,
                ShadowRenderDriver.ClassifyScope(memberIsHeliocentric: false, hasUnit: true, overlapCadenceSeconds: 200, spanSeconds: 1000));
        }

        [Fact]
        public void ClassifyScope_HeliocentricTakesPriorityOverOverlap()
        {
            // A heliocentric member that also overlaps is skipped as re-aim (the more specific reason).
            Assert.Equal(ShadowRenderDriver.ShadowScope.SkipReaim,
                ShadowRenderDriver.ClassifyScope(memberIsHeliocentric: true, hasUnit: true, overlapCadenceSeconds: 200, spanSeconds: 1000));
        }

        [Fact]
        public void ClassifyScope_DegenerateSpan_IsFaithful()
        {
            Assert.Equal(ShadowRenderDriver.ShadowScope.Faithful,
                ShadowRenderDriver.ClassifyScope(memberIsHeliocentric: false, hasUnit: true, overlapCadenceSeconds: 5, spanSeconds: 0));
        }

        // ---- ShouldSkipReaimSegment (re-aim decided PER ACTIVE SEGMENT, not per whole trajectory) ----

        [Fact]
        public void ShouldSkipReaimSegment_HeliocentricLeg_Skips()
        {
            // Flying the re-synthesized Sun-relative transfer leg → skip (raw conic points where the
            // target used to be).
            Assert.True(ShadowRenderDriver.ShouldSkipReaimSegment(intentVisible: true, frameBodyIsStar: true));
        }

        [Fact]
        public void ShouldSkipReaimSegment_FaithfulKerbinLeg_DoesNotSkip()
        {
            // The same interplanetary recording's FAITHFUL Kerbin escape / destination arrival legs must
            // render (the "Kerbal X" hyperbolic escape icon-off-orbit regression when they were dropped).
            Assert.False(ShadowRenderDriver.ShouldSkipReaimSegment(intentVisible: true, frameBodyIsStar: false));
        }

        [Fact]
        public void ShouldSkipReaimSegment_HiddenIntent_DoesNotSkip()
        {
            // A hidden intent has no active segment to classify; nothing to skip.
            Assert.False(ShadowRenderDriver.ShouldSkipReaimSegment(intentVisible: false, frameBodyIsStar: true));
        }

        // ---- DecideForGhost composition (faithful, Empty units = identity span clock, null surface) ----

        private static TrajectoryPoint Pt(double ut, string body)
            => new TrajectoryPoint { ut = ut, bodyName = body };

        // ascent (Kerbin 0..8, TracedPath) → orbit (Kerbin 10..30, StockConic) → arrival (Mun 32..36, TracedPath)
        private static MockTrajectory FaithfulChain()
            => new MockTrajectory
            {
                RecordingId = "rec-shadow",
                VesselName = "Jeb's Ride",
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

        private static GhostRenderIntent Decide(double currentUT, GhostRenderIntent prior = default(GhostRenderIntent))
            => ShadowRenderDriver.DecideForGhost(
                FaithfulChain(), committedIndex: 0, windowStartUT: 0, windowEndUT: 40,
                currentUT: currentUT, units: GhostPlaybackLogic.LoopUnitSet.Empty, surface: null, prior: prior);

        [Fact]
        public void DecideForGhost_InOrbitSegment_IsStockConicVisible()
        {
            var intent = Decide(20.0);
            Assert.True(intent.Visible);
            Assert.Equal(Treatment.StockConic, intent.Treatment);
            Assert.Equal("Kerbin", intent.FrameBodyName);
            Assert.Equal(20.0, intent.DriveUT); // identity span clock under Empty units
        }

        [Fact]
        public void DecideForGhost_InAscentPoints_IsTracedPathVisible()
        {
            var intent = Decide(4.0);
            Assert.True(intent.Visible);
            Assert.Equal(Treatment.TracedPath, intent.Treatment);
            Assert.Equal("Kerbin", intent.FrameBodyName);
        }

        [Fact]
        public void DecideForGhost_InArrivalPoints_IsTracedPathOnDestinationBody()
        {
            var intent = Decide(34.0);
            Assert.True(intent.Visible);
            Assert.Equal(Treatment.TracedPath, intent.Treatment);
            Assert.Equal("Mun", intent.FrameBodyName);
        }

        [Fact]
        public void DecideForGhost_PastWindowEnd_IsHidden()
        {
            var intent = Decide(50.0);
            Assert.False(intent.Visible);
            Assert.Equal(Treatment.None, intent.Treatment);
        }

        [Fact]
        public void DecideForGhost_InInteriorGap_HoldsPriorVisible()
        {
            var prior = Decide(20.0);          // visible StockConic on the orbit
            var held = Decide(31.0, prior);    // 31 is in the [30,32] gap before the Mun arrival run
            Assert.True(held.Visible);          // held, not blinked off
            Assert.Equal(prior.Treatment, held.Treatment);
            Assert.Equal(prior.DriveUT, held.DriveUT);
        }

        [Fact]
        public void DecideForGhost_InInteriorGap_NoPrior_IsHidden()
        {
            var intent = Decide(31.0);          // gap, nothing drawn yet
            Assert.False(intent.Visible);
        }
    }
}
