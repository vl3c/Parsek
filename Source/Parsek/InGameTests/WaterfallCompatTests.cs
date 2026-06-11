using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Runtime tests for the Waterfall pristine-config ghost FX fallback.
    /// On a stock install (no Waterfall) the gate-closed test runs and the rest skip;
    /// after a CKAN install of Waterfall + Stock Waterfall Effects the fallback tests
    /// become meaningful automatically.
    /// </summary>
    public class WaterfallCompatTests
    {
        /// <summary>Swivel: modern v2 name first, pre-v2 deprecated name as fallback.</summary>
        private static AvailablePart FindSwivel()
        {
            return PartLoader.getPartInfoByName("liquidEngine2.v2")
                ?? PartLoader.getPartInfoByName("liquidEngine2");
        }

        private static AvailablePart FindRcsBlock()
        {
            return PartLoader.getPartInfoByName("RCSBlock.v2")
                ?? PartLoader.getPartInfoByName("RCSBlock");
        }

        [InGameTest(Category = "WaterfallCompat", Scene = GameScenes.FLIGHT,
            Description = "Stock install: Waterfall gate is closed for a stock engine (fallback unreachable)")]
        public void WaterfallGateClosedOnStockInstall()
        {
            if (WaterfallCompat.IsWaterfallAssemblyLoaded())
            {
                InGameAssert.Skip("Waterfall is installed; the stock gate-closed contract is N/A.");
                return;
            }

            AvailablePart swivel = FindSwivel();
            InGameAssert.IsNotNull(swivel, "Swivel part not found in PartLoader");
            InGameAssert.IsFalse(WaterfallCompat.PartHasWaterfallModule(swivel.partPrefab),
                "Gate must be closed on a stock install (no ModuleWaterfallFX on Swivel prefab)");
        }

        [InGameTest(Category = "WaterfallCompat", Scene = GameScenes.FLIGHT,
            Description = "Waterfall installed: Swivel (legacy-fx part) gate open, post-MM scan empty, pristine legacy keys recovered and resolvable")]
        public void SwivelPristineFallbackRecoversLegacyFx()
        {
            if (!WaterfallCompat.IsWaterfallAssemblyLoaded())
            {
                InGameAssert.Skip("Waterfall not installed; rerun after the CKAN install.");
                return;
            }

            AvailablePart swivel = FindSwivel();
            InGameAssert.IsNotNull(swivel, "Swivel part not found in PartLoader");
            if (!WaterfallCompat.PartHasWaterfallModule(swivel.partPrefab))
            {
                InGameAssert.Skip("Swivel has no ModuleWaterfallFX (config pack like SWE not installed).");
                return;
            }

            // Post-MM scan of the live partConfig must be empty (the pack stripped particles).
            int postMmEntries = CountEngineParticleEntries(
                swivel.partConfig?.GetNode("EFFECTS"), moduleGroups: null);
            InGameAssert.AreEqual(0, postMmEntries,
                "post-MM EFFECTS unexpectedly still has particle entries; fallback would not engage");

            // The real stock Swivel is a LEGACY part: fx_* keys + plain ModuleEngines, no
            // EFFECTS node. Pristine recovery must surface the legacy prefab names.
            var pristine = PristinePartFxResolver.GetForPart(swivel.name, swivel.configFileFullName);
            InGameAssert.IsTrue(pristine.Found, $"pristine PART node not found (path='{swivel.configFileFullName}')");
            InGameAssert.IsGreaterThan(pristine.LegacyFxPrefabNames.Count, 0,
                "pristine legacy fx_* keys not recovered for Swivel");
            InGameAssert.IsTrue(pristine.LegacyFxPrefabNames.Contains("fx_exhaustFlame_blue"),
                "expected fx_exhaustFlame_blue among Swivel's pristine legacy keys");

            // Each synthesized name must resolve to a stock FX prefab through the candidate
            // cascade (exact, then size-suffix-stripped family base), or the legacy
            // synthesis drops it. The prefab cache is fed by parts whose post-MM config
            // still carries legacy fx_* keys (e.g. unpatched SRBs), which SWE might also
            // strip in future versions.
            for (int i = 0; i < pristine.LegacyFxPrefabNames.Count; i++)
            {
                string wanted = pristine.LegacyFxPrefabNames[i];
                var candidates = PristinePartFxResolver.BuildLegacyFxNameCandidates(wanted);
                bool resolved = false;
                for (int c = 0; c < candidates.Count && !resolved; c++)
                    resolved = GhostVisualBuilder.FindFxPrefab(candidates[c]) != null;
                InGameAssert.IsTrue(resolved,
                    $"pristine legacy FX prefab '{wanted}' unresolvable through the cascade " +
                    "(no surviving donor part in this install; ghost degrades to white flame)");
            }
        }

        [InGameTest(Category = "WaterfallCompat", Scene = GameScenes.FLIGHT,
            Description = "Waterfall installed: Mainsail legacy flame resolves via the candidate cascade (or white-flame guarantee)")]
        public void MainsailLegacyFlameCascadeResolvable()
        {
            if (!WaterfallCompat.IsWaterfallAssemblyLoaded())
            {
                InGameAssert.Skip("Waterfall not installed; rerun after the CKAN install.");
                return;
            }

            AvailablePart mainsail = PartLoader.getPartInfoByName("liquidEngineMainsail.v2")
                ?? PartLoader.getPartInfoByName("liquidEngine1-2");
            InGameAssert.IsNotNull(mainsail, "Mainsail part not found in PartLoader");
            if (!WaterfallCompat.PartHasWaterfallModule(mainsail.partPrefab))
            {
                InGameAssert.Skip("Mainsail has no ModuleWaterfallFX (config pack like SWE not installed).");
                return;
            }

            var pristine = PristinePartFxResolver.GetForPart(mainsail.name, mainsail.configFileFullName);
            InGameAssert.IsTrue(pristine.Found, $"pristine PART node not found (path='{mainsail.configFileFullName}')");
            InGameAssert.IsGreaterThan(pristine.LegacyFxPrefabNames.Count, 0,
                "pristine legacy fx_* keys not recovered for Mainsail");

            // The exact size-suffixed flame prefabs (fx_exhaustFlame_yellow_medium/_mini)
            // lose their donor parts under SWE; the cascade must reach a surviving family
            // prefab (fx_exhaustFlame_yellow lives on unpatched SRBs). If even the cascade
            // fails, the white-flame guarantee must hold.
            bool anyFlameResolved = false;
            for (int i = 0; i < pristine.LegacyFxPrefabNames.Count; i++)
            {
                string wanted = pristine.LegacyFxPrefabNames[i];
                if (wanted.IndexOf("flame", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                var candidates = PristinePartFxResolver.BuildLegacyFxNameCandidates(wanted);
                for (int c = 0; c < candidates.Count && !anyFlameResolved; c++)
                    anyFlameResolved = GhostVisualBuilder.FindFxPrefab(candidates[c]) != null;
            }

            if (!anyFlameResolved)
            {
                InGameAssert.IsNotNull(GhostVisualBuilder.FindFxPrefab("fx_exhaustFlame_white"),
                    "no Mainsail legacy flame resolves via the cascade AND the white-flame " +
                    "guarantee prefab is unresolvable; Mainsail ghosts would be flame-less");
            }
        }

        [InGameTest(Category = "WaterfallCompat", Scene = GameScenes.FLIGHT,
            Description = "Waterfall installed: Spark (EFFECTS part) pristine recovery finds particle entries")]
        public void SparkPristineFallbackRecoversEffectsFx()
        {
            if (!WaterfallCompat.IsWaterfallAssemblyLoaded())
            {
                InGameAssert.Skip("Waterfall not installed; rerun after the CKAN install.");
                return;
            }

            AvailablePart spark = PartLoader.getPartInfoByName("liquidEngineMini.v2")
                ?? PartLoader.getPartInfoByName("liquidEngineMini");
            InGameAssert.IsNotNull(spark, "Spark part not found in PartLoader");
            if (!WaterfallCompat.PartHasWaterfallModule(spark.partPrefab))
            {
                InGameAssert.Skip("Spark has no ModuleWaterfallFX (config pack like SWE not installed).");
                return;
            }

            int postMmEntries = CountEngineParticleEntries(
                spark.partConfig?.GetNode("EFFECTS"), moduleGroups: null);
            InGameAssert.AreEqual(0, postMmEntries,
                "post-MM EFFECTS unexpectedly still has particle entries; fallback would not engage");

            // The real stock Spark has pristine EFFECTS with PREFAB_PARTICLE
            // (fx_exhaustFlame_yellow_tiny_Z on thrustTransform).
            var pristine = PristinePartFxResolver.GetForPart(spark.name, spark.configFileFullName);
            InGameAssert.IsTrue(pristine.Found, $"pristine PART node not found (path='{spark.configFileFullName}')");
            InGameAssert.IsNotNull(pristine.EffectsNode, "pristine EFFECTS node missing for Spark");

            HashSet<string> moduleGroups = pristine.EngineModuleEffectNames.Count > 0
                ? pristine.EngineModuleEffectNames[0]
                : null;
            int pristineEntries = CountEngineParticleEntries(pristine.EffectsNode, moduleGroups);
            InGameAssert.IsGreaterThan(pristineEntries, 0,
                "pristine EFFECTS yielded no particle entries for Spark");
        }

        [InGameTest(Category = "WaterfallCompat", Scene = GameScenes.FLIGHT,
            Description = "Waterfall installed: RCS block pristine running group has MODEL_MULTI_PARTICLE entries")]
        public void RcsBlockPristineFallbackRecoversThrusterFx()
        {
            if (!WaterfallCompat.IsWaterfallAssemblyLoaded())
            {
                InGameAssert.Skip("Waterfall not installed; rerun after the CKAN install.");
                return;
            }

            AvailablePart rcsBlock = FindRcsBlock();
            InGameAssert.IsNotNull(rcsBlock, "RCS block part not found in PartLoader");
            if (!WaterfallCompat.PartHasWaterfallModule(rcsBlock.partPrefab))
            {
                InGameAssert.Skip("RCS block has no ModuleWaterfallFX (config pack like SWE not installed).");
                return;
            }

            var pristine = PristinePartFxResolver.GetForPart(rcsBlock.name, rcsBlock.configFileFullName);
            InGameAssert.IsTrue(pristine.Found, $"pristine PART node not found (path='{rcsBlock.configFileFullName}')");
            InGameAssert.IsNotNull(pristine.EffectsNode, "pristine EFFECTS node missing");

            string running = pristine.RcsRunningEffectNames.Count > 0
                ? pristine.RcsRunningEffectNames[0]
                : "running";
            ConfigNode group = pristine.EffectsNode.GetNode(running);
            InGameAssert.IsNotNull(group, $"pristine RCS group '{running}' missing");

            var fxDefinitions = new List<FxModelDefinition>();
            FloatCurve emission = null, speed = null;
            GhostVisualBuilder.ScanRcsEffectGroupModelNodes(group, fxDefinitions, ref emission, ref speed);
            InGameAssert.IsGreaterThan(fxDefinitions.Count, 0,
                "pristine RCS running group yielded no MODEL_MULTI_PARTICLE definitions");
        }

        [InGameTest(Category = "WaterfallCompat", Scene = GameScenes.FLIGHT,
            Description = "Waterfall installed: best-effort white-flame prefab still resolvable")]
        public void WhiteFlameLastResortPrefabResolvable()
        {
            if (!WaterfallCompat.IsWaterfallAssemblyLoaded())
            {
                InGameAssert.Skip("Waterfall not installed; rerun after the CKAN install.");
                return;
            }

            GameObject prefab = GhostVisualBuilder.FindFxPrefab("fx_exhaustFlame_white");
            InGameAssert.IsNotNull(prefab,
                "fx_exhaustFlame_white prefab unresolvable; white-flame last resort would be a no-op " +
                "(known risk on installs without Making History SRB legacy keys)");
        }

        /// <summary>
        /// Counts particle entries an engine FX scan would find in the given EFFECTS node,
        /// optionally filtered to the given group names (null = all groups).
        /// </summary>
        private static int CountEngineParticleEntries(ConfigNode effectsNode, HashSet<string> moduleGroups)
        {
            if (effectsNode == null)
                return 0;

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

            var modelFx = new List<(string, string, string, Vector3, Quaternion, string)>();
            var prefabFx = new List<(string, string, Vector3, Quaternion, bool, string)>();
            FloatCurve emission = null, speed = null;
            EngineFxBuilder.ScanEffectsModelFxEntries(
                effectGroups, effectGroupNames, modelFx, ref emission, ref speed);
            EngineFxBuilder.ScanEffectsPrefabParticleEntries(
                effectGroups, effectGroupNames, prefabFx);
            return modelFx.Count + prefabFx.Count;
        }
    }
}
