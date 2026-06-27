using System;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-1 guard for <see cref="AnchorFrame"/> + <see cref="AnchorFrameResolver"/> (design §5.2).
    /// Covers the union's discriminator/token surface and the two RESOLVABLE v1 variants:
    /// <see cref="AnchorFrame.BodyAnchor"/> (missing-body fail-closed, never NRE) and
    /// <see cref="AnchorFrame.ParentAnchoredChild"/> (bodyFixedFrames-primary ≥2-sample,
    /// out-of-range → RETIRE, never clamp).
    ///
    /// Each assertion states the bug it catches: a wrong fail-closed outcome would NRE on a renamed
    /// modded body (or render a phantom); a wrong parent-anchored surface would clamp a stale child
    /// offset (the documented "stale ghost" bug) instead of retiring, or pick the secondary surface
    /// while the body-fixed primary still covers the UT.
    /// </summary>
    public class AnchorFrameTests
    {
        // ---- Union shape / tokens ----

        [Fact]
        public void Variants_CarryKindAndToken()
        {
            Assert.Equal(AnchorFrameKind.Body, new AnchorFrame.BodyAnchor("Kerbin").Kind);
            Assert.Equal("body", new AnchorFrame.BodyAnchor("Kerbin").ToToken());

            Assert.Equal(AnchorFrameKind.ParentAnchoredChild, new AnchorFrame.ParentAnchoredChild("rec-P").Kind);
            Assert.Equal("parent-anchored-child", new AnchorFrame.ParentAnchoredChild("rec-P").ToToken());

            Assert.Equal(AnchorFrameKind.ParentGeneratedConic,
                new AnchorFrame.ParentGeneratedConicAnchor(new PhaseId("rec", 0, 1)).Kind);
            Assert.Equal("parent-generated-conic",
                new AnchorFrame.ParentGeneratedConicAnchor(new PhaseId("rec", 0, 1)).ToToken());

            Assert.Equal(AnchorFrameKind.LiveVessel, new AnchorFrame.LiveVesselAnchor(Guid.NewGuid()).Kind);
            Assert.Equal("live-vessel", new AnchorFrame.LiveVesselAnchor(Guid.NewGuid()).ToToken());

            Assert.Equal(AnchorFrameKind.RecordedAnchor, new AnchorFrame.RecordedAnchorTrajectory("rec-A").Kind);
            Assert.Equal("recorded-anchor", new AnchorFrame.RecordedAnchorTrajectory("rec-A").ToToken());
        }

        [Fact]
        public void BodyAnchor_PreservesName()
        {
            Assert.Equal("Duna", new AnchorFrame.BodyAnchor("Duna").BodyName);
        }

        // ---- BodyAnchor resolution (fail-closed, never NRE) ----

        [Fact]
        public void ResolveBody_KnownStockBody_Resolves()
        {
            Func<string, bool> exists = name => name == "Kerbin" || name == "Mun" || name == "Duna";
            Assert.Equal(AnchorFrameResolver.BodyResolveOutcome.Resolved,
                AnchorFrameResolver.ResolveBody("Kerbin", exists));
            Assert.True(AnchorFrameResolver.TryResolveBody("Mun", exists));
        }

        [Fact]
        public void ResolveBody_NeverVisitedStockBody_StillResolves()
        {
            // Discovery level is irrelevant: the probe answers on EXISTENCE, so a never-visited stock
            // body must resolve normally (design §11.4).
            Func<string, bool> existsRegardlessOfDiscovery = _ => true;
            Assert.Equal(AnchorFrameResolver.BodyResolveOutcome.Resolved,
                AnchorFrameResolver.ResolveBody("Eeloo", existsRegardlessOfDiscovery));
        }

        [Fact]
        public void ResolveBody_MissingName_FailsClosed_NotNre()
        {
            Func<string, bool> exists = _ => true;
            Assert.Equal(AnchorFrameResolver.BodyResolveOutcome.FailClosedMissingName,
                AnchorFrameResolver.ResolveBody(null, exists));
            Assert.Equal(AnchorFrameResolver.BodyResolveOutcome.FailClosedMissingName,
                AnchorFrameResolver.ResolveBody("   ", exists));
            Assert.False(AnchorFrameResolver.TryResolveBody(null, exists));
        }

        [Fact]
        public void ResolveBody_UnknownBody_FailsClosed()
        {
            // A renamed/removed modded body absent from the live set fails closed (never NRE).
            Func<string, bool> exists = name => name == "Kerbin";
            Assert.Equal(AnchorFrameResolver.BodyResolveOutcome.FailClosedUnknownBody,
                AnchorFrameResolver.ResolveBody("OPM_Sarnus", exists));
            Assert.False(AnchorFrameResolver.TryResolveBody("OPM_Sarnus", exists));
        }

        [Fact]
        public void ResolveBody_NullProbe_FailsClosed_NotNre()
        {
            Assert.Equal(AnchorFrameResolver.BodyResolveOutcome.FailClosedUnknownBody,
                AnchorFrameResolver.ResolveBody("Kerbin", null));
        }

        [Fact]
        public void ResolveBody_ThrowingProbe_FailsClosed_NotPropagated()
        {
            Func<string, bool> throwing = _ => throw new InvalidOperationException("boom");
            Assert.Equal(AnchorFrameResolver.BodyResolveOutcome.FailClosedUnknownBody,
                AnchorFrameResolver.ResolveBody("Kerbin", throwing));
        }

        // ---- ParentAnchoredChild dual-surface routing ----

        [Fact]
        public void ParentChild_TwoBodyFixedSamples_InRange_UsesPrimary()
        {
            // >=2 body-fixed samples + UT in [100,200] -> body-fixed PRIMARY.
            var s = AnchorFrameResolver.ResolveParentAnchoredChild(
                ut: 150,
                bodyFixedSampleCount: 5, bodyFixedStartUt: 100, bodyFixedEndUt: 200,
                hasAnchorLocalFrames: true, anchorLocalStartUt: 100, anchorLocalEndUt: 200);
            Assert.Equal(AnchorFrameResolver.ParentChildSurface.BodyFixedPrimary, s);
        }

        [Theory]
        [InlineData(100.0)] // inclusive start
        [InlineData(200.0)] // inclusive end
        public void ParentChild_BodyFixedRange_IsInclusive(double ut)
        {
            var s = AnchorFrameResolver.ResolveParentAnchoredChild(
                ut,
                bodyFixedSampleCount: 2, bodyFixedStartUt: 100, bodyFixedEndUt: 200,
                hasAnchorLocalFrames: false, anchorLocalStartUt: 0, anchorLocalEndUt: 0);
            Assert.Equal(AnchorFrameResolver.ParentChildSurface.BodyFixedPrimary, s);
        }

        [Fact]
        public void ParentChild_OneBodyFixedSample_FallsToSecondaryWhenItCovers()
        {
            // A single body-fixed sample fails the >=2 minimum; the anchor-local frames cover the UT.
            var s = AnchorFrameResolver.ResolveParentAnchoredChild(
                ut: 150,
                bodyFixedSampleCount: 1, bodyFixedStartUt: 150, bodyFixedEndUt: 150,
                hasAnchorLocalFrames: true, anchorLocalStartUt: 100, anchorLocalEndUt: 200);
            Assert.Equal(AnchorFrameResolver.ParentChildSurface.AnchorLocalSecondary, s);
        }

        [Fact]
        public void ParentChild_OutOfRangeBodyFixed_NoSecondary_Retires_NotClamped()
        {
            // UT past the body-fixed range, no covering secondary -> RETIRE (never clamp to stale).
            var s = AnchorFrameResolver.ResolveParentAnchoredChild(
                ut: 250,
                bodyFixedSampleCount: 5, bodyFixedStartUt: 100, bodyFixedEndUt: 200,
                hasAnchorLocalFrames: false, anchorLocalStartUt: 0, anchorLocalEndUt: 0);
            Assert.Equal(AnchorFrameResolver.ParentChildSurface.Retire, s);
        }

        [Fact]
        public void ParentChild_PrimaryHasEnoughSamplesButUtOutOfRange_FallsToSecondaryWhenItCovers()
        {
            // The loop-anchored-chain case the contract calls out: the body-fixed PRIMARY has >=2
            // samples BUT the playback UT is past its endpoint range, while the anchor-local SECONDARY
            // still covers the UT -> AnchorLocalSecondary (an out-of-range primary must NOT block the
            // covering secondary, nor clamp the stale primary sample).
            var s = AnchorFrameResolver.ResolveParentAnchoredChild(
                ut: 250,
                bodyFixedSampleCount: 3, bodyFixedStartUt: 100, bodyFixedEndUt: 200,
                hasAnchorLocalFrames: true, anchorLocalStartUt: 100, anchorLocalEndUt: 300);
            Assert.Equal(AnchorFrameResolver.ParentChildSurface.AnchorLocalSecondary, s);
        }

        [Fact]
        public void ParentChild_OutOfRangeBothSurfaces_Retires()
        {
            var s = AnchorFrameResolver.ResolveParentAnchoredChild(
                ut: 250,
                bodyFixedSampleCount: 5, bodyFixedStartUt: 100, bodyFixedEndUt: 200,
                hasAnchorLocalFrames: true, anchorLocalStartUt: 100, anchorLocalEndUt: 200);
            Assert.Equal(AnchorFrameResolver.ParentChildSurface.Retire, s);
        }

        [Fact]
        public void ParentChild_PrimaryWinsOverSecondary_WhenBothCover()
        {
            // Both surfaces cover the UT; the body-fixed PRIMARY must win.
            var s = AnchorFrameResolver.ResolveParentAnchoredChild(
                ut: 150,
                bodyFixedSampleCount: 3, bodyFixedStartUt: 100, bodyFixedEndUt: 200,
                hasAnchorLocalFrames: true, anchorLocalStartUt: 100, anchorLocalEndUt: 200);
            Assert.Equal(AnchorFrameResolver.ParentChildSurface.BodyFixedPrimary, s);
        }

        [Fact]
        public void ParentChild_NonFiniteUt_Retires()
        {
            var s = AnchorFrameResolver.ResolveParentAnchoredChild(
                ut: double.NaN,
                bodyFixedSampleCount: 5, bodyFixedStartUt: 100, bodyFixedEndUt: 200,
                hasAnchorLocalFrames: true, anchorLocalStartUt: 100, anchorLocalEndUt: 200);
            Assert.Equal(AnchorFrameResolver.ParentChildSurface.Retire, s);
        }

        [Fact]
        public void ParentChild_DegenerateRange_StartAfterEnd_Retires()
        {
            var s = AnchorFrameResolver.ResolveParentAnchoredChild(
                ut: 150,
                bodyFixedSampleCount: 5, bodyFixedStartUt: 200, bodyFixedEndUt: 100,
                hasAnchorLocalFrames: false, anchorLocalStartUt: 0, anchorLocalEndUt: 0);
            Assert.Equal(AnchorFrameResolver.ParentChildSurface.Retire, s);
        }
    }
}
