using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class EngineFxBuilderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public EngineFxBuilderTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void FilterEffectGroups_MatchingModuleGroups_ReturnsOnlySelectedGroups()
        {
            var running = new ConfigNode("running");
            var power = new ConfigNode("power");
            var spool = new ConfigNode("spool");
            var moduleGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "power",
                "spool"
            };

            EngineFxBuilder.FilterEffectGroups(
                new[] { running, power, spool },
                new[] { "running", "power", "spool" },
                moduleGroups,
                out ConfigNode[] filteredGroups,
                out string[] filteredNames);

            Assert.Equal(2, filteredGroups.Length);
            Assert.Same(power, filteredGroups[0]);
            Assert.Same(spool, filteredGroups[1]);
            Assert.Equal(new[] { "power", "spool" }, filteredNames);
        }

        [Fact]
        public void FilterEffectGroups_NoMatches_FallsBackToAllGroups()
        {
            var running = new ConfigNode("running");
            var power = new ConfigNode("power");
            var moduleGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "missing"
            };

            EngineFxBuilder.FilterEffectGroups(
                new[] { running, power },
                new[] { "running", "power" },
                moduleGroups,
                out ConfigNode[] filteredGroups,
                out string[] filteredNames);

            Assert.Equal(2, filteredGroups.Length);
            Assert.Same(running, filteredGroups[0]);
            Assert.Same(power, filteredGroups[1]);
            Assert.Equal(new[] { "running", "power" }, filteredNames);
            AssertHasEngineFxLog("no module-group matches; keeping all 2 EFFECTS groups");
        }

        [Fact]
        public void FilterEffectGroups_ShorterGroupNames_UsesUnnamedFallbackAndLogs()
        {
            var running = new ConfigNode("running");
            var unnamed = new ConfigNode("power");
            var moduleGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "?"
            };

            EngineFxBuilder.FilterEffectGroups(
                new[] { running, unnamed },
                new[] { "running" },
                moduleGroups,
                out ConfigNode[] filteredGroups,
                out string[] filteredNames);

            Assert.Single(filteredGroups);
            Assert.Same(unnamed, filteredGroups[0]);
            Assert.Equal(new[] { "?" }, filteredNames);
            AssertHasEngineFxLog("1 EFFECTS group names missing; using '?' fallback");
        }

        [Fact]
        public void TryReadModelFxConfigEntry_ParsesOffsetAndReadsNoRotation()
        {
            var modelNode = new ConfigNode("MODEL_MULTI_PARTICLE_PERSIST");
            modelNode.AddValue("transformName", "fxPoint");
            modelNode.AddValue("modelName", "Squad/FX/Monoprop_small");
            modelNode.AddValue("localPosition", "0,0,0.15");

            bool parsed = EngineFxBuilder.TryReadModelFxConfigEntry(
                "MODEL_MULTI_PARTICLE_PERSIST",
                modelNode,
                "running",
                out EngineFxBuilder.ModelFxConfigEntry entry);

            Assert.True(parsed);
            Assert.Equal("MODEL_MULTI_PARTICLE_PERSIST", entry.nodeType);
            Assert.Equal("fxPoint", entry.transformName);
            Assert.Equal("Squad/FX/Monoprop_small", entry.modelName);
            AssertVector3Close(new Vector3(0f, 0f, 0.15f), entry.localPos);
            Assert.True(string.IsNullOrEmpty(entry.rawLocalRotation));
            Assert.Equal("running", entry.groupName);
        }

        [Fact]
        public void TryReadModelFxConfigEntry_PreservesExplicitRotationString_AndUnnamedGroupFallback()
        {
            var modelNode = new ConfigNode("MODEL_PARTICLE");
            modelNode.AddValue("transformName", "thrustTransform");
            modelNode.AddValue("modelName", "customModel");
            modelNode.AddValue("localOffset", "1,2,3");
            modelNode.AddValue("localRotation", "10,20,30");

            bool parsed = EngineFxBuilder.TryReadModelFxConfigEntry(
                "MODEL_PARTICLE",
                modelNode,
                "?",
                out EngineFxBuilder.ModelFxConfigEntry entry);

            Assert.True(parsed);
            Assert.Equal("MODEL_PARTICLE", entry.nodeType);
            Assert.Equal("thrustTransform", entry.transformName);
            Assert.Equal("customModel", entry.modelName);
            AssertVector3Close(new Vector3(1f, 2f, 3f), entry.localPos);
            Assert.Equal("10,20,30", entry.rawLocalRotation);
            Assert.Equal("?", entry.groupName);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("bad-rotation")]
        [InlineData("0,0")]
        public void ModelFxWithoutAReadableLocalRotation_IsIdentity_AsStockModelParticleFxDoes(string rotation)
        {
            // The Ant and Spider carried -90 degrees about X here: PP-7 read the ghost's
            // Monoprop_small 119 px to the side of the real plume, its particles 90 degrees off.
            foreach (string type in new[] { "MODEL_MULTI_PARTICLE_PERSIST", "MODEL_MULTI_PARTICLE", "MODEL_PARTICLE" })
            foreach (string model in new[] { "Squad/FX/Monoprop_small", "Squad/FX/Monoprop_medium", "Squad/FX/ksX_Exhaust" })
            {
                var group = new ConfigNode("running");
                ConfigNode node = group.AddNode(type);
                node.AddValue("transformName", "FXTransform");
                node.AddValue("modelName", model);
                if (rotation != null) node.AddValue("localRotation", rotation);
                var entries = new List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName, GhostEngineFxCurves fxCurves)>();
                FloatCurve emission = null, speed = null;
                EngineFxBuilder.ScanEffectsModelFxEntries(
                    new[] { group }, new[] { "running" }, entries, ref emission, ref speed);
                Quaternion read = Assert.Single(entries).localRot;
                Assert.Equal(0f, read.x);
                Assert.Equal(0f, read.y);
                Assert.Equal(0f, read.z);
                Assert.Equal(1f, read.w);
            }
        }

        [Fact]
        public void TryReadModelFxConfigEntry_NullModelNode_ReturnsFalseAndLogsSkip()
        {
            bool parsed = EngineFxBuilder.TryReadModelFxConfigEntry(
                "MODEL_PARTICLE",
                null,
                "running",
                out _);

            Assert.False(parsed);
            AssertHasEngineFxLog("TryReadModelFxConfigEntry: skipped null node");
        }

        [Fact]
        public void TryReadPrefabParticleConfigEntry_SkipsFlameoutSparksAndDebrisPrefabs()
        {
            string[] skippedPrefabs =
            {
                "fx_flameout_large",
                "fx_sparks_small",
                "fx_debrisSmoke"
            };

            for (int i = 0; i < skippedPrefabs.Length; i++)
            {
                var prefabNode = new ConfigNode("PREFAB_PARTICLE");
                prefabNode.AddValue("prefabName", skippedPrefabs[i]);
                prefabNode.AddValue("transformName", "fxPoint");

                bool parsed = EngineFxBuilder.TryReadPrefabParticleConfigEntry(
                    prefabNode,
                    "running",
                    out _);

                Assert.False(parsed);
            }
        }

        [Fact]
        public void TryReadPrefabParticleConfigEntry_IgnoresLocalPosition_AsStockPrefabParticleFxDoes()
        {
            var prefabNode = new ConfigNode("PREFAB_PARTICLE");
            prefabNode.AddValue("prefabName", "fx_smokeTrail_light");
            prefabNode.AddValue("transformName", "thrustTransform");
            prefabNode.AddValue("localPosition", "0,0,0.2");
            prefabNode.AddValue("localRotation", "0,45,0");

            bool parsed = EngineFxBuilder.TryReadPrefabParticleConfigEntry(
                prefabNode,
                "running",
                out EngineFxBuilder.PrefabFxConfigEntry entry);

            Assert.True(parsed);
            Assert.Equal("fx_smokeTrail_light", entry.prefabName);
            Assert.Equal("thrustTransform", entry.transformName);
            AssertVector3Close(Vector3.zero, entry.localOffset);
            Assert.Equal("0,45,0", entry.rawLocalRotation);
            Assert.Equal("running", entry.groupName);

            prefabNode.AddValue("localOffset", "0,0,1");
            Assert.True(EngineFxBuilder.TryReadPrefabParticleConfigEntry(prefabNode, "running", out entry));
            AssertVector3Close(new Vector3(0f, 0f, 1f), entry.localOffset);
        }

        [Fact]
        public void ResolvePrefabParticleRotationMode_UsesConfiguredRotationOrParentAxisHeuristic()
        {
            EngineFxBuilder.PrefabParticleRotationMode configured = EngineFxBuilder.ResolvePrefabParticleRotationMode(
                hasLocalRotation: true,
                parentUp: Vector3.right);
            EngineFxBuilder.PrefabParticleRotationMode vertical = EngineFxBuilder.ResolvePrefabParticleRotationMode(
                hasLocalRotation: false,
                parentUp: Vector3.up);
            EngineFxBuilder.PrefabParticleRotationMode sideways = EngineFxBuilder.ResolvePrefabParticleRotationMode(
                hasLocalRotation: false,
                parentUp: Vector3.forward);

            Assert.Equal(EngineFxBuilder.PrefabParticleRotationMode.UseConfiguredRotation, configured);
            Assert.Equal(EngineFxBuilder.PrefabParticleRotationMode.UseIdentityRotation, vertical);
            Assert.Equal(EngineFxBuilder.PrefabParticleRotationMode.UseMinus90XRotation, sideways);
        }

        // ---- the engines that carried a stand-in flame play the cfg's own EFFECTS ---

        // type:group:transform:asset of every particle entry of the stock 1.12.5 part config, in the
        // scan's order: "M" MODEL_MULTI_PARTICLE, "S" MODEL_PARTICLE, "P" PREFAB_PARTICLE. The last
        // two parts are the v1 Ant and Spider, which shared the removed branch.
        private const string BigSmoke = "P:running_closed:smokePoint:fx_smokeTrail_veryLarge";
        private const string Sparks = "P:flameout:smokePoint:fx_exhaustSparks_flameout_2";

        [Theory]
        [InlineData("Size2LFB", "runningEffectName=running_closed", "Throttle",
            "M:running_closed:thrustTransform:Squad/FX/ks1_Exhaust|" + BigSmoke + "|" + Sparks)]
        [InlineData("Size2LFB_v2", "runningEffectName=running_closed", "Throttle",
            "M:running_closed:thrustTransform:Squad/FX/ks1_Exhaust|" + BigSmoke + "|" + Sparks)]
        [InlineData("Size3EngineCluster", "runningEffectName=running_closed", "Throttle",
            "M:running_closed:thrustTransform:Squad/FX/ks25_Exhaust|" + BigSmoke + "|" + Sparks)]
        [InlineData("Size3AdvancedEngine", "runningEffectName=running_closed", "Throttle",
            "M:running_closed:fxPoint:Squad/FX/ksX_Exhaust|" + BigSmoke + "|" + Sparks)]
        [InlineData("MassiveBooster", "powerEffectName=running_closed", "Thrust",
            "M:running_closed:fxPoint:Squad/FX/SRB_Large|S:running_closed:fxPoint:Squad/FX/SRB_LargeSparks|"
            + BigSmoke + "|" + Sparks)]
        [InlineData("SSME", "runningEffectName=running_closed|powerEffectName=shockDiamond", "Throttle",
            "M:running_closed:thrustPoint:Squad/FX/hydroLOXFlame|P:running_closed:smokePoint:fx_smokeTrail_light|"
            + "P:shockDiamond:thrustTransformYup:fx_exhaustFlame_blue_small|" + Sparks)]
        [InlineData("omsEngine", "runningEffectName=running", "Throttle",
            "M:running:FXTransform:Squad/FX/Monoprop_big|" + Sparks)]
        [InlineData("microEngine_v2", "runningEffectName=running", "Throttle",
            "M:running:FXTransform:Squad/FX/Monoprop_small|" + Sparks)]
        [InlineData("radialEngineMini_v2", "runningEffectName=running", "Throttle",
            "M:running:FXTransform:Squad/FX/Monoprop_small|" + Sparks)]
        [InlineData("microEngine", "runningEffectName=running", "Throttle",
            "M:running:FXTransform:Squad/FX/Monoprop_small|" + Sparks)]
        [InlineData("radialEngineMini", "runningEffectName=running", "Throttle",
            "M:running:FXTransform:Squad/FX/Monoprop_small|" + Sparks)]
        public void EngineWithoutStandIn_ScansExactlyItsOwnEffects(
            string diskName, string moduleEffects, string modelChannel, string cfgEntries)
        {
            var root = new ConfigNode("root");
            ConfigNode part = root.AddNode("PART");
            part.AddValue("name", diskName);
            ConfigNode effects = part.AddNode("EFFECTS");
            var expected = new List<string>();
            foreach (string entry in cfgEntries.Split('|'))
            {
                string[] f = entry.Split(':');
                string type = f[0] == "M" ? "MODEL_MULTI_PARTICLE" : f[0] == "S" ? "MODEL_PARTICLE" : "PREFAB_PARTICLE";
                ConfigNode node = (effects.GetNode(f[1]) ?? effects.AddNode(f[1])).AddNode(type);
                node.AddValue(f[0] == "P" ? "prefabName" : "modelName", f[3]);
                node.AddValue("transformName", f[2]);
                node.AddValue("emission", "0.0 0.0");
                node.AddValue("emission", "1.0 1.25");
                if (f[1] != "flameout") expected.Add(type + ":" + f[2] + ":" + f[3]);
            }
            ConfigNode module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleEnginesFX");
            string running = null, power = null;
            foreach (string pair in moduleEffects.Split('|'))
            {
                string[] kv = pair.Split('=');
                module.AddValue(kv[0], kv[1]);
                if (kv[0] == "runningEffectName") running = kv[1]; else power = kv[1];
            }

            // The pristine read is what an install whose Waterfall pack stripped the part draws from,
            // and it runs the same group filter and scans as the post-ModuleManager config.
            string runtimeName = diskName.Replace('_', '.');
            Assert.True(PristinePartFxResolver.TryExtract(
                root, runtimeName, "test", out PristinePartFxResolver.PristinePartFxData pristine));
            var models = new List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName, GhostEngineFxCurves fxCurves)>();
            var prefabs = new List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName, GhostEngineFxCurves fxCurves)>();
            FloatCurve emission = null, speed = null;
            EngineFxBuilder.ScanPristineEffectsEntries(
                pristine, 0, runtimeName, models, prefabs, ref emission, ref speed);

            var read = new List<string>();
            foreach (var m in models)
            {
                read.Add(m.nodeType + ":" + m.transformName + ":" + m.modelName);
                Assert.NotNull(m.fxCurves);
                Assert.Equal(modelChannel,
                    GhostEngineFxPower.ResolveChannel(m.groupName, power, running, null).ToString());
            }
            foreach (var p in prefabs)
            {
                read.Add("PREFAB_PARTICLE:" + p.transformName + ":" + p.prefabName);
                // Curves make the builder load stock's exact Effects/ asset and follow its rule.
                Assert.NotNull(p.fxCurves);
                Assert.False(EngineFxBuilder.IsSupplementFxGroup(p.groupName));
                if (p.groupName == "shockDiamond")
                    Assert.Equal(GhostEngineFxChannel.Thrust,
                        GhostEngineFxPower.ResolveChannel(p.groupName, power, running, null));
            }
            Assert.Equal(expected, read);
        }

        /// <summary>
        /// Nothing after the scan may add a plume to an engine by name or clear what its cfg says.
        /// The builder itself needs live parts, so this reads its source.
        /// </summary>
        [Fact]
        public void EngineFxBuilder_AddsNoFlameByPartName_AndDropsNoScannedEntry()
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            var code = new System.Text.StringBuilder();
            foreach (string line in System.IO.File.ReadAllLines(System.IO.Path.Combine(
                root, "Source", "Parsek", "Ghost", "EngineFxBuilder.cs")))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("//")) code.Append(trimmed).Append(' ');
            }
            string text = code.ToString();

            foreach (string name in new[]
            {
                "Size2LFB", "Size3EngineCluster", "Size3AdvancedEngine", "MassiveBooster", "SSME",
                "omsEngine", "microEngine", "radialEngineMini"
            })
                Assert.DoesNotContain("\"" + name, text);
            foreach (string asset in new[] { "fx_exhaustFlame_yellow", "fx_exhaustFlame_blue", "fx_smokeTrail_light", "fx_smokeTrail_medium" })
                Assert.DoesNotContain("\"" + asset, text);

            // The scan, the RAPIER, the last resort and the two pristine-legacy substitutions.
            Assert.Equal(5, Count(text, "prefabFxEntries.Add(("));
            Assert.Equal(1, Count(text, "modelFxEntries.Add(("));
            // Only the SWE-remnant replacement, which refills both lists from the recovered EFFECTS.
            Assert.Equal(1, Count(text, "modelFxEntries.Clear()"));
            Assert.Equal(1, Count(text, "prefabFxEntries.Clear()"));
            Assert.Equal(0, Count(text, "FxEntries.Remove"));
        }

        private static int Count(string text, string token)
        {
            int count = 0;
            for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        // ---- RAPIER: its own trail, never the fx_smokeTrail_large substitute ----------

        /// <summary>The stock RAPIER's trail entry (rapierEngine.cfg, running_open).</summary>
        private static List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName, GhostEngineFxCurves fxCurves)> StockRapierEntries()
        {
            ConfigNode[] groups = { new ConfigNode("running_open") };
            ConfigNode prefab = groups[0].AddNode("PREFAB_PARTICLE");
            prefab.AddValue("prefabName", "fx_smokeTrail_aeroSpike");
            prefab.AddValue("transformName", "smokePoint");
            prefab.AddValue("localOffset", "0, 0, 1");
            var entries = new List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName, GhostEngineFxCurves fxCurves)>();
            EngineFxBuilder.ScanEffectsPrefabParticleEntries(groups, new[] { "running_open" }, entries);
            return entries;
        }

        [Theory]
        [InlineData("RAPIER", "fx_smokeTrail_aeroSpike", true)]
        [InlineData("RAPIER", "fx_smokeTrail_aeroSpike(Clone)", true)]
        [InlineData("rapier", "FX_SMOKETRAIL_AEROSPIKE", true)]
        [InlineData("RAPIER", "fx_smokeTrail_large", false)]
        [InlineData("RAPIER", "fx_exhaustFlame_white", false)]
        [InlineData("RAPIER", null, false)]
        [InlineData("turboJet", "fx_smokeTrail_aeroSpike", false)]
        [InlineData("toroidalAerospike", "fx_smokeTrail_aeroSpike", false)]
        [InlineData(null, "fx_smokeTrail_aeroSpike", false)]
        public void IsRapierOwnTrail_OnlyTheRapiersOwnPrefab(string partName, string prefabName, bool expected)
        {
            Assert.Equal(expected, EngineFxBuilder.IsRapierOwnTrail(partName, prefabName));
        }

        [Fact]
        public void RapierWithModelFlame_KeepsOnlyItsOwnTrail()
        {
            var entries = StockRapierEntries();
            Assert.False(EngineFxBuilder.TryAddRapierWhiteFlame(
                entries, 1, _ => true, "thrustTransform", out string flameTransform));
            Assert.Null(flameTransform);
            var trail = Assert.Single(entries);
            Assert.True(EngineFxBuilder.IsRapierOwnTrail("RAPIER", trail.prefabName));
            Assert.Equal("smokePoint", trail.transformName);
            Assert.Equal("running_open", trail.groupName);
        }

        [Fact]
        public void RapierWithoutModelFlame_KeepsItsTrailAndGetsTheWhiteFlameOnIt()
        {
            var entries = StockRapierEntries();
            Assert.True(EngineFxBuilder.TryAddRapierWhiteFlame(
                entries, 0, _ => true, "thrustTransform", out string flameTransform));
            Assert.Equal("smokePoint", flameTransform);
            Assert.Equal(2, entries.Count);
            Assert.True(EngineFxBuilder.IsRapierOwnTrail("RAPIER", entries[0].prefabName));
            Assert.Equal("fx_exhaustFlame_white", entries[1].prefabName);
            Assert.Equal("fallback", entries[1].groupName);
            Assert.True(entries[1].hasLocalRotation);
            Assert.DoesNotContain(entries, e => e.prefabName == "fx_smokeTrail_large");
        }

        [Fact]
        public void RapierWhiteFlame_NotDuplicated()
        {
            var entries = StockRapierEntries();
            Assert.True(EngineFxBuilder.TryAddRapierWhiteFlame(entries, 0, _ => true, null, out _));
            Assert.False(EngineFxBuilder.TryAddRapierWhiteFlame(entries, 0, _ => true, null, out _));
            Assert.Equal(2, entries.Count);
        }

        [Theory]
        [InlineData("smokePoint", "thrustTransform", "smokePoint")]
        [InlineData("thrustTransform", "customThrust", "thrustTransform")]
        [InlineData("customThrust", "customThrust", "customThrust")]
        [InlineData("none", "customThrust", "thrustTransform")]
        public void RapierWhiteFlame_AnchorFallback(string existing, string thrustVector, string expected)
        {
            var entries = new List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName, GhostEngineFxCurves fxCurves)>();
            Assert.True(EngineFxBuilder.TryAddRapierWhiteFlame(
                entries, 0, name => name == existing, thrustVector, out string flameTransform));
            Assert.Equal(expected, flameTransform);
        }

        [Fact]
        public void RapierTrail_LookupIsExactAndNoCodePathNamesTheSubstitute()
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = System.IO.Path.Combine(root, "Source", "Parsek", "Ghost", "EngineFxBuilder.cs");
            var code = new List<string>();
            foreach (string line in System.IO.File.ReadAllLines(path))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("//"))
                    code.Add(trimmed);
            }

            // The trail's fade component reads the ACTIVE vessel's density, so it must be stripped
            // with its thresholds captured.
            string setup = System.IO.File.ReadAllText(System.IO.Path.Combine(
                root, "Source", "Parsek", "Ghost", "GhostVisualBuilder.FxSetup.cs"));
            Assert.Contains("case \"FXFadeByPressure\":", setup);
            Assert.Contains("CapturePressureFade(behaviours[i] as FXFadeByPressure, smokeFadeSink);", setup);

            Assert.DoesNotContain(code, l => l.Contains("fx_smokeTrail_large"));
            Assert.Contains("GameObject fxPrefab = fxCurves != null || isRapierOwnTrail ? " +
                "GhostVisualBuilder.FindBuiltinEffectsPrefab(prefabName) : null; " +
                "bool isStockAsset = fxPrefab != null; " +
                "if (fxPrefab == null && !isRapierOwnTrail) " +
                "fxPrefab = GhostVisualBuilder.FindFxPrefab(prefabName);", string.Join(" ", code));
        }

        // ---- ReStock-alone donor scarcity / size fidelity / prefab aim --------------

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public void ShouldProbeBuiltinEffects_WaterfallOrReStock(bool waterfall, bool restock, bool expected)
        {
            Assert.Equal(expected, EngineFxBuilder.ShouldProbeBuiltinEffectsOnPrefabMiss(waterfall, restock));
        }

        [Fact]
        public void ModelFxSizeBoost_ReStockAuthored_IsAlwaysOne()
        {
            // Live KSP never scales model-particle size with power; the stock-tuned
            // per-part overrides (Mainsail 2.0x, default 1.5x) must not
            // inflate ReStock-authored model FX.
            Assert.Equal(1f, EngineFxBuilder.ResolveModelFxSizeBoost(true, "liquidEngineMainsail.v2"));
            Assert.Equal(1f, EngineFxBuilder.ResolveModelFxSizeBoost(true, "Mite"));
            Assert.Equal(GhostVisualBuilder.ResolveEngineFxSizeBoost("liquidEngineMainsail.v2"),
                EngineFxBuilder.ResolveModelFxSizeBoost(false, "liquidEngineMainsail.v2"));
            Assert.Equal(GhostVisualBuilder.GhostEngineFxSizeBoost,
                EngineFxBuilder.ResolveModelFxSizeBoost(false, "Mite"));
        }

        [Fact]
        public void ExhaustAimedPrefabRotation_AimsMinusYEmissionAxisAlongExhaust()
        {
            // Mammoth shape: smokePoint rig where the stock parent-up heuristic aimed
            // the smoke trail straight up; the exhaust axis (parent-local) must win.
            // smokeTrail prefabs emit along local MINUS-Y (measured: round-5 probe log
            // showed every +Y-aimed instance flowing exactly inverted).
            Vector3 exhaustParentLocal = new Vector3(0f, 0f, 1f);
            Assert.True(EngineFxBuilder.TryComputeExhaustAimedPrefabRotation(
                hasCfgRotation: false, restockAuthoredEffects: true, hasExhaustDir: true,
                exhaustParentLocal, out Quaternion rot));
            AssertVector3Close(exhaustParentLocal, rot * Vector3.down);
        }

        [Fact]
        public void ExhaustAimedPrefabRotation_ExhaustAlongMinusY_IsIdentityLike()
        {
            // A rig whose parent -Y already points exhaust-ward (the correct-by-
            // identity case the round-5 probe measured on fxTransformPlume rigs)
            // needs no rotation.
            Assert.True(EngineFxBuilder.TryComputeExhaustAimedPrefabRotation(
                hasCfgRotation: false, restockAuthoredEffects: true, hasExhaustDir: true,
                Vector3.down, out Quaternion rot));
            AssertVector3Close(Vector3.down, rot * Vector3.down);
        }

        [Fact]
        public void ExhaustAimedPrefabRotation_CantedThrustWithinTrustAngle_TrustsRig()
        {
            // LES shape (round-6 probe): fxSmoke -Y is authored straight down the
            // stack (central smoke column) while the escape thrust transform is
            // deliberately canted ~30 degrees. The rig wins: identity, not the aim.
            Vector3 cantedExhaust = new Vector3(0f, -0.9f, -0.5f);
            Assert.True(EngineFxBuilder.TryComputeExhaustAimedPrefabRotation(
                hasCfgRotation: false, restockAuthoredEffects: true, hasExhaustDir: true,
                cantedExhaust, out Quaternion rot));
            AssertVector3Close(Vector3.down, rot * Vector3.down);
        }

        [Fact]
        public void ExhaustAimedPrefabRotation_OppositeAxis_RotatesFully()
        {
            // Exhaust pointing along +Y (opposite the -Y emission axis): the
            // 180-degree degenerate case must still produce a valid rotation.
            Assert.True(EngineFxBuilder.TryComputeExhaustAimedPrefabRotation(
                hasCfgRotation: false, restockAuthoredEffects: true, hasExhaustDir: true,
                Vector3.up, out Quaternion rot));
            AssertVector3Close(Vector3.up, rot * Vector3.down);
        }

        [Theory]
        [InlineData(true, true, true)]    // cfg authored a rotation -> cfg wins
        [InlineData(false, false, true)]  // not ReStock-authored -> stock heuristic
        [InlineData(false, true, false)]  // no exhaust axis -> stock heuristic
        public void ExhaustAimedPrefabRotation_FallsBackToHeuristic(
            bool hasCfgRotation, bool restockAuthored, bool hasExhaustDir)
        {
            Assert.False(EngineFxBuilder.TryComputeExhaustAimedPrefabRotation(
                hasCfgRotation, restockAuthored, hasExhaustDir,
                new Vector3(0f, 0f, 1f), out Quaternion _));
        }

        [Theory]
        [InlineData(0.5f, false)]
        [InlineData(24f, false)]
        [InlineData(26f, true)]    // Twin-Boar smokePoint sits ~200 m out (round-8 probe)
        [InlineData(199f, true)]
        public void IsFarFxMount_ThresholdAt25Meters(float distance, bool expected)
        {
            Assert.Equal(expected, EngineFxBuilder.IsFarFxMount(distance));
        }

        [Fact]
        public void ExhaustAimedPrefabRotation_DegenerateAxis_FallsBack()
        {
            Assert.False(EngineFxBuilder.TryComputeExhaustAimedPrefabRotation(
                hasCfgRotation: false, restockAuthoredEffects: true, hasExhaustDir: true,
                Vector3.zero, out Quaternion _));
        }

        // Stock LaunchEscapeSystem.cfg's EFFECTS: one plume model, by the thrust, dark below 5 %.
        [Fact]
        public void LaunchEscapeTower_DrawsStocksPlumeByItsThrust()
        {
            var running = new ConfigNode("running_closed");
            running.AddNode("AUDIO").AddValue("clip", "sound_rocket_spurts");
            ConfigNode node = running.AddNode("MODEL_MULTI_PARTICLE");
            node.AddValue("modelName", "Squad/FX/LES_Thruster");
            node.AddValue("transformName", "thrustTransform");
            foreach (string key in new[] { "0.0 0.0", "0.05 0.0", "0.075 0.25", "1.0 1.25" }) node.AddValue("emission", key);
            foreach (string key in new[] { "0.0 0.5", "1.0 1.2" }) node.AddValue("speed", key);
            EngineFxBuilder.FilterEffectGroups(
                new[] { running, new ConfigNode("engage"), new ConfigNode("flameout") },
                new[] { "running_closed", "engage", "flameout" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "running_closed" },
                out ConfigNode[] groups, out string[] names);
            var entries = new List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName, GhostEngineFxCurves fxCurves)>();
            FloatCurve emission = null, speed = null;
            EngineFxBuilder.ScanEffectsModelFxEntries(groups, names, entries, ref emission, ref speed);

            var entry = Assert.Single(entries);
            Assert.Equal("Squad/FX/LES_Thruster", entry.modelName);
            Assert.Equal("thrustTransform", entry.transformName);
            Assert.Equal(GhostEngineFxChannel.Thrust,
                GhostEngineFxPower.ResolveChannel(entry.groupName, "running_closed", "", ""));
            Assert.Equal(0f, entry.fxCurves.emission.Value(0f));
            Assert.Equal(0f, entry.fxCurves.emission.Value(0.05f));
            Assert.Equal(1.25f, entry.fxCurves.emission.Value(1f));
            Assert.Equal(1.2f, entry.fxCurves.speed.Value(1f), 1e-6f);
            // Unlit (shut down, or never lit): no particle, whatever the asset emits.
            Assert.Equal(0, GhostEngineFxPower.EmissionRate(500f, entry.fxCurves.emission.Value(0f)));
        }

        // No part is left out of the engine FX by its name: the tower was, so its ghost burned
        // with no plume at all (it has no nozzle glow either). The RAPIER's is an addition.
        [Fact]
        public void TryBuildEngineFx_NamesNoPartButTheRapier()
        {
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "Source", "Parsek", "Ghost", "EngineFxBuilder.cs"));
            var comparisons = System.Text.RegularExpressions.Regex.Matches(
                source, @"string\.Equals\(\s*partName,\s*""([^""]+)""");
            Assert.NotEmpty(comparisons);
            foreach (System.Text.RegularExpressions.Match m in comparisons)
                Assert.Equal("RAPIER", m.Groups[1].Value);
            Assert.DoesNotContain("LaunchEscapeSystem", source);
        }

        private static void AssertVector3Close(Vector3 expected, Vector3 actual, float epsilon = 1e-4f)
        {
            Assert.InRange(Mathf.Abs(expected.x - actual.x), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.y - actual.y), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.z - actual.z), 0f, epsilon);
        }

        private void AssertHasEngineFxLog(string fragment)
        {
            Assert.Contains(logLines, line =>
                line.Contains("[EngineFx]") &&
                line.Contains(fragment));
        }
    }
}
