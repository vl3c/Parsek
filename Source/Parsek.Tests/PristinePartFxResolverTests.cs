using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the Waterfall pristine-config ghost FX fallback: PristinePartFxResolver
    /// extraction/caching, the WaterfallCompat gating predicate, and SWE-shaped fixture
    /// behavior of the existing EFFECTS scanners (post-MM audio-only groups yield zero
    /// entries; pristine groups yield the stock particle entries).
    /// </summary>
    [Collection("Sequential")]
    public class PristinePartFxResolverTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PristinePartFxResolverTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            PristinePartFxResolver.ResetForTesting();
            WaterfallCompat.ResetForTesting();
        }

        public void Dispose()
        {
            PristinePartFxResolver.ResetForTesting();
            WaterfallCompat.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---- fixtures -------------------------------------------------------------

        /// <summary>
        /// Synthetic pristine EFFECTS-engine PART node (Spark/RAPIER-like shape: ModuleEnginesFX
        /// with named effects + particle nodes in EFFECTS; disk underscore name).
        /// </summary>
        private static ConfigNode BuildPristineEffectsEnginePartNode()
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "effectsEngine_v2");

            var effects = new ConfigNode("EFFECTS");
            var runningClosed = new ConfigNode("running_closed");
            var model = new ConfigNode("MODEL_MULTI_PARTICLE");
            model.AddValue("modelName", "Squad/FX/ks25_Exhaust");
            model.AddValue("transformName", "thrustTransform");
            runningClosed.AddNode(model);
            effects.AddNode(runningClosed);
            var engage = new ConfigNode("engage");
            engage.AddNode(new ConfigNode("AUDIO"));
            effects.AddNode(engage);
            part.AddNode(effects);

            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleEnginesFX");
            module.AddValue("runningEffectName", "running_closed");
            part.AddNode(module);
            return part;
        }

        /// <summary>SWE-shaped post-MM EFFECTS: renamed audio-only groups.</summary>
        private static ConfigNode BuildSweShapedEffectsNode()
        {
            var effects = new ConfigNode("EFFECTS");
            var running = new ConfigNode("fx-swivel-running");
            running.AddNode(new ConfigNode("AUDIO"));
            effects.AddNode(running);
            var engage = new ConfigNode("engage");
            engage.AddNode(new ConfigNode("AUDIO"));
            effects.AddNode(engage);
            var disengage = new ConfigNode("disengage");
            disengage.AddNode(new ConfigNode("AUDIO"));
            effects.AddNode(disengage);
            var flameout = new ConfigNode("flameout");
            flameout.AddNode(new ConfigNode("AUDIO"));
            effects.AddNode(flameout);
            return effects;
        }

        private static ConfigNode WrapInFileRoot(params ConfigNode[] partNodes)
        {
            var root = new ConfigNode("root");
            foreach (var p in partNodes)
                root.AddNode(p);
            return root;
        }

        private static void ScanAllGroups(
            ConfigNode effectsNode,
            HashSet<string> moduleGroups,
            List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName)> modelFxEntries,
            List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName)> prefabFxEntries)
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

        // ---- TryExtract -----------------------------------------------------------

        [Fact]
        public void TryExtract_MatchesDiskUnderscoreNameAgainstRuntimeDotName()
        {
            var root = WrapInFileRoot(BuildPristineEffectsEnginePartNode());

            bool found = PristinePartFxResolver.TryExtract(
                root, "effectsEngine.v2", "fixture.cfg", out var data);

            Assert.True(found);
            Assert.True(data.Found);
            Assert.NotNull(data.EffectsNode);
            Assert.Single(data.EngineModuleEffectNames);
            Assert.Contains("running_closed", data.EngineModuleEffectNames[0]);
            Assert.Contains(logLines, l => l.Contains("[WaterfallCompat]") &&
                l.Contains("pristine FX data extracted") && l.Contains("effectsEngine.v2"));
        }

        [Fact]
        public void TryExtract_MultiPartFile_PicksMatchingNodeOnly()
        {
            var other = new ConfigNode("PART");
            other.AddValue("name", "someOtherPart");
            var otherEffects = new ConfigNode("EFFECTS");
            otherEffects.AddNode(new ConfigNode("wrong_group"));
            other.AddNode(otherEffects);

            var root = WrapInFileRoot(other, BuildPristineEffectsEnginePartNode());

            bool found = PristinePartFxResolver.TryExtract(
                root, "effectsEngine.v2", "fixture.cfg", out var data);

            Assert.True(found);
            Assert.NotNull(data.EffectsNode.GetNode("running_closed"));
            Assert.Null(data.EffectsNode.GetNode("wrong_group"));
        }

        [Fact]
        public void TryExtract_PartNotInFile_ReturnsFalseAndLogs()
        {
            var root = WrapInFileRoot(BuildPristineEffectsEnginePartNode());

            bool found = PristinePartFxResolver.TryExtract(
                root, "someEntirelyDifferentPart", "fixture.cfg", out var data);

            Assert.False(found);
            Assert.False(data.Found);
            Assert.Contains(logLines, l => l.Contains("[WaterfallCompat]") &&
                l.Contains("pristine PART node not found") && l.Contains("someEntirelyDifferentPart"));
        }

        [Fact]
        public void TryExtract_MultiModeEngine_ExtractsEffectNamesPerOrdinal()
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "dualMode");
            var mode0 = new ConfigNode("MODULE");
            mode0.AddValue("name", "ModuleEnginesFX");
            mode0.AddValue("runningEffectName", "running_thrust");
            mode0.AddValue("powerEffectName", "power_open");
            part.AddNode(mode0);
            var unrelated = new ConfigNode("MODULE");
            unrelated.AddValue("name", "ModuleGimbal");
            part.AddNode(unrelated);
            var mode1 = new ConfigNode("MODULE");
            mode1.AddValue("name", "ModuleEnginesFX");
            mode1.AddValue("spoolEffectName", "spool_closed");
            part.AddNode(mode1);

            PristinePartFxResolver.TryExtract(
                WrapInFileRoot(part), "dualMode", "fixture.cfg", out var data);

            Assert.Equal(2, data.EngineModuleEffectNames.Count);
            Assert.Contains("running_thrust", data.EngineModuleEffectNames[0]);
            Assert.Contains("power_open", data.EngineModuleEffectNames[0]);
            Assert.Contains("spool_closed", data.EngineModuleEffectNames[1]);
            Assert.DoesNotContain("spool_closed", data.EngineModuleEffectNames[0]);
        }

        [Fact]
        public void TryExtract_RcsModules_ExtractRunningNamesWithDefault()
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "rcsPart");
            var rcsFx = new ConfigNode("MODULE");
            rcsFx.AddValue("name", "ModuleRCSFX");
            rcsFx.AddValue("runningEffectName", "rcs_puffs");
            part.AddNode(rcsFx);
            var rcsPlain = new ConfigNode("MODULE");
            rcsPlain.AddValue("name", "ModuleRCS");
            part.AddNode(rcsPlain);

            PristinePartFxResolver.TryExtract(
                WrapInFileRoot(part), "rcsPart", "fixture.cfg", out var data);

            Assert.Equal(2, data.RcsRunningEffectNames.Count);
            Assert.Equal("rcs_puffs", data.RcsRunningEffectNames[0]);
            Assert.Equal("running", data.RcsRunningEffectNames[1]);
        }

        // ---- legacy fx_* parsing ----------------------------------------------------

        [Fact]
        public void ParseLegacyFxKeys_KeepsRunningExhaustFiltersTransientsLightsAndEvents()
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "mainsailLike");
            part.AddValue("fx_exhaustFlame_yellow_medium", "0.0, -2, 0.0, 0.0, 1.0, 0.0, running");
            part.AddValue("fx_smokeTrail_light", "0.0, -2, 0.0, 0.0, 1.0, 0.0, running");
            part.AddValue("fx_exhaustSparks_flameout", "0.0, -2, 0.0, 0.0, 1.0, 0.0, flameout");
            part.AddValue("fx_exhaustLight_yellow", "0.0, -2, 0.0, 0.0, 0.0, 1.0, deactivate");
            part.AddValue("fx_exhaustLight_blue", "0.0, -2, 0.0, 0.0, 0.0, 1.0, running");
            part.AddValue("fx_gasBurst_white", "0.0, 0.0, 0.0, 0.0, 1.0, 0.0, decouple");
            part.AddValue("sound_rocket_hard", "running");

            List<string> kept = PristinePartFxResolver.ParseLegacyFxKeys(part, "mainsailLike");

            Assert.Equal(
                new List<string> { "fx_exhaustFlame_yellow_medium", "fx_smokeTrail_light" },
                kept);
            Assert.Contains(logLines, l => l.Contains("[WaterfallCompat]") &&
                l.Contains("kept=2") && l.Contains("skippedTransient=1") && l.Contains("skippedLight=2"));
        }

        [Fact]
        public void TryExtract_RealSwivelShape_LegacyKeysNoEffectsNode()
        {
            // The real stock Swivel v2 (liquidEngine2_v2): legacy fx_* keys + plain
            // ModuleEngines, NO EFFECTS node. Pristine recovery must surface the legacy
            // prefab names (lights excluded) with a null EffectsNode.
            var part = new ConfigNode("PART");
            part.AddValue("name", "liquidEngine2_v2");
            part.AddValue("fx_exhaustFlame_blue", "0.0, -0.574338, 0.0, 0.0, 1.0, 0.0, running");
            part.AddValue("fx_exhaustLight_blue", "0.0, -0.574338, 0.0, 0.0, 0.0, 1.0, running");
            part.AddValue("fx_smokeTrail_light", "0.0, -0.574338, 0.0, 0.0, 1.0, 0.0, running");
            part.AddValue("fx_exhaustSparks_flameout", "0.0, -0.574338, 0.0, 0.0, 1.0, 0.0, flameout");
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleEngines");
            module.AddValue("fxOffset", "0, 0, 0.574338");
            part.AddNode(module);

            bool found = PristinePartFxResolver.TryExtract(
                WrapInFileRoot(part), "liquidEngine2.v2", "fixture.cfg", out var data);

            Assert.True(found);
            Assert.Null(data.EffectsNode);
            Assert.Single(data.EngineModuleEffectNames);
            Assert.Empty(data.EngineModuleEffectNames[0]);
            Assert.Equal(
                new List<string> { "fx_exhaustFlame_blue", "fx_smokeTrail_light" },
                data.LegacyFxPrefabNames);
        }

        [Theory]
        [InlineData("0.0, -2, 0.0, 0.0, 1.0, 0.0, running", true)]
        [InlineData("0.0, -2, 0.0, 0.0, 1.0, 0.0, power", true)]
        [InlineData("0.0, -2, 0.0, 0.0, 1.0, 0.0, flameout", false)]
        [InlineData("0.0, -2, 0.0, 0.0, 1.0, 0.0, deactivate", false)]
        [InlineData("0.0, -2, 0.0, 0.0, 1.0, 0.0", true)]
        [InlineData("", true)]
        [InlineData(null, true)]
        [InlineData("0.0, -2, 0.0, 0.0, 1.0, 0.0, flameout, running", true)]
        public void LegacyFxValueIncludesRunningOrPower_Cases(string value, bool expected)
        {
            Assert.Equal(expected, PristinePartFxResolver.LegacyFxValueIncludesRunningOrPower(value));
        }

        [Fact]
        public void ReadEngineEffectNames_ReadsAllFourKeysAndSkipsMissing()
        {
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleEnginesFX");
            module.AddValue("runningEffectName", "run");
            module.AddValue("directThrottleEffectName", "direct");

            HashSet<string> names = PristinePartFxResolver.ReadEngineEffectNames(module);

            Assert.Equal(2, names.Count);
            Assert.Contains("run", names);
            Assert.Contains("direct", names);
        }

        // ---- GetForPart caching -----------------------------------------------------

        [Fact]
        public void GetForPart_CachesPerPartName_SingleFileLoad()
        {
            int loads = 0;
            PristinePartFxResolver.LoadFileRootOverrideForTesting = path =>
            {
                loads++;
                return WrapInFileRoot(BuildPristineEffectsEnginePartNode());
            };

            var first = PristinePartFxResolver.GetForPart("effectsEngine.v2", "fixture.cfg");
            var second = PristinePartFxResolver.GetForPart("effectsEngine.v2", "fixture.cfg");

            Assert.True(first.Found);
            Assert.Same(first, second);
            Assert.Equal(1, loads);
        }

        [Fact]
        public void GetForPart_MissingPath_ReturnsNotFoundAndWarnsOnce()
        {
            var first = PristinePartFxResolver.GetForPart("ghostOnlyPart", null);
            var second = PristinePartFxResolver.GetForPart("ghostOnlyPart", null);

            Assert.False(first.Found);
            Assert.Same(first, second);
            Assert.Single(logLines.FindAll(l =>
                l.Contains("[WaterfallCompat]") && l.Contains("pristine cfg path unavailable")));
        }

        [Fact]
        public void GetForPart_LoaderReturnsNull_CachesNotFound()
        {
            PristinePartFxResolver.LoadFileRootOverrideForTesting = path => null;

            var data = PristinePartFxResolver.GetForPart("effectsEngine.v2", "fixture.cfg");

            Assert.False(data.Found);
        }

        // ---- gating predicate ---------------------------------------------------------

        [Theory]
        [InlineData(0, true, true)]
        [InlineData(0, false, false)]
        [InlineData(1, true, false)]
        [InlineData(3, false, false)]
        public void ShouldAttemptPristineFxFallback_TruthTable(
            int scannedEntryCount, bool hasWaterfall, bool expected)
        {
            Assert.Equal(expected,
                WaterfallCompat.ShouldAttemptPristineFxFallback(scannedEntryCount, hasWaterfall));
        }

        // ---- SWE-shaped fixture behavior of the existing scanners ----------------------

        [Fact]
        public void SweShapedPostMmEffects_YieldsZeroParticleEntries()
        {
            var modelFx = new List<(string, string, string, Vector3, Quaternion, string)>();
            var prefabFx = new List<(string, string, Vector3, Quaternion, bool, string)>();
            var liveModuleGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "fx-swivel-running", "power", "spool", "directThrottle" };

            ScanAllGroups(BuildSweShapedEffectsNode(), liveModuleGroups, modelFx, prefabFx);

            Assert.Empty(modelFx);
            Assert.Empty(prefabFx);
        }

        [Fact]
        public void PristineEffects_FilteredByPristineModuleNames_YieldsParticleEntries()
        {
            var root = WrapInFileRoot(BuildPristineEffectsEnginePartNode());
            PristinePartFxResolver.TryExtract(root, "effectsEngine.v2", "fixture.cfg", out var data);

            var modelFx = new List<(string, string, string, Vector3, Quaternion, string)>();
            var prefabFx = new List<(string, string, Vector3, Quaternion, bool, string)>();
            ScanAllGroups(data.EffectsNode, data.EngineModuleEffectNames[0], modelFx, prefabFx);

            Assert.Single(modelFx);
            Assert.Equal("Squad/FX/ks25_Exhaust", modelFx[0].Item3);
            Assert.Equal("thrustTransform", modelFx[0].Item2);
        }

        [Fact]
        public void PristineRcsGroup_ScansModelMultiParticleNodes()
        {
            var group = new ConfigNode("running");
            var model = new ConfigNode("MODEL_MULTI_PARTICLE");
            model.AddValue("modelName", "Squad/FX/Monoprop_small");
            model.AddValue("transformName", "RCSthruster");
            group.AddNode(model);

            var fxDefinitions = new List<FxModelDefinition>();
            FloatCurve emission = null, speed = null;
            GhostVisualBuilder.ScanRcsEffectGroupModelNodes(
                group, fxDefinitions, ref emission, ref speed);

            Assert.Single(fxDefinitions);
            Assert.Equal("Squad/FX/Monoprop_small", fxDefinitions[0].modelName);
            Assert.Equal("RCSthruster", fxDefinitions[0].transformName);
        }
    }
}
