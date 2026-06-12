using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for ReStockPatchFxIndex: decorated MM patch-node parsing (verbatim real
    /// ReStock node names incl. the ':HAS[~RestockIgnore[*]]' suffix every patch
    /// carries), EFFECTS extraction, per-ordinal module effect names, merge rules,
    /// the group-filter source chains, and the build/caching seams.
    /// </summary>
    [Collection("Sequential")]
    public class ReStockPatchFxIndexTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ReStockPatchFxIndexTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ReStockPatchFxIndex.ResetForTesting();
            PristinePartFxResolver.ResetForTesting();
        }

        public void Dispose()
        {
            ReStockPatchFxIndex.ResetForTesting();
            PristinePartFxResolver.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---- fixtures (shapes verbatim from mods/ReStocked patch files) ------------

        /// <summary>
        /// Swivel-shaped engine patch: decorated node name with the wildcard-bearing
        /// ':HAS' suffix, '!EFFECTS' delete node, fresh EFFECTS with a particle group,
        /// and a module rename with '%runningEffectName'.
        /// </summary>
        private static ConfigNode BuildSwivelShapePatchNode()
        {
            var node = new ConfigNode("@PART[liquidEngine2_v2]:HAS[~RestockIgnore[*]]:FOR[000_ReStock]");
            node.AddValue("@author", "Chris Adderley (Nertea)");
            node.AddValue("!fx_exhaustFlame_blue", "DELETE");

            node.AddNode(new ConfigNode("!EFFECTS"));

            var effects = new ConfigNode("EFFECTS");
            var running = new ConfigNode("fx-swivel-running");
            var model = new ConfigNode("MODEL_MULTI_PARTICLE");
            model.AddValue("modelName", "ReStock/FX/restock-fx-swivel-plume-1");
            model.AddValue("transformName", "fxTransformPlume");
            running.AddNode(model);
            var prefab = new ConfigNode("PREFAB_PARTICLE");
            prefab.AddValue("prefabName", "fx_smokeTrail_light");
            prefab.AddValue("transformName", "smokePoint");
            running.AddNode(prefab);
            effects.AddNode(running);
            var engage = new ConfigNode("engage");
            engage.AddNode(new ConfigNode("AUDIO"));
            effects.AddNode(engage);
            node.AddNode(effects);

            var module = new ConfigNode("@MODULE[ModuleEngines]");
            module.AddValue("@name", "ModuleEnginesFX");
            module.AddValue("%runningEffectName", "fx-swivel-running");
            node.AddNode(module);

            var surfaceFx = new ConfigNode("@MODULE[ModuleSurfaceFX]");
            surfaceFx.AddValue("%fxMax", "0.5");
            node.AddNode(surfaceFx);
            return node;
        }

        /// <summary>RAPIER-shaped patch: fresh EFFECTS, NO engine module nodes (gimbal only).</summary>
        private static ConfigNode BuildRapierShapePatchNode()
        {
            var node = new ConfigNode("@PART[RAPIER]:HAS[~RestockIgnore[*]]:FOR[000_ReStock]");
            node.AddNode(new ConfigNode("!EFFECTS"));

            var effects = new ConfigNode("EFFECTS");
            foreach (string group in new[] { "running_closed", "running_open", "power_open" })
            {
                var g = new ConfigNode(group);
                var model = new ConfigNode("MODEL_MULTI_PARTICLE");
                model.AddValue("modelName", $"ReStock/FX/restock-fx-rapier-{group}");
                model.AddValue("transformName", "thrustTransform");
                g.AddNode(model);
                effects.AddNode(g);
            }
            node.AddNode(effects);

            var gimbal = new ConfigNode("@MODULE[ModuleGimbal]");
            gimbal.AddValue("%gimbalRange", "3");
            node.AddNode(gimbal);
            return node;
        }

        /// <summary>RCS-block-shaped patch: EFFECTS with 'running' group, no RCS module node.</summary>
        private static ConfigNode BuildRcsBlockShapePatchNode()
        {
            var node = new ConfigNode("@PART[RCSBlock_v2]:HAS[~RestockIgnore[*]]:FOR[000_ReStock]");
            var effects = new ConfigNode("EFFECTS");
            var running = new ConfigNode("running");
            var model = new ConfigNode("MODEL_MULTI_PARTICLE");
            model.AddValue("modelName", "ReStock/FX/restock-fx-rcs-1");
            model.AddValue("transformName", "RCSjet");
            running.AddNode(model);
            effects.AddNode(running);
            node.AddNode(effects);
            return node;
        }

        /// <summary>RCSLinearSmall-shaped patch: RCS module node WITHOUT runningEffectName.</summary>
        private static ConfigNode BuildLinearRcsShapePatchNode()
        {
            var node = new ConfigNode("@PART[RCSLinearSmall]:HAS[~RestockIgnore[*]]:FOR[000_ReStock]");
            var effects = new ConfigNode("EFFECTS");
            var running = new ConfigNode("running");
            var model = new ConfigNode("MODEL_MULTI_PARTICLE");
            model.AddValue("modelName", "ReStock/FX/restock-fx-rcs-1");
            model.AddValue("transformName", "RCSthruster");
            running.AddNode(model);
            effects.AddNode(running);
            node.AddNode(effects);

            var rcsModule = new ConfigNode("@MODULE[ModuleRCSFX]");
            rcsModule.AddValue("@thrusterTransformName", "RCSthruster");
            node.AddNode(rcsModule);
            return node;
        }

        /// <summary>Depthmask-shaped patch node: same part, NO EFFECTS child.</summary>
        private static ConfigNode BuildDepthmaskShapePatchNode(string diskPartName)
        {
            var node = new ConfigNode($"@PART[{diskPartName}]:HAS[~RestockIgnore[*]]:FOR[000_ReStock]");
            var partRenderer = new ConfigNode("MODULE");
            partRenderer.AddValue("name", "ModuleRestockDepthMask");
            node.AddNode(partRenderer);
            return node;
        }

        private static ConfigNode WrapInFileRoot(params ConfigNode[] partNodes)
        {
            var root = new ConfigNode("root");
            for (int i = 0; i < partNodes.Length; i++)
                root.AddNode(partNodes[i]);
            return root;
        }

        private static Dictionary<string, ReStockPatchFxIndex.ReStockPartFxEntry> Extract(
            out ReStockPatchFxIndex.ExtractStats stats, params ConfigNode[] partNodes)
        {
            var index = new Dictionary<string, ReStockPatchFxIndex.ReStockPartFxEntry>(StringComparer.Ordinal);
            stats = new ReStockPatchFxIndex.ExtractStats();
            ReStockPatchFxIndex.TryExtractFromPatchFileRoot(
                WrapInFileRoot(partNodes), "test.cfg", index, stats);
            return index;
        }

        // ---- target extraction / wildcard scoping ----------------------------------

        [Fact]
        public void TryExtractPartTarget_DecoratedRealName_ExtractsFirstBracketToken()
        {
            Assert.True(ReStockPatchFxIndex.TryExtractPartTarget(
                "@PART[liquidEngine2_v2]:HAS[~RestockIgnore[*]]:FOR[000_ReStock]", out string target));
            Assert.Equal("liquidEngine2_v2", target);
        }

        [Theory]
        [InlineData("@PART")]
        [InlineData("@PART[]")]
        [InlineData("@PART]liquidEngine[")]
        [InlineData("")]
        public void TryExtractPartTarget_Malformed_ReturnsFalse(string nodeName)
        {
            Assert.False(ReStockPatchFxIndex.TryExtractPartTarget(nodeName, out _));
        }

        [Fact]
        public void SwivelShape_WithWildcardInHasSuffix_IsIndexedNotSkipped()
        {
            var index = Extract(out var stats, BuildSwivelShapePatchNode());

            Assert.Equal(0, stats.WildcardSkips);
            Assert.Equal(1, stats.PartPatchNodes);
            Assert.True(index.ContainsKey("liquidEngine2.v2"));
        }

        [Fact]
        public void WildcardInTargetToken_IsSkipped()
        {
            var wildcard = new ConfigNode("@PART[*liquidEngine*]:FOR[000_ReStock]");
            wildcard.AddNode(new ConfigNode("EFFECTS"));
            var index = Extract(out var stats, wildcard);

            Assert.Equal(1, stats.WildcardSkips);
            Assert.Empty(index);
        }

        // ---- extraction content -----------------------------------------------------

        [Fact]
        public void SwivelShape_ExtractsFreshEffectsNotDeletionNode()
        {
            var index = Extract(out _, BuildSwivelShapePatchNode());

            var entry = index["liquidEngine2.v2"];
            Assert.NotNull(entry.EffectsNode);
            Assert.Equal("EFFECTS", entry.EffectsNode.name);
            Assert.NotNull(entry.EffectsNode.GetNode("fx-swivel-running"));
        }

        [Fact]
        public void SwivelShape_ReadsModuleEffectNameThroughMmPrefix()
        {
            var index = Extract(out _, BuildSwivelShapePatchNode());

            var entry = index["liquidEngine2.v2"];
            Assert.Single(entry.EngineModuleEffectNames);
            Assert.Contains("fx-swivel-running", entry.EngineModuleEffectNames[0]);
        }

        [Fact]
        public void RapierShape_NoEngineModuleNodes_EffectNamesEmpty()
        {
            var index = Extract(out _, BuildRapierShapePatchNode());

            var entry = index["RAPIER"];
            Assert.NotNull(entry.EffectsNode);
            Assert.Empty(entry.EngineModuleEffectNames);
        }

        [Fact]
        public void RcsShapes_DefaultRunningName_WithAndWithoutModuleNode()
        {
            var index = Extract(out _, BuildRcsBlockShapePatchNode(), BuildLinearRcsShapePatchNode());

            Assert.Empty(index["RCSBlock.v2"].RcsRunningEffectNames);
            Assert.Single(index["RCSLinearSmall"].RcsRunningEffectNames);
            Assert.Equal("running", index["RCSLinearSmall"].RcsRunningEffectNames[0]);
        }

        [Fact]
        public void DeletionPrefixedEffectNames_AreNotGroupNames()
        {
            var node = new ConfigNode("@PART[testEngine]:FOR[000_ReStock]");
            node.AddNode(new ConfigNode("EFFECTS"));
            var module = new ConfigNode("@MODULE[ModuleEnginesFX]");
            module.AddValue("%runningEffectName", "fx-test-running");
            module.AddValue("!powerEffectName", "DELETE");
            module.AddValue("-spoolEffectName", "DELETE");
            node.AddNode(module);

            var index = Extract(out _, node);

            var names = index["testEngine"].EngineModuleEffectNames[0];
            Assert.Single(names);
            Assert.Contains("fx-test-running", names);
        }

        [Theory]
        [InlineData("runningEffectName")]
        [InlineData("@runningEffectName")]
        [InlineData("%runningEffectName")]
        [InlineData("&runningEffectName")]
        public void ReadPatchValue_AcceptsAssignmentPrefixForms(string valueName)
        {
            var node = new ConfigNode("@MODULE[ModuleEnginesFX]");
            node.AddValue(valueName, "fx-x-running");
            Assert.Equal("fx-x-running", ReStockPatchFxIndex.ReadPatchValue(node, "runningEffectName"));
        }

        [Theory]
        [InlineData("!runningEffectName")]
        [InlineData("-runningEffectName")]
        [InlineData("#runningEffectName")]
        public void ReadPatchValue_SkipsNonAssignmentForms(string valueName)
        {
            var node = new ConfigNode("@MODULE[ModuleEnginesFX]");
            node.AddValue(valueName, "fx-x-running");
            Assert.Null(ReStockPatchFxIndex.ReadPatchValue(node, "runningEffectName"));
        }

        // ---- merge rules --------------------------------------------------------------

        [Fact]
        public void MergeRule_EffectsLessNodeNeverOverwritesEffectsBearing()
        {
            var index = Extract(out var stats,
                BuildSwivelShapePatchNode(),
                BuildDepthmaskShapePatchNode("liquidEngine2_v2"));

            Assert.NotNull(index["liquidEngine2.v2"].EffectsNode);
            Assert.Equal(0, stats.DuplicateEffectsConflicts);
        }

        [Fact]
        public void MergeRule_EffectsBearingNodeReplacesEffectsLess()
        {
            var index = Extract(out _,
                BuildDepthmaskShapePatchNode("liquidEngine2_v2"),
                BuildSwivelShapePatchNode());

            Assert.NotNull(index["liquidEngine2.v2"].EffectsNode);
        }

        [Fact]
        public void MergeRule_SecondEffectsBearingNode_FirstWinsAndConflictLogged()
        {
            var second = BuildSwivelShapePatchNode();
            // Different group name so the kept node is distinguishable.
            second.GetNode("EFFECTS").GetNode("fx-swivel-running").name = "fx-other-running";

            var index = Extract(out var stats, BuildSwivelShapePatchNode(), second);

            Assert.Equal(1, stats.DuplicateEffectsConflicts);
            Assert.NotNull(index["liquidEngine2.v2"].EffectsNode.GetNode("fx-swivel-running"));
            Assert.Contains(logLines, l => l.Contains("[ReStockCompat]") &&
                l.Contains("second EFFECTS-bearing"));
        }

        // ---- group filter chains -------------------------------------------------------

        [Fact]
        public void EngineGroupFilter_PatchNamesWin()
        {
            var index = Extract(out _, BuildSwivelShapePatchNode());
            var pristine = new PristinePartFxResolver.PristinePartFxData { Found = true };
            pristine.EngineModuleEffectNames.Add(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stock-running" });

            Assert.True(ReStockPatchFxIndex.TryResolveEngineGroupFilter(
                index["liquidEngine2.v2"], pristine, 0, 1, out var names, out string source));
            Assert.Equal("patch", source);
            Assert.Contains("fx-swivel-running", names);
        }

        [Fact]
        public void EngineGroupFilter_RapierFallsBackToPristinePerOrdinal()
        {
            var index = Extract(out _, BuildRapierShapePatchNode());
            var pristine = new PristinePartFxResolver.PristinePartFxData { Found = true };
            pristine.EngineModuleEffectNames.Add(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "power_open", "running_open" });
            pristine.EngineModuleEffectNames.Add(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "running_closed" });

            Assert.True(ReStockPatchFxIndex.TryResolveEngineGroupFilter(
                index["RAPIER"], pristine, 1, 2, out var names, out string source));
            Assert.Equal("pristine", source);
            Assert.Contains("running_closed", names);
            Assert.DoesNotContain("running_open", names);
        }

        [Fact]
        public void EngineGroupFilter_SingleModuleNoSources_ScansAllGroups()
        {
            var index = Extract(out _, BuildRapierShapePatchNode());

            Assert.True(ReStockPatchFxIndex.TryResolveEngineGroupFilter(
                index["RAPIER"], pristine: null, moduleIndex: 0, liveEngineModuleCount: 1,
                out var names, out string source));
            Assert.Equal("all", source);
            Assert.Null(names);
        }

        [Fact]
        public void EngineGroupFilter_MultiModuleNoSources_Skips()
        {
            var index = Extract(out _, BuildRapierShapePatchNode());

            Assert.False(ReStockPatchFxIndex.TryResolveEngineGroupFilter(
                index["RAPIER"], pristine: null, moduleIndex: 1, liveEngineModuleCount: 2,
                out _, out string source));
            Assert.Equal("skip", source);
        }

        [Fact]
        public void RcsRunningGroupName_ChainPatchThenPristineThenDefault()
        {
            var index = Extract(out _, BuildLinearRcsShapePatchNode(), BuildRcsBlockShapePatchNode());
            var pristine = new PristinePartFxResolver.PristinePartFxData { Found = true };
            pristine.RcsRunningEffectNames.Add("pristine-running");

            // Patch-authored ordinal (default 'running' recorded at parse).
            Assert.Equal("running", ReStockPatchFxIndex.ResolveRcsRunningGroupName(
                index["RCSLinearSmall"], pristine, 0));
            // No patch RCS module -> pristine ordinal.
            Assert.Equal("pristine-running", ReStockPatchFxIndex.ResolveRcsRunningGroupName(
                index["RCSBlock.v2"], pristine, 0));
            // Nothing anywhere -> stock default.
            Assert.Equal("running", ReStockPatchFxIndex.ResolveRcsRunningGroupName(
                index["RCSBlock.v2"], null, 5));
        }

        // ---- scanner composition (ReStock EFFECTS through the real engine scanners) ----

        private static void ScanAllGroups(
            ConfigNode effectsNode,
            HashSet<string> moduleGroups,
            List<(string nodeType, string transformName, string modelName, UnityEngine.Vector3 localPos, UnityEngine.Quaternion localRot, string groupName)> modelFxEntries,
            List<(string prefabName, string transformName, UnityEngine.Vector3 localOffset, UnityEngine.Quaternion localRotation, bool hasLocalRotation, string groupName)> prefabFxEntries)
        {
            ConfigNode[] allGroups = effectsNode.GetNodes();
            string[] allGroupNames = new string[allGroups.Length];
            for (int g = 0; g < allGroups.Length; g++)
                allGroupNames[g] = allGroups[g].name ?? "?";

            ConfigNode[] effectGroups = allGroups;
            string[] effectGroupNames = allGroupNames;
            if (moduleGroups != null && moduleGroups.Count > 0)
            {
                EngineFxBuilder.FilterEffectGroups(
                    allGroups, allGroupNames, moduleGroups,
                    out effectGroups, out effectGroupNames);
            }

            FloatCurve emission = null, speed = null;
            EngineFxBuilder.ScanEffectsModelFxEntries(
                effectGroups, effectGroupNames, modelFxEntries, ref emission, ref speed);
            EngineFxBuilder.ScanEffectsPrefabParticleEntries(
                effectGroups, effectGroupNames, prefabFxEntries);
        }

        [Fact]
        public void SwivelShapeEffects_ScanYieldsReStockModelAndSmokePrefab()
        {
            var index = Extract(out _, BuildSwivelShapePatchNode());
            var entry = index["liquidEngine2.v2"];

            var modelEntries = new List<(string nodeType, string transformName, string modelName, UnityEngine.Vector3 localPos, UnityEngine.Quaternion localRot, string groupName)>();
            var prefabEntries = new List<(string prefabName, string transformName, UnityEngine.Vector3 localOffset, UnityEngine.Quaternion localRotation, bool hasLocalRotation, string groupName)>();
            ScanAllGroups(entry.EffectsNode, entry.EngineModuleEffectNames[0], modelEntries, prefabEntries);

            Assert.Single(modelEntries);
            Assert.Equal("ReStock/FX/restock-fx-swivel-plume-1", modelEntries[0].modelName);
            Assert.Single(prefabEntries);
            Assert.Equal("fx_smokeTrail_light", prefabEntries[0].prefabName);
        }

        [Fact]
        public void RapierShapeEffects_PristineOrdinalFilter_ScansOnlyThatModesGroups()
        {
            var index = Extract(out _, BuildRapierShapePatchNode());
            var pristine = new PristinePartFxResolver.PristinePartFxData { Found = true };
            pristine.EngineModuleEffectNames.Add(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "power_open", "running_open" });
            pristine.EngineModuleEffectNames.Add(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "running_closed" });

            Assert.True(ReStockPatchFxIndex.TryResolveEngineGroupFilter(
                index["RAPIER"], pristine, 1, 2, out var names, out _));

            var modelEntries = new List<(string nodeType, string transformName, string modelName, UnityEngine.Vector3 localPos, UnityEngine.Quaternion localRot, string groupName)>();
            var prefabEntries = new List<(string prefabName, string transformName, UnityEngine.Vector3 localOffset, UnityEngine.Quaternion localRotation, bool hasLocalRotation, string groupName)>();
            ScanAllGroups(index["RAPIER"].EffectsNode, names, modelEntries, prefabEntries);

            Assert.Single(modelEntries);
            Assert.Equal("running_closed", modelEntries[0].groupName);
        }

        // ---- build / caching / standdown predicate ------------------------------------

        [Fact]
        public void TryGetForPart_BuildsOnceThroughSeams_AndCaches()
        {
            int enumerateCalls = 0;
            ReStockPatchFxIndex.EnumeratePatchFilesOverrideForTesting = () =>
            {
                enumerateCalls++;
                return new[] { "Engine/restock-engines-liquid-125.cfg" };
            };
            ReStockPatchFxIndex.LoadFileRootOverrideForTesting =
                path => WrapInFileRoot(BuildSwivelShapePatchNode());

            Assert.True(ReStockPatchFxIndex.TryGetForPart("liquidEngine2.v2", out var entry));
            Assert.NotNull(entry.EffectsNode);
            Assert.False(ReStockPatchFxIndex.TryGetForPart("JetEngine", out _));
            Assert.Equal(1, enumerateCalls);
            Assert.Contains(logLines, l => l.Contains("[ReStockCompat]") &&
                l.Contains("ReStock patch FX index built") && l.Contains("effectsBearing=1"));
        }

        [Fact]
        public void TryGetForPart_AbsentDirectory_PermanentlyEmptyWithOneLogLine()
        {
            ReStockPatchFxIndex.EnumeratePatchFilesOverrideForTesting = () => null;

            Assert.False(ReStockPatchFxIndex.TryGetForPart("liquidEngine2.v2", out _));
            Assert.False(ReStockPatchFxIndex.HasAuthoredEffectsFor("liquidEngine2.v2"));
            Assert.Single(logLines, l => l.Contains("[ReStockCompat]") &&
                l.Contains("index permanently empty"));
        }

        [Fact]
        public void HasAuthoredEffectsFor_FalseForEffectsLessEntry()
        {
            ReStockPatchFxIndex.EnumeratePatchFilesOverrideForTesting = () => new[] { "x.cfg" };
            ReStockPatchFxIndex.LoadFileRootOverrideForTesting =
                path => WrapInFileRoot(
                    BuildSwivelShapePatchNode(),
                    BuildDepthmaskShapePatchNode("JetEngine"));

            Assert.True(ReStockPatchFxIndex.HasAuthoredEffectsFor("liquidEngine2.v2"));
            Assert.True(ReStockPatchFxIndex.TryGetForPart("JetEngine", out var jet));
            Assert.Null(jet.EffectsNode);
            Assert.False(ReStockPatchFxIndex.HasAuthoredEffectsFor("JetEngine"));
            Assert.False(ReStockPatchFxIndex.HasAuthoredEffectsFor("restock-engine-les-2"));
        }
    }
}
