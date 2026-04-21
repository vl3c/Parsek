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
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
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
        public void SelectHighestPriorityLoopedGhostAudioSources_PrefersRocketsBeforeQuietAndJetSounds()
        {
            var selected = GhostPlaybackLogic.SelectHighestPriorityLoopedGhostAudioSources(
                new List<AudioGhostInfo>
                {
                    new AudioGhostInfo { priorityClass = GhostAudioPriorityClass.JetEngine },
                    new AudioGhostInfo { priorityClass = GhostAudioPriorityClass.QuietEngine },
                    new AudioGhostInfo { priorityClass = GhostAudioPriorityClass.RocketEngine },
                    new AudioGhostInfo { priorityClass = GhostAudioPriorityClass.JetEngine },
                    new AudioGhostInfo { priorityClass = GhostAudioPriorityClass.RocketEngine }
                },
                maxSources: 3);

            Assert.Collection(selected,
                info => Assert.Equal(GhostAudioPriorityClass.RocketEngine, info.priorityClass),
                info => Assert.Equal(GhostAudioPriorityClass.RocketEngine, info.priorityClass),
                info => Assert.Equal(GhostAudioPriorityClass.QuietEngine, info.priorityClass));
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
