using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure rules of case authoring (<see cref="PartParityCaseRules"/>, design 3.1):
    /// exclusions, model urls, source attribution, tags, the default-skin case key and the
    /// spawned vessel name.
    /// </summary>
    public class PartParityCaseRulesTests
    {
        [Theory]
        [InlineData("kerbalEVA", true, "eva-kerbal")]
        [InlineData("kerbalEVAfemaleVintage", true, "eva-kerbal")]
        [InlineData("flag", true, "flag")]
        [InlineData("PotatoRoid", true, "space-object")]
        [InlineData("PotatoComet", true, "space-object")]
        [InlineData("fuelTank", false, "no-prefab")]
        [InlineData("fuelTank", true, null)]
        [InlineData("flagPole", true, null)]
        public void ExclusionReason(string part, bool hasPrefab, string expected)
        {
            Assert.Equal(expected, PartParityCaseRules.ExclusionReason(part, hasPrefab));
        }

        [Fact]
        public void PartDirectory_DropsTheCfgFileAndPartName()
        {
            Assert.Equal("Squad/Parts/Engine/liquidEngineLV-T45",
                PartParityCaseRules.PartDirectoryFromPartUrl("Squad/Parts/Engine/liquidEngineLV-T45/liquidEngineLV-T45/liquidEngine"));
            Assert.Null(PartParityCaseRules.PartDirectoryFromPartUrl("a/b"));
            Assert.Null(PartParityCaseRules.PartDirectoryFromPartUrl(null));
        }

        [Fact]
        public void ModelUrls_FromModelNodesThenALegacyMesh()
        {
            var cfg = new ConfigNode("PART");
            cfg.AddValue("name", "liquidEngine");
            ConfigNode m1 = cfg.AddNode("MODEL");
            m1.AddValue("model", "ReStock/Assets/Engine/restock-engine-lvt45");
            ConfigNode m2 = cfg.AddNode("MODEL");
            m2.AddValue("model", "ReStock/Assets/Engine/restock-engine-lvt45");
            m2.AddValue("model", "Squad/Parts/Engine/shroud");
            cfg.AddValue("mesh", "model.mu");
            List<string> urls = PartParityCaseRules.CollectModelUrls(cfg, "Squad/Parts/Engine/lvt45/lvt45/liquidEngine");
            Assert.Equal(new[]
            {
                "ReStock/Assets/Engine/restock-engine-lvt45",
                "Squad/Parts/Engine/shroud",
                "Squad/Parts/Engine/lvt45/model",
            }, urls.ToArray());
        }

        [Fact]
        public void Source_FollowsTheModelNotTheCfg()
        {
            // ReStock keeps stock part names and cfg urls but swaps the model.
            Assert.Equal("ReStock", PartParityCaseRules.ResolveSource(
                new List<string> { "ReStock/Assets/x" }, "Squad/Parts/x/x/x"));
            Assert.Equal("Squad", PartParityCaseRules.ResolveSource(new List<string>(), "Squad/Parts/x/x/x"));
            Assert.Equal("unknown", PartParityCaseRules.ResolveSource(null, null));
            Assert.Equal("SquadExpansion", PartParityCaseRules.FirstSegment("/SquadExpansion/MakingHistory/x"));
        }

        [Fact]
        public void Tags_HiddenContextAndModules()
        {
            var modules = new List<string>
            {
                "ModuleEngines", "ModuleRestockDeployableMeshHider", "ModuleB9PartSwitch", "ModuleWaterfallFX",
                "MechJebCore", "SomeModThing", "ModuleWheelBase", "FStextureSwitch2", "ModuleRestockDeployableMeshHider",
            };
            var isStock = new List<bool> { true, false, false, false, false, false, true, false, false };
            List<string> tags = PartParityCaseRules.ComputeTags(modules, isStock, hidden: true);
            Assert.Equal(new[]
            {
                "hidden", "context-dependent",
                "restock-module:ModuleRestockDeployableMeshHider", "b9partswitch",
                "fx-module:ModuleWaterfallFX", "unknown-module:SomeModThing",
                "texture-switch:FStextureSwitch2",
            }, tags.ToArray());
        }

        [Fact]
        public void Tags_PlainStockPartHasNone()
        {
            Assert.Empty(PartParityCaseRules.ComputeTags(new List<string> { "ModuleFuelJettison", "ModuleCommand" },
                new List<bool> { true, true }, hidden: false));
        }

        [Fact]
        public void DefaultCaseKey_NamesTheBaseVariantOrDefault()
        {
            var withVariant = new PartParityPartEntry { Name = "fuelTank", Source = "Squad", BaseVariant = "White" };
            PartParityCaseKey k = PartParityCaseRules.DefaultCaseKey(withVariant);
            Assert.Equal("White", k.Skin);
            Assert.Equal(0, k.SkinOrdinal);
            Assert.Equal("stowed", k.State);
            Assert.True(TestCommandCaptureScreenshot.IsValidLabel(TestCommandPartParitySweep.CaseId(k)));

            var plain = new PartParityPartEntry { Name = "liquidEngine2", Source = "Squad" };
            Assert.Equal(TestCommandPartParitySweep.DefaultSkinName, PartParityCaseRules.DefaultCaseKey(plain).Skin);
        }

        [Fact]
        public void VesselName_NeverTheShowcasePrefix()
        {
            string n = PartParityCaseRules.VesselName("fueltank--white--0a1b2c3d");
            Assert.Equal("PartParity fueltank--white--0a1b2c3d", n);
            Assert.False(n.StartsWith("Part Showcase -"));
        }
    }
}
