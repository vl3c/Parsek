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
        public void ShouldStartOneShotSound_DestroyedBlocksWhileExplosionSoundBusy()
        {
            Assert.False(GhostPlaybackLogic.ShouldStartOneShotSound(
                PartEventType.Destroyed,
                sourceIsPlaying: false,
                nowRealtimeSeconds: 12.0,
                explosionBusyUntilRealtimeSeconds: 13.5));
        }

        [Fact]
        public void ShouldStartOneShotSound_DestroyedAllowsAfterExplosionSoundFinishes()
        {
            Assert.True(GhostPlaybackLogic.ShouldStartOneShotSound(
                PartEventType.Destroyed,
                sourceIsPlaying: false,
                nowRealtimeSeconds: 13.5,
                explosionBusyUntilRealtimeSeconds: 13.5));
        }

        [Fact]
        public void ShouldStartOneShotSound_DestroyedBlocksWhenSourceStillPlaying()
        {
            Assert.False(GhostPlaybackLogic.ShouldStartOneShotSound(
                PartEventType.Destroyed,
                sourceIsPlaying: true,
                nowRealtimeSeconds: 14.0,
                explosionBusyUntilRealtimeSeconds: 13.5));
        }

        [Fact]
        public void ShouldStartOneShotSound_NonExplosionIgnoresExplosionGate()
        {
            Assert.True(GhostPlaybackLogic.ShouldStartOneShotSound(
                PartEventType.Decoupled,
                sourceIsPlaying: true,
                nowRealtimeSeconds: 14.0,
                explosionBusyUntilRealtimeSeconds: 20.0));
        }

        [Fact]
        public void TryReserveOneShotSound_DestroyedBlocksSecondStartUntilClipDurationEnds()
        {
            double now = 20.0;

            Assert.True(GhostPlaybackLogic.TryReserveOneShotSound(
                PartEventType.Destroyed,
                sourceIsPlaying: false,
                clipLengthSeconds: 1.25f,
                nowRealtimeSeconds: now,
                out double firstBusyUntil));
            Assert.Equal(21.25, firstBusyUntil, 3);

            now = 20.5;
            Assert.False(GhostPlaybackLogic.TryReserveOneShotSound(
                PartEventType.Destroyed,
                sourceIsPlaying: false,
                clipLengthSeconds: 1.25f,
                nowRealtimeSeconds: now,
                out double blockedBusyUntil));
            Assert.Equal(firstBusyUntil, blockedBusyUntil, 3);

            now = 21.25;
            Assert.True(GhostPlaybackLogic.TryReserveOneShotSound(
                PartEventType.Destroyed,
                sourceIsPlaying: false,
                clipLengthSeconds: 1.25f,
                nowRealtimeSeconds: now,
                out double secondBusyUntil));
            Assert.Equal(22.5, secondBusyUntil, 3);
        }

        [Fact]
        public void TryReserveOneShotSound_InvalidClipLengthUsesFallbackDuration()
        {
            double now = 25.0;

            Assert.True(GhostPlaybackLogic.TryReserveOneShotSound(
                PartEventType.Destroyed,
                sourceIsPlaying: false,
                clipLengthSeconds: float.NaN,
                nowRealtimeSeconds: now,
                out double busyUntil));

            Assert.Equal(
                now + GhostAudioPresets.ExplosionOneShotFallbackDurationSeconds,
                busyUntil,
                3);
        }

        [Fact]
        public void ReleaseExplosionSoundReservation_AllowsRetryAfterFailedStockFx()
        {
            double now = 30.0;

            Assert.True(GhostPlaybackLogic.TryReserveExplosionSound(
                clipLengthSeconds: 2f,
                nowRealtimeSeconds: now,
                out double reservedUntil));

            GhostPlaybackLogic.ReleaseExplosionSoundReservation(reservedUntil);

            now = 30.1;
            Assert.True(GhostPlaybackLogic.TryReserveExplosionSound(
                clipLengthSeconds: 2f,
                nowRealtimeSeconds: now,
                out _));
        }

        [Fact]
        public void TryTriggerStockExplosionFxWithAudioGate_BusyGateSkipsStockAndSpawnsCustomVisual()
        {
            int resolveCalls = 0;
            int reserveCalls = 0;
            int stockCalls = 0;
            int spawnCalls = 0;
            int releaseCalls = 0;
            GhostPlaybackLogic.StockExplosionFxWithAudioGateResult? gateResult = null;

            bool result = GhostPlaybackLogic.TryTriggerStockExplosionFxWithAudioGate(
                new UnityEngine.Vector3(1f, 2f, 3f),
                power: 0.75,
                vesselLength: 12f,
                contextDescription: "manual preview \"Test\"",
                busyLogKey: "test-busy-preview",
                resolveExplosionSoundDuration: () =>
                {
                    resolveCalls++;
                    return 1.5f;
                },
                reserveExplosionSound: (float clipLengthSeconds, out double busyUntil) =>
                {
                    reserveCalls++;
                    Assert.Equal(1.5f, clipLengthSeconds);
                    busyUntil = 42.0;
                    return false;
                },
                releaseExplosionSoundReservation: _ => releaseCalls++,
                triggerStockExplosionFx: (UnityEngine.Vector3 stockPos, double stockPower, out string failure) =>
                {
                    stockCalls++;
                    failure = null;
                    return true;
                },
                spawnExplosionFx: (UnityEngine.Vector3 pos, float length) =>
                {
                    spawnCalls++;
                    Assert.Equal(12f, length);
                    Assert.Equal(1f, pos.x);
                    Assert.Equal(2f, pos.y);
                    Assert.Equal(3f, pos.z);
                },
                recordResult: value => gateResult = value);

            Assert.False(result);
            Assert.Equal(
                GhostPlaybackLogic.StockExplosionFxWithAudioGateResult.AudioBusyCustomVisualSpawned,
                gateResult);
            Assert.Equal(1, resolveCalls);
            Assert.Equal(1, reserveCalls);
            Assert.Equal(0, stockCalls);
            Assert.Equal(1, spawnCalls);
            Assert.Equal(0, releaseCalls);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostAudio]") &&
                l.Contains("Stock explosion FX skipped for manual preview \"Test\""));
        }

        [Fact]
        public void TryTriggerStockExplosionFxWithAudioGate_StockFailureReleasesReservationAndSpawnsCustomVisual()
        {
            int stockCalls = 0;
            int spawnCalls = 0;
            double releasedUntil = double.NaN;
            GhostPlaybackLogic.StockExplosionFxWithAudioGateResult? gateResult = null;

            bool result = GhostPlaybackLogic.TryTriggerStockExplosionFxWithAudioGate(
                new UnityEngine.Vector3(4f, 5f, 6f),
                power: 0.4,
                vesselLength: 9f,
                contextDescription: "ghost #3 \"Crashy\"",
                busyLogKey: "test-stock-fail",
                resolveExplosionSoundDuration: () => 2f,
                reserveExplosionSound: (float clipLengthSeconds, out double busyUntil) =>
                {
                    busyUntil = 99.0;
                    return true;
                },
                releaseExplosionSoundReservation: busyUntil => releasedUntil = busyUntil,
                triggerStockExplosionFx: (UnityEngine.Vector3 stockPos, double stockPower, out string failure) =>
                {
                    stockCalls++;
                    failure = "no live FXMonger instance";
                    return false;
                },
                spawnExplosionFx: (UnityEngine.Vector3 spawnPos, float length) =>
                {
                    spawnCalls++;
                    Assert.Equal(9f, length);
                },
                recordResult: value => gateResult = value);

            Assert.False(result);
            Assert.Equal(
                GhostPlaybackLogic.StockExplosionFxWithAudioGateResult.StockFailedCustomVisualSpawned,
                gateResult);
            Assert.Equal(1, stockCalls);
            Assert.Equal(1, spawnCalls);
            Assert.Equal(99.0, releasedUntil, 3);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[ExplosionFx]") &&
                l.Contains("FXMonger.Explode did not queue stock FX for ghost #3 \"Crashy\"") &&
                l.Contains("no live FXMonger instance"));
        }

        [Fact]
        public void TryTriggerStockExplosionFxWithAudioGate_StockSuccessKeepsReservationAndSkipsCustomVisual()
        {
            int spawnCalls = 0;
            int releaseCalls = 0;
            GhostPlaybackLogic.StockExplosionFxWithAudioGateResult? gateResult = null;

            bool result = GhostPlaybackLogic.TryTriggerStockExplosionFxWithAudioGate(
                new UnityEngine.Vector3(7f, 8f, 9f),
                power: 0.6,
                vesselLength: 15f,
                contextDescription: "ghost #4 \"Boom\"",
                busyLogKey: "test-stock-success",
                resolveExplosionSoundDuration: () => 2f,
                reserveExplosionSound: (float clipLengthSeconds, out double busyUntil) =>
                {
                    busyUntil = 101.0;
                    return true;
                },
                releaseExplosionSoundReservation: _ => releaseCalls++,
                triggerStockExplosionFx: (UnityEngine.Vector3 stockPos, double stockPower, out string failure) =>
                {
                    failure = null;
                    return true;
                },
                spawnExplosionFx: (UnityEngine.Vector3 spawnPos, float length) => spawnCalls++,
                recordResult: value => gateResult = value);

            Assert.True(result);
            Assert.Equal(
                GhostPlaybackLogic.StockExplosionFxWithAudioGateResult.StockQueued,
                gateResult);
            Assert.Equal(0, spawnCalls);
            Assert.Equal(0, releaseCalls);
        }

        [Fact]
        public void TryPlayExplosionOneShotWithAudioGate_UnavailableClipDoesNotReserveGate()
        {
            int reserveCalls = 0;
            int playCalls = 0;

            bool result = GhostPlaybackLogic.TryPlayExplosionOneShotWithAudioGate(
                new UnityEngine.Vector3(1f, 1f, 1f),
                atmosphereFactor: 1f,
                distanceMeters: 0.0,
                contextDescription: "KSC ghost #7 \"NoClip\"",
                busyLogKey: "test-explicit-audio-busy",
                resolveExplosionAudioCandidate: () => new GhostPlaybackLogic.ExplosionOneShotAudioCandidate
                {
                    canPlay = false,
                    clipPath = "sound_explosion_large",
                    failureReason = "AudioClip not found"
                },
                reserveExplosionSound: (float clipLengthSeconds, out double busyUntil) =>
                {
                    reserveCalls++;
                    busyUntil = 0.0;
                    return true;
                },
                playExplosionAudio: (pos, candidate) => playCalls++);

            Assert.False(result);
            Assert.Equal(0, reserveCalls);
            Assert.Equal(0, playCalls);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[GhostAudio]") &&
                l.Contains("Explosion one-shot unavailable for KSC ghost #7 \"NoClip\"") &&
                l.Contains("AudioClip not found"));
        }

        [Fact]
        public void TryPlayExplosionOneShotWithAudioGate_BusyGateDoesNotQueueAudio()
        {
            int reserveCalls = 0;
            int playCalls = 0;

            bool result = GhostPlaybackLogic.TryPlayExplosionOneShotWithAudioGate(
                new UnityEngine.Vector3(2f, 3f, 4f),
                atmosphereFactor: 1f,
                distanceMeters: 0.0,
                contextDescription: "KSC ghost #8 \"Busy\"",
                busyLogKey: "test-explicit-audio-busy",
                resolveExplosionAudioCandidate: () => new GhostPlaybackLogic.ExplosionOneShotAudioCandidate
                {
                    canPlay = true,
                    clipPath = "sound_explosion_large",
                    clipLengthSeconds = 1.75f,
                    volume = 0.5f,
                    priority = GhostAudioPresets.BaselineGameAudioPriority
                },
                reserveExplosionSound: (float clipLengthSeconds, out double busyUntil) =>
                {
                    reserveCalls++;
                    Assert.Equal(1.75f, clipLengthSeconds);
                    busyUntil = 20.0;
                    return false;
                },
                playExplosionAudio: (pos, candidate) => playCalls++);

            Assert.False(result);
            Assert.Equal(1, reserveCalls);
            Assert.Equal(0, playCalls);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostAudio]") &&
                l.Contains("Explosion one-shot skipped for KSC ghost #8 \"Busy\""));
        }

        [Fact]
        public void TryPlayExplosionOneShotWithAudioGate_QueuesAudioAfterSuccessfulReservation()
        {
            int reserveCalls = 0;
            int playCalls = 0;
            UnityEngine.Vector3 playedPosition = default;

            bool result = GhostPlaybackLogic.TryPlayExplosionOneShotWithAudioGate(
                new UnityEngine.Vector3(5f, 6f, 7f),
                atmosphereFactor: 1f,
                distanceMeters: 125.0,
                contextDescription: "KSC ghost #9 \"Fallback\"",
                busyLogKey: "test-explicit-audio-busy",
                resolveExplosionAudioCandidate: () => new GhostPlaybackLogic.ExplosionOneShotAudioCandidate
                {
                    canPlay = true,
                    clipPath = "sound_explosion_large",
                    clipLengthSeconds = 2.25f,
                    volume = 0.4f,
                    priority = GhostAudioPresets.BaselineGameAudioPriority
                },
                reserveExplosionSound: (float clipLengthSeconds, out double busyUntil) =>
                {
                    reserveCalls++;
                    Assert.Equal(2.25f, clipLengthSeconds);
                    busyUntil = 30.0;
                    return true;
                },
                playExplosionAudio: (pos, candidate) =>
                {
                    playCalls++;
                    playedPosition = pos;
                    Assert.Equal("sound_explosion_large", candidate.clipPath);
                    Assert.Equal(0.4f, candidate.volume);
                });

            Assert.True(result);
            Assert.Equal(1, reserveCalls);
            Assert.Equal(1, playCalls);
            Assert.Equal(5f, playedPosition.x);
            Assert.Equal(6f, playedPosition.y);
            Assert.Equal(7f, playedPosition.z);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostAudio]") &&
                l.Contains("Explosion one-shot queued for KSC ghost #9 \"Fallback\""));
        }

        [Fact]
        public void TryPlayExplosionOneShotWithAudioGate_PlayFailureReleasesReservation()
        {
            double releasedUntil = double.NaN;

            bool result = GhostPlaybackLogic.TryPlayExplosionOneShotWithAudioGate(
                new UnityEngine.Vector3(8f, 9f, 10f),
                atmosphereFactor: 1f,
                distanceMeters: 0.0,
                contextDescription: "KSC ghost #10 \"Throw\"",
                busyLogKey: "test-explicit-audio-busy",
                resolveExplosionAudioCandidate: () => new GhostPlaybackLogic.ExplosionOneShotAudioCandidate
                {
                    canPlay = true,
                    clipPath = "sound_explosion_large",
                    clipLengthSeconds = 2f,
                    volume = 0.5f,
                    priority = GhostAudioPresets.BaselineGameAudioPriority
                },
                reserveExplosionSound: (float clipLengthSeconds, out double busyUntil) =>
                {
                    busyUntil = 44.0;
                    return true;
                },
                releaseExplosionSoundReservation: busyUntil => releasedUntil = busyUntil,
                playExplosionAudio: (pos, candidate) =>
                {
                    throw new InvalidOperationException("audio source unavailable");
                });

            Assert.False(result);
            Assert.Equal(44.0, releasedUntil, 3);
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
