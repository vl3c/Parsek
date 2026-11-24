using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>Stock's ModelMultiParticleFX rule on a ghost's RCS puffs.</summary>
    public class GhostRcsFxPowerTests
    {
        // Every stock and ReStock RCS node: emission 0 up to 0.1, then to 1; speed 0.8 to 1.
        private static GhostEngineFxCurves StockCurves()
        {
            var node = new ConfigNode("MODEL_MULTI_PARTICLE");
            node.AddValue("modelName", "Squad/FX/Monoprop_small");
            node.AddValue("emission", "0.0 0.0");
            node.AddValue("emission", "0.1 0.0");
            node.AddValue("emission", "1.0 1.0");
            node.AddValue("speed", "0.0 0.8");
            node.AddValue("speed", "1.0 1.0");
            return GhostEngineFxCurves.ForNode("MODEL_MULTI_PARTICLE", node);
        }

        // PP-9 2026-11-24_0735, restock-rcs-block-triple-angled-1: all three nozzles at 0.569. The
        // real particles flew at 6.78 m/s of 7.0, the ghost's at 4.08 (0.58: the linear rule).
        [Fact]
        public void Speed_IsTheNodesCurve_NotThePower()
        {
            GhostEngineFxCurves curves = StockCurves();
            Assert.Equal(0.9206, curves.speed.Value(0.569f), 3);
            Assert.Equal(0.8, curves.speed.Value(0f), 5);
            Assert.Equal(1.0, curves.speed.Value(1f), 5);
            Assert.Equal(1.0, curves.energy.Value(0.569f), 5);
        }

        // Scan: a stock node's curves reach its FX definition, a SmokeScreen node has none.
        [Theory]
        [InlineData("MODEL_MULTI_PARTICLE", true)]
        [InlineData("MODEL_MULTI_PARTICLE_PERSIST", false)]
        public void Scan_CarriesTheCurvesOfAStockNode(string nodeType, bool expected)
        {
            string source = GhostSource("GhostVisualBuilder.RcsFx.cs");
            Assert.Contains("curves = stockNodes\n" +
                "                            ? GhostEngineFxCurves.ForNode(\"MODEL_MULTI_PARTICLE\", mmpNodes[mp])\n" +
                "                            : null", source);
            Assert.Equal(expected, GhostEngineFxCurves.ForNode(nodeType, new ConfigNode(nodeType)) != null);
        }

        // One nozzle: the recorded power is its own, floored as ModuleRCS.FixedUpdate floors it.
        [Theory]
        [InlineData(0.5f, 1, 1, 0.5f)]
        [InlineData(0.02f, 1, 1, 0.1f)]
        [InlineData(1f, 1, 1, 1f)]
        [InlineData(0f, 1, 1, 0f)]
        [InlineData(float.NaN, 1, 1, 0f)]
        // RCSBlock.v2 (stock): 9 transforms, 4 in the angled variant; 2 fired at 0.577 (recorded 0.128).
        [InlineData(0.128f, 9, 4, 0.288f)]
        // ReStock's: 18 transforms, 4 active; 2 fired at 0.711 (0.079).
        [InlineData(0.079f, 18, 4, 0.3555f)]
        [InlineData(0.9f, 18, 4, 1f)]
        // Nothing to take out: a one-variant block, or counts that make no sense.
        [InlineData(0.569f, 3, 3, 0.569f)]
        [InlineData(0.5f, 3, 4, 0.5f)]
        [InlineData(0.5f, 3, 0, 0.5f)]
        public void NozzlePower_LeavesTheOtherVariantsNozzlesOut(float recorded, int nozzles, int active, float expected)
            => Assert.Equal((double)expected, GhostEngineFxPower.RcsNozzlePower(recorded, nozzles, active), 4);

        [Fact]
        public void Emission_OfOneNozzleIsStocksCurve()
        {
            GhostEngineFxCurves curves = StockCurves();
            foreach (float power in new[] { 0.1f, 0.3f, 0.569f, 1f })
                Assert.Equal(curves.emission.Value(power),
                    GhostEngineFxPower.RcsEmission(curves.emission.Value(power), power, 1));
            Assert.Equal(0f, GhostEngineFxPower.RcsEmission(curves.emission.Value(0.1f), 0.1f, 1));
        }

        // The total of k nozzles at full is the total the ghost's nozzles draw at the mean.
        [Theory]
        [InlineData(1, 4)]
        [InlineData(2, 4)]
        [InlineData(3, 4)]
        [InlineData(4, 4)]
        [InlineData(1, 8)]
        public void Emission_OfABlockKeepsTheTotalOfNozzlesAtFull(int firing, int active)
        {
            GhostEngineFxCurves curves = StockCurves();
            float mean = firing / (float)active;
            float share = GhostEngineFxPower.RcsEmission(curves.emission.Value(mean), mean, active);
            Assert.InRange(share * active, firing - 0.001, firing * 1.1);
        }

        // Where every nozzle fires at the mean, the share is stock's own value or close above it.
        [Theory]
        [InlineData(0.569f)]
        [InlineData(0.7f)]
        [InlineData(0.9f)]
        public void Emission_OfABlockIsStocksWhereAllFireHard(float power)
        {
            GhostEngineFxCurves curves = StockCurves();
            float stock = curves.emission.Value(power);
            Assert.InRange(GhostEngineFxPower.RcsEmission(stock, power, 3), stock, stock * 1.08f);
        }

        [Fact]
        public void Apply_AnIdleOrRuleLessModuleWritesNothing()
        {
            Assert.Equal(0, GhostPlaybackLogic.ApplyRcsFxRules(null));
            Assert.Equal(0, GhostPlaybackLogic.ApplyRcsFxRules(new RcsGhostInfo { currentPower = 1f }));
            var idle = new RcsGhostInfo();
            idle.fxRules.Add(new GhostEngineFxRule());
            Assert.Equal(0, GhostPlaybackLogic.ApplyRcsFxRules(idle));
        }

        [Fact]
        public void Wiring_TheRuleReplacesTheRatio()
        {
            string rcs = GhostSource("GhostVisualBuilder.RcsFx.cs");
            Assert.Contains("AddRcsFxRule(info, fxInstance, fxDefinitions[f].curves);", rcs);
            Assert.Contains("CountRcsNozzles(prefab, rcs, modelRoot, selectedVariantGameObjects, info);", rcs);
            int capture = rcs.IndexOf("CaptureFxMagnitudeBaselines(", StringComparison.Ordinal);
            int mark = rcs.IndexOf("MarkStockRuledRcsFx(result[i], partName);", StringComparison.Ordinal);
            Assert.True(capture > 0 && mark > capture);
            string apply = GhostSource("GhostPlaybackLogic.EngineRcsFx.cs");
            int ratio = apply.IndexOf("\"rcs\", info.partPersistentId, info.moduleIndex, power);", StringComparison.Ordinal);
            int rule = apply.IndexOf("ApplyRcsFxRules(info);", StringComparison.Ordinal);
            Assert.True(ratio > 0 && rule > ratio);
        }

        private static string GhostSource(string file)
            => File.ReadAllText(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "Ghost", file))).Replace("\r\n", "\n");
    }
}
