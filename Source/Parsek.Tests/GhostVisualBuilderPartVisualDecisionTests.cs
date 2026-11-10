using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests for the decision units lifted out of GhostVisualBuilder.AddPartVisuals: the
    /// renderer filters, the optional-visual gates, the heat animation precedence and the per-part
    /// diagnostic lines (text, rate-limit key and the conditions that emit them).
    /// </summary>
    [Collection("Sequential")]
    public class GhostVisualBuilderPartVisualDecisionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostVisualBuilderPartVisualDecisionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // --- HasNoRendererComponents ---

        [Fact]
        public void HasNoRendererComponents_NullOrEmptyArrays_True()
        {
            Assert.True(GhostVisualBuilder.HasNoRendererComponents(null, null));
            Assert.True(GhostVisualBuilder.HasNoRendererComponents(
                new MeshRenderer[0], new SkinnedMeshRenderer[0]));
            Assert.True(GhostVisualBuilder.HasNoRendererComponents(null, new SkinnedMeshRenderer[0]));
            Assert.True(GhostVisualBuilder.HasNoRendererComponents(new MeshRenderer[0], null));
        }

        [Fact]
        public void HasNoRendererComponents_EitherArrayNonEmpty_False()
        {
            // The length is what counts: a slot holding no component still keeps the part.
            Assert.False(GhostVisualBuilder.HasNoRendererComponents(new MeshRenderer[1], null));
            Assert.False(GhostVisualBuilder.HasNoRendererComponents(null, new SkinnedMeshRenderer[1]));
            Assert.False(GhostVisualBuilder.HasNoRendererComponents(
                new MeshRenderer[0], new SkinnedMeshRenderer[2]));
            Assert.False(GhostVisualBuilder.HasNoRendererComponents(
                new MeshRenderer[3], new SkinnedMeshRenderer[0]));
        }

        // --- ShouldFilterInactiveVariantRenderers ---

        [Theory]
        [InlineData(false, 0, 0, false)]
        [InlineData(false, 3, 2, false)]
        [InlineData(true, 0, 0, false)]
        [InlineData(true, 1, 0, true)]
        [InlineData(true, 0, 1, true)]
        [InlineData(true, 4, 5, true)]
        public void ShouldFilterInactiveVariantRenderers_Truth(
            bool hasPartVariants, int activeMR, int activeSMR, bool expected)
        {
            Assert.Equal(expected, GhostVisualBuilder.ShouldFilterInactiveVariantRenderers(
                hasPartVariants, activeMR, activeSMR));
        }

        // --- ResolveDamagedRendererFilterNames ---

        [Fact]
        public void ResolveDamagedRendererFilterNames_NoBustedNames_Null()
        {
            var empty = new HashSet<string>();
            Assert.Null(GhostVisualBuilder.ResolveDamagedRendererFilterNames(empty, false));
            Assert.Null(GhostVisualBuilder.ResolveDamagedRendererFilterNames(empty, true));
        }

        [Fact]
        public void ResolveDamagedRendererFilterNames_Swappable_Null()
        {
            var damaged = new HashSet<string> { "wheel_busted" };
            Assert.Null(GhostVisualBuilder.ResolveDamagedRendererFilterNames(damaged, true));
        }

        [Fact]
        public void ResolveDamagedRendererFilterNames_NotSwappable_ReturnsTheSameSet()
        {
            var damaged = new HashSet<string> { "wheel_busted" };
            Assert.Same(damaged, GhostVisualBuilder.ResolveDamagedRendererFilterNames(damaged, false));
        }

        // --- ShouldResolveWheelDamageInfo ---

        [Theory]
        [InlineData(0, 0, false)]
        [InlineData(1, 0, true)]
        [InlineData(0, 1, true)]
        [InlineData(2, 2, true)]
        public void ShouldResolveWheelDamageInfo_Truth(int damagedCount, int undamagedCount, bool expected)
        {
            var damaged = new HashSet<string>();
            for (int i = 0; i < damagedCount; i++) damaged.Add("busted" + i);
            var undamaged = new HashSet<string>();
            for (int i = 0; i < undamagedCount; i++) undamaged.Add("intact" + i);
            Assert.Equal(expected, GhostVisualBuilder.ShouldResolveWheelDamageInfo(damaged, undamaged));
        }

        // --- ShouldHideJettisonTransformsAtBuild ---

        private static ConfigNode PartNodeWithJettison(string isJettisoned)
        {
            var part = new ConfigNode("PART");
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleJettison");
            if (isJettisoned != null)
                module.AddValue("isJettisoned", isJettisoned);
            part.AddNode(module);
            return part;
        }

        private static JettisonGhostInfo JettisonInfo()
        {
            return new JettisonGhostInfo { partPersistentId = 100000, jettisonTransforms = new List<Transform>() };
        }

        [Fact]
        public void ShouldHideJettisonTransformsAtBuild_JettisonedSnapshot_True()
        {
            Assert.True(GhostVisualBuilder.ShouldHideJettisonTransformsAtBuild(
                JettisonInfo(), PartNodeWithJettison("True")));
            Assert.True(GhostVisualBuilder.ShouldHideJettisonTransformsAtBuild(
                JettisonInfo(), PartNodeWithJettison("true")));
        }

        [Fact]
        public void ShouldHideJettisonTransformsAtBuild_IntactOrUnknown_False()
        {
            Assert.False(GhostVisualBuilder.ShouldHideJettisonTransformsAtBuild(
                JettisonInfo(), PartNodeWithJettison("False")));
            Assert.False(GhostVisualBuilder.ShouldHideJettisonTransformsAtBuild(
                JettisonInfo(), PartNodeWithJettison(null)));
            Assert.False(GhostVisualBuilder.ShouldHideJettisonTransformsAtBuild(
                JettisonInfo(), new ConfigNode("PART")));
        }

        [Fact]
        public void ShouldHideJettisonTransformsAtBuild_NoInfoOrNoNode_False()
        {
            Assert.False(GhostVisualBuilder.ShouldHideJettisonTransformsAtBuild(
                null, PartNodeWithJettison("True")));
            // A null part node is never read: the guard short-circuits before IsJettisonedInSnapshot.
            Assert.False(GhostVisualBuilder.ShouldHideJettisonTransformsAtBuild(JettisonInfo(), null));
        }

        // --- heat animation selection ---

        [Theory]
        [InlineData(false, false, false, false)]
        [InlineData(true, false, false, true)]
        [InlineData(false, true, false, true)]
        [InlineData(false, false, true, true)]
        [InlineData(true, true, true, true)]
        public void HasAnyHeatAnimation_Truth(bool heat, bool throttle, bool rcs, bool expected)
        {
            Assert.Equal(expected, GhostVisualBuilder.HasAnyHeatAnimation(heat, throttle, rcs));
        }

        [Fact]
        public void SelectHeatAnimationName_HeatThenThrottleThenRcs()
        {
            Assert.Equal("heatAnim", GhostVisualBuilder.SelectHeatAnimationName(
                true, "heatAnim", true, "throttleAnim", "rcsAnim"));
            Assert.Equal("throttleAnim", GhostVisualBuilder.SelectHeatAnimationName(
                false, "heatAnim", true, "throttleAnim", "rcsAnim"));
            Assert.Equal("rcsAnim", GhostVisualBuilder.SelectHeatAnimationName(
                false, "heatAnim", false, "throttleAnim", "rcsAnim"));
            // No module found: the RCS slot (null when it was never probed) is the answer.
            Assert.Null(GhostVisualBuilder.SelectHeatAnimationName(false, null, false, null, null));
        }

        [Fact]
        public void SelectHeatAnimationSourceModule_SamePrecedence()
        {
            Assert.Equal("ModuleAnimateHeat", GhostVisualBuilder.SelectHeatAnimationSourceModule(true, true));
            Assert.Equal("ModuleAnimateHeat", GhostVisualBuilder.SelectHeatAnimationSourceModule(true, false));
            Assert.Equal("FXModuleAnimateThrottle", GhostVisualBuilder.SelectHeatAnimationSourceModule(false, true));
            Assert.Equal("FXModuleAnimateRCS", GhostVisualBuilder.SelectHeatAnimationSourceModule(false, false));
        }

        // --- AnimatedModulesNeedFullModelHierarchy ---

        [Fact]
        public void AnimatedModulesNeedFullModelHierarchy_NoneSet_False()
        {
            Assert.False(GhostVisualBuilder.AnimatedModulesNeedFullModelHierarchy(
                false, false, false, false, false, false, false, false));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        public void AnimatedModulesNeedFullModelHierarchy_AnyOneSet_True(int which)
        {
            var f = new bool[8];
            f[which] = true;
            Assert.True(GhostVisualBuilder.AnimatedModulesNeedFullModelHierarchy(
                f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7]));
        }

        // --- LogPartVariantResolution ---

        private static Dictionary<string, bool> Rules(int count)
        {
            var d = new Dictionary<string, bool>();
            for (int i = 0; i < count; i++) d["go" + i] = true;
            return d;
        }

        [Fact]
        public void LogPartVariantResolution_NoVariants_LogsNothing()
        {
            GhostVisualBuilder.LogPartVariantResolution(
                false, true, "engineX", "Sel", "base-implicit", Rules(2), false, 0, 0);
            Assert.Empty(logLines);
        }

        [Fact]
        public void LogPartVariantResolution_RulesFound_LogsSelection()
        {
            GhostVisualBuilder.LogPartVariantResolution(
                true, true, "engineX", "DoubleBell", "snapshot-module", Rules(3), true, 2, 0);
            Assert.Equal(new[]
            {
                "[Parsek][VERBOSE][GhostVisual]   Variant selection: 'DoubleBell' via snapshot-module with 3 GAMEOBJECT rules",
            }, logLines);
        }

        [Fact]
        public void LogPartVariantResolution_NoRulesNoActive_LogsFallback()
        {
            GhostVisualBuilder.LogPartVariantResolution(
                true, false, "engineX", null, null, null, false, 0, 0);
            Assert.Equal(new[]
            {
                "[Parsek][VERBOSE][GhostVisual]   Variant fallback: no active variant renderers and no GAMEOBJECT rules "
                    + "— including all renderers (active MR=0, active SMR=0)",
            }, logLines);
        }

        [Fact]
        public void LogPartVariantResolution_NoRulesButFiltering_LogsNothing()
        {
            GhostVisualBuilder.LogPartVariantResolution(
                true, false, "engineX", null, null, null, true, 1, 0);
            Assert.Empty(logLines);
        }

        [Fact]
        public void LogPartVariantResolution_BaseImplicitWithRules_LogsSelectionThenBase()
        {
            GhostVisualBuilder.LogPartVariantResolution(
                true, true, "engineX", "Base", "base-implicit", Rules(0), true, 1, 1);
            Assert.Equal(new[]
            {
                "[Parsek][VERBOSE][GhostVisual]   Variant selection: 'Base' via base-implicit with 0 GAMEOBJECT rules",
                "[Parsek][VERBOSE][GhostVisual]   Variant 'Base' not in VARIANT nodes "
                    + "— using base variant (prefab default materials)",
            }, logLines);
        }

        [Fact]
        public void LogPartVariantResolution_KeysArePerPart()
        {
            GhostVisualBuilder.LogPartVariantResolution(
                true, true, "partA", "S", "p", Rules(1), true, 1, 0);
            GhostVisualBuilder.LogPartVariantResolution(
                true, true, "partA", "S", "p", Rules(1), true, 1, 0);
            GhostVisualBuilder.LogPartVariantResolution(
                true, true, "partB", "S", "p", Rules(1), true, 1, 0);
            // The second partA line falls inside the 60 s window of variant_sel_partA.
            Assert.Equal(2, logLines.Count);
        }

        // --- LogPartRendererExclusionNames ---

        [Fact]
        public void LogPartRendererExclusionNames_EmptySets_LogsNothing()
        {
            GhostVisualBuilder.LogPartRendererExclusionNames(
                new HashSet<string>(), "claw", 4242u, new HashSet<string>());
            Assert.Empty(logLines);
        }

        [Fact]
        public void LogPartRendererExclusionNames_BothSets_LogsDepthMaskThenWheel()
        {
            GhostVisualBuilder.LogPartRendererExclusionNames(
                new HashSet<string> { "claw_mask" }, "claw", 4242u, new HashSet<string> { "wheel_busted" });
            Assert.Equal(new[]
            {
                "[Parsek][VERBOSE][GhostVisual]   Part 'claw' pid=4242: found 1 depth-mask transform(s): "
                    + "[claw_mask]; ghost skips them",
                "[Parsek][VERBOSE][GhostVisual]   Part 'claw' pid=4242: found 1 ModuleWheelDamage "
                    + "damagedTransformName(s): [wheel_busted]",
            }, logLines);
        }

        [Fact]
        public void LogPartRendererExclusionNames_WheelOnly_LogsWheelOnly()
        {
            GhostVisualBuilder.LogPartRendererExclusionNames(
                new HashSet<string>(), "wheel", 7u, new HashSet<string> { "a" });
            Assert.Single(logLines);
            Assert.Contains("ModuleWheelDamage damagedTransformName(s): [a]", logLines[0]);
        }

        // --- LogVariantRendererSkips ---

        [Fact]
        public void LogVariantRendererSkips_ZeroCounts_LogsNothing()
        {
            GhostVisualBuilder.LogVariantRendererSkips(0, "engineX", 0);
            Assert.Empty(logLines);
        }

        [Fact]
        public void LogVariantRendererSkips_BothCounts_LogsBoth()
        {
            GhostVisualBuilder.LogVariantRendererSkips(2, "engineX", 5);
            Assert.Equal(new[]
            {
                "[Parsek][VERBOSE][GhostVisual]   Skipped 2 MeshRenderers on inactive variant objects",
                "[Parsek][VERBOSE][GhostVisual]   Skipped 5 MeshRenderers by selected variant GAMEOBJECT rules",
            }, logLines);
        }

        [Fact]
        public void LogVariantRendererSkips_RuleCountOnly_LogsRuleLine()
        {
            GhostVisualBuilder.LogVariantRendererSkips(0, "engineX", 1);
            Assert.Single(logLines);
            Assert.Contains("Skipped 1 MeshRenderers by selected variant GAMEOBJECT rules", logLines[0]);
        }

        // --- LogPartCloneSummary ---

        [Fact]
        public void LogPartCloneSummary_MeshClonedIsTotalMinusSkinned()
        {
            GhostVisualBuilder.LogPartCloneSummary(7, 2, "wheel", 100000u, 1, 3, 4);
            Assert.Equal(new[]
            {
                "[Parsek][VERBOSE][GhostVisual]   Part 'wheel' pid=100000: cloned 5 MeshRenderers, "
                    + "2 SkinnedMeshRenderers, skipped 1 null-mesh SMRs, skipped 3 damaged-wheel renderers, "
                    + "skipped 4 depth-mask renderers",
            }, logLines);
        }

        [Fact]
        public void LogPartCloneSummary_LoggedEvenWhenNothingCloned()
        {
            GhostVisualBuilder.LogPartCloneSummary(0, 0, "empty", 0u, 0, 0, 0);
            Assert.Single(logLines);
            Assert.Contains("cloned 0 MeshRenderers, 0 SkinnedMeshRenderers", logLines[0]);
        }
    }
}
