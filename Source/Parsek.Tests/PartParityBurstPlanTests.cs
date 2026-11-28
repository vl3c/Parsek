using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the live burst and dust driver's decisions (design-part-ghost-parity.md
    /// Appendix G, piece X4, <see cref="PartParityBurstPlan"/>): wave kinds and admission, the
    /// exclusion filter, the trial slots, tagging, the slot-frame offset, the structure diff, the
    /// control variations, the setup statuses, the ground slots and the water search, stock's
    /// raycast summary, the dust options, the part config read and the control evidence round trip.
    /// </summary>
    public class PartParityBurstPlanTests
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        // ----- wave kinds and admission -----

        [Fact]
        public void WaveKind_BurstFamiliesShareOneKind_DustItsOwn_OthersNull()
        {
            Assert.Equal(PartParityBurstPlan.KindBurst, PartParityBurstPlan.WaveKind(PartParityFxRules.FamilyExplode));
            Assert.Equal(PartParityBurstPlan.KindBurst, PartParityBurstPlan.WaveKind(PartParityFxRules.FamilyVesselExplode));
            Assert.Equal(PartParityBurstPlan.KindBurst, PartParityBurstPlan.WaveKind(PartParityFxRules.FamilyBurstCustom));
            Assert.Equal(PartParityBurstPlan.KindBurst, PartParityBurstPlan.WaveKind(PartParityFxRules.FamilyDecouple));
            Assert.Equal(PartParityBurstPlan.KindDust, PartParityBurstPlan.WaveKind(PartParityFxRules.FamilyDust));
            Assert.Null(PartParityBurstPlan.WaveKind(PartParityFxRules.FamilyPlume));
            Assert.Null(PartParityBurstPlan.WaveKind(null));
        }

        [Fact]
        public void IsP9Kind_And_MaxWaveCases()
        {
            Assert.True(PartParityBurstPlan.IsP9Kind(PartParityBurstPlan.KindBurst));
            Assert.True(PartParityBurstPlan.IsP9Kind(PartParityBurstPlan.KindDust));
            Assert.False(PartParityBurstPlan.IsP9Kind(PartParityStateWave.KindStowed));
            Assert.False(PartParityBurstPlan.IsP9Kind(null));
            Assert.Equal(1, PartParityBurstPlan.MaxWaveCases(PartParityBurstPlan.KindBurst));
            Assert.Equal(PartParityBurstPlan.MaxDustWaveCases, PartParityBurstPlan.MaxWaveCases(PartParityBurstPlan.KindDust));
        }

        [Fact]
        public void ControlWaveKind_EveryBuiltControl_EvenWithNoSubject()
        {
            foreach (string id in TestCommandPartParitySweep.BurstControlIds)
                Assert.Equal(PartParityBurstPlan.KindBurst, PartParityBurstPlan.ControlWaveKind(id));
            foreach (string id in TestCommandPartParitySweep.DustControlIds)
                Assert.Equal(PartParityBurstPlan.KindDust, PartParityBurstPlan.ControlWaveKind(id));
            Assert.Null(PartParityBurstPlan.ControlWaveKind(TestCommandPartParitySweep.ControlEmpty));
            Assert.Null(PartParityBurstPlan.ControlWaveKind(null));
        }

        [Fact]
        public void WaveFlow_BurstAndDustKindsRunTheP9Flows_InEitherContext()
        {
            Assert.Equal(PartParitySweepRunner.FlowBurst, PartParitySweepRunner.WaveFlow(PartParityBurstPlan.KindBurst, true));
            Assert.Equal(PartParitySweepRunner.FlowBurst, PartParitySweepRunner.WaveFlow(PartParityBurstPlan.KindBurst, false));
            Assert.Equal(PartParitySweepRunner.FlowDust, PartParitySweepRunner.WaveFlow(PartParityBurstPlan.KindDust, false));
            Assert.Equal(PartParitySweepRunner.FlowDust, PartParitySweepRunner.WaveFlow(PartParityBurstPlan.KindDust, true));
        }

        [Fact]
        public void AdmitWave_FirstAlways_LaterOnlyWhenTheWorstCaseFits()
        {
            Assert.True(PartParityBurstPlan.AdmitWave(PartParityBurstPlan.KindDust, 1000.0, 60, firstWave: true));
            // burst worst 60 s, dust worst 180 s (the 120 s spool cap plus a minute)
            Assert.True(PartParityBurstPlan.AdmitWave(PartParityBurstPlan.KindBurst, 400.0, 480, firstWave: false));
            Assert.False(PartParityBurstPlan.AdmitWave(PartParityBurstPlan.KindBurst, 421.0, 480, firstWave: false));
            Assert.True(PartParityBurstPlan.AdmitWave(PartParityBurstPlan.KindDust, 300.0, 480, firstWave: false));
            Assert.False(PartParityBurstPlan.AdmitWave(PartParityBurstPlan.KindDust, 301.0, 480, firstWave: false));
        }

        [Fact]
        public void ExclusionApplies_FamilyOfTheRunsWords_InTheRunsContext()
        {
            var e = new PartParityFxExclusion
            {
                Part = "x", Family = PartParityFxRules.FamilyDecouple, Context = PartParityFxRules.ContextBurst,
                Reason = PartParityFxRules.ExcludeDecoupleNoFxGroup,
            };
            Assert.True(PartParityBurstPlan.ExclusionApplies(e, new[] { PartParityFxRules.ArgDecouple }, PartParityFxRules.ContextBurst));
            Assert.False(PartParityBurstPlan.ExclusionApplies(e, new[] { PartParityFxRules.ArgExplode }, PartParityFxRules.ContextBurst));
            Assert.False(PartParityBurstPlan.ExclusionApplies(e, new[] { PartParityFxRules.ArgDecouple }, PartParityFxRules.ContextGround));
            Assert.False(PartParityBurstPlan.ExclusionApplies(e, null, PartParityFxRules.ContextBurst));
            Assert.False(PartParityBurstPlan.ExclusionApplies(null, new[] { PartParityFxRules.ArgDecouple }, PartParityFxRules.ContextBurst));
            var p8 = new PartParityFxExclusion { Family = PartParityFxRules.FamilyPlume, Context = PartParityFxRules.ContextBurst };
            Assert.False(PartParityBurstPlan.ExclusionApplies(p8, new[] { PartParityFxRules.ArgPlume }, PartParityFxRules.ContextBurst));
        }

        // ----- burst slots -----

        [Fact]
        public void TrialNorthOffset_FourSlotsCentred_SpacingApart()
        {
            double s = PartParityFxRules.BurstTrialSpacingM;
            Assert.Equal(-1.5 * s, PartParityBurstPlan.TrialNorthOffsetM(0), 9);
            Assert.Equal(-0.5 * s, PartParityBurstPlan.TrialNorthOffsetM(1), 9);
            Assert.Equal(0.5 * s, PartParityBurstPlan.TrialNorthOffsetM(2), 9);
            Assert.Equal(1.5 * s, PartParityBurstPlan.TrialNorthOffsetM(3), 9);
        }

        [Fact]
        public void BurstTile_ClampedTo16Through128()
        {
            Assert.Equal(128, PartParityBurstPlan.BurstTilePx(512));
            Assert.Equal(96, PartParityBurstPlan.BurstTilePx(96));
            Assert.Equal(16, PartParityBurstPlan.BurstTilePx(4));
        }

        [Fact]
        public void BurstOrtho_FarViewWide_NearViewsTight()
        {
            Assert.Equal(PartParityBurstPlan.BurstFarOrthoM, PartParityBurstPlan.BurstOrthoM(PartParityFxRules.ViewBurstFar));
            Assert.Equal(PartParityBurstPlan.BurstNearOrthoM, PartParityBurstPlan.BurstOrthoM(PartParityFxRules.ViewBurstSide));
            Assert.Equal(PartParityBurstPlan.BurstNearOrthoM, PartParityBurstPlan.BurstOrthoM(PartParityFxRules.ViewBurstTop));
        }

        [Fact]
        public void MetresToPx_ScalesByTileOverTwiceOrtho_NullWhenSenseless()
        {
            Assert.Equal(64.0, PartParityBurstPlan.MetresToPx(15.0, 15.0, 128).Value, 9);
            Assert.Null(PartParityBurstPlan.MetresToPx(double.NaN, 15.0, 128));
            Assert.Null(PartParityBurstPlan.MetresToPx(0.0, 15.0, 128));
            Assert.Null(PartParityBurstPlan.MetresToPx(1.0, 0.0, 128));
            Assert.Null(PartParityBurstPlan.MetresToPx(1.0, 15.0, 0));
        }

        [Fact]
        public void TrialOfFrame_RoundRobin_NegativeSafe()
        {
            Assert.Equal(0, PartParityBurstPlan.TrialOfFrame(0));
            Assert.Equal(3, PartParityBurstPlan.TrialOfFrame(7));
            Assert.Equal(1, PartParityBurstPlan.TrialOfFrame(9));
            Assert.Equal(3, PartParityBurstPlan.TrialOfFrame(-1));
        }

        // ----- tagging -----

        [Fact]
        public void TagRadius_BasePlusHalfTheExtent()
        {
            Assert.Equal(PartParityFxRules.BurstTagRadiusM, PartParityBurstPlan.TagRadiusM(double.NaN), 9);
            Assert.Equal(PartParityFxRules.BurstTagRadiusM, PartParityBurstPlan.TagRadiusM(-1.0), 9);
            Assert.Equal(PartParityFxRules.BurstTagRadiusM + 3.0, PartParityBurstPlan.TagRadiusM(6.0), 9);
        }

        [Fact]
        public void NearestSource_PicksTheClosestInsideTheRadius_ElseMinusOne()
        {
            var sources = new List<double[]> { new[] { 0.0, 0, 0 }, new[] { 20.0, 0, 0 }, null };
            Assert.Equal(0, PartParityBurstPlan.NearestSource(sources, new[] { 3.0, 0, 0 }, 10.0));
            Assert.Equal(1, PartParityBurstPlan.NearestSource(sources, new[] { 14.0, 0, 0 }, 10.0));
            Assert.Equal(-1, PartParityBurstPlan.NearestSource(sources, new[] { 10.0, 50, 0 }, 10.0));
            Assert.Equal(-1, PartParityBurstPlan.NearestSource(null, new[] { 0.0, 0, 0 }, 10.0));
            Assert.Equal(-1, PartParityBurstPlan.NearestSource(sources, new[] { 0.0 }, 10.0));
        }

        [Fact]
        public void SlotFrameOffset_ProjectsOnEastUpNorth()
        {
            double[] east = { 1, 0, 0 }, up = { 0, 1, 0 }, north = { 0, 0, 1 };
            double[] o = PartParityBurstPlan.SlotFrameOffset(east, up, north, new[] { 1.0, 2, 3 }, new[] { 2.0, 5, -1 });
            Assert.Equal(new[] { 1.0, 3.0, -4.0 }, o);
            // a rotated frame: east along +z, north along -x
            double[] o2 = PartParityBurstPlan.SlotFrameOffset(new double[] { 0, 0, 1 }, up, new double[] { -1, 0, 0 },
                new[] { 0.0, 0, 0 }, new[] { -2.0, 0, 5 });
            Assert.Equal(new[] { 5.0, 0.0, 2.0 }, o2);
        }

        [Fact]
        public void StripClone_CutsEveryCloneSuffix()
        {
            Assert.Equal("fx_explosion", PartParityBurstPlan.StripClone("fx_explosion(Clone)"));
            Assert.Equal("fx_explosion", PartParityBurstPlan.StripClone("fx_explosion(Clone) (Clone)"));
            Assert.Equal("plain", PartParityBurstPlan.StripClone("plain"));
            Assert.Null(PartParityBurstPlan.StripClone(null));
            Assert.Equal(string.Empty, PartParityBurstPlan.StripClone(string.Empty));
        }

        [Fact]
        public void DiffStructure_MissingAndExtra_Ordinal()
        {
            var missing = new List<PartParityFxStructureEntry>();
            var extra = new List<PartParityFxStructureEntry>();
            PartParityBurstPlan.DiffStructure(new[] { "b", "a", "c" }, new[] { "c", "d" }, missing, extra);
            Assert.Equal(new[] { "a", "b" }, missing.ConvertAll(m => m.Key));
            Assert.Equal(new[] { "d" }, extra.ConvertAll(m => m.Key));
            Assert.All(missing, m => Assert.True(m.Rendered));
            missing.Clear(); extra.Clear();
            PartParityBurstPlan.DiffStructure(null, null, missing, extra);
            Assert.Empty(missing);
            Assert.Empty(extra);
        }

        // ----- control variations -----

        [Fact]
        public void ControlVariations_EachControlChangesOneThing()
        {
            Assert.True(PartParityBurstPlan.IsRealVsReal(TestCommandPartParitySweep.ControlBurstRealVsReal));
            Assert.True(PartParityBurstPlan.IsRealVsReal(TestCommandPartParitySweep.ControlDustRealVsReal));
            Assert.False(PartParityBurstPlan.IsRealVsReal(null));
            Assert.True(PartParityBurstPlan.TriggersNothing(TestCommandPartParitySweep.ControlBurstEmpty));
            Assert.False(PartParityBurstPlan.TriggersNothing(TestCommandPartParitySweep.ControlBurstOff));
            Assert.True(PartParityBurstPlan.DropsGhostEvent(TestCommandPartParitySweep.ControlBurstOff));
            Assert.True(PartParityBurstPlan.DropsGhostEvent(TestCommandPartParitySweep.ControlBurstEmpty));
            Assert.True(PartParityBurstPlan.DropsGhostEvent(TestCommandPartParitySweep.ControlBurstRealVsReal));
            Assert.False(PartParityBurstPlan.DropsGhostEvent(TestCommandPartParitySweep.ControlBurstLate));
            Assert.False(PartParityBurstPlan.DropsGhostEvent(null));
            Assert.Equal(PartParityFxRules.BurstLateControlSeconds,
                PartParityBurstPlan.GhostTriggerDelaySeconds(TestCommandPartParitySweep.ControlBurstLate));
            Assert.Equal(0.0, PartParityBurstPlan.GhostTriggerDelaySeconds(null));
            Assert.Equal(PartParityFxRules.BurstShiftControlM,
                PartParityBurstPlan.GhostTriggerShiftM(TestCommandPartParitySweep.ControlBurstShift));
            Assert.Equal(0.0, PartParityBurstPlan.GhostTriggerShiftM(TestCommandPartParitySweep.ControlBurstHue));
            Assert.True(PartParityBurstPlan.RotatesGhostHue(TestCommandPartParitySweep.ControlBurstHue));
            Assert.False(PartParityBurstPlan.RotatesGhostHue(null));
            Assert.True(PartParityBurstPlan.LowersGhostPower(TestCommandPartParitySweep.ControlBurstPower));
            Assert.False(PartParityBurstPlan.LowersGhostPower(null));
            Assert.True(PartParityBurstPlan.HidesGhostDust(TestCommandPartParitySweep.ControlDustOff));
            Assert.False(PartParityBurstPlan.HidesGhostDust(TestCommandPartParitySweep.ControlDustAboveReach));
        }

        [Theory]
        [InlineData(0, true, 0)]
        [InlineData(1, false, 0)]
        [InlineData(2, true, 1)]
        [InlineData(3, false, 1)]
        public void RealVsRealTrial_EvenTrialsAreTheRealSide(int trial, bool real, int number)
        {
            PartParityBurstPlan.RealVsRealTrial(trial, out bool realSide, out int n);
            Assert.Equal(real, realSide);
            Assert.Equal(number, n);
        }

        [Fact]
        public void LowerIndexPower_DrawsThePrefabOneIndexLower()
        {
            const int prefabs = 7;
            int i = PartParityFxRules.FxMongerPrefabIndex(0.9, prefabs);
            double lower = PartParityBurstPlan.LowerIndexPower(0.9, prefabs);
            Assert.Equal(i - 1, PartParityFxRules.FxMongerPrefabIndex(lower, prefabs));
            // index 0 stays at index 0
            Assert.Equal(0, PartParityFxRules.FxMongerPrefabIndex(PartParityBurstPlan.LowerIndexPower(0.05, prefabs), prefabs));
            // senseless inputs fall back without throwing
            Assert.Equal(0.0, PartParityBurstPlan.LowerIndexPower(double.NaN, prefabs));
            Assert.Equal(0.4, PartParityBurstPlan.LowerIndexPower(0.4, 1));
            Assert.Equal(0.4, PartParityBurstPlan.LowerIndexPower(0.4, 0));
        }

        // ----- setup statuses -----

        [Fact]
        public void IsFxMongerFamily_TheBlastFamiliesOnly()
        {
            Assert.True(PartParityBurstPlan.IsFxMongerFamily(PartParityFxRules.FamilyExplode));
            Assert.True(PartParityBurstPlan.IsFxMongerFamily(PartParityFxRules.FamilyVesselExplode));
            Assert.True(PartParityBurstPlan.IsFxMongerFamily(PartParityFxRules.FamilyBurstCustom));
            Assert.False(PartParityBurstPlan.IsFxMongerFamily(PartParityFxRules.FamilyDecouple));
            Assert.False(PartParityBurstPlan.IsFxMongerFamily(PartParityFxRules.FamilyDust));
        }

        [Fact]
        public void BurstSetupStatus_OkOnlyWhenStockDrew()
        {
            string ok = TestCommandPartParitySweep.SetupOk, not = TestCommandPartParitySweep.SetupBurstNotTriggered;
            string ex = PartParityFxRules.FamilyExplode, dec = PartParityFxRules.FamilyDecouple;
            Assert.Equal(ok, PartParityBurstPlan.BurstSetupStatus(null, PartParityFxRules.StateExplode, ex, true, true, 1, 1));
            Assert.Equal(not, PartParityBurstPlan.BurstSetupStatus(null, PartParityFxRules.StateExplode, ex, false, true, 1, 1));
            Assert.Equal(not, PartParityBurstPlan.BurstSetupStatus(null, PartParityFxRules.StateExplode, ex, true, false, 1, 1));
            Assert.Equal(not, PartParityBurstPlan.BurstSetupStatus(null, PartParityFxRules.StateExplode, ex, true, true, 0, 1));
            Assert.Equal(not, PartParityBurstPlan.BurstSetupStatus(null, PartParityFxRules.StateExplode, ex, true, true, 1, 0));
            // a decouple owes no FXMonger blast
            Assert.Equal(ok, PartParityBurstPlan.BurstSetupStatus(null, PartParityFxRules.StateDecouple, dec, true, true, 2, 0));
            // a joint break draws nothing by design: only the trigger counts
            Assert.Equal(ok, PartParityBurstPlan.BurstSetupStatus(null, PartParityFxRules.StateJointBreak, dec, true, false, 0, 0));
            Assert.Equal(not, PartParityBurstPlan.BurstSetupStatus(null, PartParityFxRules.StateJointBreak, dec, false, false, 0, 0));
            // the empty control triggers nothing and is ok
            Assert.Equal(ok, PartParityBurstPlan.BurstSetupStatus(TestCommandPartParitySweep.ControlBurstEmpty,
                PartParityFxRules.StateExplode, ex, false, false, 0, 0));
        }

        [Fact]
        public void DustSetupStatus_NoHitIsItsOwnStatus()
        {
            Assert.Equal(TestCommandPartParitySweep.SetupDustNoStockHit, PartParityBurstPlan.DustSetupStatus(0));
            Assert.Equal(TestCommandPartParitySweep.SetupOk, PartParityBurstPlan.DustSetupStatus(3));
        }

        // ----- the ground context -----

        [Fact]
        public void DustSlots_CentredSpacingApart()
        {
            double s = PartParityBurstPlan.DustSlotSpacingM;
            Assert.Equal(0.0, PartParityBurstPlan.DustSlotNorthOffsetM(0, 1), 9);
            Assert.Equal(-0.5 * s, PartParityBurstPlan.DustSlotNorthOffsetM(0, 2), 9);
            Assert.Equal(1.5 * s, PartParityBurstPlan.DustSlotNorthOffsetM(3, 4), 9);
            Assert.Equal(0.0, PartParityBurstPlan.DustSlotNorthOffsetM(0, 0), 9);
        }

        [Fact]
        public void DustRootAltitude_PutsTheNozzleAtTheSlotHeight()
        {
            Assert.Equal(75.0 + 10.0 - 1.5, PartParityBurstPlan.DustRootAltitudeM(75.0, 10.0, 1.5), 9);
            Assert.Equal(20.0 + 2.0, PartParityBurstPlan.DustRootAltitudeM(0.0, 20.0, -2.0), 9);
        }

        [Fact]
        public void FindWaterEast_FirstDeepPointPlusInset_NaNWhenNone()
        {
            Assert.Equal(1200.0 + PartParityBurstPlan.WaterInsetM,
                PartParityBurstPlan.FindWaterEastM(d => d < 1200.0 ? 60.0 : -50.0), 9);
            // a beach (shallower than the depth line) does not count
            Assert.Equal(2000.0 + PartParityBurstPlan.WaterInsetM,
                PartParityBurstPlan.FindWaterEastM(d => d < 1500.0 ? 10.0 : (d < 2000.0 ? -5.0 : -30.0)), 9);
            Assert.True(double.IsNaN(PartParityBurstPlan.FindWaterEastM(d => 80.0)));
            Assert.True(double.IsNaN(PartParityBurstPlan.FindWaterEastM(d => double.NaN)));
            Assert.True(double.IsNaN(PartParityBurstPlan.FindWaterEastM(null)));
        }

        [Fact]
        public void DustLoadRange_CoversTheWaterSearch()
        {
            Assert.True(PartParityBurstPlan.DustLoadRangeM > PartParityBurstPlan.WaterSearchEndM + PartParityBurstPlan.WaterInsetM);
        }

        [Theory]
        [InlineData(0, PartParityFxRules.DustSurfaceNone)]
        [InlineData(1, PartParityFxRules.DustSurfaceTerrain)]
        [InlineData(2, PartParityFxRules.DustSurfaceWater)]
        [InlineData(3, PartParityFxRules.DustSurfaceLaunchpad)]
        [InlineData(9, PartParityFxRules.DustSurfaceNone)]
        public void SurfaceName_MapsStocksSurfaceType(int type, string name)
        {
            Assert.Equal(name, PartParityBurstPlan.SurfaceName(type));
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(3, true)]
        public void DustSampleHit_ReadsStocksOwnSurfaceClassification(int type, bool hit)
        {
            // Stock's ocean branch sets hit = Water with raycastHit false (the first live run read
            // every water sample as a miss while stock drew dust at distance 12 m, ScaledFX 0.19).
            Assert.Equal(hit, PartParityBurstPlan.DustSampleHit(type));
        }

        // ----- the dust ignition -----

        private static PartParityFxOption DustEngine(bool spool, double accel)
            => new PartParityFxOption
            {
                Family = PartParityFxRules.FamilyDust, Throttle = PartParityFxRules.BurnFullThrottle,
                UseEngineResponseTime = spool, EngineAccelerationSpeed = accel,
            };

        [Fact]
        public void DustIgnitionTimeout_Rocket_FiveSeconds()
        {
            Assert.Equal(5.0, PartParityBurstPlan.DustIgnitionTimeoutFor(DustEngine(false, double.NaN)));
            Assert.Equal(5.0, PartParityBurstPlan.DustIgnitionTimeoutFor(null));
        }

        [Fact]
        public void DustIgnitionTimeout_Jet_TheP8SpoolTimeoutCoveringStocksOwnSpool()
        {
            // The Wheesley (0.12 /s) and the Goliath (0.06 /s) spool slower than 5 s; the first live
            // run timed out jetengine, minijetengine and turbofansize2 in all three slots.
            foreach (double accel in new[] { 0.06, 0.12, 0.2 })
            {
                double timeout = PartParityBurstPlan.DustIgnitionTimeoutFor(DustEngine(true, accel));
                Assert.Equal(PartParityFxRules.SpoolPressTimeoutSeconds(1.0, accel), timeout);
                double spool = Math.Log(1.0 / PartParityFxRules.ThrottleDoneTolerance) / accel;
                Assert.True(timeout >= spool, $"accel {accel}: timeout {timeout} under the spool {spool}");
            }
            Assert.Equal(PartParityFxRules.SpoolTimeoutSeconds,
                PartParityBurstPlan.DustIgnitionTimeoutFor(DustEngine(true, double.NaN)));
        }

        [Fact]
        public void DustWaveWorst_CoversTheLongestIgnitionWaitAndTheWarmUpCap()
        {
            double longestIgnition = PartParityBurstPlan.DustIgnitionTimeoutFor(DustEngine(true, 1e-6));
            Assert.Equal(PartParityFxRules.SpoolTimeoutCapSeconds, longestIgnition);
            Assert.True(PartParityBurstPlan.DustWaveWorstSeconds >= longestIgnition + PartParityFxRules.PlateauLifetimeCapSeconds);
        }

        [Fact]
        public void DustRequestedThrottle_OptionsElseFull()
        {
            Assert.Equal(1.0, PartParityBurstPlan.DustRequestedThrottle(null));
            Assert.Equal(1.0, PartParityBurstPlan.DustRequestedThrottle(new PartParityFxOption { Throttle = double.NaN }));
            Assert.Equal(0.3, PartParityBurstPlan.DustRequestedThrottle(new PartParityFxOption { Throttle = 0.3 }));
        }

        [Theory]
        [InlineData(true, 1.0, true)]
        [InlineData(true, 0.995, true)]
        [InlineData(true, 0.5001, false)]   // a jet halfway up its spool is not lit yet
        [InlineData(false, 1.0, false)]     // the recorder would not record it ignited
        [InlineData(true, double.NaN, false)]
        public void DustEngineLit_RecorderIgnitedAndThrottleAtTheRequest(bool ignited, double current, bool lit)
        {
            Assert.Equal(lit, PartParityBurstPlan.DustEngineLit(ignited, current, 1.0));
        }

        // ----- the raycast summary -----

        private static PartParityDustRaycastSample Hit(string surface, double d, double scale)
            => new PartParityDustRaycastSample
            {
                Hit = true, Surface = surface, DistanceM = d, ScaledFx = scale,
                PointM = new[] { 0.0, -d, 0.0 }, Normal = new[] { 0.0, 1.0, 0.0 },
            };

        [Fact]
        public void SummarizeRaycast_NoHit_SurfaceNone()
        {
            var s = PartParityBurstPlan.SummarizeRaycast(new[] { new PartParityDustRaycastSample(), new PartParityDustRaycastSample() });
            Assert.Equal(0, s.Hits);
            Assert.Equal(2, s.Misses);
            Assert.Equal(PartParityFxRules.DustSurfaceNone, s.Surface);
            Assert.True(double.IsNaN(s.DistanceM));
            Assert.Null(s.PointM);
            Assert.Equal(2, s.Samples.Count);
            Assert.Equal(0, PartParityBurstPlan.SummarizeRaycast(null).Hits);
        }

        [Fact]
        public void SummarizeRaycast_MostFrequentSurface_MeansOverHits()
        {
            var samples = new List<PartParityDustRaycastSample>
            {
                Hit(PartParityFxRules.DustSurfaceTerrain, 10.0, 0.5),
                Hit(PartParityFxRules.DustSurfaceWater, 20.0, 0.3),
                Hit(PartParityFxRules.DustSurfaceWater, 30.0, 0.1),
                new PartParityDustRaycastSample(),
                null,
            };
            var s = PartParityBurstPlan.SummarizeRaycast(samples);
            Assert.Equal(3, s.Hits);
            Assert.Equal(1, s.Misses);
            Assert.Equal(4, s.Samples.Count);
            Assert.Equal(PartParityFxRules.DustSurfaceWater, s.Surface);
            Assert.Equal(20.0, s.DistanceM, 9);
            Assert.Equal(0.3, s.ScaledFx, 9);
            Assert.Equal(-20.0, s.PointM[1], 9);
            Assert.Equal(1.0, s.Normal[1], 9);
        }

        [Fact]
        public void SummarizeRaycast_TieBreaksInSurfaceOrder()
        {
            var s = PartParityBurstPlan.SummarizeRaycast(new[]
            {
                Hit(PartParityFxRules.DustSurfaceWater, 5.0, 1.0), Hit(PartParityFxRules.DustSurfaceTerrain, 5.0, 1.0),
            });
            Assert.Equal(PartParityFxRules.DustSurfaceTerrain, s.Surface);
        }

        [Fact]
        public void DustSampleRounds_DoubledForRealVsReal()
        {
            Assert.Equal(16, PartParityBurstPlan.DustSampleRounds(null));
            Assert.Equal(16, PartParityBurstPlan.DustSampleRounds(TestCommandPartParitySweep.ControlDustOff));
            Assert.Equal(32, PartParityBurstPlan.DustSampleRounds(TestCommandPartParitySweep.ControlDustRealVsReal));
        }

        // ----- dust options -----

        private static PartParityFxOption DustLow() => new PartParityFxOption
        {
            Family = PartParityFxRules.FamilyDust, State = PartParityFxRules.StateDustLow, Context = PartParityFxRules.ContextGround,
            Module = PartParityFxRules.ModuleSurfaceFx, ModuleIndex = 3, EngineModule = "ModuleEnginesFX",
            ThrustProviderModuleIndex = 1, SurfaceFxMaxDistanceM = 50.0, SlotFraction = 0.2, SlotHeightM = 10.0,
        };

        [Fact]
        public void PlumeOptionForDust_ThePlumeOfTheEngineBehind_OriginalUntouched()
        {
            PartParityFxOption dust = DustLow();
            PartParityFxOption p = PartParityBurstPlan.PlumeOptionForDust(dust);
            Assert.Equal(PartParityFxRules.FamilyPlume, p.Family);
            Assert.Equal(PartParityFxRules.StateBurnFull, p.State);
            Assert.Equal("ModuleEnginesFX", p.Module);
            Assert.Equal(1, p.ModuleIndex);
            Assert.Equal(PartParityFxRules.ContextGround, p.Context);
            Assert.Equal(PartParityFxRules.FamilyDust, dust.Family);
            Assert.Equal(3, dust.ModuleIndex);
            Assert.Null(PartParityBurstPlan.PlumeOptionForDust(null));
        }

        [Fact]
        public void AboveReachOption_SlotPastTheReach()
        {
            PartParityFxOption o = PartParityBurstPlan.AboveReachOption(DustLow());
            Assert.Equal(PartParityFxRules.DustAboveReachFraction, o.SlotFraction, 9);
            Assert.Equal(PartParityFxRules.DustAboveReachFraction * 50.0, o.SlotHeightM, 9);
            Assert.True(o.SlotHeightM > o.SurfaceFxMaxDistanceM);
            Assert.Null(PartParityBurstPlan.AboveReachOption(null));
        }

        // ----- part config -----

        [Fact]
        public void ReadPartConfig_PotentialInvariant_FxGroupsInOrder()
        {
            var p = new PartParityBurstPart();
            PartParityBurstPlan.ReadPartConfig(new[]
            {
                new KeyValuePair<string, string>("name", "x"),
                new KeyValuePair<string, string>(PartParityFxRules.KeyExplosionPotential, "0.25"),
                new KeyValuePair<string, string>("fx_gasBurst_white", "0.0, 0.1, 0.0, 0.0, 1.0, 0.0, decouple"),
                new KeyValuePair<string, string>("fx_other", "0, 0, 0, activate"),
                new KeyValuePair<string, string>("fx_dup", "0, 0, 0, decouple"),
                new KeyValuePair<string, string>(null, "ignored"),
            }, p);
            Assert.Equal(0.25, p.ExplosionPotential, 9);
            Assert.Equal(new[] { "decouple", "activate" }, p.FxGroups);
        }

        [Fact]
        public void ReadPartConfig_DefaultPotential_WhenAbsentOrUnreadable()
        {
            var a = new PartParityBurstPart { ExplosionPotential = 0.9 };
            PartParityBurstPlan.ReadPartConfig(null, a);
            Assert.Equal(PartParityFxRules.DefaultExplosionPotential, a.ExplosionPotential, 9);
            var b = new PartParityBurstPart();
            PartParityBurstPlan.ReadPartConfig(new[] { new KeyValuePair<string, string>(PartParityFxRules.KeyExplosionPotential, "0,25x") }, b);
            Assert.Equal(PartParityFxRules.DefaultExplosionPotential, b.ExplosionPotential, 9);
        }

        [Fact]
        public void ReadPartConfig_ParsesUnderAnotherCulture()
        {
            CultureInfo prior = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var p = new PartParityBurstPart();
                PartParityBurstPlan.ReadPartConfig(new[] { new KeyValuePair<string, string>(PartParityFxRules.KeyExplosionPotential, "0.75") }, p);
                Assert.Equal(0.75, p.ExplosionPotential, 9);
            }
            finally { CultureInfo.CurrentCulture = prior; }
        }

        // ----- control evidence -----

        [Fact]
        public void BurstEvidence_RoundTrips_MeasuresInTableOrder()
        {
            var e = new PartParityBurstControlEvidence { AllCoverageZero = true, RealBlasts = 2, GhostBlasts = 0, ScheduleIndexDiffers = true };
            e.FailingMeasures.Add(PartParityFxRules.MeasureSchedule);
            e.FailingMeasures.Add(PartParityFxRules.MeasurePresence);
            string flag = PartParityBurstPlan.FormatBurstEvidence(e);
            Assert.StartsWith(PartParityBurstPlan.BurstEvidencePrefix, flag);
            Assert.Contains("failing=" + PartParityFxRules.MeasurePresence + "," + PartParityFxRules.MeasureSchedule, flag);
            var back = PartParityBurstPlan.ParseBurstEvidence(new[] { "other:1", flag }, TestCommandPartParitySweep.SetupOk);
            Assert.True(back.AllCoverageZero);
            Assert.Equal(2, back.RealBlasts);
            Assert.Equal(0, back.GhostBlasts);
            Assert.True(back.ScheduleIndexDiffers);
            Assert.Equal(2, back.FailingMeasures.Count);
            Assert.Null(PartParityBurstPlan.FormatBurstEvidence(null));
            Assert.Null(PartParityBurstPlan.ParseBurstEvidence(new[] { "other:1" }, null));
            Assert.Null(PartParityBurstPlan.ParseBurstEvidence(null, null));
        }

        [Fact]
        public void DustEvidence_RoundTrips()
        {
            var e = new PartParityDustControlEvidence { StockRaycastHit = true, RealDustCovered = true, GhostDustCovered = false };
            e.FailingDustMeasures.Add(PartParityFxRules.MeasurePresence);
            string flag = PartParityBurstPlan.FormatDustEvidence(e);
            Assert.StartsWith(PartParityBurstPlan.DustEvidencePrefix, flag);
            var back = PartParityBurstPlan.ParseDustEvidence(new[] { flag }, TestCommandPartParitySweep.SetupOk);
            Assert.True(back.StockRaycastHit);
            Assert.True(back.RealDustCovered);
            Assert.False(back.GhostDustCovered);
            Assert.Contains(PartParityFxRules.MeasurePresence, back.FailingDustMeasures);
            Assert.Empty(back.FailingFlameMeasures);
            Assert.Null(PartParityBurstPlan.FormatDustEvidence(null));
        }

        [Fact]
        public void IsBuiltControl_TheBurstAndDustControlsOnly()
        {
            foreach (string id in TestCommandPartParitySweep.BurstControlIds) Assert.True(PartParityBurstPlan.IsBuiltControl(id));
            foreach (string id in TestCommandPartParitySweep.DustControlIds) Assert.True(PartParityBurstPlan.IsBuiltControl(id));
            Assert.False(PartParityBurstPlan.IsBuiltControl(TestCommandPartParitySweep.ControlEmpty));
            Assert.False(PartParityBurstPlan.IsBuiltControl(null));
        }

        [Fact]
        public void JudgeControl_ReadsTheEvidenceFlagThroughX1sJudges()
        {
            var e = new PartParityBurstControlEvidence { RealBlasts = 1, GhostBlasts = 0 };
            e.FailingMeasures.Add(PartParityFxRules.MeasurePresence);
            var flags = new List<string> { PartParityBurstPlan.FormatBurstEvidence(e) };
            Assert.Equal(TestCommandPartParitySweep.ControlsOk,
                PartParityBurstPlan.JudgeControl(TestCommandPartParitySweep.ControlBurstOff, flags, TestCommandPartParitySweep.SetupOk, out _));
            Assert.Equal(TestCommandPartParitySweep.ControlsBad,
                PartParityBurstPlan.JudgeControl(TestCommandPartParitySweep.ControlBurstEmpty, flags, TestCommandPartParitySweep.SetupOk, out _));
        }

        [Fact]
        public void JudgeControl_NoFlag_SetupFaultIsBad_NoEvidenceIsBad()
        {
            Assert.Equal(TestCommandPartParitySweep.ControlsBad,
                PartParityBurstPlan.JudgeControl(TestCommandPartParitySweep.ControlBurstHue, new List<string>(),
                    TestCommandPartParitySweep.SetupSpawnTimeout, out string d1));
            Assert.Contains(TestCommandPartParitySweep.SetupSpawnTimeout, d1);
            Assert.Equal(TestCommandPartParitySweep.ControlsBad,
                PartParityBurstPlan.JudgeControl(TestCommandPartParitySweep.ControlDustOff, new List<string>(),
                    TestCommandPartParitySweep.SetupOk, out string d2));
            Assert.Equal("no evidence", d2);
        }

        [Fact]
        public void JudgeControl_AboveReach_NoStockHitIsItsPass()
        {
            var e = new PartParityDustControlEvidence();
            var flags = new List<string> { PartParityBurstPlan.FormatDustEvidence(e) };
            Assert.Equal(TestCommandPartParitySweep.ControlsOk,
                PartParityBurstPlan.JudgeControl(TestCommandPartParitySweep.ControlDustAboveReach, flags,
                    TestCommandPartParitySweep.SetupDustNoStockHit, out _));
        }

        [Fact]
        public void EvidenceFlags_AreCultureInvariant()
        {
            CultureInfo prior = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
                var e = new PartParityBurstControlEvidence { RealBlasts = 12, GhostBlasts = 3 };
                string flag = PartParityBurstPlan.FormatBurstEvidence(e);
                Assert.Contains("real=12;ghost=3", flag);
                Assert.Equal(12, PartParityBurstPlan.ParseBurstEvidence(new[] { flag }, null).RealBlasts);
            }
            finally { CultureInfo.CurrentCulture = prior; }
        }

        [Fact]
        public void Constants_PinnedValues()
        {
            Assert.Equal("p9:burst", PartParityBurstPlan.KindBurst);
            Assert.Equal("p9:dust", PartParityBurstPlan.KindDust);
            Assert.Equal(128, PartParityBurstPlan.BurstMaxTilePx);
            Assert.Equal(15000.0, PartParityBurstPlan.DustLoadRangeM);
            Assert.Equal(1.5, PartParityBurstPlan.BurstSettleSeconds);
            Assert.Equal("1.5", PartParityBurstPlan.BurstSettleSeconds.ToString(Ic));
        }
    }
}
