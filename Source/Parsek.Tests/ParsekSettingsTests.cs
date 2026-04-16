using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    public class ParsekSettingsTests
    {
        [Fact]
        public void SamplingDensityField_UsesCustomIntParameterUi()
        {
            FieldInfo field = typeof(ParsekSettings).GetField(nameof(ParsekSettings.samplingDensity));

            Assert.NotNull(field);
            Assert.NotNull(field.GetCustomAttribute<GameParameters.CustomIntParameterUI>());
        }

        [Fact]
        public void ResolveSamplingDensityFromConfig_UsesStoredSamplingDensityWhenPresent()
        {
            var node = new ConfigNode("ParsekSettings");
            node.AddValue("samplingDensity", "2");
            node.AddValue("minSampleInterval", "0.5");
            node.AddValue("maxSampleInterval", "8");
            node.AddValue("velocityDirThreshold", "6");
            node.AddValue("speedChangeThreshold", "12");

            SamplingDensity level = ParsekSettings.ResolveSamplingDensityFromConfig(
                node, out bool migratedFromLegacy, out string invalidSamplingDensityValue);

            Assert.Equal(SamplingDensity.High, level);
            Assert.False(migratedFromLegacy);
            Assert.Null(invalidSamplingDensityValue);
        }

        [Fact]
        public void ResolveSamplingDensityFromConfig_MigratesLegacyThresholdsToNearestPreset()
        {
            var node = new ConfigNode("ParsekSettings");
            node.AddValue("minSampleInterval", "0.35");
            node.AddValue("maxSampleInterval", "6.5");
            node.AddValue("velocityDirThreshold", "4.5");
            node.AddValue("speedChangeThreshold", "10");

            SamplingDensity level = ParsekSettings.ResolveSamplingDensityFromConfig(
                node, out bool migratedFromLegacy, out string invalidSamplingDensityValue);

            Assert.Equal(SamplingDensity.Low, level);
            Assert.True(migratedFromLegacy);
            Assert.Null(invalidSamplingDensityValue);
        }

        [Fact]
        public void ResolveSamplingDensityFromConfig_UsesLegacyDefaultsForMissingFields()
        {
            var node = new ConfigNode("ParsekSettings");
            node.AddValue("maxSampleInterval", "3");
            node.AddValue("speedChangeThreshold", "5");

            SamplingDensity level = ParsekSettings.ResolveSamplingDensityFromConfig(
                node, out bool migratedFromLegacy, out string invalidSamplingDensityValue);

            Assert.Equal(SamplingDensity.Medium, level);
            Assert.True(migratedFromLegacy);
            Assert.Null(invalidSamplingDensityValue);
        }

        [Fact]
        public void ResolveSamplingDensityFromConfig_InvalidStoredValueFallsBackToLegacyThresholds()
        {
            var node = new ConfigNode("ParsekSettings");
            node.AddValue("samplingDensity", "99");
            node.AddValue("minSampleInterval", "0.5");
            node.AddValue("maxSampleInterval", "8");
            node.AddValue("velocityDirThreshold", "6");
            node.AddValue("speedChangeThreshold", "12");

            SamplingDensity level = ParsekSettings.ResolveSamplingDensityFromConfig(
                node, out bool migratedFromLegacy, out string invalidSamplingDensityValue);

            Assert.Equal(SamplingDensity.Low, level);
            Assert.True(migratedFromLegacy);
            Assert.Equal("99", invalidSamplingDensityValue);
        }
    }
}
