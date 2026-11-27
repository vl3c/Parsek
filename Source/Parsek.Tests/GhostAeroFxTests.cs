using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure halves of the ghost reentry sheath (part parity P9 Y1,
    /// design-part-ghost-parity.md G.7 "Y1 design"): stock's blend, LOD and reach rules and the
    /// ghost's ownership decisions.
    /// </summary>
    public class GhostAeroFxTests
    {
        private static AeroFxLook Look(Color min, Color max, float lo, float hi)
        {
            var r = new AeroFxRange(lo, hi);
            return new AeroFxLook
            {
                ColorMin = min, ColorMax = max,
                Length = r, EdgeFade = r, Falloff1 = r, Falloff2 = r, Falloff3 = r,
                Intensity = r, Wobble = r, LightPower = r,
            };
        }

        // --- ThermalShare ---

        [Theory]
        [InlineData(1f, 1f, 1f)]
        [InlineData(1f, 0f, 0f)]
        [InlineData(0.5f, 0.4f, 0.2f)]
        [InlineData(-0.3f, 1f, 0f)]
        [InlineData(2f, 2f, 1f)]
        public void ThermalShare_IsScalarTimesBlend_Clamped(float fx, float state, float expected)
        {
            Assert.InRange(GhostAeroFx.ThermalShare(fx, state), expected - 1e-6f, expected + 1e-6f);
        }

        // --- Blend ---

        [Fact]
        public void Blend_PureCondensation_LerpsTheCondensationSetByTheScalar()
        {
            AeroFxLook cond = Look(Color.black, Color.white, 0f, 10f);
            AeroFxLook heat = Look(Color.red, Color.red, 100f, 200f);
            AeroFxDrawValues v = GhostAeroFx.Blend(cond, heat, 0.5f, 0f);
            Assert.InRange(v.Length, 5f - 1e-4f, 5f + 1e-4f);
            Assert.InRange(v.Intensity, 5f - 1e-4f, 5f + 1e-4f);
            Assert.InRange(v.Color.r, 0.5f - 1e-4f, 0.5f + 1e-4f);
            Assert.Equal(1f, v.Color.a);
        }

        [Fact]
        public void Blend_PureHeat_LerpsTheReentrySetByTheScalar()
        {
            AeroFxLook cond = Look(Color.black, Color.white, 0f, 10f);
            AeroFxLook heat = Look(Color.black, Color.red, 100f, 200f);
            AeroFxDrawValues v = GhostAeroFx.Blend(cond, heat, 0.25f, 1f);
            Assert.InRange(v.Falloff2, 125f - 1e-3f, 125f + 1e-3f);
            Assert.InRange(v.Wobble, 125f - 1e-3f, 125f + 1e-3f);
            Assert.InRange(v.Color.r, 0.25f - 1e-4f, 0.25f + 1e-4f);
            Assert.Equal(0f, v.Color.g);
        }

        [Fact]
        public void Blend_HalfState_MixesBothSets()
        {
            AeroFxLook cond = Look(Color.black, Color.black, 0f, 0f);
            AeroFxLook heat = Look(Color.white, Color.white, 10f, 10f);
            AeroFxDrawValues v = GhostAeroFx.Blend(cond, heat, 0.7f, 0.5f);
            Assert.InRange(v.EdgeFade, 5f - 1e-4f, 5f + 1e-4f);
            Assert.InRange(v.Color.b, 0.5f - 1e-4f, 0.5f + 1e-4f);
        }

        [Fact]
        public void Blend_FloorsTheLightPowerAtZero()
        {
            AeroFxLook cond = Look(Color.black, Color.black, -4f, -2f);
            AeroFxDrawValues v = GhostAeroFx.Blend(cond, cond, 0.5f, 0f);
            Assert.Equal(0f, v.LightPower);
            Assert.InRange(v.Length, -3f - 1e-4f, -3f + 1e-4f);
        }

        // --- LOD, colour alpha, reach, velocity box ---

        [Theory]
        [InlineData(4, 3, 1)]
        [InlineData(4, 1, 3)]
        [InlineData(4, 0, 3)]
        [InlineData(4, 9, 0)]
        [InlineData(1, 3, 0)]
        [InlineData(0, 3, -1)]
        public void ShaderLod_IsStocksPickClampedIntoTheArray(int count, int detail, int expected)
        {
            Assert.Equal(expected, GhostAeroFx.ShaderLod(count, detail));
        }

        [Theory]
        [InlineData(0.11f, 0, 0.11f)]
        [InlineData(0.11f, 2, 0.33f)]
        [InlineData(0.5f, 3, 2f)]
        public void FxColorAlpha_IsIntensityTimesLodPlusOne(float intensity, int lod, float expected)
        {
            Assert.InRange(GhostAeroFx.FxColorAlpha(intensity, lod), expected - 1e-5f, expected + 1e-5f);
        }

        [Theory]
        [InlineData(0f, 300f)]
        [InlineData(150f, 300f)]
        [InlineData(200f, 300f)]
        [InlineData(250f, 350f)]
        [InlineData(-5f, 300f)]
        [InlineData(float.NaN, 300f)]
        public void StockReach_IsTheFxCameraFarClip(float cameraDistance, float expected)
        {
            Assert.Equal(expected, GhostAeroFx.StockReach(cameraDistance));
        }

        [Fact]
        public void WithinReach_CountsTheGhostRadius()
        {
            Assert.True(GhostAeroFx.WithinReach(299f, 0f, 300f));
            Assert.True(GhostAeroFx.WithinReach(300f, 0f, 300f));
            Assert.False(GhostAeroFx.WithinReach(301f, 0f, 300f));
            Assert.True(GhostAeroFx.WithinReach(305f, 10f, 300f));
            Assert.False(GhostAeroFx.WithinReach(301f, -10f, 300f));
            Assert.False(GhostAeroFx.WithinReach(float.NaN, 1f, 300f));
            Assert.False(GhostAeroFx.WithinReach(float.PositiveInfinity, 1f, 300f));
        }

        [Theory]
        [InlineData(0f, 3f, 50f)]
        [InlineData(2f, 3f, 50f)]
        [InlineData(10f, 10f, 50f)]
        [InlineData(30f, 30f, 60f)]
        [InlineData(float.NaN, 3f, 50f)]
        [InlineData(-4f, 3f, 50f)]
        public void VelocityCamBox_IsStocksBoxOnTheGhostSize(float size, float box, float far)
        {
            GhostAeroFx.VelocityCamBox(size, out float gotBox, out float gotFar);
            Assert.Equal(box, gotBox);
            Assert.Equal(far, gotFar);
        }

        // --- DecideSheath ---

        private static GhostAeroSheathDecision Sheath(
            float fx = 0.8f, bool map = false, bool debug = false, int detail = 3, bool assets = true,
            bool stockOn = false, bool inReach = true, int meshes = 5, bool isolated = false, int drawn = 0)
            => GhostAeroFx.DecideSheath(fx, map, debug, detail, assets, stockOn, inReach, meshes, isolated, drawn);

        [Fact]
        public void DecideSheath_DrawsWhenEveryGateIsOpen()
        {
            Assert.Equal(GhostAeroSheathDecision.Draw, Sheath());
            Assert.Equal(GhostAeroSheathDecision.Draw, Sheath(drawn: GhostAeroFx.MaxSheathsPerFrame - 1));
        }

        [Fact]
        public void DecideSheath_EachGateRefusesWithItsOwnReason()
        {
            Assert.Equal(GhostAeroSheathDecision.NoEffect, Sheath(fx: 0f));
            Assert.Equal(GhostAeroSheathDecision.NoEffect, Sheath(fx: float.NaN));
            Assert.Equal(GhostAeroSheathDecision.MapView, Sheath(map: true));
            Assert.Equal(GhostAeroSheathDecision.ThermalColorsDebug, Sheath(debug: true));
            Assert.Equal(GhostAeroSheathDecision.DetailOff, Sheath(detail: 0));
            Assert.Equal(GhostAeroSheathDecision.NoStockAssets, Sheath(assets: false));
            Assert.Equal(GhostAeroSheathDecision.StockDraws, Sheath(stockOn: true));
            Assert.Equal(GhostAeroSheathDecision.OutOfReach, Sheath(inReach: false));
            Assert.Equal(GhostAeroSheathDecision.NoMeshes, Sheath(meshes: 0));
            Assert.Equal(GhostAeroSheathDecision.Isolated, Sheath(isolated: true));
            Assert.Equal(GhostAeroSheathDecision.FrameCap, Sheath(drawn: GhostAeroFx.MaxSheathsPerFrame));
        }

        [Fact]
        public void DecideSheath_StocksGatesComeBeforeTheOwnershipRules()
        {
            // Each pair: the earlier gate wins over every later one.
            Assert.Equal(GhostAeroSheathDecision.NoEffect,
                Sheath(fx: 0f, map: true, debug: true, detail: 0, assets: false, stockOn: true, inReach: false));
            Assert.Equal(GhostAeroSheathDecision.MapView, Sheath(map: true, debug: true, detail: 0));
            Assert.Equal(GhostAeroSheathDecision.ThermalColorsDebug, Sheath(debug: true, detail: 0));
            Assert.Equal(GhostAeroSheathDecision.DetailOff, Sheath(detail: 0, assets: false, stockOn: true));
            Assert.Equal(GhostAeroSheathDecision.NoStockAssets, Sheath(assets: false, stockOn: true));
            Assert.Equal(GhostAeroSheathDecision.StockDraws, Sheath(stockOn: true, inReach: false));
            Assert.Equal(GhostAeroSheathDecision.OutOfReach, Sheath(inReach: false, meshes: 0));
            Assert.Equal(GhostAeroSheathDecision.NoMeshes, Sheath(meshes: 0, isolated: true));
            Assert.Equal(GhostAeroSheathDecision.Isolated,
                Sheath(isolated: true, drawn: GhostAeroFx.MaxSheathsPerFrame));
        }

        // --- DecideLight and the nearest offer ---

        [Fact]
        public void DecideLight_OffersOnlyWhileStocksLightIsOffAndInReach()
        {
            Assert.Equal(GhostAeroLightDecision.Offer, GhostAeroFx.DecideLight(0.5f, false, true, false, true));
            Assert.Equal(GhostAeroLightDecision.NoEffect, GhostAeroFx.DecideLight(0f, false, true, false, true));
            Assert.Equal(GhostAeroLightDecision.MapView, GhostAeroFx.DecideLight(0.5f, true, true, false, true));
            Assert.Equal(GhostAeroLightDecision.NoStockLight, GhostAeroFx.DecideLight(0.5f, false, false, false, true));
            Assert.Equal(GhostAeroLightDecision.StockLightOn, GhostAeroFx.DecideLight(0.5f, false, true, true, true));
            Assert.Equal(GhostAeroLightDecision.OutOfReach, GhostAeroFx.DecideLight(0.5f, false, true, false, false));
            // Stock's light needs no detail level: the light is not gated on it.
            Assert.Equal(GhostAeroLightDecision.StockLightOn, GhostAeroFx.DecideLight(0.5f, false, true, true, false));
        }

        [Fact]
        public void LightOfferWins_NearestTakesTheLight_TiesKeepTheFirst()
        {
            Assert.True(GhostAeroFx.LightOfferWins(false, 0f, 500f));
            Assert.True(GhostAeroFx.LightOfferWins(true, 100f, 50f));
            Assert.False(GhostAeroFx.LightOfferWins(true, 100f, 100f));
            Assert.False(GhostAeroFx.LightOfferWins(true, 100f, 150f));
        }

        // --- The velocity depth atlas ---

        [Theory]
        [InlineData(0, 4, 0f, 0.25f, -0.75f)]
        [InlineData(1, 4, 0.25f, 0.25f, -0.25f)]
        [InlineData(3, 4, 0.75f, 0.25f, 0.75f)]
        [InlineData(0, 1, 0f, 1f, 0f)]
        [InlineData(9, 4, 0.75f, 0.25f, 0.75f)]
        [InlineData(-2, 4, 0f, 0.25f, -0.75f)]
        public void VelocityAtlasTile_MapsTheFullViewIntoItsTile(int slot, int slots,
            float rectX, float width, float offset)
        {
            GhostAeroFx.VelocityAtlasTile(slot, slots, out float gotX, out float gotW,
                out float scale, out float gotOffset);
            Assert.InRange(gotX, rectX - 1e-6f, rectX + 1e-6f);
            Assert.InRange(gotW, width - 1e-6f, width + 1e-6f);
            Assert.InRange(scale, width - 1e-6f, width + 1e-6f);
            Assert.InRange(gotOffset, offset - 1e-6f, offset + 1e-6f);
            // The view's edges land on the tile's edges in NDC (rect x -> 2x - 1).
            Assert.InRange(scale * -1f + gotOffset, 2f * gotX - 1f - 1e-5f, 2f * gotX - 1f + 1e-5f);
            Assert.InRange(scale * 1f + gotOffset, 2f * (gotX + gotW) - 1f - 1e-5f, 2f * (gotX + gotW) - 1f + 1e-5f);
        }

        // --- The build cost line ---

        [Theory]
        [InlineData(1, 1, 1)]
        [InlineData(3, 2, 2)]
        [InlineData(2, 5, 2)]
        [InlineData(0, 3, 0)]
        [InlineData(2, 0, 0)]
        [InlineData(-1, 2, 0)]
        public void SubmeshDraws_IsOnePerSubmeshWithAMaterial(int subMeshes, int materials, int expected)
        {
            Assert.Equal(expected, GhostAeroFx.SubmeshDraws(subMeshes, materials));
        }

        [Fact]
        public void FormatBuildCost_StatesTheCountsInvariantly()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                string line = GhostAeroFx.FormatBuildCost(12000, "Probe", 7, 6, 9);
                Assert.Equal("Reentry FX built for ghost #12000 \"Probe\": particles=0 glowMaterials=7 " +
                    "sheathMeshes=6; per frame while its sheath draws: drawCalls=9 velocityDepthRenders=1 " +
                    "(a 256x256 tile of the shared depth atlas); shared per frame while any ghost draws: " +
                    "sceneDepthPass=1 light=1, at most 4 ghosts", line);
                Assert.Contains("\"?\"", GhostAeroFx.FormatBuildCost(1, null, 0, 0, 0));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // --- ComputeFxScalar: the raw (unclamped) scalar ---

        [Fact]
        public void ComputeFxScalar_IsNegativeBelowTheThreshold_InDenseAir()
        {
            // Stock draws only above 0; the raw value below the heat-flux threshold is negative.
            float fx = GhostAeroFx.ComputeFxScalar(1.0, 100.0, 0.3f, AeroFxConstants.StockDefaults, out float state);
            Assert.True(fx < 0f, $"expected a negative raw scalar, got {fx}");
            Assert.Equal(0f, state);
        }

        [Fact]
        public void ComputeFxScalar_ReadsTheGivenConstants()
        {
            AeroFxConstants c = AeroFxConstants.StockDefaults;
            c.ThermalStartMach = 1f;
            c.ThermalFullMach = 2f;
            GhostAeroFx.ComputeFxScalar(0.05, 1500.0, 1.5f, c, out float state);
            Assert.InRange(state, 0.5f - 1e-5f, 0.5f + 1e-5f);
        }
    }
}
