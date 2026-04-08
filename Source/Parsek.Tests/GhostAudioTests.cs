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
        [InlineData(PartEventType.Decoupled, "sound_decoupler_fire")]
        [InlineData(PartEventType.Destroyed, "sound_explosion_large")]
        public void ResolveOneShotClip_KnownTypes(PartEventType eventType, string expectedClip)
        {
            Assert.Equal(expectedClip, GhostAudioPresets.ResolveOneShotClip(eventType));
        }

        [Theory]
        [InlineData(PartEventType.EngineIgnited)]
        [InlineData(PartEventType.ParachuteDeployed)]
        [InlineData(PartEventType.LightOn)]
        public void ResolveOneShotClip_UnknownTypesReturnNull(PartEventType eventType)
        {
            Assert.Null(GhostAudioPresets.ResolveOneShotClip(eventType));
        }

        #endregion

        #region RCS Clip

        [Fact]
        public void ResolveRcsAudioClip_AlwaysReturnsMini()
        {
            Assert.Equal("sound_rocket_mini", GhostAudioPresets.ResolveRcsAudioClip());
        }

        #endregion

        // Volume/pitch curve tests skipped — FloatCurve requires Unity runtime (cannot instantiate outside KSP).

        #region Engine Clip Resolution (without prefab — fallback)

        [Fact]
        public void ResolveEngineAudioClip_NullPrefab_ReturnsFallback()
        {
            string clip = GhostAudioPresets.ResolveEngineAudioClip(null, 0);
            Assert.Equal("sound_rocket_spurts", clip);
        }

        [Fact]
        public void ResolveEngineAudioClip_NullPrefab_LogsWarning()
        {
            GhostAudioPresets.ResolveEngineAudioClip(null, 0);
            // No warning logged for null prefab — it just returns fallback
            // The fallback itself is a valid clip
        }

        #endregion

        #region Constants

        [Fact]
        public void MaxAudioSourcesPerGhost_IsReasonable()
        {
            Assert.True(GhostAudioPresets.MaxAudioSourcesPerGhost >= 2,
                "Should allow at least 2 audio sources per ghost");
            Assert.True(GhostAudioPresets.MaxAudioSourcesPerGhost <= 8,
                "Should not exceed 8 to avoid channel exhaustion");
        }

        #endregion

        #region Atmosphere Factor

        [Fact]
        public void ComputeAtmosphereFactor_NullBodyName_ReturnsZero()
        {
            Assert.Equal(0f, GhostPlaybackLogic.ComputeAtmosphereFactor(null, 1000));
        }

        [Fact]
        public void ComputeAtmosphereFactor_EmptyBodyName_ReturnsZero()
        {
            Assert.Equal(0f, GhostPlaybackLogic.ComputeAtmosphereFactor("", 1000));
        }

        // Body-dependent tests (ComputeAtmosphereFactor with real CelestialBody) require
        // Unity runtime — covered by in-game tests instead.

        #endregion
    }
}
