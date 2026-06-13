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
        public void TryReadModelFxConfigEntry_MalformedRotationStillEnablesKnownModelFallback()
        {
            var modelNode = new ConfigNode("MODEL_PARTICLE");
            modelNode.AddValue("transformName", "thrustTransform");
            modelNode.AddValue("modelName", "Squad/FX/Monoprop_small");
            modelNode.AddValue("localRotation", "bad-rotation");

            bool parsed = EngineFxBuilder.TryReadModelFxConfigEntry(
                "MODEL_PARTICLE",
                modelNode,
                "running",
                out EngineFxBuilder.ModelFxConfigEntry entry);

            Assert.True(parsed);
            Assert.Equal("bad-rotation", entry.rawLocalRotation);
            Assert.True(entry.useFallbackRotation);
            AssertVector3Close(new Vector3(-90f, 0f, 0f), entry.fallbackEuler);
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
            AssertHasEngineFxLog("using localPosition fallback");
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

        // ---- world-space emitter velocity floor (ReStock SRB smoke models) ---------

        [Fact]
        public void WorldSpaceEmitterFloor_ZeroVelocity_FlowsAlongExhaustAxis()
        {
            // restock-fx-srb-smoke-3 shape: world-space, exactly zero baked velocity.
            Assert.True(GhostVisualBuilder.TryComputeWorldSpaceEmitterVelocityFloor(
                useWorldSpace: true, localVelocity: Vector3.zero,
                exhaustAxisEmitterLocal: new Vector3(0f, 0f, 1f), out Vector3 floored));
            AssertVector3Close(new Vector3(0f, 0f, GhostVisualBuilder.WorldSpaceEmitterFloorSpeed), floored);
        }

        [Fact]
        public void WorldSpaceEmitterFloor_SlowVelocity_ExhaustAxisWinsOverAuthoredAxis()
        {
            // The asset's authored axis is rig-relative and differs across ReStock's
            // SRBs (Clydesdale's fxTransformSmoke is oriented differently than
            // Hammer's, which made an authored-axis floor squirt sideways). The
            // engine's exhaust axis is the ground truth.
            Assert.True(GhostVisualBuilder.TryComputeWorldSpaceEmitterVelocityFloor(
                useWorldSpace: true, localVelocity: new Vector3(0f, -1f, 0f),
                exhaustAxisEmitterLocal: new Vector3(1f, 0f, 0f), out Vector3 floored));
            AssertVector3Close(new Vector3(GhostVisualBuilder.WorldSpaceEmitterFloorSpeed, 0f, 0f), floored);
        }

        [Fact]
        public void WorldSpaceEmitterFloor_DegenerateAxis_FallsBackMinusY()
        {
            Assert.True(GhostVisualBuilder.TryComputeWorldSpaceEmitterVelocityFloor(
                useWorldSpace: true, localVelocity: Vector3.zero,
                exhaustAxisEmitterLocal: Vector3.zero, out Vector3 floored));
            AssertVector3Close(Vector3.down * GhostVisualBuilder.WorldSpaceEmitterFloorSpeed, floored);
        }

        [Fact]
        public void WorldSpaceEmitterFloor_FastWorldSpace_Unchanged()
        {
            Assert.False(GhostVisualBuilder.TryComputeWorldSpaceEmitterVelocityFloor(
                useWorldSpace: true, localVelocity: new Vector3(0f, 0f, 14f),
                exhaustAxisEmitterLocal: Vector3.down, out Vector3 floored));
            AssertVector3Close(new Vector3(0f, 0f, 14f), floored);
        }

        [Fact]
        public void WorldSpaceEmitterFloor_LocalSpace_NeverFloored()
        {
            // Every stock engine FX emitter is local-space (the stock no-op guarantee);
            // even a zero-velocity local-space emitter must stay untouched.
            Assert.False(GhostVisualBuilder.TryComputeWorldSpaceEmitterVelocityFloor(
                useWorldSpace: false, localVelocity: Vector3.zero,
                exhaustAxisEmitterLocal: Vector3.down, out Vector3 floored));
            AssertVector3Close(Vector3.zero, floored);
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
            // per-part overrides (Rhino 2.7x, Mainsail 2.0x, default 1.5x) must not
            // inflate ReStock-authored model FX.
            Assert.Equal(1f, EngineFxBuilder.ResolveModelFxSizeBoost(true, "Size3AdvancedEngine"));
            Assert.Equal(1f, EngineFxBuilder.ResolveModelFxSizeBoost(true, "Mite"));
            Assert.Equal(GhostVisualBuilder.ResolveEngineFxSizeBoost("Size3AdvancedEngine"),
                EngineFxBuilder.ResolveModelFxSizeBoost(false, "Size3AdvancedEngine"));
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
