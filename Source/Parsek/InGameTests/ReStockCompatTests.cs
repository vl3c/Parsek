using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Runtime tests for the ReStock ghost FX support: ReStockPatchFxIndex built from the
    /// real GameData/ReStock/Patches files, the ReStock-authored EFFECTS recovery under
    /// Waterfall config packs, and the stock-tuning standdown predicate.
    /// All tests skip when ReStock is not installed; the Waterfall-dependent ones also
    /// skip without Waterfall (rerun after the CKAN install).
    /// </summary>
    public class ReStockCompatTests
    {
        private static bool IsReStockInstalled()
        {
            try
            {
                return System.IO.Directory.Exists(System.IO.Path.Combine(
                    KSPUtil.ApplicationRootPath ?? string.Empty, "GameData/ReStock/Patches"));
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private static AvailablePart FindSwivel()
        {
            return PartLoader.getPartInfoByName("liquidEngine2.v2");
        }

        [InGameTest(Category = "ReStockCompat", Scene = GameScenes.FLIGHT,
            Description = "ReStock installed: patch FX index builds and carries Swivel's authored EFFECTS")]
        public void IndexContainsSwivelAuthoredEffects()
        {
            if (!IsReStockInstalled())
            {
                InGameAssert.Skip("ReStock not installed; rerun after the CKAN install.");
                return;
            }

            InGameAssert.IsTrue(ReStockPatchFxIndex.TryGetForPart("liquidEngine2.v2", out var entry),
                "index has no entry for Swivel (liquidEngine2.v2)");
            InGameAssert.IsNotNull(entry.EffectsNode, "Swivel entry has no authored EFFECTS node");
            InGameAssert.IsTrue(entry.EngineModuleEffectNames.Count > 0 &&
                entry.EngineModuleEffectNames[0].Contains("fx-swivel-running"),
                "Swivel patch-authored module effect names missing 'fx-swivel-running'");
            InGameAssert.IsTrue(ReStockPatchFxIndex.HasAuthoredEffectsFor("liquidEngine2.v2"),
                "HasAuthoredEffectsFor must be true for Swivel under ReStock");
        }

        [InGameTest(Category = "ReStockCompat", Scene = GameScenes.FLIGHT,
            Description = "ReStock alone (no Waterfall): Swivel post-MM EFFECTS scan is NON-empty, normal path serves the ghost")]
        public void ReStockAloneNormalScanNonEmpty()
        {
            if (!IsReStockInstalled())
            {
                InGameAssert.Skip("ReStock not installed; rerun after the CKAN install.");
                return;
            }
            if (WaterfallCompat.IsWaterfallAssemblyLoaded())
            {
                InGameAssert.Skip("Waterfall installed; the ReStock-alone contract is N/A here.");
                return;
            }

            AvailablePart swivel = FindSwivel();
            InGameAssert.IsNotNull(swivel, "Swivel part not found in PartLoader");
            InGameAssert.IsFalse(WaterfallCompat.PartHasWaterfallModule(swivel.partPrefab),
                "gate must be closed without Waterfall");
            int postMmEntries = CountEngineParticleEntries(
                swivel.partConfig?.GetNode("EFFECTS"), moduleGroups: null);
            InGameAssert.IsGreaterThan(postMmEntries, 0,
                "ReStock-alone Swivel post-MM EFFECTS has no particle entries; " +
                "the normal scan path would not render the ReStock plume");
        }

        [InGameTest(Category = "ReStockCompat", Scene = GameScenes.FLIGHT,
            Description = "ReStock + Waterfall: Swivel post-MM scan empty, ReStock-authored EFFECTS yields ReStock/FX model entries")]
        public void SwivelReStockRecoveryYieldsReStockModels()
        {
            if (!IsReStockInstalled())
            {
                InGameAssert.Skip("ReStock not installed; rerun after the CKAN install.");
                return;
            }
            if (!WaterfallCompat.IsWaterfallAssemblyLoaded())
            {
                InGameAssert.Skip("Waterfall not installed; the recovery path is unreachable.");
                return;
            }

            AvailablePart swivel = FindSwivel();
            InGameAssert.IsNotNull(swivel, "Swivel part not found in PartLoader");
            if (!WaterfallCompat.PartHasWaterfallModule(swivel.partPrefab))
            {
                InGameAssert.Skip("Swivel has no ModuleWaterfallFX (config pack like SWE not installed).");
                return;
            }

            int postMmEntries = CountEngineParticleEntries(
                swivel.partConfig?.GetNode("EFFECTS"), moduleGroups: null);
            InGameAssert.AreEqual(0, postMmEntries,
                "post-MM EFFECTS unexpectedly still has particle entries; recovery would not engage");

            InGameAssert.IsTrue(ReStockPatchFxIndex.TryGetForPart(swivel.name, out var entry) &&
                entry.EffectsNode != null,
                "no ReStock-authored EFFECTS in the index for Swivel");

            HashSet<string> moduleGroups = entry.EngineModuleEffectNames.Count > 0
                ? entry.EngineModuleEffectNames[0]
                : null;
            var modelFx = new List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName)>();
            var prefabFx = new List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName)>();
            ScanEffects(entry.EffectsNode, moduleGroups, modelFx, prefabFx);

            InGameAssert.IsGreaterThan(modelFx.Count, 0,
                "ReStock-authored EFFECTS yielded no model entries for Swivel");
            bool anyReStockModel = false;
            for (int i = 0; i < modelFx.Count && !anyReStockModel; i++)
                anyReStockModel = modelFx[i].modelName.StartsWith("ReStock/", System.StringComparison.OrdinalIgnoreCase);
            InGameAssert.IsTrue(anyReStockModel,
                "no recovered model entry references a ReStock/FX model; the look would be stock, not ReStock");
        }

        [InGameTest(Category = "ReStockCompat", Scene = GameScenes.FLIGHT,
            Description = "ReStock + Waterfall: RCS block ReStock running group yields ReStock/FX thruster definitions")]
        public void RcsBlockReStockRecoveryYieldsReStockModels()
        {
            if (!IsReStockInstalled())
            {
                InGameAssert.Skip("ReStock not installed; rerun after the CKAN install.");
                return;
            }
            if (!WaterfallCompat.IsWaterfallAssemblyLoaded())
            {
                InGameAssert.Skip("Waterfall not installed; the recovery path is unreachable.");
                return;
            }

            AvailablePart rcsBlock = PartLoader.getPartInfoByName("RCSBlock.v2");
            InGameAssert.IsNotNull(rcsBlock, "RCS block part not found in PartLoader");
            if (!WaterfallCompat.PartHasWaterfallModule(rcsBlock.partPrefab))
            {
                InGameAssert.Skip("RCS block has no ModuleWaterfallFX (config pack like SWE not installed).");
                return;
            }

            InGameAssert.IsTrue(ReStockPatchFxIndex.TryGetForPart(rcsBlock.name, out var entry) &&
                entry.EffectsNode != null,
                "no ReStock-authored EFFECTS in the index for the RCS block");

            var pristine = PristinePartFxResolver.GetForPart(rcsBlock.name, rcsBlock.configFileFullName);
            string running = ReStockPatchFxIndex.ResolveRcsRunningGroupName(entry, pristine, 0);
            ConfigNode group = entry.EffectsNode.GetNode(running);
            InGameAssert.IsNotNull(group, $"ReStock RCS group '{running}' missing from authored EFFECTS");

            var fxDefinitions = new List<FxModelDefinition>();
            FloatCurve emission = null, speed = null;
            GhostVisualBuilder.ScanRcsEffectGroupModelNodes(group, fxDefinitions, ref emission, ref speed);
            InGameAssert.IsGreaterThan(fxDefinitions.Count, 0,
                "ReStock RCS running group yielded no MODEL_MULTI_PARTICLE definitions");
            bool anyReStockModel = false;
            for (int i = 0; i < fxDefinitions.Count && !anyReStockModel; i++)
                anyReStockModel = fxDefinitions[i].modelName != null &&
                    fxDefinitions[i].modelName.StartsWith("ReStock/", System.StringComparison.OrdinalIgnoreCase);
            InGameAssert.IsTrue(anyReStockModel,
                "no recovered RCS definition references a ReStock/FX model");
        }

        [InGameTest(Category = "ReStockCompat", Scene = GameScenes.FLIGHT,
            Description = "ReStock installed: RAPIER per-ordinal recovery yields non-empty, distinct group sets for both modes")]
        public void RapierBothOrdinalsRecoverDistinctGroups()
        {
            if (!IsReStockInstalled())
            {
                InGameAssert.Skip("ReStock not installed; rerun after the CKAN install.");
                return;
            }

            AvailablePart rapier = PartLoader.getPartInfoByName("RAPIER");
            InGameAssert.IsNotNull(rapier, "RAPIER part not found in PartLoader");
            InGameAssert.IsTrue(ReStockPatchFxIndex.TryGetForPart("RAPIER", out var entry) &&
                entry.EffectsNode != null,
                "no ReStock-authored EFFECTS in the index for RAPIER");

            // The RAPIER patch authors no engine-module nodes, so the per-ordinal filter
            // must come from the PRISTINE stock cfg (whose group names ReStock kept).
            var pristine = PristinePartFxResolver.GetForPart(rapier.name, rapier.configFileFullName);
            InGameAssert.IsTrue(pristine != null && pristine.Found,
                "pristine RAPIER cfg not found; the per-ordinal fallback source is missing");

            var perOrdinalGroups = new List<string>[2];
            for (int midx = 0; midx < 2; midx++)
            {
                InGameAssert.IsTrue(ReStockPatchFxIndex.TryResolveEngineGroupFilter(
                    entry, pristine, midx, 2, out HashSet<string> names, out string source),
                    $"RAPIER midx={midx}: group filter unresolvable (source chain empty); " +
                    "recovery would skip this mode's plume");
                InGameAssert.IsTrue(names != null && names.Count > 0,
                    $"RAPIER midx={midx}: expected a named group filter, got all-groups " +
                    $"(source={source}); modes would bleed FX onto each other");

                var modelFx = new List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName)>();
                var prefabFx = new List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName)>();
                ScanEffects(entry.EffectsNode, names, modelFx, prefabFx);
                InGameAssert.IsGreaterThan(modelFx.Count + prefabFx.Count, 0,
                    $"RAPIER midx={midx}: ReStock EFFECTS yielded no entries for groups " +
                    $"[{string.Join(",", new List<string>(names).ToArray())}]");

                perOrdinalGroups[midx] = new List<string>();
                for (int i = 0; i < modelFx.Count; i++)
                    perOrdinalGroups[midx].Add(modelFx[i].groupName);
                for (int i = 0; i < prefabFx.Count; i++)
                    perOrdinalGroups[midx].Add(prefabFx[i].groupName);
            }

            // The two modes must not resolve to the same group set (distinct plumes).
            bool anyOverlap = false;
            for (int i = 0; i < perOrdinalGroups[0].Count && !anyOverlap; i++)
                anyOverlap = perOrdinalGroups[1].Contains(perOrdinalGroups[0][i]);
            InGameAssert.IsFalse(anyOverlap,
                "RAPIER mode 0 and mode 1 recovered overlapping EFFECTS groups (mode bleed)");
        }

        [InGameTest(Category = "ReStockCompat", Scene = GameScenes.FLIGHT,
            Description = "ReStock installed: stock per-part FX tunings stand down for every ReStock-covered tuned part")]
        public void TuningStanddownEngagesForCoveredTunedParts()
        {
            if (!IsReStockInstalled())
            {
                InGameAssert.Skip("ReStock not installed; rerun after the CKAN install.");
                return;
            }

            // Every part name with a hardcoded stock tuning block that ReStock covers.
            // HasAuthoredEffectsFor(true) is exactly the condition that stands the
            // tunings down in TryBuildEngineFX.
            string[] coveredTunedParts =
            {
                "microEngine.v2", "radialEngineMini.v2",        // Ant/Spider supplement
                "MassiveBooster",                                // Kickback forced plume
                "omsEngine",                                     // Puff supplement
                "Size3AdvancedEngine", "Size3EngineCluster",     // Rhino plume + heavy white flame
                "Size2LFB.v2", "Size2LFB",                       // Twin-Boar white flame
                "SSME",                                          // Vector blue flame
                "RAPIER",                                        // RAPIER plume forcing
                "LaunchEscapeSystem"                             // LES particle-FX skip
            };
            var missing = new List<string>();
            for (int i = 0; i < coveredTunedParts.Length; i++)
            {
                if (!ReStockPatchFxIndex.HasAuthoredEffectsFor(coveredTunedParts[i]))
                    missing.Add(coveredTunedParts[i]);
            }
            InGameAssert.IsTrue(missing.Count == 0,
                $"standdown will NOT engage for {missing.Count} ReStock-covered tuned parts: " +
                string.Join(", ", missing.ToArray()));

            // And the deprecated v1 parts ReStock does not cover keep their tunings.
            InGameAssert.IsFalse(ReStockPatchFxIndex.HasAuthoredEffectsFor("microEngine"),
                "v1 Ant is not ReStock-covered; its tuning must NOT stand down");
        }

        [InGameTest(Category = "ReStockCompat", Scene = GameScenes.FLIGHT,
            Description = "ALL indexed ReStock parts: every authored EFFECTS asset resolves exactly (model prefabs loaded, particle prefabs resolvable)")]
        public void AllReStockPatchFxResolveExactly()
        {
            if (!IsReStockInstalled())
            {
                InGameAssert.Skip("ReStock not installed; rerun after the CKAN install.");
                return;
            }

            int entries = 0, effectsBearing = 0, partsMissing = 0, modelEntries = 0, prefabEntries = 0;
            var failures = new List<string>();

            foreach (var kvp in ReStockPatchFxIndex.Entries())
            {
                entries++;
                var entry = kvp.Value;
                if (entry.EffectsNode == null)
                    continue;
                effectsBearing++;

                // RestockIgnore'd or otherwise absent parts: count, don't fail.
                if (PartLoader.getPartInfoByName(kvp.Key) == null)
                {
                    partsMissing++;
                    continue;
                }

                ConfigNode[] groups = entry.EffectsNode.GetNodes();
                for (int g = 0; g < groups.Length; g++)
                {
                    string[] modelTypes = { "MODEL_MULTI_PARTICLE_PERSIST", "MODEL_MULTI_PARTICLE", "MODEL_PARTICLE" };
                    for (int mt = 0; mt < modelTypes.Length; mt++)
                    {
                        ConfigNode[] modelNodes = groups[g].GetNodes(modelTypes[mt]);
                        for (int m = 0; m < modelNodes.Length; m++)
                        {
                            string modelName = modelNodes[m].GetValue("modelName");
                            if (string.IsNullOrEmpty(modelName))
                                continue;
                            modelEntries++;
                            if (GameDatabase.Instance.GetModelPrefab(modelName) == null)
                                failures.Add($"{kvp.Key}: EFFECTS model '{modelName}' not loaded");
                        }
                    }

                    ConfigNode[] prefabNodes = groups[g].GetNodes("PREFAB_PARTICLE");
                    for (int p = 0; p < prefabNodes.Length; p++)
                    {
                        if (!EngineFxBuilder.TryReadPrefabParticleConfigEntry(
                            prefabNodes[p], groups[g].name, out var prefabEntry))
                            continue;
                        prefabEntries++;
                        if (GhostVisualBuilder.FindFxPrefabIncludingBuiltinEffects(
                                prefabEntry.prefabName, out bool _) == null)
                            failures.Add($"{kvp.Key}: EFFECTS prefab '{prefabEntry.prefabName}' unresolvable");
                    }
                }
            }

            ParsekLog.Info("ReStockCompat",
                $"resolve sweep: entries={entries} effectsBearing={effectsBearing} " +
                $"partsMissing={partsMissing} modelEntries={modelEntries} " +
                $"prefabEntries={prefabEntries} failures={failures.Count}");

            InGameAssert.IsGreaterThan(effectsBearing, 0,
                "index has no EFFECTS-bearing entries despite ReStock being installed");
            if (failures.Count > 0)
            {
                int show = failures.Count < 8 ? failures.Count : 8;
                InGameAssert.Fail(
                    $"{failures.Count} ReStock-authored FX assets do not resolve exactly; first {show}: " +
                    string.Join("; ", failures.GetRange(0, show).ToArray()));
            }
        }

        private static void ScanEffects(
            ConfigNode effectsNode,
            HashSet<string> moduleGroups,
            List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName)> modelFx,
            List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName)> prefabFx)
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
                effectGroups, effectGroupNames, modelFx, ref emission, ref speed);
            EngineFxBuilder.ScanEffectsPrefabParticleEntries(
                effectGroups, effectGroupNames, prefabFx);
        }

        /// <summary>
        /// Counts particle entries an engine FX scan would find in the given EFFECTS node,
        /// optionally filtered to the given group names (null = all groups).
        /// </summary>
        private static int CountEngineParticleEntries(ConfigNode effectsNode, HashSet<string> moduleGroups)
        {
            if (effectsNode == null)
                return 0;

            var modelFx = new List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName)>();
            var prefabFx = new List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName)>();
            ScanEffects(effectsNode, moduleGroups, modelFx, prefabFx);
            return modelFx.Count + prefabFx.Count;
        }
    }
}
