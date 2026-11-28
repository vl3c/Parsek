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
        // The rig's pose: stock read ortho=32.4 far=50.0 for a 32.4 m camera distance.
        [InlineData(3f, 32.4f, 32.4f, 50f)]
        [InlineData(0f, 10f, 10f, 50f)]
        [InlineData(2f, 0.5f, 2f, 50f)]
        [InlineData(30f, 20f, 30f, 60f)]
        [InlineData(5f, 300f, 300f, 305f)]
        [InlineData(float.NaN, 40f, 40f, 50f)]
        [InlineData(-4f, 40f, 40f, 50f)]
        [InlineData(2f, float.NaN, 3f, 50f)]
        [InlineData(2f, -1f, 3f, 50f)]
        [InlineData(2f, float.PositiveInfinity, 3f, 50f)]
        public void VelocityCamBox_IsStocksBoxOnTheCameraDistance(float size, float distance, float box, float far)
        {
            GhostAeroFx.VelocityCamBox(size, distance, out float gotBox, out float gotFar);
            Assert.Equal(box, gotBox, 0.0001f);
            Assert.Equal(far, gotFar, 0.0001f);
        }

        // --- DecideSheath ---

        private static GhostAeroSheathDecision Sheath(
            float fx = 0.8f, bool map = false, bool debug = false, int detail = 3, bool assets = true,
            bool inReach = true, int meshes = 5, bool isolated = false)
            => GhostAeroFx.DecideSheath(fx, map, debug, detail, assets, inReach, meshes, isolated);

        [Fact]
        public void DecideSheath_DrawsWhenEveryGateIsOpen()
        {
            Assert.Equal(GhostAeroSheathDecision.Draw, Sheath());
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
            Assert.Equal(GhostAeroSheathDecision.OutOfReach, Sheath(inReach: false));
            Assert.Equal(GhostAeroSheathDecision.NoMeshes, Sheath(meshes: 0));
            Assert.Equal(GhostAeroSheathDecision.Isolated, Sheath(isolated: true));
        }

        [Fact]
        public void DecideSheath_StocksGatesComeFirst()
        {
            // Each pair: the earlier gate wins over every later one.
            Assert.Equal(GhostAeroSheathDecision.NoEffect,
                Sheath(fx: 0f, map: true, debug: true, detail: 0, assets: false, inReach: false));
            Assert.Equal(GhostAeroSheathDecision.MapView, Sheath(map: true, debug: true, detail: 0));
            Assert.Equal(GhostAeroSheathDecision.ThermalColorsDebug, Sheath(debug: true, detail: 0));
            Assert.Equal(GhostAeroSheathDecision.DetailOff, Sheath(detail: 0, assets: false));
            Assert.Equal(GhostAeroSheathDecision.NoStockAssets, Sheath(assets: false, inReach: false));
            Assert.Equal(GhostAeroSheathDecision.OutOfReach, Sheath(inReach: false, meshes: 0));
            Assert.Equal(GhostAeroSheathDecision.NoMeshes, Sheath(meshes: 0, isolated: true));
        }

        [Fact]
        public void DecideSheath_HasNoStockOwnershipGateAndNoGhostCap()
        {
            // Owner rulings 2026-11-28 01:06 / 01:07: a ghost draws its own fire whether or not
            // stock's is on, and every ghost in reach draws (the signature has neither input).
            var parameters = typeof(GhostAeroFx).GetMethod("DecideSheath",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetParameters();
            Assert.DoesNotContain(parameters, p => p.Name.IndexOf("stock", System.StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.DoesNotContain(parameters, p => p.Name.IndexOf("drawn", System.StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.False(System.Enum.IsDefined(typeof(GhostAeroSheathDecision), "StockDraws"));
            Assert.False(System.Enum.IsDefined(typeof(GhostAeroSheathDecision), "FrameCap"));
        }

        // --- DecideLight and the nearest offer ---

        [Fact]
        public void DecideLight_NeedsTheEffectReachAndAVisibleMesh()
        {
            Assert.Equal(GhostAeroLightDecision.Offer, GhostAeroFx.DecideLight(0.5f, false, true, true, 3));
            Assert.Equal(GhostAeroLightDecision.NoEffect, GhostAeroFx.DecideLight(0f, false, true, true, 3));
            Assert.Equal(GhostAeroLightDecision.MapView, GhostAeroFx.DecideLight(0.5f, true, true, true, 3));
            Assert.Equal(GhostAeroLightDecision.NoStockLight, GhostAeroFx.DecideLight(0.5f, false, false, true, 3));
            Assert.Equal(GhostAeroLightDecision.OutOfReach, GhostAeroFx.DecideLight(0.5f, false, true, false, 3));
            // A ghost whose renderers are hidden (a loop pause while intensity decays) lights nothing.
            Assert.Equal(GhostAeroLightDecision.NoMeshes, GhostAeroFx.DecideLight(0.5f, false, true, true, 0));
            Assert.False(System.Enum.IsDefined(typeof(GhostAeroLightDecision), "StockLightOn"));
        }

        [Fact]
        public void LightOfferWins_NearestTakesTheLight_TiesKeepTheFirst()
        {
            Assert.True(GhostAeroFx.LightOfferWins(false, 0f, 500f));
            Assert.True(GhostAeroFx.LightOfferWins(true, 100f, 50f));
            Assert.False(GhostAeroFx.LightOfferWins(true, 100f, 100f));
            Assert.False(GhostAeroFx.LightOfferWins(true, 100f, 150f));
        }

        // --- Per-ghost depth maps ---

        [Theory]
        [InlineData(256, 256)]
        [InlineData(512, 512)]
        [InlineData(1024, 1024)]
        [InlineData(2048, 2048)]
        [InlineData(0, 512)]
        [InlineData(-1, 512)]
        [InlineData(4096, 512)]
        [InlineData(128, 512)]
        public void OcclusionResolution_IsStocksOwnSize(int stock, int expected)
        {
            Assert.Equal(expected, GhostAeroFx.OcclusionResolution(stock));
        }

        [Theory]
        [InlineData(0, 1, 0f, 100f, false)]    // drawn this frame
        [InlineData(1, 1, 99f, 100f, false)]   // idle 1 s
        [InlineData(1, 1, 95f, 100f, true)]    // idle 5 s
        [InlineData(3, 0, 0f, 100f, true)]
        [InlineData(2, 3, 0f, 100f, false)]
        [InlineData(1, 0, float.NaN, 100f, true)]
        public void ShouldReleasePooledDepthMap_OnlyIdleSlotsPastTheFrame(int slot, int drawn,
            float lastUsed, float now, bool expected)
        {
            Assert.Equal(expected, GhostAeroFx.ShouldReleasePooledDepthMap(slot, drawn, lastUsed, now));
        }

        // --- Masking ghosts out of stock's fire ---

        [Fact]
        public void ShouldMaskFromStockFx_OnlyWhileStockIsOnAndInReach()
        {
            Assert.True(GhostAeroFx.ShouldMaskFromStockFx(true, true));
            Assert.False(GhostAeroFx.ShouldMaskFromStockFx(false, true));
            Assert.False(GhostAeroFx.ShouldMaskFromStockFx(true, false));
            Assert.False(GhostAeroFx.ShouldMaskFromStockFx(false, false));
        }

        [Theory]
        [InlineData(0, 0x20001, 1, true)]
        [InlineData(17, 0x20001, 1, true)]
        [InlineData(6, 0x20001, 1, false)]
        [InlineData(15, 0x20001, 1, false)]
        [InlineData(0, 0, 1, true)]
        [InlineData(-1, 0x20001, 1, false)]
        [InlineData(32, -1, -1, false)]
        public void LayerSeenByStockFx_ReadsBothCamerasMasks(int layer, int fxMask, int velocityMask, bool expected)
        {
            Assert.Equal(expected, GhostAeroFx.LayerSeenByStockFx(layer, fxMask, velocityMask));
        }

        [Theory]
        [InlineData(2f, 3f, 3.01f)]
        [InlineData(3f, 2f, 3.01f)]
        [InlineData(float.NaN, 2f, 2.01f)]
        [InlineData(float.NaN, float.NaN, 3.01f)]
        public void CopyCameraDepth_RendersAfterBothStockCameras(float depthCam, float fxCam, float expected)
        {
            float got = GhostAeroFx.CopyCameraDepth(depthCam, fxCam);
            Assert.InRange(got, expected - 1e-5f, expected + 1e-5f);
            if (!float.IsNaN(depthCam)) Assert.True(got > depthCam);
            if (!float.IsNaN(fxCam)) Assert.True(got > fxCam);
        }

        [Theory]
        [InlineData(3.02f, 3.02f, false)]
        [InlineData(3f, 3.02f, true)]
        [InlineData(3.02f, 3.01f, true)]
        [InlineData(float.NaN, 3.02f, true)]
        [InlineData(3.02f, float.NaN, true)]
        [InlineData(float.NaN, float.NaN, true)]
        public void CameraDepthNeedsWrite_OnlyOnAChange(float current, float wanted, bool expected)
        {
            Assert.Equal(expected, GhostAeroFx.CameraDepthNeedsWrite(current, wanted));
        }

        [Fact]
        public void CameraDepthNeedsWrite_FalseForTheValueItWrote()
        {
            // The copy's per-frame depth is derived again every frame; a stable stock depth must
            // never cause a second write.
            float written = GhostAeroFx.CopyCameraDepth(2f, 3f) + GhostAeroFx.CopyCameraDepthStep;
            Assert.False(GhostAeroFx.CameraDepthNeedsWrite(written,
                GhostAeroFx.CopyCameraDepth(2f, 3f) + GhostAeroFx.CopyCameraDepthStep));
        }

        private sealed class FakeRenderer
        {
            public bool Off;
            public bool Throws;
            public int Writes;
        }

        private static StockFxMaskLedger<FakeRenderer> Ledger() => new StockFxMaskLedger<FakeRenderer>(
            r => r.Off,
            (r, off) => { if (r.Throws) throw new System.InvalidOperationException("destroyed"); r.Off = off; r.Writes++; });

        [Fact]
        public void MaskLedger_StockCameraMasksAndPostRenderRestores()
        {
            var a = new FakeRenderer();
            var b = new FakeRenderer();
            var ledger = Ledger();
            ledger.AddCandidate(a);
            ledger.AddCandidate(b);
            ledger.AddCandidate(null);

            Assert.Equal(2, ledger.PreCull(stockFxCamera: true));
            Assert.True(a.Off);
            Assert.True(b.Off);
            Assert.Equal(2, ledger.LastMaskedCount);

            Assert.Equal(2, ledger.PostRender());
            Assert.False(a.Off);
            Assert.False(b.Off);
            Assert.Equal(0, ledger.MaskedCount);
        }

        [Fact]
        public void MaskLedger_AnyOtherCamerasCullRestoresAMissedPostRender()
        {
            // Stock's camera masked, its post-render never came (disabled mid-frame, an
            // exception): the main camera's next cull must find the ghost visible.
            var a = new FakeRenderer();
            var ledger = Ledger();
            ledger.AddCandidate(a);
            ledger.PreCull(stockFxCamera: true);
            Assert.True(a.Off);

            Assert.Equal(0, ledger.PreCull(stockFxCamera: false));
            Assert.False(a.Off);
            Assert.Equal(0, ledger.MaskedCount);
        }

        [Fact]
        public void MaskLedger_TwoStockCullsInARowNeverLoseARenderer()
        {
            // FX camera then velocity camera with no post-render between: the second cull
            // restores and re-masks, so the one restore after still finds every renderer.
            var a = new FakeRenderer();
            var ledger = Ledger();
            ledger.AddCandidate(a);
            ledger.PreCull(true);
            ledger.PreCull(true);
            Assert.True(a.Off);
            Assert.Equal(1, ledger.MaskedCount);
            ledger.Restore();
            Assert.False(a.Off);
        }

        [Fact]
        public void MaskLedger_NeverTouchesARendererSomethingElseSwitchedOff()
        {
            var hiddenByOthers = new FakeRenderer { Off = true };
            var ledger = Ledger();
            ledger.AddCandidate(hiddenByOthers);
            Assert.Equal(0, ledger.PreCull(true));
            ledger.PostRender();
            Assert.True(hiddenByOthers.Off);
            Assert.Equal(0, hiddenByOthers.Writes);
        }

        [Fact]
        public void MaskLedger_AFailingWriteIsCountedAndTheRestStillRestore()
        {
            var a = new FakeRenderer();
            var b = new FakeRenderer();
            var ledger = Ledger();
            ledger.AddCandidate(a);
            ledger.AddCandidate(b);
            ledger.PreCull(true);
            a.Throws = true;   // destroyed between the cull and the restore
            Assert.Equal(1, ledger.Restore());
            Assert.False(b.Off);
            Assert.Equal(0, ledger.MaskedCount);
            Assert.Equal(1, ledger.TakeFailedWrites());
            Assert.Equal(0, ledger.TakeFailedWrites());
        }

        [Fact]
        public void MaskLedger_ClearedCandidatesMaskNothing()
        {
            var a = new FakeRenderer();
            var ledger = Ledger();
            ledger.AddCandidate(a);
            ledger.ClearCandidates();
            Assert.Equal(0, ledger.PreCull(true));
            Assert.False(a.Off);
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
                    "(its own depth map at stock's occlusion size); shared per frame while any ghost draws: " +
                    "sceneDepthPass=1 light=1, no ghost cap; while stock's fire is on: its renderers are " +
                    "hidden from stock's FX and velocity cameras", line);
                Assert.Contains("\"?\"", GhostAeroFx.FormatBuildCost(1, null, 0, 0, 0));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void FormatInputs_StatesTheConditionsInvariantly()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                var inputs = new AeroFxInputs
                {
                    Speed = 1110.27f, ChordSpeed = 1088.5f, StockSpeed = true, Density = 0.032914,
                    Temperature = 218.44, TemperatureOffset = -3.26, Mach = 3.75f, RawFx = 0.836f,
                };
                Assert.Equal("speed=1110.3 speedSource=stock chordSpeed=1088.5 rho=0.03291 temp=218.4K " +
                    "tempOffset=-3.3K mach=3.750 rawFx=0.836", GhostAeroFx.FormatInputs(inputs));
                inputs.StockSpeed = false;
                Assert.Contains("speedSource=chord", GhostAeroFx.FormatInputs(inputs));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void ComputeFxScalar_StocksMach375Reading_NeedsItsSmoothedSpeed()
        {
            // Flight 3 (2026-11-28_0203), real vessel at m375: rho=0.03291 srfSpeed=1110.2
            // mach=3.75 and AerodynamicsFX read FxScalar=0.836 state=0.998. At the logged
            // instantaneous srfSpeed the formula gives 0.796; stock's airSpeed is vessel.speed,
            // FlightIntegrator's ten-physics-frame mean, about 1 % above srfSpeed under this
            // deceleration, and that 1 % is the 0.04.
            float atSrfSpeed = GhostAeroFx.ComputeFxScalar(0.03291, 1110.2, 3.75f, AeroFxConstants.StockDefaults, out _);
            Assert.InRange(atSrfSpeed, 0.786f, 0.806f);
            double smoothed = 1110.2 * 1.0117;
            float atSmoothed = GhostAeroFx.ComputeFxScalar(0.03291, smoothed, 3.7475f, AeroFxConstants.StockDefaults, out float state);
            Assert.InRange(atSmoothed, 0.826f, 0.846f);
            Assert.InRange(state, 0.99f, 1f);
        }

        [Fact]
        public void ComputeFxScalar_TwoPercentSlowSpeed_ReadsTheGhostsLowValue()
        {
            // The same air, the speed 2 % low (the bracket chord's lag): the ghost logged 0.708
            // with state 0.93 at that mark. Speed to the 3.5th power and Mach through the thermal
            // blend (thin air: the scalar is multiplied by state below Mach 3.75) both pull it down.
            double speed = 1110.2 * 0.98;
            float mach = (float)(speed / (1110.2 / 3.75));
            float fx = GhostAeroFx.ComputeFxScalar(0.03291, speed, mach, AeroFxConstants.StockDefaults, out float state);
            Assert.InRange(fx, 0.68f, 0.74f);
            Assert.InRange(state, 0.92f, 0.95f);
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
        // --- per-frame mesh visibility and the rig diagnostics ---

        [Theory]
        [InlineData(true, true, 0, 0x20001, true)]
        [InlineData(true, true, 17, 0x20001, true)]
        [InlineData(true, true, 1, 0x20001, false)]   // a plume or transparent helper: stock skips it
        [InlineData(false, true, 0, 0x20001, false)]  // renderer off
        [InlineData(true, false, 0, 0x20001, false)]  // hidden part (decoupled, inactive root)
        [InlineData(true, true, 5, ~0, true)]          // stock not resolved yet: every layer
        public void SheathMeshVisible_NeedsEnabledActiveAndAStockLayer(bool enabled, bool active, int layer,
            int fxMask, bool expected)
        {
            Assert.Equal(expected, GhostAeroFx.SheathMeshVisible(enabled, active, layer, fxMask));
        }

        [Fact]
        public void ShouldAbandonCapture_WaitsTheTimeoutThenGivesUp()
        {
            Assert.False(GhostAeroFx.ShouldAbandonCapture(100, 100));
            Assert.False(GhostAeroFx.ShouldAbandonCapture(100, 100 + GhostAeroFx.DiagnosticCaptureTimeoutFrames));
            Assert.True(GhostAeroFx.ShouldAbandonCapture(100, 101 + GhostAeroFx.DiagnosticCaptureTimeoutFrames));
        }

        [Fact]
        public void DiagnosticModes_NormalRunsEveryPass()
        {
            Assert.Equal(GhostAeroFxDiagnosticMode.Normal, default(GhostAeroFxDiagnosticMode));
            Assert.True(GhostAeroFx.SceneDepthPassOn(GhostAeroFxDiagnosticMode.Normal));
            Assert.True(GhostAeroFx.UsesReplacementShader(GhostAeroFxDiagnosticMode.Normal));
            Assert.False(GhostAeroFx.SceneDepthPassOn(GhostAeroFxDiagnosticMode.NoSceneDepth));
            Assert.True(GhostAeroFx.UsesReplacementShader(GhostAeroFxDiagnosticMode.NoSceneDepth));
            Assert.True(GhostAeroFx.SceneDepthPassOn(GhostAeroFxDiagnosticMode.SanityUnlit));
            Assert.False(GhostAeroFx.UsesReplacementShader(GhostAeroFxDiagnosticMode.SanityUnlit));
        }

        [Fact]
        public void DescribePixels_CountsWhatDiffersFromTheClearColour()
        {
            var clear = new Color32(0, 0, 0, 0);
            var px = new[]
            {
                clear, clear, new Color32(200, 80, 20, 100), new Color32(10, 250, 0, 50),
            };
            string d = GhostAeroFx.DescribePixels(px, clear);
            Assert.Contains("pixels=4 covered=2 (50.000%)", d);
            Assert.Contains("maxRGB=(200,250,20)", d);
            Assert.Contains("meanAlphaCovered=75.0", d);
            Assert.Equal("pixels=0", GhostAeroFx.DescribePixels(null, clear));
            Assert.Contains("covered=0 ", GhostAeroFx.DescribePixels(new[] { clear }, clear));
        }

        [Fact]
        public void DescribeDepthPixels_ReadsTheRedRangeAgainstTheCorner()
        {
            var px = new[]
            {
                new Color32(0, 0, 0, 255), new Color32(0, 0, 0, 255),
                new Color32(120, 120, 120, 255), new Color32(200, 0, 0, 255),
            };
            string d = GhostAeroFx.DescribeDepthPixels(px);
            Assert.Contains("red=0..200 corner=0 differFromCorner=2 (50.000%)", d);
            Assert.Equal("pixels=0", GhostAeroFx.DescribeDepthPixels(new Color32[0]));
        }

        [Fact]
        public void FormatCameraState_IsInvariantAndNamesTheScreenTarget()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    System.Globalization.CultureInfo.GetCultureInfo("de-DE");
                string row = GhostAeroFx.FormatCameraState("Camera 00", true, true, 0.5f, "Depth", 0x20001,
                    null, 0.21f, 750000f, 60f, false, "None");
                Assert.Equal("'Camera 00' enabled=True active=True depth=0.5 clear=Depth mask=0x20001 " +
                    "target=screen near=0.21 far=750000 fov=60.0 ortho=False depthTex=None", row);
                Assert.Contains("target=rt:512x512:Depth",
                    GhostAeroFx.FormatCameraState("v", false, true, 0f, "SolidColor", 1, "rt:512x512:Depth",
                        0f, 50f, 60f, true, "Depth"));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // --- Rig capture probes (flight 4's base V) ---

        [Fact]
        public void MeasurePixels_CountsAndSumsOnlyNonBackgroundPixels()
        {
            var bg = new Color32(0, 0, 0, 0);
            var px = new[]
            {
                bg, new Color32(10, 20, 30, 255), bg, new Color32(1, 1, 1, 0),
            };
            GhostAeroFx.MeasurePixels(px, bg, out int covered, out long sum);
            Assert.Equal(2, covered);
            Assert.Equal(63L, sum);
            GhostAeroFx.MeasurePixels(null, bg, out covered, out sum);
            Assert.Equal(0, covered);
            Assert.Equal(0L, sum);
        }

        [Fact]
        public void CountDifferingPixels_UsesThePerChannelThreshold()
        {
            var a = new[] { new Color32(100, 0, 0, 255), new Color32(0, 0, 0, 0), new Color32(5, 5, 5, 5) };
            var b = new[] { new Color32(108, 0, 0, 255), new Color32(0, 9, 0, 0), new Color32(5, 5, 5, 200) };
            // 8 is not above 8; 9 is; alpha is not compared.
            Assert.Equal(1, GhostAeroFx.CountDifferingPixels(a, b, 8));
            Assert.Equal(2, GhostAeroFx.CountDifferingPixels(a, b, 7));
            Assert.Equal(-1, GhostAeroFx.CountDifferingPixels(a, new Color32[2], 8));
            Assert.Equal(-1, GhostAeroFx.CountDifferingPixels(null, b, 8));
        }

        [Theory]
        // The camera axis is the map's centre texel.
        [InlineData(0f, 0f, 32.4f, 2048, true, 1024, 1024)]
        // A point 1 m right and 0.5 m down of the axis on a 32.4 m box: 2048 / 64.8 px per metre.
        [InlineData(1f, -0.5f, 32.4f, 2048, true, 1055, 1008)]
        // Right at the edge is off the map; just inside is the last texel.
        [InlineData(32.4f, 0f, 32.4f, 2048, false, -1, -1)]
        [InlineData(-32.39f, 0f, 32.4f, 2048, true, 0, 1024)]
        [InlineData(0f, 0f, 0f, 2048, false, -1, -1)]
        [InlineData(float.NaN, 0f, 10f, 512, false, -1, -1)]
        public void TryVelocityMapTexel_MapsCameraSpaceToTheReadBackTexel(float x, float y, float box, int size,
            bool expected, int px, int py)
        {
            bool on = GhostAeroFx.TryVelocityMapTexel(x, y, box, size, out int tx, out int ty);
            Assert.Equal(expected, on);
            Assert.Equal(px, tx);
            Assert.Equal(py, ty);
        }

        [Fact]
        public void VelocityMapDistance_ReadsBothDepthConventions()
        {
            // Flight 4's map: the shield face read 91 of 255 on a far of 50 m, 32.16 m from the
            // camera 32.4 m ahead of the ghost on a reversed-Z device.
            Assert.Equal(32.16f, GhostAeroFx.VelocityMapDistance(91, 50f, true), 0.01f);
            Assert.Equal(17.84f, GhostAeroFx.VelocityMapDistance(91, 50f, false), 0.01f);
            Assert.Equal(50f, GhostAeroFx.VelocityMapDistance(0, 50f, true), 0.001f);
            Assert.Equal(0f, GhostAeroFx.VelocityMapDistance(0, 50f, false), 0.001f);
        }

        [Fact]
        public void FormatSheathMeshProbe_IsInvariantAndSignsBehindMap()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    System.Globalization.CultureInfo.GetCultureInfo("de-DE");
                string on = GhostAeroFx.FormatSheathMeshProbe(3, "cone", "coneMesh", "KSP/Diffuse[Opaque]", 0,
                    120, 450, 98765L, 33.25f, 32.75f, true, 1024, 1030, 91, 32.16f);
                Assert.Equal("mesh[3] 'cone'/'coneMesh' KSP/Diffuse[Opaque] L0 verts=120 fxPx=450 fxSum=98765 " +
                    "d=33.25m dNear=32.75m mapD=32.16m texel=(1024,1030) raw=91 behindMap=0.59m", on);
                string off = GhostAeroFx.FormatSheathMeshProbe(0, null, null, null, 17, 0, 0, 0L,
                    float.NaN, float.NaN, false, -1, -1, 0, float.NaN);
                Assert.Equal("mesh[0] '?'/'?' ? L17 verts=0 fxPx=0 fxSum=0 d=NaNm dNear=NaNm mapD=off-map", off);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
