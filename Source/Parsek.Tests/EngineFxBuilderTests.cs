using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class EngineFxBuilderTests
    {
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
        }

        [Fact]
        public void TryGetFxModelFallbackEuler_TrimsAndMatchesKnownModelsCaseInsensitively()
        {
            bool found = EngineFxBuilder.TryGetFxModelFallbackEuler(
                "  squad/fx/monoprop_small  ",
                out Vector3 fallbackEuler);

            Assert.True(found);
            AssertVector3Close(new Vector3(-90f, 0f, 0f), fallbackEuler);
        }

        [Fact]
        public void TryReadModelFxConfigEntry_ParsesOffsetAndFallbackRotationMetadata()
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
            Assert.True(entry.useFallbackRotation);
            AssertVector3Close(new Vector3(-90f, 0f, 0f), entry.fallbackEuler);
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
            Assert.False(entry.useFallbackRotation);
            Assert.Equal("?", entry.groupName);
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
        public void TryReadPrefabParticleConfigEntry_UsesLocalPositionAsOffsetFallback()
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
            AssertVector3Close(new Vector3(0f, 0f, 0.2f), entry.localOffset);
            Assert.Equal("0,45,0", entry.rawLocalRotation);
            Assert.Equal("running", entry.groupName);
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

        private static void AssertVector3Close(Vector3 expected, Vector3 actual, float epsilon = 1e-4f)
        {
            Assert.InRange(Mathf.Abs(expected.x - actual.x), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.y - actual.y), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.z - actual.z), 0f, epsilon);
        }
    }
}
