using System;
using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>The pure FX framing and view geometry (design-part-ghost-parity.md E.3):
    /// PartParityFxFraming.</summary>
    public class PartParityFxFramingTests
    {
        private static readonly double[] DownY = { 0.0, -1.0, 0.0 };

        /// <summary>An LV-T45-like body: 1.25 m wide, 1.8 m long, the nozzle at its bottom.</summary>
        private static PartParityAabb Body()
            => PartParityAabb.FromMinMax(new[] { -0.6, -0.9, -0.6 }, new[] { 0.6, 0.9, 0.6 });

        private static void Near(double expected, double actual) => Assert.True(Math.Abs(expected - actual) < 1e-9, expected + " != " + actual);

        private static void NearV(double[] expected, double[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++) Near(expected[i], actual[i]);
        }

        // ----- view frames -----

        [Fact]
        public void ADownwardExhaust_GetsThePartsStowedSideViews()
        {
            Assert.True(PartParityFxFraming.TryViewFrame(PartParityFxRules.ViewFxSideA, DownY, out double[] dir, out double[] up));
            NearV(new[] { 0.0, 0.0, -1.0 }, dir);
            NearV(new[] { 0.0, 1.0, 0.0 }, up);
            Assert.True(PartParityFxFraming.TryViewFrame(PartParityFxRules.ViewFxSideB, DownY, out dir, out up));
            NearV(new[] { 1.0, 0.0, 0.0 }, dir);
            NearV(new[] { 0.0, 1.0, 0.0 }, up);
            Assert.True(PartParityFxFraming.TryViewFrame(PartParityFxRules.ViewFxFar, DownY, out dir, out up));
            NearV(new[] { 0.0, 0.0, -1.0 }, dir);
            Assert.True(PartParityFxFraming.TryViewFrame(PartParityFxRules.ViewFxBottom, DownY, out dir, out up));
            NearV(DownY, dir);
            NearV(new[] { 0.0, 0.0, -1.0 }, up);
        }

        [Theory]
        [InlineData(0.0, -1.0, 0.0)]
        [InlineData(0.0, 0.0, 1.0)]
        [InlineData(0.0, 0.0, -1.0)]
        [InlineData(1.0, 0.0, 0.0)]
        [InlineData(0.6, -0.6, 0.53)]
        public void EverySideView_LooksAcrossTheAxis_UpAgainstTheFlow(double x, double y, double z)
        {
            double[] u = PartParityFxFraming.Unit(new[] { x, y, z });
            foreach (string view in new[] { PartParityFxRules.ViewFxSideA, PartParityFxRules.ViewFxSideB, PartParityFxRules.ViewFxFar })
            {
                Assert.True(PartParityFxFraming.TryViewFrame(view, u, out double[] dir, out double[] up));
                Near(1.0, PartParityFxFraming.Length(dir));
                Assert.True(Math.Abs(PartParityFxFraming.Dot(dir, u)) < 1e-9, view + " looks along the axis");
                NearV(new[] { -u[0], -u[1], -u[2] }, up);
            }
            Assert.True(PartParityFxFraming.TryViewFrame(PartParityFxRules.ViewFxSideA, u, out double[] a, out _));
            Assert.True(PartParityFxFraming.TryViewFrame(PartParityFxRules.ViewFxSideB, u, out double[] b, out _));
            Assert.True(Math.Abs(PartParityFxFraming.Dot(a, b)) < 1e-9, "the side views are a quarter turn apart");
            Assert.True(PartParityFxFraming.TryViewFrame(PartParityFxRules.ViewFxBottom, u, out double[] bottom, out double[] bottomUp));
            NearV(u, bottom);
            Assert.True(Math.Abs(PartParityFxFraming.Dot(bottomUp, u)) < 1e-9);
        }

        [Fact]
        public void AZeroAxisOrAnUnknownView_HasNoFrame()
        {
            Assert.False(PartParityFxFraming.TryViewFrame(PartParityFxRules.ViewFxSideA, new double[3], out _, out _));
            Assert.False(PartParityFxFraming.TryViewFrame("side-a", DownY, out _, out _));
            Assert.Null(PartParityFxFraming.Unit(null));
        }

        // ----- boxes -----

        [Fact]
        public void TheNearBox_IsThreeBodyLengthsAlongTheFlow()
        {
            PartParityAabb near = PartParityFxFraming.NearBox(Body(), DownY);
            Near(0.9, near.Max[1]);
            Near(-0.9 - 2 * 1.8, near.Min[1]);
            Near(-0.6, near.Min[0]);
            Near(0.6, near.Max[0]);
            Near(3 * 1.8, PartParityFxFraming.AlongSize(near, DownY));
        }

        [Fact]
        public void TheBottomBox_IsTwiceAsWide_AndAsLong()
        {
            PartParityAabb near = PartParityFxFraming.NearBox(Body(), DownY);
            PartParityAabb bottom = PartParityFxFraming.BottomBox(near, DownY);
            Near(PartParityFxFraming.AlongSize(near, DownY), PartParityFxFraming.AlongSize(bottom, DownY));
            Near(2.0 * PartParityFxFraming.AcrossSize(near, DownY), PartParityFxFraming.AcrossSize(bottom, DownY));
        }

        [Fact]
        public void TheFarBox_ReachesTheParticles_AtLeastAsWideAsTheBody()
        {
            double[] nozzle = { 0.0, -0.9, 0.0 };
            PartParityAabb far = PartParityFxFraming.FarBox(Body(), nozzle, DownY, 12.0, 2.0, out bool clamped);
            Assert.False(clamped);
            Near(-0.9 - 12.0, far.Min[1]);
            Near(0.9, far.Max[1]);
            Near(-2.0, far.Min[0]);
            Near(2.0, far.Max[0]);
            // A narrow plume keeps the body's width.
            far = PartParityFxFraming.FarBox(Body(), nozzle, DownY, 12.0, 0.1, out _);
            Near(0.6, far.Max[0]);
        }

        [Fact]
        public void TheFarBox_ClampsAt80m_AndNeverDropsBelowTheNearBox()
        {
            double[] nozzle = { 0.0, -0.9, 0.0 };
            PartParityAabb far = PartParityFxFraming.FarBox(Body(), nozzle, DownY, 6000.0, 8000.0, out bool clamped);
            Assert.True(clamped);
            Near(-0.9 - PartParityFxRules.FramingClampM, far.Min[1]);
            far = PartParityFxFraming.FarBox(Body(), nozzle, DownY, null, null, out clamped);
            Assert.False(clamped);
            Near(-0.9 - 2 * 1.8, far.Min[1]);
            far = PartParityFxFraming.FarBox(Body(), nozzle, DownY, double.NaN, double.NaN, out _);
            Near(-0.9 - 2 * 1.8, far.Min[1]);
        }

        [Fact]
        public void TheNozzleDiameter_IsTheThrustSpread_OrHalfTheBody()
        {
            double[] nozzle = { 0.0, -0.9, 0.0 };
            // One nozzle has no spread: half the 1.2 m width.
            Near(0.6, PartParityFxFraming.NozzleDiameterM(new List<double[]> { nozzle }, nozzle, DownY, Body()));
            // A pair 1.6 m apart across the axis (the far box and its nozzles on a wide engine).
            var pair = new List<double[]> { new[] { 0.8, -0.9, 0.0 }, new[] { -0.8, -0.9, 0.0 } };
            Near(1.6, PartParityFxFraming.NozzleDiameterM(pair, nozzle, DownY, Body()));
            // Spread ALONG the axis does not count.
            var stack = new List<double[]> { new[] { 0.0, -0.9, 0.0 }, new[] { 0.0, -1.9, 0.0 } };
            Near(0.6, PartParityFxFraming.NozzleDiameterM(stack, nozzle, DownY, Body()));
        }

        // ----- camera -----

        [Fact]
        public void ProjectBox_ReadsEachAxisOfTheCamera()
        {
            PartParityFxFraming.TryViewFrame(PartParityFxRules.ViewFxSideA, DownY, out double[] dir, out double[] up);
            PartParityFxFraming.ProjectBox(Body(), dir, up, out double w, out double h, out double d);
            Near(0.6, w);
            Near(0.9, h);
            Near(0.6, d);
            PartParityFxFraming.TryViewFrame(PartParityFxRules.ViewFxBottom, DownY, out dir, out up);
            PartParityFxFraming.ProjectBox(Body(), dir, up, out w, out h, out d);
            Near(0.6, w);
            Near(0.6, h);
            Near(0.9, d);
        }

        [Fact]
        public void TheAxisBlock_IsTheProjectedNozzleAndFlow_InTilePixels()
        {
            double ortho = 2.0;
            int tile = 512;
            PartParityFxAxis axis = PartParityFxFraming.AxisFor(PartParityFxRules.ViewFxSideA, new[] { 0.5, 1.0 }, new[] { 0.0, -1.0 },
                ortho, tile, 0.6);
            NearV(new[] { (0.5 / 4.0 + 0.5) * 512, (0.5 - 1.0 / 4.0) * 512 }, axis.OriginPx);
            // The camera's up is against the flow, so the flow points down the image (y down).
            NearV(new[] { 0.0, 1.0 * 512 / 4.0 }, axis.DirPx);
            Near(0.6 * 512 / 4.0, axis.NozzleDiameterPx.Value);
            // The axis view has no direction.
            axis = PartParityFxFraming.AxisFor(PartParityFxRules.ViewFxBottom, new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 }, ortho, tile, 0.6);
            Assert.Null(axis.DirPx);
            Assert.NotNull(axis.OriginPx);
            // A nozzle of no size has no diameter.
            axis = PartParityFxFraming.AxisFor(PartParityFxRules.ViewFxSideA, new[] { 0.0, 0.0 }, new[] { 0.0, -1.0 }, ortho, tile, 0.0);
            Assert.Null(axis.NozzleDiameterPx);
        }

        [Fact]
        public void TheOrthoSize_HasAMarginAndAFloor()
        {
            Near(2.0 * PartParityFxFraming.OrthoMargin, PartParityFxFraming.OrthoSize(1.0, 2.0));
            Near(PartParityFxFraming.MinHalfExtentM * PartParityFxFraming.OrthoMargin, PartParityFxFraming.OrthoSize(0.0, 0.0));
        }
    }
}
