using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Where stock puts an engine / RCS effect instance: its scale under the mount, its local
    /// position key, and the direction of a legacy <c>fx_*</c> system.
    /// </summary>
    public class GhostFxStockPlacementTests
    {
        private static Vector3 V(float x) => new Vector3(x, x, x);

        private static void Close(float expected, Vector3 actual)
        {
            Assert.Equal((double)expected, actual.x, 4);
            Assert.Equal((double)expected, actual.y, 4);
            Assert.Equal((double)expected, actual.z, 4);
        }

        private static string Source(string file) => File.ReadAllText(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Source", "Parsek", "Ghost", file));

        // PP-9 `2026-11-23_1050`, real side: RAPIER and RCSBlock.v2 (rescale 1.25) read 0.80 on the
        // first nozzle and 0.64 on every other; the Valiant's single mount 0.80; the Poodle's last
        // mount (scaled 0.5) 2.00; an unscaled part 1.00.
        [Theory]
        [InlineData(1.25f, 1.25f, true, 0.8f)]
        [InlineData(1.25f, 1.25f, false, 0.64f)]
        [InlineData(1f, 0.5f, false, 2f)]
        [InlineData(1f, 1f, true, 1f)]
        [InlineData(1f, 1f, false, 1f)]
        public void NestedScale_FirstMountKeepsTheWorldScale_TheOthersCopyTheNestedFirst(
            float firstMount, float mount, bool isFirst, float expected)
            => Close(expected, GhostVisualBuilder.NestedModelFxScale(V(1f), V(firstMount), V(mount), isFirst, V(1f)));

        [Fact]
        public void NestedScale_TheNodesOwnScaleMultipliesEveryNesting()
        {
            Close(0.4f, GhostVisualBuilder.NestedModelFxScale(V(1f), V(1.25f), V(1.25f), true, V(0.5f)));
            Close(0.16f, GhostVisualBuilder.NestedModelFxScale(V(1f), V(1.25f), V(1.25f), false, V(0.5f)));
            Close(1.6f, GhostVisualBuilder.NestedModelFxScale(V(2f), V(1.25f), V(1.25f), true, V(1f)));
        }

        [Fact]
        public void NestedScale_AMountScaledToNothingKeepsTheValue()
        {
            Close(1f, GhostVisualBuilder.NestedModelFxScale(V(1f), V(0f), V(0f), true, V(1f)));
            Close(1f, GhostVisualBuilder.NestedModelFxScale(V(1f), V(0f), V(1f), false, V(1f)));
        }

        private static FxModelDefinition ScanRcs(string nodeType, params string[] keyValues)
        {
            var group = new ConfigNode("running");
            ConfigNode node = group.AddNode(nodeType);
            node.AddValue("transformName", "RCSjet");
            node.AddValue("modelName", "ReStock/FX/restock-fx-rcs-1");
            for (int i = 0; i + 1 < keyValues.Length; i += 2) node.AddValue(keyValues[i], keyValues[i + 1]);
            var definitions = new List<FxModelDefinition>();
            FloatCurve emission = null, speed = null;
            GhostVisualBuilder.ScanRcsEffectGroupModelNodes(group, definitions, ref emission, ref speed);
            return Assert.Single(definitions);
        }

        // ReStock: RCSBlock_v2 authors localPosition (real pos -0.015), restock-rcs-single-mini-1
        // authors localOffset (real pos 0: stock has no such key).
        [Fact]
        public void RcsPosition_IsStocksLocalPosition_NotLocalOffset()
        {
            FxModelDefinition position = ScanRcs("MODEL_MULTI_PARTICLE", "localPosition", "0, -0.015, 0");
            Assert.Equal((double)-0.015f, position.localOffset.y, 5);
            Assert.True(position.stockNested);
            FxModelDefinition offset = ScanRcs("MODEL_MULTI_PARTICLE", "localOffset", "0, -0.15, 0");
            Assert.Equal(Vector3.zero, offset.localOffset);
            Assert.Equal(new Vector3(1f, 1f, 1f), offset.localScale);
        }

        // SmokeScreen's node is not stock's: it keeps the keys and the scale it had.
        [Fact]
        public void RcsPosition_ASmokeScreenNodeKeepsItsOffset()
        {
            FxModelDefinition offset = ScanRcs("MODEL_MULTI_PARTICLE_PERSIST", "localOffset", "0, 0.1, 0");
            Assert.Equal((double)0.1f, offset.localOffset.y, 5);
            Assert.False(offset.stockNested);
            FxModelDefinition both = ScanRcs("MODEL_MULTI_PARTICLE_PERSIST",
                "localOffset", "0, 0.1, 0", "localPosition", "0, 0.2, 0");
            Assert.Equal((double)0.2f, both.localOffset.y, 5);
        }

        // Every legacy system of PP-7 reads dir (0, 0, -1) under its thrust transform on the real
        // part, the separation motors of Size1p5.Tank.05 included.
        [Fact]
        public void LegacyDirection_IsStocksMinus90AboutX()
        {
            Vector3 dir = EngineFxBuilder.LegacyFxLocalRotation * Vector3.up;
            Assert.Equal((double)0f, dir.x, 5);
            Assert.Equal((double)0f, dir.y, 5);
            Assert.Equal((double)-1f, dir.z, 5);
        }

        // The Mainsail's `fx_exhaustFlame_yellow_mini = 0.041, -1.01, 1.85, ..., running, true`: the
        // prefab child is named as PP-7's `2026-11-23_1732` hierarchy dump reads it.
        [Theory]
        [InlineData("fx_exhaustFlame_yellow_mini(Clone)(Keep Pos)", true)]
        [InlineData("fx_exhaustFlame_yellow_mini(Clone)", false)]
        [InlineData("fx_exhaustFlame_yellow_mini(Clone)(keep pos)", false)]
        [InlineData(null, false)]
        public void LegacyPosition_IsFxOffset_UnlessTheKeyKeepsItsOwn(string name, bool keeps)
        {
            var authored = new Vector3(0.041f, -1.01f, 1.85f);
            var fxOffset = new Vector3(0f, 0f, 2.18f);
            Assert.Equal(keeps ? authored : fxOffset, EngineFxBuilder.LegacyFxLocalPosition(name, authored, fxOffset));
        }

        // The builders instantiate Unity objects, so what they hand the pure rules is pinned by text.
        [Fact]
        public void Wiring_TheBuildersFollowStocksPlacement()
        {
            string engine = Source("EngineFxBuilder.cs");
            Assert.Contains("fxInstance.transform.localScale = GhostVisualBuilder.NestedModelFxScale(", engine);
            Assert.Contains("single || srcFxTransform == firstMount, Vector3.one);", engine);
            // Stock nests under every mount, so the first is taken before the variant filter.
            Assert.True(engine.IndexOf("Transform firstMount =", StringComparison.Ordinal)
                < engine.IndexOf("variant-model-filter-", StringComparison.Ordinal));
            Assert.Contains("? LegacyFxLocalRotation", engine);
            Assert.Contains("LegacyFxLocalPosition(child.name, child.localPosition, legacyFxOffset);", engine);
            Assert.Contains("fxClone.transform.localPosition = legacyLocalPos;", engine);
            string rcs = Source("GhostVisualBuilder.RcsFx.cs");
            Assert.Contains("allFxTransforms[0].lossyScale, srcFxTransform.lossyScale,", rcs);
            Assert.Contains("srcFxTransform == allFxTransforms[0], fxDefinitions[f].localScale)", rcs);
            // Stock's EFFECTS prefabs sit on identity, whatever the ghost's world attitude.
            Assert.Contains("if (isRapierOwnTrail || (isStockAsset && !reanchored))", engine);
        }

        // ReStock sets the cookie on the live lights at start, so the prefab's Light has none and
        // the module's own path is what the ghost reads; a Light the module does not drive keeps
        // the model's.
        [Fact]
        public void Wiring_TheLampCookieIsTheModulesOnItsOwnLights()
        {
            string lights = Source("GhostVisualBuilder.PartInfoBuilders.cs");
            Assert.Contains("ghostLight.cookie = driven && moduleCookie != null ? moduleCookie : srcLight.cookie;", lights);
            Assert.Contains("ghostLight.cookieSize = srcLight.cookieSize;", lights);
            Assert.Contains("Fields.GetValue(\"cookiePath\")", lights);
            Assert.Equal("ModuleRestockEnhancedLight", GhostVisualBuilder.ReStockEnhancedLightModule);
        }
    }
}
