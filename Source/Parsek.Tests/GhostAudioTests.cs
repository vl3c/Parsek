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

        #region Preset Map Coverage (bug #423)

        // Bug #423: stock cfg references `sound_IonEngine` for the ion engine,
        // but that asset is not surfaced via `GameDatabase.GetAudioClip` in
        // KSP 1.12. The preset map must NOT emit `sound_IonEngine` for the ion
        // propellant keys, otherwise every ion-engine ghost spams the
        // "AudioClip not found" WARN that #421 dedupe'd. Substitute with a clip
        // that does ship in stock GameData.
        [Theory]
        [InlineData("XenonGas")]
        [InlineData("ElectricCharge")]
        public void LookupClip_IonPropellants_DoNotMapToMissingStockClip(string propellantKey)
        {
            string clip = GhostAudioPresets.LookupClip(propellantKey);
            Assert.NotNull(clip);
            Assert.NotEqual("sound_IonEngine", clip);
        }

        [Theory]
        [InlineData("XenonGas", "sound_rocket_mini")]
        [InlineData("ElectricCharge", "sound_rocket_mini")]
        public void LookupClip_IonPropellants_MapToQuietRocketSubstitute(
            string propellantKey, string expectedClip)
        {
            Assert.Equal(expectedClip, GhostAudioPresets.LookupClip(propellantKey));
        }

        // Regression guard for the rest of the preset map: the ion fix must
        // not perturb the other propellant entries the playtest log confirms
        // resolve cleanly (`sound_rocket_hard`, `sound_rocket_spurts`,
        // `sound_rocket_mini`, `sound_jet_deep`).
        [Theory]
        [InlineData("LiquidFuel_Heavy",      "sound_rocket_hard")]
        [InlineData("LiquidFuel_Medium",     "sound_rocket_hard")]
        [InlineData("LiquidFuel_Light",      "sound_rocket_spurts")]
        [InlineData("SolidFuel_Heavy",       "sound_rocket_hard")]
        [InlineData("SolidFuel_Medium",      "sound_rocket_hard")]
        [InlineData("SolidFuel_Light",       "sound_rocket_spurts")]
        [InlineData("MonoPropellant_Heavy",  "sound_rocket_spurts")]
        [InlineData("MonoPropellant_Medium", "sound_rocket_mini")]
        [InlineData("MonoPropellant_Light",  "sound_rocket_mini")]
        [InlineData("IntakeAir",             "sound_jet_deep")]
        [InlineData("Fallback",              "sound_rocket_spurts")]
        public void LookupClip_NonIonEntries_Unchanged(string key, string expectedClip)
        {
            Assert.Equal(expectedClip, GhostAudioPresets.LookupClip(key));
        }

        [Fact]
        public void LookupClip_UnknownKey_ReturnsNull()
        {
            Assert.Null(GhostAudioPresets.LookupClip("NoSuchPreset"));
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
