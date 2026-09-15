using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// B3 ghost-state gaps: the recorder's deployable pose-animation gate
    /// (GS6-DEPLOYABLE-NO-RESOLVED-VISUAL-solarPanels5) and the colour-changer
    /// config-source resolver (GS6-GHOST-HAS-NO-COLORCHANGER-STATE).
    ///
    /// Both surfaces under test are PURE - a string predicate and a ConfigNode walk - which
    /// is the whole reason they are the shared convention point rather than a condition each
    /// end spells out for itself.
    /// </summary>
    [Collection("Sequential")]
    public class GhostStateGapsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostStateGapsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        #region Deployable pose-animation gate

        [Fact]
        public void DeployableHasNoPoseAnimation_EmptyAnimationName_IsScreened()
        {
            // The OX-STAT (`solarPanels5`) shape: a ModuleDeployablePart permanently EXTENDED
            // with no animationName, so nothing samples a stowed/deployed pose pair for it and
            // the ghost reports `no-resolved-visual` on every replay.
            Assert.True(PartStateSeeder.DeployableHasNoPoseAnimation(null));
            Assert.True(PartStateSeeder.DeployableHasNoPoseAnimation(string.Empty));
        }

        [Fact]
        public void DeployableHasNoPoseAnimation_RealAnimationName_IsNotScreened()
        {
            // The mediumDishAntenna shape: a real clip, sampled into a pose pair, applied=1.
            Assert.False(PartStateSeeder.DeployableHasNoPoseAnimation("deploy"));
            Assert.False(PartStateSeeder.DeployableHasNoPoseAnimation("SolarPanelDeploy"));
        }

        [Fact]
        public void DeployableHasNoPoseAnimation_WhitespaceName_IsNotScreened()
        {
            // Deliberate: the predicate is exactly `string.IsNullOrEmpty`, which is exactly what
            // GhostVisualBuilder.SampleDeployableStates screened with before the two ends shared
            // one predicate. A whitespace name is a broken config, not a static panel, and the
            // builder would still try to sample it - so the recorder must too, or the pair drifts.
            Assert.False(PartStateSeeder.DeployableHasNoPoseAnimation(" "));
        }

        #endregion

        #region ColorChanger config-source resolution

        private static ConfigNode PersistedColorChangerPartNode()
        {
            // Byte-shaped after a real persisted node, read off
            // harness/fixtures/saves/b1-pad-craft/persistent.sfs: isEnabled / animState /
            // stagingEnabled and NOTHING config-only. This is what every ghost sidecar carries.
            var partNode = new ConfigNode("PART");
            partNode.AddValue("name", "mk1-3pod");
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleColorChanger");
            module.AddValue("isEnabled", "True");
            module.AddValue("animState", "False");
            module.AddValue("stagingEnabled", "True");
            partNode.AddNode(module);
            return partNode;
        }

        private static ConfigNode PrefabColorChangerConfig(string shaderProperty)
        {
            var prefabConfig = new ConfigNode("PART");
            prefabConfig.AddValue("name", "mk1-3pod");
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleColorChanger");
            module.AddValue("shaderProperty", shaderProperty);
            module.AddValue("toggleInFlight", "true");
            prefabConfig.AddNode(module);
            return prefabConfig;
        }

        [Fact]
        public void ResolveColorChangerConfigNodes_PersistedSnapshot_FallsBackToPrefab()
        {
            // THE DEFECT, reproduced: before the fallback the persisted node's empty
            // shaderProperty made the builder `continue`, so no flight ghost ever carried a
            // colorChangerInfos dictionary and every LightOn/LightOff apply read no-family-state.
            var resolved = GhostVisualBuilder.ResolveColorChangerConfigNodes(
                PersistedColorChangerPartNode(), PrefabColorChangerConfig("_EmissiveColor"));

            Assert.Single(resolved);
            Assert.Equal("_EmissiveColor", resolved[0].GetValue("shaderProperty"));
            Assert.Equal("true", resolved[0].GetValue("toggleInFlight"));
        }

        [Fact]
        public void ResolveColorChangerConfigNodes_SnapshotCarryingConfig_KeepsTheSnapshotNode()
        {
            // THE SHOWCASE REGRESSION PIN: a synthetic snapshot node that authors the
            // config-only fields keeps winning, even when the prefab config disagrees. Showcase
            // ghost building is therefore byte-identical to its historical behaviour.
            var snapshot = new ConfigNode("PART");
            snapshot.AddValue("name", "mk1pod.v2");
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleColorChanger");
            module.AddValue("shaderProperty", "_EmissiveColor");
            module.AddValue("toggleInFlight", "True");
            snapshot.AddNode(module);

            var resolved = GhostVisualBuilder.ResolveColorChangerConfigNodes(
                snapshot, PrefabColorChangerConfig("_BurnColor"));

            Assert.Single(resolved);
            Assert.Equal("_EmissiveColor", resolved[0].GetValue("shaderProperty"));
            Assert.Equal("True", resolved[0].GetValue("toggleInFlight"));
        }

        [Fact]
        public void ResolveColorChangerConfigNodes_NoColorChangerInSnapshot_UsesPrefabList()
        {
            var snapshot = new ConfigNode("PART");
            snapshot.AddValue("name", "mk1-3pod");
            var other = new ConfigNode("MODULE");
            other.AddValue("name", "ModuleCommand");
            snapshot.AddNode(other);

            var resolved = GhostVisualBuilder.ResolveColorChangerConfigNodes(
                snapshot, PrefabColorChangerConfig("_EmissiveColor"));

            Assert.Single(resolved);
            Assert.Equal("_EmissiveColor", resolved[0].GetValue("shaderProperty"));
        }

        [Fact]
        public void ResolveColorChangerConfigNodes_NeitherSideHasOne_IsEmpty()
        {
            // The part with no colour changer at all: still no infos, so the family's
            // no-family-state reading stays truthful for a craft that genuinely carries none.
            var snapshot = new ConfigNode("PART");
            snapshot.AddValue("name", "fuelTank");
            var prefab = new ConfigNode("PART");
            prefab.AddValue("name", "fuelTank");

            Assert.Empty(GhostVisualBuilder.ResolveColorChangerConfigNodes(snapshot, prefab));
            Assert.Empty(GhostVisualBuilder.ResolveColorChangerConfigNodes(null, null));
        }

        [Fact]
        public void ResolveColorChangerConfigNodes_TwoInstances_MatchByDeclarationOrder()
        {
            var snapshot = new ConfigNode("PART");
            snapshot.AddValue("name", "twoChangers");
            for (int i = 0; i < 2; i++)
            {
                var m = new ConfigNode("MODULE");
                m.AddValue("name", "ModuleColorChanger");
                m.AddValue("animState", "False");
                snapshot.AddNode(m);
            }

            var prefab = new ConfigNode("PART");
            prefab.AddValue("name", "twoChangers");
            string[] shaders = { "_EmissiveColor", "_BurnColor" };
            for (int i = 0; i < 2; i++)
            {
                var m = new ConfigNode("MODULE");
                m.AddValue("name", "ModuleColorChanger");
                m.AddValue("shaderProperty", shaders[i]);
                prefab.AddNode(m);
            }

            var resolved = GhostVisualBuilder.ResolveColorChangerConfigNodes(snapshot, prefab);

            Assert.Equal(2, resolved.Count);
            Assert.Equal("_EmissiveColor", resolved[0].GetValue("shaderProperty"));
            Assert.Equal("_BurnColor", resolved[1].GetValue("shaderProperty"));
        }

        [Fact]
        public void ResolveColorChangerConfigNodes_MoreSnapshotNodesThanPrefab_KeepsTheExtra()
        {
            // No index to fall back to: the snapshot node is returned as-is and the builder's
            // own empty-shaderProperty skip counts it, rather than an out-of-range read.
            var snapshot = new ConfigNode("PART");
            snapshot.AddValue("name", "odd");
            for (int i = 0; i < 2; i++)
            {
                var m = new ConfigNode("MODULE");
                m.AddValue("name", "ModuleColorChanger");
                m.AddValue("animState", "False");
                snapshot.AddNode(m);
            }

            var resolved = GhostVisualBuilder.ResolveColorChangerConfigNodes(
                snapshot, PrefabColorChangerConfig("_EmissiveColor"));

            Assert.Equal(2, resolved.Count);
            Assert.Equal("_EmissiveColor", resolved[0].GetValue("shaderProperty"));
            Assert.True(string.IsNullOrEmpty(resolved[1].GetValue("shaderProperty")));
        }

        [Fact]
        public void CollectColorChangerModuleNodes_IgnoresOtherModules()
        {
            var partNode = new ConfigNode("PART");
            var a = new ConfigNode("MODULE");
            a.AddValue("name", "ModuleCommand");
            partNode.AddNode(a);
            var b = new ConfigNode("MODULE");
            b.AddValue("name", "ModuleColorChanger");
            partNode.AddNode(b);

            Assert.Single(GhostVisualBuilder.CollectColorChangerModuleNodes(partNode));
            Assert.Empty(GhostVisualBuilder.CollectColorChangerModuleNodes(null));
        }

        [Fact]
        public void BuildColorChangerInfos_NoColorChangerOnEitherSide_ReturnsNull()
        {
            // The "one without produces null" half of the builder contract, at the one layer
            // reachable headlessly (the material clone below it needs live renderers).
            var partNode = new ConfigNode("PART");
            partNode.AddValue("name", "fuelTank");

            Assert.Null(GhostVisualBuilder.BuildColorChangerInfos(
                partNode, null, null, 100u, "fuelTank"));
        }

        #endregion
    }
}
