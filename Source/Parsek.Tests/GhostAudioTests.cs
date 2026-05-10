using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostAudioTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostAudioTests()
        {
            ParsekLog.ResetTestOverrides();
            GhostPlaybackLogic.ResetForTesting();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            GhostPlaybackLogic.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        private static List<Propellant> MakePropellants(params string[] names)
        {
            var result = new List<Propellant>(names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                result.Add(new Propellant { name = names[i] });
            }

            return result;
        }

        #region Propellant Classification

        [Fact]
        public void ClassifyPropellantType_NullReturnsUnknown()
        {
            Assert.Equal("Unknown", GhostAudioPresets.ClassifyPropellantType(null));
        }

        [Fact]
        public void ClassifyPropellantType_EmptyReturnsUnknown()
        {
            Assert.Equal("Unknown", GhostAudioPresets.ClassifyPropellantType(new List<Propellant>()));
        }

        [Fact]
        public void ClassifyPropellantType_IonMixedPropellants_PrefersXenonGas()
        {
            Assert.Equal("XenonGas",
                GhostAudioPresets.ClassifyPropellantType(
                    MakePropellants("ElectricCharge", "XenonGas")));
        }

        [Fact]
        public void ClassifyPropellantType_PureElectricCharge_ReturnsElectricCharge()
        {
            Assert.Equal("ElectricCharge",
                GhostAudioPresets.ClassifyPropellantType(
                    MakePropellants("ElectricCharge")));
        }

        #endregion

        #region Priority Classification

        [Theory]
        [InlineData("LiquidFuel", 1)]
        [InlineData("MonoPropellant", 1)]
        [InlineData("XenonGas", 2)]
        [InlineData("ElectricCharge", 2)]
        [InlineData("IntakeAir", 3)]
        public void ClassifyLoopedPriority_MapsPropellantsToExpectedTier(
            string propellantName, int expectedPriorityClass)
        {
            Assert.Equal((GhostAudioPriorityClass)expectedPriorityClass,
                GhostAudioPresets.ClassifyLoopedPriority(MakePropellants(propellantName)));
        }

        [Fact]
        public void ClassifyOneShotPriority_Destroyed_ReturnsExplosionTier()
        {
            Assert.Equal(
                GhostAudioPriorityClass.Explosion,
                GhostAudioPresets.ClassifyOneShotPriority(PartEventType.Destroyed));
        }

        [Fact]
        public void ComputeRuntimePriority_ExplosionAtZeroDistance_StaysAtBaselineGamePriority()
        {
            Assert.Equal(
                GhostAudioPresets.BaselineGameAudioPriority,
                GhostAudioPresets.ComputeRuntimePriority(
                    GhostAudioPriorityClass.Explosion,
                    distanceMeters: 0.0));
        }

        [Fact]
        public void ComputeRuntimePriority_DistancePenaltyLowersPriorityForFarGhosts()
        {
            int nearPriority = GhostAudioPresets.ComputeRuntimePriority(
                GhostAudioPriorityClass.RocketEngine,
                distanceMeters: DistanceThresholds.GhostAudio.RolloffMinDistanceMeters);
            int farPriority = GhostAudioPresets.ComputeRuntimePriority(
                GhostAudioPriorityClass.RocketEngine,
                distanceMeters: DistanceThresholds.GhostAudio.RolloffMaxDistanceMeters);

            Assert.True(farPriority > nearPriority);
        }

        [Fact]
        public void ComputeRuntimePriority_RocketEngineBeatsJetAtSameDistance()
        {
            double distanceMeters = 1000.0;
            int rocketPriority = GhostAudioPresets.ComputeRuntimePriority(
                GhostAudioPriorityClass.RocketEngine,
                distanceMeters);
            int jetPriority = GhostAudioPresets.ComputeRuntimePriority(
                GhostAudioPriorityClass.JetEngine,
                distanceMeters);

            Assert.True(rocketPriority < jetPriority);
        }

        [Fact]
        public void TryTriggerStockExplosionFxOrCustom_StockSucceeds_QueuesStock()
        {
            int stockCalls = 0;
            int spawnCalls = 0;
            GhostPlaybackLogic.StockExplosionFxResult? result = null;

            bool ok = GhostPlaybackLogic.TryTriggerStockExplosionFxOrCustom(
                new UnityEngine.Vector3(1f, 2f, 3f),
                power: 0.75,
                vesselLength: 12f,
                contextDescription: "manual preview \"Test\"",
                triggerStockExplosionFx: (UnityEngine.Vector3 pos, double pwr, out string failure) =>
                {
                    stockCalls++;
                    Assert.Equal(0.75, pwr);
                    failure = null;
                    return true;
                },
                spawnExplosionFx: (UnityEngine.Vector3 pos, float length) => spawnCalls++,
                recordResult: value => result = value);

            Assert.True(ok);
            Assert.Equal(GhostPlaybackLogic.StockExplosionFxResult.StockQueued, result);
            Assert.Equal(1, stockCalls);
            Assert.Equal(0, spawnCalls);
        }

        [Fact]
        public void TryTriggerStockExplosionFxOrCustom_StockFails_FallsBackToCustomVisual()
        {
            int stockCalls = 0;
            int spawnCalls = 0;
            GhostPlaybackLogic.StockExplosionFxResult? result = null;

            bool ok = GhostPlaybackLogic.TryTriggerStockExplosionFxOrCustom(
                new UnityEngine.Vector3(4f, 5f, 6f),
                power: 0.4,
                vesselLength: 9f,
                contextDescription: "ghost #3 \"Crashy\"",
                triggerStockExplosionFx: (UnityEngine.Vector3 pos, double pwr, out string failure) =>
                {
                    stockCalls++;
                    failure = "no live FXMonger instance";
                    return false;
                },
                spawnExplosionFx: (UnityEngine.Vector3 pos, float length) =>
                {
                    spawnCalls++;
                    Assert.Equal(9f, length);
                },
                recordResult: value => result = value);

            Assert.False(ok);
            Assert.Equal(GhostPlaybackLogic.StockExplosionFxResult.StockFailedCustomVisualSpawned, result);
            Assert.Equal(1, stockCalls);
            Assert.Equal(1, spawnCalls);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[ExplosionFx]") &&
                l.Contains("FXMonger.Explode did not queue stock FX for ghost #3 \"Crashy\"") &&
                l.Contains("no live FXMonger instance"));
        }

        [Fact]
        public void TryPlayIndependentExplosionOneShot_UnavailableClipReturnsFalseAndDoesNotPlay()
        {
            int playCalls = 0;

            bool result = GhostPlaybackLogic.TryPlayIndependentExplosionOneShot(
                new UnityEngine.Vector3(1f, 1f, 1f),
                atmosphereFactor: 1f,
                distanceMeters: 0.0,
                contextDescription: "KSC ghost #7 \"NoClip\"",
                resolveExplosionAudioCandidate: () => new GhostPlaybackLogic.ExplosionOneShotAudioCandidate
                {
                    canPlay = false,
                    clipPath = "sound_explosion_large",
                    failureReason = "AudioClip not found"
                },
                playExplosionAudio: (pos, candidate) => playCalls++);

            Assert.False(result);
            Assert.Equal(0, playCalls);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[GhostAudio]") &&
                l.Contains("Explosion one-shot unavailable for KSC ghost #7 \"NoClip\"") &&
                l.Contains("AudioClip not found"));
        }

        [Fact]
        public void TryPlayIndependentExplosionOneShot_PlaysWhenCandidateAvailable()
        {
            int playCalls = 0;
            UnityEngine.Vector3 playedPosition = default;

            bool result = GhostPlaybackLogic.TryPlayIndependentExplosionOneShot(
                new UnityEngine.Vector3(5f, 6f, 7f),
                atmosphereFactor: 1f,
                distanceMeters: 125.0,
                contextDescription: "KSC ghost #9 \"Fallback\"",
                resolveExplosionAudioCandidate: () => new GhostPlaybackLogic.ExplosionOneShotAudioCandidate
                {
                    canPlay = true,
                    clipPath = "sound_explosion_large",
                    clipLengthSeconds = 2.25f,
                    volume = 0.4f,
                    priority = GhostAudioPresets.BaselineGameAudioPriority
                },
                playExplosionAudio: (pos, candidate) =>
                {
                    playCalls++;
                    playedPosition = pos;
                    Assert.Equal("sound_explosion_large", candidate.clipPath);
                    Assert.Equal(0.4f, candidate.volume);
                });

            Assert.True(result);
            Assert.Equal(1, playCalls);
            Assert.Equal(5f, playedPosition.x);
            Assert.Equal(6f, playedPosition.y);
            Assert.Equal(7f, playedPosition.z);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostAudio]") &&
                l.Contains("Explosion one-shot queued for KSC ghost #9 \"Fallback\""));
        }

        [Fact]
        public void TryPlayIndependentExplosionOneShot_PlayThrows_LogsAndReturnsFalse()
        {
            bool result = GhostPlaybackLogic.TryPlayIndependentExplosionOneShot(
                new UnityEngine.Vector3(8f, 9f, 10f),
                atmosphereFactor: 1f,
                distanceMeters: 0.0,
                contextDescription: "KSC ghost #10 \"Throw\"",
                resolveExplosionAudioCandidate: () => new GhostPlaybackLogic.ExplosionOneShotAudioCandidate
                {
                    canPlay = true,
                    clipPath = "sound_explosion_large",
                    clipLengthSeconds = 2f,
                    volume = 0.5f,
                    priority = GhostAudioPresets.BaselineGameAudioPriority
                },
                playExplosionAudio: (pos, candidate) =>
                {
                    throw new InvalidOperationException("audio source unavailable");
                });

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[GhostAudio]") &&
                l.Contains("Explosion one-shot queue failed for KSC ghost #10 \"Throw\"") &&
                l.Contains("audio source unavailable"));
        }

        [Fact]
        public void ResolveAudioPriorityDistance_PrefersRenderDistanceThenFallsBackToActiveDistance()
        {
            var state = new GhostPlaybackState
            {
                lastRenderDistance = 4200.0,
                lastDistance = 800.0
            };

            Assert.Equal(4200.0, GhostPlaybackLogic.ResolveAudioPriorityDistance(state));

            state.lastRenderDistance = double.NaN;
            Assert.Equal(800.0, GhostPlaybackLogic.ResolveAudioPriorityDistance(state));
        }

        [Fact]
        public void SelectHighestPriorityActiveLoopedGhostAudioSources_PrefersActiveRocketsBeforeQuietAndJetSounds()
        {
            var selected = GhostPlaybackLogic.SelectHighestPriorityActiveLoopedGhostAudioSources(
                new List<AudioGhostInfo>
                {
                    new AudioGhostInfo { priorityClass = GhostAudioPriorityClass.JetEngine, currentPower = 0.4f },
                    new AudioGhostInfo { priorityClass = GhostAudioPriorityClass.QuietEngine, currentPower = 0.7f },
                    new AudioGhostInfo { priorityClass = GhostAudioPriorityClass.RocketEngine, currentPower = 1.0f },
                    new AudioGhostInfo { priorityClass = GhostAudioPriorityClass.JetEngine, currentPower = 0f },
                    new AudioGhostInfo { priorityClass = GhostAudioPriorityClass.RocketEngine, currentPower = 0.8f }
                },
                maxSources: 3);

            Assert.Collection(selected,
                info => Assert.Equal(GhostAudioPriorityClass.RocketEngine, info.priorityClass),
                info => Assert.Equal(GhostAudioPriorityClass.RocketEngine, info.priorityClass),
                info => Assert.Equal(GhostAudioPriorityClass.QuietEngine, info.priorityClass));
        }

        [Fact]
        public void SelectHighestPriorityActiveLoopedGhostAudioSources_IgnoresDormantHigherTierEngines()
        {
            var selected = GhostPlaybackLogic.SelectHighestPriorityActiveLoopedGhostAudioSources(
                new List<AudioGhostInfo>
                {
                    new AudioGhostInfo { priorityClass = GhostAudioPriorityClass.RocketEngine, currentPower = 0f },
                    new AudioGhostInfo { priorityClass = GhostAudioPriorityClass.RocketEngine, currentPower = 0f },
                    new AudioGhostInfo { priorityClass = GhostAudioPriorityClass.JetEngine, currentPower = 0.9f },
                    new AudioGhostInfo { priorityClass = GhostAudioPriorityClass.QuietEngine, currentPower = 0.6f }
                },
                maxSources: 2);

            Assert.Collection(selected,
                info => Assert.Equal(GhostAudioPriorityClass.QuietEngine, info.priorityClass),
                info => Assert.Equal(GhostAudioPriorityClass.JetEngine, info.priorityClass));
        }

        [Fact]
        public void SetEngineAudio_MutedStillTracksLatestPower()
        {
            ulong key = FlightRecorder.EncodeEngineKey(42, 1);
            var info = new AudioGhostInfo
            {
                partPersistentId = 42,
                moduleIndex = 1,
                currentPower = 1f
            };
            var state = new GhostPlaybackState
            {
                audioMuted = true,
                audioInfos = new Dictionary<ulong, AudioGhostInfo>
                {
                    { key, info }
                }
            };

            GhostPlaybackLogic.SetEngineAudio(
                state,
                new PartEvent { partPersistentId = 42, moduleIndex = 1 },
                power: 0f);

            Assert.Equal(0f, info.currentPower);
        }

        [Fact]
        public void SetEngineAudio_PausedTracksLatestPowerWithoutEnforcingPlaybackCap()
        {
            int enforceCount = 0;
            GhostPlaybackLogic.EnforceLoopedAudioPlaybackCapOverrideForTesting = _ =>
            {
                enforceCount++;
                return true;
            };

            ulong key = FlightRecorder.EncodeEngineKey(42, 1);
            var info = new AudioGhostInfo
            {
                partPersistentId = 42,
                moduleIndex = 1,
                currentPower = 1f
            };
            var state = new GhostPlaybackState
            {
                audioPaused = true,
                audioInfos = new Dictionary<ulong, AudioGhostInfo>
                {
                    { key, info }
                }
            };

            Assert.True(GhostPlaybackLogic.SetEngineAudio(
                state,
                new PartEvent { partPersistentId = 42, moduleIndex = 1 },
                power: 0f));

            Assert.Equal(0f, info.currentPower);
            Assert.Equal(0, enforceCount);
        }

        [Fact]
        public void CollectDeferredAudioPowerRestores_PausedReturnsEmpty()
        {
            ulong key = FlightRecorder.EncodeEngineKey(42, 1);
            var state = new GhostPlaybackState
            {
                audioPaused = true,
                audioInfos = new Dictionary<ulong, AudioGhostInfo>
                {
                    {
                        key,
                        new AudioGhostInfo
                        {
                            partPersistentId = 42,
                            moduleIndex = 1,
                            currentPower = 1f
                        }
                    }
                }
            };

            Assert.Empty(GhostPlaybackLogic.CollectDeferredAudioPowerRestores(state));
        }

        [Fact]
        public void SetEngineAudio_DeferredPlaybackCapEnforcesOnceAfterBatch()
        {
            int enforceCount = 0;
            GhostPlaybackLogic.EnforceLoopedAudioPlaybackCapOverrideForTesting = _ =>
            {
                enforceCount++;
                return true;
            };
            var infos = new List<AudioGhostInfo>();
            var state = new GhostPlaybackState
            {
                audioInfos = new Dictionary<ulong, AudioGhostInfo>()
            };

            for (int i = 0; i < 8; i++)
            {
                uint pid = (uint)(100 + i);
                var info = new AudioGhostInfo
                {
                    partPersistentId = pid,
                    moduleIndex = 0,
                    selectionOrder = i,
                    priorityClass = GhostAudioPriorityClass.RocketEngine
                };
                infos.Add(info);
                state.audioInfos[FlightRecorder.EncodeEngineKey(pid, 0)] = info;
            }

            for (int i = 0; i < 4; i++)
            {
                Assert.True(GhostPlaybackLogic.SetEngineAudio(
                    state,
                    new PartEvent { partPersistentId = infos[i].partPersistentId, moduleIndex = 0 },
                    1f,
                    enforcePlaybackCap: false));
            }
            for (int i = 0; i < 4; i++)
            {
                Assert.True(GhostPlaybackLogic.SetEngineAudio(
                    state,
                    new PartEvent { partPersistentId = infos[i].partPersistentId, moduleIndex = 0 },
                    0f,
                    enforcePlaybackCap: false));
            }
            for (int i = 4; i < 8; i++)
            {
                Assert.True(GhostPlaybackLogic.SetEngineAudio(
                    state,
                    new PartEvent { partPersistentId = infos[i].partPersistentId, moduleIndex = 0 },
                    1f,
                    enforcePlaybackCap: false));
            }

            Assert.Equal(0, enforceCount);
            GhostPlaybackLogic.EnforceLoopedAudioPlaybackCapWithTestingOverride(state);
            Assert.Equal(1, enforceCount);

            var selected = GhostPlaybackLogic.SelectHighestPriorityActiveLoopedGhostAudioSources(
                new List<AudioGhostInfo>(state.audioInfos.Values),
                GhostAudioPresets.MaxAudioSourcesPerGhost);

            Assert.Equal(4, selected.Count);
            for (int i = 0; i < 4; i++)
                Assert.DoesNotContain(infos[i], selected);
            for (int i = 4; i < 8; i++)
                Assert.Contains(infos[i], selected);
        }

        #endregion

        #region Recording Gate

        [Theory]
        [InlineData(true, true, 12f, true)]
        [InlineData(true, true, 0f, false)]
        [InlineData(true, false, 12f, false)]
        [InlineData(false, true, 12f, false)]
        public void ShouldRecordEngineAsIgnited_RequiresRealThrust(
            bool engineIgnited,
            bool isOperational,
            float finalThrust,
            bool expected)
        {
            Assert.Equal(expected,
                FlightRecorder.ShouldRecordEngineAsIgnited(engineIgnited, isOperational, finalThrust));
        }

        [Fact]
        public void ShouldRecordEngineAsIgnited_RejectsNonFiniteThrust()
        {
            Assert.False(FlightRecorder.ShouldRecordEngineAsIgnited(
                engineIgnited: true,
                isOperational: true,
                finalThrust: float.NaN));
            Assert.False(FlightRecorder.ShouldRecordEngineAsIgnited(
                engineIgnited: true,
                isOperational: true,
                finalThrust: float.PositiveInfinity));
            Assert.False(FlightRecorder.ShouldRecordEngineAsIgnited(
                engineIgnited: true,
                isOperational: true,
                finalThrust: float.NegativeInfinity));
        }

        [Fact]
        public void ComputeRecordedEnginePower_UsesThrottleWhenPresent()
        {
            Assert.Equal(0.7f, FlightRecorder.ComputeRecordedEnginePower(
                currentThrottle: 0.7f,
                finalThrust: 20f,
                maxThrust: 100f));
        }

        [Fact]
        public void ComputeRecordedEnginePower_FallsBackToFinalThrustRatio()
        {
            Assert.Equal(0.25f, FlightRecorder.ComputeRecordedEnginePower(
                currentThrottle: 0f,
                finalThrust: 25f,
                maxThrust: 100f));
        }

        [Fact]
        public void ComputeRecordedEnginePower_IgnoresNonFiniteThrottleAndFallsBackToThrust()
        {
            Assert.Equal(0.25f, FlightRecorder.ComputeRecordedEnginePower(
                currentThrottle: float.NaN,
                finalThrust: 25f,
                maxThrust: 100f));
            Assert.Equal(0.25f, FlightRecorder.ComputeRecordedEnginePower(
                currentThrottle: float.PositiveInfinity,
                finalThrust: 25f,
                maxThrust: 100f));
            Assert.Equal(0.25f, FlightRecorder.ComputeRecordedEnginePower(
                currentThrottle: float.NegativeInfinity,
                finalThrust: 25f,
                maxThrust: 100f));
        }

        [Fact]
        public void ComputeRecordedEnginePower_ReturnsZeroForInvalidThrustFallback()
        {
            Assert.Equal(0f, FlightRecorder.ComputeRecordedEnginePower(
                currentThrottle: 0f,
                finalThrust: float.NaN,
                maxThrust: 100f));
            Assert.Equal(0f, FlightRecorder.ComputeRecordedEnginePower(
                currentThrottle: 0f,
                finalThrust: 25f,
                maxThrust: float.PositiveInfinity));
            Assert.Equal(0f, FlightRecorder.ComputeRecordedEnginePower(
                currentThrottle: 0f,
                finalThrust: 25f,
                maxThrust: 0f));
        }

        #endregion

        #region Thrust Classification

        [Theory]
        [InlineData(500f, "Heavy")]
        [InlineData(301f, "Heavy")]
        [InlineData(300f, "Medium")]
        [InlineData(100f, "Medium")]
        [InlineData(51f, "Medium")]
        [InlineData(50f, "Light")]
        [InlineData(10f, "Light")]
        [InlineData(0f, "Light")]
        public void ClassifyThrustClass_CorrectBuckets(float maxThrust, string expected)
        {
            Assert.Equal(expected, GhostAudioPresets.ClassifyThrustClass(maxThrust));
        }

        #endregion

        #region One-Shot Clip Resolution

        [Theory]
        [InlineData(PartEventType.Destroyed, "sound_explosion_large")]
        public void ResolveOneShotClip_KnownTypes(PartEventType eventType, string expectedClip)
        {
            Assert.Equal(expectedClip, GhostAudioPresets.ResolveOneShotClip(eventType));
        }

        [Theory]
        [InlineData(PartEventType.Decoupled)]
        [InlineData(PartEventType.EngineIgnited)]
        [InlineData(PartEventType.ParachuteDeployed)]
        [InlineData(PartEventType.LightOn)]
        public void ResolveOneShotClip_UnknownTypesReturnNull(PartEventType eventType)
        {
            Assert.Null(GhostAudioPresets.ResolveOneShotClip(eventType));
        }

        #endregion

        // Volume/pitch curve tests skipped — FloatCurve requires Unity runtime.

        #region Engine Clip Resolution (without prefab — fallback)

        [Fact]
        public void ResolveEngineAudioClip_NullPrefab_ReturnsFallback()
        {
            string clip = GhostAudioPresets.ResolveEngineAudioClip(null, 0);
            Assert.Equal("sound_rocket_spurts", clip);
        }

        #endregion

        #region Engine Preset Resolution (bug #423)

        [Fact]
        public void ResolveEngineAudioClip_IonPropellants_UseQuietSubstituteAndQuietVolumeCurve()
        {
            string clip = GhostAudioPresets.ResolveEngineAudioClip(
                "ionEngine",
                0,
                MakePropellants("XenonGas", "ElectricCharge"),
                2f,
                out bool useQuietVolumeCurve);

            Assert.True(useQuietVolumeCurve);
            Assert.Equal("sound_rocket_mini", clip);
        }

        [Fact]
        public void ResolveEngineAudioClip_PureElectricCharge_UsesQuietSubstituteAndQuietVolumeCurve()
        {
            string clip = GhostAudioPresets.ResolveEngineAudioClip(
                "electricOnlyEngine",
                0,
                MakePropellants("ElectricCharge"),
                5f,
                out bool useQuietVolumeCurve);

            Assert.True(useQuietVolumeCurve);
            Assert.Equal("sound_rocket_mini", clip);
        }

        [Fact]
        public void ResolveEngineAudioClip_JetPropellant_UsesQuietVolumeCurve()
        {
            string clip = GhostAudioPresets.ResolveEngineAudioClip(
                "jetEngine",
                0,
                MakePropellants("IntakeAir", "LiquidFuel"),
                130f,
                out bool useQuietVolumeCurve);

            Assert.True(useQuietVolumeCurve);
            Assert.Equal("sound_jet_deep", clip);
        }

        [Theory]
        [InlineData("LiquidFuel", 500f, "sound_rocket_hard")]
        [InlineData("LiquidFuel", 100f, "sound_rocket_hard")]
        [InlineData("LiquidFuel", 10f, "sound_rocket_spurts")]
        [InlineData("SolidFuel", 500f, "sound_rocket_hard")]
        [InlineData("SolidFuel", 100f, "sound_rocket_hard")]
        [InlineData("SolidFuel", 10f, "sound_rocket_spurts")]
        [InlineData("MonoPropellant", 100f, "sound_rocket_mini")]
        [InlineData("MonoPropellant", 10f, "sound_rocket_mini")]
        public void ResolveEngineAudioClip_RocketFamilies_KeepRocketVolumeCurve(
            string propellantName, float maxThrust, string expectedClip)
        {
            string clip = GhostAudioPresets.ResolveEngineAudioClip(
                "rocketEngine",
                0,
                MakePropellants(propellantName),
                maxThrust,
                out bool useQuietVolumeCurve);

            Assert.False(useQuietVolumeCurve);
            Assert.Equal(expectedClip, clip);
        }

        [Fact]
        public void ResolveEngineAudioClip_UnknownPropellant_WarnsAndUsesFallback()
        {
            string clip = GhostAudioPresets.ResolveEngineAudioClip(
                "weirdEngine",
                2,
                MakePropellants("Ore"),
                75f,
                out bool useQuietVolumeCurve);

            Assert.False(useQuietVolumeCurve);
            Assert.Equal("sound_rocket_spurts", clip);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostAudio]") &&
                l.Contains("Unknown preset key 'Unknown_Medium'") &&
                l.Contains("'weirdEngine'"));
        }

        #endregion

        #region Atmosphere Factor

        [Fact]
        public void ComputeAtmosphereFactor_NullBodyName_ReturnsZero()
        {
            var state = new GhostPlaybackState { lastInterpolatedBodyName = null, lastInterpolatedAltitude = 1000 };
            Assert.Equal(0f, GhostPlaybackLogic.ComputeAtmosphereFactor(state));
        }

        [Fact]
        public void ComputeAtmosphereFactor_EmptyBodyName_ReturnsZero()
        {
            var state = new GhostPlaybackState { lastInterpolatedBodyName = "", lastInterpolatedAltitude = 1000 };
            Assert.Equal(0f, GhostPlaybackLogic.ComputeAtmosphereFactor(state));
        }

        // Body-dependent tests (with real CelestialBody) require Unity runtime — in-game tests.

        #endregion

        #region Deferred Audio Start

        [Fact]
        public void CanStartLoopedGhostAudio_MissingSource_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.CanStartLoopedGhostAudio(
                sourceExists: false,
                sourceIsActiveAndEnabled: false));
        }

        [Fact]
        public void CanStartLoopedGhostAudio_InactiveOrDisabledSource_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.CanStartLoopedGhostAudio(
                sourceExists: true,
                sourceIsActiveAndEnabled: false));
        }

        [Fact]
        public void CanStartLoopedGhostAudio_ActiveEnabledSource_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.CanStartLoopedGhostAudio(
                sourceExists: true,
                sourceIsActiveAndEnabled: true));
        }

        #endregion
    }
}
