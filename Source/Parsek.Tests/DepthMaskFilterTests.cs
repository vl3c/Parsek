using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the ghost depth-mask renderer filter: ReStock (and community
    /// DepthMask-pattern mods) add depth-only mask meshes managed by a live plugin;
    /// cloned onto ghosts they punch see-through holes, so the clone loop skips them.
    /// Stock configs carry no such modules: the helpers must return empty/false.
    /// </summary>
    public class DepthMaskFilterTests
    {
        private static ConfigNode BuildJetShapeConfig()
        {
            // Shape verbatim from ReStock's restock-engines-jet-depthmasks.cfg
            // post-MM: a MODULE named ModuleRestockDepthMask with maskTransform.
            var part = new ConfigNode("PART");
            var engine = new ConfigNode("MODULE");
            engine.AddValue("name", "ModuleEnginesFX");
            part.AddNode(engine);
            var mask = new ConfigNode("MODULE");
            mask.AddValue("name", "ModuleRestockDepthMask");
            mask.AddValue("maskTransform", "basicjet_mask");
            part.AddNode(mask);
            return part;
        }

        [Fact]
        public void GetDepthMaskTransformNames_ReStockJetShape_FindsMask()
        {
            var names = GhostVisualBuilder.GetDepthMaskTransformNames(BuildJetShapeConfig());
            Assert.Single(names);
            Assert.Contains("basicjet_mask", names);
        }

        [Fact]
        public void GetDepthMaskTransformNames_CommunityModuleDepthMask_Matches()
        {
            var part = new ConfigNode("PART");
            var mask = new ConfigNode("MODULE");
            mask.AddValue("name", "ModuleDepthMask");
            mask.AddValue("maskTransform", "intake_mask");
            part.AddNode(mask);

            var names = GhostVisualBuilder.GetDepthMaskTransformNames(part);
            Assert.Contains("intake_mask", names);
        }

        [Fact]
        public void GetDepthMaskTransformNames_StockConfig_Empty()
        {
            var part = new ConfigNode("PART");
            var engine = new ConfigNode("MODULE");
            engine.AddValue("name", "ModuleEngines");
            part.AddNode(engine);
            var wheel = new ConfigNode("MODULE");
            wheel.AddValue("name", "ModuleWheelDamage");
            wheel.AddValue("damagedTransformName", "wheelBusted");
            part.AddNode(wheel);

            Assert.Empty(GhostVisualBuilder.GetDepthMaskTransformNames(part));
            Assert.Empty(GhostVisualBuilder.GetDepthMaskTransformNames(null));
        }

        [Theory]
        [InlineData("DepthMask", true)]
        [InlineData("Mobile/DepthMask", true)]
        [InlineData("KSP/Alpha/DepthMask", true)]
        [InlineData("KSP/Bumped Specular", false)]
        [InlineData("KSP/Particles/Alpha Blended", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsDepthMaskShaderName_MatchesDepthMaskShadersOnly(string shaderName, bool expected)
        {
            Assert.Equal(expected, GhostVisualBuilder.IsDepthMaskShaderName(shaderName));
        }
    }
}
