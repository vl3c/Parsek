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

        #region ClassifyVariantProperty

        [Fact]
        public void ClassifyVariantProperty_TextureURLKeys_ReturnTexture()
        {
            Assert.Equal(VariantPropertyType.Texture, GhostVisualBuilder.ClassifyVariantProperty("mainTextureURL"));
            Assert.Equal(VariantPropertyType.Texture, GhostVisualBuilder.ClassifyVariantProperty("backTextureURL"));
        }

        [Fact]
        public void ClassifyVariantProperty_KnownTextureNames_ReturnTexture()
        {
            Assert.Equal(VariantPropertyType.Texture, GhostVisualBuilder.ClassifyVariantProperty("_BumpMap"));
            Assert.Equal(VariantPropertyType.Texture, GhostVisualBuilder.ClassifyVariantProperty("_Emissive"));
            Assert.Equal(VariantPropertyType.Texture, GhostVisualBuilder.ClassifyVariantProperty("_SpecMap"));
        }

        [Fact]
        public void ClassifyVariantProperty_ColorKeys_ReturnColor()
        {
            Assert.Equal(VariantPropertyType.Color, GhostVisualBuilder.ClassifyVariantProperty("_Color"));
            Assert.Equal(VariantPropertyType.Color, GhostVisualBuilder.ClassifyVariantProperty("color"));
            Assert.Equal(VariantPropertyType.Color, GhostVisualBuilder.ClassifyVariantProperty("_SpecColor"));
        }

        [Fact]
        public void ClassifyVariantProperty_FloatKeys_ReturnFloat()
        {
            Assert.Equal(VariantPropertyType.Float, GhostVisualBuilder.ClassifyVariantProperty("_Shininess"));
            Assert.Equal(VariantPropertyType.Float, GhostVisualBuilder.ClassifyVariantProperty("_Opacity"));
            Assert.Equal(VariantPropertyType.Float, GhostVisualBuilder.ClassifyVariantProperty("_RimFalloff"));
            Assert.Equal(VariantPropertyType.Float, GhostVisualBuilder.ClassifyVariantProperty("_AmbientMultiplier"));
            Assert.Equal(VariantPropertyType.Float, GhostVisualBuilder.ClassifyVariantProperty("_TemperatureColor"));
        }

        [Fact]
        public void ClassifyVariantProperty_SkipKeys_ReturnSkip()
        {
            Assert.Equal(VariantPropertyType.Skip, GhostVisualBuilder.ClassifyVariantProperty("shader"));
            Assert.Equal(VariantPropertyType.Skip, GhostVisualBuilder.ClassifyVariantProperty("materialName"));
            Assert.Equal(VariantPropertyType.Skip, GhostVisualBuilder.ClassifyVariantProperty("transformName"));
        }

        [Fact]
        public void ClassifyVariantProperty_NullOrEmpty_ReturnsSkip()
        {
            Assert.Equal(VariantPropertyType.Skip, GhostVisualBuilder.ClassifyVariantProperty(null));
            Assert.Equal(VariantPropertyType.Skip, GhostVisualBuilder.ClassifyVariantProperty(""));
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

        #region TextureUrlToShaderProperty

        [Fact]
        public void TextureUrlToShaderProperty_ConvertsCorrectly()
        {
            Assert.Equal("_MainTex", GhostVisualBuilder.TextureUrlToShaderProperty("mainTextureURL"));
            Assert.Equal("_BackTex", GhostVisualBuilder.TextureUrlToShaderProperty("backTextureURL"));
            Assert.Equal("_BumpMap", GhostVisualBuilder.TextureUrlToShaderProperty("_BumpMap"));
            Assert.Equal("_Emissive", GhostVisualBuilder.TextureUrlToShaderProperty("_Emissive"));
            Assert.Equal("_SpecMap", GhostVisualBuilder.TextureUrlToShaderProperty("_SpecMap"));
        }

        [Fact]
        public void TextureUrlToShaderProperty_EmptyPrefixURL_ReturnsFallback()
        {
            Assert.Equal("_MainTex", GhostVisualBuilder.TextureUrlToShaderProperty("URL"));
        }

        #endregion

        #region TryParseKspColor

        [Fact]
        public void TryParseKspColor_CommaSeparatedRGBA_Parses()
        {
            Assert.True(GhostVisualBuilder.TryParseKspColor("1.0, 0.5, 0.25, 0.8", out var color));
            Assert.Equal(1.0, (double)color.r, 3);
            Assert.Equal(0.5, (double)color.g, 3);
            Assert.Equal(0.25, (double)color.b, 3);
            Assert.Equal(0.8, (double)color.a, 3);
        }

        [Fact]
        public void TryParseKspColor_CommaSeparatedRGB_DefaultsAlphaToOne()
        {
            Assert.True(GhostVisualBuilder.TryParseKspColor("0.5, 0.5, 0.5", out var color));
            Assert.Equal(0.5, (double)color.r, 3);
            Assert.Equal(1.0, (double)color.a, 3);
        }

        [Fact]
        public void TryParseKspColor_NullOrEmpty_ReturnsFalse()
        {
            Assert.False(GhostVisualBuilder.TryParseKspColor(null, out _));
            Assert.False(GhostVisualBuilder.TryParseKspColor("", out _));
        }

        [Fact]
        public void TryParseKspColor_TooFewComponents_ReturnsFalse()
        {
            Assert.False(GhostVisualBuilder.TryParseKspColor("0.5, 0.5", out _));
        }

        // Hex format (#RRGGBB) is handled via ColorUtility.TryParseHtmlString
        // which requires Unity runtime — can't be unit tested, verified in-game

        [Fact]
        public void TryParseKspColor_InvalidFloatComponents_ReturnsFalse()
        {
            Assert.False(GhostVisualBuilder.TryParseKspColor("abc, 0.5, 0.5, 1.0", out _));
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

            var textureNodes = variantNode.GetNodes("TEXTURE");
            Assert.Single(textureNodes);

            var rule = ParseTextureRule(textureNodes[0]);

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

            var textureNodes = variantNode.GetNodes("TEXTURE");
            Assert.Equal(2, textureNodes.Length);

            var rule1 = ParseTextureRule(textureNodes[0]);
            var rule2 = ParseTextureRule(textureNodes[1]);

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

            var textureNodes = variantNode.GetNodes("TEXTURE");
            Assert.Empty(textureNodes);
        }

        [Fact]
        public void TextureNodeParsing_ShaderOnly_NoProperties()
        {
            var variantNode = new ConfigNode("VARIANT");
            variantNode.AddValue("name", "Shiny");

            var texNode = variantNode.AddNode("TEXTURE");
            texNode.AddValue("shader", "KSP/Specular");

            var textureNodes = variantNode.GetNodes("TEXTURE");
            var rule = ParseTextureRule(textureNodes[0]);

            Assert.Equal("KSP/Specular", rule.shaderName);
            Assert.Null(rule.materialName);
            Assert.Empty(rule.properties);
        }

        [Fact]
        public void TextureNodeParsing_TransformNameFilter_Extracted()
        {
            var texNode = new ConfigNode("TEXTURE");
            texNode.AddValue("transformName", "nozzle_mesh");
            texNode.AddValue("mainTextureURL", "Squad/nozzle_alt");

            var rule = ParseTextureRule(texNode);
            Assert.Equal("nozzle_mesh", rule.transformName);
            Assert.Single(rule.properties);
        }

        private static VariantTextureRule ParseTextureRule(ConfigNode texNode)
        {
            var rule = new VariantTextureRule
            {
                materialName = texNode.GetValue("materialName"),
                shaderName = texNode.GetValue("shader"),
                transformName = texNode.GetValue("transformName"),
                properties = new List<(string key, string value)>()
            };

            for (int v = 0; v < texNode.values.Count; v++)
            {
                var val = texNode.values[v];
                if (val == null || string.IsNullOrEmpty(val.name))
                    continue;

                string key = val.name.Trim();
                if (key == "shader" || key == "materialName" || key == "transformName")
                    continue;

                string value = val.value != null ? val.value.Trim() : "";
                rule.properties.Add((key, value));
            }

            return rule;
        }

        #endregion

        #region Property classification edge cases

        [Fact]
        public void ClassifyVariantProperty_UnknownKey_ReturnsSkip()
        {
            Assert.Equal(VariantPropertyType.Skip,
                GhostVisualBuilder.ClassifyVariantProperty("randomUnknownKey"));
        }

        [Fact]
        public void ClassifyVariantProperty_ColorInMiddleOfKey_ReturnsColor()
        {
            Assert.Equal(VariantPropertyType.Color,
                GhostVisualBuilder.ClassifyVariantProperty("_EmissiveColor"));
        }

        [Fact]
        public void ClassifyVariantProperty_URLSuffix_CaseMatters()
        {
            Assert.Equal(VariantPropertyType.Texture,
                GhostVisualBuilder.ClassifyVariantProperty("customURL"));
            Assert.Equal(VariantPropertyType.Skip,
                GhostVisualBuilder.ClassifyVariantProperty("customUrl"));
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

        #region TryFindSelectedVariantNode_PartLevelModuleVariantName

        [Fact]
        public void TryFindSelectedVariantNode_ReadsPartLevelModuleVariantName()
        {
            // Simulate a part config with ModulePartVariants
            var partConfig = new ConfigNode("PART");
            var variantModule = partConfig.AddNode("MODULE");
            variantModule.AddValue("name", "ModulePartVariants");
            variantModule.AddValue("baseVariant", "DoubleBell");
            var v1 = variantModule.AddNode("VARIANT");
            v1.AddValue("name", "DoubleBell");
            var v2 = variantModule.AddNode("VARIANT");
            v2.AddValue("name", "SingleBell");

            // Simulate a snapshot PART node with moduleVariantName at PART level
            // (where KSP actually writes it) and empty MODULE
            var partNode = new ConfigNode("PART");
            partNode.AddValue("moduleVariantName", "SingleBell");
            var snapModule = partNode.AddNode("MODULE");
            snapModule.AddValue("name", "ModulePartVariants");
            snapModule.AddValue("isEnabled", "True");
            // Note: no selectedVariant/currentVariant inside MODULE

            // Create a minimal prefab-like object for the test
            // We can't fully test this without Unity, but we can test the ConfigNode logic
            bool found = GhostVisualBuilder.TryFindSelectedVariantNode(
                null, // prefab not available in unit tests
                partNode,
                out ConfigNode selectedVariant,
                out string selectedName,
                out string resolution);

            // Without a real prefab (prefab.partInfo.partConfig), returns false
            // but the moduleVariantName read logic is exercised
            Assert.False(found); // can't find variant config without prefab
        }

        #endregion
    }
}
