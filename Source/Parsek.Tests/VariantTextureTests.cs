using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class VariantTextureTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public VariantTextureTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        #region ResolveVariantMaterialCommand (stock PartMaterialCommand)

        [Theory]
        [InlineData("mainTextureURL", "Squad/Parts/x", "_MainTex")]
        [InlineData("backTextureURL", "Squad/Parts/x", "_BackTex")]
        [InlineData("_BumpMap", "Squad/Parts/x_n", "_BumpMap")]
        [InlineData("_SpecMap", "ReStock/Assets/y", "_SpecMap")]
        [InlineData("_Emissive", "Squad/Parts/z", "_Emissive")]
        [InlineData("customURL", "Squad/Parts/x", "customURL")]
        public void ResolveVariantMaterialCommand_NonColourNonFloat_IsTextureOnStockKey(
            string key, string value, string expectedProperty)
        {
            Assert.Equal(VariantPropertyType.Texture, GhostVisualBuilder.ResolveVariantMaterialCommand(
                key, value, out string prop, out _, out _));
            Assert.Equal(expectedProperty, prop);
        }

        [Theory]
        [InlineData("color", "#efe38b", "_Color")]
        [InlineData("backColor", "#ffffffff", "_BackColor")]
        [InlineData("_SpecColor", "#3a392d", "_SpecColor")]
        [InlineData("_EmissiveColor", "#000", "_EmissiveColor")]
        public void ResolveVariantMaterialCommand_HashValue_IsColour(
            string key, string value, string expectedProperty)
        {
            Assert.Equal(VariantPropertyType.Color, GhostVisualBuilder.ResolveVariantMaterialCommand(
                key, value, out string prop, out _, out _));
            Assert.Equal(expectedProperty, prop);
        }

        [Fact]
        public void ResolveVariantMaterialCommand_NumericValue_IsFloat()
        {
            Assert.Equal(VariantPropertyType.Float, GhostVisualBuilder.ResolveVariantMaterialCommand(
                "_Shininess", "0.41", out string prop, out _, out float number));
            Assert.Equal("_Shininess", prop);
            Assert.Equal(0.41, (double)number, 4);
        }

        [Fact]
        public void ResolveVariantMaterialCommand_CommaColour_IsNotAColour()
        {
            // Stock reads no comma colours: the value goes down the float / texture paths.
            Assert.NotEqual(VariantPropertyType.Color, GhostVisualBuilder.ResolveVariantMaterialCommand(
                "_SpecColor", "0.75,0.75,0.5,1", out _, out _, out _));
        }

        [Fact]
        public void ResolveVariantMaterialCommand_BadHash_FallsThroughToTexture()
        {
            Assert.Equal(VariantPropertyType.Texture, GhostVisualBuilder.ResolveVariantMaterialCommand(
                "color", "#zz", out _, out _, out _));
        }

        [Theory]
        [InlineData("shader")]
        [InlineData("materialName")]
        [InlineData("transformName")]
        [InlineData("")]
        [InlineData(null)]
        public void ResolveVariantMaterialCommand_FilterKeys_Skip(string key)
        {
            Assert.Equal(VariantPropertyType.Skip, GhostVisualBuilder.ResolveVariantMaterialCommand(
                key, "x", out _, out _, out _));
        }

        [Fact]
        public void TryParseHtmlHexColor_Forms()
        {
            Assert.True(GhostVisualBuilder.TryParseHtmlHexColor("#efe38b", out UnityEngine.Color c6));
            Assert.Equal(0xef / 255.0, (double)c6.r, 4);
            Assert.Equal(0xe3 / 255.0, (double)c6.g, 4);
            Assert.Equal(0x8b / 255.0, (double)c6.b, 4);
            Assert.Equal(1.0, (double)c6.a, 4);

            Assert.True(GhostVisualBuilder.TryParseHtmlHexColor("#ffffff00", out UnityEngine.Color c8));
            Assert.Equal(0.0, (double)c8.a, 4);

            Assert.True(GhostVisualBuilder.TryParseHtmlHexColor("#f80", out UnityEngine.Color c3));
            Assert.Equal(1.0, (double)c3.r, 4);
            Assert.Equal(0x88 / 255.0, (double)c3.g, 4);
            Assert.Equal(0.0, (double)c3.b, 4);

            Assert.False(GhostVisualBuilder.TryParseHtmlHexColor("#12345", out _));
            Assert.False(GhostVisualBuilder.TryParseHtmlHexColor("efe38b", out _));
            Assert.False(GhostVisualBuilder.TryParseHtmlHexColor("#gggggg", out _));
        }

        #endregion

        #region DoesRendererMatchTextureRule

        [Fact]
        public void DoesRendererMatchTextureRule_NoFilter_MatchesAll()
        {
            var rule = new VariantTextureRule
            {
                materialName = null,
                transformName = null
            };

            Assert.True(GhostVisualBuilder.DoesRendererMatchTextureRule(
                "AnyMaterial", "AnyTransform", rule));
        }

        [Fact]
        public void DoesRendererMatchTextureRule_MaterialNameFilter_Matches()
        {
            var rule = new VariantTextureRule
            {
                materialName = "FuelTankMaterial",
                transformName = null
            };

            Assert.True(GhostVisualBuilder.DoesRendererMatchTextureRule(
                "FuelTankMaterial", "obj", rule));
        }

        [Fact]
        public void DoesRendererMatchTextureRule_MaterialNameFilter_DoesNotMatch()
        {
            var rule = new VariantTextureRule
            {
                materialName = "FuelTankMaterial",
                transformName = null
            };

            Assert.False(GhostVisualBuilder.DoesRendererMatchTextureRule(
                "EngineMaterial", "obj", rule));
        }

        [Fact]
        public void DoesRendererMatchTextureRule_MaterialNameWithInstanceSuffix_MatchesViaStartsWith()
        {
            var rule = new VariantTextureRule
            {
                materialName = "FuelTankMaterial",
                transformName = null
            };

            Assert.True(GhostVisualBuilder.DoesRendererMatchTextureRule(
                "FuelTankMaterial (Instance)", "obj", rule));
        }

        [Fact]
        public void DoesRendererMatchTextureRule_TransformNameFilter_Matches()
        {
            var rule = new VariantTextureRule
            {
                materialName = null,
                transformName = "nozzle"
            };

            Assert.True(GhostVisualBuilder.DoesRendererMatchTextureRule(
                "SomeMat", "nozzle", rule));
        }

        [Fact]
        public void DoesRendererMatchTextureRule_TransformNameFilter_DoesNotMatch()
        {
            var rule = new VariantTextureRule
            {
                materialName = null,
                transformName = "nozzle"
            };

            Assert.False(GhostVisualBuilder.DoesRendererMatchTextureRule(
                "SomeMat", "bell", rule));
        }

        [Fact]
        public void DoesRendererMatchTextureRule_BothFilters_MustMatchBoth()
        {
            var rule = new VariantTextureRule
            {
                materialName = "FuelTank",
                transformName = "nozzle"
            };

            Assert.False(GhostVisualBuilder.DoesRendererMatchTextureRule(
                "FuelTank", "bell", rule));
            Assert.False(GhostVisualBuilder.DoesRendererMatchTextureRule(
                "Engine", "nozzle", rule));
            Assert.True(GhostVisualBuilder.DoesRendererMatchTextureRule(
                "FuelTank", "nozzle", rule));
        }

        [Fact]
        public void DoesRendererMatchTextureRule_NullMaterialName_FailsWhenFilterSet()
        {
            var rule = new VariantTextureRule
            {
                materialName = "Required",
                transformName = null
            };

            Assert.False(GhostVisualBuilder.DoesRendererMatchTextureRule(
                null, "obj", rule));
        }

        [Fact]
        public void DoesRendererMatchTextureRule_NullTransformName_FailsWhenFilterSet()
        {
            var rule = new VariantTextureRule
            {
                materialName = null,
                transformName = "Required"
            };

            Assert.False(GhostVisualBuilder.DoesRendererMatchTextureRule(
                "SomeMat", null, rule));
        }

        #endregion

        #region TryGetSelectedVariantTextureRules — ConfigNode parsing

        [Fact]
        public void TryGetSelectedVariantTextureRules_NullPrefab_ReturnsFalse()
        {
            bool result = GhostVisualBuilder.TryGetSelectedVariantTextureRules(
                null, new ConfigNode("PART"),
                out string variantName, out List<VariantTextureRule> rules);

            Assert.False(result);
            Assert.Null(variantName);
            Assert.Null(rules);
        }

        #endregion

        #region TryFindSelectedVariantNode — ConfigNode parsing

        [Fact]
        public void TryFindSelectedVariantNode_NullPrefab_ReturnsFalse()
        {
            bool result = GhostVisualBuilder.TryFindSelectedVariantNode(
                null, new ConfigNode("PART"),
                out ConfigNode variantNode, out string variantName);

            Assert.False(result);
            Assert.Null(variantNode);
            Assert.Null(variantName);
        }

        #endregion

        #region MatchVariantNode

        private static ConfigNode BuildRockomax16VariantConfig()
        {
            // Mirrors Rockomax16.cfg: base variant is BlackAndWhite (implicit),
            // two explicit VARIANT nodes: Orange and ESA.
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModulePartVariants");
            module.AddValue("baseThemeName", "BlackAndWhite");

            var orange = module.AddNode("VARIANT");
            orange.AddValue("name", "Orange");
            var orangeTex = orange.AddNode("TEXTURE");
            orangeTex.AddValue("mainTextureURL", "Squad/Parts/FuelTank/rockomax_16_O");

            var esa = module.AddNode("VARIANT");
            esa.AddValue("name", "ESA");
            var esaTex = esa.AddNode("TEXTURE");
            esaTex.AddValue("mainTextureURL", "Squad/Parts/FuelTank/rockomax_16_ESA");

            return module;
        }

        [Fact]
        public void MatchVariantNode_SnapshotNameMatchesVariant_ReturnsTrue()
        {
            var config = BuildRockomax16VariantConfig();

            bool result = GhostVisualBuilder.MatchVariantNode(
                config, "Orange", variantFromSnapshot: true,
                out ConfigNode node, out string name, out string path);

            Assert.True(result);
            Assert.Equal("Orange", name);
            Assert.Equal("snapshot", path);
            Assert.NotNull(node);
        }

        [Fact]
        public void MatchVariantNode_SnapshotNameMatchesVariant_CaseInsensitive()
        {
            var config = BuildRockomax16VariantConfig();

            bool result = GhostVisualBuilder.MatchVariantNode(
                config, "orange", variantFromSnapshot: true,
                out ConfigNode node, out string name, out string path);

            Assert.True(result);
            Assert.Equal("Orange", name);
            Assert.Equal("snapshot", path);
        }

        [Fact]
        public void MatchVariantNode_SnapshotNameNotInVariants_ReturnsFalse_BaseImplicit()
        {
            // This is the bug #241 scenario: snapshot says "Basic" (the implicit base
            // variant display name) but no VARIANT node is named "Basic".
            var config = BuildRockomax16VariantConfig();

            bool result = GhostVisualBuilder.MatchVariantNode(
                config, "Basic", variantFromSnapshot: true,
                out ConfigNode node, out string name, out string path);

            Assert.False(result);
            Assert.Null(node);
            Assert.Equal("Basic", name);
            Assert.Equal("base-implicit", path);
        }

        [Fact]
        public void MatchVariantNode_EmptySnapshotName_FallsThrough_ReturnsFirstVariant()
        {
            // When snapshot has no variant name, fall through to first VARIANT node.
            var config = BuildRockomax16VariantConfig();

            bool result = GhostVisualBuilder.MatchVariantNode(
                config, null, variantFromSnapshot: false,
                out ConfigNode node, out string name, out string path);

            Assert.True(result);
            Assert.Equal("Orange", name);
            Assert.Equal("first-fallback", path);
        }

        [Fact]
        public void MatchVariantNode_BaseVariantConfigured_MatchesBaseVariant()
        {
            // When baseVariant is set and no snapshot name, should match the base variant.
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModulePartVariants");
            module.AddValue("baseVariant", "White");

            var orange = module.AddNode("VARIANT");
            orange.AddValue("name", "Orange");
            var white = module.AddNode("VARIANT");
            white.AddValue("name", "White");

            bool result = GhostVisualBuilder.MatchVariantNode(
                module, null, variantFromSnapshot: false,
                out ConfigNode node, out string name, out string path);

            Assert.True(result);
            Assert.Equal("White", name);
            Assert.Equal("base", path);
        }

        [Fact]
        public void MatchVariantNode_RuntimeNameNotInVariants_StillFallsThrough()
        {
            // When the name comes from runtime fields (not snapshot), an unmatched name
            // should still fall through to baseVariant/first-variant — runtime fields are
            // less authoritative than snapshot.
            var config = BuildRockomax16VariantConfig();

            bool result = GhostVisualBuilder.MatchVariantNode(
                config, "SomeRuntimeName", variantFromSnapshot: false,
                out ConfigNode node, out string name, out string path);

            Assert.True(result);
            Assert.Equal("Orange", name);
            Assert.Equal("first-fallback", path);
        }

        [Fact]
        public void MatchVariantNode_NoVariantNodes_ReturnsFalse()
        {
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModulePartVariants");

            bool result = GhostVisualBuilder.MatchVariantNode(
                module, "Orange", variantFromSnapshot: true,
                out ConfigNode node, out string name, out string path);

            Assert.False(result);
        }

        #endregion

        #region VariantTextureRule struct — TEXTURE ConfigNode parsing

        [Fact]
        public void TextureNodeParsing_SingleNodeWithMixedProperties()
        {
            var variantNode = new ConfigNode("VARIANT");
            variantNode.AddValue("name", "Orange");

            var texNode = variantNode.AddNode("TEXTURE");
            texNode.AddValue("materialName", "FuelTankMat");
            texNode.AddValue("shader", "KSP/Bumped Specular");
            texNode.AddValue("mainTextureURL", "Squad/Parts/FuelTank/orange");
            texNode.AddValue("_BumpMap", "Squad/Parts/FuelTank/orange_NRM");
            texNode.AddValue("color", "1.0, 0.5, 0.0, 1.0");
            texNode.AddValue("_Shininess", "0.4");

            Assert.Single(variantNode.GetNodes("TEXTURE"));

            var rules = GhostVisualBuilder.ParseVariantTextureRules(variantNode);
            Assert.Single(rules);
            var rule = rules[0];

            Assert.Equal("FuelTankMat", rule.materialName);
            Assert.Equal("KSP/Bumped Specular", rule.shaderName);
            Assert.Equal(4, rule.properties.Count);

            Assert.Contains(rule.properties, p => p.key == "mainTextureURL" && p.value == "Squad/Parts/FuelTank/orange");
            Assert.Contains(rule.properties, p => p.key == "_BumpMap" && p.value == "Squad/Parts/FuelTank/orange_NRM");
            Assert.Contains(rule.properties, p => p.key == "color" && p.value == "1.0, 0.5, 0.0, 1.0");
            Assert.Contains(rule.properties, p => p.key == "_Shininess" && p.value == "0.4");
        }

        [Fact]
        public void TextureNodeParsing_MultipleNodesWithDifferentMaterialFilters()
        {
            var variantNode = new ConfigNode("VARIANT");
            variantNode.AddValue("name", "Custom");

            var tex1 = variantNode.AddNode("TEXTURE");
            tex1.AddValue("materialName", "BodyMat");
            tex1.AddValue("mainTextureURL", "Squad/Parts/body_tex");

            var tex2 = variantNode.AddNode("TEXTURE");
            tex2.AddValue("materialName", "NozzleMat");
            tex2.AddValue("mainTextureURL", "Squad/Parts/nozzle_tex");
            tex2.AddValue("_Shininess", "0.8");

            var rules = GhostVisualBuilder.ParseVariantTextureRules(variantNode);
            Assert.Equal(2, rules.Count);

            var rule1 = rules[0];
            var rule2 = rules[1];

            Assert.Equal("BodyMat", rule1.materialName);
            Assert.Single(rule1.properties);

            Assert.Equal("NozzleMat", rule2.materialName);
            Assert.Equal(2, rule2.properties.Count);
        }

        [Fact]
        public void TextureNodeParsing_NoTextureNodes_EmptyResult()
        {
            var variantNode = new ConfigNode("VARIANT");
            variantNode.AddValue("name", "Plain");

            var gameObjects = variantNode.AddNode("GAMEOBJECTS");
            gameObjects.AddValue("SomeMesh", "true");

            Assert.Empty(variantNode.GetNodes("TEXTURE"));
            // No TEXTURE node means no rule list at all - the caller's false return.
            Assert.Null(GhostVisualBuilder.ParseVariantTextureRules(variantNode));
        }

        [Fact]
        public void TextureNodeParsing_ShaderOnly_NoProperties()
        {
            var variantNode = new ConfigNode("VARIANT");
            variantNode.AddValue("name", "Shiny");

            var texNode = variantNode.AddNode("TEXTURE");
            texNode.AddValue("shader", "KSP/Specular");

            var rule = GhostVisualBuilder.ParseVariantTextureRules(variantNode)[0];

            Assert.Equal("KSP/Specular", rule.shaderName);
            Assert.Null(rule.materialName);
            Assert.Empty(rule.properties);
        }

        [Fact]
        public void TextureNodeParsing_TransformNameFilter_Extracted()
        {
            var variantNode = new ConfigNode("VARIANT");
            variantNode.AddValue("name", "Nozzle");

            var texNode = variantNode.AddNode("TEXTURE");
            texNode.AddValue("transformName", "nozzle_mesh");
            texNode.AddValue("mainTextureURL", "Squad/nozzle_alt");

            var rule = GhostVisualBuilder.ParseVariantTextureRules(variantNode)[0];
            Assert.Equal("nozzle_mesh", rule.transformName);
            Assert.Single(rule.properties);
        }

        #endregion

        #region ExtractShortTransformName

        [Fact]
        public void ExtractShortTransformName_NullOrEmpty_ReturnsNull()
        {
            Assert.Null(GhostVisualBuilder.ExtractShortTransformName(null));
            Assert.Null(GhostVisualBuilder.ExtractShortTransformName(""));
        }

        [Fact]
        public void ExtractShortTransformName_SimpleNameNoSlash_ReturnsNull()
        {
            // Short names without path separators should return null (already short)
            Assert.Null(GhostVisualBuilder.ExtractShortTransformName("Shroud3x0"));
            Assert.Null(GhostVisualBuilder.ExtractShortTransformName("EngineFixed"));
        }

        [Fact]
        public void ExtractShortTransformName_FullPathWithClone_ExtractsShortName()
        {
            Assert.Equal("Shroud3x0",
                GhostVisualBuilder.ExtractShortTransformName(
                    "SquadExpansion/MakingHistory/Parts/SharedAssets/Shroud3x0(Clone)"));
        }

        [Fact]
        public void ExtractShortTransformName_FullPathWithoutClone_ExtractsShortName()
        {
            Assert.Equal("EnginePlate",
                GhostVisualBuilder.ExtractShortTransformName(
                    "SquadExpansion/MakingHistory/Parts/Coupling/Assets/EnginePlate"));
        }

        [Fact]
        public void ExtractShortTransformName_SingleSegmentModelPath_ExtractsCorrectly()
        {
            Assert.Equal("LqdEnginePoodle_v2",
                GhostVisualBuilder.ExtractShortTransformName(
                    "Squad/Parts/Engine/liquidEnginePoodle_v2/LqdEnginePoodle_v2(Clone)"));
        }

        #endregion

        #region ResolveVariantNameFromSnapshot

        [Fact]
        public void ResolveVariantNameFromSnapshot_NullNode_ReturnsNull()
        {
            Assert.Null(GhostVisualBuilder.ResolveVariantNameFromSnapshot(null));
        }

        [Fact]
        public void ResolveVariantNameFromSnapshot_PartLevelModuleVariantName_Found()
        {
            // KSP stores variant at PART level as moduleVariantName
            var partNode = new ConfigNode("PART");
            partNode.AddValue("moduleVariantName", "SingleBell");
            var snapModule = partNode.AddNode("MODULE");
            snapModule.AddValue("name", "ModulePartVariants");
            snapModule.AddValue("isEnabled", "True");
            // No selectedVariant inside MODULE

            Assert.Equal("SingleBell", GhostVisualBuilder.ResolveVariantNameFromSnapshot(partNode));
        }

        [Fact]
        public void ResolveVariantNameFromSnapshot_ModuleLevelCurrentVariant_PreferredOverPartLevel()
        {
            var partNode = new ConfigNode("PART");
            partNode.AddValue("moduleVariantName", "FromPartLevel");
            var snapModule = partNode.AddNode("MODULE");
            snapModule.AddValue("name", "ModulePartVariants");
            snapModule.AddValue("currentVariant", "FromModuleLevel");

            // MODULE-level should win over PART-level
            Assert.Equal("FromModuleLevel", GhostVisualBuilder.ResolveVariantNameFromSnapshot(partNode));
        }

        [Fact]
        public void ResolveVariantNameFromSnapshot_EmptyModuleAndNoPart_ReturnsNull()
        {
            var partNode = new ConfigNode("PART");
            var snapModule = partNode.AddNode("MODULE");
            snapModule.AddValue("name", "ModulePartVariants");
            snapModule.AddValue("isEnabled", "True");

            Assert.Null(GhostVisualBuilder.ResolveVariantNameFromSnapshot(partNode));
        }

        #endregion
    }
}
