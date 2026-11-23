using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Headless half of the stock engine FX power rules (todo PARTPARITY-FX-AIR-FIRST-READINGS,
    /// PARTPARITY-FX-FIRST-READINGS). Expected numbers are stock config values run through the
    /// decompiled arithmetic, or readings of the real part from the F3 / F7 flights.
    /// </summary>
    [Collection("Sequential")]
    public class GhostEngineFxPowerTests : IDisposable
    {
        public GhostEngineFxPowerTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ConfigNode Node(params string[] emissionLines)
        {
            var node = new ConfigNode("PREFAB_PARTICLE");
            foreach (string line in emissionLines) node.AddValue("emission", line);
            return node;
        }

        // ---- FXCurve -----------------------------------------------------------------

        /// <summary>RAPIER closed cycle, F3 burn-low: the real emitter's speed read 9.09 against the
        /// ghost's asset speed 13.96 (0.651). A linear segment would read 0.71.</summary>
        [Fact]
        public void Curve_SegmentIsSmoothstep_AsTheRealRapierReads()
        {
            var node = new ConfigNode("MODEL_MULTI_PARTICLE");
            node.AddValue("speed", "0.0 0.5");
            node.AddValue("speed", "1.0 1.2");
            GhostFxCurve speed = GhostFxCurve.Read(node, "speed");
            Assert.Equal(0.6512f, speed.Value(0.3f), 1e-4f);
            Assert.Equal(1.2f, speed.Value(1f), 1e-6f);
            Assert.Equal(0.5f, speed.Value(0f), 1e-6f);
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(0.05f, 0f)]
        [InlineData(0.075f, 0.25f)]
        [InlineData(0.2253f, 0.3208f)]
        [InlineData(1f, 1.25f)]
        [InlineData(2f, 1.25f)]   // clamped past the last key
        [InlineData(-1f, 0f)]
        public void Curve_StockJetSmokeEmission(float power, float expected)
        {
            GhostFxCurve emission = GhostFxCurve.Read(
                Node("0.0 0.0", "0.05 0.0", "0.075 0.25", "1.0 1.25"), "emission");
            Assert.Equal(expected, emission.Value(power), 1e-3f);
            Assert.Equal(4, emission.KeyCount);
        }

        [Fact]
        public void Curve_NoValue_IsStocksDefaultOne()
        {
            GhostFxCurve curve = GhostFxCurve.Read(new ConfigNode("X"), "emission");
            Assert.True(curve.IsConstant);
            Assert.Equal(1f, curve.Value(0.1f));
            Assert.Equal(1f, GhostFxCurve.Read(null, "emission").Value(0.5f));
        }

        [Fact]
        public void Curve_OneValue_IsAConstantOfItsFirstToken()
        {
            Assert.Equal(2f, GhostFxCurve.Read(Node("2.0"), "emission").Value(0.3f));
            Assert.Equal(0.5f, GhostFxCurve.Read(Node("0.5 4.0"), "emission").Value(1f));
        }

        [Fact]
        public void Curve_KeysAreSorted_DuplicateTimeKeepsTheFirst_BadLineSkipped()
        {
            GhostFxCurve curve = GhostFxCurve.Read(
                Node("1.0 4.0", "0.0 2.0", "1.0 9.0", "oops", "x y"), "emission");
            Assert.Equal(2, curve.KeyCount);
            Assert.Equal(2f, curve.Value(0f));
            Assert.Equal(4f, curve.Value(1f));
            Assert.Equal(3f, curve.Value(0.5f), 1e-6f);
        }

        [Fact]
        public void Curve_ParsesUnderACommaCulture()
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal(0.75f, GhostFxCurve.Read(Node("0.0 0.75", "1.0 0.75"), "emission").Value(0.4f), 1e-6f);
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Curve_NaNPower_ReadsTheFirstKey()
        {
            Assert.Equal(0f, GhostFxCurve.Read(Node("0.0 0.0", "1.0 1.0"), "emission").Value(float.NaN));
        }

        [Theory]
        [InlineData("MODEL_MULTI_PARTICLE", true)]
        [InlineData("MODEL_PARTICLE", true)]
        [InlineData("PREFAB_PARTICLE", true)]
        [InlineData("MODEL_MULTI_PARTICLE_PERSIST", false)]   // SmokeScreen: sub-node curves
        [InlineData(null, false)]
        public void Curves_OnlyStockNodeTypes(string nodeType, bool expected)
        {
            Assert.Equal(expected, GhostEngineFxCurves.ForNode(nodeType, new ConfigNode("X")) != null);
            Assert.Null(GhostEngineFxCurves.ForNode(nodeType, null));
        }

        // ---- the scan carries the curves ----------------------------------------------

        [Fact]
        public void Scan_PrefabEntryCarriesItsCurves_AndAddedPlumesCarryNone()
        {
            ConfigNode[] groups = { new ConfigNode("running_open") };
            ConfigNode prefab = groups[0].AddNode("PREFAB_PARTICLE");
            prefab.AddValue("prefabName", "fx_smokeTrail_aeroSpike");
            prefab.AddValue("transformName", "smokePoint");
            prefab.AddValue("emission", "0.0 0.0");
            prefab.AddValue("emission", "1.0 1.25");
            var entries = new List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName, GhostEngineFxCurves fxCurves)>();
            EngineFxBuilder.ScanEffectsPrefabParticleEntries(groups, new[] { "running_open" }, entries);
            Assert.True(EngineFxBuilder.TryAddRapierWhiteFlame(entries, 0, _ => true, null, out _));
            Assert.Equal(2, entries[0].fxCurves.emission.KeyCount);
            Assert.True(entries[0].fxCurves.energy.IsConstant);
            Assert.Null(entries[1].fxCurves);
        }

        [Fact]
        public void Scan_ModelEntryCarriesItsCurves()
        {
            ConfigNode[] groups = { new ConfigNode("IonPlume") };
            ConfigNode model = groups[0].AddNode("MODEL_MULTI_PARTICLE");
            model.AddValue("modelName", "Squad/FX/IonPlume");
            model.AddValue("transformName", "thrustTransform");
            model.AddValue("emission", "0.0 0.0");
            model.AddValue("emission", "0.25 0.5");
            model.AddValue("emission", "1.0 1.0");
            var entries = new List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName, GhostEngineFxCurves fxCurves)>();
            FloatCurve emission = null, speed = null;
            EngineFxBuilder.ScanEffectsModelFxEntries(groups, new[] { "IonPlume" }, entries, ref emission, ref speed);
            // The values are no sub-node, so the FloatCurve the S1 fallback reads stays null.
            Assert.Null(emission);
            GhostEngineFxCurves curves = Assert.Single(entries).fxCurves;
            // Real ion engine, F3 at 30 %: 32.06 particles against 59.5 at full (0.54; the emitter
            // adds one per frame).
            Assert.Equal(0.5064f, curves.emission.Value(0.3f), 1e-3f);
        }

        // ---- channels ------------------------------------------------------------------

        [Theory]
        // JetEngine: powerEffectName = running_thrust, spoolEffectName = running_turbine.
        [InlineData("running_thrust", "running_thrust", "running", "running_turbine", GhostEngineFxChannel.Thrust)]
        [InlineData("running_turbine", "running_thrust", "running", "running_turbine", GhostEngineFxChannel.Spool)]
        // turboFanEngine: runningEffectName = shockDiamond.
        [InlineData("shockDiamond", "running_thrust", "shockDiamond", "running_turbine", GhostEngineFxChannel.Throttle)]
        // A group two fields name: FXUpdate calls power last.
        [InlineData("g", "g", "g", "g", GhostEngineFxChannel.Thrust)]
        [InlineData("g", "p", "g", "g", GhostEngineFxChannel.Throttle)]
        [InlineData("unknown", "p", "r", "s", GhostEngineFxChannel.Throttle)]
        [InlineData(null, null, null, null, GhostEngineFxChannel.Throttle)]
        [InlineData("", "", "", "", GhostEngineFxChannel.Throttle)]
        internal void ResolveChannel_FollowsFxUpdate(
            string group, string power, string running, string spool, GhostEngineFxChannel expected)
        {
            Assert.Equal(expected, GhostEngineFxPower.ResolveChannel(group, power, running, spool));
        }

        [Theory]
        [InlineData(GhostEngineFxChannel.Throttle, 0.3f, 0.5f, 0.05f, 0.3f)]
        [InlineData(GhostEngineFxChannel.Thrust, 0.3f, 0.5f, 0.05f, 0.15f)]
        [InlineData(GhostEngineFxChannel.Spool, 0.3f, 0.5f, 0.05f, 0.3f)]
        [InlineData(GhostEngineFxChannel.Spool, 0.01f, 0.5f, 0.05f, 0.05f)]
        internal void ChannelPower_PerChannel(
            GhostEngineFxChannel channel, float throttle, float fraction, float idle, float expected)
        {
            Assert.Equal(expected, GhostEngineFxPower.ChannelPower(channel, throttle, fraction, idle), 1e-6f);
        }

        [Theory]
        [InlineData(50f, 0.3208f, 16)]
        [InlineData(50f, 0f, 0)]      // stock draws nothing, no visibility floor
        [InlineData(50f, 0.0199f, 0)]
        [InlineData(50f, 1.25f, 62)]
        [InlineData(50f, float.NaN, 0)]
        public void EmissionRate_IsFloored(float baseline, float emission, int expected)
        {
            Assert.Equal(expected, GhostEngineFxPower.EmissionRate(baseline, emission));
        }

        // ---- legacy groups ---------------------------------------------------------------

        [Theory]
        // LV-T45 in vacuum, F3: the real flame read size 2.63 at full and 0.90 at 30 % on a 1.5 asset.
        [InlineData(1f, 1.75f)]
        [InlineData(0.3f, 0.6f)]
        [InlineData(0.1f, 0.5f)]
        [InlineData(0f, 0.5f)]
        [InlineData(0.875f, 1.75f)]
        [InlineData(0.78125f, 1.5625f)]
        [InlineData(float.NaN, 0.5f)]
        public void LegacyRunningPower_IsStocksClamp(float thrustOverMax, float expected)
        {
            Assert.Equal(expected, GhostEngineFxPower.LegacyRunningPower(thrustOverMax), 1e-5f);
        }

        [Theory]
        [InlineData(0f, 0.45f)]
        [InlineData(1f, 1.2f)]
        [InlineData(0.5f, 0.825f)]
        [InlineData(2f, 1.2f)]
        [InlineData(-1f, 0.45f)]
        public void LegacyPowerGroupPower_IsStocksLerp(float throttle, float expected)
        {
            Assert.Equal(expected, GhostEngineFxPower.LegacyPowerGroupPower(throttle), 1e-5f);
        }

        [Theory]
        [InlineData(3f, 4f, 3f, 4f)]
        [InlineData(4f, 3f, 3f, 4f)]   // fx_smokeTrail_aeroSpike authors its ends swapped
        [InlineData(1.5f, 1.5f, 1.5f, 1.5f)]
        public void LegacyRange_IsFxGroupBegin(float a, float b, float min, float max)
        {
            GhostEngineFxPower.LegacyRange(a, b, out float lo, out float hi);
            Assert.Equal(min, lo);
            Assert.Equal(max, hi);
        }

        [Theory]
        [InlineData(true, false, "LegacyRunning")]
        [InlineData(false, true, "LegacyPower")]
        [InlineData(true, true, "LegacyRunning")]
        [InlineData(false, false, "LegacyRunning")]
        public void LegacyRuleKind_PerGroupMembership(bool running, bool power, string expected)
        {
            Assert.Equal(expected, EngineFxBuilder.ResolveLegacyFxRuleKind(running, power).ToString());
        }

        [Fact]
        public void UsesThrust_LegacyRunningAndThrustChannelOnly()
        {
            Assert.True(new GhostEngineFxRule { kind = GhostEngineFxRuleKind.LegacyRunning }.UsesThrust);
            Assert.False(new GhostEngineFxRule { kind = GhostEngineFxRuleKind.LegacyPower }.UsesThrust);
            Assert.True(new GhostEngineFxRule
            {
                kind = GhostEngineFxRuleKind.PrefabParticle,
                channel = GhostEngineFxChannel.Thrust
            }.UsesThrust);
            Assert.False(new GhostEngineFxRule
            {
                kind = GhostEngineFxRuleKind.ModelEmitter,
                channel = GhostEngineFxChannel.Spool
            }.UsesThrust);
        }

        // ---- thrust share ------------------------------------------------------------------

        private static GhostEngineFxPower.ThrustInputs Rocket(float ispHere, float ispVacuum)
        {
            return new GhostEngineFxPower.ThrustInputs
            {
                ispHere = ispHere,
                ispVacuum = ispVacuum,
                throttlingIsp = 1f,
                atmCurve = 1f,
                velCurve = 1f,
                atmCurveIsp = 1f,
                velCurveIsp = 1f,
                flowMultCap = float.MaxValue,
                flowMultCapSharpness = 2f,
                clamp = 1e-5f,
                multFlow = 1f,
                multIsp = 1f
            };
        }

        [Fact]
        public void ThrustFraction_RocketIsItsIspRatio()
        {
            Assert.Equal(250f / 320f, GhostEngineFxPower.ThrustFraction(Rocket(250f, 320f)), 1e-5f);
            Assert.Equal(1f, GhostEngineFxPower.ThrustFraction(Rocket(320f, 320f)), 1e-5f);
        }

        [Fact]
        public void ThrustFraction_JetFollowsItsFlowCurves()
        {
            GhostEngineFxPower.ThrustInputs jet = Rocket(10500f, 10500f);
            jet.atmChangeFlow = true;
            jet.airDensity = 1.0;
            Assert.Equal((float)GhostEngineFxPower.FlowPerDensity, GhostEngineFxPower.ThrustFraction(jet), 1e-5f);
            jet.useAtmCurve = true;
            jet.atmCurve = 0.9f;
            jet.velCurve = 0.5f;
            Assert.Equal(0.45f, GhostEngineFxPower.ThrustFraction(jet), 1e-5f);
            // Without atmChangeFlow the density factor does not apply (stock ModifyFlow).
            jet.atmChangeFlow = false;
            Assert.Equal(0.5f, GhostEngineFxPower.ThrustFraction(jet), 1e-5f);
        }

        [Fact]
        public void ThrustFraction_CapAndClampAndBadInputs()
        {
            GhostEngineFxPower.ThrustInputs m = Rocket(100f, 100f);
            m.velCurve = 4f;
            m.flowMultCap = 2f;
            // 2 + 2 / (2 + 2 / 2)
            Assert.Equal(2f + 2f / 3f, GhostEngineFxPower.ThrustFraction(m), 1e-5f);
            m = Rocket(100f, 100f);
            m.velCurve = 0f;
            Assert.Equal(1e-5f, GhostEngineFxPower.ThrustFraction(m), 1e-8f);
            Assert.Equal(1f, GhostEngineFxPower.ThrustFraction(Rocket(100f, 0f)));
            Assert.Equal(1f, GhostEngineFxPower.ThrustFraction(Rocket(float.NaN, 100f)));
            Assert.Equal(1f, GhostEngineFxPower.ThrustFraction(Rocket(-5f, 100f)));
        }

        [Theory]
        [InlineData("Kerbin", 1000.0, 0.5f, true)]
        [InlineData("Kerbin", 1009.0, 0.5f, true)]
        [InlineData("Kerbin", 1011.0, 0.5f, false)]
        [InlineData("Kerbin", 1000.0, 0.53f, false)]
        [InlineData("Duna", 1000.0, 0.5f, false)]
        [InlineData(null, 1000.0, 0.5f, false)]
        public void CanReuseEngineAir_PlaceAndMach(string body, double altitude, float mach, bool expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.CanReuseEngineAir("Kerbin", 1000.0, 0.5f, body, altitude, mach));
        }

        [Theory]
        [InlineData(1f, 1f, false)]
        [InlineData(1f, 0.99f, false)]
        [InlineData(1f, 0.98f, true)]
        [InlineData(0.5f, 0.6f, true)]
        public void ShouldApplyThrustFraction_ByStep(float applied, float fraction, bool expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.ShouldApplyThrustFraction(applied, fraction));
        }

        [Fact]
        public void HasLitThrustDrivenFx_NeedsPowerAndAThrustRule()
        {
            var info = new EngineGhostInfo { partPersistentId = 1, usesThrustFraction = true };
            var state = new GhostPlaybackState
            {
                engineInfos = new Dictionary<ulong, EngineGhostInfo> { { 256UL, info } }
            };
            Assert.False(GhostPlaybackLogic.HasLitThrustDrivenFx(null));
            Assert.False(GhostPlaybackLogic.HasLitThrustDrivenFx(new GhostPlaybackState()));
            Assert.False(GhostPlaybackLogic.HasLitThrustDrivenFx(state));
            info.currentPower = 0.5f;
            Assert.True(GhostPlaybackLogic.HasLitThrustDrivenFx(state));
            info.usesThrustFraction = false;
            Assert.False(GhostPlaybackLogic.HasLitThrustDrivenFx(state));
        }

        [Fact]
        public void UpdateEngineThrustFractions_UnknownPlace_LeavesTheShare()
        {
            var info = new EngineGhostInfo { partPersistentId = 1, usesThrustFraction = true, currentPower = 1f };
            var state = new GhostPlaybackState
            {
                engineInfos = new Dictionary<ulong, EngineGhostInfo> { { 256UL, info } }
            };
            GhostPlaybackLogic.UpdateEngineThrustFractions(state);
            Assert.Equal(1f, info.thrustFraction);
            Assert.Equal(0, state.engineAirSerial);
        }

        [Theory]
        [InlineData(true, 340f, 340.0, 0.2f, 1f)]
        [InlineData(true, 0f, 340.0, 2f, 0f)]
        [InlineData(true, 900f, 0.0, 2f, 0f)]     // no air
        [InlineData(false, 0f, 340.0, 2.9f, 2.9f)] // unknown speed is not a standstill
        public void ResolveEngineMach_UnknownSpeedKeepsTheLast(
            bool known, float speed, double soundSpeed, float last, float expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.ResolveEngineMach(known, speed, soundSpeed, last), 1e-5f);
        }

        [Fact]
        public void UpdateEngineThrustFractions_AsksForMachBeforeTheCurves()
        {
            Assert.False(GhostPlaybackLogic.NeedsMach(null));
            string source = GhostSource("GhostPlaybackLogic.EngineFxPower.cs");
            int guard = source.IndexOf("if (!air.machKnown && NeedsMach(info.engine)) continue;", StringComparison.Ordinal);
            int compute = source.IndexOf("float fraction = ComputeEngineThrustFraction(", StringComparison.Ordinal);
            Assert.True(guard > 0 && compute > guard);
        }

        [Fact]
        public void ApplyEngineFxRules_NoRulesOrDark_WritesNothing()
        {
            Assert.Equal(0, GhostPlaybackLogic.ApplyEngineFxRules(null));
            Assert.Equal(0, GhostPlaybackLogic.ApplyEngineFxRules(new EngineGhostInfo { currentPower = 1f }));
            Assert.Equal(0, GhostPlaybackLogic.ApplyEngineFxRules(new EngineGhostInfo
            {
                fxRules = { new GhostEngineFxRule { kind = GhostEngineFxRuleKind.LegacyRunning } }
            }));
        }

        // ---- heat clip ----------------------------------------------------------------------

        [Fact]
        public void HeatClip_LevelsReadTheClipAtItsStartMiddleAndEnd()
        {
            var samples = new Color[GhostVisualBuilder.NozzleGlowSampleCount];
            for (int i = 0; i < samples.Length; i++) samples[i] = new Color(i, 0f, 0f, 1f);
            GhostVisualBuilder.SelectHeatClipColors(samples, out Color cold, out Color medium, out Color hot);
            Assert.Equal(0f, cold.r);
            Assert.Equal((GhostVisualBuilder.NozzleGlowSampleCount - 1) / 2f, medium.r);
            Assert.Equal(GhostVisualBuilder.NozzleGlowSampleCount - 1f, hot.r);
        }

        // ---- wiring -------------------------------------------------------------------------

        private static string GhostSource(string name)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return File.ReadAllText(Path.Combine(root, "Source", "Parsek", "Ghost", name)).Replace("\r\n", "\n");
        }

        [Fact]
        public void Wiring_RulesReplaceTheBoostAndTheRatio()
        {
            string builder = GhostSource("EngineFxBuilder.cs");
            Assert.Contains("if (!TryAddModelFxRule(fxInstance, info, engine, groupName, fxCurves))\n" +
                "                                    GhostVisualBuilder.ApplyGhostEngineFxSizeBoost(", builder);
            Assert.Contains("if (!isStockAsset || !TryAddPrefabFxRule(fxInstance, info, engine, groupName, fxCurves))\n" +
                "                            GhostVisualBuilder.ApplyGhostEngineFxSizeBoost(", builder);
            // Stock's three parentings keep the world scale.
            Assert.Contains("fxInstance.transform.SetParent(ghostFxParent, isStockAsset);", builder);
            Assert.Contains("fxInstance.transform.SetParent(ghostFxParent, fxCurves != null);", builder);
            Assert.Contains("fxClone.transform.SetParent(ghostLegacyParent, true);", builder);
            Assert.DoesNotContain("fxClone.transform.localScale = child.localScale;\n\n                        GhostVisualBuilder.Strip", builder);
            Assert.Contains("MarkStockRuledFx(result[i]);", builder);
            string apply = GhostSource("GhostPlaybackLogic.EngineRcsFx.cs");
            Assert.Contains("r.stockRuled", apply);
            Assert.Contains("b.stockRuled", apply);
            Assert.Contains("ApplyEngineFxRules(info);", apply);
        }

        /// <summary>
        /// The FX sweep primes its ghost with the held vessel's ground speed. A playing ghost gets
        /// one the same way: the flight point positioner attaches it to its result and every point
        /// entry hands the result to <c>SetInterpolated</c> (whose copy GhostSurfaceVelocityTests pins).
        /// </summary>
        [Fact]
        public void Wiring_PlaybackSetsTheGroundSpeedTheSweepPrimes()
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Source", "Parsek"));
            string points = File.ReadAllText(Path.Combine(root, "Controllers", "ParsekFlightGhostPositioner.Points.cs"));
            Assert.Matches(@"interpResult = interpResult\.WithSurfaceVelocity\(\s*\w+,\s*GhostSurfaceVelocitySource\.Positions\)", points);
            string positioner = File.ReadAllText(Path.Combine(root, "Controllers", "ParsekFlightGhostPositioner.cs"));
            Assert.Contains("state.SetInterpolated(interpResult);", positioner);
            string driver = File.ReadAllText(Path.Combine(root, "TestCommands", "PartParityFxDriver.cs"));
            Assert.Contains("state.lastInterpolatedSurfaceVelocity = (Vector3)wc.Real.srf_velocity;", driver);
            Assert.Contains("state.lastInterpolatedSurfaceVelocitySource = GhostSurfaceVelocitySource.Positions;", driver);
        }

        [Fact]
        public void Wiring_PrefabSystemsRestartOnThePhysicsStep()
        {
            string builder = GhostSource("EngineFxBuilder.cs");
            Assert.Contains("ps.gameObject.AddComponent<GhostPrefabFxRestart>().system = ps;", builder);
            string restart = GhostSource("GhostEngineFxPower.cs");
            Assert.Contains("private void FixedUpdate()", restart);
            Assert.Contains("if (system != null && system.isEmitting) system.Play();", restart);
        }
    }
}
