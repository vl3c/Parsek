using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure decisions behind the second round of part ghost parity fixes (sweeps PP-1
    /// 2026-11-21_2306 / PP-2 2026-11-21_2317): each test pins what stock KSP shows on the
    /// live part so the ghost matches it.
    /// </summary>
    [Collection("Sequential")]
    public class GhostPartParityFix2Tests : IDisposable
    {
        public GhostPartParityFix2Tests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        #region Colour changer

        private static ConfigNode ColorChangerNode(string moduleId, string property,
            string key0 = "0 0", string key1 = "1 1")
        {
            var node = new ConfigNode("MODULE");
            node.AddValue("name", "ModuleColorChanger");
            if (moduleId != null) node.AddValue("moduleID", moduleId);
            node.AddValue("shaderProperty", property);
            foreach (string curve in new[] { "redCurve", "greenCurve", "blueCurve", "alphaCurve" })
            {
                var c = new ConfigNode(curve);
                c.AddValue("key", key0);
                c.AddValue("key", key1);
                node.AddNode(c);
            }
            return node;
        }

        [Theory]
        // ReStock heatColor / throttleColor: no ablator, useRate default on, toggleInFlight off.
        [InlineData(false, "_EmissiveColor", true, false, (int)ColorChangerPattern.RestState)]
        [InlineData(false, "_TintColor", true, false, (int)ColorChangerPattern.RestState)]
        // KV pod shieldChar: ablator-driven on _EmissiveColor, useRate off.
        [InlineData(false, "_EmissiveColor", false, true, (int)ColorChangerPattern.AblationChar)]
        // Stock heat shield char.
        [InlineData(false, "_BurnColor", false, true, (int)ColorChangerPattern.AblationChar)]
        // Cabin lights.
        [InlineData(true, "_EmissiveColor", true, false, (int)ColorChangerPattern.CabinLight)]
        // useRate off and nothing drives it: stock never writes.
        [InlineData(false, "_EmissiveColor", false, false, (int)ColorChangerPattern.None)]
        [InlineData(false, "", true, false, (int)ColorChangerPattern.None)]
        public void ColorChanger_Classify_FollowsTheStockWriter(bool toggleInFlight, string property,
            bool useRate, bool ablatorDriven, int expected)
        {
            Assert.Equal((ColorChangerPattern)expected, GhostVisualBuilder.ClassifyColorChangerPattern(
                toggleInFlight, property, useRate, ablatorDriven));
        }

        [Fact]
        public void ColorChanger_AnimState_SnapshotThenConfigThenFalse()
        {
            var snapshot = new ConfigNode("MODULE");
            snapshot.AddValue("animState", "True");
            var config = new ConfigNode("MODULE");
            config.AddValue("animState", "False");
            Assert.True(GhostVisualBuilder.ResolveColorChangerAnimState(snapshot, config));
            Assert.False(GhostVisualBuilder.ResolveColorChangerAnimState(new ConfigNode("MODULE"), config));
            config.SetValue("animState", "True");
            Assert.True(GhostVisualBuilder.ResolveColorChangerAnimState(null, config));
            Assert.False(GhostVisualBuilder.ResolveColorChangerAnimState(null, null));
        }

        [Fact]
        public void ColorChanger_RestState_EngineAtRestIsBlackNotTheBakedGlow()
        {
            GhostVisualBuilder.ResolveColorChangerScalars(ColorChangerPattern.RestState, false,
                0.85f, 0.1f, out float off, out float on);
            Assert.Equal(0f, off);
            Assert.Equal(0f, on);
            Color heat = GhostVisualBuilder.EvaluateColorChangerColor(
                ColorChangerNode("heatColor", "_EmissiveColor", "0 0 1 1", "1 1 1 1"), off);
            Assert.Equal(new Color(0f, 0f, 0f, 0f), heat);

            GhostVisualBuilder.ResolveColorChangerScalars(ColorChangerPattern.RestState, true,
                0.85f, 0.1f, out off, out on);
            Assert.Equal(1f, off);
            Assert.Equal(1f, on);
        }

        [Fact]
        public void ColorChanger_Scalars_CabinAndChar()
        {
            GhostVisualBuilder.ResolveColorChangerScalars(ColorChangerPattern.CabinLight, true,
                0.85f, 0.1f, out float off, out float on);
            Assert.Equal(0f, off);
            Assert.Equal(1f, on);
            GhostVisualBuilder.ResolveColorChangerScalars(ColorChangerPattern.AblationChar, false,
                0.85f, 0.1f, out off, out on);
            Assert.Equal(0.85f, off);
            Assert.Equal(0.1f, on);
        }

        [Fact]
        public void ColorChanger_AlphaOnly_UsesTheFixedColour()
        {
            ConfigNode node = ColorChangerNode("x", "_TintColor", "0 0.25", "1 1");
            node.AddValue("alphaOnly", "True");
            node.AddValue("redColor", "0.5");
            node.AddValue("blueColor", "0.75");
            Color c = GhostVisualBuilder.EvaluateColorChangerColor(node, 0f);
            Assert.Equal(0.5f, c.r);
            Assert.Equal(0f, c.g);
            Assert.Equal(0.75f, c.b);
            Assert.Equal(0.25f, c.a);
        }

        [Fact]
        public void ColorChanger_Filter_SingularKeysOnly_ExcludedWinsOverIncluded()
        {
            ConfigNode node = ColorChangerNode("throttleColor", "_TintColor");
            node.AddValue("includedRenderers", "EngineCore"); // plural: stock never reads it
            var filter = GhostVisualBuilder.ParseColorChangerRendererFilter(node);
            Assert.Empty(filter.includedRenderers);
            Assert.True(GhostVisualBuilder.ColorChangerKeepsRenderer(filter, "Body", null));

            node.AddValue("includedRenderer", "EngineCore");
            node.AddValue("excludedRenderer", "Fairing");
            filter = GhostVisualBuilder.ParseColorChangerRendererFilter(node);
            Assert.Empty(filter.includedRenderers);
            Assert.False(GhostVisualBuilder.ColorChangerKeepsRenderer(filter, "Fairing", null));
            Assert.True(GhostVisualBuilder.ColorChangerKeepsRenderer(filter, "Body", null));
        }

        [Fact]
        public void ColorChanger_Filter_IncludedAndExcludedAreExactNames()
        {
            ConfigNode node = ColorChangerNode("throttleColor", "_TintColor");
            node.AddValue("includedRenderer", "EngineCore");
            var filter = GhostVisualBuilder.ParseColorChangerRendererFilter(node);
            Assert.True(GhostVisualBuilder.ColorChangerKeepsRenderer(filter, "EngineCore", null));
            Assert.False(GhostVisualBuilder.ColorChangerKeepsRenderer(filter, "EngineCoreGlow", null));

            ConfigNode excl = ColorChangerNode("shieldChar", "_BurnColor");
            excl.AddValue("excludedRenderer", "HeatShield1875Fairing");
            filter = GhostVisualBuilder.ParseColorChangerRendererFilter(excl);
            Assert.False(GhostVisualBuilder.ColorChangerKeepsRenderer(filter, "HeatShield1875Fairing", null));
            Assert.True(GhostVisualBuilder.ColorChangerKeepsRenderer(filter, "HeatShield1875", null));
        }

        [Fact]
        public void ColorChanger_Filter_MaterialsList_ContainsAnyListedName()
        {
            ConfigNode node = ColorChangerNode("x", "_EmissiveColor");
            node.AddValue("useMaterialsList", "True");
            node.AddValue("materialsNames", "Window,Lamp");
            var filter = GhostVisualBuilder.ParseColorChangerRendererFilter(node);
            Assert.True(GhostVisualBuilder.ColorChangerKeepsRenderer(filter, "r",
                new List<string> { "Hull", "PodWindowMat" }));
            Assert.False(GhostVisualBuilder.ColorChangerKeepsRenderer(filter, "r",
                new List<string> { "Hull" }));
            Assert.False(GhostVisualBuilder.ColorChangerKeepsRenderer(filter, "r", new List<string>()));
        }

        [Fact]
        public void ColorChanger_FrameOrder_AblatorWritesLast()
        {
            int[] order = GhostVisualBuilder.ResolveColorChangerFrameOrder(new List<ColorChangerPattern>
            {
                ColorChangerPattern.AblationChar, ColorChangerPattern.CabinLight,
            });
            Assert.Equal(new[] { 1, 0 }, order);
        }

        [Fact]
        public void ColorChanger_Sources_KvPodWindowShowsTheCharNotTheLight()
        {
            // Writers in frame order: cabin light (index 0) then the ablator's char (index 1),
            // both on _EmissiveColor, both unfiltered.
            var sources = GhostVisualBuilder.ResolveColorChangerRendererSources(
                new List<string> { "_EmissiveColor", "_EmissiveColor" },
                new List<bool[]> { new[] { true, true }, new[] { true, true } }, 2);
            Assert.Equal(1, sources[0]["_EmissiveColor"]);
            Assert.Equal(1, sources[1]["_EmissiveColor"]);
        }

        [Fact]
        public void ColorChanger_Sources_SharedBlockCarriesEveryPropertyToBothRendererSets()
        {
            // ReStock engine: heatColor (_EmissiveColor, excludes the core = renderer 1) then
            // throttleColor (_TintColor, includes only the core). The part's one block reaches
            // both sets with both properties.
            var sources = GhostVisualBuilder.ResolveColorChangerRendererSources(
                new List<string> { "_EmissiveColor", "_TintColor" },
                new List<bool[]> { new[] { true, false }, new[] { false, true } }, 2);
            Assert.Equal(0, sources[0]["_EmissiveColor"]);
            Assert.Equal(1, sources[0]["_TintColor"]);
            Assert.Equal(0, sources[1]["_EmissiveColor"]);
            Assert.Equal(1, sources[1]["_TintColor"]);
        }

        [Fact]
        public void ColorChanger_Sources_SameProperty_RendererShowsItsLastWriter()
        {
            // Light (all renderers) then char (renderer 0 only): renderer 1 keeps the light.
            var sources = GhostVisualBuilder.ResolveColorChangerRendererSources(
                new List<string> { "_EmissiveColor", "_EmissiveColor" },
                new List<bool[]> { new[] { true, true }, new[] { true, false } }, 2);
            Assert.Equal(1, sources[0]["_EmissiveColor"]);
            Assert.Equal(0, sources[1]["_EmissiveColor"]);
        }

        [Fact]
        public void ColorChanger_Sources_ExcludedRendererGetsNothing()
        {
            // Single heat shield char excluding renderer 1 (the ReStock 1.875 fairing plate).
            var sources = GhostVisualBuilder.ResolveColorChangerRendererSources(
                new List<string> { "_BurnColor" },
                new List<bool[]> { new[] { true, false } }, 2);
            Assert.Equal(0, sources[0]["_BurnColor"]);
            Assert.Empty(sources[1]);
        }

        #endregion

        #region Animation rest state

        private static ConfigNode Module(string name, string anim)
        {
            var node = new ConfigNode("MODULE");
            node.AddValue("name", name);
            if (anim != null) node.AddValue("animationName", anim);
            return node;
        }

        [Fact]
        public void AnimationRest_Candidates_ThrottleAtZero_GenericAtSnapshotAnimTime()
        {
            var config = new ConfigNode("PART");
            config.AddNode(Module("FXModuleAnimateThrottle", "colorAnimation"));
            config.AddNode(Module("ModuleAnimateGeneric", "screenon"));
            config.AddNode(Module("ModuleAnimateGeneric", "deploy"));
            config.AddNode(Module("ModuleEnginesFX", null));

            var snapshot = new ConfigNode("PART");
            var g1 = snapshot.AddNode("MODULE");
            g1.AddValue("name", "ModuleAnimateGeneric");
            g1.AddValue("animTime", "0");
            var g2 = snapshot.AddNode("MODULE");
            g2.AddValue("name", "ModuleAnimateGeneric");
            g2.AddValue("animTime", "1");

            var candidates = GhostVisualBuilder.CollectAnimationRestCandidates(config, snapshot);
            Assert.Equal(3, candidates.Count);
            Assert.Equal("colorAnimation", candidates[0].animationName);
            Assert.Equal(0f, candidates[0].restNormalizedTime);
            Assert.Equal("screenon", candidates[1].animationName);
            Assert.Equal(0f, candidates[1].restNormalizedTime);
            Assert.Equal("deploy", candidates[2].animationName);
            Assert.Equal(1f, candidates[2].restNormalizedTime);
        }

        [Fact]
        public void AnimationRest_Candidates_SameClipListedOnce_NoAnimationNameSkipped()
        {
            var config = new ConfigNode("PART");
            config.AddNode(Module("FXModuleAnimateThrottle", "glow"));
            config.AddNode(Module("ModuleAnimateGeneric", "glow"));
            config.AddNode(Module("ModuleAnimateGeneric", null));
            var candidates = GhostVisualBuilder.CollectAnimationRestCandidates(config, null);
            Assert.Single(candidates);
            Assert.Equal("FXModuleAnimateThrottle", candidates[0].moduleName);
            Assert.Empty(GhostVisualBuilder.CollectAnimationRestCandidates(null, null));
        }

        [Theory]
        [InlineData(null, 0f)]
        [InlineData("", 0f)]
        [InlineData("junk", 0f)]
        [InlineData("0.25", 0.25f)]
        [InlineData("-1", 0f)]
        [InlineData("2", 1f)]
        [InlineData("NaN", 0f)]
        public void AnimationRest_AnimateGenericRestTime_IsTheClampedAnimTime(string raw, float expected)
        {
            Assert.Equal(expected, GhostVisualBuilder.ResolveAnimateGenericRestTime(raw));
        }

        [Fact]
        public void AnimationRest_AnimatedColor_DiffersBetweenTheClipEnds()
        {
            Assert.True(GhostVisualBuilder.IsAnimatedColor(Color.black, new Color(0.2f, 0.8f, 0.9f, 1f)));
            Assert.True(GhostVisualBuilder.IsAnimatedColor(new Color(1f, 1f, 1f, 0f), Color.white));
            Assert.False(GhostVisualBuilder.IsAnimatedColor(Color.clear, Color.clear));
            Assert.False(GhostVisualBuilder.IsAnimatedColor(Color.white, new Color(1f, 1f, 1f, 1.00001f)));
        }

        #endregion

        #region LookAt constraints

        [Fact]
        public void LookAt_ParsesEveryConstraintPair_DropsIncompleteOnes()
        {
            var part = new ConfigNode("PART");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "FXModuleLookAtConstraint");
            var a = module.AddNode("CONSTRAINLOOKFX");
            a.AddValue("targetName", "pistonTargetA");
            a.AddValue("rotatorsName", "pistonA");
            var b = module.AddNode("CONSTRAINLOOKFX");
            b.AddValue("targetName", "pistonTargetB");
            var other = part.AddNode("MODULE");
            other.AddValue("name", "ModuleGimbal");
            var stray = other.AddNode("CONSTRAINLOOKFX");
            stray.AddValue("targetName", "x");
            stray.AddValue("rotatorsName", "y");

            var pairs = GhostVisualBuilder.ParseLookAtConstraintNames(part);
            Assert.Single(pairs);
            Assert.Equal("pistonTargetA", pairs[0].Key);
            Assert.Equal("pistonA", pairs[0].Value);
            Assert.Empty(GhostVisualBuilder.ParseLookAtConstraintNames(null));
        }

        [Fact]
        public void LookAt_ReaimsOnlyWhenTheGimbalPoseChanged()
        {
            Assert.True(GhostPlaybackLogic.ShouldReaimLookAtConstraints(
                new Vector2(float.NaN, float.NaN), Vector2.zero));
            Assert.False(GhostPlaybackLogic.ShouldReaimLookAtConstraints(Vector2.zero, Vector2.zero));
            Assert.True(GhostPlaybackLogic.ShouldReaimLookAtConstraints(Vector2.zero, new Vector2(0.5f, 0f)));
            Assert.True(GhostPlaybackLogic.ShouldReaimLookAtConstraints(new Vector2(0.5f, 0f), new Vector2(0.5f, -1f)));
        }

        #endregion

        #region ReStock linked mesh

        private static ConfigNode ReStockStrutConfig()
        {
            var part = new ConfigNode("PART");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleRestockLinkedMesh");
            module.AddValue("lineObjName", "strut");
            module.AddValue("targetAnchorName", "strut_target_anchor");
            module.AddValue("targetCapName", "strut_target_cap");
            module.AddValue("stretchAxis", "x");
            module.AddValue("stretchTextures", "_MainTex _BumpMap");
            return part;
        }

        private static ConfigNode ConnectedStrutSnapshot()
        {
            var part = new ConfigNode("PART");
            var data = part.AddNode("PARTDATA");
            data.AddValue("pos", "0,1,2");
            data.AddValue("rot", "0,0,0,1");
            data.AddValue("tpersID", "4242");
            return part;
        }

        [Fact]
        public void LinkedMesh_ReStockSubclass_ParsesUnderItsOwnModuleName()
        {
            bool parsed = GhostVisualBuilder.TryParseCompoundPartData(
                ConnectedStrutSnapshot(), ReStockStrutConfig(), true, 7u, "strutConnector",
                "ModuleRestockLinkedMesh", out CompoundPartData data);
            Assert.True(parsed);
            Assert.Equal("strut", data.lineObjName);
            Assert.Equal("strut_target_anchor", data.targetAnchorName);
            Assert.Equal("strut_target_cap", data.targetCapName);
            Assert.Equal(4242u, data.targetPersistentId);
            Assert.Equal(new[] { "_MainTex", "_BumpMap" }, data.stretchTextures);
            Assert.Equal(0, data.stretchAxisIndex);

            // The literal stock name finds nothing on a ReStock part: the pre-fix behaviour.
            Assert.False(GhostVisualBuilder.TryParseCompoundPartData(
                ConnectedStrutSnapshot(), ReStockStrutConfig(), true, 7u, "strutConnector", out _));
        }

        [Fact]
        public void LinkedMesh_StockModule_HasNoTextureStretch()
        {
            var part = new ConfigNode("PART");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "CModuleLinkedMesh");
            module.AddValue("lineObjName", "obj_strut");
            Assert.True(GhostVisualBuilder.TryParseCompoundPartData(
                ConnectedStrutSnapshot(), part, true, 7u, "strutConnector", out CompoundPartData data));
            Assert.Null(data.stretchTextures);
        }

        [Fact]
        public void LinkedMesh_ReStockStretch_AxisAndDefaults()
        {
            var node = new ConfigNode("MODULE");
            GhostVisualBuilder.ParseReStockStretch(node, out string[] textures, out int axis);
            Assert.Equal(new[] { "_MainTex" }, textures);
            Assert.Equal(0, axis);
            node.AddValue("stretchAxis", "y");
            GhostVisualBuilder.ParseReStockStretch(node, out _, out axis);
            Assert.Equal(1, axis);
        }

        [Fact]
        public void LinkedMesh_ReStockStretch_ScaleAndOffsetOnTheStretchedAxis()
        {
            GhostVisualBuilder.ComputeReStockTextureStretch(2.5f, 1.25f, 0, out Vector2 scale, out Vector2 offset);
            Assert.Equal(2f, scale.x, 0.0001f);
            Assert.Equal(1f, scale.y);
            Assert.Equal(-0.5f, offset.x, 0.0001f);
            Assert.Equal(0f, offset.y);

            GhostVisualBuilder.ComputeReStockTextureStretch(0.625f, 1.25f, 1, out scale, out offset);
            Assert.Equal(1f, scale.x);
            Assert.Equal(0.5f, scale.y, 0.0001f);
            Assert.Equal(0.25f, offset.y, 0.0001f);
        }

        #endregion

        #region Light movement transforms

        [Theory]
        [InlineData("X", 30f, 30f, 0f, 0f)]
        [InlineData("x", 30f, 30f, 0f, 0f)]
        [InlineData("X-", 30f, -30f, 0f, 0f)]
        [InlineData("Y", 15f, 0f, 15f, 0f)]
        [InlineData("Z", 90f, 0f, 0f, 90f)]
        [InlineData("Q", 90f, 0f, 0f, 0f)]
        [InlineData("", 90f, 0f, 0f, 0f)]
        public void LightMovement_AngleVector_TranscribesStock(string axis, float angle,
            float ex, float ey, float ez)
        {
            Vector3 v = GhostVisualBuilder.ResolveLightAngleVector(axis, angle);
            Assert.Equal(ex, v.x);
            Assert.Equal(ey, v.y);
            Assert.Equal(ez, v.z);
        }

        [Fact]
        public void LightMovement_Transforms_CombinedFirst_SeparateOnlyWhenItIsMissing()
        {
            var model = new HashSet<string> { "SpotLightBase", "light_12.002", "Lamp" };
            Func<string, string> find = n => n != null && model.Contains(n) ? n : null;

            // ReStock spot light: movementTransformName set, default rotate / pitch names.
            GhostVisualBuilder.ResolveLightMovementTransforms("SpotLightBase",
                "movementRotateTransformName", "movementPitchTransformName", find,
                out string combined, out string rotate, out string pitch);
            Assert.Equal("SpotLightBase", combined);
            Assert.Null(rotate);
            Assert.Null(pitch);

            // Stock groundLight1: no combined transform, separate rotate and pitch.
            GhostVisualBuilder.ResolveLightMovementTransforms("movementTransformName",
                "light_12.002", "Lamp", find, out combined, out rotate, out pitch);
            Assert.Null(combined);
            Assert.Equal("light_12.002", rotate);
            Assert.Equal("Lamp", pitch);

            // Equal rotate and pitch names replace the combined lookup.
            GhostVisualBuilder.ResolveLightMovementTransforms("SpotLightBase", "Lamp", "Lamp", find,
                out combined, out rotate, out pitch);
            Assert.Equal("Lamp", combined);
        }

        [Fact]
        public void LightMovement_AnglesClampToTheModuleLimits()
        {
            Assert.Equal(0f, GhostVisualBuilder.ClampLightAngle(-10f, 0f, 180f));
            Assert.Equal(180f, GhostVisualBuilder.ClampLightAngle(200f, 0f, 180f));
            Assert.Equal(35f, GhostVisualBuilder.ClampLightAngle(35f, -35f, 35f));
        }

        #endregion
    }
}
